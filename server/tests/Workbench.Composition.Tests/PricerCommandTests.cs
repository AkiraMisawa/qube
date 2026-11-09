using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Agent;
using Workbench.Composition.Application;
using Workbench.Composition.Application.Workspaces;
using Workbench.Composition.Contracts;

namespace Workbench.Composition.Tests;

// ADR-0005, ADR-0010 §9: the XVA page's vary, add trades and set, as the agent's tool calls reach them. Each takes what
// the command line takes, goes through the same workspace service, answers in the command line's words, and the log
// says the AI did it. Every name and figure is synthetic.
public class PricerCommandTests(WorkbenchFactory factory) : IClassFixture<WorkbenchFactory>
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
        Assert.Equal(["workspaceId", "revision", "cell", "values"], Fields("xva_vary"));
        Assert.Equal(["workspaceId", "cell", "values"], Required("xva_vary"));
        Assert.Equal(["workspaceId", "revision"], Fields("xva_trades_add"));
        Assert.Equal(["workspaceId", "revision", "rows", "column", "value"], Fields("xva_set"));
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

    [Fact] // ADR-0010 §9, docs/03 §5: the AI's add trades and set do what the command line's do; a set names the revision it was made against
    public async Task The_ais_add_trades_and_set_do_what_the_command_lines_do()
    {
        var ws = await Create();
        var id = ws["workspaceId"]!.GetValue<string>();

        var added = await Ok(await Command("xva.trades.add", new JsonObject { ["workspaceId"] = id }));
        var set = await Ok(await Command("xva.set", new JsonObject
        {
            ["workspaceId"] = id, ["revision"] = added["revision"]!.GetValue<long>(), ["rows"] = "CSA-A", ["column"] = "threshold", ["value"] = "5mm",
        }));
        var moved = await Command("xva.set", new JsonObject
        {
            ["workspaceId"] = id, ["revision"] = added["revision"]!.GetValue<long>(), ["rows"] = "CSA-A", ["column"] = "threshold", ["value"] = "6mm",
        });

        Assert.Equal("Made 3 case(s): add DRAFT-1, add DRAFT-2, add DRAFT-3.", added["said"]!.GetValue<string>());
        Assert.Equal("Set CSA-A threshold to 5mm.", set["said"]!.GetValue<string>());
        Assert.Equal(5_000_000m, (await View(id))["csas"]!.AsArray().Single(c => c!["csaId"]!.GetValue<string>() == "CSA-A")!["counterpartyThreshold"]!.GetValue<decimal>());
        Assert.Equal(HttpStatusCode.Conflict, moved.StatusCode);
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
}
