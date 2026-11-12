using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Qube.Agent;
using Qube.AgentHost;
using Qube.AgentHost.Contracts;
using Qube.Composition.Application;
using Qube.Composition.Application.Settings;
using Qube.Composition.Application.Workspaces;
using Qube.Samples.Rates;
using Qube.Web;

namespace Qube.Composition.Tests;

// ADR-0007 §5, C44: the Copilot runtime. The workbench posts each turn to the agent host, a process of its own that
// holds the model credential; the host answers the model's words and tool calls, and every call is invoked in the
// workbench for the signed-in user. The model here is a fake behind the host's backend port, so the wire, the host and
// the workbench's side are the real ones; the Copilot SDK itself is exercised on the owner's machine.
public class CopilotRuntimeTests(QubeFactory factory) : IClassFixture<QubeFactory>, IAsyncLifetime
{
    const string Key = "test-key-0123456789-0123456789-0123456789";
    readonly FakeModel model = new();
    WebApplication? agentHost;
    WebApplicationFactory<Program>? qube;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration["AgentHost:Key"] = Key;
        builder.Services.AddSingleton<IModelBackend>(model);
        agentHost = AgentHostApp.Build(builder);
        await agentHost.StartAsync();
        var handler = agentHost.GetTestServer().CreateHandler();
        qube = factory.WithWebHostBuilder(b => b
            .UseSetting("Rates:Ticker", "false")
            .UseSetting("Agent:Runtime", "copilot")
            .UseSetting("Agent:Copilot:HostUrl", "http://agent-host/")
            .UseSetting("Agent:Copilot:HostKey", Key)
            .UseSetting("Agent:Copilot:Models:0", "fake-model")
            .UseSetting("Agent:Copilot:Models:1", "other-model")
            .ConfigureTestServices(s => s.AddHttpClient(Program.CopilotHttpClient).ConfigurePrimaryHttpMessageHandler(() => handler)));
    }

    public async Task DisposeAsync()
    {
        qube?.Dispose();
        if (agentHost is not null) await agentHost.DisposeAsync();
    }

    WebApplicationFactory<Program> Qube => qube!;

    static readonly JsonObject NewWorkspace = new() { ["request"] = new JsonObject { ["valuationDate"] = "2026-10-27" } };

    static JsonObject Swap(string workspaceId) => new()
    {
        ["request"] = new JsonObject { ["product"] = "rates.irs", ["workspaceId"] = workspaceId, ["terms"] = "jpy 10y 1bn pay" },
    };

    static JsonObject Input(string threadId, string runId, string messageId, string text, params (string Description, string Value)[] context) => new()
    {
        ["threadId"] = threadId,
        ["runId"] = runId,
        ["messages"] = new JsonArray(new JsonObject { ["id"] = messageId, ["role"] = "user", ["content"] = text }),
        ["tools"] = new JsonArray(),
        ["context"] = new JsonArray([.. context.Select(c => (JsonNode)new JsonObject { ["description"] = c.Description, ["value"] = c.Value })]),
    };

    async Task<List<JsonObject>> Run(JsonObject input, HttpClient? client = null)
    {
        var response = await (client ?? Qube.CreateClient()).PostAsJsonAsync("/api/agent/runs", input);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadAsStringAsync();
        return [.. body.Split('\n').Where(l => l.StartsWith("data: ", StringComparison.Ordinal)).Select(l => (JsonObject)JsonNode.Parse(l[6..])!)];
    }

    static IEnumerable<string> Types(IEnumerable<JsonObject> events) => events.Select(e => e["type"]!.GetValue<string>());

    static string Reply(IEnumerable<JsonObject> events) =>
        string.Concat(events.Where(e => e["type"]!.GetValue<string>() == "TEXT_MESSAGE_CONTENT").Select(e => e["delta"]!.GetValue<string>()));

    int Workspaces() => Qube.Services.GetRequiredService<RatesWorkspaces>().OwnedBy(Qube.Services.GetRequiredService<CurrentUser>().Id).Count;

    [Fact] // ADR-0007 §3, §5, C44: the model's tool call runs as the user's command in the workbench, and the run says it is a model
    public async Task A_Copilot_tool_call_runs_as_the_users_command_in_the_workbench()
    {
        string? seen = null;
        model.Answer = async (turn, sink, ct) =>
        {
            var made = (JsonObject)JsonNode.Parse(await sink.CallAsync("call-1", "rates_workspace_create", NewWorkspace.ToJsonString(), ct))!;
            seen = await sink.CallAsync("call-2", "entity_create", Swap(made["outputs"]!["workspace"]!["workspaceId"]!.GetValue<string>()).ToJsonString(), ct);
            sink.Say("Made the swap.");
        };
        var before = Workspaces();

        var events = await Run(Input("c-1", "r-1", "m-1", "make a 10y JPY TONA payer, 1bn", ("Active panel", "Pricing DB")));

        Assert.Equal("RUN_FINISHED", Types(events).Last());
        var qubeMeta = events[0]["metadata"]!["qube"]!;
        Assert.Equal("copilot", qubeMeta["runtime"]!.GetValue<string>());
        Assert.Equal("Copilot", qubeMeta["label"]!.GetValue<string>());
        Assert.Equal("fake-model", qubeMeta["model"]!.GetValue<string>());
        Assert.False(qubeMeta["simulated"]!.GetValue<bool>());
        Assert.Equal(["rates_workspace_create", "entity_create"], events.Where(e => e["type"]!.GetValue<string>() == "TOOL_CALL_START").Select(e => e["toolCallName"]!.GetValue<string>()));
        Assert.Equal("Made the swap.", Reply(events));

        var result = (JsonObject)JsonNode.Parse(seen!)!;
        Assert.Equal("done", result["status"]!.GetValue<string>());
        var id = result["outputs"]!["entity"]!["workspaceId"]!.GetValue<string>();
        Assert.Equal(before + 1, Workspaces());
        var log = Qube.Services.GetRequiredService<WorkspaceService>().Find(id)!.Log;
        Assert.All(log.Where(e => e.Kind != EditKind.Derive), e => Assert.Equal(RunOrigin.Ai, e.Origin));

        // The host was offered the workbench's commands and nothing else, with the request's context and no history yet.
        var turn = Assert.Single(model.Turns);
        Assert.Equal("fake-model", turn.Model);
        Assert.Contains("entity_create", turn.Tools.Select(t => t.Name));
        Assert.DoesNotContain("rates_line_create", turn.Tools.Select(t => t.Name));
        Assert.All(turn.Tools, t => Assert.NotNull(Qube.Services.GetRequiredService<AgentToolGateway>().CapabilityOf(t.Name)));
        Assert.Contains("- Active panel: Pricing DB", turn.Prompt, StringComparison.Ordinal);
        Assert.Null(turn.Earlier);
    }

    [Fact] // ADR-0007 §2: the conversation is the workbench's, and goes with each turn for a host that has lost its session
    public async Task Each_turn_carries_the_conversation_before_it()
    {
        model.Answer = (turn, sink, ct) =>
        {
            sink.Say(turn.Earlier is null ? "Which currency?" : "JPY it is.");
            return Task.CompletedTask;
        };
        var client = Qube.CreateClient();
        await Run(Input("c-2", "r-1", "m-1", "make a swap"), client);
        var events = await Run(Input("c-2", "r-2", "m-2", "JPY"), client);

        Assert.Equal("JPY it is.", Reply(events));
        Assert.Equal("User: make a swap\nAssistant: Which currency?\n", model.Turns[1].Earlier);
        Assert.Equal("JPY", model.Turns[1].Prompt);
    }

    [Fact] // ADR-0007 §3 (AI06): a tool the model invents is refused by the workbench, and the refusal goes back to the model
    public async Task A_tool_the_model_invents_is_refused_and_the_model_is_told()
    {
        string? seen = null;
        model.Answer = async (turn, sink, ct) => seen = await sink.CallAsync("call-1", "run_sql", "{\"sql\":\"delete from Workspace\"}", ct);

        var events = await Run(Input("c-3", "r-1", "m-1", "clean up"));

        Assert.Equal("RUN_FINISHED", Types(events).Last());
        var result = (JsonObject)JsonNode.Parse(seen!)!;
        Assert.Equal("refused", result["status"]!.GetValue<string>());
        Assert.Equal("UnknownTool", result["code"]!.GetValue<string>());
    }

    [Fact] // ADR-0007 §4: stopping a run stops the model's turn too
    public async Task Cancelling_a_run_aborts_the_models_turn()
    {
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var aborted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        model.Answer = async (turn, sink, ct) =>
        {
            asked.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            finally
            {
                aborted.TrySetResult();
            }
        };
        var client = Qube.CreateClient();
        var stream = Run(Input("c-4", "r-1", "m-1", "think hard"), client);
        await asked.Task;

        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync("/api/agent/threads/c-4/runs/r-1/cancel", null)).StatusCode);
        var events = await stream;

        Assert.Equal("cancelled", events[^1]["outcome"]!["type"]!.GetValue<string>());
        await aborted.Task;
    }

    [Fact] // ADR-0007 §5: a failure at the model ends the run with what the model's side said, and nothing stands in for it
    public async Task A_model_failure_ends_the_run_with_its_message()
    {
        model.Answer = (_, _, _) => throw new ModelFailed("Copilot quota exhausted");

        var events = await Run(Input("c-5", "r-1", "m-1", "anything"));

        var error = events[^1];
        Assert.Equal("RUN_ERROR", error["type"]!.GetValue<string>());
        Assert.Equal("AgentFailed", error["code"]!.GetValue<string>());
        Assert.Contains("Copilot quota exhausted", error["message"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact] // C44: the host answers only a caller holding the shared key, and does not start without one
    public async Task The_host_refuses_a_caller_without_the_key_and_will_not_start_without_one()
    {
        var anonymous = agentHost!.GetTestServer().CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/models")).StatusCode);
        anonymous.DefaultRequestHeaders.Add(AgentHostWire.KeyHeader, "wrong-key-0123456789-0123456789-0123456789");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/models")).StatusCode);

        var keyless = WebApplication.CreateBuilder();
        keyless.WebHost.UseTestServer();
        keyless.Services.AddSingleton<IModelBackend>(model);
        var failure = Assert.Throws<InvalidOperationException>(() => AgentHostApp.Build(keyless));
        Assert.Contains("AgentHost:Key", failure.Message, StringComparison.Ordinal);
    }

    [Fact] // ADR-0007 §5: the Copilot runtime without its settings stops startup instead of falling back to the scripted one
    public void Copilot_without_its_settings_stops_startup()
    {
        var unset = factory.WithWebHostBuilder(b => b.UseSetting("Agent:Runtime", "copilot"));
        var failure = Assert.Throws<InvalidOperationException>(() => unset.CreateClient());
        Assert.Contains("Agent:Copilot:", failure.Message, StringComparison.Ordinal);
    }

    string User => Qube.Services.GetRequiredService<CurrentUser>().Id;

    UserSettings Settings => Qube.Services.GetRequiredService<UserSettings>();

    /// <summary>The request with the model chosen in the pane for its conversation, as the pane sends it.</summary>
    static JsonObject WithChosenModel(JsonObject input, string chosen)
    {
        input["forwardedProps"] = new JsonObject { ["qube"] = new JsonObject { ["model"] = chosen } };
        return input;
    }

    /// <summary>The model a run says it ran on.</summary>
    static string? RanOn(List<JsonObject> events) => events[0]["metadata"]!["qube"]!["model"]?.GetValue<string>();

    void ModelSaysOk() => model.Answer = (_, sink, _) =>
    {
        sink.Say("ok");
        return Task.CompletedTask;
    };

    [Fact] // ADR-0017 §6.3: a new conversation starts on the user's default model and holds it after the default moves; each run says the model it ran on
    public async Task A_new_conversation_starts_on_the_users_default_model()
    {
        ModelSaysOk();
        Settings.Set(User, "default model", "other-model");
        try
        {
            var first = await Run(Input("c-m1", "r-1", "m-1", "hello"));
            Settings.Set(User, "default model", "fake-model");
            var held = await Run(Input("c-m1", "r-2", "m-2", "again"));
            var next = await Run(Input("c-m2", "r-1", "m-1", "hello"));

            Assert.Equal(["other-model", "other-model", "fake-model"], model.Turns.Select(t => t.Model));
            Assert.Equal(["other-model", "other-model", "fake-model"], new[] { first, held, next }.Select(RanOn));
        }
        finally
        {
            Settings.Reset(User, "default model");
        }
    }

    [Fact] // ADR-0017 §6.3: a model chosen in the pane is its conversation's alone, and one the configuration does not allow is refused and runs nothing
    public async Task A_model_chosen_for_a_conversation_is_its_own_and_one_not_allowed_is_refused()
    {
        ModelSaysOk();
        var client = Qube.CreateClient();

        var chosen = await Run(WithChosenModel(Input("c-m3", "r-1", "m-1", "hello"), "other-model"), client);
        var kept = await Run(Input("c-m3", "r-2", "m-2", "again"), client);
        var other = await Run(Input("c-m4", "r-1", "m-1", "hello"), client);
        var refused = await client.PostAsJsonAsync("/api/agent/runs", WithChosenModel(Input("c-m5", "r-1", "m-1", "hello"), "unlisted-model"));

        Assert.Equal(["other-model", "other-model", "fake-model"], model.Turns.Select(t => t.Model));
        Assert.Equal(["other-model", "other-model", "fake-model"], new[] { chosen, kept, other }.Select(RanOn));
        Assert.Equal("other-model", Qube.Services.GetRequiredService<AgentThreads>().Find("c-m3")!.Model);
        var setting = Settings.One(User, "default model");
        Assert.Equal((false, "fake-model"), (setting.Set, setting.Value!.GetValue<string>()));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        var error = (await refused.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal(("ModelNotAllowed", "unlisted-model is not one of the models the AI may use: fake-model, other-model."),
            (error["code"]!.GetValue<string>(), error["message"]!.GetValue<string>()));
    }

    [Fact] // ADR-0017 §2, §6.3: a default model the configuration no longer allows is a preference no longer taken: a new conversation runs on the registered one, and the settings say the user's is unreadable
    public async Task A_default_model_no_longer_allowed_gives_way_to_the_registered_one_and_says_so()
    {
        ModelSaysOk();
        Qube.Services.GetRequiredService<IUserSettingStore>().Put(User, new StoredSetting(AgentSettings.DefaultModel, JsonValue.Create("retired-model"), DateTimeOffset.UnixEpoch));
        try
        {
            var events = await Run(Input("c-m6", "r-1", "m-1", "hello"));
            var setting = Settings.One(User, "default model");

            Assert.Equal("fake-model", RanOn(events));
            Assert.Equal("fake-model", Assert.Single(model.Turns).Model);
            Assert.Equal(("retired-model", "one of fake-model, other-model"), (setting.Stored!.GetValue<string>(), setting.Unreadable));
        }
        finally
        {
            Settings.Reset(User, "default model");
        }
    }

    [Fact] // ADR-0017 §6.3: the one model of before stops startup and says it is now a list, rather than being read as a list of one
    public void The_one_model_of_before_stops_startup_naming_the_list()
    {
        var before = factory.WithWebHostBuilder(b => b
            .UseSetting("Agent:Runtime", "copilot")
            .UseSetting("Agent:Copilot:HostUrl", "http://agent-host/")
            .UseSetting("Agent:Copilot:HostKey", Key)
            .UseSetting("Agent:Copilot:Model", "fake-model"));
        var failure = Assert.Throws<InvalidOperationException>(() => before.CreateClient());
        Assert.Contains("Agent:Copilot:Model is now Agent:Copilot:Models", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>The model behind the host, answering as each test says, and keeping the turns it was asked.</summary>
    sealed class FakeModel : IModelBackend
    {
        public List<HostTurn> Turns { get; } = [];

        public Func<HostTurn, IModelTurnSink, CancellationToken, Task> Answer { get; set; } = (_, _, _) => Task.CompletedTask;

        public Task RunAsync(HostTurn turn, IModelTurnSink sink, CancellationToken cancellationToken)
        {
            lock (Turns) Turns.Add(turn);
            return Answer(turn, sink, cancellationToken);
        }

        public Task<IReadOnlyList<string>> ModelsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>(["fake-model"]);
    }
}
