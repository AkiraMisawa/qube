using System.Text.Json;
using System.Text.Json.Nodes;
using Qube.Composition.Application;
using Qube.Composition.Application.Workspaces;
using Qube.Composition.Contracts;
using Qube.Samples.Ccr;
using Qube.Samples.Portfolio;
using Qube.Samples.Xva;

namespace Qube.Composition.Tests;

// Cases on the portfolio workspace service (ADR-0010 §3 and its note of 2026-11-08, §9's note on naming a cell), with
// XVA's and SA-CCR's registrations, as the application registers them. A case's figures are checked against a Base
// priced with the same value typed into the workspace. Names and figures are synthetic.
public class PricerCaseTests
{
    static readonly DateOnly Day1 = new(2026, 10, 26);

    static (PortfolioWorkspaces Service, RunService Runs, IDataStore Store) Priced(PortfolioOptions? options = null)
    {
        var workspaces = new WorkspaceService(new Dictionary<string, IEditRules> { [PortfolioEditRules.App] = new PortfolioEditRules() }, TimeProvider.System);
        var store = new InMemoryDataStore();
        var runs = new RunService(new CompositionRegistry([new PortfolioModule(), new XvaModule(), new CcrModule()]), store, TimeProvider.System);
        var register = new MeasureRegister([new XvaMeasures(Pv01Provider.Simulated(TimeSpan.Zero)), new CcrMeasures()]);
        return (new PortfolioWorkspaces(workspaces, store, runs, register, UnsetSettings.Pricer(), options), runs, store);
    }

    /// <summary>Prices the revision and reads the whole accepted input and the XVA request's rows.</summary>
    static async Task<(JsonObject Input, IReadOnlyList<JsonNode> Rows, RunRecord Run)> Price(RunService runs, IDataStore store, PortfolioWorkspaces service, WorkspaceState ws)
    {
        var accepted = Assert.IsType<SubmissionResult.Accepted>(service.Price(ws.WorkspaceId, ws.Revision, "akira", Guid.NewGuid().ToString()));
        await runs.Completion(accepted.Run.RunId);
        var run = runs.Find(accepted.Run.RunId)!;
        Assert.Equal(RunStatus.Succeeded, run.Status);
        var input = store.ReadRequired(((DataReferenceBinding)run.Workflow.Steps[0].Bindings["acceptedInput"]).Reference).AsObject();
        return (input, [.. store.ReadRequired(run.Outputs[XvaMeasures.Module])["rows"]!.AsArray().Select(r => r!)], run);
    }

    static JsonNode Row(IReadOnlyList<JsonNode> rows, string @case, string metric) =>
        rows.Single(r => r["case"]!.GetValue<string>() == @case && r["metric"]!.GetValue<string>() == metric);

    static string Id(PortfolioWorkspaces service, WorkspaceState ws, string title) =>
        service.View(ws.WorkspaceId, "akira").Cases.Single(c => c.Title == title).Id;

    [Fact] // ADR-0010 §3, §9: vary makes a case per value, named from its change; the workspace's own value makes none
    public void Vary_makes_a_case_per_value_and_skips_the_workspaces_value()
    {
        var (service, _, _) = Priced();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 40);

        var answer = service.Vary(ws.WorkspaceId, ws.Revision, "akira", "CSA-A threshold", ["10mm", "5mm", "0"]);

        Assert.Equal(["CSA-A threshold 10mm", "CSA-A threshold 5mm"], answer.Made);
        Assert.Equal(["0 is the workspace's value"], answer.Skipped);
        var view = service.View(ws.WorkspaceId, "akira");
        Assert.Equal([("base", "Base", 0), ("vary", "CSA-A threshold 10mm", 1), ("vary", "CSA-A threshold 5mm", 1)], view.Cases.Select(c => (c.Kind, c.Title, c.Changes)));
        // The workspace shows the Base: a case's change is not a change of the workspace.
        Assert.Equal(0m, view.Csas.Single(c => c.CsaId == "CSA-A").CounterpartyThreshold);
        Assert.Equal(0, view.Overrides);
    }

    [Fact] // ADR-0010 §3's note of 2026-11-08: a second vary adds its cases beside the first's, and a case made already is not made again
    public void A_second_vary_adds_its_cases_and_never_makes_one_twice()
    {
        var (service, _, _) = Priced();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 40);
        ws = service.Vary(ws.WorkspaceId, ws.Revision, "akira", "CSA-A threshold", ["10mm"]).State;

        var answer = service.Vary(ws.WorkspaceId, ws.Revision, "akira", "NS-001 recovery", ["40%", "25%", "40%"]);
        var again = service.Vary(answer.State.WorkspaceId, answer.State.Revision, "akira", "csa-a THRESHOLD", ["10,000k"]);

        Assert.Equal(["NS-001 recovery 40%", "NS-001 recovery 25%"], answer.Made);
        Assert.Equal(["40% is case NS-001 recovery 40%, made already"], answer.Skipped);
        Assert.Empty(again.Made);
        Assert.Equal(["10,000k is case CSA-A threshold 10mm, made already"], again.Skipped);
        Assert.Equal(answer.State.Revision, again.State.Revision);
        Assert.Equal(["Base", "CSA-A threshold 10mm", "NS-001 recovery 40%", "NS-001 recovery 25%"],
            service.View(ws.WorkspaceId, "akira").Cases.Select(c => c.Title));
    }

    [Fact] // ADR-0010 §9's note of 2026-11-08: a cell that names nothing is refused with what it could mean, and a list with a value that cannot be read makes no case
    public void A_cell_or_a_value_that_cannot_be_read_is_refused_and_makes_no_case()
    {
        var (service, _, _) = Priced();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 40);

        var row = Assert.Throws<WorkspaceRefused>(() => service.Vary(ws.WorkspaceId, ws.Revision, "akira", "CSA-Z threshold", ["5mm"]));
        var column = Assert.Throws<WorkspaceRefused>(() => service.Vary(ws.WorkspaceId, ws.Revision, "akira", "CSA-A haircut", ["5mm"]));
        var value = Assert.Throws<WorkspaceRefused>(() => service.Vary(ws.WorkspaceId, ws.Revision, "akira", "CSA-A threshold", ["5mm", "lots"]));
        var negative = Assert.Throws<WorkspaceRefused>(() => service.Vary(ws.WorkspaceId, ws.Revision, "akira", "CSA-A mta", ["-1mm"]));
        var full = Assert.Throws<WorkspaceRefused>(() => service.Vary(ws.WorkspaceId, ws.Revision, "akira", "CSA-A threshold", ["5000000"]));

        Assert.Equal(("UnknownCell", "No row CSA-Z: a row is a CSA (CSA-A, CSA-B), the netting set (NS-001), credit, cds, a trade's id, or xva for XVA's settings."), (row.Code, row.Message));
        Assert.Equal("UnknownCell", column.Code);
        Assert.StartsWith("CSA-A has no column haircut; its columns are threshold, mta,", column.Message);
        Assert.Equal(("InvalidValue", "CSA-A threshold is an amount: 5mm, 250k, 1bn, or a bare number of millions; lots is not."), (value.Code, value.Message));
        Assert.Equal("InvalidValue", negative.Code);
        Assert.Equal("CSA-A threshold is in millions; write a full amount with its scale, such as 1bn; 5000000 is not.", full.Message);
        Assert.Equal(["Base"], service.View(ws.WorkspaceId, "akira").Cases.Select(c => c.Title));
    }

    [Fact] // ADR-0010 §3: Price prices the Base and every case in one Run, each case as the workspace with its change typed in
    public async Task Price_prices_the_base_and_every_case_each_as_its_change_typed_in()
    {
        var (service, runs, store) = Priced();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 40);
        var (_, plain, _) = await Price(runs, store, service, ws);
        var typed = service.Create("akira", "ABC/LE-01", Day1, 40);
        typed = service.Edit(typed.WorkspaceId, typed.Revision, "akira", "csa:CSA-A", "counterpartyThreshold", 200_000_000m);
        var (_, expected, _) = await Price(runs, store, service, typed);

        ws = service.Vary(ws.WorkspaceId, ws.Revision, "akira", "CSA-A threshold", ["200mm"]).State;
        var (_, rows, _) = await Price(runs, store, service, ws);

        // One Run's one XVA request priced both.
        var raised = Id(service, ws, "CSA-A threshold 200mm");
        Assert.Equal(["base", raised], rows.Where(r => r["metric"]!.GetValue<string>() == "fva").Select(r => r["case"]!.GetValue<string>()));
        Assert.Equal("CSA-A threshold 200mm", Row(rows, raised, "fva")["caseTitle"]!.GetValue<string>());
        foreach (var metric in new[] { "cva", "fva", "cvaCharge", "pv" })
        {
            Assert.Equal(Row(plain, "base", metric)["diff"]!.GetValue<decimal>(), Row(rows, "base", metric)["diff"]!.GetValue<decimal>());
            Assert.Equal(Row(expected, "base", metric)["after"]!.GetValue<decimal>(), Row(rows, raised, metric)["after"]!.GetValue<decimal>());
        }
        // The threshold moved CSA-A's trades into FVA in the case alone.
        Assert.True(Row(rows, raised, "fva")["tradesAfter"]!.GetValue<int>() > Row(rows, "base", "fva")["tradesAfter"]!.GetValue<int>());
    }

    [Fact] // ADR-0010 §3: a case's change of a new trade's own field wins over the Common edit of it, whatever scope each is kept in
    public async Task A_cases_change_of_a_new_trades_own_field_wins_over_the_common_edit()
    {
        var (service, runs, store) = Priced();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 40);
        // A new trade's own content is kept in its own scope, so it stays when the counterparty changes; a case's is the counterparty's.
        ws = service.Edit(ws.WorkspaceId, ws.Revision, "akira", "trade:DRAFT-1", "notional", 30_000_000m);
        ws = service.Vary(ws.WorkspaceId, ws.Revision, "akira", "DRAFT-1 notional", ["10mm"]).State;

        var (input, _, _) = await Price(runs, store, service, ws);

        decimal Notional(PricedCase c) => c.Input["trades"]!.AsArray().Single(t => t!["tradeId"]!.GetValue<string>() == "DRAFT-1")!["notional"]!.GetValue<decimal>();
        Assert.Equal([30_000_000m, 10_000_000m], PortfolioInput.Cases(input).Select(Notional));
        Assert.Equal(30_000_000m, service.Page(ws.WorkspaceId, "akira", 0, 10, [], "new").Rows.Single(t => t.TradeId == "DRAFT-1").Notional);
    }

    [Fact] // ADR-0010 §3: a case is the Base plus its own change; its value wins on a field the Base edits too, and the Base's other edits are in every case
    public async Task A_case_reads_the_base_with_its_own_change_winning()
    {
        var (service, runs, store) = Priced();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 40);
        ws = service.Edit(ws.WorkspaceId, ws.Revision, "akira", "csa:CSA-A", "counterpartyThreshold", 200_000_000m);
        ws = service.Edit(ws.WorkspaceId, ws.Revision, "akira", "csa:CSA-B", "counterpartyMta", 2_000_000m);

        var answer = service.Vary(ws.WorkspaceId, ws.Revision, "akira", "CSA-A threshold", ["0", "200mm"]);
        var (input, _, _) = await Price(runs, store, service, answer.State);

        Assert.Equal(["CSA-A threshold 0"], answer.Made);
        Assert.Equal(["200mm is the workspace's value"], answer.Skipped);
        var cases = PortfolioInput.Cases(input);
        decimal Term(PricedCase c, string csa, string term) =>
            c.Input["modules"]!["ccr"]!["csas"]!.AsArray().Single(x => x!["csaId"]!.GetValue<string>() == csa)![term]!.GetValue<decimal>();
        Assert.Equal((200_000_000m, 0m), (Term(cases[0], "CSA-A", "threshold"), Term(cases[1], "CSA-A", "threshold")));
        Assert.Equal((2_000_000m, 2_000_000m), (Term(cases[0], "CSA-B", "mta"), Term(cases[1], "CSA-B", "mta")));
        Assert.Equal(200_000_000m, service.View(ws.WorkspaceId, "akira").Csas.Single(c => c.CsaId == "CSA-A").CounterpartyThreshold);
    }

    [Fact] // ADR-0010 §3, story 23: the Run freezes every case with its changes and its trades, the Base first
    public async Task The_run_freezes_every_case_with_its_changes_and_trades()
    {
        var (service, runs, store) = Priced();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 40);
        ws = service.Vary(ws.WorkspaceId, ws.Revision, "akira", "NS-001 recovery", ["25%"]).State;

        var (input, _, _) = await Price(runs, store, service, ws);

        var cases = input["cases"]!.AsArray();
        Assert.Equal([("base", "Base", "base"), (Id(service, ws, "NS-001 recovery 25%"), "NS-001 recovery 25%", "vary")],
            cases.Select(c => (c!["id"]!.GetValue<string>(), c["title"]!.GetValue<string>(), c["kind"]!.GetValue<string>())));
        Assert.Empty(cases[0]!["changes"]!.AsArray());
        var change = Assert.Single(cases[1]!["changes"]!.AsArray())!;
        Assert.Equal(("netting:NS-001", "cvaRecovery", 0.25m), (change["entity"]!.GetValue<string>(), change["field"]!.GetValue<string>(), change["value"]!.GetValue<decimal>()));
        Assert.Equal(cases[0]!["trades"]!.AsArray().Count, cases[1]!["trades"]!.AsArray().Count);
        // The Base keeps an empty CVA recovery empty (docs/03); the case prices its own.
        Assert.Null(cases[0]!["modules"]!["xva"]!["credit"]!["cvaRecovery"]);
        Assert.Equal(0.25m, cases[1]!["modules"]!["xva"]!["credit"]!["cvaRecovery"]!.GetValue<decimal>());
        Assert.Equal(new[] { "ccr.spotCapital", "cva", "fva", "kva" }, input["selection"]!.AsArray().Select(m => m!.GetValue<string>()).Order(StringComparer.Ordinal));
    }

    [Fact] // ADR-0010 §3's note of 2026-11-08: Price over the cap of cases, the Base counted, is refused by how much
    public void Price_over_the_cap_of_cases_is_refused_by_how_much()
    {
        var (service, _, _) = Priced(new PortfolioOptions(CaseCap: 3));
        var ws = service.Create("akira", "ABC/LE-01", Day1, 40);
        ws = service.Vary(ws.WorkspaceId, ws.Revision, "akira", "CSA-A threshold", ["1mm", "2mm", "3mm"]).State;

        var refused = Assert.Throws<WorkspaceRefused>(() => service.Price(ws.WorkspaceId, ws.Revision, "akira", "k1"));

        Assert.Equal(("TooManyCases", "Price prices 3 cases at most, the Base among them; this workspace holds 4, 1 over. Remove 1 to price."), (refused.Code, refused.Message));
    }

    [Fact] // ADR-0010 §3's further note, ADR-0003 §6: a counterparty change drops the cases made on it, counted in the confirmation
    public void A_counterparty_change_drops_the_cases_counted_in_its_confirmation()
    {
        var (service, _, _) = Priced();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 40);
        ws = service.Vary(ws.WorkspaceId, ws.Revision, "akira", "CSA-A threshold", ["1mm", "2mm"]).State;

        var drop = service.PreviewCounterpartyChange(ws.WorkspaceId, "akira");
        service.ChangeCounterparty(ws.WorkspaceId, ws.Revision, "akira", "ABC/LE-02", drop.Count);

        Assert.Equal((4, 2, 2), (drop.Count, drop.ByKind["portfolio.case"], drop.ByKind["xva.csa"]));
        Assert.Equal(["Base"], service.View(ws.WorkspaceId, "akira").Cases.Select(c => c.Title));
    }

    [Fact] // ADR-0013 §2 with ADR-0010 §3: spot capital is priced for every case, with a trace for each
    public async Task Spot_capital_is_priced_for_every_case_with_its_trace()
    {
        var (service, runs, store) = Priced();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 40);
        ws = service.Vary(ws.WorkspaceId, ws.Revision, "akira", "CSA-A threshold", ["500mm"]).State;

        var (_, rows, run) = await Price(runs, store, service, ws);

        var raised = Id(service, ws, "CSA-A threshold 500mm");
        var capital = rows.Where(r => r["measure"]!.GetValue<string>() == CcrMeasures.SpotCapital && r["metric"]!.GetValue<string>() == "capital").ToList();
        Assert.Equal(["base", raised], capital.Select(r => r["case"]!.GetValue<string>()));
        // A higher threshold leaves more exposure uncollateralised: more capital in the case, as when it is typed in.
        Assert.True(capital[1]["after"]!.GetValue<decimal>() > capital[0]["after"]!.GetValue<decimal>());
        var typed = service.Create("akira", "ABC/LE-01", Day1, 40);
        typed = service.Edit(typed.WorkspaceId, typed.Revision, "akira", "csa:CSA-A", "counterpartyThreshold", 500_000_000m);
        var (_, expected, _) = await Price(runs, store, service, typed);
        Assert.Equal(Row(expected, "base", "capital")["after"]!.GetValue<decimal>(), capital[1]["after"]!.GetValue<decimal>());
        var traces = store.ReadRequired(run.Invocations.Single(i => i.StepId == XvaMeasures.Module).Outputs["traces"]).Deserialize<TraceSet>(WireJson.Options)!;
        Assert.Equal([("base", "before"), ("base", "after"), (raised, "before"), (raised, "after")], traces.Traces.Select(t => (t.Case, t.Side)));
        // Each case's profile is its own.
        var profile = store.ReadRequired(run.Invocations.Single(i => i.StepId == XvaMeasures.Module).Outputs["profiles"])["rows"]!.AsArray();
        Assert.Equal(["base", raised], profile.Select(p => p!["case"]!.GetValue<string>()).Distinct());
    }

    [Fact] // ADR-0010 §3's note of 2026-11-08: the Changes tab lists each Common change, then each case with its own, with Original's value
    public void The_changes_list_holds_the_common_changes_and_each_cases_with_original()
    {
        var (service, _, _) = Priced();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 40);
        ws = service.Edit(ws.WorkspaceId, ws.Revision, "akira", "csa:CSA-B", "counterpartyMta", 2_000_000m);
        ws = service.Edit(ws.WorkspaceId, ws.Revision, "akira", "grid:existing", "before", false);
        ws = service.Vary(ws.WorkspaceId, ws.Revision, "akira", "CSA-A threshold", ["5mm"]).State;

        var changes = service.Changes(ws.WorkspaceId, "akira");

        Assert.Equal(3, changes.Count);
        Assert.Equal(3, service.View(ws.WorkspaceId, "akira").Changes);
        Assert.Equal([("CSA-B mta", "2mm", "500k", true), ("Existing B every row", "off", "each row's own", false)],
            changes.Common.Select(c => (c.Title, c.Text, c.OriginalText, c.Editable)));
        var @case = Assert.Single(changes.Cases);
        Assert.Equal(("CSA-A threshold 5mm", "vary"), (@case.Case.Title, @case.Case.Kind));
        var change = Assert.Single(@case.Changes);
        Assert.Equal(("csa:CSA-A", "counterpartyThreshold", @case.Case.Id, "CSA-A threshold", "5mm", "0", "set"),
            (change.Entity, change.Field, change.Case, change.Title, change.Text, change.OriginalText, change.State));
    }

    [Fact] // ADR-0010 §3's note of 2026-11-08: a case's change edited in the Changes tab changes that case alone, and removed leaves the case without it
    public async Task A_cases_change_is_edited_and_removed_in_that_case_alone()
    {
        var (service, runs, store) = Priced();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 40);
        ws = service.Vary(ws.WorkspaceId, ws.Revision, "akira", "CSA-A threshold", ["5mm", "10mm"]).State;
        var (five, ten) = (Id(service, ws, "CSA-A threshold 5mm"), Id(service, ws, "CSA-A threshold 10mm"));

        ws = service.SetText(ws.WorkspaceId, ws.Revision, "akira", "csa:CSA-A", "counterpartyThreshold", "200mm", five);
        var refused = Assert.Throws<WorkspaceRefused>(() => service.SetText(ws.WorkspaceId, ws.Revision, "akira", "csa:CSA-A", "counterpartyThreshold", "-1mm", five));
        var (input, _, _) = await Price(runs, store, service, ws);

        var thresholds = PortfolioInput.Cases(input).Select(c => c.Input["modules"]!["ccr"]!["csas"]!.AsArray()
            .Single(x => x!["csaId"]!.GetValue<string>() == "CSA-A")!["threshold"]!.GetValue<decimal>());
        Assert.Equal([0m, 200_000_000m, 10_000_000m], thresholds);
        Assert.Equal("InvalidValue", refused.Code);
        ws = service.Resolve(ws.WorkspaceId, ws.Revision, "akira", "csa:CSA-A", "counterpartyThreshold", "reset", ten);
        var changes = service.Changes(ws.WorkspaceId, "akira");
        Assert.Equal([1, 0], changes.Cases.Select(c => c.Changes.Count));
        Assert.Equal(["Base", "CSA-A threshold 5mm", "CSA-A threshold 10mm"], service.View(ws.WorkspaceId, "akira").Cases.Select(c => c.Title));
    }

    [Fact] // ADR-0010 §3's note of 2026-11-08: a case is removed whole, by its title, in one log entry, and `cases clear` removes every one
    public async Task A_case_is_removed_whole_and_cases_clear_removes_every_one()
    {
        var (service, runs, store) = Priced();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 40);
        ws = service.Vary(ws.WorkspaceId, ws.Revision, "akira", "CSA-A threshold", ["5mm", "10mm", "20mm"]).State;

        var removed = service.RemoveCase(ws.WorkspaceId, ws.Revision, "akira", "csa-a threshold 10MM").State;
        var unknown = Assert.Throws<WorkspaceRefused>(() => service.RemoveCase(removed.WorkspaceId, removed.Revision, "akira", "CSA-A threshold 10mm"));
        var (_, rows, _) = await Price(runs, store, service, removed);

        Assert.Equal(ws.Revision + 1, removed.Revision);
        Assert.Equal("1 edits", removed.Log[^1].Note);
        Assert.Equal(("UnknownCase", "No case CSA-A threshold 10mm; the cases are CSA-A threshold 5mm, CSA-A threshold 20mm."), (unknown.Code, unknown.Message));
        Assert.Equal(["Base", "CSA-A threshold 5mm", "CSA-A threshold 20mm"], rows.Where(r => r["metric"]!.GetValue<string>() == "cva").Select(r => r["caseTitle"]!.GetValue<string>()));
        var cleared = service.ClearCases(removed.WorkspaceId, removed.Revision, "akira").State;
        Assert.Equal(["Base"], service.View(cleared.WorkspaceId, "akira").Cases.Select(c => c.Title));
        Assert.Empty(service.Changes(cleared.WorkspaceId, "akira").Cases);
        Assert.Equal("NothingToChange", Assert.Throws<WorkspaceRefused>(() => service.ClearCases(cleared.WorkspaceId, cleared.Revision, "akira")).Code);
    }

    // The new trades a case's After holds, from the accepted input's case.
    static IReadOnlyList<string> NewInAfter(PricedCase @case) =>
        [.. @case.Input["trades"]!.AsArray().Where(t => t!["isNew"]!.GetValue<bool>() && t["after"]!.GetValue<bool>()).Select(t => t!["tradeId"]!.GetValue<string>())];

    static IReadOnlyList<string> ExistingInAfter(PricedCase @case) =>
        [.. @case.Input["trades"]!.AsArray().Where(t => !t!["isNew"]!.GetValue<bool>() && t["after"]!.GetValue<bool>()).Select(t => t!["tradeId"]!.GetValue<string>())];

    [Fact] // docs/03 §5, ADR-0010 §3's note of 2026-11-08: add each makes a case per new trade with A ticked, each After the existing trades and that trade alone
    public async Task Add_each_makes_a_case_per_new_trade_each_holding_it_alone()
    {
        var (service, runs, store) = Priced();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 40);

        var answer = service.AddTrades(ws.WorkspaceId, ws.Revision, "akira");
        // A Common edit made after the set does not undo a case's own: A every New row, ticked again.
        var later = service.Edit(answer.State.WorkspaceId, answer.State.Revision, "akira", "grid:new", "after", true);
        var (input, _, _) = await Price(runs, store, service, later);

        Assert.Equal(["add DRAFT-1", "add DRAFT-2", "add DRAFT-3"], answer.Made);
        Assert.Empty(answer.Replaced);
        var cases = PortfolioInput.Cases(input);
        Assert.Equal([("base", "Base"), ("add", "add DRAFT-1"), ("add", "add DRAFT-2"), ("add", "add DRAFT-3")], cases.Select(c => (c.Kind, c.Title)));
        // The Base holds the package, every case one trade of it on top of the same existing trades.
        Assert.Equal([["DRAFT-1", "DRAFT-2", "DRAFT-3"], ["DRAFT-1"], ["DRAFT-2"], ["DRAFT-3"]], cases.Select(NewInAfter));
        Assert.NotEmpty(ExistingInAfter(cases[0]));
        Assert.All(cases, c => Assert.Equal(ExistingInAfter(cases[0]), ExistingInAfter(c)));
    }

    [Fact] // docs/03 §5: each case of the set prices as the workspace with only its new trade ticked, never cumulatively
    public async Task Each_case_of_the_set_prices_as_the_workspace_with_only_its_trade_ticked()
    {
        var (service, runs, store) = Priced();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 40);
        ws = service.AddTrades(ws.WorkspaceId, ws.Revision, "akira").State;
        var (_, rows, _) = await Price(runs, store, service, ws);

        foreach (var trade in new[] { "DRAFT-1", "DRAFT-2", "DRAFT-3" })
        {
            var alone = service.Create("akira", "ABC/LE-01", Day1, 40);
            foreach (var other in new[] { "DRAFT-1", "DRAFT-2", "DRAFT-3" }.Where(o => o != trade))
                alone = service.Edit(alone.WorkspaceId, alone.Revision, "akira", $"trade:{other}", "after", false);
            var (_, expected, _) = await Price(runs, store, service, alone);
            var id = Id(service, ws, $"add {trade}");
            foreach (var metric in new[] { "cva", "fva", "cvaCharge", "pv" })
            {
                Assert.Equal(Row(expected, "base", metric)["before"]!.GetValue<decimal>(), Row(rows, id, metric)["before"]!.GetValue<decimal>());
                Assert.Equal(Row(expected, "base", metric)["after"]!.GetValue<decimal>(), Row(rows, id, metric)["after"]!.GetValue<decimal>());
            }
            Assert.Equal(Row(rows, "base", "cva")["tradesAfter"]!.GetValue<int>() - 2, Row(rows, id, "cva")["tradesAfter"]!.GetValue<int>());
        }
    }

    [Fact] // ADR-0016 note 96 (owner, 2026-11-19): a New row in Before and After, a trade booked today the source does not hold yet, is read as an existing trade: no candidate, in Before and After in every case, which each case's title says, and taken out by a package as an existing trade is
    public async Task A_new_row_in_before_and_after_is_read_as_an_existing_trade()
    {
        var (service, runs, store) = Priced();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 40);
        var e = service.Page(ws.WorkspaceId, "akira", 0, 1, [], "existing").Rows[0].TradeId;
        ws = Flag(service, ws, "DRAFT-2", "before", true);

        var alone = service.AddTrades(ws.WorkspaceId, ws.Revision, "akira");
        var (input, _, _) = await Price(runs, store, service, alone.State);

        Assert.Equal(["add DRAFT-1", "add DRAFT-3"], alone.Made);
        var cases = PortfolioInput.Cases(input);
        Assert.Equal(["Base", "add DRAFT-1 · with new DRAFT-2 in Before and After", "add DRAFT-3 · with new DRAFT-2 in Before and After"], cases.Select(c => c.Title));
        Assert.All(cases, c => Assert.Equal((true, true), Sides(c, "DRAFT-2")));
        Assert.Equal([["DRAFT-1", "DRAFT-2", "DRAFT-3"], ["DRAFT-1", "DRAFT-2"], ["DRAFT-2", "DRAFT-3"]], cases.Select(NewInAfter));

        // In a package with an existing trade, it is taken out as the existing trade is: P1's case is DRAFT-1 instead of
        // what was booked today, and does not say the change it takes out.
        ws = Package(service, alone.State, "P1", "DRAFT-1", "DRAFT-2", e);
        var packaged = service.AddTrades(ws.WorkspaceId, ws.Revision, "akira");
        var (inP1, _, _) = await Price(runs, store, service, packaged.State);

        Assert.Equal([$"P1: add DRAFT-1 without DRAFT-2 + {e}", "add DRAFT-3"], packaged.Made);
        Assert.Equal([new PackageView("P1", ["DRAFT-1"], ["DRAFT-2", e])], service.View(ws.WorkspaceId, "akira").Packages, new PackageViewComparer());
        var priced = PortfolioInput.Cases(inP1);
        Assert.Equal(["Base", $"P1: add DRAFT-1 without DRAFT-2 + {e}", "add DRAFT-3 · with new DRAFT-2 in Before and After"], priced.Select(c => c.Title));
        Assert.Equal([(true, true), (true, false), (true, true)], priced.Select(c => Sides(c, "DRAFT-2")));
        Assert.Equal([(true, true), (true, false), (true, true)], priced.Select(c => Sides(c, e)));

        // In a package with A clear, it is a trade After does not hold, refused as an existing one is; and with every New
        // row in Before, no row is a candidate.
        var cleared = Flag(service, packaged.State, "DRAFT-2", "after", false);
        var notInAfter = Assert.Throws<WorkspaceRefused>(() => service.AddTrades(cleared.WorkspaceId, cleared.Revision, "akira"));
        var none = Package(service, service.Edit(cleared.WorkspaceId, cleared.Revision, "akira", "grid:new", "before", true), null, "DRAFT-1", "DRAFT-2", e);
        var nothing = Assert.Throws<WorkspaceRefused>(() => service.AddTrades(none.WorkspaceId, none.Revision, "akira"));

        Assert.Equal(("NotInAfter", "DRAFT-2 is in the package P1 and not in After in the Base, so no case takes it out. Tick A on it, or clear its Package."),
            (notInAfter.Code, notInAfter.Message));
        Assert.Equal(("NothingToAdd", "add each makes a case per package and per New row with A ticked and B clear, and no trade is in a package and no New row has A ticked and B clear. "
            + "A New row in Before is read as an existing trade."), (nothing.Code, nothing.Message));
        Assert.Equal(none.Revision, service.View(none.WorkspaceId, "akira").Revision);
    }

    // Where a case holds a trade, in Before and in After; a trade in neither is not in the case's input at all.
    static (bool Before, bool After)? Sides(PricedCase @case, string id) =>
        @case.Input["trades"]!.AsArray().SingleOrDefault(t => t!["tradeId"]!.GetValue<string>() == id) is { } trade
            ? (trade["before"]!.GetValue<bool>(), trade["after"]!.GetValue<bool>()) : null;

    [Fact] // docs/03 §5, ADR-0010 §3's note of 2026-11-08: Price refuses, naming the trades, a set that no longer matches the New rows with A ticked; add each again makes it anew
    public async Task Price_refuses_a_set_the_ticked_new_rows_no_longer_match_and_add_each_makes_it_anew()
    {
        var (service, runs, store) = Priced();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 40);
        ws = service.Edit(ws.WorkspaceId, ws.Revision, "akira", "trade:DRAFT-3", "after", false);
        ws = service.AddTrades(ws.WorkspaceId, ws.Revision, "akira").State;
        ws = service.Edit(ws.WorkspaceId, ws.Revision, "akira", "trade:DRAFT-3", "after", true);

        var ticked = Assert.Throws<WorkspaceRefused>(() => service.Price(ws.WorkspaceId, ws.Revision, "akira", Guid.NewGuid().ToString()));
        var inBefore = service.Edit(ws.WorkspaceId, ws.Revision, "akira", "trade:DRAFT-1", "before", true);
        var before = Assert.Throws<WorkspaceRefused>(() => service.Price(inBefore.WorkspaceId, inBefore.Revision, "akira", Guid.NewGuid().ToString()));
        var cleared = service.Edit(inBefore.WorkspaceId, inBefore.Revision, "akira", "trade:DRAFT-1", "before", false);
        var again = service.AddTrades(cleared.WorkspaceId, cleared.Revision, "akira");
        var (input, _, _) = await Price(runs, store, service, again.State);

        Assert.Equal(("AdditionSetChanged", "add each's cases are not what the Package column and A say. Missing: add DRAFT-3. Run add each again to make the set anew."),
            (ticked.Code, ticked.Message));
        // DRAFT-1 ticked in Before is read as an existing trade from then on (ADR-0016 note 96): its case is out of date.
        Assert.Equal(("AdditionSetChanged", "add each's cases are not what the Package column and A say. Missing: add DRAFT-3. Out of date: add DRAFT-1. Run add each again to make the set anew."),
            (before.Code, before.Message));
        Assert.Equal(["add DRAFT-1", "add DRAFT-2"], again.Replaced);
        Assert.Equal(["Base", "add DRAFT-1", "add DRAFT-2", "add DRAFT-3"], PortfolioInput.Cases(input).Select(c => c.Title));
        Assert.Equal([["DRAFT-1", "DRAFT-2", "DRAFT-3"], ["DRAFT-1"], ["DRAFT-2"], ["DRAFT-3"]], PortfolioInput.Cases(input).Select(NewInAfter));
    }

    [Fact] // docs/03 §5: a common change to the existing trades is said in every case of the set, as it stands when priced
    public async Task A_common_change_to_the_existing_trades_is_said_in_every_case_of_the_set()
    {
        var (service, runs, store) = Priced();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 40);
        ws = service.AddTrades(ws.WorkspaceId, ws.Revision, "akira").State;
        var first = service.Page(ws.WorkspaceId, "akira", 0, 1, [], "existing").Rows[0].TradeId;
        var plain = service.View(ws.WorkspaceId, "akira").Cases;
        ws = service.Edit(ws.WorkspaceId, ws.Revision, "akira", $"trade:{first}", "after", false);

        var (input, rows, _) = await Price(runs, store, service, ws);

        Assert.All(plain, c => Assert.Null(c.Note));
        Assert.Equal([null, $"with existing {first} out of After", $"with existing {first} out of After", $"with existing {first} out of After"],
            service.View(ws.WorkspaceId, "akira").Cases.Select(c => c.Note));
        Assert.Equal(["Base", $"add DRAFT-1 · with existing {first} out of After", $"add DRAFT-2 · with existing {first} out of After", $"add DRAFT-3 · with existing {first} out of After"],
            rows.Where(r => r["metric"]!.GetValue<string>() == "cva").Select(r => r["caseTitle"]!.GetValue<string>()));
        Assert.All(PortfolioInput.Cases(input), c => Assert.DoesNotContain(first, ExistingInAfter(c)));
    }

    [Fact] // ADR-0010 §3's note of 2026-11-08: a case of the set is its membership, with no cell to edit it from, and is removed whole
    public void A_case_of_the_set_is_changed_only_whole()
    {
        var (service, _, _) = Priced();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 40);
        ws = service.AddTrades(ws.WorkspaceId, ws.Revision, "akira").State;
        var id = Id(service, ws, "add DRAFT-1");

        var changes = service.Changes(ws.WorkspaceId, "akira").Cases[0];
        var set = Assert.Throws<WorkspaceRefused>(() => service.SetText(ws.WorkspaceId, ws.Revision, "akira", "trade:DRAFT-2", "after", "on", id));
        var reset = Assert.Throws<WorkspaceRefused>(() => service.Resolve(ws.WorkspaceId, ws.Revision, "akira", "trade:DRAFT-2", "after", "reset", id));
        var removed = service.RemoveCase(ws.WorkspaceId, ws.Revision, "akira", "add DRAFT-1").State;

        Assert.Equal(("add DRAFT-1", "add", "DRAFT-1", 0), (changes.Case.Title, changes.Case.Kind, string.Join(", ", changes.Case.Iteration!.Add), changes.Case.Iteration.Without.Count));
        Assert.Equal([("DRAFT-2 A", "off", "on", false), ("DRAFT-3 A", "off", "on", false)], changes.Changes.Select(c => (c.Title, c.Text, c.OriginalText, c.Editable)));
        Assert.Equal(("NotEditable", "Case add DRAFT-1 is its trades and no other change: remove the case whole, or run add each again to make the set anew."), (set.Code, set.Message));
        Assert.Equal("NotEditable", reset.Code);
        Assert.Equal(["Base", "add DRAFT-2", "add DRAFT-3"], service.View(removed.WorkspaceId, "akira").Cases.Select(c => c.Title));
        // A is still a cell vary can name, typed in any case and written as the grid heads it.
        Assert.Equal(["DRAFT-1 A off"], service.Vary(removed.WorkspaceId, removed.Revision, "akira", "draft-1 a", ["off"]).Made);
    }

    static IReadOnlyList<string> ExistingInBefore(PricedCase @case) =>
        [.. @case.Input["trades"]!.AsArray().Where(t => !t!["isNew"]!.GetValue<bool>() && t["before"]!.GetValue<bool>()).Select(t => t!["tradeId"]!.GetValue<string>())];

    /// <summary>Ticks or clears a trade's B (<c>before</c>) or A (<c>after</c>).</summary>
    static WorkspaceState Flag(PortfolioWorkspaces service, WorkspaceState state, string trade, string field, bool on) =>
        service.Edit(state.WorkspaceId, state.Revision, "akira", $"trade:{trade}", field, on);

    // Writes a package on trades as a cell is written (ADR-0016 note 90), one edit a trade.
    static WorkspaceState Package(PortfolioWorkspaces service, WorkspaceState ws, string? name, params string[] trades) =>
        trades.Aggregate(ws, (at, trade) => service.Edit(at.WorkspaceId, at.Revision, "akira", $"trade:{trade}", "package", name));

    static string Lists(JsonNode iteration) =>
        $"{iteration["package"]?.GetValue<string>()}: {string.Join(", ", iteration["add"]!.AsArray())} | {string.Join(", ", iteration["without"]!.AsArray())}";

    [Fact] // ADR-0016 note 90: a package adds its new trades together and takes its existing trades out of After in one case that names them all; a ticked new trade in none is a case alone
    public async Task A_package_adds_its_new_trades_together_and_takes_its_existing_trades_out_in_one_case()
    {
        var (service, runs, store) = Priced();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 40);
        var taken = service.Page(ws.WorkspaceId, "akira", 0, 1, [], "existing").Rows[0].TradeId;
        ws = Package(service, ws, "P1", "DRAFT-1", "DRAFT-2", taken);

        var answer = service.AddTrades(ws.WorkspaceId, ws.Revision, "akira");
        var (input, rows, _) = await Price(runs, store, service, answer.State);

        Assert.Equal([$"P1: add DRAFT-1 + DRAFT-2 without {taken}", "add DRAFT-3"], answer.Made);
        Assert.Equal([null, $"P1: DRAFT-1, DRAFT-2 | {taken}", ": DRAFT-3 | "],
            service.View(ws.WorkspaceId, "akira").Cases.Select(c => c.Iteration is { } i ? $"{i.Package}: {string.Join(", ", i.Add)} | {string.Join(", ", i.Without)}" : null));
        var cases = PortfolioInput.Cases(input);
        Assert.Equal([["DRAFT-1", "DRAFT-2", "DRAFT-3"], ["DRAFT-1", "DRAFT-2"], ["DRAFT-3"]], cases.Select(NewInAfter));
        // The trade taken out leaves this case's After alone, and stays in its Before: the difference is the whole change.
        Assert.Equal([true, false, true], cases.Select(c => ExistingInAfter(c).Contains(taken)));
        Assert.All(cases, c => Assert.Contains(taken, ExistingInBefore(c)));
        // What Price froze names the package and its two lists, and each trade's package as the user wrote it.
        Assert.Equal([null, $"P1: DRAFT-1, DRAFT-2 | {taken}", ": DRAFT-3 | "], input["cases"]!.AsArray().Select(c => c!["iteration"] is { } i ? Lists(i) : null));
        Assert.Equal([("DRAFT-1", "P1"), ("DRAFT-2", "P1"), ("DRAFT-3", null), (taken, "P1")],
            input["cases"]![0]!["trades"]!.AsArray().Where(t => t!["tradeId"]!.GetValue<string>() is var id && (id.StartsWith("DRAFT-", StringComparison.Ordinal) || id == taken))
                .Select(t => (t!["tradeId"]!.GetValue<string>(), t["package"]?.GetValue<string>())).OrderBy(t => t.Item1.StartsWith('D') ? 0 : 1).ThenBy(t => t.Item1, StringComparer.Ordinal));
        Assert.Contains(input["overrides"]!.AsArray(), o => o!["entity"]!.GetValue<string>() == $"trade:{taken}" && o["field"]!.GetValue<string>() == "package" && o["value"]!.GetValue<string>() == "P1");

        // The case prices as the workspace with that membership typed in.
        var typed = service.Create("akira", "ABC/LE-01", Day1, 40);
        typed = service.Edit(typed.WorkspaceId, typed.Revision, "akira", "trade:DRAFT-3", "after", false);
        typed = service.Edit(typed.WorkspaceId, typed.Revision, "akira", $"trade:{taken}", "after", false);
        var (_, expected, _) = await Price(runs, store, service, typed);
        var id = Id(service, answer.State, $"P1: add DRAFT-1 + DRAFT-2 without {taken}");
        foreach (var metric in new[] { "cva", "fva", "cvaCharge", "pv" })
        {
            Assert.Equal(Row(expected, "base", metric)["before"]!.GetValue<decimal>(), Row(rows, id, metric)["before"]!.GetValue<decimal>());
            Assert.Equal(Row(expected, "base", metric)["after"]!.GetValue<decimal>(), Row(rows, id, metric)["after"]!.GetValue<decimal>());
        }
    }

    [Fact] // ADR-0016 note 90: a package of existing trades alone is a case that only takes them out, with no new trade in its After
    public async Task A_package_of_existing_trades_alone_is_a_case_that_only_takes_them_out()
    {
        var (service, runs, store) = Priced();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 40);
        var out2 = service.Page(ws.WorkspaceId, "akira", 0, 2, [], "existing").Rows.Select(r => r.TradeId).ToList();
        ws = Package(service, ws, "U1", [.. out2]);

        var answer = service.AddTrades(ws.WorkspaceId, ws.Revision, "akira");
        var (input, rows, _) = await Price(runs, store, service, answer.State);

        Assert.Equal(["add DRAFT-1", "add DRAFT-2", "add DRAFT-3", $"U1: take out {out2[0]} + {out2[1]}"], answer.Made);
        var unwind = PortfolioInput.Cases(input)[^1];
        Assert.Empty(NewInAfter(unwind));
        Assert.DoesNotContain(out2[0], ExistingInAfter(unwind));
        Assert.DoesNotContain(out2[1], ExistingInAfter(unwind));
        Assert.Contains(out2[1], ExistingInBefore(unwind));

        // It prices as the workspace with no new trade and the two out of After, typed in.
        var typed = service.Create("akira", "ABC/LE-01", Day1, 40);
        typed = service.Edit(typed.WorkspaceId, typed.Revision, "akira", "grid:new", "after", false);
        foreach (var trade in out2) typed = service.Edit(typed.WorkspaceId, typed.Revision, "akira", $"trade:{trade}", "after", false);
        var (_, expected, _) = await Price(runs, store, service, typed);
        var id = Id(service, answer.State, $"U1: take out {out2[0]} + {out2[1]}");
        foreach (var metric in new[] { "cva", "fva", "cvaCharge", "pv" })
            Assert.Equal(Row(expected, "base", metric)["after"]!.GetValue<decimal>(), Row(rows, id, metric)["after"]!.GetValue<decimal>());
    }

    [Fact] // ADR-0016 note 90: the packages are the cells', so add each again makes them again as the cells write them, and the ticks still say which trades are alone
    public void Add_each_again_makes_the_packages_again_as_the_cells_write_them()
    {
        var (service, _, _) = Priced();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 40);
        ws = Package(service, ws, "P1", "DRAFT-1", "DRAFT-2");
        ws = service.AddTrades(ws.WorkspaceId, ws.Revision, "akira").State;
        ws = service.Edit(ws.WorkspaceId, ws.Revision, "akira", "trade:DRAFT-3", "after", false);

        var stale = Assert.Throws<WorkspaceRefused>(() => service.Price(ws.WorkspaceId, ws.Revision, "akira", Guid.NewGuid().ToString()));
        var again = service.AddTrades(ws.WorkspaceId, ws.Revision, "akira");

        Assert.Equal(("AdditionSetChanged", "add each's cases are not what the Package column and A say. Out of date: add DRAFT-3. Run add each again to make the set anew."),
            (stale.Code, stale.Message));
        Assert.Equal(["P1: add DRAFT-1 + DRAFT-2"], again.Made);
        Assert.Equal(["P1: add DRAFT-1 + DRAFT-2", "add DRAFT-3"], again.Replaced);
        Assert.Equal("add each", again.State.Log[^1].Command?.Said);
        Assert.IsType<SubmissionResult.Accepted>(service.Price(ws.WorkspaceId, again.State.Revision, "akira", Guid.NewGuid().ToString()));
    }

    [Fact] // ADR-0016 notes 90 and 91, spine 1: what the Package cells say that no case can be made of is refused by the trades' names, and nothing is made, ticked or cleared
    public void What_the_package_cells_say_that_no_case_can_be_made_of_is_refused_by_name()
    {
        var (service, _, _) = Priced();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 40);
        var existing = service.Page(ws.WorkspaceId, "akira", 0, 1, [], "existing").Rows[0].TradeId;
        var at = Package(service, ws, "P1", "DRAFT-1", "DRAFT-2");
        // Each change is made, refused by add each, and taken back before the next.
        string Refused(Func<WorkspaceState, WorkspaceState> change, Func<WorkspaceState, WorkspaceState> back)
        {
            var changed = change(at);
            var refused = Assert.Throws<WorkspaceRefused>(() => service.AddTrades(changed.WorkspaceId, changed.Revision, "akira"));
            Assert.Equal(["Base"], service.View(changed.WorkspaceId, "akira").Cases.Select(c => c.Title));
            at = back(changed);
            return $"{refused.Code}: {refused.Message}";
        }
        WorkspaceState Set(WorkspaceState state, string entity, string field, JsonNode? value) => service.Edit(state.WorkspaceId, state.Revision, "akira", entity, field, value);

        Assert.Equal("CandidateNotTicked: DRAFT-1 is in the package P1 and has A clear, and a package adds New rows with A ticked. Tick A on it, or clear its Package.",
            Refused(w => Set(w, "trade:DRAFT-1", "after", false), w => Set(w, "trade:DRAFT-1", "after", true)));
        Assert.Equal("CandidateNotTicked: DRAFT-1 (P1) and DRAFT-2 (P1) are in a package and have A clear, and a package adds New rows with A ticked. Tick A on them, or clear their Package.",
            Refused(w => Set(w, "grid:new", "after", false), w => Set(w, "grid:new", "after", true)));
        Assert.Equal($"NotInAfter: {existing} is in the package P2 and not in After in the Base, so no case takes it out. Tick A on it, or clear its Package.",
            Refused(w => Package(service, Set(w, $"trade:{existing}", "after", false), "P2", existing), w => Package(service, Set(w, $"trade:{existing}", "after", true), null, existing)));
        Assert.Equal("PackageNamesDiffer: The packages P1 and p1 differ only in the case of their letters, and whether they are one is not guessed. Write one name on all of their trades.",
            Refused(w => Package(service, w, "p1", "DRAFT-3"), w => Package(service, w, null, "DRAFT-3")));
        var packaged = at;

        // A cell names one package or more separated by commas (note 95), and is refused where a name is empty or written
        // twice, whatever the case of its letters; a package is no value vary makes a case of.
        string RefusedCell(string cell)
        {
            var refused = Assert.Throws<WorkspaceRefused>(() => service.SetText(ws.WorkspaceId, packaged.Revision, "akira", "trade:DRAFT-3", "package", cell));
            return $"{refused.Code}: {refused.Message}";
        }
        const string rule = "InvalidValue: A Package cell names one package or more, separated by commas, such as P1 or P1, P2, or is empty for none.";
        Assert.Equal(rule, RefusedCell("P1,"));
        Assert.Equal(rule, RefusedCell("P1,, P2"));
        // A cell typed is trimmed as any cell is; a value a request gives is read as given.
        var spaced = Assert.Throws<WorkspaceRefused>(() => Set(packaged, "trade:DRAFT-3", "package", " P1, P2"));
        Assert.Equal(rule, $"{spaced.Code}: {spaced.Message}");
        Assert.Equal("InvalidValue: P1 is written twice in one Package cell; write each package once.", RefusedCell("P1, P2, P1"));
        Assert.Equal("InvalidValue: P1 and p1 are one name written twice in one Package cell, whatever the case of its letters; write each package once.", RefusedCell("P1, p1"));
        Assert.Equal(packaged.Revision, service.View(ws.WorkspaceId, "akira").Revision);
        var vary = Assert.Throws<WorkspaceRefused>(() => service.Vary(ws.WorkspaceId, packaged.Revision, "akira", "DRAFT-3 package", ["P1", "P2"]));
        Assert.Equal(("NotVaried", "DRAFT-3 package is the package add each makes a case of, and no value a case of vary prices. Write it in the Package column, then add each."),
            (vary.Code, vary.Message));
    }

    [Fact] // ADR-0016 note 91's table, row by row: each row is in every case as its B and A say, but for a package's new trades and the existing trades it takes out; a refused combination refuses add each, and Make package with it, by the row's name
    public async Task Every_combination_of_a_rows_B_A_and_package_is_what_note_91_tables()
    {
        var (service, runs, store) = Priced();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 40);
        var e = service.Page(ws.WorkspaceId, "akira", 0, 6, [], "existing").Rows.Select(r => r.TradeId).ToList();
        // New: DRAFT-1 in P1, DRAFT-2 alone, DRAFT-3 in Before alone. Existing: e0 in P1; e1 in After alone and in P1 (the
        // owner's choice, 2026-11-14: allowed, P1's case holds it in neither); e2 in neither; e3 in both; e4 in Before
        // alone; e5 in After alone.
        ws = Flag(service, Flag(service, ws, "DRAFT-3", "before", true), "DRAFT-3", "after", false);
        ws = Flag(service, ws, e[1], "before", false);
        ws = Flag(service, Flag(service, ws, e[2], "before", false), e[2], "after", false);
        ws = Flag(service, ws, e[4], "after", false);
        ws = Flag(service, ws, e[5], "before", false);
        ws = Package(service, ws, "P1", "DRAFT-1", e[0], e[1]);

        var answer = service.AddTrades(ws.WorkspaceId, ws.Revision, "akira");
        // A case of vary beside them holds every row as the Base does: a package changes only add each's cases.
        var varied = service.Vary(ws.WorkspaceId, answer.State.Revision, "akira", "NS-001 recovery", ["25%"]);
        var (input, _, _) = await Price(runs, store, service, varied.State);

        Assert.Equal([$"P1: add DRAFT-1 without {e[0]} + {e[1]}", "add DRAFT-2"], answer.Made);
        var all = PortfolioInput.Cases(input);
        Assert.Equal(["base", "add", "add", "vary"], all.Select(c => c.Kind));
        Assert.All(new[] { "DRAFT-1", "DRAFT-2", "DRAFT-3" }.Concat(e), id => Assert.Equal(Sides(all[0], id), Sides(all[3], id)));
        var cases = all.Take(3).ToList();
        // e1, e4, e5 and DRAFT-3, a New row in Before read as an existing trade (note 96), are the change the Base makes,
        // which each case's title says it carries (docs/03 §5); P1's case takes e1 out, and does not say it.
        Assert.Equal(["Base", $"P1: add DRAFT-1 without {e[0]} + {e[1]} · with existing {e[4]} and new DRAFT-3 out of After and existing {e[5]} in After only",
            $"add DRAFT-2 · with existing {e[4]} and new DRAFT-3 out of After and existing {e[1]}, {e[5]} in After only"], cases.Select(c => c.Title));
        Assert.Equal([(false, true), (false, true), null], cases.Select(c => Sides(c, "DRAFT-1")));
        Assert.Equal([(false, true), null, (false, true)], cases.Select(c => Sides(c, "DRAFT-2")));
        Assert.All(cases, c => Assert.Equal((true, false), Sides(c, "DRAFT-3")));
        Assert.Equal([(true, true), (true, false), (true, true)], cases.Select(c => Sides(c, e[0])));
        Assert.Equal([(false, true), null, (false, true)], cases.Select(c => Sides(c, e[1])));
        Assert.All(cases, c => Assert.Null(Sides(c, e[2])));
        Assert.All(cases, c => Assert.Equal((true, true), Sides(c, e[3])));
        Assert.All(cases, c => Assert.Equal((true, false), Sides(c, e[4])));
        Assert.All(cases, c => Assert.Equal((false, true), Sides(c, e[5])));

        // The refused rows, each written and taken back: add each, and Make package however few rows it writes, say the
        // row, and nothing is written.
        var at = varied.State;
        string Refused(Func<WorkspaceState, WorkspaceState> change, Func<WorkspaceState, AddAnswer> run, Func<WorkspaceState, WorkspaceState> back)
        {
            var changed = change(at);
            var refused = Assert.Throws<WorkspaceRefused>(() => run(changed));
            Assert.Equal(changed.Revision, service.View(ws.WorkspaceId, "akira").Revision);
            at = back(changed);
            return $"{refused.Code}: {refused.Message}";
        }
        AddAnswer AddEach(WorkspaceState w) => service.AddTrades(w.WorkspaceId, w.Revision, "akira");
        // DRAFT-3, in Before, is read as an existing trade (note 96): in a package, it is one After does not hold.
        Assert.Equal("NotInAfter: DRAFT-3 is in the package P2 and not in After in the Base, so no case takes it out. Tick A on it, or clear its Package.",
            Refused(w => Package(service, w, "P2", "DRAFT-3"), AddEach, w => Package(service, w, null, "DRAFT-3")));
        Assert.Equal($"NotInAfter: {e[2]} is in the package P2 and not in After in the Base, so no case takes it out. Tick A on it, or clear its Package.",
            Refused(w => Package(service, w, "P2", e[2]), AddEach, w => Package(service, w, null, e[2])));
        Assert.Equal($"NotInAfter: {e[4]} is in the package P3 and not in After in the Base, so no case takes it out. Tick A on it, or clear its Package.",
            Refused(w => Package(service, w, "P3", e[4]), w => service.AddTrades(w.WorkspaceId, w.Revision, "akira", new("P2", ["DRAFT-2"])), w => Package(service, w, null, e[4])));

        // A New row with B and A clear is in no case, and refused in a package as one the package would add.
        at = Flag(service, at, "DRAFT-3", "before", false);
        Assert.Equal("CandidateNotTicked: DRAFT-3 is in the package P2 and has A clear, and a package adds New rows with A ticked. Tick A on it, or clear its Package.",
            Refused(w => Package(service, w, "P2", "DRAFT-3"), AddEach, w => Package(service, w, null, "DRAFT-3")));
        var again = AddEach(at);
        var (cleared, _, _) = await Price(runs, store, service, again.State);
        Assert.Equal(answer.Made, again.Made);
        Assert.All(PortfolioInput.Cases(cleared), c => Assert.Null(Sides(c, "DRAFT-3")));
    }

    [Fact] // ADR-0016 note 95 (owner, 2026-11-15): a row in several packages is in each of their cases, so two alternatives may add one new trade or take out one existing trade; every name of a cell is read, and a refusal names every package its row is in
    public async Task A_row_in_several_packages_is_in_each_of_their_cases()
    {
        var (service, runs, store) = Priced();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 40);
        var e = service.Page(ws.WorkspaceId, "akira", 0, 2, [], "existing").Rows.Select(r => r.TradeId).ToList();
        // DRAFT-1 and e0 in P1 and P2, DRAFT-2 in P2, e1 in P1, DRAFT-3 in none.
        ws = Package(service, ws, "P1, P2", "DRAFT-1", e[0]);
        ws = Package(service, Package(service, ws, "P2", "DRAFT-2"), "P1", e[1]);

        var answer = service.AddTrades(ws.WorkspaceId, ws.Revision, "akira");
        var (input, _, _) = await Price(runs, store, service, answer.State);

        Assert.Equal([$"P1: add DRAFT-1 without {e[0]} + {e[1]}", $"P2: add DRAFT-1 + DRAFT-2 without {e[0]}", "add DRAFT-3"], answer.Made);
        Assert.Equal([new PackageView("P1", ["DRAFT-1"], [e[0], e[1]]), new PackageView("P2", ["DRAFT-1", "DRAFT-2"], [e[0]])],
            service.View(ws.WorkspaceId, "akira").Packages, new PackageViewComparer());
        Assert.Equal("P3", service.View(ws.WorkspaceId, "akira").NextPackage);
        // Each case adds its own new trades alone, and takes its own existing trades out of After alone.
        var cases = PortfolioInput.Cases(input);
        Assert.Equal([["DRAFT-1", "DRAFT-2", "DRAFT-3"], ["DRAFT-1"], ["DRAFT-1", "DRAFT-2"], ["DRAFT-3"]], cases.Select(NewInAfter));
        Assert.Equal([true, false, false, true], cases.Select(c => ExistingInAfter(c).Contains(e[0])));
        Assert.Equal([true, false, true, true], cases.Select(c => ExistingInAfter(c).Contains(e[1])));
        Assert.All(cases, c => Assert.Contains(e[0], ExistingInBefore(c)));
        // What Price froze holds each cell as written.
        Assert.Equal([("DRAFT-1", "P1, P2"), ("DRAFT-2", "P2"), ("DRAFT-3", null)],
            input["cases"]![0]!["trades"]!.AsArray().Where(t => t!["tradeId"]!.GetValue<string>().StartsWith("DRAFT-", StringComparison.Ordinal))
                .Select(t => (t!["tradeId"]!.GetValue<string>(), t["package"]?.GetValue<string>())).OrderBy(t => t.Item1, StringComparer.Ordinal));

        // A refusal names the packages of each row it names; two names that differ only in case are one clash wherever
        // they are written.
        var at = answer.State;
        string Refused(Func<WorkspaceState, WorkspaceState> change, Func<WorkspaceState, WorkspaceState> back)
        {
            var changed = change(at);
            var refused = Assert.Throws<WorkspaceRefused>(() => service.AddTrades(changed.WorkspaceId, changed.Revision, "akira"));
            at = back(changed);
            return $"{refused.Code}: {refused.Message}";
        }
        Assert.Equal("CandidateNotTicked: DRAFT-1 is in the packages P1 and P2 and has A clear, and a package adds New rows with A ticked. Tick A on it, or clear its Package.",
            Refused(w => Flag(service, w, "DRAFT-1", "after", false), w => Flag(service, w, "DRAFT-1", "after", true)));
        Assert.Equal("CandidateNotTicked: DRAFT-1 (P1, P2) and DRAFT-2 (P2) are in packages and have A clear, and a package adds New rows with A ticked. Tick A on them, or clear their Package.",
            Refused(w => Flag(service, Flag(service, w, "DRAFT-1", "after", false), "DRAFT-2", "after", false), w => Flag(service, Flag(service, w, "DRAFT-1", "after", true), "DRAFT-2", "after", true)));
        Assert.Equal($"NotInAfter: {e[0]} is in the packages P1 and P2 and not in After in the Base, so no case takes it out. Tick A on it, or clear its Package.",
            Refused(w => Flag(service, w, e[0], "after", false), w => Flag(service, w, e[0], "after", true)));
        Assert.Equal("PackageNamesDiffer: The packages P2 and p2 differ only in the case of their letters, and whether they are one is not guessed. Write one name on all of their trades.",
            Refused(w => Package(service, w, "P3, p2", "DRAFT-3"), w => Package(service, w, null, "DRAFT-3")));

        // A package written into one more cell is a set the cells no longer say, and Price names the case it changes.
        var grown = Package(service, at, "P1, P2", e[1]);
        var stale = Assert.Throws<WorkspaceRefused>(() => service.Price(grown.WorkspaceId, grown.Revision, "akira", Guid.NewGuid().ToString()));
        Assert.Equal(("AdditionSetChanged", $"add each's cases are not what the Package column and A say. Missing: P2: add DRAFT-1 + DRAFT-2 without {e[0]} + {e[1]}. "
            + $"Out of date: P2: add DRAFT-1 + DRAFT-2 without {e[0]}. Run add each again to make the set anew."), (stale.Code, stale.Message));
    }

    [Fact] // ADR-0016 note 90, docs/03 §5: Price refuses packages no case prices, and a set the cells and A no longer say, naming its cases
    public void Price_refuses_packages_no_case_prices_and_a_set_the_cells_no_longer_say()
    {
        var (service, _, _) = Priced();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 40);
        string Refused(WorkspaceState state)
        {
            var refused = Assert.Throws<WorkspaceRefused>(() => service.Price(state.WorkspaceId, state.Revision, "akira", Guid.NewGuid().ToString()));
            return $"{refused.Code}: {refused.Message}";
        }
        WorkspaceState Set(WorkspaceState state, string entity, string field, JsonNode? value) => service.Edit(state.WorkspaceId, state.Revision, "akira", entity, field, value);
        var packaged = Package(service, ws, "P1", "DRAFT-1", "DRAFT-2");

        Assert.Equal("PackagesNotMade: The Package column names P1, and no case of add each prices it. Run add each to make the cases, or clear the column with reset all package to price without them.",
            Refused(packaged));
        var made = service.AddTrades(ws.WorkspaceId, packaged.Revision, "akira").State;
        var grown = Package(service, made, "P1", "DRAFT-3");
        Assert.Equal("AdditionSetChanged: add each's cases are not what the Package column and A say. Missing: P1: add DRAFT-1 + DRAFT-2 + DRAFT-3. "
            + "Out of date: P1: add DRAFT-1 + DRAFT-2, add DRAFT-3. Run add each again to make the set anew.",
            Refused(grown));
        // What the cells say that no set could be made of is refused as add each refuses it.
        var unticked = Set(Package(service, grown, null, "DRAFT-3"), "trade:DRAFT-2", "after", false);
        Assert.Equal("CandidateNotTicked: DRAFT-2 is in the package P1 and has A clear, and a package adds New rows with A ticked. Tick A on it, or clear its Package.",
            Refused(unticked));
        // Where the cells now make no case, add each again would refuse too: the sentence says to remove the cases instead.
        var none = Set(Package(service, Set(unticked, "trade:DRAFT-2", "after", true), null, "DRAFT-1", "DRAFT-2"), "grid:new", "after", false);
        Assert.Equal("AdditionSetChanged: add each's cases are not what the Package column and A say, which make no case now. "
            + "Out of date: P1: add DRAFT-1 + DRAFT-2, add DRAFT-3. Remove them with case remove or in Changes, or tick A on a new trade with B clear or write a package and run add each again.",
            Refused(none));
        // With no case of add each and the column clear, Price prices the Base: no package is priced by a guess.
        var cleared = service.ClearCases(ws.WorkspaceId, none.Revision, "akira").State;
        Assert.IsType<SubmissionResult.Accepted>(service.Price(ws.WorkspaceId, cleared.Revision, "akira", Guid.NewGuid().ToString()));
    }

    [Fact] // ADR-0010 §3's note of 2026-11-08 with ADR-0016 note 90: add each over the cap of cases is refused by how much, and packages are the way under it
    public void Add_each_over_the_cap_of_cases_is_refused_by_how_much_and_a_package_brings_it_under()
    {
        var (service, _, _) = Priced(new PortfolioOptions(CaseCap: 2));
        var ws = service.Create("akira", "ABC/LE-01", Day1, 40);

        var over = Assert.Throws<WorkspaceRefused>(() => service.AddTrades(ws.WorkspaceId, ws.Revision, "akira"));
        var made = service.View(ws.WorkspaceId, "akira").Cases.Select(c => c.Title).ToList();
        var packaged = Package(service, ws, "P1", "DRAFT-1", "DRAFT-2", "DRAFT-3");
        var under = service.AddTrades(ws.WorkspaceId, packaged.Revision, "akira");

        Assert.Equal(("TooManyCases", "add each would make 3 case(s), and Price prices 2 at most, the Base among them: 2 over. "
            + "Put new trades together in packages, or clear A on new trades in none."), (over.Code, over.Message));
        Assert.Equal(["Base"], made);
        Assert.Equal(["P1: add DRAFT-1 + DRAFT-2 + DRAFT-3"], under.Made);
        Assert.IsType<SubmissionResult.Accepted>(service.Price(ws.WorkspaceId, under.State.Revision, "akira", Guid.NewGuid().ToString()));
    }

    [Fact] // ADR-0016 notes 90 and 95: Make package adds the package to the trades it names and makes the set anew in one command, naming a package the next free name and saying the packages a trade stays in
    public void A_package_given_is_written_on_its_trades_and_the_set_made_in_one_command()
    {
        var (service, _, _) = Priced();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 40);
        var taken = service.Page(ws.WorkspaceId, "akira", 0, 1, [], "existing").Rows[0].TradeId;

        // An id is read whatever the case of its letters, as a key is.
        var first = service.AddTrades(ws.WorkspaceId, ws.Revision, "akira", new(null, ["draft-1", "DRAFT-2", taken]));
        var second = service.AddTrades(ws.WorkspaceId, first.State.Revision, "akira", new(null, ["DRAFT-2", "DRAFT-3"]));

        Assert.Equal($"P1 on DRAFT-1, DRAFT-2 and {taken}", first.Wrote);
        Assert.Equal([$"P1: add DRAFT-1 + DRAFT-2 without {taken}", "add DRAFT-3"], first.Made);
        Assert.Equal($"add each: package P1 on DRAFT-1, DRAFT-2 and {taken}", first.State.Log[^1].Command?.Said);
        // DRAFT-2 stays in P1 and is in P2 too (note 95): each case adds it.
        Assert.Equal("P2 on DRAFT-2 (also in P1) and DRAFT-3", second.Wrote);
        Assert.Equal("add each: package P2 on DRAFT-2 (also in P1) and DRAFT-3", second.State.Log[^1].Command?.Said);
        Assert.Equal([$"P1: add DRAFT-1 + DRAFT-2 without {taken}", "P2: add DRAFT-2 + DRAFT-3"], second.Made);
        Assert.Equal([new PackageView("P1", ["DRAFT-1", "DRAFT-2"], [taken]), new PackageView("P2", ["DRAFT-2", "DRAFT-3"], [])],
            service.View(ws.WorkspaceId, "akira").Packages, new PackageViewComparer());
        // The view names the package a name left out would be: what Make package… offers.
        Assert.Equal("P3", service.View(ws.WorkspaceId, "akira").NextPackage);
        Assert.Equal(["P1", "P1, P2", "P2"], service.Page(ws.WorkspaceId, "akira", 0, 3, [], "new").Rows.Select(r => r.Package));
        Assert.Equal($"Wrote P2 on DRAFT-2 (also in P1) and DRAFT-3; made 2 case(s): P1: add DRAFT-1 + DRAFT-2 without {taken}, P2: add DRAFT-2 + DRAFT-3; replaced P1: add DRAFT-1 + DRAFT-2 without {taken}, add DRAFT-3.",
            PortfolioCommandContracts.Added(second));
        // Given again, a trade already in the package is left as it is, and nothing but the set is made anew.
        var again = service.AddTrades(ws.WorkspaceId, second.State.Revision, "akira", new("P2", ["DRAFT-2"]));
        Assert.Equal("P2 on DRAFT-2 (also in P1)", again.Wrote);
        Assert.Equal(["P1", "P1, P2", "P2"], service.Page(ws.WorkspaceId, "akira", 0, 3, [], "new").Rows.Select(r => r.Package));
        Assert.Equal(second.Made, again.Made);
        second = again;

        string Refused(PackageGiven given)
        {
            var refused = Assert.Throws<WorkspaceRefused>(() => service.AddTrades(ws.WorkspaceId, second.State.Revision, "akira", given));
            return $"{refused.Code}: {refused.Message}";
        }
        Assert.Equal("UnknownTarget: No trade DRAFT-9 in this workspace.", Refused(new("P3", ["DRAFT-9"])));
        Assert.Equal("GivenTwice: DRAFT-1 is named twice.", Refused(new("P3", ["DRAFT-1", "draft-1"])));
        Assert.Equal("InvalidValue: A package is one name with no comma, such as P1.", Refused(new("P3,P4", ["DRAFT-1"])));
        // Added to a cell that names it in other letters, a name is refused as that cell would be, not taken as the same.
        Assert.Equal("InvalidValue: DRAFT-1's Package would read P1, p1: P1 and p1 are one name written twice in one Package cell, whatever the case of its letters; write each package once.",
            Refused(new("p1", ["DRAFT-1"])));
        Assert.Equal("NothingGiven: A package takes one trade at least, and none is given for P3.", Refused(new("P3", [])));
        Assert.Equal(second.State.Revision, service.View(ws.WorkspaceId, "akira").Revision);

        // The packages a trade stays in are named in the order of the workspace's packages, as the dialog lists them,
        // whatever order its cell writes them in; the cell keeps its own order.
        var reordered = Package(service, second.State, "P2, P1", "DRAFT-3");
        var third = service.AddTrades(ws.WorkspaceId, reordered.Revision, "akira", new("P3", ["DRAFT-3"]));
        Assert.Equal("P3 on DRAFT-3 (also in P1 and P2)", third.Wrote);
        Assert.Equal(["P1", "P1, P2", "P2, P1, P3"], service.Page(ws.WorkspaceId, "akira", 0, 3, [], "new").Rows.Select(r => r.Package));
    }

    sealed class PackageViewComparer : IEqualityComparer<PackageView>
    {
        public bool Equals(PackageView? x, PackageView? y) => x is not null && y is not null && x.Name == y.Name && x.Adds.SequenceEqual(y.Adds) && x.TakesOut.SequenceEqual(y.TakesOut);
        public int GetHashCode(PackageView obj) => obj.Name.GetHashCode(StringComparison.Ordinal);
    }

    [Fact] // ADR-0016 notes 93 and 94: the next free name is the first of P1, P2… that no trade's package is, whatever the case of its letters, and a package given no name is given it
    public void The_next_free_name_skips_every_name_written_whatever_the_case_of_its_letters()
    {
        var (service, _, _) = Priced();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 40);
        ws = Package(service, Package(service, ws, "p1", "DRAFT-1"), "P3", "DRAFT-2");

        var offered = service.View(ws.WorkspaceId, "akira").NextPackage;
        var given = service.AddTrades(ws.WorkspaceId, ws.Revision, "akira", new(null, ["DRAFT-3"]));

        Assert.Equal("P2", offered);
        Assert.Equal("P2 on DRAFT-3", given.Wrote);
        Assert.Equal(["p1: add DRAFT-1", "P3: add DRAFT-2", "P2: add DRAFT-3"], given.Made);
        Assert.Equal("P4", service.View(ws.WorkspaceId, "akira").NextPackage);
    }

    [Fact] // ADR-0010 §7 with §3: Copy quote holds a line per case of the Run, each named, with its CVA Charge and PV
    public async Task Copy_quote_holds_a_line_per_case_with_its_charge_and_pv()
    {
        var (service, runs, store) = Priced();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 40);
        ws = service.Vary(ws.WorkspaceId, ws.Revision, "akira", "CSA-A threshold", ["200mm"]).State;
        var (_, rows, run) = await Price(runs, store, service, ws);
        var raised = Id(service, ws, "CSA-A threshold 200mm");
        string Diff(string @case, string metric) => Row(rows, @case, metric)["diff"]!.GetValue<decimal>().ToString("#,0.00", System.Globalization.CultureInfo.InvariantCulture);

        var quote = XvaQuote.Of(run, store);

        Assert.Equal([
            $"Base: CVA Charge {Diff("base", "cvaCharge")} · PV {Diff("base", "pv")}",
            $"CSA-A threshold 200mm: CVA Charge {Diff(raised, "cvaCharge")} · PV {Diff(raised, "pv")}",
        ], quote.Text.Split('\n').Select(l => l.TrimEnd('\r')).Skip(2));
        Assert.Contains("<td>CSA-A threshold 200mm</td>", quote.Html);
    }

    [Theory] // ADR-0010 §9, spine 1: a value is written in the form a cell reads back to the same value, never bare where bare is millions
    [InlineData(500, "0.5k")]
    [InlineData(1_234.567, "1.234567k")]
    [InlineData(5_000_000, "5mm")]
    [InlineData(1_234_567, "1.234567mm")]
    [InlineData(-2_500_000, "-2.5mm")]
    [InlineData(1_500_000_000, "1.5bn")]
    [InlineData(0, "0")]
    public void An_amount_is_written_as_it_reads_back(double amount, string written)
    {
        var cell = new CellName("csa:CSA-A", "counterpartyMta", "CSA-A", "mta", new FieldSpec(FieldForm.SignedAmount));
        var value = JsonValue.Create((decimal)amount);

        Assert.Equal(written, PortfolioCells.Write(cell, value));
        Assert.Equal((decimal)amount, PortfolioCells.Read(cell, written)!.GetValue<decimal>());
    }

    [Fact] // ADR-0010 §3: a workspace with cases is read through a reading; read as stored, it is refused, never read as some layer's value
    public void A_workspace_with_cases_is_read_through_a_reading_only()
    {
        var (service, _, _) = Priced();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 40);
        ws = service.Vary(ws.WorkspaceId, ws.Revision, "akira", "CSA-A threshold", ["5mm"]).State;

        Assert.Throws<InvalidOperationException>(() => PortfolioMaterialiser.Csas(ws));
        Assert.Equal(0m, PortfolioMaterialiser.Csas(PortfolioCases.Base(ws)).Single(c => c.CsaId == "CSA-A").CounterpartyThreshold);
    }
}
