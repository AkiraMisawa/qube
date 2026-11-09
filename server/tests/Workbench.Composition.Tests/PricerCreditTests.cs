using System.Text.Json.Nodes;
using Workbench.Composition.Application;
using Workbench.Composition.Application.Workspaces;
using Workbench.Composition.Contracts;
using Workbench.Samples.Ccr;
using Workbench.Samples.Portfolio;
using Workbench.Samples.Xva;

namespace Workbench.Composition.Tests;

// The counterparty's CDS on the portfolio workspace service (ADR-0010 §8 and its note of 2026-11-09), with XVA's and
// SA-CCR's registrations: the curve chosen, edited and priced, and CVA refused without one. Every curve is synthetic.
public class PricerCreditTests
{
    static readonly DateOnly Day1 = new(2026, 10, 26);
    const string AbchUsd = "ABCH.SNRFOR.USD.XR14";

    static (PortfolioWorkspaces Service, RunService Runs, IDataStore Store) Priced()
    {
        var workspaces = new WorkspaceService(new Dictionary<string, IEditRules> { [PortfolioEditRules.App] = new PortfolioEditRules() }, TimeProvider.System);
        var store = new InMemoryDataStore();
        var runs = new RunService(new CompositionRegistry([new PortfolioModule(), new XvaModule(), new CcrModule()]), store, TimeProvider.System);
        var register = new MeasureRegister([new XvaMeasures(Pv01Provider.Simulated(TimeSpan.Zero)), new CcrMeasures()]);
        return (new PortfolioWorkspaces(workspaces, store, runs, register), runs, store);
    }

    static async Task<(JsonObject Input, IReadOnlyList<JsonNode> Rows)> Price(RunService runs, IDataStore store, PortfolioWorkspaces service, WorkspaceState ws)
    {
        var accepted = Assert.IsType<SubmissionResult.Accepted>(service.Price(ws.WorkspaceId, ws.Revision, "akira", Guid.NewGuid().ToString()));
        await runs.Completion(accepted.Run.RunId);
        var run = runs.Find(accepted.Run.RunId)!;
        Assert.Equal(RunStatus.Succeeded, run.Status);
        var input = PortfolioInput.Cases(store.ReadRequired(((DataReferenceBinding)run.Workflow.Steps[0].Bindings["acceptedInput"]).Reference))[0].Input;
        return (input, [.. store.ReadRequired(run.Outputs[XvaMeasures.Module])["rows"]!.AsArray().Select(r => r!)]);
    }

    static JsonNode Row(IReadOnlyList<JsonNode> rows, string metric, string @case = PortfolioCases.BaseId) =>
        rows.Single(r => r["metric"]!.GetValue<string>() == metric && r["case"]!.GetValue<string>() == @case);

    static decimal After(IReadOnlyList<JsonNode> rows, string metric, string @case = PortfolioCases.BaseId) => Row(rows, metric, @case)["after"]!.GetValue<decimal>();

    static WorkspaceState Cds(PortfolioWorkspaces service, WorkspaceState ws, string text) => service.ChooseCds(ws.WorkspaceId, ws.Revision, "akira", text).State;

    [Fact] // ADR-0010 §8's note of 2026-11-09: the source names each counterparty's CDS; XYZ Industries is proxied
    public void Each_counterparty_has_the_cds_its_source_names()
    {
        var (service, _, _) = Priced();
        EffectiveCds Of(string registration) => service.View(service.Create("akira", registration, Day1, 5).WorkspaceId, "akira").Credit.Cds!;

        var abch = Of("ABC/LE-01");
        Assert.Equal((AbchUsd, CdsCatalog.SingleName, "ABCH | USD | SNRFOR | XR14 | ABC Holdings (synthetic)", 0.4m), (abch.CurveId, abch.Type, abch.Reference, abch.Recovery));
        Assert.Equal(0.0052m, abch.Spreads["5y"]);
        Assert.Equal("ABCS.SNRFOR.USD.XR14", Of("ABC/LE-02").CurveId);
        var xyz = Of("XYZ/LE-03");
        Assert.Equal((CdsCatalog.Proxy, "industrials", "asia", "BBB"), (xyz.Type, xyz.Sector, xyz.Region, xyz.Rating));
        Assert.Equal(CdsCatalog.Tenors, xyz.Spreads.Keys);
    }

    [Fact] // ADR-0010 §8's note of 2026-11-09: cds reads a single name's words in any order, and refuses what names none or several
    public void Cds_chooses_a_single_name_by_its_words_a_proxy_by_its_keys_or_none()
    {
        var (service, _, _) = Priced();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 5);

        ws = Cds(service, ws, "cr14 abch");
        Assert.Equal("ABCH.SNRFOR.JPY.CR14", service.View(ws.WorkspaceId, "akira").Credit.Cds!.CurveId);
        var ambiguous = Assert.Throws<WorkspaceRefused>(() => Cds(service, ws, "abch usd"));
        Assert.Equal(("AmbiguousCds", "abch usd names 2 single names, ABCH USD SNRFOR XR14; ABCH USD SUBLT2 XR14: add a word to name one."), (ambiguous.Code, ambiguous.Message));
        Assert.Equal("UnknownCds", Assert.Throws<WorkspaceRefused>(() => Cds(service, ws, "zzz")).Code);

        ws = Cds(service, ws, "proxy BBB financials europe");
        var proxy = service.View(ws.WorkspaceId, "akira").Credit;
        Assert.Equal(("PROXY.FINANCIALS.EUROPE.BBB", 0.4m), (proxy.Cds!.CurveId, proxy.CdsRecovery));
        Assert.Equal("cds proxy takes a sector, a region and a rating; it has no rating.",
            Assert.Throws<WorkspaceRefused>(() => Cds(service, ws, "proxy financials europe")).Message);

        ws = Cds(service, ws, "none");
        var none = service.View(ws.WorkspaceId, "akira").Credit;
        Assert.Null(none.Cds);
        Assert.Null(none.CdsRecovery);
        Assert.Equal("No CDS for ABC/LE-01: choose a single name or a proxy on the Credit tab, or with cds.", none.Missing);
        // The keys of the type not chosen are kept: choosing it again finds them.
        ws = Cds(service, ws, "abch snrfor usd");
        Assert.Equal((CdsCatalog.SingleName, "financials"), (service.View(ws.WorkspaceId, "akira").Credit.Choice.Type, service.View(ws.WorkspaceId, "akira").Credit.Choice.Sector));
    }

    [Fact] // ADR-0010 §8's note of 2026-11-09: a spread edit stays with its curve, and a spread is above 0
    public void A_spread_edit_stays_with_its_curve()
    {
        var (service, _, _) = Priced();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 5);

        ws = service.Edit(ws.WorkspaceId, ws.Revision, "akira", Entity.Of(Entity.Cds, AbchUsd), "5y", 0.012m);
        var edited = service.View(ws.WorkspaceId, "akira").Credit.Cds!;
        Assert.Equal(0.012m, edited.Spreads["5y"]);
        Assert.Equal(["5y"], edited.Overridden);
        Assert.Equal(0.0052m, edited.Original!["5y"]!.GetValue<decimal>());

        ws = Cds(service, ws, "abch jpy");
        Assert.Equal(0.0045m, service.View(ws.WorkspaceId, "akira").Credit.Cds!.Spreads["5y"]);
        ws = Cds(service, ws, "abch usd snrfor");
        Assert.Equal(0.012m, service.View(ws.WorkspaceId, "akira").Credit.Cds!.Spreads["5y"]);

        var refused = Assert.Throws<WorkspaceRefused>(() => service.Edit(ws.WorkspaceId, ws.Revision, "akira", Entity.Of(Entity.Cds, AbchUsd), "5y", 0m));
        Assert.Equal(("InvalidValue", "A spread is above 0bp and below 10000bp."), (refused.Code, refused.Message));
    }

    [Fact] // ADR-0010 §8, XV15: a wider spread makes CVA more negative; an empty CVA recovery takes the curve's
    public async Task Cva_reads_the_curve_its_spreads_and_its_recovery()
    {
        var (service, runs, store) = Priced();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 20);
        var (input, rows) = await Price(runs, store, service, ws);
        var cva = After(rows, "cva");
        Assert.True(cva < 0, $"CVA {cva} is a cost.");
        var credit = input["modules"]!["xva"]!["credit"]!;
        Assert.Equal((AbchUsd, 0.4m), (credit["cds"]!["curveId"]!.GetValue<string>(), credit["cds"]!["recovery"]!.GetValue<decimal>()));
        Assert.Null(credit["cvaRecovery"]);

        foreach (var tenor in CdsCatalog.Tenors)
            ws = service.SetCells(ws.WorkspaceId, ws.Revision, "akira", ["cds"], tenor, "150bp");
        var (_, wider) = await Price(runs, store, service, ws);
        Assert.True(After(wider, "cva") < cva, $"CVA {After(wider, "cva")} on 150bp is not below {cva}.");

        // A CVA recovery set is the loss's; the empty one was the curve's 40%.
        ws = service.Edit(ws.WorkspaceId, ws.Revision, "akira", "netting:NS-001", "cvaRecovery", 0.1m);
        var (_, lower) = await Price(runs, store, service, ws);
        Assert.Equal(Math.Round(After(wider, "cva") * 0.9m / 0.6m, 0), Math.Round(After(lower, "cva"), 0));
    }

    [Fact] // ADR-0010 §8's note of 2026-11-09, §10: a missing CDS refuses CVA and CVA Charge by name, and FVA is priced
    public async Task A_missing_cds_refuses_cva_and_its_charge_and_nothing_else()
    {
        var (service, runs, store) = Priced();
        var ws = service.Create("akira", "XYZ/LE-03", Day1, 10);
        ws = service.Edit(ws.WorkspaceId, ws.Revision, "akira", "credit:XYZ/LE-03", "proxyRating", null);

        var (_, rows) = await Price(runs, store, service, ws);

        const string why = "No CDS for XYZ/LE-03: its proxy has no rating. Choose it on the Credit tab, or with cds proxy.";
        Assert.All(new[] { "cva", "cvaCharge", "pv" }, metric => Assert.Equal(("refused", why), (Row(rows, metric)["afterStatus"]!.GetValue<string>(), Row(rows, metric)["reason"]!.GetValue<string>())));
        Assert.Equal("priced", Row(rows, "fva")["afterStatus"]!.GetValue<string>());
    }

    [Fact] // ADR-0010 §9, §8's note of 2026-11-09: set and vary reach the chosen curve's spreads, and the Changes tab names them
    public async Task Set_and_vary_reach_the_chosen_curve()
    {
        var (service, runs, store) = Priced();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 20);

        ws = service.SetCells(ws.WorkspaceId, ws.Revision, "akira", ["cds"], "5y", "120bp");
        Assert.Equal(0.012m, service.View(ws.WorkspaceId, "akira").Credit.Cds!.Spreads["5y"]);
        ws = service.Vary(ws.WorkspaceId, ws.Revision, "akira", "cds 3y", ["100bp", "300bp"]).State;

        var changes = service.Changes(ws.WorkspaceId, "akira");
        Assert.Equal(("cds 5y", "120bp", "52bp"), (changes.Common[0].Title, changes.Common[0].Text, changes.Common[0].OriginalText));
        Assert.Equal(["cds 3y 100bp", "cds 3y 300bp"], changes.Cases.Select(c => c.Case.Title));
        var (_, rows) = await Price(runs, store, service, ws);
        var cases = service.View(ws.WorkspaceId, "akira").Cases;
        Assert.True(After(rows, "cva", cases[2].Id) < After(rows, "cva", cases[1].Id));

        ws = service.ChooseCds(ws.WorkspaceId, ws.Revision, "akira", "none").State;
        Assert.Equal("No CDS for ABC/LE-01: choose a single name or a proxy on the Credit tab, or with cds.",
            Assert.Throws<WorkspaceRefused>(() => service.SetCells(ws.WorkspaceId, ws.Revision, "akira", ["cds"], "5y", "1%")).Message);
        // The choice itself is a cell too.
        ws = service.SetCells(ws.WorkspaceId, ws.Revision, "akira", ["credit"], "type", CdsCatalog.Proxy);
        Assert.Equal("No CDS for ABC/LE-01: its proxy has no sector, region, rating. Choose them on the Credit tab, or with cds proxy.",
            service.View(ws.WorkspaceId, "akira").Credit.Missing);
    }

    [Fact] // ADR-0010 §4, §8's note of 2026-11-09: a Credit shift widens the chosen curve in a scenario: CVA more negative, FVA as on the base market
    public async Task A_credit_shift_widens_the_chosen_curve_in_a_scenario()
    {
        var (service, runs, store) = Priced();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 20);
        ws = service.SetMeasures(ws.WorkspaceId, ws.Revision, "akira", ["cva", "fva"]);

        ws = service.DefineScenario(ws.WorkspaceId, ws.Revision, "akira", "wide", "cds +50bp").State;
        var (_, rows) = await Price(runs, store, service, ws);

        decimal On(string scenario, string metric) => rows.Single(r => r["metric"]!.GetValue<string>() == metric && r["case"]!.GetValue<string>() == PortfolioCases.BaseId
            && r["scenario"]!.GetValue<string>() == scenario)["after"]!.GetValue<decimal>();
        // A scenario is priced as the market of its order, titled by its name.
        Assert.Equal("wide", rows.First(r => r["scenario"]!.GetValue<string>() == "s1")["scenarioTitle"]!.GetValue<string>());
        Assert.True(On("s1", "cva") < On(PortfolioScenarios.BaseId, "cva"));
        Assert.Equal(On(PortfolioScenarios.BaseId, "fva"), On("s1", "fva"));
        var row = Assert.Single(service.View(ws.WorkspaceId, "akira").Shifts);
        Assert.Equal(("Credit", "cds", "all", "abs", "+50bp"), (row.Category, row.Index, row.Factor, row.Type, row.SizeText));
    }

    [Fact] // ADR-0010 §4's note of 2026-11-09: a key-rate Credit shift falls to nothing at the CDS tenors either side of its own
    public void A_key_rate_credit_shift_moves_only_the_points_around_its_tenor()
    {
        var market = new ScenarioMarket([ShiftReader.Read("cds 5y +20bp")], Day1);

        Assert.Equal([0m, 10m, 20m, 10m, 0m], new[] { 3.0, 4.0, 5.0, 6.0, 7.0 }.Select(market.CreditShiftBp));
        // A Credit shift moves no curve, and an IR shift no CDS.
        Assert.Equal(0m, market.RateShiftBp("jpy_ois", 5));
        Assert.Equal(0m, new ScenarioMarket([ShiftReader.Read("jpy_ois +20bp")], Day1).CreditShiftBp(5));
    }

    [Theory] // ADR-0010 §4's note of 2026-11-09: tenors are each category's, and a Credit shift is absolute, each refused naming what it takes
    [InlineData("cds 30y +10bp", "The shift cds 30y +10bp cds has no tenor 30y: a Credit tenor is 6m, 1y, 2y, 3y, 5y, 7y, 10y.")]
    [InlineData("jpy_ois 7y +10bp", "The shift jpy_ois 7y +10bp jpy_ois has no tenor 7y: an IR tenor is 1m, 3m, 6m, 1y, 2y, 5y, 10y, 30y.")]
    [InlineData("cds -5%", "The shift cds -5% shifts cds relatively, and a Credit shift is absolute, in bp: write cds +10bp.")]
    public void A_credit_shift_is_refused_off_its_tenors_or_relative(string written, string said)
    {
        Assert.Equal(said, Assert.Throws<WorkspaceRefused>(() => ShiftReader.Read(written)).Message);
    }

    [Fact] // ADR-0010 §4: the Scenarios grid and the generators take a Credit shift, the grid refusing an IR tenor on the CDS
    public void The_grid_and_the_ladder_take_a_credit_shift()
    {
        var (service, _, _) = Priced();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 20);

        var made = service.GenerateScenarios(ws.WorkspaceId, ws.Revision, "akira", "ladder", "cds 5y -20bp..+20bp step 20bp");
        Assert.Equal(["cds 5y -20bp", "cds 5y +20bp"], made.Made);
        ws = service.EditShifts(made.State.WorkspaceId, made.State.Revision, "akira",
            [new(null, 3, new Dictionary<string, string?> { ["scenario"] = "kr", ["index"] = "cds", ["factor"] = "7y", ["size"] = "15" })]);
        var row = service.View(ws.WorkspaceId, "akira").Shifts.Single(r => r.Scenario == "kr");
        Assert.Equal(("Credit", "7y", "abs", "+15bp", null), (row.Category, row.Factor, row.Type, row.SizeText, row.Issue));

        var refused = Assert.Throws<WorkspaceRefused>(() => service.EditShifts(ws.WorkspaceId, ws.Revision, "akira",
            [new(row.Id, row.Row, new Dictionary<string, string?> { ["factor"] = "30y" })]));
        Assert.Equal("Row 3, Risk factor: 30y is no tenor of cds: a Credit tenor is 6m, 1y, 2y, 3y, 5y, 7y, 10y, or all for the whole curve.", refused.Message);
    }
}
