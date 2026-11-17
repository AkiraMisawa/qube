using System.Text.Json.Nodes;
using Qube.Samples.Rates.Alpha;

namespace Qube.Composition.Tests;

// The resident worker's channel (alpha docs/spec/rates-pricer.md §7): JSON lines with ids, answers in any order,
// a worker that exits fails what was waiting and is started again for the next request.
public class AlphaChannelTests
{
    sealed class FakeWorker
    {
        public int Starts;
        public readonly List<(StreamWriter ToClient, StreamReader FromClient)> Ends = [];

        public AlphaWorkerConnection Start()
        {
            Starts++;
            var toWorker = new System.IO.Pipes.AnonymousPipeServerStream(System.IO.Pipes.PipeDirection.Out);
            var workerIn = new System.IO.Pipes.AnonymousPipeClientStream(System.IO.Pipes.PipeDirection.In, toWorker.ClientSafePipeHandle);
            var toClient = new System.IO.Pipes.AnonymousPipeServerStream(System.IO.Pipes.PipeDirection.Out);
            var clientIn = new System.IO.Pipes.AnonymousPipeClientStream(System.IO.Pipes.PipeDirection.In, toClient.ClientSafePipeHandle);
            var end = (new StreamWriter(toClient) { AutoFlush = true }, new StreamReader(workerIn));
            lock (Ends) Ends.Add(end);
            return new AlphaWorkerConnection(new StreamWriter(toWorker) { AutoFlush = true }, new StreamReader(clientIn),
                () => { toWorker.Dispose(); toClient.Dispose(); });
        }

        public async Task<JsonObject> Receive(int worker)
        {
            var line = await Ends[worker].FromClient.ReadLineAsync();
            return JsonNode.Parse(line!)!.AsObject();
        }

        public Task Answer(int worker, JsonObject answer) => Ends[worker].ToClient.WriteLineAsync(answer.ToJsonString());
    }

    [Fact]
    public async Task Answers_are_matched_by_id_whatever_their_order()
    {
        var worker = new FakeWorker();
        await using var channel = new AlphaProcessChannel(worker.Start, TimeSpan.FromSeconds(10));

        var a = channel.SendAsync(new JsonObject { ["op"] = "a" }, CancellationToken.None);
        var b = channel.SendAsync(new JsonObject { ["op"] = "b" }, CancellationToken.None);
        var (ra, rb) = (await worker.Receive(0), await worker.Receive(0));
        await worker.Answer(0, new JsonObject { ["id"] = rb["id"]!.DeepClone(), ["ok"] = true, ["was"] = "b" });
        await worker.Answer(0, new JsonObject { ["id"] = ra["id"]!.DeepClone(), ["ok"] = true, ["was"] = "a" });

        Assert.Equal(("a", "b"), ((string)(await a)["was"]!, (string)(await b)["was"]!));
        Assert.Equal(1, worker.Starts);
    }

    [Fact] // CLAUDE.md: an exited worker is a failure on what was waiting, and the next request starts a new one
    public async Task A_worker_that_exits_fails_the_waiting_request_and_is_started_again()
    {
        var worker = new FakeWorker();
        await using var channel = new AlphaProcessChannel(worker.Start, TimeSpan.FromSeconds(10));

        var waiting = channel.SendAsync(new JsonObject { ["op"] = "a" }, CancellationToken.None);
        await worker.Receive(0);
        worker.Ends[0].ToClient.Dispose();   // the worker's output closes: it exited

        await Assert.ThrowsAsync<IOException>(() => waiting);

        var next = channel.SendAsync(new JsonObject { ["op"] = "b" }, CancellationToken.None);
        var r = await worker.Receive(1);
        await worker.Answer(1, new JsonObject { ["id"] = r["id"]!.DeepClone(), ["ok"] = true });
        Assert.True((bool)(await next)["ok"]!);
        Assert.Equal(2, worker.Starts);
    }

    [Fact] // docs/07: a timeout does not prove the computation stopped; the late answer is dropped, not given to another request
    public async Task A_request_that_times_out_fails_and_its_late_answer_is_dropped()
    {
        var worker = new FakeWorker();
        await using var channel = new AlphaProcessChannel(worker.Start, TimeSpan.FromMilliseconds(100));

        var slow = channel.SendAsync(new JsonObject { ["op"] = "slow" }, CancellationToken.None);
        var r = await worker.Receive(0);
        await Assert.ThrowsAsync<TimeoutException>(() => slow);
        await worker.Answer(0, new JsonObject { ["id"] = r["id"]!.DeepClone(), ["ok"] = true });

        var next = channel.SendAsync(new JsonObject { ["op"] = "next" }, CancellationToken.None);
        var n = await worker.Receive(0);
        await worker.Answer(0, new JsonObject { ["id"] = n["id"]!.DeepClone(), ["ok"] = true, ["was"] = "next" });
        Assert.Equal("next", (string)(await next)["was"]!);
    }
}
