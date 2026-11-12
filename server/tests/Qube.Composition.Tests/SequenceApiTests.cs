using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Qube.Samples.Rates;

namespace Qube.Composition.Tests;

// ADR-0016 §8 and its notes 60 to 64: saved sequences of edits as the host serves them, on the pricer and on Rates,
// through the preview and the two doors. Every name and figure is synthetic.
public class SequenceApiTests : IClassFixture<QubeFactory>
{
    readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> host;
    readonly HttpClient client;

    public SequenceApiTests(QubeFactory factory)
    {
        host = factory.WithWebHostBuilder(b => b.UseSetting("Rates:Ticker", "false"));
        client = host.CreateClient();
    }

    static Task<HttpResponseMessage> Command(HttpClient http, string capability, JsonObject request, string? key = null) =>
        http.PostAsJsonAsync($"/api/commands/{capability}/1.0.0", new { inputs = new JsonObject { ["request"] = request }, idempotencyKey = key ?? Guid.NewGuid().ToString() });

    static async Task<JsonObject> Ok(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!;
    }

    static async Task<JsonNode> Answer(HttpResponseMessage response) => (await Ok(response))["outputs"]!["answer"]!;

    static async Task<string> Pricer(HttpClient http, string registration = "ABC/LE-01") =>
        (await Ok(await http.PostAsJsonAsync("/api/xva/workspaces", new { registration, valueDate = "2026-10-26", portfolioSize = 10 })))["workspaceId"]!.GetValue<string>();

    static async Task<string> Rates(HttpClient http) =>
        (await Ok(await http.PostAsJsonAsync("/api/rates/workspaces", new { valuationDate = "2026-10-27" })))["workspaceId"]!.GetValue<string>();

    static async Task<JsonObject> Preview(HttpClient http, string workspaceId, string line) =>
        await Ok(await http.PostAsJsonAsync("/api/commands/preview", new { line, workspaceId }));

    static Task<HttpResponseMessage> Save(HttpClient http, string workspaceId, string name, params string[] lines) =>
        Command(http, "sequence.save", new JsonObject { ["workspaceId"] = workspaceId, ["name"] = name, ["lines"] = new JsonArray([.. lines.Select(l => (JsonNode)l)]) });

    [Fact] // ADR-0016 §8, notes 62 and 64: on Rates a sequence makes a line and prices it, and the last line's answer names the Run its page opens
    public async Task A_rates_sequence_makes_a_line_and_ends_by_pricing_it()
    {
        var id = await Rates(client);
        await Answer(await Save(client, id, "ten-year", "new irs jpy 10y 1bn pay", "price"));
        // A market to price on: the test's host has no ticker.
        await host.Services.GetRequiredService<MarketFeed>().AdvanceAsync();

        var read = await Preview(client, id, "sequence run ten-year");
        Assert.Null(read["refused"]);
        var ran = await Answer(await client.PostAsJsonAsync("/api/commands/sequence.run/1.0.0", new { inputs = read["inputs"], idempotencyKey = Guid.NewGuid().ToString() }));

        Assert.Null(ran["stoppedWhy"]?.GetValue<string>());
        var steps = ran["steps"]!.AsArray();
        Assert.Equal(["new irs jpy 10y 1bn pay [entity.create]", "price [rates.workspace.price]"], steps.Select(s => $"{s!["line"]} [{s["command"]}]"));
        // What `price` answers when typed alone is the step's answer, whole: the Run and the market it was priced on.
        var priced = JsonNode.Parse(steps[1]!["answer"]!.GetValue<string>())!;
        Assert.NotNull(priced["marketSnapshotId"]);
        var run = (await client.GetFromJsonAsync<JsonObject>($"/api/runs/{priced["runId"]!.GetValue<string>()}"))!;
        Assert.Equal("workflow", run["origin"]!.GetValue<string>());
        // The Run is of the revision the line before left.
        Assert.Equal(steps[0]!["revision"]!.GetValue<long>(), run["relations"]!["workspaceRevision"]!.GetValue<long>());
    }

    [Fact] // ADR-0016 note 60: a sequence is of one application, and the same name may be another's
    public async Task A_pricers_sequence_is_not_seen_on_a_rates_workspace()
    {
        var (pricer, rates) = (await Pricer(client), await Rates(client));
        await Answer(await Save(client, pricer, "tidy", "after none existing"));

        var missing = await Command(client, "sequence.run", new JsonObject { ["workspaceId"] = rates, ["sequence"] = "tidy" });
        Assert.False(missing.IsSuccessStatusCode);
        Assert.Equal("UnknownSaved", (await missing.Content.ReadFromJsonAsync<JsonObject>())!["code"]!.GetValue<string>());
        Assert.DoesNotContain((await Preview(client, rates, "sequence run "))["complete"]!.AsArray(), c => c!["value"]!.GetValue<string>() == "tidy");
        Assert.Contains((await Preview(client, pricer, "sequence run "))["complete"]!.AsArray(), c => c!["value"]!.GetValue<string>() == "tidy");

        // The pricer's words are none of Rates', so the line is no line of a Rates sequence; Rates' own tidy replaces nothing.
        var foreign = await Save(client, rates, "tidy", "after none existing");
        Assert.Equal("NotACommand", (await foreign.Content.ReadFromJsonAsync<JsonObject>())!["code"]!.GetValue<string>());
        var own = await Answer(await Save(client, rates, "tidy", "new irs jpy 10y 1bn pay"));
        Assert.False(own["replaced"]!.GetValue<bool>());
    }

    [Fact] // ADR-0016 note 60, C32: a sequence is kept in the database, and a host started again on it runs it
    public async Task A_sequence_is_kept_across_a_restart()
    {
        var database = Path.Combine(Path.GetTempPath(), $"qube-sequences-{Guid.NewGuid():N}.db");
        try
        {
            string id;
            using (var first = new QubeFactory(database, ownsDatabase: false))
            {
                var http = first.CreateClient();
                id = await Pricer(http);
                await Answer(await Save(http, id, "tidy", "after none existing", "set existing where currency = USD after on"));
            }
            using var second = new QubeFactory(database, ownsDatabase: false);
            var again = second.CreateClient();

            var read = await Preview(again, id, "sequence run tidy");
            Assert.StartsWith("Runs tidy, of 2 lines, each as its own command, until one is refused: 1. `after none existing` 2. `set existing where currency = USD after on`. ", read["would"]!.GetValue<string>());
            var ran = await Answer(await again.PostAsJsonAsync("/api/commands/sequence.run/1.0.0", new { inputs = read["inputs"], idempotencyKey = Guid.NewGuid().ToString() }));
            Assert.Equal(2, ran["ran"]!.GetValue<int>());
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            QubeFactory.RemoveDatabase(database);
        }
    }

    [Fact] // ADR-0016 notes 62 and 63, ADR-0005: the AI has both as tools, and what it runs is the AI's in the log
    public async Task The_AI_saves_and_runs_a_sequence_through_its_door()
    {
        var id = await Pricer(client);
        Task<HttpResponseMessage> Ai(string capability, JsonObject request) =>
            client.PostAsJsonAsync($"/api/agent/commands/{capability}/1.0.0", new { inputs = new JsonObject { ["request"] = request }, idempotencyKey = Guid.NewGuid().ToString() });

        await Answer(await Ai("sequence.save", new JsonObject { ["workspaceId"] = id, ["name"] = "tidy", ["lines"] = new JsonArray("after none existing") }));
        var ran = await Answer(await Ai("sequence.run", new JsonObject { ["workspaceId"] = id, ["sequence"] = "tidy" }));

        Assert.Equal(1, ran["ran"]!.GetValue<int>());
        var undo = await Preview(client, id, "undo");
        Assert.Contains("(the AI for you, ", undo["would"]!.GetValue<string>());
    }
}
