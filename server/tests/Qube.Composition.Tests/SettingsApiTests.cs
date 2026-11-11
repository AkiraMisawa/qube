using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Qube.Agent;
using Qube.Composition.Application;
using Qube.Composition.Contracts;

namespace Qube.Composition.Tests;

// ADR-0017 §2 and §4: a user's settings through the one gateway, as the command line, the settings dialog and the AI
// reach them, and as the page reads them when it starts.
public sealed class SettingsApiTests(QubeFactory factory) : IClassFixture<QubeFactory>
{
    readonly HttpClient client = factory.CreateClient();

    Task<HttpResponseMessage> Command(string capability, JsonObject request) =>
        client.PostAsJsonAsync($"/api/commands/{capability}/1.0.0", new { inputs = new JsonObject { ["request"] = request }, idempotencyKey = Guid.NewGuid().ToString() });

    static async Task<JsonObject> Answer(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!["outputs"]!["answer"]!.AsObject();
    }

    async Task<JsonObject> Setting(string key) =>
        (await client.GetFromJsonAsync<JsonObject>("/api/settings"))!["settings"]!.AsArray().Single(s => s!["key"]!.GetValue<string>() == key)!.AsObject();

    [Fact] // ADR-0017 §2: the page reads the user's settings as it starts, each with what it takes and whether it was set
    public async Task The_page_reads_the_users_settings_with_what_each_takes()
    {
        var theme = await Setting("shell.theme");

        Assert.Equal("Theme", theme["title"]!.GetValue<string>());
        Assert.Equal("preference", theme["kind"]!.GetValue<string>());
        Assert.Equal(["dark", "light", "auto"], theme["choices"]!.AsArray().Select(c => c!.GetValue<string>()));
        Assert.Equal("dark", theme["registered"]!.GetValue<string>());
    }

    [Fact] // ADR-0017 §4: set and reset through the gateway say what they replaced and how to set it back
    public async Task Set_and_reset_say_what_they_replaced()
    {
        var set = await Answer(await Command("settings.set", new JsonObject { ["setting"] = "theme", ["value"] = "Light" }));
        Assert.Equal("light", set["value"]!.GetValue<string>());
        Assert.Equal("Theme is light; it was dark, as registered. To set it back: settings reset theme.", set["said"]!.GetValue<string>());
        Assert.True((await Setting("shell.theme"))["set"]!.GetValue<bool>());

        var reset = await Answer(await Command("settings.reset", new JsonObject { ["setting"] = "theme" }));
        Assert.Equal("Theme is dark again, as registered; it was light. To set it back: settings theme light.", reset["said"]!.GetValue<string>());
        Assert.False((await Setting("shell.theme"))["set"]!.GetValue<bool>());
    }

    [Fact] // ADR-0017 §2: a value the setting does not take is refused by name, and nothing is written
    public async Task A_value_not_taken_is_refused_by_name()
    {
        var response = await Command("settings.set", new JsonObject { ["setting"] = "theme", ["value"] = "pink" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var refused = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal("InvalidValue", refused["code"]!.GetValue<string>());
        Assert.Contains("Theme is", refused["issues"]![0]!["message"]!.GetValue<string>());
        Assert.Contains("pink is not", refused["issues"]![0]!["message"]!.GetValue<string>());
    }

    [Fact] // ADR-0017 §5: a grid's widths are kept by its id, and read back as the page wrote them
    public async Task A_grids_widths_are_kept_by_its_id()
    {
        await Answer(await Command("settings.set", new JsonObject { ["setting"] = "grid.widths.xva/trades/existing", ["value"] = """{"notional":180}""" }));

        var widths = await Setting("grid.widths.xva/trades/existing");
        Assert.Equal("widths xva/trades/existing", widths["name"]!.GetValue<string>());
        Assert.Equal(180, JsonNode.Parse(widths["value"]!.GetValue<string>())!["notional"]!.GetValue<int>());
    }

    [Fact] // ADR-0017 §4, ADR-0005 §2: the settings' commands are commands, never a workflow step, and the agent is offered them
    public void The_settings_commands_are_commands_the_agent_is_offered()
    {
        var registry = factory.Services.GetRequiredService<CompositionRegistry>();
        var workflow = new WorkflowDefinition("workflow", CompositionValidator.SchemaVersion, "workflow.test.setting", "1.0.0", "Setting as a step",
            "settings.set-request", "1.0.0",
            [new WorkflowStep("set", "settings.set", "1.0.0", [], new Dictionary<string, Binding> { ["request"] = new ConstantBinding(new JsonObject { ["setting"] = "theme", ["value"] = "light" }) }, FailurePolicy.Stop)],
            new Dictionary<string, Binding> { ["answer"] = new OutputBinding("set", "answer") });

        Assert.Contains(new CompositionValidator(registry).Validate(workflow), i => i.Code == "CommandAsStep");
        var tools = factory.Services.GetRequiredService<AgentToolGateway>().Tools.Select(t => t.Name).ToList();
        Assert.Contains("settings_read", tools);
        Assert.Contains("settings_set", tools);
        Assert.Contains("settings_reset", tools);
    }
}
