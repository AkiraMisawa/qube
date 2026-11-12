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

    [Fact] // docs/03 §5's DEFAULT: a candidate in Before is refused by name, never unticked, and nothing is made
    public void A_candidate_in_before_is_refused_by_name_and_nothing_is_made()
    {
        var (service, _, _) = Priced();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 40);
        ws = service.Edit(ws.WorkspaceId, ws.Revision, "akira", "trade:DRAFT-2", "before", true);

        var refused = Assert.Throws<WorkspaceRefused>(() => service.AddTrades(ws.WorkspaceId, ws.Revision, "akira"));
        var none = service.Edit(ws.WorkspaceId, ws.Revision, "akira", "grid:new", "after", false);
        var nothing = Assert.Throws<WorkspaceRefused>(() => service.AddTrades(none.WorkspaceId, none.Revision, "akira"));

        Assert.Equal(("CandidateInBefore", "DRAFT-2 is in Before, and a new trade is priced alone only out of Before. Clear B on it, or A to leave it out, then add each again."),
            (refused.Code, refused.Message));
        Assert.Equal("NothingToAdd", nothing.Code);
        Assert.Equal(none.Revision, service.View(none.WorkspaceId, "akira").Revision);
        Assert.Equal(["Base"], service.View(none.WorkspaceId, "akira").Cases.Select(c => c.Title));
    }

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

        Assert.Equal(("AdditionSetChanged", "add each's cases add DRAFT-1, DRAFT-2, and DRAFT-3 is ticked and in no case. Run add each again to make the set anew."),
            (ticked.Code, ticked.Message));
        Assert.Equal("add each's cases add DRAFT-1, DRAFT-2, and DRAFT-3 is ticked and in no case; DRAFT-1 is in Before. Run add each again to make the set anew, with B clear on each.",
            before.Message);
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

    [Fact] // ADR-0016 §3, note 37: an iteration adds its candidates together and takes its trades out of After, in one case that names them all; a ticked candidate none names is one alone
    public async Task An_iteration_adds_its_candidates_together_and_takes_trades_out_in_one_case()
    {
        var (service, runs, store) = Priced();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 40);
        var taken = service.Page(ws.WorkspaceId, "akira", 0, 1, [], "existing").Rows[0].TradeId;

        // An id is read whatever the case of its letters, as a key is.
        var answer = service.AddTrades(ws.WorkspaceId, ws.Revision, "akira", [new(["DRAFT-1", "draft-2"], [taken.ToLowerInvariant()])]);
        var (input, rows, _) = await Price(runs, store, service, answer.State);

        Assert.Equal([$"add DRAFT-1 + DRAFT-2 without {taken}", "add DRAFT-3"], answer.Made);
        var made = service.View(ws.WorkspaceId, "akira").Cases;
        Assert.Equal([null, $"DRAFT-1, DRAFT-2 | {taken}", "DRAFT-3 | "],
            made.Select(c => c.Iteration is { } i ? $"{string.Join(", ", i.Add)} | {string.Join(", ", i.Without)}" : null));
        var cases = PortfolioInput.Cases(input);
        Assert.Equal([["DRAFT-1", "DRAFT-2", "DRAFT-3"], ["DRAFT-1", "DRAFT-2"], ["DRAFT-3"]], cases.Select(NewInAfter));
        // The trade taken out leaves this case's After alone, and stays in its Before: the difference is the whole change.
        Assert.Equal([true, false, true], cases.Select(c => ExistingInAfter(c).Contains(taken)));
        Assert.All(cases, c => Assert.Contains(taken, ExistingInBefore(c)));
        Assert.Equal($"DRAFT-1, DRAFT-2 | {taken}",
            $"{string.Join(", ", input["cases"]![1]!["iteration"]!["add"]!.AsArray())} | {string.Join(", ", input["cases"]![1]!["iteration"]!["without"]!.AsArray())}");

        // The case prices as the workspace with that membership typed in.
        var typed = service.Create("akira", "ABC/LE-01", Day1, 40);
        typed = service.Edit(typed.WorkspaceId, typed.Revision, "akira", "trade:DRAFT-3", "after", false);
        typed = service.Edit(typed.WorkspaceId, typed.Revision, "akira", $"trade:{taken}", "after", false);
        var (_, expected, _) = await Price(runs, store, service, typed);
        var id = Id(service, ws, $"add DRAFT-1 + DRAFT-2 without {taken}");
        foreach (var metric in new[] { "cva", "fva", "cvaCharge", "pv" })
        {
            Assert.Equal(Row(expected, "base", metric)["before"]!.GetValue<decimal>(), Row(rows, id, metric)["before"]!.GetValue<decimal>());
            Assert.Equal(Row(expected, "base", metric)["after"]!.GetValue<decimal>(), Row(rows, id, metric)["after"]!.GetValue<decimal>());
        }
    }

    [Fact] // ADR-0016 §3, note 37: a candidate may be in several iterations, the set given is the whole set, and the set made before is replaced
    public void The_iterations_given_are_the_whole_set_and_a_candidate_may_be_in_several()
    {
        var (service, _, _) = Priced();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 40);
        ws = service.AddTrades(ws.WorkspaceId, ws.Revision, "akira").State;

        var answer = service.AddTrades(ws.WorkspaceId, ws.Revision, "akira", [new(["DRAFT-1"], []), new(["DRAFT-1", "DRAFT-2"], []), new(["DRAFT-3"], [])]);

        Assert.Equal(["add DRAFT-1", "add DRAFT-1 + DRAFT-2", "add DRAFT-3"], answer.Made);
        Assert.Equal(["add DRAFT-1", "add DRAFT-2", "add DRAFT-3"], answer.Replaced);
        Assert.Equal(["Base", "add DRAFT-1", "add DRAFT-1 + DRAFT-2", "add DRAFT-3"], service.View(ws.WorkspaceId, "akira").Cases.Select(c => c.Title));
        // The command has no line that types it, so the log names it by the iterations given that no case made before
        // is, the first three and how many more, then how many are as before: what undo would take back is what is new.
        Assert.Equal("add each: DRAFT-1 + DRAFT-2, with 2 as before", answer.State.Log[^1].Command?.Said);
        var more = service.AddTrades(ws.WorkspaceId, answer.State.Revision, "akira", [new(["DRAFT-1"], []), new(["DRAFT-1", "DRAFT-2"], []), new(["DRAFT-3"], []),
            new(["DRAFT-2"], []), new(["DRAFT-2", "DRAFT-3"], []), new(["DRAFT-1", "DRAFT-3"], []), new(["DRAFT-1", "DRAFT-2", "DRAFT-3"], [])]);
        Assert.Equal("add each: DRAFT-2; DRAFT-2 + DRAFT-3; DRAFT-1 + DRAFT-3 and 1 more, with 3 as before", more.State.Log[^1].Command?.Said);
        var same = service.AddTrades(ws.WorkspaceId, more.State.Revision, "akira", [new(["DRAFT-2", "DRAFT-1"], []), new(["DRAFT-3"], [])]);
        Assert.Equal("add each: 2 as before", same.State.Log[^1].Command?.Said);
    }

    [Fact] // ADR-0016 §3, note 37 (corrected in review): with keep, the iterations given are made beside the cases made, which stay as they are; one made already is refused by its case's name
    public async Task With_keep_the_iterations_given_are_made_beside_the_cases_made()
    {
        var (service, runs, store) = Priced();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 40);
        var taken = service.Page(ws.WorkspaceId, "akira", 0, 1, [], "existing").Rows[0].TradeId;
        ws = service.AddTrades(ws.WorkspaceId, ws.Revision, "akira").State;
        var alone = Id(service, ws, "add DRAFT-1");

        var answer = service.AddTrades(ws.WorkspaceId, ws.Revision, "akira", [new(["DRAFT-1", "DRAFT-2"], [taken])], keep: true);
        var again = Assert.Throws<WorkspaceRefused>(() => service.AddTrades(ws.WorkspaceId, answer.State.Revision, "akira", [new(["DRAFT-2", "DRAFT-1"], [taken])], keep: true));
        var none = Assert.Throws<WorkspaceRefused>(() => service.AddTrades(ws.WorkspaceId, answer.State.Revision, "akira", keep: true));
        var (input, _, _) = await Price(runs, store, service, answer.State);

        Assert.Equal([$"add DRAFT-1 + DRAFT-2 without {taken}"], answer.Made);
        Assert.Empty(answer.Replaced);
        Assert.Equal(["Base", "add DRAFT-1", "add DRAFT-2", "add DRAFT-3", $"add DRAFT-1 + DRAFT-2 without {taken}"], service.View(ws.WorkspaceId, "akira").Cases.Select(c => c.Title));
        // A case kept is the case it was, not one made again under its title.
        Assert.Equal(alone, Id(service, answer.State, "add DRAFT-1"));
        Assert.Equal($"add each: DRAFT-1 + DRAFT-2 without {taken}, beside the cases made", answer.State.Log[^1].Command?.Said);
        Assert.Equal(("AlreadyMade", $"The case add DRAFT-1 + DRAFT-2 without {taken} is made already."), (again.Code, again.Message));
        Assert.Equal(("NothingGiven", "keep makes the iterations given beside the cases made, and none is given. Give one, or leave keep out for a case per New row with A ticked."),
            (none.Code, none.Message));
        var cases = PortfolioInput.Cases(input);
        Assert.Equal([["DRAFT-1", "DRAFT-2", "DRAFT-3"], ["DRAFT-1"], ["DRAFT-2"], ["DRAFT-3"], ["DRAFT-1", "DRAFT-2"]], cases.Select(NewInAfter));
        Assert.Equal([true, true, true, true, false], cases.Select(c => ExistingInAfter(c).Contains(taken)));
    }

    [Fact] // ADR-0016 §3, note 37 (corrected in review), ADR-0010 §3's note: a case kept is not read again, so a stale one refuses no new case and stays Price's to refuse
    public async Task With_keep_a_stale_case_refuses_no_new_one_and_stays_prices_to_refuse()
    {
        var (service, runs, store) = Priced();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 40);
        ws = service.AddTrades(ws.WorkspaceId, ws.Revision, "akira").State;
        // The case add DRAFT-3 is stale from here: its trade is no longer ticked.
        ws = service.Edit(ws.WorkspaceId, ws.Revision, "akira", "trade:DRAFT-3", "after", false);

        var whole = Assert.Throws<WorkspaceRefused>(() => service.AddTrades(ws.WorkspaceId, ws.Revision, "akira",
            [new(["DRAFT-1"], []), new(["DRAFT-2"], []), new(["DRAFT-3"], []), new(["DRAFT-1", "DRAFT-2"], [])]));
        var kept = service.AddTrades(ws.WorkspaceId, ws.Revision, "akira", [new(["DRAFT-1", "DRAFT-2"], [])], keep: true);
        var stale = Assert.Throws<WorkspaceRefused>(() => service.Price(ws.WorkspaceId, kept.State.Revision, "akira", Guid.NewGuid().ToString()));
        var removed = service.RemoveCase(ws.WorkspaceId, kept.State.Revision, "akira", "add DRAFT-3").State;
        var (input, _, _) = await Price(runs, store, service, removed);

        // The whole set given again names the stale case, which the caller who adds one case did not name.
        Assert.Equal("CandidateNotTicked", whole.Code);
        Assert.Equal(["add DRAFT-1 + DRAFT-2"], kept.Made);
        Assert.Equal(("AdditionSetChanged", "add each's cases add DRAFT-1, DRAFT-2, DRAFT-3, and DRAFT-3 is in a case and no longer ticked. Run add each again to make the set anew."),
            (stale.Code, stale.Message));
        Assert.Equal([["DRAFT-1", "DRAFT-2"], ["DRAFT-1"], ["DRAFT-2"], ["DRAFT-1", "DRAFT-2"]], PortfolioInput.Cases(input).Select(NewInAfter));
    }

    [Fact] // ADR-0016 §3, note 37 (corrected in review), docs/03 §5: with keep, a ticked New row no case names is a case alone, and it is off in the cases kept, so each still adds its own trades alone
    public async Task With_keep_a_candidate_that_joins_the_set_is_a_case_alone_and_off_in_the_cases_kept()
    {
        var (service, runs, store) = Priced();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 40);
        ws = service.Edit(ws.WorkspaceId, ws.Revision, "akira", "trade:DRAFT-3", "after", false);
        ws = service.AddTrades(ws.WorkspaceId, ws.Revision, "akira").State;
        ws = service.Edit(ws.WorkspaceId, ws.Revision, "akira", "trade:DRAFT-3", "after", true);

        var answer = service.AddTrades(ws.WorkspaceId, ws.Revision, "akira", [new(["DRAFT-1", "DRAFT-2"], [])], keep: true);
        var (input, _, _) = await Price(runs, store, service, answer.State);

        Assert.Equal(["add DRAFT-1 + DRAFT-2", "add DRAFT-3"], answer.Made);
        Assert.Equal(["Base", "add DRAFT-1", "add DRAFT-2", "add DRAFT-1 + DRAFT-2", "add DRAFT-3"], PortfolioInput.Cases(input).Select(c => c.Title));
        Assert.Equal([["DRAFT-1", "DRAFT-2", "DRAFT-3"], ["DRAFT-1"], ["DRAFT-2"], ["DRAFT-1", "DRAFT-2"], ["DRAFT-3"]], PortfolioInput.Cases(input).Select(NewInAfter));
    }

    [Fact] // ADR-0016 §3, note 37: what an iteration cannot name is refused by the trade's name, and nothing is made, ticked or cleared
    public void An_iteration_that_names_what_it_cannot_is_refused_by_name()
    {
        var (service, _, _) = Priced();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 40);
        var existing = service.Page(ws.WorkspaceId, "akira", 0, 2, [], "existing").Rows.Select(r => r.TradeId).ToList();
        ws = service.Edit(ws.WorkspaceId, ws.Revision, "akira", "trade:DRAFT-3", "after", false);
        ws = service.Edit(ws.WorkspaceId, ws.Revision, "akira", "trade:DRAFT-2", "before", true);
        ws = service.Edit(ws.WorkspaceId, ws.Revision, "akira", $"trade:{existing[1]}", "after", false);
        string Refused(params Iteration[] iterations)
        {
            var refused = Assert.Throws<WorkspaceRefused>(() => service.AddTrades(ws.WorkspaceId, ws.Revision, "akira", iterations));
            return $"{refused.Code}: {refused.Message}";
        }

        Assert.Equal("UnknownTarget: No trade DRAFT-9 in this workspace.", Refused(new Iteration(["DRAFT-9"], [])));
        Assert.Equal($"NotACandidate: {existing[0]} is an existing trade: an iteration adds New rows, and takes existing trades out.", Refused(new Iteration([existing[0]], [])));
        Assert.Equal("CandidateNotTicked: DRAFT-3 has A clear, and add each prices New rows with A ticked. Tick A on it, or leave it out of the iteration.",
            Refused(new Iteration(["DRAFT-1", "DRAFT-3"], [])));
        Assert.Equal("CandidateInBefore: DRAFT-2 is in Before, and a new trade is priced alone only out of Before. Clear B on it, or A to leave it out, then add each again.",
            Refused(new Iteration(["DRAFT-1", "DRAFT-2"], [])));
        Assert.Equal("NotExisting: DRAFT-1 is a new trade: an iteration takes existing trades out of After. Leave a new trade out of what it adds instead.",
            Refused(new Iteration(["DRAFT-1"], ["DRAFT-1"])));
        Assert.Equal($"NotInAfter: {existing[1]} is not in After in the Base, so no case takes it out.", Refused(new Iteration(["DRAFT-1"], [existing[1]])));
        Assert.Equal($"NothingToAdd: An iteration adds one new trade at least. A case that only takes {existing[0]} out is vary {existing[0]} A off.",
            Refused(new Iteration([], [existing[0]])));
        Assert.Equal("GivenTwice: DRAFT-1 is named twice in one iteration.", Refused(new Iteration(["DRAFT-1", "draft-1"], [])));
        Assert.Equal($"GivenTwice: The iteration DRAFT-1 without {existing[0]} is given twice.",
            Refused(new Iteration(["DRAFT-1"], [existing[0]]), new Iteration(["DRAFT-1"], [existing[0]])));
        Assert.Equal(ws.Revision, service.View(ws.WorkspaceId, "akira").Revision);
        Assert.Equal(["Base"], service.View(ws.WorkspaceId, "akira").Cases.Select(c => c.Title));
    }

    [Fact] // ADR-0016 §3, note 37: Price reads the iterations' candidates together, and refuses a trade a case takes out that the Base no longer has in After
    public void Price_checks_the_candidates_of_every_iteration_together_and_the_trades_taken_out()
    {
        var (service, _, _) = Priced();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 40);
        var taken = service.Page(ws.WorkspaceId, "akira", 0, 1, [], "existing").Rows[0].TradeId;
        ws = service.Edit(ws.WorkspaceId, ws.Revision, "akira", "trade:DRAFT-3", "after", false);
        ws = service.AddTrades(ws.WorkspaceId, ws.Revision, "akira", [new(["DRAFT-1", "DRAFT-2"], [taken])]).State;
        string Refused(WorkspaceState state)
        {
            var refused = Assert.Throws<WorkspaceRefused>(() => service.Price(state.WorkspaceId, state.Revision, "akira", Guid.NewGuid().ToString()));
            return $"{refused.Code}: {refused.Message}";
        }

        // Each change is made, refused at Price, and taken back before the next.
        string After(string trade, bool after)
        {
            var changed = service.Edit(ws.WorkspaceId, ws.Revision, "akira", $"trade:{trade}", "after", after);
            var refused = Refused(changed);
            ws = service.Edit(changed.WorkspaceId, changed.Revision, "akira", $"trade:{trade}", "after", !after);
            return refused;
        }

        Assert.Equal("AdditionSetChanged: add each's cases add DRAFT-1, DRAFT-2, and DRAFT-3 is ticked and in no case. Run add each again to make the set anew.", After("DRAFT-3", true));
        Assert.Equal("AdditionSetChanged: add each's cases add DRAFT-1, DRAFT-2, and DRAFT-2 is in a case and no longer ticked. Run add each again to make the set anew.", After("DRAFT-2", false));
        Assert.Equal($"AdditionSetChanged: add each's cases add DRAFT-1, DRAFT-2, and {taken} is taken out by a case and no longer in After. Run add each again to make the set anew.",
            After(taken, false));
        Assert.IsType<SubmissionResult.Accepted>(service.Price(ws.WorkspaceId, ws.Revision, "akira", Guid.NewGuid().ToString()));
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
