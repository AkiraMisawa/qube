using System.Text.Json.Nodes;
using Workbench.Composition.Application;
using Workbench.Composition.Application.Workspaces;
using Workbench.Composition.Contracts;
using Workbench.Samples.Ccr;
using Workbench.Samples.Portfolio;
using Workbench.Samples.Xva;

namespace Workbench.Composition.Tests;

// The Profile tab's rows (the owner's Profile of 2026-11-08, portfolio.profiles): the exposure profile CVA and FVA
// integrate and the capital profile KVA charges for, per case, scenario and side. Every figure is the simulated engine's.
public class PricerProfileTests
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

    static async Task<(JsonObject Input, IReadOnlyList<JsonNode> Profiles)> Price(RunService runs, IDataStore store, PortfolioWorkspaces service, WorkspaceState ws)
    {
        var accepted = Assert.IsType<SubmissionResult.Accepted>(service.Price(ws.WorkspaceId, ws.Revision, "akira", Guid.NewGuid().ToString()));
        await runs.Completion(accepted.Run.RunId);
        var run = runs.Find(accepted.Run.RunId)!;
        Assert.Equal(RunStatus.Succeeded, run.Status);
        var input = PortfolioInput.Cases(store.ReadRequired(((DataReferenceBinding)run.Workflow.Steps[0].Bindings["acceptedInput"]).Reference))[0].Input;
        var outputs = run.Invocations.Single(i => i.StepId == XvaMeasures.Module).Outputs;
        return (input, [.. store.ReadRequired(outputs["profiles"])["rows"]!.AsArray().Select(r => r!)]);
    }

    static string Text(JsonNode row, string column) => row[column]!.GetValue<string>();

    static IReadOnlyList<JsonNode> Series(IReadOnlyList<JsonNode> rows, string side, string series, string scenario = PortfolioScenarios.BaseId) =>
        [.. rows.Where(r => Text(r, "side") == side && Text(r, "series") == series && Text(r, "scenario") == scenario && Text(r, "case") == PortfolioCases.BaseId)];

    [Fact] // the owner's Profile of 2026-11-08: EE at the value date is the sum of the positive PVs held, and EE and ENE run off to 0 at the last maturity
    public async Task The_exposure_starts_at_the_positive_pvs_and_runs_off()
    {
        var (service, runs, store) = Priced();
        // XYZ Industries has no CSA, so every trade's exposure is its whole value (the toy's collateral factor is 1).
        var ws = service.Create("akira", "XYZ/LE-03", Day1, 20);
        ws = service.SetMeasures(ws.WorkspaceId, ws.Revision, "akira", ["cva"]);

        var (input, profiles) = await Price(runs, store, service, ws);

        var market = new ScenarioMarket([], Day1);
        var currency = input["displayCurrency"]!.GetValue<string>();
        foreach (var side in new[] { "before", "after" })
        {
            var held = input["trades"]!.AsArray().Where(t => t![side]!.GetValue<bool>())
                .Sum(t => Math.Max(0m, market.ToGbp(t!["isNew"]!.GetValue<bool>() ? 0m : t["officialPv"]!.GetValue<decimal>(), t["currency"]!.GetValue<string>()) * market.UnitsPerGbp("JPY")));
            var ee = Series(profiles, side, "ee");
            var ene = Series(profiles, side, "ene");
            Assert.Equal(0d, ee[0]["years"]!.GetValue<double>());
            Assert.Equal("2026-10-26", Text(ee[0], "date"));
            Assert.Equal(Math.Round(market.ToGbp(held, "JPY") * market.UnitsPerGbp(currency), 2), ee[0]["value"]!.GetValue<decimal>());
            Assert.Equal((0m, 0m), (ee[^1]["value"]!.GetValue<decimal>(), ene[^1]["value"]!.GetValue<decimal>()));
            Assert.All(ene, p => Assert.True(p["value"]!.GetValue<decimal>() <= 0));
            Assert.All(ee, p => Assert.Equal(("exposure", "Exposure", currency), (Text(p, "profile"), Text(p, "profileTitle"), Text(p, "currency"))));
        }
        // Without KVA there is no capital profile: the tab says which profiles the Run returned.
        Assert.Equal(["exposure"], profiles.Select(p => Text(p, "profile")).Distinct());
    }

    [Fact] // the owner's Profile of 2026-11-08, ADR-0013 §1: with KVA the capital profile, EAD and capital, for every case and market
    public async Task The_capital_profile_comes_with_kva_for_every_case_and_market()
    {
        var (service, runs, store) = Priced();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 20);
        ws = service.SetMeasures(ws.WorkspaceId, ws.Revision, "akira", ["kva"]);
        ws = service.Vary(ws.WorkspaceId, ws.Revision, "akira", "CSA-A threshold", ["50mm"]).State;
        ws = service.DefineScenario(ws.WorkspaceId, ws.Revision, "akira", "up", "usdjpy +10%").State;

        var (_, profiles) = await Price(runs, store, service, ws);

        Assert.Equal(["capital"], profiles.Select(p => Text(p, "profile")).Distinct());
        Assert.Equal(["ead", "capital"], profiles.Select(p => Text(p, "series")).Distinct());
        Assert.Equal(4, profiles.Select(p => (Text(p, "case"), Text(p, "scenario"))).Distinct().Count());
        var capital = Series(profiles, "after", "capital");
        Assert.True(capital.Zip(capital.Skip(1)).All(p => p.First["trades"]!.GetValue<int>() >= p.Second["trades"]!.GetValue<int>()));
        Assert.NotEqual(capital[0]["value"]!.GetValue<decimal>(), Series(profiles, "after", "capital", "s1")[0]["value"]!.GetValue<decimal>());
    }
}
