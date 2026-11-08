using System.Text.Json.Nodes;
using Workbench.Composition.Application;
using Workbench.Composition.Application.Workspaces;
using Workbench.Composition.Contracts;
using Workbench.Samples.Portfolio;
using Workbench.Samples.Xva;

namespace Workbench.Composition.Tests;

// The pricer's rules on the portfolio workspace service (ADR-0010), with XVA's registration. Names and figures are synthetic.
public class PricerTests
{
    static readonly DateOnly Day1 = new(2026, 10, 26);

    static PortfolioWorkspaces Service(IMeasureModule? xva = null) => Priced(xva).Service;

    static (PortfolioWorkspaces Service, RunService Runs, IDataStore Store, WorkspaceService Workspaces) Priced(IMeasureModule? xva = null)
    {
        var workspaces = new WorkspaceService(new Dictionary<string, IEditRules> { [PortfolioEditRules.App] = new PortfolioEditRules() }, TimeProvider.System);
        var store = new InMemoryDataStore();
        var runs = new RunService(new CompositionRegistry([new PortfolioModule(), new XvaModule()]), store, TimeProvider.System);
        return (new PortfolioWorkspaces(workspaces, store, runs, new MeasureRegister([xva ?? new XvaMeasures()])), runs, store, workspaces);
    }

    /// <summary>Prices the revision and reads its accepted input and the XVA module's rows.</summary>
    static async Task<(JsonNode Input, IReadOnlyList<JsonNode> Rows)> Price(RunService runs, IDataStore store, PortfolioWorkspaces service, WorkspaceState ws)
    {
        var accepted = Assert.IsType<SubmissionResult.Accepted>(service.Price(ws.WorkspaceId, ws.Revision, "akira", Guid.NewGuid().ToString()));
        await runs.Completion(accepted.Run.RunId);
        var run = runs.Find(accepted.Run.RunId)!;
        Assert.Equal(RunStatus.Succeeded, run.Status);
        var input = store.ReadRequired(((DataReferenceBinding)run.Workflow.Steps[0].Bindings["acceptedInput"]).Reference);
        return (input, [.. store.ReadRequired(run.Outputs[XvaMeasures.Module])["rows"]!.AsArray().Select(r => r!)]);
    }

    static JsonNode Row(IReadOnlyList<JsonNode> rows, string metric) => rows.Single(r => r["metric"]!.GetValue<string>() == metric);

    /// <summary>XVA's registration with a validation that blocks CVA, as a missing CDS will (ADR-0010 §11).</summary>
    sealed class CvaBlocked : IMeasureModule
    {
        readonly XvaMeasures xva = new();
        public string Id => xva.Id;
        public IReadOnlyList<MeasureDefinition> Measures => xva.Measures;
        public JsonObject Input(WorkspaceState state) => xva.Input(state);
        public IReadOnlyList<IMembershipRule> Membership(WorkspaceState state) => xva.Membership(state);
        public IReadOnlyList<MeasureRefusal> Validate(WorkspaceState state, IReadOnlyList<string> selected) =>
            selected.Contains(XvaMeasures.Cva) ? [new(XvaMeasures.Cva, "MissingCds", "No CDS curve for ABC/LE-01.")] : [];
        public WorkflowStep Step(TypedDataRef acceptedInput) => xva.Step(acceptedInput);
    }

    [Fact] // ADR-0010 §10 (DEFAULT): an owner's first workspace selects every registered measure
    public void A_first_workspace_selects_every_measure()
    {
        var service = Service();

        var ws = service.Create("akira", "ABC/LE-01", Day1, 10);

        Assert.Equal(["cva", "fva"], service.View(ws.WorkspaceId, "akira").SelectedMeasures);
    }

    [Fact] // ADR-0010 §10: the selection belongs to the workspace, so a counterparty change keeps it
    public void The_selection_survives_a_counterparty_change()
    {
        var service = Service();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 10);
        ws = service.SetMeasures(ws.WorkspaceId, ws.Revision, "akira", ["fva"]);

        var drop = service.PreviewCounterpartyChange(ws.WorkspaceId, "akira");
        var (moved, carried) = service.ChangeCounterparty(ws.WorkspaceId, ws.Revision, "akira", "ABC/LE-02", drop.Count);

        Assert.Equal(0, drop.Count);
        Assert.Equal(["fva"], service.View(moved.WorkspaceId, "akira").SelectedMeasures);
        // The summaries count the edits to the portfolio's data; the selection is the page's choice, shown on its own.
        Assert.Equal(0, carried.Carried);
        Assert.Equal(0, service.View(moved.WorkspaceId, "akira").Overrides);
    }

    [Fact] // ADR-0010 §10: a measure that cannot be priced blocks only itself, and says why
    public async Task A_refused_measure_says_why_and_the_others_are_priced()
    {
        var (service, runs, store, _) = Priced(new CvaBlocked());
        var ws = service.Create("akira", "ABC/LE-01", Day1, 10);

        var accepted = Assert.IsType<SubmissionResult.Accepted>(service.Price(ws.WorkspaceId, ws.Revision, "akira", "k1"));
        await runs.Completion(accepted.Run.RunId);
        var run = runs.Find(accepted.Run.RunId)!;
        var rows = store.ReadRequired(run.Outputs[XvaMeasures.Module])["rows"]!.AsArray().Select(r => r!).ToList();

        Assert.Equal(RunStatus.Succeeded, run.Status);
        var cva = rows.Where(r => r["measure"]!.GetValue<string>() == "cva").ToList();
        Assert.Equal(2, cva.Count);
        Assert.All(cva, r => Assert.Equal(("refused", "No CDS curve for ABC/LE-01."), (r["afterStatus"]!.GetValue<string>(), r["reason"]!.GetValue<string>())));
        Assert.All(cva, r => Assert.Null(r["after"]));
        var fva = Assert.Single(rows, r => r["measure"]!.GetValue<string>() == "fva");
        Assert.Equal("priced", fva["afterStatus"]!.GetValue<string>());
    }

    [Fact] // ADR-0010 §5, §8: each CSA says whether FVA counts it imperfect, and why, at the value date's FX
    public void Each_csa_says_whether_it_is_imperfect_and_why()
    {
        var service = Service();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 10);

        var csas = service.View(ws.WorkspaceId, "akira").Csas.ToDictionary(c => c.CsaId, c => c.Membership![XvaMeasures.Fva]);

        Assert.Equal(new Membership(false, "CSA-A: daily, MTA+threshold 0.27mm GBP"), csas["CSA-A"]);
        Assert.Equal(new Membership(true, "CSA-B: counterparty MTA+threshold 1.19mm GBP over 1.0mm"), csas["CSA-B"]);
    }

    [Fact] // ADR-0010 §5: raising a threshold past 1mm GBP moves the CSA's trades into FVA, and the next Price counts them
    public async Task Raising_a_threshold_past_the_limit_moves_the_trades_into_fva()
    {
        var (service, runs, store, _) = Priced();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 40);
        var (_, plain) = await Price(runs, store, service, ws);

        ws = service.Edit(ws.WorkspaceId, ws.Revision, "akira", "csa:CSA-A", "counterpartyThreshold", 200_000_000m);
        var (input, raised) = await Price(runs, store, service, ws);

        var onA = input["trades"]!.AsArray().Where(t => t!["csa"]!.GetValue<string>() == "CSA-A").ToList();
        Assert.NotEmpty(onA);
        Assert.All(onA, t => Assert.Equal((true, "CSA-A: counterparty MTA+threshold 1.33mm GBP over 1.0mm"),
            (t!["membership"]!["fva"]!["in"]!.GetValue<bool>(), t["membership"]!["fva"]!["reason"]!.GetValue<string>())));
        var afterA = onA.Count(t => t!["after"]!.GetValue<bool>());
        Assert.Equal(Row(plain, "fva")["tradesAfter"]!.GetValue<int>() + afterA, Row(raised, "fva")["tradesAfter"]!.GetValue<int>());
        // CVA prices every trade either way; only FVA's portfolio moved.
        Assert.Equal(Row(plain, "cva")["tradesAfter"]!.GetValue<int>(), Row(raised, "cva")["tradesAfter"]!.GetValue<int>());
        Assert.Equal("fva-imperfect-csa/1 limit 1.0mm GBP", input["membership"]!["fva"]!["version"]!.GetValue<string>());
    }

    [Fact] // ADR-0010 §5: an uncollateralised trade is outside FVA, and adds 0 to it
    public async Task An_uncollateralised_trade_is_outside_fva_and_adds_nothing()
    {
        var (service, runs, store, _) = Priced();
        var ws = service.Create("akira", "XYZ/LE-03", Day1, 10);

        var (input, rows) = await Price(runs, store, service, ws);

        Assert.All(input["trades"]!.AsArray(), t => Assert.Equal((false, "nocsa_MA-003: uncollateralised, FVA 0"),
            (t!["membership"]!["fva"]!["in"]!.GetValue<bool>(), t["membership"]!["fva"]!["reason"]!.GetValue<string>())));
        var fva = Row(rows, "fva");
        Assert.Equal(("priced", 0m, 0), (fva["afterStatus"]!.GetValue<string>(), fva["after"]!.GetValue<decimal>(), fva["tradesAfter"]!.GetValue<int>()));
        Assert.True(Row(rows, "cva")["tradesAfter"]!.GetValue<int>() > 0);
    }

    [Fact] // ADR-0010 §8, XV15: the CVA recovery is the netting set's, and empty stays empty
    public void The_cva_recovery_is_the_netting_sets_and_empty_stays_empty()
    {
        var service = Service();
        var ws = service.Create("akira", "ABC/LE-02", Day1, 10);
        Assert.Equal(0.35m, service.View(ws.WorkspaceId, "akira").Credit.CvaRecovery);

        ws = service.Edit(ws.WorkspaceId, ws.Revision, "akira", "netting:NS-002", "cvaRecovery", null);

        var credit = service.View(ws.WorkspaceId, "akira").Credit;
        Assert.Equal((null, Ownership.User, 0.4m), (credit.CvaRecovery, credit.CvaRecoveryOwner, credit.CdsRecovery));
        Assert.Equal("NotEditable", Assert.Throws<WorkspaceRefused>(() => service.Edit(ws.WorkspaceId, ws.Revision, "akira", "credit:ABC/LE-02", "cvaRecovery", 0.3m)).Code);
    }

    [Fact] // ADR-0010 §8: a recovery set on the counterparty before it moved to the netting set still applies
    public void A_recovery_set_on_the_counterparty_before_still_applies()
    {
        var (service, _, _, workspaces) = Priced();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 10);
        var source = new PortfolioSourceVersion("ABC/LE-01", Day1, 10);

        // As a workspace saved before this slice holds it: the edit is on the counterparty's credit.
        ws = workspaces.Set(ws.WorkspaceId, ws.Revision, "akira", PortfolioEditRules.Target(source, "credit:ABC/LE-01", "cvaRecovery"), JsonValue.Create(0.3m));
        Assert.Equal(0.3m, service.View(ws.WorkspaceId, "akira").Credit.CvaRecovery);

        ws = service.Edit(ws.WorkspaceId, ws.Revision, "akira", "netting:NS-001", "cvaRecovery", 0.25m);
        Assert.Equal(0.25m, service.View(ws.WorkspaceId, "akira").Credit.CvaRecovery);
    }

    [Theory] // ADR-0010 §8: CSA terms are typed and checked on the server like any other input
    [InlineData("frequency", "\"hourly\"")]
    [InlineData("counterpartyThreshold", "-1")]
    [InlineData("selfCurrency", "\"CHF\"")]
    [InlineData("oneWay", "\"yes\"")]
    [InlineData("type", "\"IM\"")]
    public void A_csa_term_that_does_not_read_is_refused(string field, string json)
    {
        var service = Service();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 10);

        var refused = Assert.Throws<WorkspaceRefused>(() => service.Edit(ws.WorkspaceId, ws.Revision, "akira", "csa:CSA-A", field, JsonNode.Parse(json)));

        Assert.Contains(refused.Code, new[] { "InvalidValue", "NotEditable" });
    }

    [Fact] // ADR-0010 §6: clearing B for every existing trade is one edit, and a row ticked afterwards stays ticked
    public void Clearing_before_for_a_grid_is_one_edit_and_a_later_row_edit_wins()
    {
        var service = Service();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 20);
        var log = ws.Log.Count;

        ws = service.Edit(ws.WorkspaceId, ws.Revision, "akira", "grid:existing", "before", false);
        Assert.Equal(log + 1, ws.Log.Count);
        var existing = service.Page(ws.WorkspaceId, "akira", 0, 100, [], "existing").Rows;
        Assert.All(existing, t => Assert.False(t.Before));
        Assert.All(service.Page(ws.WorkspaceId, "akira", 0, 100, [], "new").Rows, t => Assert.True(t.IsNew));

        var first = existing[0].TradeId;
        ws = service.Edit(ws.WorkspaceId, ws.Revision, "akira", $"trade:{first}", "before", true);

        var rows = service.Page(ws.WorkspaceId, "akira", 0, 100, [], "existing").Rows;
        Assert.True(rows.Single(t => t.TradeId == first).Before);
        Assert.All(rows.Where(t => t.TradeId != first), t => Assert.False(t.Before));
        Assert.Equal(1, service.View(ws.WorkspaceId, "akira").InBefore);
    }

    [Fact] // ADR-0010 §6: the grid's edit made after a row's sets that row too
    public void A_grid_edit_after_a_row_edit_sets_that_row()
    {
        var service = Service();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 5);
        ws = service.Edit(ws.WorkspaceId, ws.Revision, "akira", "trade:DRAFT-1", "after", false);

        ws = service.Edit(ws.WorkspaceId, ws.Revision, "akira", "grid:new", "after", true);

        Assert.All(service.Page(ws.WorkspaceId, "akira", 0, 10, [], "new").Rows, t => Assert.True(t.After));
    }

    [Fact] // ADR-0010 §3, §7: with every B box clear, Price prices After alone: Before not priced, no Diff, never 0
    public async Task With_every_before_clear_price_prices_after_alone()
    {
        var (service, runs, store, _) = Priced();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 20);
        ws = service.Edit(ws.WorkspaceId, ws.Revision, "akira", "grid:existing", "before", false);
        Assert.Equal(0, service.View(ws.WorkspaceId, "akira").InBefore);

        var (_, rows) = await Price(runs, store, service, ws);

        Assert.All(rows, r =>
        {
            Assert.Equal(("notPriced", "priced"), (r["beforeStatus"]!.GetValue<string>(), r["afterStatus"]!.GetValue<string>()));
            Assert.Null(r["before"]);
            Assert.Null(r["diff"]);
        });
    }

    [Fact] // ADR-0010 §3: a Price with no trade on either side is refused by name
    public void A_price_with_no_trade_on_either_side_is_refused()
    {
        var service = Service();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 0);
        ws = service.Edit(ws.WorkspaceId, ws.Revision, "akira", "grid:new", "after", false);

        var refused = Assert.Throws<WorkspaceRefused>(() => service.Price(ws.WorkspaceId, ws.Revision, "akira", "k1"));

        Assert.Equal("NothingToPrice", refused.Code);
    }

    [Fact] // ADR-0010 §6: an existing trade shows its official PV from the source; a new trade has none
    public void An_existing_trade_shows_the_sources_official_pv()
    {
        var service = Service();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 10);
        var source = new PortfolioSourceVersion("ABC/LE-01", Day1, 10);

        var existing = service.Page(ws.WorkspaceId, "akira", 0, 100, [], "existing").Rows;

        Assert.All(existing, t => Assert.Equal(source.Trade(t.TradeId)!.OfficialPv, t.OfficialPv));
        Assert.All(existing, t => Assert.NotNull(t.OfficialPv));
        Assert.All(service.Page(ws.WorkspaceId, "akira", 0, 10, [], "new").Rows, t => Assert.Null(t.OfficialPv));
    }

    [Fact] // ADR-0010 §6: a grid nobody named is refused, never read as all
    public void An_unknown_grid_is_refused()
    {
        var service = Service();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 10);

        Assert.Equal("UnknownGrid", Assert.Throws<WorkspaceRefused>(() => service.Page(ws.WorkspaceId, "akira", 0, 10, [], "old")).Code);
    }

    [Fact] // ADR-0010 §2: a CSA curve edit marks the CSA and every trade that follows it, not a trade the user set to its own value
    public void A_csa_curve_edit_marks_the_csa_and_the_trades_that_follow_it()
    {
        var service = Service();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 20);
        var onA = service.Page(ws.WorkspaceId, "akira", 0, 100, [], "existing").Rows.Where(t => t.Csa == "CSA-A").Select(t => t.TradeId).ToList();
        Assert.True(onA.Count >= 2);
        // The user sets one trade's curve to the value it already has; the CSA edit then no longer reaches it.
        ws = service.Edit(ws.WorkspaceId, ws.Revision, "akira", $"trade:{onA[0]}", "curve", "jpy_ois");

        ws = service.Edit(ws.WorkspaceId, ws.Revision, "akira", "csa:CSA-A", "curve", "usd_sofr");

        var rows = service.Page(ws.WorkspaceId, "akira", 0, 100, [], "existing").Rows.ToDictionary(t => t.TradeId);
        Assert.Null(rows[onA[0]].Original);
        Assert.All(onA.Skip(1), id => Assert.Equal("jpy_ois", rows[id].Original!["curve"]!.GetValue<string>()));
        var csa = service.View(ws.WorkspaceId, "akira").Csas.Single(c => c.CsaId == "CSA-A");
        Assert.Equal(["curve"], csa.Original!.Keys);
    }

    [Fact] // ADR-0010 §2: the test is the value: a value edited back to Original is not marked
    public void A_value_edited_back_to_original_is_not_marked()
    {
        var service = Service();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 5);
        var draft = service.Page(ws.WorkspaceId, "akira", 0, 10, [], "new").Rows.Single(t => t.TradeId == "DRAFT-1");

        ws = service.Edit(ws.WorkspaceId, ws.Revision, "akira", "trade:DRAFT-1", "notional", 9_000_000_000m);
        Assert.Equal(draft.Notional, service.Page(ws.WorkspaceId, "akira", 0, 10, [], "new").Rows.Single(t => t.TradeId == "DRAFT-1").Original!["notional"]!.GetValue<decimal>());
        ws = service.Edit(ws.WorkspaceId, ws.Revision, "akira", "trade:DRAFT-1", "notional", draft.Notional);
        ws = service.Edit(ws.WorkspaceId, ws.Revision, "akira", "csa:CSA-B", "counterpartyThreshold", 1_000_000.00m);

        Assert.Null(service.Page(ws.WorkspaceId, "akira", 0, 10, [], "new").Rows.Single(t => t.TradeId == "DRAFT-1").Original);
        Assert.Null(service.View(ws.WorkspaceId, "akira").Csas.Single(c => c.CsaId == "CSA-B").Original);
    }

    [Fact] // ADR-0010 §2: B and A are shown by their boxes and never marked
    public void Before_and_after_are_never_marked()
    {
        var service = Service();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 10);

        ws = service.Edit(ws.WorkspaceId, ws.Revision, "akira", "grid:existing", "before", false);
        ws = service.Edit(ws.WorkspaceId, ws.Revision, "akira", "trade:DRAFT-1", "after", false);

        Assert.All(service.Page(ws.WorkspaceId, "akira", 0, 100, [], null).Rows, t => Assert.Null(t.Original));
    }

    [Fact] // ADR-0010 §2, §8: the netting set's CVA recovery is marked against the source's, empty included
    public void The_cva_recovery_is_marked_against_the_sources()
    {
        var service = Service();
        var ws = service.Create("akira", "ABC/LE-02", Day1, 5);

        ws = service.Edit(ws.WorkspaceId, ws.Revision, "akira", "netting:NS-002", "cvaRecovery", null);

        Assert.Equal(0.35m, service.View(ws.WorkspaceId, "akira").Credit.Original!["cvaRecovery"]!.GetValue<decimal>());
        ws = service.Edit(ws.WorkspaceId, ws.Revision, "akira", "netting:NS-002", "cvaRecovery", 0.35m);
        Assert.Null(service.View(ws.WorkspaceId, "akira").Credit.Original);
    }
}
