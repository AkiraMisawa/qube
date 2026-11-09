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
/// A side's note is said beside its figure, as spot IM's governing regime, and the basis with the row's figures, as
/// <c>SIMM PARAMETERS DEMO</c> (ADR-0010 §7's note of 2026-11-08); either is empty where the measure has nothing to say.
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
        new("beforeNote", DatasetColumnType.String, true),
        new("afterNote", DatasetColumnType.String, true),
        new("basis", DatasetColumnType.String, true),
        new("conversion", DatasetColumnType.String, true),
    ];

    /// <summary>A side's figure: its value when priced, else nothing; and what is said beside it, if anything.</summary>
    public readonly record struct Side(string Status, decimal? Value, int? Trades, string? Reason = null, string? Note = null)
    {
        public static Side Of(decimal value, int trades) => new(Priced, value, trades);
        public static Side NotPricedWith(int trades) => new(NotPriced, null, trades);
    }

    /// <summary>
    /// A metric's row. <paramref name="basis"/> names what the figures stand on where it is not a model of record, and
    /// <paramref name="conversion"/> says, once, the rate a figure computed in another currency is shown at.
    /// </summary>
    public static JsonObject Row(string @case, string caseTitle, string measure, MetricDefinition metric, string currency, Side before, Side after,
        string? basis = null, string? conversion = null) => new()
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
        ["beforeNote"] = before.Note,
        ["afterNote"] = after.Note,
        ["basis"] = basis,
        ["conversion"] = conversion,
    };
}

/// <summary>
/// A case of an accepted input in the shape a one-case input had (ADR-0010 §3's note of 2026-11-08): what the cases share,
/// and the case's own trades, module parts and measures, so a provider loops over cases with its body unchanged and writes
/// each row and trace entry with the case's id and title.
/// </summary>
public sealed record PricedCase(string Id, string Title, string Kind, JsonObject Input);

/// <summary>
/// Builds the accepted input Price freezes: what the cases share at its top (the context, the display currency, the
/// membership rules' versions, the selection), and each case, the Base first, with its changes, every trade in its Before
/// or After with its effective values, each module's own part and the selected measures with any refusal, so one case's
/// refused measure leaves the others standing. The Run keeps it, with the copy of the edits it priced; nothing re-derives
/// it later.
/// </summary>
public static class PortfolioInput
{
    public static JsonObject Build(WorkspaceFreeze freeze, MeasureRegister register, int caseCap = PortfolioCases.DefaultCap)
    {
        var raw = freeze.State;
        var cases = PortfolioCases.All(raw);
        // The cap counts the Base (ADR-0010 §3's note): a Run nobody can read is refused before it is made.
        if (cases.Count > caseCap)
            throw new WorkspaceRefused("TooManyCases",
                $"Price prices {caseCap} cases at most, the Base among them; this workspace holds {cases.Count}, {cases.Count - caseCap} over. Remove {cases.Count - caseCap} to price.");
        var state = PortfolioCases.Base(raw);
        var source = (PortfolioSourceVersion)state.Source;
        var selected = PortfolioMaterialiser.SelectedMeasures(state, register);
        return new JsonObject
        {
            ["workspaceId"] = freeze.WorkspaceId,
            ["revision"] = freeze.Revision,
            ["context"] = freeze.Context.DeepClone(),
            ["sourceVersion"] = freeze.SourceVersion,
            ["profileVersion"] = PortfolioCatalog.Profile.ProfileVersion,
            // The currency every measure reports in (ADR-0010 §1, §7), frozen with the rest.
            ["displayCurrency"] = PortfolioMaterialiser.DisplayCurrency(state),
            ["registration"] = source.Registration.Key,
            ["agreement"] = source.Agreement.MaId,
            ["nettingSet"] = source.Agreement.NettingSetId,
            // Each membership rule's version, frozen with the trades it classified (ADR-0010 §5).
            ["membership"] = new JsonObject([.. register.Rules(state).Select(r => KeyValuePair.Create(r.Measure, (JsonNode?)new JsonObject { ["version"] = r.Version }))]),
            ["selection"] = new JsonArray([.. selected.Select(id => JsonValue.Create(id))]),
            ["cases"] = new JsonArray([.. cases.Select(c => Case(c, raw, register, selected))]),
            ["overrides"] = new JsonArray([.. freeze.Overrides.Select(o => new JsonObject
            {
                ["entity"] = o.Target.Entity, ["field"] = o.Target.Field, ["layer"] = o.Target.Layer, ["value"] = o.Value?.DeepClone(), ["baseVersion"] = o.BaseVersion, ["seq"] = o.Seq,
            })]),
        };
    }

    static JsonObject Case(CaseView @case, WorkspaceState raw, MeasureRegister register, IReadOnlyList<string> selected)
    {
        var state = PortfolioCases.Reading(raw, @case.Id);
        var named = @case.Id == PortfolioCases.BaseId ? "" : $"Case {@case.Title}: ";
        // The trades with their membership and the modules' cells, read once: what is refused and what is frozen are
        // one reading of a cell that changes on its own, such as PV01 (ADR-0010 §6).
        var trades = register.WithCells(state, [.. register.WithMembership(state, PortfolioMaterialiser.Trades(state)).Where(t => t.Before || t.After)]);
        // A side with no trade is not priced (ADR-0010 §3); with no trade on either side there is nothing to price.
        if (trades.Count == 0)
            throw new WorkspaceRefused("NothingToPrice", $"{named}No trade is in Before or After: tick B or A on a trade to price it.");
        var invalid = trades.Where(t => t.Issues.Count > 0).ToList();
        if (invalid.Count > 0)
            throw new WorkspaceRefused("InvalidInput", $"{named}{invalid.Count} trade(s) to price have validation issues, first {invalid[0].TradeId}: {invalid[0].Issues[0]}");
        var refusals = register.Modules
            .SelectMany(m => m.Validate(state, [.. selected.Where(id => m.Measures.Any(d => d.Id == id))], trades))
            .ToDictionary(r => r.Measure);
        // Each request reads the part of every module it prices (ADR-0010 §10): XVA's with KVA reads SA-CCR's.
        var modules = new JsonObject();
        foreach (var module in register.Inputs(selected))
            modules[module.Id] = module.Input(state);
        return new JsonObject
        {
            ["id"] = @case.Id,
            ["title"] = @case.Title,
            ["kind"] = @case.Kind,
            ["trade"] = @case.Trade,
            // What makes it this case, as the Pricing DB and Download say it (ADR-0010 §3).
            ["changes"] = new JsonArray([.. PortfolioCases.ChangesOf(raw, @case.Id).Select(o => new JsonObject
            {
                ["entity"] = o.Target.Entity, ["field"] = o.Target.Field, ["value"] = o.Value?.DeepClone(),
            })]),
            ["measures"] = new JsonArray([.. selected.Select(id => new JsonObject
            {
                ["id"] = id,
                ["module"] = register.Measures.Single(m => m.Id == id).Module,
                ["refused"] = refusals.TryGetValue(id, out var refusal) ? new JsonObject { ["code"] = refusal.Code, ["message"] = refusal.Message } : null,
            })]),
            ["trades"] = JsonSerializer.SerializeToNode(trades, WireJson.Options),
            ["modules"] = modules,
        };
    }

    /// <summary>
    /// Each case of an accepted input, the Base first, in the shape a one-case input had: the shared fields with the
    /// case's own trades, module parts and measures. An input that holds no case is one no provider can read.
    /// </summary>
    public static IReadOnlyList<PricedCase> Cases(JsonNode input)
    {
        var top = input as JsonObject ?? throw new CapabilityFailure("InvalidInput", "The accepted input is not an object.");
        if (top["cases"] is not JsonArray { Count: > 0 } cases)
            throw new CapabilityFailure("InvalidInput", "The accepted input holds no cases.");
        var shared = top.Where(p => p.Key is not ("cases" or "overrides")).ToList();
        return [.. cases.Select(node =>
        {
            var c = node as JsonObject ?? throw new CapabilityFailure("InvalidInput", "A case of the accepted input is not an object.");
            string Text(string key) => c[key]?.GetValue<string>() ?? throw new CapabilityFailure("InvalidInput", $"A case of the accepted input has no {key}.");
            var one = new JsonObject([.. shared.Select(p => KeyValuePair.Create(p.Key, p.Value?.DeepClone()))]);
            foreach (var key in new[] { "trades", "modules", "measures" })
                one[key] = (c[key] ?? throw new CapabilityFailure("InvalidInput", $"Case {Text("id")} of the accepted input has no {key}.")).DeepClone();
            return new PricedCase(Text("id"), Text("title"), Text("kind"), one);
        })];
    }
}

/// <summary>Price: freeze the revision the user saw, build its accepted input, retain it, and submit one Run holding a
/// request per module whose measures are selected (ADR-0010 §10: one Price is one Run, holding every request it made).</summary>
public static class PortfolioPrice
{
    public static RunSubmission Submission(WorkspaceService workspaces, IDataStore store, MeasureRegister register, string workspaceId, long expectedRevision,
        string idempotencyKey, string owner, RunOrigin origin = RunOrigin.Pricer, RunRelations? relations = null, int caseCap = PortfolioCases.DefaultCap)
    {
        var freeze = workspaces.Freeze(workspaceId, expectedRevision, PortfolioCases.PricedEntities);
        var input = PortfolioInput.Build(freeze, register, caseCap);
        var reference = store.Put($"portfolio-input-{freeze.WorkspaceId}-r{freeze.Revision}", PortfolioContracts.AcceptedInput, PortfolioContracts.Version, input);
        var selected = PortfolioMaterialiser.SelectedMeasures(PortfolioCases.Base(freeze.State), register);
        // The Run's input says what was asked, so the page and the Pricing DB read it from the Run, not from the screen.
        return new RunSubmission(Workflow(register, reference, selected), freeze.Context,
            new JsonObject { ["workspaceId"] = freeze.WorkspaceId, ["revision"] = freeze.Revision, ["measures"] = string.Join(' ', selected) },
            idempotencyKey, owner, origin,
            (relations ?? RunRelations.None) with { WorkspaceId = freeze.WorkspaceId, WorkspaceRevision = freeze.Revision },
            Basis(workspaceId, expectedRevision, origin, relations));
    }

    /// <summary>What names one Price as a business submission (XV13): the workspace revision priced, and from where. Not
    /// the accepted input, which freezes cells that answer on their own, such as PV01 (ADR-0010 §6): a retry after one
    /// answered, or after the workspace moved on, is still the Price the key was first given.</summary>
    public static JsonObject Basis(string workspaceId, long revision, RunOrigin origin = RunOrigin.Pricer, RunRelations? relations = null) => new()
    {
        ["workflow"] = PortfolioContracts.PriceWorkflow,
        ["version"] = PortfolioContracts.Version,
        ["workspaceId"] = workspaceId,
        ["revision"] = revision,
        ["origin"] = JsonSerializer.SerializeToNode(origin, WireJson.Options),
        ["relations"] = JsonSerializer.SerializeToNode(relations ?? RunRelations.None, WireJson.Options),
    };

    /// <summary>The price workflow for one frozen accepted input: a step per request a selected measure needs, each output
    /// named by its module. It pins the input by reference, so the Run carries it.</summary>
    public static WorkflowDefinition Workflow(MeasureRegister register, TypedDataRef acceptedInput, IReadOnlyList<string> selected)
    {
        var modules = register.Requests(selected);
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
