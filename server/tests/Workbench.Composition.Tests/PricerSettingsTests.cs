using System.Text.Json.Nodes;
using Workbench.Composition.Application;
using Workbench.Composition.Application.Workspaces;
using Workbench.Composition.Contracts;
using Workbench.Samples.Ccr;
using Workbench.Samples.Portfolio;
using Workbench.Samples.Xva;

namespace Workbench.Composition.Tests;

// A module's settings on the portfolio workspace service (ADR-0010 §8, §10 and §8's note of 2026-11-09): registered by
// XVA as it registers its trade columns, shown while a measure they serve is selected, checked on the server, frozen by
// Price and read by the provider. Every figure is the simulated engine's.
public class PricerSettingsTests
{
    static readonly DateOnly Day1 = new(2026, 10, 26);

    static (PortfolioWorkspaces Service, RunService Runs, IDataStore Store) Priced(Pv01Provider? pv01 = null)
    {
        var workspaces = new WorkspaceService(new Dictionary<string, IEditRules> { [PortfolioEditRules.App] = new PortfolioEditRules() }, TimeProvider.System);
        var store = new InMemoryDataStore();
        var runs = new RunService(new CompositionRegistry([new PortfolioModule(), new XvaModule(), new CcrModule()]), store, TimeProvider.System);
        var register = new MeasureRegister([new XvaMeasures(pv01 ?? Pv01Provider.Simulated(TimeSpan.Zero)), new CcrMeasures()]);
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

    static WorkspaceState Set(PortfolioWorkspaces service, WorkspaceState ws, string column, string text) =>
        service.SetCells(ws.WorkspaceId, ws.Revision, "akira", [XvaMeasures.Module], column, text);

    static IReadOnlyList<string> Shown(PortfolioWorkspaces service, WorkspaceState ws) =>
        [.. service.View(ws.WorkspaceId, "akira").Settings.SelectMany(m => m.Settings.Select(s => $"{m.Module}.{s.Id}"))];

    [Fact] // ADR-0010 §8, §10: a setting shows while a measure it serves is selected, with its default, and is marked where it differs
    public void The_view_lists_the_settings_the_selected_measures_need()
    {
        var (service, _, _) = Priced();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 5);

        ws = service.SetMeasures(ws.WorkspaceId, ws.Revision, "akira", ["cva"]);
        Assert.Equal(["xva.mpor", "xva.chargeRatio", "xva.pvAdjustment", "xva.risks"], Shown(service, ws));
        ws = service.SetMeasures(ws.WorkspaceId, ws.Revision, "akira", ["kva"]);
        Assert.Equal(["xva.costOfCapital", "xva.pvAdjustment"], Shown(service, ws));
        // A module none of whose measures is selected has no section: SA-CCR registers no setting of its own.
        ws = service.SetMeasures(ws.WorkspaceId, ws.Revision, "akira", ["ccr.spotCapital"]);
        Assert.Empty(service.View(ws.WorkspaceId, "akira").Settings);

        ws = service.SetMeasures(ws.WorkspaceId, ws.Revision, "akira", ["cva", "kva"]);
        ws = Set(service, ws, "cost of capital", "12%");
        var xva = Assert.Single(service.View(ws.WorkspaceId, "akira").Settings);
        Assert.Equal(("xva", "XVA"), (xva.Module, xva.Title));
        var coc = xva.Settings.Single(s => s.Id == XvaMeasures.CostOfCapital);
        Assert.Equal((0.12m, "12%", 0.10m, "10%", true), (coc.Value!.GetValue<decimal>(), coc.Text, coc.Default!.GetValue<decimal>(), coc.DefaultText, coc.Changed));
        Assert.Equal(["kva"], coc.Measures);
        // An empty MPOR override is the model's own, and says so.
        var mpor = xva.Settings.Single(s => s.Id == XvaMeasures.Mpor);
        Assert.Equal((null, "empty", "the model's 10 days", false), (mpor.Value, mpor.Text, mpor.Empty, mpor.Changed));
        // A setting set back to its default is not marked (ADR-0010 §2).
        ws = Set(service, ws, "coc", "10%");
        Assert.False(service.View(ws.WorkspaceId, "akira").Settings[0].Settings.Single(s => s.Id == XvaMeasures.CostOfCapital).Changed);
    }

    [Fact] // ADR-0010 §8, ADR-0003: a setting is checked on the server by the module that reads it, and is the workspace's own
    public void A_setting_out_of_its_bounds_is_refused_and_survives_a_counterparty_change()
    {
        var (service, _, _) = Priced();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 5);

        string Refused(string column, string text) => Assert.Throws<WorkspaceRefused>(() => Set(service, ws, column, text)).Message;
        Assert.Equal("A cost of capital is from 0% up to 100%, never empty.", Refused("cost of capital", "100%"));
        Assert.Equal("A charge ratio is a number above 0 and up to 10.", Refused("charge ratio", "0"));
        Assert.Equal("An MPOR override is a whole number of days from 1 to 250, or empty for the model's 10.", Refused("mpor", "0d"));
        Assert.Equal("xva mpor override is a whole number of days, 10 or 10d, or empty; ten is not.", Refused("mpor", "ten"));
        Assert.Equal("xva risks included is on or off; maybe is not.", Refused("risks", "maybe"));
        Assert.Equal("XVA has no setting other; its settings are mpor, costOfCapital, chargeRatio, pvAdjustment, risks.",
            Assert.Throws<WorkspaceRefused>(() => service.Edit(ws.WorkspaceId, ws.Revision, "akira", "settings:xva", "other", 1)).Message);
        Assert.Equal("A charge ratio is a number above 0 and up to 10.",
            Assert.Throws<WorkspaceRefused>(() => service.Edit(ws.WorkspaceId, ws.Revision, "akira", "settings:xva", XvaMeasures.ChargeRatio, "1.5")).Message);

        ws = Set(service, ws, "mpor", "20d");
        var drop = service.PreviewCounterpartyChange(ws.WorkspaceId, "akira");
        var (moved, _) = service.ChangeCounterparty(ws.WorkspaceId, ws.Revision, "akira", "ABC/LE-02", drop.Count);

        Assert.Equal(0, drop.Count);
        Assert.Equal(20, service.View(moved.WorkspaceId, "akira").Settings[0].Settings.Single(s => s.Id == XvaMeasures.Mpor).Value!.GetValue<decimal>());
    }

    [Fact] // ADR-0010 §8, §3: Price freezes the settings, and each moves the figure that reads it: vary makes a case per value
    public async Task The_settings_are_frozen_and_move_the_figures_that_read_them()
    {
        var (service, runs, store) = Priced();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 20);
        ws = service.SetMeasures(ws.WorkspaceId, ws.Revision, "akira", ["cva", "kva"]);

        ws = service.Vary(ws.WorkspaceId, ws.Revision, "akira", "xva charge ratio", ["1", "1.5"]).State;
        ws = service.Vary(ws.WorkspaceId, ws.Revision, "akira", "xva cost of capital", ["20%"]).State;
        ws = service.Vary(ws.WorkspaceId, ws.Revision, "akira", "xva mpor", ["40d"]).State;
        var cases = service.View(ws.WorkspaceId, "akira").Cases.ToDictionary(c => c.Title, c => c.Id);
        var (input, rows) = await Price(runs, store, service, ws);

        // The Base's settings are the defaults, carried whole in XVA's part of the input.
        Assert.Equal(new JsonObject { ["mpor"] = null, ["costOfCapital"] = 0.10m, ["chargeRatio"] = 1.25m, ["pvAdjustment"] = true, ["risks"] = false },
            input["modules"]!["xva"]!["settings"], JsonNode.DeepEquals);
        var cva = After(rows, "cva");
        Assert.Equal(Math.Round(cva * 1.25m, 2), After(rows, "cvaCharge"));
        Assert.Equal(cva, After(rows, "cvaCharge", cases["xva charge ratio 1"]));
        Assert.Equal(Math.Round(cva * 1.5m, 2), After(rows, "cvaCharge", cases["xva charge ratio 1.5"]));
        // A higher cost of capital makes KVA more negative: twice the cost, twice the charge.
        Assert.Equal(Math.Round(After(rows, "kva") * 2, 0), Math.Round(After(rows, "kva", cases["xva cost of capital 20%"]), 0));
        // A longer margin period is a larger move on a collateralised trade: more exposure, a more negative CVA.
        Assert.True(After(rows, "cva", cases["xva mpor override 40d"]) < cva);
        Assert.Equal(After(rows, "kva"), After(rows, "kva", cases["xva mpor override 40d"]));
        // The Changes tab names each by its cell, with the default as its Original.
        var changed = service.Changes(ws.WorkspaceId, "akira").Cases.SelectMany(c => c.Changes).Select(c => (c.Title, c.Text, c.OriginalText));
        Assert.Equal([("xva charge ratio", "1", "1.25"), ("xva charge ratio", "1.5", "1.25"), ("xva cost of capital", "20%", "10%"), ("xva mpor override", "40d", "empty")], changed);
    }

    [Fact] // ADR-0010 §6, §8: PV adjustment off prices new trades with no PV Adj, and a PV Adj not known refuses nothing then
    public async Task Pv_adjustment_off_prices_new_trades_without_one()
    {
        var answer = new TaskCompletionSource<decimal>();
        var pv01 = new Pv01Provider((_, _) => answer.Task);
        var (service, runs, store) = Priced(pv01);
        var ws = service.Create("akira", "ABC/LE-01", Day1, 5);
        ws = service.SetMeasures(ws.WorkspaceId, ws.Revision, "akira", ["cva", "fva"]);
        ws = service.Edit(ws.WorkspaceId, ws.Revision, "akira", "trade:DRAFT-1", XvaMeasures.Field(XvaMeasures.Margin), 2m);

        var (_, pending) = await Price(runs, store, service, ws);
        Assert.Equal(MeasureResult.Refused, Row(pending, "cva")["afterStatus"]!.GetValue<string>());
        Assert.StartsWith("PV Adj of DRAFT-1 is unknown", Row(pending, "cva")["reason"]!.GetValue<string>());
        ws = Set(service, ws, "pv adjustment", "off");
        var (input, off) = await Price(runs, store, service, ws);
        Assert.Equal(MeasureResult.Priced, Row(off, "cva")["afterStatus"]!.GetValue<string>());
        Assert.False(input["modules"]!["xva"]!["settings"]!["pvAdjustment"]!.GetValue<bool>());

        // With a PV Adj known, it moves the new trades' value only while the setting is on.
        answer.SetResult(-1_500m);
        await pv01.Settled();
        var (_, without) = await Price(runs, store, service, ws);
        ws = Set(service, ws, "pv adj", "on");
        var (_, with) = await Price(runs, store, service, ws);
        Assert.Equal(Row(without, "pv")["before"]!.GetValue<decimal>(), Row(with, "pv")["before"]!.GetValue<decimal>());
        Assert.NotEqual(After(without, "pv"), After(with, "pv"));
    }
}
