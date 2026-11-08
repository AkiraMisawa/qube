using System.Text.Json.Nodes;
using Workbench.Composition.Application;
using Workbench.Composition.Application.Workspaces;
using Workbench.Composition.Contracts;
using Workbench.Samples.Ccr;
using Workbench.Samples.Portfolio;
using Workbench.Samples.Xva;

namespace Workbench.Composition.Tests;

// The pricer's rules on the portfolio workspace service (ADR-0010), with XVA's and SA-CCR's registrations, as the
// application registers them. Names and figures are synthetic.
public class PricerTests
{
    static readonly DateOnly Day1 = new(2026, 10, 26);

    static PortfolioWorkspaces Service(IMeasureModule? xva = null) => Priced(xva).Service;

    static (PortfolioWorkspaces Service, RunService Runs, IDataStore Store, WorkspaceService Workspaces) Priced(IMeasureModule? xva = null)
    {
        var workspaces = new WorkspaceService(new Dictionary<string, IEditRules> { [PortfolioEditRules.App] = new PortfolioEditRules() }, TimeProvider.System);
        var store = new InMemoryDataStore();
        var runs = new RunService(new CompositionRegistry([new PortfolioModule(), new XvaModule(), new CcrModule()]), store, TimeProvider.System);
        return (new PortfolioWorkspaces(workspaces, store, runs, new MeasureRegister([xva ?? new XvaMeasures(Pv01Provider.Simulated(TimeSpan.Zero)), new CcrMeasures()])), runs, store, workspaces);
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
        readonly XvaMeasures xva = new(Pv01Provider.Simulated(TimeSpan.Zero));
        public string Id => xva.Id;
        public IReadOnlyList<MeasureDefinition> Measures => xva.Measures;
        public JsonObject Input(WorkspaceState state) => xva.Input(state);
        public IReadOnlyList<IMembershipRule> Membership(WorkspaceState state) => xva.Membership(state);
        public IReadOnlyList<MeasureRefusal> Validate(WorkspaceState state, IReadOnlyList<string> selected) => throw new InvalidOperationException("Price gives the trades.");
        public IReadOnlyList<MeasureRefusal> Validate(WorkspaceState state, IReadOnlyList<string> selected, IReadOnlyList<EffectiveTrade> priced) =>
            selected.Contains(XvaMeasures.Cva) ? [new(XvaMeasures.Cva, "MissingCds", "No CDS curve for ABC/LE-01.")] : xva.Validate(state, selected, priced);
        public IReadOnlyList<TradeColumn> TradeColumns => xva.TradeColumns;
        public IReadOnlyList<IReadOnlyDictionary<string, TradeCell>?> Cells(WorkspaceState state, IReadOnlyList<EffectiveTrade> trades) => xva.Cells(state, trades);
        public WorkflowStep Step(TypedDataRef acceptedInput) => xva.Step(acceptedInput);
    }

    /// <summary>A new trade's cell in one of XVA's columns, as the New grid reads it now.</summary>
    static TradeCell Cell(PortfolioWorkspaces service, WorkspaceState ws, string tradeId, string column) =>
        service.Page(ws.WorkspaceId, "akira", 0, 10, [], "new").Rows.Single(t => t.TradeId == tradeId).Cells![XvaMeasures.Field(column)];

    /// <summary>The cell once every PV01 asked by reading it has answered.</summary>
    static async Task<TradeCell> Settled(PortfolioWorkspaces service, Pv01Provider pv01, WorkspaceState ws, string tradeId, string column)
    {
        Cell(service, ws, tradeId, column);
        await pv01.Settled();
        return Cell(service, ws, tradeId, column);
    }

    [Fact] // ADR-0010 §10 (DEFAULT): an owner's first workspace selects every registered measure
    public void A_first_workspace_selects_every_measure()
    {
        var service = Service();

        var ws = service.Create("akira", "ABC/LE-01", Day1, 10);

        Assert.Equal(["cva", "fva", "kva", "ccr.spotCapital"], service.View(ws.WorkspaceId, "akira").SelectedMeasures);
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
        Assert.Equal(3, cva.Count);
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

        Assert.Equal(new Membership(false, "CSA-A perfect: daily, MTA+threshold 0.27mm GBP"), csas["CSA-A"]);
        Assert.Equal(new Membership(true, "CSA-B imperfect: counterparty MTA+threshold 1.19mm GBP over 1.0mm"), csas["CSA-B"]);
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
        Assert.All(onA, t => Assert.Equal((true, "CSA-A imperfect: counterparty MTA+threshold 1.33mm GBP over 1.0mm"),
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

    [Fact] // ADR-0010 §10, §11: CVA's recovery is a field of the Netting tab only while CVA is selected, and keeps its value hidden
    public void The_cva_recovery_shows_only_while_cva_is_selected()
    {
        var service = Service();
        var ws = service.Create("akira", "ABC/LE-02", Day1, 10);
        Assert.Equal([new MeasureInput("netting", "cvaRecovery", XvaMeasures.Cva)], service.View(ws.WorkspaceId, "akira").Inputs);
        ws = service.Edit(ws.WorkspaceId, ws.Revision, "akira", "netting:NS-002", "cvaRecovery", 0.3m);

        ws = service.SetMeasures(ws.WorkspaceId, ws.Revision, "akira", ["fva"]);
        var hidden = service.View(ws.WorkspaceId, "akira");

        Assert.Empty(hidden.Inputs);
        Assert.Equal(0.3m, hidden.Credit.CvaRecovery);
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

    [Fact] // ADR-0010 §6: a new trade's PV01 is asked when the trade is first read, pending until it answers, never 0
    public async Task A_new_trades_pv01_is_pending_and_then_its_figure()
    {
        var answer = new TaskCompletionSource<decimal>();
        var pv01 = new Pv01Provider((_, _) => answer.Task);
        var service = Service(new XvaMeasures(pv01));
        var ws = service.Create("akira", "ABC/LE-01", Day1, 5);

        var pending = Cell(service, ws, "DRAFT-1", XvaMeasures.Pv01);
        Assert.Equal((TradeCell.Pending, null, false), (pending.State, pending.Value, pending.Editable));
        Assert.Equal(3, service.View(ws.WorkspaceId, "akira").PendingCells);
        // XVA's three, then SA-CCR's classification (ADR-0013 §6).
        Assert.Equal(["xva.pv01", "xva.margin", "xva.pvAdj", "ccr.class"], service.View(ws.WorkspaceId, "akira").TradeColumns.Select(c => c.Field));

        answer.SetResult(-1_500m);
        await pv01.Settled();

        Assert.Equal(-1_500m, Cell(service, ws, "DRAFT-1", XvaMeasures.Pv01).Value!.GetValue<decimal>());
        Assert.Equal(0, service.View(ws.WorkspaceId, "akira").PendingCells);
        // An existing trade has none of XVA's columns: its official PV is the source's.
        Assert.All(service.Page(ws.WorkspaceId, "akira", 0, 10, [], "existing").Rows, t => Assert.Equal(["ccr.class"], t.Cells!.Keys));
    }

    [Fact] // ADR-0010 §6: Margin 2bp on a PV01 of -1,500 is a PV Adj of -3,000, read-only and saying it came from Margin
    public async Task Margin_gives_pv_adj_and_pv_adj_is_read_only_then()
    {
        var pv01 = new Pv01Provider((_, _) => Task.FromResult(-1_500m));
        var service = Service(new XvaMeasures(pv01));
        var ws = service.Create("akira", "ABC/LE-01", Day1, 5);

        ws = service.Edit(ws.WorkspaceId, ws.Revision, "akira", "trade:DRAFT-1", "xva.margin", 2m);
        var adjustment = await Settled(service, pv01, ws, "DRAFT-1", XvaMeasures.PvAdj);

        Assert.Equal((-3_000m, TradeCell.Shown, false), (adjustment.Value!.GetValue<decimal>(), adjustment.State, adjustment.Editable));
        Assert.Equal("From Margin: 2 bp × PV01 -1,500", adjustment.Note);
        var refused = Assert.Throws<WorkspaceRefused>(() => service.Edit(ws.WorkspaceId, ws.Revision, "akira", "trade:DRAFT-1", "xva.pvAdj", 100m));
        Assert.Equal(("NotEditable", "PV Adj of DRAFT-1 is read-only: From Margin: 2 bp × PV01 -1,500"), (refused.Code, refused.Message));
    }

    [Fact] // ADR-0010 §2, §6: a PV Adj that Margin filled differs from its empty Original; PV01, a result, is never marked
    public async Task A_pv_adj_filled_from_margin_differs_from_original()
    {
        var pv01 = new Pv01Provider((_, _) => Task.FromResult(-1_500m));
        var service = Service(new XvaMeasures(pv01));
        var ws = service.Create("akira", "ABC/LE-01", Day1, 5);

        ws = service.Edit(ws.WorkspaceId, ws.Revision, "akira", "trade:DRAFT-1", "xva.margin", 2m);
        await Settled(service, pv01, ws, "DRAFT-1", XvaMeasures.PvAdj);

        var row = service.Page(ws.WorkspaceId, "akira", 0, 10, [], "new").Rows.Single(t => t.TradeId == "DRAFT-1");
        Assert.Equal(["xva.margin", "xva.pvAdj"], row.Original!.Keys.Where(k => k.StartsWith("xva.", StringComparison.Ordinal)).Order());
        Assert.All(row.Original.Where(o => o.Key.StartsWith("xva.", StringComparison.Ordinal)), o => Assert.Null(o.Value));
    }

    [Fact] // ADR-0010 §6, §2: with Margin empty PV Adj is typed, marked against an empty Original, and Margin wins while entered
    public async Task With_margin_empty_pv_adj_is_typed()
    {
        var pv01 = new Pv01Provider((_, _) => Task.FromResult(-1_500m));
        var service = Service(new XvaMeasures(pv01));
        var ws = service.Create("akira", "ABC/LE-01", Day1, 5);

        ws = service.Edit(ws.WorkspaceId, ws.Revision, "akira", "trade:DRAFT-1", "xva.pvAdj", 1_234.5m);
        var typed = await Settled(service, pv01, ws, "DRAFT-1", XvaMeasures.PvAdj);
        Assert.Equal((1_234.5m, true), (typed.Value!.GetValue<decimal>(), typed.Editable));
        var row = service.Page(ws.WorkspaceId, "akira", 0, 10, [], "new").Rows.Single(t => t.TradeId == "DRAFT-1");
        Assert.True(row.Original!.ContainsKey("xva.pvAdj"));
        Assert.Null(row.Original["xva.pvAdj"]);

        ws = service.Edit(ws.WorkspaceId, ws.Revision, "akira", "trade:DRAFT-1", "xva.margin", 2m);
        Assert.Equal(-3_000m, Cell(service, ws, "DRAFT-1", XvaMeasures.PvAdj).Value!.GetValue<decimal>());
        ws = service.Edit(ws.WorkspaceId, ws.Revision, "akira", "trade:DRAFT-1", "xva.margin", null);
        Assert.Equal(1_234.5m, Cell(service, ws, "DRAFT-1", XvaMeasures.PvAdj).Value!.GetValue<decimal>());
    }

    [Fact] // ADR-0010 §6: Price freezes each PV Adj with the PV01 and Margin it came from; a later PV01 does not change it
    public async Task Price_freezes_pv_adj_with_its_pv01_and_margin()
    {
        var pv01 = new Pv01Provider((request, _) => Task.FromResult(request.Notional == 9_000_000_000m ? -1_800m : -1_500m));
        var (service, runs, store, _) = Priced(new XvaMeasures(pv01));
        var ws = service.Create("akira", "ABC/LE-01", Day1, 5);
        ws = service.Edit(ws.WorkspaceId, ws.Revision, "akira", "trade:DRAFT-1", "xva.margin", 2m);
        await Settled(service, pv01, ws, "DRAFT-1", XvaMeasures.Pv01);

        var accepted = Assert.IsType<SubmissionResult.Accepted>(service.Price(ws.WorkspaceId, ws.Revision, "akira", "k1"));
        await runs.Completion(accepted.Run.RunId);
        ws = service.Edit(ws.WorkspaceId, ws.Revision, "akira", "trade:DRAFT-1", "notional", 9_000_000_000m);
        Assert.Equal(-1_800m, (await Settled(service, pv01, ws, "DRAFT-1", XvaMeasures.Pv01)).Value!.GetValue<decimal>());

        var run = runs.Find(accepted.Run.RunId)!;
        var input = store.ReadRequired(((DataReferenceBinding)run.Workflow.Steps[0].Bindings["acceptedInput"]).Reference);
        var cells = input["trades"]!.AsArray().Single(t => t!["tradeId"]!.GetValue<string>() == "DRAFT-1")!["cells"]!;
        Assert.Equal((-1_500m, 2m, -3_000m), (cells["xva.pv01"]!["value"]!.GetValue<decimal>(), cells["xva.margin"]!["value"]!.GetValue<decimal>(), cells["xva.pvAdj"]!["value"]!.GetValue<decimal>()));
        Assert.Equal("From Margin: 2 bp × PV01 -1,500", cells["xva.pvAdj"]!["note"]!.GetValue<string>());
        Assert.Equal(RunStatus.Succeeded, run.Status);
    }

    [Fact] // ADR-0010 §6 (DEFAULT): with Margin entered and PV01 pending, XVA's measures are refused by name at Price
    public async Task Margin_with_pv01_pending_refuses_xvas_measures()
    {
        var (service, runs, store, _) = Priced(new XvaMeasures(new Pv01Provider((_, _) => new TaskCompletionSource<decimal>().Task)));
        var ws = service.Create("akira", "ABC/LE-01", Day1, 5);
        ws = service.Edit(ws.WorkspaceId, ws.Revision, "akira", "trade:DRAFT-2", "xva.margin", 2m);

        var (_, all) = await Price(runs, store, service, ws);

        // XVA's own measures; spot capital, which reads no PV Adj, stands (ADR-0010 §10: a refusal blocks its own).
        var rows = all.Where(r => r["measure"]!.GetValue<string>() != CcrMeasures.SpotCapital).ToList();
        Assert.Equal(5, rows.Count);
        Assert.All(all.Except(rows), r => Assert.Equal("priced", r["afterStatus"]!.GetValue<string>()));
        Assert.All(rows, r => Assert.Equal(("refused", "PV Adj of DRAFT-2 is unknown: From Margin: PV01 is pending."),
            (r["afterStatus"]!.GetValue<string>(), r["reason"]!.GetValue<string>())));
        Assert.All(rows, r => Assert.Null(r["after"]));
    }

    [Fact] // XV13, ADR-0010 §6: a retry of Price with its key is the same Run though a PV01 answered between the two
    public async Task A_retried_price_is_the_same_run_after_a_pv01_answered()
    {
        var answer = new TaskCompletionSource<decimal>();
        var pv01 = new Pv01Provider((_, _) => answer.Task);
        var (service, runs, _, _) = Priced(new XvaMeasures(pv01));
        var ws = service.Create("akira", "ABC/LE-01", Day1, 5);
        Assert.Equal(TradeCell.Pending, Cell(service, ws, "DRAFT-1", XvaMeasures.Pv01).State);

        var first = Assert.IsType<SubmissionResult.Accepted>(service.Price(ws.WorkspaceId, ws.Revision, "akira", "k1"));
        answer.SetResult(-1_500m);
        await pv01.Settled();
        var retry = service.Price(ws.WorkspaceId, ws.Revision, "akira", "k1");

        Assert.Equal(first.Run.RunId, Assert.IsType<SubmissionResult.Accepted>(retry).Run.RunId);
        await runs.Completion(first.Run.RunId);
        Assert.Equal([first.Run.RunId], service.View(ws.WorkspaceId, "akira").Runs.Select(r => r.RunId));
    }

    [Fact] // XV13: a retry after the workspace moved on is the same Run; the key on another revision is another submission, refused
    public async Task A_retried_price_is_the_same_run_after_an_edit_and_the_key_cannot_price_another_revision()
    {
        var (service, runs, _, _) = Priced();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 5);
        var first = Assert.IsType<SubmissionResult.Accepted>(service.Price(ws.WorkspaceId, ws.Revision, "akira", "k1"));
        var moved = service.Edit(ws.WorkspaceId, ws.Revision, "akira", "trade:DRAFT-1", "notional", 9_000_000m);

        var retry = service.Price(ws.WorkspaceId, ws.Revision, "akira", "k1");
        var reused = service.Price(ws.WorkspaceId, moved.Revision, "akira", "k1");

        Assert.Equal(first.Run.RunId, Assert.IsType<SubmissionResult.Accepted>(retry).Run.RunId);
        Assert.Equal("IdempotencyConflict", Assert.IsType<SubmissionResult.Rejected>(reused).Code);
        await runs.Completion(first.Run.RunId);
        Assert.Equal([first.Run.RunId], service.View(ws.WorkspaceId, "akira").Runs.Select(r => r.RunId));
    }

    [Fact] // ADR-0010 §6: a failed PV01 says why and stays failed until `pv01 retry` asks again
    public async Task A_failed_pv01_says_why_until_it_is_asked_again()
    {
        var calls = 0;
        var pv01 = new Pv01Provider((_, _) => Interlocked.Increment(ref calls) <= 3
            ? Task.FromException<decimal>(new InvalidOperationException("The engine is unavailable (simulated)."))
            : Task.FromResult(-1_500m));
        var service = Service(new XvaMeasures(pv01));
        var ws = service.Create("akira", "ABC/LE-01", Day1, 5);

        var failed = await Settled(service, pv01, ws, "DRAFT-1", XvaMeasures.Pv01);
        Assert.Equal((TradeCell.Failed, "PV01 failed: The engine is unavailable (simulated)."), (failed.State, failed.Note));
        Assert.Equal(TradeCell.Failed, Cell(service, ws, "DRAFT-1", XvaMeasures.Pv01).State);
        Assert.Equal(3, calls);

        Assert.Equal(3, service.Retry(ws.WorkspaceId, "akira"));
        await pv01.Settled();

        Assert.Equal(-1_500m, Cell(service, ws, "DRAFT-1", XvaMeasures.Pv01).Value!.GetValue<decimal>());
        Assert.Equal(0, service.Retry(ws.WorkspaceId, "akira"));
    }

    [Theory] // ADR-0010 §6: XVA's columns are a new trade's; PV01 is a result; a Margin is a number of basis points
    [InlineData("existing", "xva.margin", "2", "NotEditable")]
    [InlineData("DRAFT-1", "xva.pv01", "-1500", "NotEditable")]
    [InlineData("DRAFT-1", "xva.margin", "\"two\"", "InvalidValue")]
    [InlineData("DRAFT-1", "xva.charge", "2", "NotEditable")]
    public void An_xva_column_edit_that_cannot_be_made_is_refused(string trade, string field, string json, string code)
    {
        var service = Service();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 5);
        var tradeId = trade == "existing" ? service.Page(ws.WorkspaceId, "akira", 0, 1, [], "existing").Rows[0].TradeId : trade;

        var refused = Assert.Throws<WorkspaceRefused>(() => service.Edit(ws.WorkspaceId, ws.Revision, "akira", $"trade:{tradeId}", field, JsonNode.Parse(json)));

        Assert.Equal(code, refused.Code);
    }

    /// <summary>Prices the revision and answers the finished Run.</summary>
    static async Task<RunRecord> Finished(RunService runs, PortfolioWorkspaces service, WorkspaceState ws)
    {
        var accepted = Assert.IsType<SubmissionResult.Accepted>(service.Price(ws.WorkspaceId, ws.Revision, "akira", Guid.NewGuid().ToString()));
        await runs.Completion(accepted.Run.RunId);
        return runs.Find(accepted.Run.RunId)!;
    }

    [Fact] // ADR-0010 §7, docs/05: Download is the Run's record, its frozen input and its result, each saying what it is
    public async Task A_runs_download_holds_its_record_input_and_result()
    {
        var (service, runs, store, _) = Priced();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 5);
        var run = await Finished(runs, service, ws);

        var zip = new MemoryStream();
        RunArchive.Write(run, store, zip);
        using var archive = new System.IO.Compression.ZipArchive(new MemoryStream(zip.ToArray()));
        JsonNode Read(string path) { using var s = archive.GetEntry(path)!.Open(); return JsonNode.Parse(s)!; }

        Assert.Equal(run.RunId, Read("run.json")["runId"]!.GetValue<string>());
        var entries = Read("manifest.json")["entries"]!.AsArray().Select(e => e!).ToList();
        var input = Assert.Single(entries, e => e["roles"]!.AsArray().Any(r => r!.GetValue<string>() == "input:xva/acceptedInput"));
        var result = Assert.Single(entries, e => e["roles"]!.AsArray().Any(r => r!.GetValue<string>() == "output:xva"));
        Assert.All(entries, e => Assert.Equal("retained", e["state"]!.GetValue<string>()));
        var pinned = ((DataReferenceBinding)run.Workflow.Steps[0].Bindings["acceptedInput"]).Reference;
        Assert.True(JsonNode.DeepEquals(store.ReadRequired(pinned), Read(input["path"]!.GetValue<string>())));
        Assert.True(JsonNode.DeepEquals(store.ReadRequired(run.Outputs[XvaMeasures.Module]), Read(result["path"]!.GetValue<string>())));
    }

    [Fact] // ADR-0010 §7 (DEFAULT note of 2026-11-07): Copy quote gives the Run's header and each case's CVA Charge and PV Diff
    public async Task Copy_quote_reads_the_header_and_each_cases_diffs_from_the_run()
    {
        var pv01 = new Pv01Provider((_, _) => Task.FromResult(-1_500m));
        var (service, runs, store, _) = Priced(new XvaMeasures(pv01));
        var ws = service.Create("akira", "ABC/LE-01", Day1, 5);
        ws = service.Edit(ws.WorkspaceId, ws.Revision, "akira", "trade:DRAFT-1", "xva.margin", 2m);
        await Settled(service, pv01, ws, "DRAFT-1", XvaMeasures.Pv01);
        var run = await Finished(runs, service, ws);
        var rows = store.ReadRequired(run.Outputs[XvaMeasures.Module])["rows"]!.AsArray().Select(r => r!).ToList();
        string Diff(string metric) => Row(rows, metric)["diff"]!.GetValue<decimal>().ToString("#,0.00", System.Globalization.CultureInfo.InvariantCulture);

        var quote = XvaQuote.Of(run, store);

        Assert.Equal([
            $"XVA quote (simulated) · {run.RunId}",
            $"Counterparty ABC/LE-01 · Netting set NS-001 · Value date 2026-10-26 · Revision {ws.Revision} · JPY",
            $"Workspace: CVA Charge {Diff("cvaCharge")} · PV {Diff("pv")}",
        ], quote.Text.Split('\n').Select(l => l.TrimEnd('\r')));
        // The PV a case adds is its new trades' value: DRAFT-1's PV Adj, in the result's currency.
        var draft = service.Page(ws.WorkspaceId, "akira", 0, 10, [], "new").Rows.Single(t => t.TradeId == "DRAFT-1");
        var expected = Math.Round(PortfolioMarket.ToGbp(-3_000m, draft.Currency, Day1) * PortfolioMarket.UnitsPerGbp("JPY", Day1), 2);
        Assert.Equal(expected, Row(rows, "pv")["diff"]!.GetValue<decimal>());
        Assert.Contains("<td style=\"text-align:right\">" + Diff("cvaCharge") + "</td>", quote.Html);
    }

    [Fact] // ADR-0010 §7: a one-sided case quotes its priced side and names it, never a Diff against nothing
    public async Task A_one_sided_case_quotes_its_priced_side_and_names_it()
    {
        var (service, runs, store, _) = Priced();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 0);
        var run = await Finished(runs, service, ws);
        var rows = store.ReadRequired(run.Outputs[XvaMeasures.Module])["rows"]!.AsArray().Select(r => r!).ToList();
        var charge = Row(rows, "cvaCharge")["after"]!.GetValue<decimal>().ToString("#,0.00", System.Globalization.CultureInfo.InvariantCulture);

        var quote = XvaQuote.Of(run, store);

        Assert.EndsWith($"Workspace (After only): CVA Charge {charge} · PV 0.00", quote.Text);
        Assert.Contains("<td>After only</td>", quote.Html);
    }

    [Fact] // ADR-0010 §7, spine 1: a Run that did not price CVA has no CVA Charge to quote, and says so
    public async Task A_run_without_cva_has_nothing_to_quote()
    {
        var (service, runs, store, _) = Priced();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 5);
        ws = service.SetMeasures(ws.WorkspaceId, ws.Revision, "akira", ["fva"]);
        var run = await Finished(runs, service, ws);

        Assert.Equal("NothingToQuote", Assert.Throws<WorkspaceRefused>(() => XvaQuote.Of(run, store)).Code);
    }

    [Fact] // ADR-0010 §1: figures are shown in the display currency the workspace sets, which Price freezes
    public async Task Price_reports_in_the_display_currency_the_workspace_sets()
    {
        var (service, runs, store, _) = Priced();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 5);
        Assert.Equal("JPY", service.View(ws.WorkspaceId, "akira").DisplayCurrency);
        var (_, inJpy) = await Price(runs, store, service, ws);

        ws = service.SetDisplayCurrency(ws.WorkspaceId, ws.Revision, "akira", "USD");
        var (input, inUsd) = await Price(runs, store, service, ws);

        Assert.Equal("USD", input["displayCurrency"]!.GetValue<string>());
        Assert.All(inUsd, r => Assert.Equal("USD", r["currency"]!.GetValue<string>()));
        var jpy = Row(inJpy, "cva")["after"]!.GetValue<decimal>();
        var usd = Row(inUsd, "cva")["after"]!.GetValue<decimal>();
        Assert.InRange(usd - PortfolioMarket.ToGbp(jpy, "JPY", Day1) * PortfolioMarket.UnitsPerGbp("USD", Day1), -0.01m, 0.01m);
        // A setting is the page's, not an edit to the portfolio's data.
        Assert.Equal(0, service.View(ws.WorkspaceId, "akira").Overrides);
        Assert.Equal("InvalidValue", Assert.Throws<WorkspaceRefused>(() => service.SetDisplayCurrency(ws.WorkspaceId, ws.Revision, "akira", "CHF")).Code);
    }

    [Fact] // ADR-0010 §1: Refresh acquires the trades again for the same value date, and the edits carry
    public void Refresh_acquires_again_and_the_edits_carry()
    {
        var service = Service();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 5);
        ws = service.Edit(ws.WorkspaceId, ws.Revision, "akira", "trade:DRAFT-1", "notional", 9_000_000_000m);
        ws = service.SetDisplayCurrency(ws.WorkspaceId, ws.Revision, "akira", "EUR");

        var (refreshed, carried) = service.Refresh(ws.WorkspaceId, ws.Revision, "akira");

        Assert.Equal((1, 0, 0), (carried.Carried, carried.Conflicted, carried.Lost));
        Assert.Equal("EUR", service.View(refreshed.WorkspaceId, "akira").DisplayCurrency);
        // Spot capital, selected with the rest by default, is simulated too (ADR-0013 §2).
        Assert.Equal(["xva", "ccr"], service.View(refreshed.WorkspaceId, "akira").Simulated);
    }
}

