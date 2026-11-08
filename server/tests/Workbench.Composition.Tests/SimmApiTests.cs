using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Composition.Application;
using Workbench.Composition.Application.Traces;
using Workbench.Composition.Contracts;
using Workbench.Samples.Simm;

namespace Workbench.Composition.Tests;

// Spot SIMM through the workbench (ADR-0012 §4): risk.crif.generate and margin.simm.calculate, each run alone as a
// workflow over the HTTP API, with their contracts, their refusals by name, their labels, and the Run kept.
public class SimmApiTests(WorkbenchFactory factory) : IClassFixture<WorkbenchFactory>
{
    readonly HttpClient client = factory.CreateClient();

    // The synthetic market of the development settings, pinned by its identifier: snapshot 0 of scenario demo-1.
    const string Market = "synthetic:demo-1:20261027:0";

    async Task<JsonObject> Ok(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!;
    }

    async Task<JsonObject> CatalogWorkflow(string id)
    {
        var catalog = (await client.GetFromJsonAsync<JsonObject>("/api/catalog"))!;
        return catalog["workflows"]!.AsArray().Single(w => w!["definitionId"]!.GetValue<string>() == id)!.DeepClone().AsObject();
    }

    async Task<HttpResponseMessage> Submit(JsonNode workflow, JsonObject input) => await client.PostAsJsonAsync("/api/runs", new JsonObject
    {
        ["workflow"] = workflow, ["context"] = new JsonObject(), ["input"] = input, ["idempotencyKey"] = Guid.NewGuid().ToString(),
    });

    async Task<JsonObject> Completed(HttpResponseMessage submitted)
    {
        Assert.Equal(HttpStatusCode.Accepted, submitted.StatusCode);
        var runId = (await submitted.Content.ReadFromJsonAsync<JsonObject>())!["runId"]!.GetValue<string>();
        JsonObject run;
        do run = (await client.GetFromJsonAsync<JsonObject>($"/api/runs/{runId}"))!;
        while (run["status"]!.GetValue<string>() == "running");
        return run;
    }

    async Task<JsonObject> Read(JsonNode reference) => await Ok(await client.PostAsJsonAsync("/api/data/read", reference));

    async Task<JsonObject> CrifRun() => await Completed(await Submit(await CatalogWorkflow(SimmModule.CrifWorkflow),
        new JsonObject { ["registration"] = "ABC/LE-01", ["valueDate"] = "2026-11-06", ["marketSnapshotId"] = Market }));

    static JsonObject SimmInput(string parameters = "DEMO-2.x") => new() { ["registration"] = "ABC/LE-01", ["parameters"] = parameters };

    static JsonNode Workflow(WorkflowDefinition workflow) => JsonSerializer.SerializeToNode(workflow, WireJson.Options)!;

    static TypedDataRef Reference(JsonNode node) => node.Deserialize<TypedDataRef>(WireJson.Options)!;

    [Fact] // ADR-0012 §4, §5: CRIF runs alone from the source's frozen trades and a pinned market; the exempt forward is out by name
    public async Task The_crif_workflow_runs_alone_from_the_trades_and_a_pinned_market()
    {
        var run = await CrifRun();

        Assert.Equal("succeeded", run["status"]!.GetValue<string>());
        Assert.Equal(Market, run["input"]!["marketSnapshotId"]!.GetValue<string>());
        var crif = await Read(run["outputs"]!["crif"]!);
        var rows = crif["rows"]!.AsArray().Select(r => r!.AsObject()).ToList();
        Assert.Equal(("simulated", "synthetic"), (crif["provenance"]!["executionMode"]!.GetValue<string>(), crif["provenance"]!["dataOrigin"]!.GetValue<string>()));
        Assert.Contains(Market, crif["title"]!.GetValue<string>());
        Assert.Contains(rows, r => (r["tradeId"]!.GetValue<string>(), r["riskType"]!.GetValue<string>(), r["qualifier"]!.GetValue<string>(), r["label2"]?.GetValue<string>())
            == ("T-1001", "Risk_IRCurve", "JPY", "OIS"));
        Assert.Contains(rows, r => (r["tradeId"]!.GetValue<string>(), r["riskType"]!.GetValue<string>(), r["qualifier"]!.GetValue<string>()) == ("T-1004", "Risk_XCcyBasis", "JPY"));
        Assert.Contains(rows, r => (r["tradeId"]!.GetValue<string>(), r["riskType"]!.GetValue<string>(), r["qualifier"]!.GetValue<string>()) == ("T-1004", "Risk_FX", "JPY"));
        var t1001 = rows.First(r => r["tradeId"]!.GetValue<string>() == "T-1001");
        Assert.Equal(("JFSA,CFTC", "JFSA"), (t1001["collectRegulations"]!.GetValue<string>(), t1001["postRegulations"]!.GetValue<string>()));
        Assert.All(rows.Where(r => r["amountCurrency"]!.GetValue<string>() == "JPY"),
            r => Assert.Equal(r["amount"]!.GetValue<double>() / 150, r["amountUsd"]!.GetValue<double>(), 6));
        var trades = (await Read(run["outputs"]!["trades"]!))["rows"]!.AsArray().ToDictionary(r => r!["tradeId"]!.GetValue<string>(), r => r!.AsObject());
        Assert.Equal(["T-1001", "T-1002", "T-1003", "T-1004", "T-1005"], trades.Keys.Order());
        Assert.Equal((0, "Physically settled FX forward: exempt from IM in every regime."),
            (trades["T-1005"]["lines"]!.GetValue<int>(), trades["T-1005"]["excluded"]!.GetValue<string>()));
        Assert.Null(trades["T-1001"]["excluded"]);
    }

    [Fact] // ADR-0012 §1, §4, §5: SIMM runs alone on a retained CRIF; each direction's IM is its worst regime, labelled DEMO, and the Run is kept
    public async Task Simm_runs_alone_on_a_retained_crif()
    {
        var crifRun = await CrifRun();
        var crifRef = Reference(crifRun["outputs"]!["crif"]!);

        var run = await Completed(await Submit(Workflow(SimmModule.Calculate(crifRef)), SimmInput()));

        Assert.True(run["status"]!.GetValue<string>() == "succeeded", run["error"]?.ToJsonString());
        Assert.Equal("DEMO-2.x", run["input"]!["parameters"]!.GetValue<string>());
        var im = await Read(run["outputs"]!["im"]!);
        var regimes = await Read(run["outputs"]!["regimes"]!);
        var contributions = await Read(run["outputs"]!["contributions"]!);
        foreach (var dataset in new[] { im, regimes, contributions })
        {
            Assert.Contains("SIMM PARAMETERS DEMO", dataset["title"]!.GetValue<string>());
            Assert.Equal("simulated", dataset["provenance"]!["executionMode"]!.GetValue<string>());
        }

        // The figures are the engine's on the CRIF as retained: the dataset carries every line as it was written.
        var crif = SimmDatasets.ReadCrif(await Read(crifRun["outputs"]!["crif"]!));
        var expected = SimmEngine.Calculate(crif, SyntheticImSource.Find("ABC/LE-01")!.Csas, SimmParameters.Demo);
        var regimeRows = regimes["rows"]!.AsArray().Select(r => r!.AsObject()).ToList();
        foreach (var direction in expected.Directions)
            foreach (var regime in direction.Regimes)
                Assert.Equal(regime.Total.Value, regimeRows.Single(r => r["direction"]!.GetValue<string>() == direction.Direction.ToString().ToLowerInvariant()
                    && r["regime"]!.GetValue<string>() == regime.Regime)["simm"]!.GetValue<double>(), 6);

        foreach (var row in im["rows"]!.AsArray().Select(r => r!.AsObject()))
        {
            var direction = row["direction"]!.GetValue<string>();
            var own = regimeRows.Where(r => r["direction"]!.GetValue<string>() == direction).ToList();
            var worst = own.MaxBy(r => r["simm"]!.GetValue<double>())!;
            Assert.Equal((worst["regime"]!.GetValue<string>(), worst["simm"]!.GetValue<double>()),
                (row["governingRegime"]!.GetValue<string>(), row["im"]!.GetValue<double>()));
            Assert.Equal([worst["regime"]!.GetValue<string>()], own.Where(r => r["governing"]!.GetValue<bool>()).Select(r => r["regime"]!.GetValue<string>()));
        }
        Assert.Equal(["collect", "post"], im["rows"]!.AsArray().Select(r => r!["direction"]!.GetValue<string>()).Order());

        // ADR-0012 §6: a regime's lines' contributions add up to its SIMM.
        foreach (var regime in regimeRows)
            Assert.Equal(regime["simm"]!.GetValue<double>(), contributions["rows"]!.AsArray()
                .Where(c => c!["direction"]!.GetValue<string>() == regime["direction"]!.GetValue<string>() && c["regime"]!.GetValue<string>() == regime["regime"]!.GetValue<string>())
                .Sum(c => c!["contribution"]!.GetValue<double>()), 6);

        // ADR-0011 §3, ADR-0012 §6: the Run keeps the engine's trace, labelled as its figures, and every term reconciles.
        var trace = (await Read(run["outputs"]!["trace"]!)).Deserialize<CalculationTrace>(WireJson.Options)!;
        Assert.Equal((ExecutionMode.Simulated, "ABC/LE-01"), (trace.Provenance!.ExecutionMode, trace.Subject!.Id));
        Assert.Equal(["collect", "post"], trace.Roots);
        var catalog = factory.Services.GetRequiredService<CompositionRegistry>().FindTraceCatalog(SimmTraceCatalog.Method, "DEMO-2.x")!;
        var evaluator = new TraceEvaluator(new TraceIndex(trace), catalog);
        Assert.All(trace.Terms.Where(t => t.State == TermState.Value), t => Assert.Equal(CheckStatus.Matches, evaluator.Check(t).Status));

        var history = await Ok(await client.PostAsJsonAsync("/api/history/rows", new { domain = (string?)null, start = 0, count = 2000 }));
        Assert.Contains(history["rows"]!.AsArray(), r => r!["runId"]!.GetValue<string>() == run["runId"]!.GetValue<string>());
    }

    [Fact] // ADR-0012 §5, spine 1: a parameter table that is not loaded is refused by name before any Run
    public async Task A_parameter_table_that_is_not_loaded_is_refused_by_name()
    {
        var crifRef = Reference((await CrifRun())["outputs"]!["crif"]!);

        var response = await Submit(Workflow(SimmModule.Calculate(crifRef)), SimmInput("ISDA-2.6"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var error = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Contains(error["issues"]!.AsArray(), i => i!["path"]!.GetValue<string>() == "/input/parameters");
    }

    [Fact] // ADR-0012 §2, spine 1: a CRIF line naming a regime the source does not know fails the Run by name, never drops out of scope
    public async Task A_crif_line_naming_an_unknown_regime_is_refused_by_name()
    {
        var store = factory.Services.GetRequiredService<IDataStore>();
        var line = new CrifLine("X-01", "T-1001", CrifRiskType.IrCurve, "JPY", "5y", "OIS", 15_000, "JPY", 100, ["JSFA"], ["JFSA"]);
        var crifRef = store.Put($"crif-{Guid.NewGuid():N}", SimmContracts.Crif, SimmContracts.Version,
            SimmDatasets.Crif([line], "CRIF (loaded)", new(ExecutionMode.Simulated, DataOrigin.Synthetic, "test")));

        var run = await Completed(await Submit(Workflow(SimmModule.Calculate(crifRef)), SimmInput()));

        var step = run["invocations"]!.AsArray().Single(i => i!["stepId"]!.GetValue<string>() == "simm")!;
        Assert.Equal(("failed", "UnknownRegime"), (run["status"]!.GetValue<string>(), step["error"]!["code"]!.GetValue<string>()));
        Assert.Contains("X-01", step["error"]!["message"]!.GetValue<string>());
        Assert.Contains("JSFA", step["error"]!["message"]!.GetValue<string>());
    }
}
