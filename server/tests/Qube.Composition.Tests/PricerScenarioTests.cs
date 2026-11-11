using System.Text.Json;
using System.Text.Json.Nodes;
using Qube.Composition.Application;
using Qube.Composition.Application.Workspaces;
using Qube.Composition.Contracts;
using Qube.Samples.Ccr;
using Qube.Samples.Portfolio;
using Qube.Samples.Rates;
using Qube.Samples.Simm;
using Qube.Samples.Xva;

namespace Qube.Composition.Tests;

// Scenarios on the portfolio workspace service (ADR-0010 §4 and its note of 2026-11-09), with XVA's, SA-CCR's and SIMM's
// registrations, as the application registers them. A scenario's figures are read against the base market's of the same
// Run: they move in the direction its shift says, and equal them where nothing priced reads the shifted factor. Names
// and figures are synthetic.
public class PricerScenarioTests
{
    static readonly DateOnly Day1 = new(2026, 10, 26);

    static ConventionProfile Profile(string ccy, string index) =>
        new($"test.{ccy}.{index}", "1", ccy, index, DayCountBasis.Act360, "weekends-only", BusinessDayAdjustment.ModifiedFollowing, 2, 2, StubRule.ShortFront, false, true);

    static readonly ConventionRegistry Conventions = new([Profile("JPY", "TONA"), Profile("USD", "SOFR")], [new WeekendsOnlyCalendar()]);

    static readonly SyntheticMarket Market = new(new("scenario-1", 7, new DateTimeOffset(2026, 10, 26, 0, 0, 0, TimeSpan.Zero), TimeSpan.FromSeconds(1),
        [new("JPY", "TONA", [(Tenor.Parse("1y"), 0.4), (Tenor.Parse("5y"), 0.9), (Tenor.Parse("10y"), 1.3)]),
         new("USD", "SOFR", [(Tenor.Parse("1y"), 4.1), (Tenor.Parse("5y"), 3.8), (Tenor.Parse("10y"), 3.9)])],
        new Dictionary<string, double> { ["USD"] = 1, ["JPY"] = 150 }, 0.5, 0.2, 0.0005));

    static (PortfolioWorkspaces Service, RunService Runs, IDataStore Store) Priced(PortfolioOptions? options = null)
    {
        var workspaces = new WorkspaceService(new Dictionary<string, IEditRules> { [PortfolioEditRules.App] = new PortfolioEditRules() }, TimeProvider.System);
        var store = new InMemoryDataStore();
        var simm = new SimmModule(new SyntheticImSource(), new FakeRatesPricing(), Market.Resolve, Conventions, [SimmParameters.Demo]);
        var runs = new RunService(new CompositionRegistry([new PortfolioModule(), new XvaModule(), new CcrModule(), simm]), store, TimeProvider.System);
        var register = new MeasureRegister([new XvaMeasures(Pv01Provider.Simulated(TimeSpan.Zero)), new CcrMeasures(),
            new SimmMeasures(Conventions, _ => Market.Snapshot(0), [SimmParameters.Demo])]);
        return (new PortfolioWorkspaces(workspaces, store, runs, register, UnsetSettings.Pricer(), options), runs, store);
    }

    sealed record PricedRun(RunRecord Run, JsonObject Input, IReadOnlyList<JsonNode> Rows, IReadOnlyList<TraceEntry> Traces);

    /// <summary>Prices the revision and reads its accepted input, every module's rows and every trace written.</summary>
    static async Task<PricedRun> Price(RunService runs, IDataStore store, PortfolioWorkspaces service, WorkspaceState ws)
    {
        var accepted = Assert.IsType<SubmissionResult.Accepted>(service.Price(ws.WorkspaceId, ws.Revision, "akira", Guid.NewGuid().ToString()));
        await runs.Completion(accepted.Run.RunId);
        var run = runs.Find(accepted.Run.RunId)!;
        Assert.Equal(RunStatus.Succeeded, run.Status);
        var input = store.ReadRequired(((DataReferenceBinding)run.Workflow.Steps[0].Bindings["acceptedInput"]).Reference).AsObject();
        var rows = run.Outputs.Values.SelectMany(o => store.ReadRequired(o)["rows"]!.AsArray().Select(r => r!)).ToList();
        var traces = run.Invocations.Where(i => i.Outputs.ContainsKey("traces"))
            .SelectMany(i => store.ReadRequired(i.Outputs["traces"]).Deserialize<TraceSet>(WireJson.Options)!.Traces).ToList();
        return new(run, input, rows, traces);
    }

    static JsonNode Row(PricedRun priced, string scenario, string metric, string @case = PortfolioCases.BaseId) =>
        priced.Rows.Single(r => r["case"]!.GetValue<string>() == @case && r["scenario"]!.GetValue<string>() == scenario && r["metric"]!.GetValue<string>() == metric);

    static decimal After(PricedRun priced, string scenario, string metric) => Row(priced, scenario, metric)["after"]!.GetValue<decimal>();

    static WorkspaceState Define(PortfolioWorkspaces service, WorkspaceState ws, string name, string shifts) =>
        service.DefineScenario(ws.WorkspaceId, ws.Revision, "akira", name, shifts).State;

    // Every existing trade in After alone, as a desk prices its portfolio; the new drafts out.
    static WorkspaceState Existing(PortfolioWorkspaces service, WorkspaceState ws)
    {
        ws = service.Edit(ws.WorkspaceId, ws.Revision, "akira", Entity.Of(Entity.Grid, Entity.ExistingGrid), "after", JsonValue.Create(true));
        return service.Edit(ws.WorkspaceId, ws.Revision, "akira", Entity.Of(Entity.Grid, Entity.NewGrid), "after", JsonValue.Create(false));
    }

    [Theory] // ADR-0010 §4: a shift's words in any order, each recognised by its form; the unit gives the shift type
    [InlineData("jpy_ois +10bp", "IR", "jpy_ois", null, "abs", 10)]
    [InlineData("+3bp 5Y JPY_OIS", "IR", "jpy_ois", "5y", "abs", 3)]
    [InlineData("usdjpy -5%", "FX", "usdjpy", null, "rel", -5)]
    [InlineData("usd_funding 10y 2.5 abs", "IR", "usd_funding", "10y", "abs", 2.5)]
    [InlineData("eurjpy rel 1.5", "FX", "eurjpy", null, "rel", 1.5)]
    public void A_shift_reads_its_words_in_any_order(string text, string category, string index, string? tenor, string type, double size)
    {
        var shift = ShiftReader.Read(text);

        Assert.Equal(new Shift(category, index, tenor, type, (decimal)size), shift);
    }

    [Theory] // ADR-0010 §4 and its note of 2026-11-09: what does not resolve is refused with what it could be, never guessed
    [InlineData("jpy_ojs +10bp", "jpy_ojs, which is no index, tenor or size: the indices are jpy_ois")]
    [InlineData("jpy_ois +10%", "an IR shift is absolute, in bp: write jpy_ois +10bp")]
    [InlineData("usdjpy +10bp", "an FX shift is relative, in %: write usdjpy -5%")]
    [InlineData("usdjpy 5y -5%", "usdjpy is a spot rate and has no tenor")]
    [InlineData("jpy_ois +10", "a size with no unit, and the unit says how it shifts: write jpy_ois +10bp")]
    [InlineData("jpy_ois usd_sofr +10bp", "names two indices, jpy_ois and usd_sofr")]
    [InlineData("5y +10bp", "names no index")]
    [InlineData("jpy_ois +10bp rel", "is in bp, which is not rel")]
    public void A_shift_that_does_not_resolve_is_refused_with_what_it_could_be(string text, string said)
    {
        var refused = Assert.Throws<WorkspaceRefused>(() => ShiftReader.Read(text));

        Assert.Equal("InvalidShift", refused.Code);
        Assert.Contains(said, refused.Message);
    }

    [Fact] // ADR-0010 §4: one command makes a scenario of every shift; again, it replaces the one of its name and says so
    public void A_scenario_is_defined_whole_and_replaced_by_name()
    {
        var (service, _, _) = Priced();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 40);

        var made = service.DefineScenario(ws.WorkspaceId, ws.Revision, "akira", "s1", "jpy_ois +10bp; usdjpy -5%; jpy_ois 10y +3bp");
        var again = service.DefineScenario(made.State.WorkspaceId, made.State.Revision, "akira", "S1", "usd_sofr -25bp");

        Assert.Equal(["s1"], made.Made);
        Assert.Empty(made.Replaced);
        Assert.Equal(["S1"], again.Made);
        Assert.Equal(["S1"], again.Replaced);
        var view = service.View(ws.WorkspaceId, "akira");
        Assert.Equal([new ScenarioView("S1", 1, true, null)], view.Scenarios);
        var row = Assert.Single(view.Shifts);
        Assert.Equal(("S1", "IR", "usd_sofr", null, "abs", "-25bp", null), (row.Scenario, row.Category, row.Index, row.Tenor, row.Type, row.SizeText, row.Issue));
        // A scenario is no change of the portfolio's data: the Changes tab does not count it.
        Assert.Equal(0, view.Changes);
    }

    [Theory] // ADR-0010 §4's note of 2026-11-09: a name the command line could not tell from a command, an empty name, and a zero shift are refused
    [InlineData("include", "jpy_ois +10bp", "scenario include is a command")]
    [InlineData(" ", "jpy_ois +10bp", "A scenario needs a name")]
    [InlineData("s1", "jpy_ois 0", "jpy_ois 0bp shifts nothing")]
    [InlineData("s1", "jpy_ois +10bp;; usdjpy -5%", "A shift between two semicolons is empty")]
    public void A_scenario_that_cannot_be_made_is_refused_by_name(string name, string shifts, string said)
    {
        var (service, _, _) = Priced();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 40);

        var refused = Assert.Throws<WorkspaceRefused>(() => service.DefineScenario(ws.WorkspaceId, ws.Revision, "akira", name, shifts));

        Assert.Contains(said, refused.Message);
        Assert.Empty(service.View(ws.WorkspaceId, "akira").Scenarios);
    }

    [Fact] // ADR-0010 §4's note of 2026-11-09: a scenario belongs to the workspace, so a counterparty change keeps it
    public void A_counterparty_change_keeps_the_scenarios()
    {
        var (service, _, _) = Priced();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 40);
        ws = Define(service, ws, "s1", "jpy_ois +10bp");

        var drop = service.PreviewCounterpartyChange(ws.WorkspaceId, "akira");
        var (moved, _) = service.ChangeCounterparty(ws.WorkspaceId, ws.Revision, "akira", "ABC/LE-02", drop.Count);

        Assert.Equal(0, drop.Count);
        Assert.Equal(["s1"], service.View(moved.WorkspaceId, "akira").Scenarios.Select(s => s.Name));
    }

    [Fact] // ADR-0010 §4: Price prices the base market and each scenario, crossed with every case, in one Run that freezes the shifts
    public async Task Price_crosses_every_case_with_the_base_market_and_each_scenario()
    {
        var (service, runs, store) = Priced();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 40);
        ws = service.SelectMeasures(ws.WorkspaceId, ws.Revision, "akira", "cva fva").State;
        ws = service.Vary(ws.WorkspaceId, ws.Revision, "akira", "CSA-A threshold", ["10mm"]).State;
        ws = Define(service, ws, "s1", "jpy_ois +10bp; usdjpy -5%");
        ws = Define(service, ws, "s2", "usd_sofr 5y -25bp");

        var priced = await Price(runs, store, service, ws);

        var scenarios = priced.Input["scenarios"]!.AsArray();
        Assert.Equal([("base", "Base market", 0), ("s1", "s1", 2), ("s2", "s2", 1)],
            scenarios.Select(s => (s!["id"]!.GetValue<string>(), s["title"]!.GetValue<string>(), s["shifts"]!.AsArray().Count)));
        Assert.Equal(("IR", "usd_sofr", "5y", "abs", -25m), (scenarios[2]!["shifts"]![0]!["category"]!.GetValue<string>(), scenarios[2]!["shifts"]![0]!["index"]!.GetValue<string>(),
            scenarios[2]!["shifts"]![0]!["tenor"]!.GetValue<string>(), scenarios[2]!["shifts"]![0]!["type"]!.GetValue<string>(), scenarios[2]!["shifts"]![0]!["size"]!.GetValue<decimal>()));
        // Two cases on three markets: six pairs, each with every metric of the selected measures.
        var pairs = PortfolioInput.Cases(priced.Input).Select(c => (c.Scenario.Id, c.Title)).ToList();
        Assert.Equal(6, pairs.Count);
        Assert.Equal(("base", "Base"), pairs[0]);
        Assert.Equal(6, priced.Rows.Count(r => r["metric"]!.GetValue<string>() == "cva"));
        Assert.All(priced.Rows, r => Assert.Equal(r["scenario"]!.GetValue<string>() == "base" ? "Base market" : r["scenario"]!.GetValue<string>(), r["scenarioTitle"]!.GetValue<string>()));
    }

    [Fact] // ADR-0010 §4's note of 2026-11-09: a curve shift moves the toy XVA of trades on that curve; one on a curve no trade is on moves nothing
    public async Task A_curve_shift_moves_CVA_FVA_and_PV_and_one_no_trade_reads_moves_nothing()
    {
        var (service, runs, store) = Priced();
        var ws = Existing(service, service.Create("akira", "ABC/LE-01", Day1, 40));
        ws = service.SelectMeasures(ws.WorkspaceId, ws.Revision, "akira", "cva fva").State;
        // CSA-B's trades, on usd_sofr, are the ones in FVA.
        ws = Define(service, ws, "up", "usd_sofr +100bp");
        ws = Define(service, ws, "eur", "eur_estr +100bp");

        var priced = await Price(runs, store, service, ws);

        foreach (var metric in new[] { "cva", "fva", "pv" })
        {
            Assert.NotEqual(After(priced, "base", metric), After(priced, "s1", metric));
            Assert.Equal(After(priced, "base", metric), After(priced, "s2", metric));
        }
    }

    [Fact] // ADR-0010 §4's note of 2026-11-09: a funding curve shift moves FVA's toy funding spread alone
    public async Task A_funding_shift_moves_FVA_alone()
    {
        var (service, runs, store) = Priced();
        var ws = Existing(service, service.Create("akira", "ABC/LE-01", Day1, 40));
        ws = service.SelectMeasures(ws.WorkspaceId, ws.Revision, "akira", "cva fva").State;
        // CSA-B is imperfect (ADR-0010 §5), so its trades are FVA's, funded on usd_funding.
        ws = Define(service, ws, "funding", "usd_funding +50bp");

        var priced = await Price(runs, store, service, ws);

        Assert.True(After(priced, "s1", "fva") < After(priced, "base", "fva"), "Funding 50bp dearer makes FVA more negative.");
        Assert.Equal(After(priced, "base", "cva"), After(priced, "s1", "cva"));
        Assert.Equal(After(priced, "base", "pv"), After(priced, "s1", "pv"));
    }

    [Fact] // ADR-0010 §4's note of 2026-11-09: an FX shift moves the figures of trades in that currency, and every amount converts at its FX
    public async Task An_FX_shift_moves_the_figures_of_trades_in_that_currency()
    {
        var (service, runs, store) = Priced();
        var ws = Existing(service, service.Create("akira", "ABC/LE-01", Day1, 40));
        ws = service.SelectMeasures(ws.WorkspaceId, ws.Revision, "akira", "cva").State;
        ws = Define(service, ws, "usd", "usdjpy +10%");
        ws = Define(service, ws, "gbp", "gbpjpy +10%");

        var priced = await Price(runs, store, service, ws);

        // Shown in JPY, the display currency: USD trades are worth more yen, and GBP, which no trade is in, moves nothing.
        Assert.NotEqual(After(priced, "base", "cva"), After(priced, "s1", "cva"));
        Assert.NotEqual(After(priced, "base", "pv"), After(priced, "s1", "pv"));
        Assert.Equal(After(priced, "base", "cva"), After(priced, "s2", "cva"));
        Assert.Equal(After(priced, "base", "pv"), After(priced, "s2", "pv"));
    }

    [Fact] // ADR-0010 §4: two shifts touching one risk factor in one scenario are refused at Price, naming them
    public void Two_shifts_on_one_risk_factor_refuse_Price_naming_them()
    {
        var (service, _, _) = Priced();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 40);
        ws = Define(service, ws, "s1", "jpy_ois +10bp; usdjpy -5%; jpy_ois 5y +3bp");

        var refused = Assert.Throws<WorkspaceRefused>(() => service.Price(ws.WorkspaceId, ws.Revision, "akira", Guid.NewGuid().ToString()));

        Assert.Equal("RiskFactorShiftedTwice", refused.Code);
        Assert.Contains("Scenario s1 shifts jpy_ois 5y twice, in row 1 (jpy_ois +10bp) and row 3 (jpy_ois 5y +3bp)", refused.Message);
        Assert.Contains("jpy_ois 5y twice", service.View(ws.WorkspaceId, "akira").Scenarios.Single().Issue);
    }

    [Fact] // ADR-0010 §4's note of 2026-11-09: two key-rate shifts at different tenors of one index do not touch
    public void Key_rate_shifts_at_two_tenors_do_not_touch_and_fade_between_them()
    {
        Assert.False(ShiftReader.Read("jpy_ois 5y +3bp").Touches(ShiftReader.Read("jpy_ois 10y +3bp")));
        var market = new ScenarioMarket([ShiftReader.Read("jpy_ois 5y +10bp")], Day1);

        Assert.Equal(10m, market.RateShiftBp("jpy_ois", 5));
        Assert.Equal(5m, market.RateShiftBp("jpy_ois", 7.5));
        Assert.Equal(0m, market.RateShiftBp("jpy_ois", 10));
        Assert.Equal(0m, market.RateShiftBp("jpy_ois", 2));
        Assert.Equal(0m, market.RateShiftBp("usd_sofr", 5));
        // The last tenor's shift holds beyond it, as the first's holds before it.
        Assert.Equal(4m, new ScenarioMarket([ShiftReader.Read("jpy_ois 30y +4bp")], Day1).RateShiftBp("jpy_ois", 40));
    }

    [Fact] // ADR-0010 §3, §4's note of 2026-11-09: the cap counts pairs of a case and a market, and Price over it says by how much
    public void Pairs_over_the_cap_refuse_Price_by_how_much()
    {
        var (service, _, _) = Priced(new PortfolioOptions(CaseCap: 5));
        var ws = service.Create("akira", "ABC/LE-01", Day1, 40);
        ws = service.Vary(ws.WorkspaceId, ws.Revision, "akira", "CSA-A threshold", ["10mm"]).State;
        ws = Define(service, ws, "s1", "jpy_ois +10bp");
        ws = Define(service, ws, "s2", "jpy_ois -10bp");

        var refused = Assert.Throws<WorkspaceRefused>(() => service.Price(ws.WorkspaceId, ws.Revision, "akira", Guid.NewGuid().ToString()));

        Assert.Equal("TooManyCases", refused.Code);
        Assert.Contains("2 case(s) × 3 markets (the base market and 2 scenario(s)) are 6, 1 over", refused.Message);
    }

    [Fact] // ADR-0010 §4's note of 2026-11-09: Copy quote quotes the scenario asked for, and names it; the base market by default
    public async Task Copy_quote_quotes_one_market_and_names_a_scenario()
    {
        var (service, runs, store) = Priced();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 40);
        ws = service.SelectMeasures(ws.WorkspaceId, ws.Revision, "akira", "cva").State;
        ws = Define(service, ws, "up", "usdjpy +10%");
        var priced = await Price(runs, store, service, ws);

        var basis = XvaQuote.Of(priced.Run, store);
        var stressed = XvaQuote.Of(priced.Run, store, "s1");

        Assert.DoesNotContain("Scenario", basis.Text);
        Assert.Contains("Scenario up", stressed.Text);
        Assert.NotEqual(basis.Text.Split('\n')[^1], stressed.Text.Split('\n')[^1]);
        Assert.Equal("UnknownScenario", Assert.Throws<WorkspaceRefused>(() => XvaQuote.Of(priced.Run, store, "s9")).Code);
    }

    [Fact] // ADR-0010 §4's note of 2026-11-09: spot IM generates its CRIF again on the shifted curves and FX; a shift it does not read moves nothing
    public async Task Spot_IM_is_priced_again_under_each_scenario()
    {
        var (service, runs, store) = Priced();
        var ws = Existing(service, service.Create("akira", "ABC/LE-01", Day1, 40));
        ws = service.SelectMeasures(ws.WorkspaceId, ws.Revision, "akira", "simm.spotIm").State;
        ws = Define(service, ws, "rates", "jpy_ois +100bp; usd_sofr +100bp");
        ws = Define(service, ws, "fx", "usdjpy +10%");
        ws = Define(service, ws, "funding", "jpy_funding +100bp");

        var priced = await Price(runs, store, service, ws);

        var metric = SimmMeasures.Definitions.Single().Metrics[0].Id;
        Assert.NotEqual(After(priced, "base", metric), After(priced, "s1", metric));
        Assert.NotEqual(After(priced, "base", metric), After(priced, "s2", metric));
        Assert.Equal(After(priced, "base", metric), After(priced, "s3", metric));
        // Each pair's trace is written at its case and scenario (ADR-0011).
        Assert.Equal(["base", "s1", "s2", "s3"], priced.Traces.Where(t => t.Measure == SimmMeasures.SpotIm && t.Side == "after").Select(t => t.Scenario));
    }

    [Fact] // ADR-0010 §4's note of 2026-11-09: spot CCR capital takes the revalued trades and the shifted FX
    public async Task Spot_capital_is_priced_again_under_each_scenario()
    {
        var (service, runs, store) = Priced();
        var ws = Existing(service, service.Create("akira", "ABC/LE-01", Day1, 40));
        ws = service.SelectMeasures(ws.WorkspaceId, ws.Revision, "akira", "ccr.spotCapital").State;
        ws = Define(service, ws, "rates", "jpy_ois +100bp");
        ws = Define(service, ws, "fx", "usdjpy +10%");
        ws = Define(service, ws, "eur", "eur_estr +100bp");

        var priced = await Price(runs, store, service, ws);

        Assert.NotEqual(After(priced, "base", "ead"), After(priced, "s1", "ead"));
        Assert.NotEqual(After(priced, "base", "ead"), After(priced, "s2", "ead"));
        Assert.Equal(After(priced, "base", "ead"), After(priced, "s3", "ead"));
        Assert.Equal(["base", "s1", "s2", "s3"], priced.Traces.Where(t => t.Measure == CcrMeasures.SpotCapital && t.Side == "after").Select(t => t.Scenario));
        // Through its replacement cost, whose market value the curve shift revalues, and through its add-on, whose
        // notionals the FX shift converts at another rate.
        double Sum(string scenario, string kind) => priced.Traces.Single(t => t.Measure == CcrMeasures.SpotCapital && t.Side == "after" && t.Scenario == scenario)
            .Trace.Terms.Where(t => t.Kind == kind).Sum(t => t.Value!.GetValue<double>());
        Assert.NotEqual(Sum("base", "saccr.marketValue"), Sum("s1", "saccr.marketValue"));
        Assert.NotEqual(Sum("base", "saccr.addOnAggregate"), Sum("s2", "saccr.addOnAggregate"));
    }

    static ShiftEdit Blank(int row, string scenario, string index, string factor, string size, string? description = null) =>
        new(null, row, new Dictionary<string, string?> { ["scenario"] = scenario, ["index"] = index, ["factor"] = factor, ["size"] = size, ["description"] = description });

    [Fact] // ADR-0010 §4: a block pasted onto the blank rows makes a shift per row in one batch, Category and Shift type derived
    public void A_block_pasted_onto_the_blank_rows_makes_a_shift_per_row()
    {
        var (service, _, _) = Priced();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 40);

        var pasted = service.EditShifts(ws.WorkspaceId, ws.Revision, "akira",
            [Blank(1, "up", "jpy_ois", "", "+10bp", "parallel"), Blank(2, "up", "USDJPY", "spot", "-5%"), Blank(3, "kr", "jpy_ois", "5y", "3"),
             new(null, 4, new Dictionary<string, string?> { ["scenario"] = "", ["index"] = " " })]);

        var view = service.View(ws.WorkspaceId, "akira");
        Assert.Equal([("up", "IR", "jpy_ois", "all", "abs", "+10bp", "parallel"), ("up", "FX", "usdjpy", "spot", "rel", "-5%", null), ("kr", "IR", "jpy_ois", "5y", "abs", "+3bp", null)],
            view.Shifts.Select(r => (r.Scenario, r.Category, r.Index, r.Factor, r.Type, r.SizeText, r.Description)));
        Assert.Equal([new ScenarioView("up", 2, true, null), new ScenarioView("kr", 1, true, null)], view.Scenarios);
        // One batch: the workspace moved by one change a row made, and a blank row pasted made none.
        Assert.Equal(ws.Revision + 3, pasted.Revision);
    }

    [Theory] // ADR-0010 §4 and spine 1: a cell that does not resolve refuses the paste whole, naming its row and column
    [InlineData("index", "jpy_ojs", "Row 2, Index: jpy_ojs is no index: the indices are jpy_ois")]
    [InlineData("factor", "7y", "Row 2, Risk factor: 7y is no tenor of jpy_ois: an IR tenor is 1m, 3m")]
    [InlineData("size", "+10%", "Row 2, Size: an IR shift is absolute, in bp: write +10bp")]
    [InlineData("size", "ten", "Row 2, Size: ten is no size: write it as +10bp")]
    [InlineData("category", "FX", "Row 2, Category: jpy_ois is IR, not FX: the category follows the index")]
    [InlineData("type", "rel", "Row 2, Shift type: the size is in bp, so the shift is abs, not rel")]
    [InlineData("scenario", "include", "Row 2, Scenario: A scenario cannot be named include")]
    public void A_cell_that_does_not_resolve_refuses_the_paste_whole(string column, string text, string said)
    {
        var (service, _, _) = Priced();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 40);
        var cells = new Dictionary<string, string?> { ["scenario"] = "up", ["index"] = "jpy_ois", ["factor"] = "all", ["size"] = "+10bp", [column] = text };

        var refused = Assert.Throws<WorkspaceRefused>(() => service.EditShifts(ws.WorkspaceId, ws.Revision, "akira", [Blank(1, "up", "usd_sofr", "", "+5bp"), new(null, 2, cells)]));

        Assert.Contains(said, refused.Message);
        Assert.Empty(service.View(ws.WorkspaceId, "akira").Shifts);
    }

    [Fact] // ADR-0010 §4: an incomplete row says what it lacks on the row, and Price refuses its scenario by name while it is included
    public void An_incomplete_row_says_what_it_lacks_and_Price_refuses_its_scenario_while_included()
    {
        var (service, _, _) = Priced();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 40);
        ws = Define(service, ws, "up", "jpy_ois +10bp");
        ws = service.EditShifts(ws.WorkspaceId, ws.Revision, "akira", [new(null, 2, new Dictionary<string, string?> { ["scenario"] = "half", ["index"] = "usd_sofr" })]);

        var view = service.View(ws.WorkspaceId, "akira");
        Assert.Equal(("it has no size", "size"), (view.Shifts[1].Issue, view.Shifts[1].IssueColumn));
        Assert.Equal("row 2: it has no size", view.Scenarios.Single(s => s.Name == "half").Issue);
        var refused = Assert.Throws<WorkspaceRefused>(() => service.Price(ws.WorkspaceId, ws.Revision, "akira", Guid.NewGuid().ToString()));
        Assert.Equal("ScenarioInvalid", refused.Code);
        Assert.Contains("Scenario half cannot be priced: row 2 it has no size", refused.Message);

        ws = service.IncludeScenario(ws.WorkspaceId, ws.Revision, "akira", "HALF", included: false);

        Assert.IsType<SubmissionResult.Accepted>(service.Price(ws.WorkspaceId, ws.Revision, "akira", Guid.NewGuid().ToString()));
        Assert.Equal([("up", true), ("half", false)], service.View(ws.WorkspaceId, "akira").Scenarios.Select(s => (s.Name, s.Included)));
    }

    [Fact] // ADR-0010 §4: excluding leaves a scenario out of Price and keeps its rows; duplicating copies them; removing takes them away
    public async Task Exclude_duplicate_and_remove_act_on_a_scenario_by_name()
    {
        var (service, runs, store) = Priced();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 40);
        ws = service.SelectMeasures(ws.WorkspaceId, ws.Revision, "akira", "cva").State;
        ws = Define(service, ws, "up", "jpy_ois +10bp; usdjpy -5%");
        ws = Define(service, ws, "down", "jpy_ois -10bp");

        ws = service.IncludeScenario(ws.WorkspaceId, ws.Revision, "akira", "down", included: false);
        var copied = service.DuplicateScenario(ws.WorkspaceId, ws.Revision, "akira", "up", "up2");
        ws = copied.State;

        Assert.Equal(["up2"], copied.Made);
        var view = service.View(ws.WorkspaceId, "akira");
        Assert.Equal([("up", 2, true), ("down", 1, false), ("up2", 2, true)], view.Scenarios.Select(s => (s.Name, s.Shifts, s.Included)));
        Assert.Equal(["jpy_ois +10bp", "usdjpy -5%"], view.Shifts.Where(r => r.Scenario == "up2").Select(r => r.Shift!.Text));
        var priced = await Price(runs, store, service, ws);
        Assert.Equal(["Base market", "up", "up2"], priced.Input["scenarios"]!.AsArray().Select(s => s!["title"]!.GetValue<string>()));

        var before = ws.Revision;
        ws = service.RemoveScenario(ws.WorkspaceId, ws.Revision, "akira", "UP");

        Assert.Equal(before + 2, ws.Revision);
        Assert.Equal(["down", "up2"], service.View(ws.WorkspaceId, "akira").Scenarios.Select(s => s.Name));
        Assert.Equal("UnknownScenario", Assert.Throws<WorkspaceRefused>(() => service.RemoveScenario(ws.WorkspaceId, ws.Revision, "akira", "up")).Code);
        Assert.Contains("A scenario up2 exists", Assert.Throws<WorkspaceRefused>(() => service.DuplicateScenario(ws.WorkspaceId, ws.Revision, "akira", "down", "UP2")).Message);

        ws = service.ClearScenarios(ws.WorkspaceId, ws.Revision, "akira");
        view = service.View(ws.WorkspaceId, "akira");
        Assert.Empty(view.Scenarios);
        Assert.Empty(view.Shifts);
    }

    [Fact] // ADR-0010 §4: a row edits as a spreadsheet's does: a cell typed moves it, and a row whose cells are all cleared is removed
    public void A_row_edits_cell_by_cell_and_goes_when_every_cell_is_cleared()
    {
        var (service, _, _) = Priced();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 40);
        ws = Define(service, ws, "up", "jpy_ois +10bp; usdjpy -5%");
        var id = service.View(ws.WorkspaceId, "akira").Shifts[0].Id;

        ws = service.EditShifts(ws.WorkspaceId, ws.Revision, "akira", [new(id, 1, new Dictionary<string, string?> { ["factor"] = "10Y", ["size"] = "-2.5" })]);
        Assert.Equal(("10y", "-2.5bp"), (service.View(ws.WorkspaceId, "akira").Shifts[0].Factor, service.View(ws.WorkspaceId, "akira").Shifts[0].SizeText));
        // An index moved to one in another unit leaves the size, and the row says to type it again.
        ws = service.EditShifts(ws.WorkspaceId, ws.Revision, "akira", [new(id, 1, new Dictionary<string, string?> { ["index"] = "eurjpy", ["factor"] = "spot" })]);
        Assert.StartsWith("an FX shift is in %, and its size is in bp", service.View(ws.WorkspaceId, "akira").Shifts[0].Issue);

        ws = service.EditShifts(ws.WorkspaceId, ws.Revision, "akira",
            [new(id, 1, new Dictionary<string, string?> { ["scenario"] = null, ["category"] = null, ["index"] = "", ["factor"] = "", ["type"] = null, ["size"] = "", ["description"] = null })]);

        var view = service.View(ws.WorkspaceId, "akira");
        Assert.Equal(["usdjpy"], view.Shifts.Select(r => r.Index));
        Assert.Equal(1, view.Shifts[0].Row);
        // A scenario is no change of the portfolio's data, edited or not.
        Assert.Equal(0, view.Changes);
    }

    [Fact] // ADR-0010 §4: a ladder makes a scenario per size, the zero left out because Price prices the base market anyway
    public void A_ladder_makes_a_scenario_per_size_without_the_zero()
    {
        var made = ScenarioGenerators.Ladder("jpy_ois -50bp..+50bp step 10bp");

        Assert.Equal(["jpy_ois -50bp", "jpy_ois -40bp", "jpy_ois -30bp", "jpy_ois -20bp", "jpy_ois -10bp", "jpy_ois +10bp", "jpy_ois +20bp", "jpy_ois +30bp", "jpy_ois +40bp", "jpy_ois +50bp"],
            made.Select(m => m.Name));
        Assert.Equal(["usdjpy -10%", "usdjpy -5%", "usdjpy +5%", "usdjpy +10%"], ScenarioGenerators.Ladder("usdjpy -10% .. 10% step 5%").Select(m => m.Name));
        Assert.Equal(new Shift("IR", "jpy_ois", "5y", "abs", 2), Assert.Single(ScenarioGenerators.Ladder("JPY_OIS 5Y 1bp..2bp step 1bp")[1].Shifts));
    }

    [Fact] // ADR-0010 §4: a cross makes a scenario per combination, named from its shifts; one with a zero shifts the other factor alone
    public void A_cross_makes_a_scenario_per_combination_named_from_its_shifts()
    {
        var made = ScenarioGenerators.Cross("jpy_ois -50bp, 0, +50bp x usdjpy -10%, 0, +10%");

        Assert.Equal(["jpy_ois -50bp · usdjpy -10%", "jpy_ois -50bp", "jpy_ois -50bp · usdjpy +10%", "usdjpy -10%", "usdjpy +10%",
            "jpy_ois +50bp · usdjpy -10%", "jpy_ois +50bp", "jpy_ois +50bp · usdjpy +10%"], made.Select(m => m.Name));
        Assert.Equal(2, made[0].Shifts.Count);
    }

    [Theory] // ADR-0010 §4's note of 2026-11-09: over the limit, a step that does not divide, and two lists on one factor are refused by name
    [InlineData("ladder", "jpy_ois -50bp..+50bp step 1bp", "makes 100 scenarios, and a set is 49 at most")]
    [InlineData("ladder", "jpy_ois -50bp..+50bp step 15bp", "steps by 15bp, which does not divide -50bp..+50bp")]
    [InlineData("ladder", "jpy_ois +50bp..-50bp step 10bp", "a ladder rises from its first size to its last")]
    [InlineData("ladder", "jpy_ois -5%..+5% step 1%", "an IR shift is absolute, in bp")]
    [InlineData("ladder", "jpy_ois -50bp..+50bp", "needs a factor, a range and a step")]
    [InlineData("cross", "jpy_ois -10bp, 0, +10bp, +20bp, +30bp, +40bp, +50bp, +60bp x usdjpy -1%, 0, +1%, +2%, +3%, +4%, +5%", "makes 55 scenarios, and a set is 49 at most")]
    [InlineData("cross", "jpy_ois -10bp, +10bp x jpy_ois 5y -1bp, +1bp", "shifts jpy_ois 5y in two lists")]
    [InlineData("cross", "jpy_ois -10bp, +10bp", "needs two lists or more, separated by x")]
    [InlineData("cross", "jpy_ois -10bp, -10bp x usdjpy +1%", "lists -10bp twice for jpy_ois")]
    public void A_generator_that_cannot_make_its_set_is_refused_by_name(string generator, string text, string said)
    {
        var (service, _, _) = Priced();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 40);

        var refused = Assert.Throws<WorkspaceRefused>(() => service.GenerateScenarios(ws.WorkspaceId, ws.Revision, "akira", generator, text));

        Assert.Contains(said, refused.Message);
        Assert.Empty(service.View(ws.WorkspaceId, "akira").Scenarios);
    }

    [Fact] // ADR-0010 §4: a generated scenario replaces one of its name, and the answer says how many it made and replaced
    public void A_generated_scenario_replaces_one_of_its_name()
    {
        var (service, _, _) = Priced();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 40);
        ws = Define(service, ws, "jpy_ois +10bp", "jpy_ois 10y +10bp");

        var answer = service.GenerateScenarios(ws.WorkspaceId, ws.Revision, "akira", "ladder", "jpy_ois -10bp..+10bp step 10bp");

        Assert.Equal(["jpy_ois -10bp", "jpy_ois +10bp"], answer.Made);
        Assert.Equal(["jpy_ois +10bp"], answer.Replaced);
        var view = service.View(answer.State.WorkspaceId, "akira");
        Assert.Equal([("jpy_ois -10bp", "all"), ("jpy_ois +10bp", "all")], view.Shifts.Select(r => (r.Scenario!, r.Factor!)));
    }

    [Fact] // ADR-0010 §4: a scenario is included when it is made, even where one of its name was excluded before its rows went
    public void A_scenario_made_again_after_its_rows_went_is_included()
    {
        var (service, _, _) = Priced();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 40);
        ws = Define(service, ws, "down", "jpy_ois -10bp");
        ws = Define(service, ws, "flat", "usdjpy -5%");
        ws = service.IncludeScenario(ws.WorkspaceId, ws.Revision, "akira", "down", false);
        ws = service.IncludeScenario(ws.WorkspaceId, ws.Revision, "akira", "flat", false);
        var rows = service.View(ws.WorkspaceId, "akira").Shifts;

        // down's row cleared in the grid, flat's renamed: neither name has a row left.
        ws = service.EditShifts(ws.WorkspaceId, ws.Revision, "akira", [
            new ShiftEdit(rows[0].Id, 1, Columns().ToDictionary(c => c, _ => (string?)null)),
            new ShiftEdit(rows[1].Id, 2, new Dictionary<string, string?> { ["scenario"] = "flat2" })]);
        ws = Define(service, ws, "down", "jpy_ois -20bp");
        ws = service.DuplicateScenario(ws.WorkspaceId, ws.Revision, "akira", "flat2", "flat").State;

        Assert.Equal([("flat2", true), ("down", true), ("flat", true)], service.View(ws.WorkspaceId, "akira").Scenarios.Select(s => (s.Name, s.Included)));
        static IEnumerable<string> Columns() => PortfolioScenarios.Columns;
    }
}
