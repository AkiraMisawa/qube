using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Qube.Agent;
using Qube.Composition.Application;
using Qube.Composition.Application.Workspaces;
using Qube.Composition.Contracts;

namespace Qube.Composition.Tests;

// ADR-0005, ADR-0010 §9, ADR-0016 §3: the XVA page's vary, add each and set, as the agent's tool calls reach them. Each takes what
// the command line takes, goes through the same workspace service, answers in the command line's words, and the log
// says the AI did it. Every name and figure is synthetic.
public class PricerCommandTests(QubeFactory factory) : IClassFixture<QubeFactory>
{
    readonly HttpClient client = factory.CreateClient();

    async Task<JsonObject> Create()
    {
        var response = await client.PostAsJsonAsync("/api/xva/workspaces", new { registration = "ABC/LE-01", valueDate = "2026-10-26", portfolioSize = 10 });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!;
    }

    Task<HttpResponseMessage> Command(string capability, JsonObject request) =>
        client.PostAsJsonAsync($"/api/agent/commands/{capability}/1.0.0", new { inputs = new JsonObject { ["request"] = request }, idempotencyKey = Guid.NewGuid().ToString() });

    static async Task<JsonObject> Ok(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!["outputs"]!["answer"]!.AsObject();
    }

    async Task<JsonObject> View(string id) => (await client.GetFromJsonAsync<JsonObject>($"/api/xva/workspaces/{id}"))!;

    [Fact] // ADR-0005, ADR-0010 §9: each command is a tool with its input contract, the fields the command line takes
    public void Each_command_is_offered_to_the_agent_with_its_contract()
    {
        var tools = factory.Services.GetRequiredService<AgentToolGateway>().Tools.ToDictionary(t => t.Name);

        string[] Fields(string tool) => [.. tools[tool].Parameters["properties"]!["request"]!["properties"]!.AsObject().Select(p => p.Key)];
        string[] Required(string tool) => [.. tools[tool].Parameters["properties"]!["request"]!["required"]!.AsArray().Select(r => r!.GetValue<string>())];
        // A cell is named once: as the line names it, or by its entity and field as a page's dialog does (ADR-0016 note 28).
        Assert.Equal(["workspaceId", "revision", "cell", "entity", "field", "values"], Fields("xva_vary"));
        Assert.Equal(["workspaceId", "values"], Required("xva_vary"));
        // The iterations are a list the tool takes as a typed value (ADR-0016 §3, note 37): the trades each case adds and takes out.
        Assert.Equal(["workspaceId", "revision", "iterations"], Fields("xva_trades_add"));
        Assert.Equal(["add", "without"], tools["xva_trades_add"].Parameters["properties"]!["request"]!["properties"]!["iterations"]!["items"]!["properties"]!.AsObject().Select(p => p.Key));
        // `set` is the kernel's, over the pricer's registered tables (ADR-0016 §3): no tool of the pricer's own.
        Assert.Equal(["workspaceId", "revision", "table", "rows", "set"], Fields("entity_set"));
        Assert.Equal(["workspaceId", "set"], Required("entity_set"));
        Assert.DoesNotContain("xva_set", tools.Keys);
    }

    [Fact] // ADR-0010 §9: an AI vary makes the cases the command line makes, says them as it does, and the log says the AI made them
    public async Task An_ai_vary_makes_the_cases_the_command_line_makes_logged_as_the_ais()
    {
        var ws = await Create();
        var id = ws["workspaceId"]!.GetValue<string>();

        var answer = await Ok(await Command("xva.vary", new JsonObject { ["workspaceId"] = id, ["cell"] = "CSA-A threshold", ["values"] = "10mm, 5mm, 0" }));

        Assert.Equal(2, answer["made"]!.GetValue<int>());
        Assert.Equal("Made 2 case(s): CSA-A threshold 10mm, CSA-A threshold 5mm; skipped 0 is the workspace's value.", answer["said"]!.GetValue<string>());
        Assert.Equal(["Base", "CSA-A threshold 10mm", "CSA-A threshold 5mm"], (await View(id))["cases"]!.AsArray().Select(c => c!["title"]!.GetValue<string>()));
        var log = factory.Services.GetRequiredService<WorkspaceService>().Find(id)!.Log;
        var made = log.Where(e => e.Seq > ws["revision"]!.GetValue<long>()).ToList();
        Assert.Equal(4, made.Count);
        Assert.All(made, e => Assert.Equal(RunOrigin.Ai, e.Origin));
    }

    [Fact] // ADR-0010 §9, docs/03 §5: the AI's add each and set do what the command line's do; a set names the revision it was made against
    public async Task The_ais_add_each_and_set_do_what_the_command_lines_do()
    {
        var ws = await Create();
        var id = ws["workspaceId"]!.GetValue<string>();

        var added = await Ok(await Command("xva.trades.add", new JsonObject { ["workspaceId"] = id }));
        var set = await Ok(await Command("entity.set", new JsonObject
        {
            ["workspaceId"] = id, ["revision"] = added["revision"]!.GetValue<long>(), ["rows"] = "CSA-A", ["set"] = "threshold=5mm",
        }));
        var moved = await Command("entity.set", new JsonObject
        {
            ["workspaceId"] = id, ["revision"] = added["revision"]!.GetValue<long>(), ["rows"] = "CSA-A", ["set"] = "threshold=6mm",
        });

        Assert.Equal("Made 3 case(s): add DRAFT-1, add DRAFT-2, add DRAFT-3.", added["said"]!.GetValue<string>());
        Assert.Equal("Set CSA-A threshold to 5mm.", set["said"]!.GetValue<string>());
        Assert.Equal(5_000_000m, (await View(id))["csas"]!.AsArray().Single(c => c!["csaId"]!.GetValue<string>() == "CSA-A")!["counterpartyThreshold"]!.GetValue<decimal>());
        Assert.Equal(HttpStatusCode.Conflict, moved.StatusCode);
    }

    [Fact] // ADR-0016 §6: over HTTP a refusal says the word at fault and what it could have been, on its issue
    public async Task A_refused_set_answers_the_word_at_fault_and_what_it_could_have_been()
    {
        var id = (await Create())["workspaceId"]!.GetValue<string>();

        var refused = await Command("entity.set", new JsonObject { ["workspaceId"] = id, ["rows"] = "CSA-A", ["set"] = "treshold=5mm" });
        var error = (await refused.Content.ReadFromJsonAsync<JsonObject>())!;
        var issue = Assert.Single(error["issues"]!.AsArray())!;

        Assert.Equal((HttpStatusCode.UnprocessableEntity, "UnknownField"), (refused.StatusCode, error["code"]!.GetValue<string>()));
        Assert.Equal("treshold", issue["word"]!.GetValue<string>());
        Assert.Equal(["csa id", "type", "threshold"], issue["candidates"]!.AsArray().Take(3).Select(c => c!.GetValue<string>()));
    }

    [Fact] // ADR-0010 §8's note of 2026-11-09, ADR-0016 note 28: the AI's cds chooses the CDS the Credit tab does, and the page's door, through the same command, says the same
    public async Task The_ais_cds_chooses_the_counterpartys_curve_as_the_command_line_does()
    {
        var id = (await Create())["workspaceId"]!.GetValue<string>();

        var answer = await Ok(await Command("xva.cds", new JsonObject { ["workspaceId"] = id, ["cds"] = "proxy financials europe A" }));
        Assert.Equal("The CDS of ABC/LE-01 is the proxy PROXY.FINANCIALS.EUROPE.A | USD | SNRFOR | XR14 | Proxy Financials · Europe · A (synthetic), recovery 40%.",
            answer["said"]!.GetValue<string>());
        Assert.Equal("PROXY.FINANCIALS.EUROPE.A", (await View(id))["credit"]!["cds"]!["curveId"]!.GetValue<string>());

        var revision = (await View(id))["revision"]!.GetValue<long>();
        Task<HttpResponseMessage> Door(long at, string cds) => client.PostAsJsonAsync("/api/commands/xva.cds/1.0.0",
            new { inputs = new { request = new { workspaceId = id, revision = at, cds } }, idempotencyKey = Guid.NewGuid().ToString() });
        var chosen = await Ok(await Door(revision, "none"));
        Assert.Equal("No CDS for ABC/LE-01: choose a single name or a proxy on the Credit tab, or with cds.", chosen["said"]!.GetValue<string>());
        Assert.Null((await View(id))["credit"]!["cds"]);

        var ambiguous = await Door(revision + 1, "abch");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, ambiguous.StatusCode);
        var catalogue = (await client.GetFromJsonAsync<JsonObject>("/api/xva/cds"))!;
        Assert.Contains(catalogue["singleNames"]!.AsArray(), c => c!["reference"]!.GetValue<string>() == "ABCH | USD | SNRFOR | XR14 | ABC Holdings (synthetic)");
        Assert.Equal(["6m", "1y", "2y", "3y", "5y", "7y", "10y"], catalogue["tenors"]!.AsArray().Select(t => t!.GetValue<string>()));
    }

    [Fact] // ADR-0005, ADR-0010 §9: a cell the command cannot name is refused by name, as the command line refuses it
    public async Task A_cell_the_command_cannot_name_is_refused_by_name()
    {
        var id = (await Create())["workspaceId"]!.GetValue<string>();

        var response = await Command("xva.vary", new JsonObject { ["workspaceId"] = id, ["cell"] = "CSA-Z threshold", ["values"] = "1mm" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var error = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal("UnknownCell", error["code"]!.GetValue<string>());
        Assert.StartsWith("No row CSA-Z: a row is a CSA (CSA-A, CSA-B", error["message"]!.GetValue<string>());
    }

    [Fact] // ADR-0010 §9, spine 1: a comma between two digits could be a separator of values or of thousands, so the list is refused, never read either way
    public async Task A_comma_between_two_digits_is_refused_never_split_as_a_guess()
    {
        var id = (await Create())["workspaceId"]!.GetValue<string>();

        var response = await Command("xva.vary", new JsonObject { ["workspaceId"] = id, ["cell"] = "CSA-A threshold", ["values"] = "10,000k, 5mm" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var error = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal(("NothingToVary", "10,000k could be one value or two: a comma separates values here, so write an amount without separators, such as 10000k or 10mm."),
            (error["code"]!.GetValue<string>(), error["message"]!.GetValue<string>()));
        Assert.Equal(["Base"], (await View(id))["cases"]!.AsArray().Select(c => c!["title"]!.GetValue<string>()));
    }

    [Fact] // ADR-0005, ADR-0010 §4: each scenario command is a tool with its contract, the fields the command line takes
    public void Each_scenario_command_is_offered_to_the_agent_with_its_contract()
    {
        var tools = factory.Services.GetRequiredService<AgentToolGateway>().Tools.ToDictionary(t => t.Name);

        string[] Fields(string tool) => [.. tools[tool].Parameters["properties"]!["request"]!["properties"]!.AsObject().Select(p => p.Key)];
        string[] Required(string tool) => [.. tools[tool].Parameters["properties"]!["request"]!["required"]!.AsArray().Select(r => r!.GetValue<string>())];
        Assert.Equal(["workspaceId", "revision", "name", "shifts"], Fields("xva_scenario"));
        Assert.Equal(["workspaceId", "name", "shifts"], Required("xva_scenario"));
        foreach (var verb in new[] { "include", "exclude", "remove" }) Assert.Equal(["workspaceId", "revision", "name"], Fields($"xva_scenario_{verb}"));
        Assert.Equal(["workspaceId", "name", "copy"], Required("xva_scenario_duplicate"));
        Assert.Equal(["workspaceId", "revision"], Fields("xva_scenarios_clear"));
        foreach (var generator in new[] { "ladder", "cross" }) Assert.Equal(["workspaceId", "factors"], Required($"xva_scenario_{generator}"));
        Assert.Equal(["workspaceId", "revision", "set", "share"], Fields("xva_scenario_save"));
        Assert.Equal(["workspaceId", "set"], Required("xva_scenario_save"));
        Assert.Equal(["workspaceId", "revision", "set"], Fields("xva_scenario_load"));
    }

    [Fact] // ADR-0010 §4, ADR-0005: a scenario the AI defines is in the log as the AI's, and a revision the AI moved is refused when stale
    public async Task A_scenario_the_ai_defines_is_logged_as_the_ais_and_a_stale_revision_is_refused()
    {
        var ws = await Create();
        var id = ws["workspaceId"]!.GetValue<string>();
        var revision = ws["revision"]!.GetValue<long>();

        var defined = await Ok(await Command("xva.scenario", new JsonObject { ["workspaceId"] = id, ["revision"] = revision, ["name"] = "up", ["shifts"] = "jpy_ois +10bp; usdjpy -5%" }));
        var excluded = await Ok(await Command("xva.scenario.exclude", new JsonObject { ["workspaceId"] = id, ["name"] = "up" }));
        var stale = await Command("xva.scenario", new JsonObject { ["workspaceId"] = id, ["revision"] = revision, ["name"] = "down", ["shifts"] = "jpy_ois -10bp" });

        Assert.Equal(("Made the scenario up.", 1), (defined["said"]!.GetValue<string>(), defined["made"]!.GetValue<int>()));
        Assert.Equal("Price leaves the scenario up out; its shifts stay.", excluded["said"]!.GetValue<string>());
        var made = factory.Services.GetRequiredService<WorkspaceService>().Find(id)!.Log.Where(e => e.Seq > revision).ToList();
        Assert.Equal(3, made.Count);
        Assert.All(made, e => Assert.Equal(RunOrigin.Ai, e.Origin));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("RevisionMoved", (await stale.Content.ReadFromJsonAsync<JsonObject>())!["code"]!.GetValue<string>());
        Assert.Equal([("up", false)], (await View(id))["scenarios"]!.AsArray().Select(s => (s!["name"]!.GetValue<string>(), s["included"]!.GetValue<bool>())));
    }

    [Fact] // ADR-0010 §4: a cross asked of the AI is one generator call, saved and loaded as the command line does, refusals by code
    public async Task A_cross_asked_of_the_ai_is_one_generator_call_and_its_set_saves_and_loads()
    {
        var id = (await Create())["workspaceId"]!.GetValue<string>();

        var crossed = await Ok(await Command("xva.scenario.cross", new JsonObject { ["workspaceId"] = id, ["factors"] = "jpy_ois -50bp, 0, +50bp x usdjpy -10%, 0, +10%" }));
        var saved = await Ok(await Command("xva.scenario.save", new JsonObject { ["workspaceId"] = id, ["set"] = "ai-cross", ["share"] = "all" }));
        var other = (await Create())["workspaceId"]!.GetValue<string>();
        var loaded = await Ok(await Command("xva.scenario.load", new JsonObject { ["workspaceId"] = other, ["set"] = "ai-cross" }));
        var refused = await Command("xva.scenario", new JsonObject { ["workspaceId"] = other, ["name"] = "up", ["shifts"] = "jpy_ois +10%" });
        var removed = await Ok(await Command("xva.scenario.remove", new JsonObject { ["workspaceId"] = other, ["name"] = "jpy_ois +50bp" }));
        var duplicated = await Ok(await Command("xva.scenario.duplicate", new JsonObject { ["workspaceId"] = other, ["name"] = "jpy_ois -50bp", ["copy"] = "down" }));

        Assert.Equal(8, crossed["made"]!.GetValue<int>());
        Assert.StartsWith("Made 8 scenario(s): jpy_ois -50bp · usdjpy -10%, ", crossed["said"]!.GetValue<string>());
        Assert.Matches(@"^Saved 8 scenario\(s\) as the set ai-cross, for every user(, replacing your ai-cross)?\.$", saved["said"]!.GetValue<string>());
        Assert.Equal(8, loaded["made"]!.GetValue<int>());
        Assert.StartsWith("Loaded 8 scenario(s) from ai-cross: ", loaded["said"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal("InvalidShift", (await refused.Content.ReadFromJsonAsync<JsonObject>())!["code"]!.GetValue<string>());
        Assert.Equal("Removed the scenario jpy_ois +50bp.", removed["said"]!.GetValue<string>());
        Assert.Equal("Duplicated jpy_ois -50bp as down.", duplicated["said"]!.GetValue<string>());
        Assert.Equal(8, (await View(other))["scenarios"]!.AsArray().Count);
        var cleared = await Ok(await Command("xva.scenarios.clear", new JsonObject { ["workspaceId"] = other }));
        Assert.Equal("Removed every scenario; Price prices the base market alone.", cleared["said"]!.GetValue<string>());
        Assert.Empty((await View(other))["scenarios"]!.AsArray());
    }
}
