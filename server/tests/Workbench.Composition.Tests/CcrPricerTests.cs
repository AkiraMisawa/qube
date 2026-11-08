using System.Text.Json;
using System.Text.Json.Nodes;
using Workbench.Composition.Application;
using Workbench.Composition.Application.Traces;
using Workbench.Composition.Application.Workspaces;
using Workbench.Composition.Contracts;
using Workbench.Samples.Ccr;
using Workbench.Samples.Portfolio;
using Workbench.Samples.Xva;

namespace Workbench.Composition.Tests;

/// <summary>
/// ADR-0013 §1, §2, §6: spot CCR capital in the pricer. SA-CCR registers the measure, its membership, its column and its
/// validation; XVA's request prices it, its KVA option returning spot capital, its trace on each side, the capital
/// profile and KVA, all simulated. Names and figures are synthetic.
/// </summary>
public class CcrPricerTests
{
    static readonly DateOnly Day1 = new(2026, 10, 26);

    static (PortfolioWorkspaces Service, RunService Runs, IDataStore Store) Priced()
    {
        var workspaces = new WorkspaceService(new Dictionary<string, IEditRules> { [PortfolioEditRules.App] = new PortfolioEditRules() }, TimeProvider.System);
        var store = new InMemoryDataStore();
        var runs = new RunService(new CompositionRegistry([new PortfolioModule(), new XvaModule(), new CcrModule()]), store, TimeProvider.System);
        var register = new MeasureRegister([new XvaMeasures(Pv01Provider.Simulated(TimeSpan.Zero)), new CcrMeasures()]);
        return (new PortfolioWorkspaces(workspaces, store, runs, register), runs, store);
    }

    sealed record Priced_(RunRecord Run, JsonObject Input, IReadOnlyList<JsonNode> Rows, TraceSet Traces, IReadOnlyList<JsonNode> Profile);

    static async Task<Priced_> Price(RunService runs, IDataStore store, PortfolioWorkspaces service, WorkspaceState ws)
    {
        var accepted = Assert.IsType<SubmissionResult.Accepted>(service.Price(ws.WorkspaceId, ws.Revision, "akira", Guid.NewGuid().ToString()));
        await runs.Completion(accepted.Run.RunId);
        var run = runs.Find(accepted.Run.RunId)!;
        Assert.Equal(RunStatus.Succeeded, run.Status);
        var input = store.ReadRequired(((DataReferenceBinding)run.Workflow.Steps[0].Bindings["acceptedInput"]).Reference).AsObject();
        var outputs = run.Invocations.Single(i => i.StepId == XvaMeasures.Module).Outputs;
        return new(run, input, [.. store.ReadRequired(run.Outputs[XvaMeasures.Module])["rows"]!.AsArray().Select(r => r!)],
            store.ReadRequired(outputs["traces"]).Deserialize<TraceSet>(WireJson.Options)!,
            [.. store.ReadRequired(outputs["profile"])["rows"]!.AsArray().Select(r => r!)]);
    }

    static WorkspaceState Select(PortfolioWorkspaces service, WorkspaceState ws, params string[] measures) =>
        service.SetMeasures(ws.WorkspaceId, ws.Revision, "akira", measures);

    static JsonNode Row(IReadOnlyList<JsonNode> rows, string measure, string metric) =>
        rows.Single(r => r["measure"]!.GetValue<string>() == measure && r["metric"]!.GetValue<string>() == metric);

    static void Reconciles(CalculationTrace trace)
    {
        var evaluator = new TraceEvaluator(new TraceIndex(trace), SpotCapitalTrace.Catalog);
        Assert.All(trace.Terms, t => Assert.True(evaluator.Check(t).Status is CheckStatus.Matches or CheckStatus.NotApplicable, $"{t.Id}: {evaluator.Check(t).Reason}"));
        Assert.All(trace.Leaves, row => Assert.Empty(evaluator.CheckRow(row)));
    }

    [Fact] // ADR-0013 §1, §2: spot capital alone still asks XVA's request with KVA on; it returns spot capital and a trace a side
    public async Task Spot_capital_comes_from_xvas_request_with_a_trace_on_each_side()
    {
        var (service, runs, store) = Priced();
        var ws = Select(service, service.Create("akira", "ABC/LE-01", Day1, 20), "ccr");

        var priced = await Price(runs, store, service, ws);

        // `measures ccr` is spot capital; its request is XVA's, the one step of the Run, which reads SA-CCR's part too.
        Assert.Equal("ccr.spotCapital", priced.Run.Input["measures"]!.GetValue<string>());
        Assert.Equal([("xva", "xva.price")], priced.Run.Workflow.Steps.Select(s => (s.StepId, s.CapabilityId)));
        Assert.Equal(["xva", "ccr"], priced.Input["modules"]!.AsObject().Select(p => p.Key));
        // SA-CCR's threshold and MTA are the counterparty's (CRE52.18), with the disputes the collateral desk records.
        var csaB = priced.Input["modules"]!["ccr"]!["csas"]!.AsArray().Single(c => c!["csaId"]!.GetValue<string>() == "CSA-B")!;
        Assert.Equal((1_000_000m, 500_000m, "USD", 3), (csaB["threshold"]!.GetValue<decimal>(), csaB["mta"]!.GetValue<decimal>(),
            csaB["thresholdCurrency"]!.GetValue<string>(), csaB["disputes"]!.GetValue<int>()));
        // KVA is not shown, since it was not selected (ADR-0013 §2).
        Assert.Equal(["ccr.spotCapital"], priced.Rows.Select(r => r["measure"]!.GetValue<string>()).Distinct());
        foreach (var metric in new[] { "capital", "rwa", "ead" })
        {
            var row = Row(priced.Rows, "ccr.spotCapital", metric);
            Assert.Equal(("priced", "priced", "JPY"), (row["beforeStatus"]!.GetValue<string>(), row["afterStatus"]!.GetValue<string>(), row["currency"]!.GetValue<string>()));
            Assert.Equal(row["after"]!.GetValue<decimal>() - row["before"]!.GetValue<decimal>(), row["diff"]!.GetValue<decimal>());
        }

        Assert.Equal(["before", "after"], priced.Traces.Traces.Select(t => t.Side));
        foreach (var entry in priced.Traces.Traces)
        {
            Assert.Equal(("base", "base", "ccr.spotCapital"), (entry.Case, entry.Scenario, entry.Measure));
            Reconciles(entry.Trace);
            Assert.Equal((ExecutionMode.Simulated, "ABC/LE-01"), (entry.Trace.Provenance!.ExecutionMode, entry.Trace.Subject!.Id));
            var capital = Row(priced.Rows, "ccr.spotCapital", "capital")[entry.Side]!.GetValue<decimal>();
            Assert.Equal(capital, Math.Round((decimal)entry.Trace.Terms.Single(t => t.Id == "capital").Value!.GetValue<double>(), 2));
        }
    }

    [Fact] // ADR-0011 §4, ADR-0013 §1: a Price without Before has After alone, explained on that side
    public async Task A_price_without_before_explains_spot_capital_on_after_alone()
    {
        var (service, runs, store) = Priced();
        var ws = Select(service, service.Create("akira", "ABC/LE-01", Day1, 20), "ccr");
        ws = service.Edit(ws.WorkspaceId, ws.Revision, "akira", "grid:existing", "before", false);

        var priced = await Price(runs, store, service, ws);

        var row = Row(priced.Rows, "ccr.spotCapital", "capital");
        Assert.Equal(("notPriced", "priced"), (row["beforeStatus"]!.GetValue<string>(), row["afterStatus"]!.GetValue<string>()));
        Assert.Null(row["before"]);
        Assert.Null(row["diff"]);
        var entry = Assert.Single(priced.Traces.Traces);
        Assert.Equal("after", entry.Side);
        Reconciles(entry.Trace);
    }

    [Fact] // ADR-0013 §1: KVA is the cost of the capital profile, which starts at spot capital; spot capital is not shown unless selected
    public async Task KVA_comes_from_the_capital_profile()
    {
        var (service, runs, store) = Priced();
        var ws = Select(service, service.Create("akira", "ABC/LE-01", Day1, 20), "kva", "ccr.spotCapital");

        var priced = await Price(runs, store, service, ws);

        foreach (var side in new[] { "before", "after" })
        {
            var points = priced.Profile.Where(p => p["side"]!.GetValue<string>() == side).ToList();
            Assert.Equal(0, points[0]["years"]!.GetValue<double>());
            Assert.Equal(Row(priced.Rows, "ccr.spotCapital", "capital")[side]!.GetValue<decimal>(), points[0]["capital"]!.GetValue<decimal>());
            // Fewer trades are alive at each later date, never more.
            Assert.True(points.Zip(points.Skip(1)).All(p => p.First["trades"]!.GetValue<int>() >= p.Second["trades"]!.GetValue<int>()));
            var kva = Row(priced.Rows, "kva", "kva")[side]!.GetValue<decimal>();
            Assert.True(kva < 0, $"KVA {kva} is a cost.");
        }

        // Without spot capital selected, the profile and KVA are still returned, and no spot capital row or trace.
        ws = Select(service, ws, "kva");
        priced = await Price(runs, store, service, ws);
        Assert.Equal(["kva"], priced.Rows.Select(r => r["measure"]!.GetValue<string>()).Distinct());
        Assert.Empty(priced.Traces.Traces);
        Assert.NotEmpty(priced.Profile);
        Assert.Equal("priced", Row(priced.Rows, "kva", "kva")["afterStatus"]!.GetValue<string>());
    }

    [Fact] // ADR-0013 §6: every derivative is a member, and the SA-CCR column says what SA-CCR decided it is
    public void Every_trade_is_a_member_and_its_column_says_what_sa_ccr_decided()
    {
        var (service, _, _) = Priced();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 20);

        var rows = service.Page(ws.WorkspaceId, "akira", 0, 100, []).Rows;

        Assert.All(rows, t => Assert.Equal(new Membership(true, "A derivative of NS-001"), t.Membership![CcrMeasures.SpotCapital]));
        Assert.All(rows, t =>
        {
            var cell = t.Cells!["ccr.class"];
            Assert.Equal((TradeCell.Shown, false), (cell.State, cell.Editable));
            Assert.Matches(t.Product == "IRS" ? $"^Interest rate · {t.Currency} · (under 1y|1-5y|over 5y) · (long|short)$" : "^FX · JPYUSD · (long|short)$", cell.Value!.GetValue<string>());
        });
        // The source's terms the decomposition read, read-only on the trade.
        Assert.All(rows, t => Assert.NotNull(t.Start));
        Assert.All(rows.Where(t => t.IsNew), t => Assert.Equal("2026-10-26", t.Start));
        Assert.All(rows.Where(t => t.Product == "FXForward"), t => Assert.True(t.CounterCurrency is "JPY" or "USD" && t.CounterNotional > 0 && t.Direction is "buy" or "sell"));
        Assert.All(rows.Where(t => t.Product == "IRS"), t => Assert.True(t.Direction is "payFixed" or "receiveFixed" && t.CounterCurrency is null));
    }

    [Fact] // ADR-0013 §6: a CSA SA-CCR cannot read refuses spot capital and KVA by name, and CVA and FVA stand
    public async Task A_CSA_sa_ccr_cannot_read_refuses_spot_capital_and_kva_and_cva_stands()
    {
        var (service, runs, store) = Priced();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 20);
        Assert.Contains(service.Page(ws.WorkspaceId, "akira", 0, 100, []).Rows, t => t.Csa == "CSA-B");
        ws = service.Edit(ws.WorkspaceId, ws.Revision, "akira", "csa:CSA-B", "oneWay", true);

        var priced = await Price(runs, store, service, ws);

        const string why = "CSA-B is one-way, and its terms do not say which party posts, so its margined exposure cannot be decided.";
        Assert.All(priced.Rows.Where(r => r["measure"]!.GetValue<string>() == "ccr.spotCapital"),
            r => Assert.Equal(("refused", why), (r["afterStatus"]!.GetValue<string>(), r["reason"]!.GetValue<string>())));
        Assert.Equal(("refused", $"KVA reads spot CCR capital: {why}"), (Row(priced.Rows, "kva", "kva")["afterStatus"]!.GetValue<string>(), Row(priced.Rows, "kva", "kva")["reason"]!.GetValue<string>()));
        Assert.Equal("priced", Row(priced.Rows, "cva", "cva")["afterStatus"]!.GetValue<string>());
        Assert.Empty(priced.Traces.Traces);
    }

    [Fact] // ADR-0013 §3: a netting set that is not legally enforceable is one netting set per trade
    public async Task An_unenforceable_netting_set_is_one_netting_set_per_trade()
    {
        var (service, runs, store) = Priced();
        var ws = Select(service, service.Create("akira", "XYZ/LE-03", Day1, 12), "ccr");

        var priced = await Price(runs, store, service, ws);

        var after = priced.Traces.Traces.Single(t => t.Side == "after").Trace;
        var trades = priced.Input["trades"]!.AsArray().Count(t => t!["after"]!.GetValue<bool>());
        Assert.Equal(trades, after.Terms.Count(t => t.Parent == "ead"));
        // An unrated corporate that is not an SME is weighted 100% (CRE20.42).
        Assert.Equal(1.0, after.Terms.Single(t => t.Id == "rw").Value!.GetValue<double>());
    }

    [Fact] // ADR-0013 §2: spot capital is also a capability of its own, and answers as XVA's KVA option does
    public async Task The_capability_of_its_own_answers_as_the_kva_option_does()
    {
        var (service, runs, store) = Priced();
        var ws = Select(service, service.Create("akira", "ABC/LE-02", Day1, 15), "ccr");
        var priced = await Price(runs, store, service, ws);
        var reference = ((DataReferenceBinding)priced.Run.Workflow.Steps[0].Bindings["acceptedInput"]).Reference;

        var answer = await new SpotCapitalPrice().InvokeAsync(new CapabilityInvocation("own-1",
            new Dictionary<string, JsonNode?> { ["acceptedInput"] = JsonSerializer.SerializeToNode(reference, WireJson.Options) }, store), CancellationToken.None);

        var rows = answer["result"]!["rows"]!.AsArray().Select(r => r!).ToList();
        Assert.Equal(priced.Rows.Select(r => (r["metric"]!.GetValue<string>(), r["after"]!.GetValue<decimal>())),
            rows.Select(r => (r["metric"]!.GetValue<string>(), r["after"]!.GetValue<decimal>())));
        Assert.Equal(2, answer["traces"]!["traces"]!.AsArray().Count);
        // The securities firm is supervised as a bank and unrated: SCRA grade A, 40% (CRE20.21).
        Assert.Equal(0.40, priced.Traces.Traces[1].Trace.Terms.Single(t => t.Id == "rw").Value!.GetValue<double>());
    }
}
