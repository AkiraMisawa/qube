using System.Text.Json;
using System.Text.Json.Nodes;
using Workbench.Composition.Application;
using Workbench.Composition.Application.Workspaces;
using Workbench.Composition.Contracts;
using Workbench.Samples.Ccr;
using Workbench.Samples.Portfolio;
using Workbench.Samples.Rates;
using Workbench.Samples.Simm;
using Workbench.Samples.Xva;

namespace Workbench.Composition.Tests;

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
        return (new PortfolioWorkspaces(workspaces, store, runs, register, options), runs, store);
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
        ws = service.SetMeasures(ws.WorkspaceId, ws.Revision, "akira", ["cva", "fva"]);
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
        ws = service.SetMeasures(ws.WorkspaceId, ws.Revision, "akira", ["cva", "fva"]);
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
        ws = service.SetMeasures(ws.WorkspaceId, ws.Revision, "akira", ["cva", "fva"]);
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
        ws = service.SetMeasures(ws.WorkspaceId, ws.Revision, "akira", ["cva"]);
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
        ws = service.SetMeasures(ws.WorkspaceId, ws.Revision, "akira", ["cva"]);
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
        ws = service.SetMeasures(ws.WorkspaceId, ws.Revision, "akira", ["simm.spotIm"]);
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
        ws = service.SetMeasures(ws.WorkspaceId, ws.Revision, "akira", ["ccr.spotCapital"]);
        ws = Define(service, ws, "rates", "jpy_ois +100bp");
        ws = Define(service, ws, "fx", "usdjpy +10%");
        ws = Define(service, ws, "eur", "eur_estr +100bp");

        var priced = await Price(runs, store, service, ws);

        Assert.NotEqual(After(priced, "base", "ead"), After(priced, "s1", "ead"));
        Assert.NotEqual(After(priced, "base", "ead"), After(priced, "s2", "ead"));
        Assert.Equal(After(priced, "base", "ead"), After(priced, "s3", "ead"));
        Assert.Equal(["base", "s1", "s2", "s3"], priced.Traces.Where(t => t.Measure == CcrMeasures.SpotCapital && t.Side == "after").Select(t => t.Scenario));
    }
}
