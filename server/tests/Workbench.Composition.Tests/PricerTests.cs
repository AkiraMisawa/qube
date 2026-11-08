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

    static (PortfolioWorkspaces Service, RunService Runs, IDataStore Store) Priced(IMeasureModule? xva = null)
    {
        var workspaces = new WorkspaceService(new Dictionary<string, IEditRules> { [PortfolioEditRules.App] = new PortfolioEditRules() }, TimeProvider.System);
        var store = new InMemoryDataStore();
        var runs = new RunService(new CompositionRegistry([new PortfolioModule(), new XvaModule()]), store, TimeProvider.System);
        return (new PortfolioWorkspaces(workspaces, store, runs, new MeasureRegister([xva ?? new XvaMeasures()])), runs, store);
    }

    /// <summary>XVA's registration with a validation that blocks CVA, as a missing CDS will (ADR-0010 §11).</summary>
    sealed class CvaBlocked : IMeasureModule
    {
        readonly XvaMeasures xva = new();
        public string Id => xva.Id;
        public IReadOnlyList<MeasureDefinition> Measures => xva.Measures;
        public JsonObject Input(WorkspaceState state) => xva.Input(state);
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
        var (service, runs, store) = Priced(new CvaBlocked());
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
}
