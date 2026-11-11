using System.Text.Json.Nodes;
using Qube.Composition.Application;
using Qube.Composition.Application.Workspaces;
using Qube.Composition.Contracts;
using Qube.Samples.Ccr;
using Qube.Samples.Portfolio;
using Qube.Samples.Xva;

namespace Qube.Composition.Tests;

// The Risks tab's rows (the owner's Risks of 2026-11-08, ADR-0010 §8's note of 2026-11-09): with risks included, XVA's
// request bumps each factor on top of the pair's market and prices again, and a measure with no deltas says why. Every
// figure is the simulated engine's.
public class PricerRiskTests
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

    /// <summary>Prices the revision and reads XVA's figures and its deltas.</summary>
    static async Task<(IReadOnlyList<JsonNode> Rows, IReadOnlyList<JsonNode> Risks)> Price(RunService runs, IDataStore store, PortfolioWorkspaces service, WorkspaceState ws)
    {
        var accepted = Assert.IsType<SubmissionResult.Accepted>(service.Price(ws.WorkspaceId, ws.Revision, "akira", Guid.NewGuid().ToString()));
        await runs.Completion(accepted.Run.RunId);
        var run = runs.Find(accepted.Run.RunId)!;
        Assert.Equal(RunStatus.Succeeded, run.Status);
        var outputs = run.Invocations.Single(i => i.StepId == XvaMeasures.Module).Outputs;
        IReadOnlyList<JsonNode> Rows(TypedDataRef reference) => [.. store.ReadRequired(reference)["rows"]!.AsArray().Select(r => r!)];
        return (Rows(outputs["result"]), Rows(outputs["risks"]));
    }

    static string Text(JsonNode row, string column) => row[column]!.GetValue<string>();
    static decimal Number(JsonNode row, string column) => row[column]!.GetValue<decimal>();

    static JsonNode Figure(IReadOnlyList<JsonNode> rows, string metric, string scenario) =>
        rows.Single(r => Text(r, "metric") == metric && Text(r, "case") == PortfolioCases.BaseId && Text(r, "scenario") == scenario);

    static JsonNode Delta(IReadOnlyList<JsonNode> risks, string measure, string factor) =>
        risks.Single(r => Text(r, "measure") == measure && r["factor"]?.GetValue<string>() == factor && Text(r, "scenario") == PortfolioScenarios.BaseId
            && Text(r, "case") == PortfolioCases.BaseId);

    [Fact] // the owner's Risks of 2026-11-08: a delta is the difference of two priced figures, the bump priced as a scenario is
    public async Task A_delta_is_the_bumped_figure_minus_the_priced_one()
    {
        var (service, runs, store) = Priced();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 20);
        ws = service.SetMeasures(ws.WorkspaceId, ws.Revision, "akira", ["cva", "fva"]);
        ws = service.SetText(ws.WorkspaceId, ws.Revision, "akira", MeasureSettings.EntityOf(XvaMeasures.Module), XvaMeasures.Risks, "on");
        // The bumps themselves, priced as scenarios beside the base market.
        ws = service.DefineScenario(ws.WorkspaceId, ws.Revision, "akira", "credit", "cds 3y +1bp").State;
        ws = service.DefineScenario(ws.WorkspaceId, ws.Revision, "akira", "fx", "usdjpy +1%").State;
        ws = service.DefineScenario(ws.WorkspaceId, ws.Revision, "akira", "funding", "jpy_funding +1bp").State;

        var (rows, risks) = await Price(runs, store, service, ws);

        var credit = Delta(risks, "cva", "cds 3y");
        Assert.Equal(("Credit", "+1bp", "CVA"), (Text(credit, "riskClass"), Text(credit, "bump"), Text(credit, "metricTitle")));
        foreach (var side in new[] { "before", "after" })
        {
            Assert.Equal(Number(Figure(rows, "cva", "s1"), side) - Number(Figure(rows, "cva", PortfolioScenarios.BaseId), side), Number(credit, side));
            Assert.Equal(Number(Figure(rows, "cva", "s2"), side) - Number(Figure(rows, "cva", PortfolioScenarios.BaseId), side), Number(Delta(risks, "cva", "usdjpy"), side));
            Assert.Equal(Number(Figure(rows, "fva", "s3"), side) - Number(Figure(rows, "fva", PortfolioScenarios.BaseId), side), Number(Delta(risks, "fva", "jpy_funding"), side));
        }
        // A wider spread is a larger expected loss: CVA's credit delta is negative.
        Assert.True(Number(credit, "after") < 0);
        Assert.Equal(Number(credit, "after") - Number(credit, "before"), Number(credit, "diff"));
        // CVA's factors are the CDS tenors, the discount curves and the currencies against JPY; FVA's the curves and the currencies.
        Assert.Equal([.. CdsCatalog.Tenors.Select(t => $"cds {t}"), "jpy_ois", "usd_sofr", "eur_estr", "gbpjpy", "usdjpy", "eurjpy"],
            risks.Where(r => Text(r, "measure") == "cva" && Text(r, "scenario") == PortfolioScenarios.BaseId).Select(r => Text(r, "factor")));
        Assert.Equal(["IR", "IR", "IR", "IR", "IR", "IR", "FX", "FX", "FX"],
            risks.Where(r => Text(r, "measure") == "fva" && Text(r, "scenario") == PortfolioScenarios.BaseId).Select(r => Text(r, "riskClass")));
        // Every pair has its deltas, taken on its own market.
        Assert.Equal(4, risks.Where(r => r["factor"]?.GetValue<string>() == "cds 3y").Select(r => Text(r, "scenario")).Distinct().Count());
    }

    [Fact] // the owner's Risks of 2026-11-08, spine 1: a measure with no deltas has a row saying why, never a table of zeros
    public async Task A_measure_without_deltas_says_why()
    {
        var (service, runs, store) = Priced();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 20);
        ws = service.SetMeasures(ws.WorkspaceId, ws.Revision, "akira", ["cva", "fva", "kva"]);

        var (_, off) = await Price(runs, store, service, ws);

        // KVA's deltas are out of scope: it registers no Risks tab and has no rows.
        Assert.Equal(["cva", "fva"], off.Select(r => Text(r, "measure")));
        Assert.All(off, r => Assert.Equal((null, null, "Risks were not asked: turn Risks included on in Settings, or set xva risks on, and Price again."),
            (r["factor"], r["after"], Text(r, "reason"))));

        ws = service.SetText(ws.WorkspaceId, ws.Revision, "akira", MeasureSettings.EntityOf(XvaMeasures.Module), XvaMeasures.Risks, "on");
        ws = service.ChooseCds(ws.WorkspaceId, ws.Revision, "akira", "none").State;
        var (_, refused) = await Price(runs, store, service, ws);

        var cva = Assert.Single(refused, r => Text(r, "measure") == "cva");
        Assert.Equal("CVA is refused, so it has no deltas: No CDS for ABC/LE-01: choose a single name or a proxy on the Credit tab, or with cds.", Text(cva, "reason"));
        Assert.Contains(refused, r => Text(r, "measure") == "fva" && r["factor"]?.GetValue<string>() == "jpy_ois");
    }
}
