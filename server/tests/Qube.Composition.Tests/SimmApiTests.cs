using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Qube.Composition.Application;
using Qube.Composition.Application.Traces;
using Qube.Composition.Contracts;
using Qube.Samples.Rates;
using Qube.Samples.Simm;

namespace Qube.Composition.Tests;

// Spot SIMM through the workbench (ADR-0012 §4): the IM source frozen with the CRIF it gives, then margin.simm.calculate,
// each run alone as a workflow over the HTTP API, with their contracts, their refusals by name, their labels, and the
// Run kept with the inputs it resolves.
public class SimmApiTests(QubeFactory factory) : IClassFixture<QubeFactory>
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

    static JsonObject SimmInput(string parameters = "DEMO-2.x") => new() { ["parameters"] = parameters };

    static JsonNode Workflow(WorkflowDefinition workflow) => JsonSerializer.SerializeToNode(workflow, WireJson.Options)!;

    static TypedDataRef Reference(JsonNode node) => node.Deserialize<TypedDataRef>(WireJson.Options)!;

    static string Text(JsonNode? node) => node!.GetValue<string>();

    [Fact] // ADR-0012 §4, §5: CRIF runs alone from the source's frozen trades and a pinned market; the exempt forward is out by name
    public async Task The_crif_workflow_runs_alone_from_the_trades_and_a_pinned_market()
    {
        var run = await CrifRun();

        Assert.Equal("succeeded", Text(run["status"]));
        Assert.Equal(Market, Text(run["input"]!["marketSnapshotId"]));
        var crif = await Read(run["outputs"]!["crif"]!);
        var rows = crif["rows"]!.AsArray().Select(r => r!.AsObject()).ToList();
        Assert.Equal(("simulated", "synthetic"), (Text(crif["provenance"]!["executionMode"]), Text(crif["provenance"]!["dataOrigin"])));
        Assert.Contains(Market, Text(crif["title"]));
        Assert.Equal(("ABC/LE-01", "2026-11-06"), (Text(crif["metadata"]!["value"]!["registration"]), Text(crif["metadata"]!["value"]!["valueDate"])));
        Assert.Contains(rows, r => (Text(r["tradeId"]), Text(r["riskType"]), Text(r["qualifier"]), r["label2"]?.GetValue<string>()) == ("T-1001", "Risk_IRCurve", "JPY", "OIS"));
        Assert.Contains(rows, r => (Text(r["tradeId"]), Text(r["riskType"]), Text(r["qualifier"])) == ("T-1004", "Risk_XCcyBasis", "JPY"));
        Assert.Contains(rows, r => (Text(r["tradeId"]), Text(r["riskType"]), Text(r["qualifier"])) == ("T-1004", "Risk_FX", "JPY"));
        var t1001 = rows.First(r => Text(r["tradeId"]) == "T-1001");
        Assert.Equal(("JFSA,CFTC", "JFSA"), (Text(t1001["collectRegulations"]), Text(t1001["postRegulations"])));
        Assert.All(rows.Where(r => Text(r["amountCurrency"]) == "JPY"), r => Assert.Equal(r["amount"]!.GetValue<double>() / 150, r["amountUsd"]!.GetValue<double>(), 6));
        var coverage = (await Read(run["outputs"]!["coverage"]!))["rows"]!.AsArray().ToDictionary(r => Text(r!["tradeId"]), r => r!.AsObject());
        Assert.Equal(["T-1001", "T-1002", "T-1003", "T-1004", "T-1005"], coverage.Keys.Order());
        Assert.Equal((0, "Physically settled FX forward: exempt from IM in every regime."),
            (coverage["T-1005"]["lines"]!.GetValue<int>(), Text(coverage["T-1005"]["excluded"])));
        Assert.Null(coverage["T-1001"]["excluded"]);
    }

    [Fact] // CLAUDE.md, ADR-0012 §4: the CRIF's Run keeps the trades and IM CSAs it was frozen from, as the source held them
    public async Task The_crif_run_keeps_the_trades_and_im_csas_it_froze()
    {
        var run = await CrifRun();

        Assert.Equal(["freeze", "crif"], run["invocations"]!.AsArray().Select(i => Text(i!["stepId"])));
        var trades = (await Read(run["outputs"]!["trades"]!)).Deserialize<FrozenTrades>(RatesAcceptedInput.Json)!;
        Assert.Equal(("ABC/LE-01", SyntheticImSource.SourceVersion, DataOrigin.Synthetic, new DateOnly(2026, 11, 6)),
            (trades.Registration, trades.SourceVersion, trades.Origin, trades.ValueDate));
        Assert.Empty(trades.NotFrozen);
        Assert.Equal(["T-1001", "T-1002", "T-1003", "T-1004"], trades.Trades.Where(t => t.Pricing is not null).Select(t => t.Id));
        var forward = trades.Trades.Single(t => t.Id == "T-1005");
        Assert.Null(forward.Pricing);
        Assert.Equal("Physically settled FX forward: exempt from IM in every regime.", forward.Exemption);
        // The CCS's terms are frozen as resolved: its legs, their periods and its principal exchanges.
        var ccs = trades.Trades.Single(t => t.Id == "T-1004").Pricing!;
        Assert.Equal(["JPY", "USD"], ccs.Legs.Select(l => l.Terms.Currency));
        Assert.All(ccs.Legs, l => Assert.NotEmpty(l.Periods));
        Assert.All(ccs.Legs, l => Assert.NotEmpty(l.Exchanges));

        var csas = (await Read(run["outputs"]!["csas"]!)).Deserialize<FrozenCsas>(RatesAcceptedInput.Json)!;
        Assert.Equal("ABC/LE-01", csas.Registration);
        Assert.Equal(["CFTC", "EMIR", "JFSA"], csas.Regimes);
        Assert.Equal([("IMR-ABC-01", ImDirection.Collect), ("IMP-ABC-01", ImDirection.Post)], csas.Csas.Select(c => (c.Id, c.Direction)));
    }

    async Task<(JsonObject CrifRun, TypedDataRef Crif, TypedDataRef Csas)> Retained()
    {
        var crifRun = await CrifRun();
        return (crifRun, Reference(crifRun["outputs"]!["crif"]!), Reference(crifRun["outputs"]!["csas"]!));
    }

    [Fact] // ADR-0012 §1, §4, §5: SIMM runs alone on a retained CRIF and IM CSAs; each direction's IM is its worst regime, labelled DEMO, and the Run is kept
    public async Task Simm_runs_alone_on_a_retained_crif()
    {
        var (crifRun, crifRef, csasRef) = await Retained();

        var run = await Completed(await Submit(Workflow(SimmModule.Calculate(crifRef, csasRef)), SimmInput()));

        Assert.True(Text(run["status"]) == "succeeded", run["error"]?.ToJsonString());
        Assert.Equal("DEMO-2.x", Text(run["input"]!["parameters"]));
        var im = await Read(run["outputs"]!["im"]!);
        var regimes = await Read(run["outputs"]!["regimes"]!);
        foreach (var dataset in new[] { im, regimes })
        {
            Assert.Contains("SIMM PARAMETERS DEMO", Text(dataset["title"]));
            Assert.Equal("simulated", Text(dataset["provenance"]!["executionMode"]));
        }

        // The Run resolves its inputs: the CRIF and the IM CSAs it was computed from are the ones its workflow names.
        var bindings = run["workflow"]!["steps"]![0]!["bindings"]!;
        Assert.Equal((crifRef, csasRef), (Reference(bindings["crif"]!["reference"]!), Reference(bindings["csas"]!["reference"]!)));
        var crif = SimmDatasets.ReadCrif(await Read(bindings["crif"]!["reference"]!));
        var csas = (await Read(bindings["csas"]!["reference"]!)).Deserialize<FrozenCsas>(RatesAcceptedInput.Json)!;

        // The figures are the engine's on the CRIF and IM CSAs as retained.
        var expected = SimmEngine.Calculate(crif.Lines, csas.Csas, SimmParameters.Demo);
        var regimeRows = regimes["rows"]!.AsArray().Select(r => r!.AsObject()).ToList();
        foreach (var direction in expected.Directions)
            foreach (var regime in direction.Regimes)
                Assert.Equal(regime.Total.Value, regimeRows.Single(r => Text(r["direction"]) == SimmDatasets.Name(direction.Direction)
                    && Text(r["regime"]) == regime.Regime)["simm"]!.GetValue<double>(), 6);

        foreach (var row in im["rows"]!.AsArray().Select(r => r!.AsObject()))
        {
            var own = regimeRows.Where(r => Text(r["direction"]) == Text(row["direction"])).ToList();
            var worst = own.MaxBy(r => r["simm"]!.GetValue<double>())!;
            Assert.Equal((Text(worst["regime"]), worst["simm"]!.GetValue<double>()), (Text(row["governingRegime"]), row["im"]!.GetValue<double>()));
            Assert.Equal([Text(worst["regime"])], own.Where(r => r["governing"]!.GetValue<bool>()).Select(r => Text(r["regime"])));
        }
        Assert.Equal(["collect", "post"], im["rows"]!.AsArray().Select(r => Text(r!["direction"])).Order());

        // ADR-0011 §1, §3, ADR-0012 §6: the Run keeps the engine's trace, of its request and value date, labelled as its figures, and every term reconciles.
        var trace = (await Read(run["outputs"]!["trace"]!)).Deserialize<CalculationTrace>(WireJson.Options)!;
        Assert.Equal((ExecutionMode.Simulated, "ABC/LE-01", "2026-11-06"), (trace.Provenance!.ExecutionMode, trace.Subject!.Id, trace.ValuationDate));
        Assert.StartsWith(Text(run["runId"]), trace.RequestId);
        Assert.Equal(["collect", "post"], trace.Roots);
        var catalog = factory.Services.GetRequiredService<CompositionRegistry>().FindTraceCatalog(SimmTraceCatalog.Method, "DEMO-2.x")!;
        var evaluator = new TraceEvaluator(new TraceIndex(trace), catalog);
        Assert.All(trace.Terms.Where(t => t.State == TermState.Value), t => Assert.Equal(CheckStatus.Matches, evaluator.Check(t).Status));

        // ADR-0012 §6: a regime's CRIF lines' contributions, the trace's leaf rows, add up to its SIMM.
        foreach (var regime in regimeRows)
            Assert.Equal(regime["simm"]!.GetValue<double>(), trace.Leaves
                .Where(l => Text(l["parent"]).StartsWith($"{Text(regime["direction"])}/{Text(regime["regime"])}/", StringComparison.Ordinal))
                .Sum(l => l["contribution"]!.GetValue<double>()), 6);

        var history = await Ok(await client.PostAsJsonAsync("/api/history/rows", new { domain = (string?)null, start = 0, count = 2000 }));
        Assert.Contains(history["rows"]!.AsArray(), r => Text(r!["runId"]) == Text(run["runId"]));
        Assert.Contains(history["rows"]!.AsArray(), r => Text(r!["runId"]) == Text(crifRun["runId"]));
    }

    [Fact] // ADR-0011 §5, ADR-0012 §6: SIMM's Run is read a term at a time, on After of the base case, down to its CRIF lines
    public async Task Simms_trace_is_read_a_term_at_a_time_down_to_the_crif_lines()
    {
        var (_, crifRef, csasRef) = await Retained();
        var run = await Completed(await Submit(Workflow(SimmModule.Calculate(crifRef, csasRef)), SimmInput()));
        var runId = Text(run["runId"]);
        var collectIm = (await Read(run["outputs"]!["im"]!))["rows"]!.AsArray().Single(r => Text(r!["direction"]) == "collect")!["im"]!.GetValue<double>();
        string Url(string tail, string? path) => $"/api/runs/{runId}/cases/base/traces/{SimmTraceCatalog.Measure}{tail}{(path is null ? "" : "?path=" + Uri.EscapeDataString(path))}";

        // A Run that priced one side alone is read on After, with no Before and no Diff (ADR-0011 §4).
        var root = await Ok(await client.GetAsync(Url("", null)));
        Assert.Equal(["after"], root["trace"]!["sides"]!.AsArray().Select(Text));
        var collect = root["term"]!["term"]!;
        Assert.Equal(("collect", collectIm, "matches"),
            (Text(collect["id"]), collect["after"]!["value"]!.GetValue<double>(), Text(collect["after"]!["check"]!["status"])));
        Assert.Null(collect["before"]);

        // Down the first child at every level, to a risk factor, whose leaf rows are its CRIF lines with their contributions.
        var at = "collect";
        while (true)
        {
            var open = (await Ok(await client.GetAsync(Url("", at))))["term"]!;
            Assert.Equal("matches", Text(open["term"]!["after"]!["check"]!["status"]));
            if (open["term"]!["after"]!["leaves"] is not null) break;
            at = Text(open["children"]!.AsArray().First()!["id"]);
        }
        var leaves = (await Ok(await client.GetAsync(Url("/leaves", at))))["leaves"]!["rows"]!.AsArray().Select(r => r!["after"]!).ToList();
        Assert.NotEmpty(leaves);
        Assert.All(leaves, r => Assert.Contains(Text(r["tradeId"]), new[] { "T-1001", "T-1002", "T-1003", "T-1004" }));
        Assert.All(leaves, r => Assert.NotNull(r["contribution"]));
    }

    [Fact] // ADR-0012 §5, spine 1: a parameter table that is not loaded is refused by name before any Run
    public async Task A_parameter_table_that_is_not_loaded_is_refused_by_name()
    {
        var (_, crifRef, csasRef) = await Retained();

        var response = await Submit(Workflow(SimmModule.Calculate(crifRef, csasRef)), SimmInput("ISDA-2.6"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var error = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Contains(error["issues"]!.AsArray(), i => Text(i!["path"]) == "/input/parameters");
    }

    TypedDataRef Loaded(string registration, params CrifLine[] lines) =>
        factory.Services.GetRequiredService<IDataStore>().Put($"crif-{Guid.NewGuid():N}", SimmContracts.Crif, SimmContracts.Version,
            SimmDatasets.Crif(new(registration, new(2026, 11, 6), lines), "CRIF (loaded)", new(ExecutionMode.Simulated, DataOrigin.Synthetic, "test")));

    async Task<(string Status, JsonNode Error)> Failed(TypedDataRef crif, TypedDataRef csas)
    {
        var run = await Completed(await Submit(Workflow(SimmModule.Calculate(crif, csas)), SimmInput()));
        var step = run["invocations"]!.AsArray().Single(i => Text(i!["stepId"]) == "simm")!;
        return (Text(run["status"]), step["error"]!);
    }

    [Fact] // ADR-0012 §2, spine 1: a CRIF line naming a regime the source does not know fails the Run by name, never drops out of scope
    public async Task A_crif_line_naming_an_unknown_regime_is_refused_by_name()
    {
        var (_, _, csasRef) = await Retained();
        var crif = Loaded("ABC/LE-01", new CrifLine("X-01", "T-1001", CrifRiskType.IrCurve, "JPY", "5y", "OIS", 15_000, "JPY", 100, ["JSFA"], ["JFSA"]));

        var (status, error) = await Failed(crif, csasRef);

        Assert.Equal(("failed", "UnknownRegime"), (status, Text(error["code"])));
        Assert.Contains("X-01", Text(error["message"]));
        Assert.Contains("JSFA", Text(error["message"]));
    }

    [Fact] // docs/03, spine 1: a CRIF of another registration is refused by name, never computed to no IM under these IM CSAs
    public async Task A_crif_of_another_registration_is_refused_by_name()
    {
        var (_, _, csasRef) = await Retained();
        var crif = Loaded("ABC/LE-02", new CrifLine("X-01", "T-1001", CrifRiskType.IrCurve, "JPY", "5y", "OIS", 15_000, "JPY", 100, ["JFSA"], ["JFSA"]));

        var (status, error) = await Failed(crif, csasRef);

        Assert.Equal(("failed", "RegistrationMismatch"), (status, Text(error["code"])));
        Assert.Contains("ABC/LE-02", Text(error["message"]));
        Assert.Contains("ABC/LE-01", Text(error["message"]));
    }
}
