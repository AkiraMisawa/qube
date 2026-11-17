using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Qube.Samples.Rates.Alpha;

/// <summary>A started worker: what to write requests to, what to read answers from, and how to stop it.</summary>
public sealed record AlphaWorkerConnection(TextWriter Input, TextReader Output, Action Stop);

/// <summary>
/// The resident worker as a channel (alpha docs/spec/rates-pricer.md §7): one JSON object per line each way, matched
/// by an id this side gives, so answers may come in any order and many requests wait at once. A worker whose output
/// closes has exited: what was waiting fails, and the next request starts a new one. A request that waits longer than
/// the timeout fails; its answer, if it comes later, is dropped (a timeout does not prove the computation stopped).
/// </summary>
public sealed class AlphaProcessChannel(Func<AlphaWorkerConnection> start, TimeSpan timeout) : IAlphaChannel, IAsyncDisposable
{
    readonly SemaphoreSlim writing = new(1, 1);
    readonly Lock gate = new();
    readonly ConcurrentDictionary<long, TaskCompletionSource<JsonObject>> waiting = new();
    AlphaWorkerConnection? worker;
    long next;

    /// <summary>Starts the worker as a process (the command pinned in configuration); stderr goes to the host's log.</summary>
    public static AlphaWorkerConnection StartProcess(string command, IReadOnlyList<string> arguments, Action<string> log)
    {
        var info = new ProcessStartInfo(command) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in arguments) info.ArgumentList.Add(a);
        var process = Process.Start(info) ?? throw new IOException($"The worker '{command}' did not start.");
        process.ErrorDataReceived += (_, e) => { if (e.Data is { } line) log(line); };
        process.BeginErrorReadLine();
        return new AlphaWorkerConnection(process.StandardInput, process.StandardOutput, () => { try { process.Kill(); } catch (InvalidOperationException) { } process.Dispose(); });
    }

    AlphaWorkerConnection Connected()
    {
        lock (gate)
        {
            if (worker is not null) return worker;
            var started = start();
            worker = started;
            _ = Task.Run(() => Read(started));
            return started;
        }
    }

    async Task Read(AlphaWorkerConnection from)
    {
        try
        {
            while (await from.Output.ReadLineAsync() is { } line)
            {
                if (JsonNode.Parse(line) is not JsonObject answer || answer["id"] is not JsonValue id) continue;
                if (waiting.TryRemove(id.GetValue<long>(), out var pending)) pending.TrySetResult(answer);
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        lock (gate) if (ReferenceEquals(worker, from)) worker = null;
        from.Stop();
        foreach (var id in waiting.Keys)
            if (waiting.TryRemove(id, out var pending)) pending.TrySetException(new IOException("The rates worker exited."));
    }

    public async Task<JsonObject> SendAsync(JsonObject request, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref next);
        var answer = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        waiting[id] = answer;
        var message = (JsonObject)request.DeepClone();
        message["id"] = id;
        var to = Connected();
        await writing.WaitAsync(cancellationToken);
        try { await to.Input.WriteLineAsync(message.ToJsonString()); await to.Input.FlushAsync(cancellationToken); }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            waiting.TryRemove(id, out _);
            throw new IOException("The rates worker cannot be written to.", e);
        }
        finally { writing.Release(); }
        try { return await answer.Task.WaitAsync(timeout, cancellationToken); }
        catch (TimeoutException)
        {
            waiting.TryRemove(id, out _);
            throw new TimeoutException($"The rates worker did not answer within {timeout.TotalSeconds:0.###} s.");
        }
    }

    public ValueTask DisposeAsync()
    {
        AlphaWorkerConnection? w;
        lock (gate) { w = worker; worker = null; }
        w?.Stop();
        return ValueTask.CompletedTask;
    }
}
