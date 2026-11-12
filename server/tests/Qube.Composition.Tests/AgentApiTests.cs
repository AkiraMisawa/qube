using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Qube.Agent;
using Qube.Composition.Application;
using Qube.Composition.Application.Workspaces;
using Qube.Samples.Rates;
using Qube.Web;

namespace Qube.Composition.Tests;

// ADR-0007: the AI pane's AG-UI runs. The server holds the conversation, the agent's tools are the registered commands
// invoked for the server's caller, a run is read again rather than run again, and the scripted agent asks for what a
// command refused instead of filling it in.
public class AgentApiTests : IClassFixture<QubeFactory>
{
    readonly WebApplicationFactory<Program> host;
    readonly HttpClient client;

    public AgentApiTests(QubeFactory factory)
    {
        host = factory.WithWebHostBuilder(b => b.UseSetting("Rates:Ticker", "false"));
        client = host.CreateClient();
    }

    const string FullSwap = "make a 10y JPY TONA swap, pay fixed, 1bn, fixed 12m float 12m, spread 0, valuation date 2026-10-27";

    static JsonObject Input(string threadId, string runId, params (string Id, string Text)[] messages) => new()
    {
        ["threadId"] = threadId,
        ["runId"] = runId,
        ["state"] = new JsonObject(),
        ["messages"] = new JsonArray([.. messages.Select(m => (JsonNode)new JsonObject { ["id"] = m.Id, ["role"] = "user", ["content"] = m.Text })]),
        ["tools"] = new JsonArray(),
        ["context"] = new JsonArray(),
        ["forwardedProps"] = new JsonObject(),
    };

    static async Task<List<JsonObject>> Events(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        return [.. body.Split('\n').Where(l => l.StartsWith("data: ", StringComparison.Ordinal)).Select(l => (JsonObject)JsonNode.Parse(l[6..])!)];
    }

    Task<HttpResponseMessage> Post(JsonObject input) => client.PostAsJsonAsync("/api/agent/runs", input);

    static IEnumerable<string> Types(IEnumerable<JsonObject> events) => events.Select(e => e["type"]!.GetValue<string>());

    static string Reply(IEnumerable<JsonObject> events) =>
        string.Concat(events.Where(e => e["type"]!.GetValue<string>() == "TEXT_MESSAGE_CONTENT").Select(e => e["delta"]!.GetValue<string>()));

    static List<JsonObject> Results(IEnumerable<JsonObject> events) =>
        [.. events.Where(e => e["type"]!.GetValue<string>() == "TOOL_CALL_RESULT").Select(e => (JsonObject)JsonNode.Parse(e["content"]!.GetValue<string>())!)];

    string User => host.Services.GetRequiredService<CurrentUser>().Id;

    int Workspaces() => host.Services.GetRequiredService<RatesWorkspaces>().OwnedBy(User).Count;

    [Fact] // ADR-0007 §1, §3, §6: one request makes a line and starts ticking it, through the commands, as the AI's edits
    public async Task A_request_creates_a_line_and_starts_ticking_through_the_commands()
    {
        var events = await Events(await Post(Input("t-1", "r-1", ("m-1", FullSwap + " and start ticking"))));

        Assert.Equal("RUN_STARTED", Types(events).First());
        Assert.Equal("RUN_FINISHED", Types(events).Last());
        Assert.Equal("scripted", events[0]["metadata"]!["qube"]!["runtime"]!.GetValue<string>());
        Assert.True(events[0]["metadata"]!["qube"]!["simulated"]!.GetValue<bool>());
        Assert.Equal(["rates_workspace_create", "entity_create", "rates_live_start"],
            events.Where(e => e["type"]!.GetValue<string>() == "TOOL_CALL_START").Select(e => e["toolCallName"]!.GetValue<string>()));
        var results = Results(events);
        Assert.All(results, r => Assert.Equal("done", r["status"]!.GetValue<string>()));

        var id = results[0]["outputs"]!["workspace"]!["workspaceId"]!.GetValue<string>();
        Assert.Equal(id, results[1]["outputs"]!["entity"]!["workspaceId"]!.GetValue<string>());
        Assert.Equal(id, results[2]["outputs"]!["subscription"]!["workspaceId"]!.GetValue<string>());
        var view = (await client.GetFromJsonAsync<JsonObject>($"/api/rates/workspaces/{id}"))!;
        Assert.Equal("2026-10-27", view["valuationDate"]!.GetValue<string>());
        Assert.True(view["lines"]!.AsArray().Single()!["priceable"]!.GetValue<bool>());
        Assert.Equal("ai", view["live"]!["startedFrom"]!.GetValue<string>());
        var log = host.Services.GetRequiredService<WorkspaceService>().Find(id)!.Log;
        Assert.All(log.Where(e => e.Kind != EditKind.Derive), e => Assert.Equal(RunOrigin.Ai, e.Origin));
        Assert.Contains($"workspace {id}", Reply(events), StringComparison.Ordinal);
    }

    [Fact] // ADR-0005 §4, ADR-0007 §6, ADR-0008 §4: what no default supplies is refused by name and asked for; the answer
           // completes it, even when it asks for more ("and start ticking" ticks the line the answer made)
    public async Task Missing_terms_are_asked_for_and_the_answer_completes_the_request()
    {
        var before = Workspaces();

        var asked = await Events(await Post(Input("t-2", "r-1", ("m-1", "make a 10y JPY TONA swap, 1bn, valuation date 2026-10-27"))));

        // The workspace is made; the line is refused for its direction alone, since the profile's template gives the
        // frequencies and C47 the quotes.
        var results = Results(asked);
        Assert.Equal(["rates.workspace.create", "entity.create"], results.Select(r => r["capability"]!.GetValue<string>()));
        Assert.Equal("refused", results[1]["status"]!.GetValue<string>());
        Assert.Equal(["/inputs/request/terms/direction"], results[1]["issues"]!.AsArray().Select(i => i!["path"]!.GetValue<string>()));
        Assert.Contains("direction", Reply(asked), StringComparison.Ordinal);
        var id = results[0]["outputs"]!["workspace"]!["workspaceId"]!.GetValue<string>();

        var answered = await Events(await Post(Input("t-2", "r-2", ("m-1", "ignored: the server already holds m-1"),
            ("m-2", "pay fixed and start ticking"))));

        results = Results(answered);
        Assert.Equal(["entity.create", "rates.live.start"], results.Select(r => r["capability"]!.GetValue<string>()));
        Assert.All(results, r => Assert.Equal("done", r["status"]!.GetValue<string>()));
        // The answer's line goes into the workspace the first turn made, not a second one.
        Assert.Equal(id, results[0]["outputs"]!["entity"]!["workspaceId"]!.GetValue<string>());
        Assert.Equal(id, results[1]["outputs"]!["subscription"]!["workspaceId"]!.GetValue<string>());
        Assert.Equal(before + 1, Workspaces());

        // A request after one that was done starts afresh: it takes none of the earlier terms, and with no workspace open
        // and no valuation date it has nowhere to go.
        var fresh = Results(await Events(await Post(Input("t-2", "r-3", ("m-3", "make a 5y swap"))))).Single();
        Assert.Equal("refused", fresh["status"]!.GetValue<string>());
        Assert.Contains("/inputs/request/workspaceId", fresh["issues"]!.AsArray().Select(i => i!["path"]!.GetValue<string>()));
    }

    [Fact] // ADR-0007 §2, §4 (AI04): a run posted again is read again, never run twice, and can be read from where a reader stopped
    public async Task A_run_is_read_again_rather_than_run_twice()
    {
        var before = Workspaces();
        var first = await Events(await Post(Input("t-3", "r-1", ("m-1", FullSwap))));
        var again = await Events(await Post(Input("t-3", "r-1", ("m-1", FullSwap))));

        Assert.Equal(first.Select(e => e.ToJsonString()), again.Select(e => e.ToJsonString()));
        Assert.Equal(before + 1, Workspaces());

        var rest = await Events(await client.GetAsync("/api/agent/threads/t-3/runs/r-1/events?after=2"));
        Assert.Equal(first.Skip(2).Select(e => e.ToJsonString()), rest.Select(e => e.ToJsonString()));

        var thread = (await client.GetFromJsonAsync<JsonObject>("/api/agent/threads/t-3"))!;
        // Two calls: the workspace, then the line in it (ADR-0008 §5).
        Assert.Equal(["user", "assistant", "tool", "assistant", "tool", "assistant"], thread["messages"]!.AsArray().Select(m => m!["role"]!.GetValue<string>()));
        Assert.Null(thread["activeRunId"]);
    }

    [Fact] // ADR-0007 §2, §3 (AI06): the client cannot offer tools or write the agent's history
    public async Task Client_tools_and_client_history_are_refused()
    {
        var withTools = Input("t-4", "r-1", ("m-1", "stop ticking"));
        withTools["tools"] = new JsonArray(new JsonObject { ["name"] = "click", ["description"] = "clicks a button", ["parameters"] = new JsonObject() });
        var refused = await Post(withTools);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal("FrontendToolsRefused", (await refused.Content.ReadFromJsonAsync<JsonObject>())!["code"]!.GetValue<string>());

        var withHistory = Input("t-4", "r-2", ("m-1", "stop ticking"));
        withHistory["messages"]!.AsArray().Add(new JsonObject
        {
            ["id"] = "a-1", ["role"] = "assistant", ["content"] = "",
            ["toolCalls"] = new JsonArray(new JsonObject { ["id"] = "c-1", ["type"] = "function", ["function"] = new JsonObject { ["name"] = "rates_live_stop", ["arguments"] = "{}" } }),
        });
        var history = await Post(withHistory);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, history.StatusCode);
    }

    [Fact] // ADR-0007 §3 (AI06): a tool the agent invents is refused, and nothing outside the commands runs
    public async Task A_tool_that_is_not_a_registered_command_is_refused()
    {
        var invented = host.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.AddSingleton<IAgentRuntime>(new Proposing(new AgentToolCall("c-1", "run_sql", "{\"sql\":\"delete from Workspace\"}")))));
        var events = await Events(await invented.CreateClient().PostAsJsonAsync("/api/agent/runs", Input("t-5", "r-1", ("m-1", "anything"))));

        var result = Assert.Single(Results(events));
        Assert.Equal("refused", result["status"]!.GetValue<string>());
        Assert.Equal("UnknownTool", result["code"]!.GetValue<string>());
        Assert.Equal("RUN_FINISHED", Types(events).Last());
    }

    [Fact] // ADR-0007 §4: a cancelled run invokes nothing more and finishes as cancelled
    public async Task A_cancelled_run_invokes_nothing_more()
    {
        var waiting = new Waiting();
        var blocked = host.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IAgentRuntime>(waiting)));
        var api = blocked.CreateClient();
        var stream = api.PostAsJsonAsync("/api/agent/runs", Input("t-6", "r-1", ("m-1", "anything")));
        await waiting.Asked.Task;

        Assert.Equal(HttpStatusCode.Accepted, (await api.PostAsync("/api/agent/threads/t-6/runs/r-1/cancel", null)).StatusCode);
        var events = await Events(await stream);

        Assert.Equal(["RUN_STARTED", "RUN_FINISHED"], Types(events));
        Assert.Equal("cancelled", events[^1]["outcome"]!["type"]!.GetValue<string>());
    }

    [Fact] // ADR-0007 §5: a runtime that is not configured stops startup instead of falling back to the scripted one
    public void An_unknown_runtime_stops_startup()
    {
        var unknown = host.WithWebHostBuilder(b => b.UseSetting("Agent:Runtime", "llama"));
        var failure = Assert.Throws<InvalidOperationException>(() => unknown.CreateClient());
        Assert.Contains("'llama'", failure.Message, StringComparison.Ordinal);
    }

    [Fact] // ADR-0017 §6.3: the stand-in runs on no model: no default model is registered, a run names none, and one chosen for its conversation is refused
    public async Task The_stand_in_runs_on_no_model_and_refuses_one_chosen()
    {
        var plain = await Events(await Post(Input("t-model-1", "r-1", ("m-1", "hello"))));
        var input = Input("t-model-2", "r-1", ("m-1", "hello"));
        input["forwardedProps"] = new JsonObject { ["qube"] = new JsonObject { ["model"] = "any-model" } };
        var refused = await Post(input);
        var listed = (await client.GetFromJsonAsync<JsonObject>("/api/settings"))!["settings"]!.AsArray().Select(s => s!["key"]!.GetValue<string>());

        Assert.Null(plain[0]["metadata"]!["qube"]!["model"]);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        var error = (await refused.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal(("ModelNotAllowed", "scripted · no model runs on no model, so no model is chosen for its conversations."),
            (error["code"]!.GetValue<string>(), error["message"]!.GetValue<string>()));
        Assert.DoesNotContain(AgentSettings.DefaultModel, listed);
    }

    /// <summary>Calls one tool, then says it is done.</summary>
    sealed class Proposing(AgentToolCall call) : IAgentRuntime
    {
        public AgentRuntimeInfo Info { get; } = new("test", "test runtime", true, []);

        public async Task RunAsync(AgentTurn turn, IAgentTurnSink sink, CancellationToken cancellationToken)
        {
            await sink.CallAsync(call, cancellationToken);
            await sink.SayAsync("done", cancellationToken);
        }
    }

    /// <summary>Waits until the run is cancelled.</summary>
    sealed class Waiting : IAgentRuntime
    {
        public TaskCompletionSource Asked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public AgentRuntimeInfo Info { get; } = new("test", "test runtime", true, []);

        public async Task RunAsync(AgentTurn turn, IAgentTurnSink sink, CancellationToken cancellationToken)
        {
            Asked.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }

    // What the pane sends of an XVA workspace open in the active panel (ADR-0007 §8).
    static JsonArray Showing(string workspaceId) =>
        [new JsonObject { ["description"] = "The XVA workspace open in the active panel", ["value"] = new JsonObject { ["kind"] = "xva.workspace", ["workspaceId"] = workspaceId }.ToJsonString() }];

    async Task<List<JsonObject>> Asked(string thread, string words, JsonArray context, int turn = 1)
    {
        var input = Input(thread, $"r-{turn}", ($"m-{turn}", words));
        input["context"] = context;
        return await Events(await Post(input));
    }

    static IEnumerable<string> Called(IEnumerable<JsonObject> events) =>
        events.Where(e => e["type"]!.GetValue<string>() == "TOOL_CALL_START").Select(e => e["toolCallName"]!.GetValue<string>());

    [Fact] // ADR-0016 §7, note 38, ADR-0007 §6: the stand-in describes, reads and previews the workspace the pane shows, through the three tools, and says each count as its tool gave it
    public async Task The_scripted_agent_describes_reads_and_previews_the_workspace_the_pane_shows()
    {
        var made = await client.PostAsJsonAsync("/api/xva/workspaces", new { registration = "ABC/LE-01", valueDate = "2026-10-26", portfolioSize = 10 });
        var ws = (await made.Content.ReadFromJsonAsync<JsonObject>())!;
        var id = ws["workspaceId"]!.GetValue<string>();
        var page = (await (await client.PostAsJsonAsync($"/api/xva/workspaces/{id}/rows", new { start = 0, count = 200, sorts = new JsonArray(), grid = "existing" })).Content.ReadFromJsonAsync<JsonObject>())!;
        var usd = page["rows"]!.AsArray().Where(r => r!["currency"]!.GetValue<string>() == "USD").Select(r => r!["tradeId"]!.GetValue<string>()).ToList();
        Assert.NotEmpty(usd);

        var described = await Asked("t-describe", "describe the tables", Showing(id));
        var read = await Asked("t-read", "read existing where ccy=USD fields notional, csa", Showing(id));
        var previewed = await Asked("t-preview", "preview set existing after off", Showing(id));
        var existing = page["rows"]!.AsArray().Count;

        Assert.Equal(["tables_describe"], Called(described));
        Assert.StartsWith("The workspace has ", Reply(described));
        Assert.Contains("trades (", Reply(described));
        Assert.Equal(["tables_read"], Called(read));
        var rows = Results(read).Single()["outputs"]!["answer"]!;
        Assert.Equal((usd.Count, "notional|csa"), (rows["count"]!.GetValue<int>(), string.Join("|", rows["fields"]!.AsArray().Select(f => f!.GetValue<string>()))));
        Assert.Equal(usd, rows["rows"]!.AsArray().Select(r => r!["key"]!.GetValue<string>()));
        Assert.Equal($"{usd.Count} row(s) of xva.trades match, and all are listed.", Reply(read));
        Assert.Equal(["commands_preview"], Called(previewed));
        Assert.Equal((6, $"`set existing after off` would act on trades, 6 rows: A: 6 on to off. Nothing was run."), (existing, Reply(previewed)));
        // Nothing was written by any of the three.
        Assert.Equal(ws["revision"]!.GetValue<long>(), (await client.GetFromJsonAsync<JsonObject>($"/api/xva/workspaces/{id}"))!["revision"]!.GetValue<long>());
    }

    [Fact] // ADR-0007 §6, ADR-0016 note 38: with no workspace in the context a reading is refused for what it lacks, and the stand-in asks for it
    public async Task The_scripted_agent_asks_for_a_workspace_when_the_pane_shows_none()
    {
        var events = await Asked("t-none", "read all", []);

        Assert.Equal("refused", Results(events).Single()["status"]!.GetValue<string>());
        Assert.Contains("it needs workspaceId", Reply(events));
    }

    [Fact] // ADR-0016 note 38, ADR-0007 §6: a reading that was refused took every word after its own, so what is said next is a request of its own and never more rows of the refused one
    public async Task What_follows_a_refused_reading_is_a_request_of_its_own()
    {
        var made = await client.PostAsJsonAsync("/api/xva/workspaces", new { registration = "ABC/LE-01", valueDate = "2026-10-26", portfolioSize = 10 });
        var id = (await made.Content.ReadFromJsonAsync<JsonObject>())!["workspaceId"]!.GetValue<string>();

        var refused = await Asked("t-refused", "read tides", Showing(id));
        var next = await Asked("t-refused", "describe the tables", Showing(id), turn: 2);

        Assert.Equal(("tables.read", "refused"), (Results(refused).Single()["capability"]!.GetValue<string>(), Results(refused).Single()["status"]!.GetValue<string>()));
        Assert.Equal(["tables_describe"], Called(next));
        Assert.StartsWith("The workspace has ", Reply(next));
    }
    // The pane's context of the open panels (ADR-0018 §8), as the client sends it: each panel by its id and title.
    static JsonArray Panels(params (string Id, string Title)[] panels) =>
        [new JsonObject
        {
            ["description"] = "The open panels and how they are arranged",
            ["value"] = new JsonObject
            {
                ["kind"] = "qube.panels",
                ["panels"] = new JsonArray([.. panels.Select(p => (JsonNode)new JsonObject { ["panel"] = p.Id, ["title"] = p.Title, ["area"] = "area-1" })]),
            }.ToJsonString(),
        }];

    [Fact] // ADR-0018 §8, ADR-0007 §6: the stand-in proposes the panels it is asked for side by side, by their titles in the context, and moves nothing
    public async Task The_scripted_agent_proposes_two_panels_side_by_side_and_one_beside_another()
    {
        JsonArray Context() => Panels(("panel-1", "Pricing DB"), ("panel-2", "Rates 2026-10-27"), ("panel-3", "Rates 2026-10-27 · 2"));

        var sideBySide = await Asked("t-side", "put rates 2026-10-27 · 2 and pricing db side by side", Context());
        var beside = await Asked("t-beside", "put the pricing db beside rates 2026-10-27", Context());

        Assert.Equal(["layout_propose"], Called(sideBySide));
        var proposed = Results(sideBySide).Single()["outputs"]!["answer"]!;
        Assert.Equal(["panel-3", "panel-1"], proposed["places"]!.AsArray().Select(p => p!["panel"]!.GetValue<string>()));
        Assert.Equal((2, 640), (proposed["count"]!.GetValue<int>(), proposed["width"]!.GetValue<int>()));
        Assert.StartsWith("Proposed 2 areas, for a panels' space of at least 640 by 240px.", Reply(sideBySide));
        // "a beside b" keeps b on the left.
        var placed = Results(beside).Single()["outputs"]!["answer"]!["places"]!.AsArray();
        Assert.Equal(["panel-2", "panel-1"], placed.Select(p => p!["panel"]!.GetValue<string>()));
    }

    [Fact] // ADR-0018 §8, ADR-0007 §6: a request naming one open panel leaves the other area empty, and the stand-in says what the proposal lacks
    public async Task The_scripted_agent_asks_for_a_second_panel_when_the_request_names_one()
    {
        var events = await Asked("t-one", "put pricing db side by side", Panels(("panel-1", "Pricing DB"), ("panel-2", "Rates 2026-10-27")));

        Assert.Equal("refused", Results(events).Single()["status"]!.GetValue<string>());
        Assert.Contains("Area area 2 holds nothing", Reply(events));
        Assert.Contains("two open panels by their titles", Reply(events));
    }
}
