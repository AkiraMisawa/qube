using System.Text.Json;
using System.Text.Json.Nodes;
using Workbench.Composition.Application;
using Workbench.Composition.Application.Workspaces;
using Workbench.Composition.Contracts;

namespace Workbench.Samples.Portfolio;

public static class PortfolioContracts
{
    public const string Version = "1.0.0";
    /// <summary>What Price froze: the shared portfolio and each module's own part (ADR-0010 §10).</summary>
    public const string AcceptedInput = "portfolio.accepted-input";
    /// <summary>The figures every measure reports in one shape, a row per case, measure and metric.</summary>
    public const string MeasureResult = "portfolio.measure-result";
    public const string PriceInput = "portfolio.price-input";
    public const string PriceWorkflow = "workflow.portfolio.price";
}

/// <summary>
/// The shape every measure's request writes (ADR-0010 §7): one row per case, measure and metric, with Before, After and
/// Diff, the trades each side priced, and each side's status. A side not priced is <c>notPriced</c> and has no figure;
/// a failed one is <c>unavailable</c>; a measure blocked at Price is <c>refused</c> with its reason. Never 0 for any of them.
/// </summary>
public static class MeasureResult
{
    public const string Priced = "priced";
    public const string NotPriced = "notPriced";
    public const string Unavailable = "unavailable";
    public const string Refused = "refused";

    public static readonly IReadOnlyList<DatasetColumn> Columns =
    [
        new("case", DatasetColumnType.String, false),
        new("caseTitle", DatasetColumnType.String, false),
        new("measure", DatasetColumnType.String, false),
        new("metric", DatasetColumnType.String, false),
        new("metricTitle", DatasetColumnType.String, false),
        new("currency", DatasetColumnType.String, false),
        new("before", DatasetColumnType.Number, true),
        new("after", DatasetColumnType.Number, true),
        new("diff", DatasetColumnType.Number, true),
        new("tradesBefore", DatasetColumnType.Integer, true),
        new("tradesAfter", DatasetColumnType.Integer, true),
        new("beforeStatus", DatasetColumnType.String, false),
        new("afterStatus", DatasetColumnType.String, false),
        new("reason", DatasetColumnType.String, true),
    ];

    /// <summary>A side's figure: its value when priced, else nothing.</summary>
    public readonly record struct Side(string Status, decimal? Value, int? Trades, string? Reason = null)
    {
        public static Side Of(decimal value, int trades) => new(Priced, value, trades);
        public static Side NotPricedWith(int trades) => new(NotPriced, null, trades);
    }

    public static JsonObject Row(string @case, string caseTitle, string measure, MetricDefinition metric, string currency, Side before, Side after) => new()
    {
        ["case"] = @case,
        ["caseTitle"] = caseTitle,
        ["measure"] = measure,
        ["metric"] = metric.Id,
        ["metricTitle"] = metric.Title,
        ["currency"] = currency,
        ["before"] = before.Value,
        ["after"] = after.Value,
        // A Diff exists only where both sides were priced (ADR-0010 §7).
        ["diff"] = before.Status == Priced && after.Status == Priced ? after.Value - before.Value : null,
        ["tradesBefore"] = before.Trades,
        ["tradesAfter"] = after.Trades,
        ["beforeStatus"] = before.Status,
        ["afterStatus"] = after.Status,
        ["reason"] = before.Reason ?? after.Reason,
    };
}

/// <summary>
/// Builds the accepted input Price freezes: every trade in Before or After with its effective values, the selected
/// measures with any refusal, each module's own part, and the copy of the overrides it priced. The Run keeps it;
/// nothing re-derives it later.
/// </summary>
public static class PortfolioInput
{
    public static JsonObject Build(WorkspaceFreeze freeze, MeasureRegister register)
    {
        var state = freeze.State;
        var source = (PortfolioSourceVersion)state.Source;
        var trades = PortfolioMaterialiser.Trades(state).Where(t => t.Before || t.After).ToList();
        var invalid = trades.Where(t => t.Issues.Count > 0).ToList();
        if (invalid.Count > 0)
            throw new WorkspaceRefused("InvalidInput", $"{invalid.Count} trade(s) to price have validation issues, first {invalid[0].TradeId}: {invalid[0].Issues[0]}");
        var selected = PortfolioMaterialiser.SelectedMeasures(state, register);
        var refusals = register.Modules
            .SelectMany(m => m.Validate(state, [.. selected.Where(id => m.Measures.Any(d => d.Id == id))]))
            .ToDictionary(r => r.Measure);
        var modules = new JsonObject();
        foreach (var module in register.Modules.Where(m => m.Measures.Any(d => selected.Contains(d.Id))))
            modules[module.Id] = module.Input(state);
        return new JsonObject
        {
            ["workspaceId"] = freeze.WorkspaceId,
            ["revision"] = freeze.Revision,
            ["context"] = freeze.Context.DeepClone(),
            ["sourceVersion"] = freeze.SourceVersion,
            ["profileVersion"] = PortfolioCatalog.Profile.ProfileVersion,
            ["registration"] = source.Registration.Key,
            ["agreement"] = source.Agreement.MaId,
            ["nettingSet"] = source.Agreement.NettingSetId,
            ["measures"] = new JsonArray([.. selected.Select(id => new JsonObject
            {
                ["id"] = id,
                ["module"] = register.Measures.Single(m => m.Id == id).Module,
                ["refused"] = refusals.TryGetValue(id, out var refusal) ? new JsonObject { ["code"] = refusal.Code, ["message"] = refusal.Message } : null,
            })]),
            ["trades"] = JsonSerializer.SerializeToNode(trades, WireJson.Options),
            ["modules"] = modules,
            ["overrides"] = new JsonArray([.. freeze.Overrides.Select(o => new JsonObject
            {
                ["entity"] = o.Target.Entity, ["field"] = o.Target.Field, ["value"] = o.Value?.DeepClone(), ["baseVersion"] = o.BaseVersion, ["seq"] = o.Seq,
            })]),
        };
    }
}

/// <summary>Price: freeze the revision the user saw, build its accepted input, retain it, and submit one Run holding a
/// request per module whose measures are selected (ADR-0010 §10: one Price is one Run, holding every request it made).</summary>
public static class PortfolioPrice
{
    public static RunSubmission Submission(WorkspaceService workspaces, IDataStore store, MeasureRegister register, string workspaceId, long expectedRevision,
        string idempotencyKey, string owner, RunOrigin origin = RunOrigin.Pricer, RunRelations? relations = null)
    {
        var freeze = workspaces.Freeze(workspaceId, expectedRevision, PortfolioMaterialiser.PricedEntities);
        var input = PortfolioInput.Build(freeze, register);
        var reference = store.Put($"portfolio-input-{freeze.WorkspaceId}-r{freeze.Revision}", PortfolioContracts.AcceptedInput, PortfolioContracts.Version, input);
        var selected = PortfolioMaterialiser.SelectedMeasures(freeze.State, register);
        // The Run's input says what was asked, so the page and the Pricing DB read it from the Run, not from the screen.
        return new RunSubmission(Workflow(register, reference, selected), freeze.Context,
            new JsonObject { ["workspaceId"] = freeze.WorkspaceId, ["revision"] = freeze.Revision, ["measures"] = string.Join(' ', selected) },
            idempotencyKey, owner, origin,
            (relations ?? RunRelations.None) with { WorkspaceId = freeze.WorkspaceId, WorkspaceRevision = freeze.Revision });
    }

    /// <summary>The price workflow for one frozen accepted input: a step per module with a selected measure, each output
    /// named by its module. It pins the input by reference, so the Run carries it.</summary>
    public static WorkflowDefinition Workflow(MeasureRegister register, TypedDataRef acceptedInput, IReadOnlyList<string> selected)
    {
        var modules = register.Modules.Where(m => m.Measures.Any(d => selected.Contains(d.Id))).ToList();
        return new("workflow", CompositionValidator.SchemaVersion, PortfolioContracts.PriceWorkflow, PortfolioContracts.Version,
            "Price the portfolio", PortfolioContracts.PriceInput, PortfolioContracts.Version,
            [.. modules.Select(m => m.Step(acceptedInput))],
            modules.ToDictionary(m => m.Id, Binding (m) => new OutputBinding(m.Id, "result")));
    }
}

/// <summary>Registers the portfolio's price input contract: the workspace revision priced and the measures asked for.</summary>
public sealed class PortfolioModule : IWorkbenchModule
{
    public void Register(CompositionRegistryBuilder builder)
    {
        builder.AddContract(new ContractDefinition(PortfolioContracts.PriceInput, PortfolioContracts.Version, "Portfolio price input", new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["workspaceId"] = new JsonObject { ["type"] = "string", ["title"] = "Workspace" },
                ["revision"] = new JsonObject { ["type"] = "integer", ["title"] = "Revision" },
                ["measures"] = new JsonObject { ["type"] = "string", ["title"] = "Measures", ["description"] = "The measures priced, as `measures` takes them." },
            },
            ["required"] = new JsonArray("workspaceId", "revision", "measures"),
            ["additionalProperties"] = false,
        }));
    }
}
