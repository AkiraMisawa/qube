using System.Text.Json;
using System.Text.Json.Nodes;
using Workbench.Composition.Application;
using Workbench.Composition.Application.Workspaces;
using Workbench.Composition.Contracts;
using Workbench.Samples.Ccr;
using Workbench.Samples.Portfolio;
using Workbench.Samples.Xva;

namespace Workbench.Composition.Tests;

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
        return (new PortfolioWorkspaces(workspaces, store, runs, register, options), runs, store);
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

        Assert.Equal(("UnknownCell", "No row CSA-Z: a row is a CSA (CSA-A, CSA-B), the netting set (NS-001) or a trade's id."), (row.Code, row.Message));
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
        Assert.Equal(3, service.View(ws.WorkspaceId, "akira").CaseCap);
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
        // A higher threshold leaves more exposure uncollateralised: more capital in the case.
        Assert.True(capital[1]["after"]!.GetValue<decimal>() > capital[0]["after"]!.GetValue<decimal>());
        var traces = store.ReadRequired(run.Invocations.Single(i => i.StepId == XvaMeasures.Module).Outputs["traces"]).Deserialize<TraceSet>(WireJson.Options)!;
        Assert.Equal([("base", "before"), ("base", "after"), (raised, "before"), (raised, "after")], traces.Traces.Select(t => (t.Case, t.Side)));
        // Each case's profile is its own.
        var profile = store.ReadRequired(run.Invocations.Single(i => i.StepId == XvaMeasures.Module).Outputs["profile"])["rows"]!.AsArray();
        Assert.Equal(["base", raised], profile.Select(p => p!["case"]!.GetValue<string>()).Distinct());
    }
}
