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
    /// <summary>Each measure's deltas, a row per case, scenario, measure and risk factor (ADR-0010 OPEN of 2026-11-08, Risks).</summary>
    public const string Risks = "portfolio.risks";
    /// <summary>The profiles a request returns, a row per case, scenario, side, profile, series and date (Profile).</summary>
    public const string Profiles = "portfolio.profiles";
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
        // The market the row is priced on (ADR-0010 §4): the base market, or a scenario of the Run.
        new("scenario", DatasetColumnType.String, false),
        new("scenarioTitle", DatasetColumnType.String, false),
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
    /// A metric's row for a case on its market. <paramref name="basis"/> names what the figures stand on where it is not a
    /// model of record, and <paramref name="conversion"/> says, once, the rate a figure computed in another currency is shown at.
    /// </summary>
    public static JsonObject Row(PricedCase @case, string measure, MetricDefinition metric, string currency, Side before, Side after,
        string? basis = null, string? conversion = null) => new()
    {
        ["case"] = @case.Id,
        ["caseTitle"] = @case.Title,
        ["scenario"] = @case.Scenario.Id,
        ["scenarioTitle"] = @case.Scenario.Title,
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
/// The deltas a request returns (the Risks tab, owner 2026-11-08): for each measure it prices with risks asked, a row per
/// risk factor with its risk class (<c>Credit</c>, <c>IR</c>, <c>FX</c>) and the bump it was moved by, each side the
/// figure on the market with the bump minus the figure on the market without, and Diff After − Before where both sides
/// were priced. A measure with no deltas has one row with no factor saying why: risks not asked, the measure refused, the
/// scenario not applied. Never a row of zeros for a delta nobody computed.
/// </summary>
public static class MeasureRisks
{
    public static readonly IReadOnlyList<DatasetColumn> Columns =
    [
        new("case", DatasetColumnType.String, false),
        new("caseTitle", DatasetColumnType.String, false),
        new("scenario", DatasetColumnType.String, false),
        new("scenarioTitle", DatasetColumnType.String, false),
        new("measure", DatasetColumnType.String, false),
        new("metric", DatasetColumnType.String, false),
        new("metricTitle", DatasetColumnType.String, false),
        new("riskClass", DatasetColumnType.String, true),
        new("factor", DatasetColumnType.String, true),
        new("bump", DatasetColumnType.String, true),
        new("currency", DatasetColumnType.String, false),
        new("before", DatasetColumnType.Number, true),
        new("after", DatasetColumnType.Number, true),
        new("diff", DatasetColumnType.Number, true),
        new("beforeStatus", DatasetColumnType.String, true),
        new("afterStatus", DatasetColumnType.String, true),
        new("reason", DatasetColumnType.String, true),
    ];

    /// <summary>One factor's delta of a measure's metric on both sides.</summary>
    public static JsonObject Row(PricedCase @case, string measure, MetricDefinition metric, string currency, string riskClass, string factor, string bump,
        MeasureResult.Side before, MeasureResult.Side after) =>
        Of(@case, measure, metric, currency, (riskClass, factor, bump), (before, after), before.Reason ?? after.Reason);

    /// <summary>Why a measure has no deltas for the pair: a row with no factor and no figure.</summary>
    public static JsonObject None(PricedCase @case, string measure, MetricDefinition metric, string currency, string reason) =>
        Of(@case, measure, metric, currency, null, null, reason);

    static JsonObject Of(PricedCase @case, string measure, MetricDefinition metric, string currency, (string RiskClass, string Factor, string Bump)? factor,
        (MeasureResult.Side Before, MeasureResult.Side After)? sides, string? reason) => new()
    {
        ["case"] = @case.Id, ["caseTitle"] = @case.Title, ["scenario"] = @case.Scenario.Id, ["scenarioTitle"] = @case.Scenario.Title,
        ["measure"] = measure, ["metric"] = metric.Id, ["metricTitle"] = metric.Title,
        ["riskClass"] = factor?.RiskClass, ["factor"] = factor?.Factor, ["bump"] = factor?.Bump, ["currency"] = currency,
        ["before"] = sides?.Before.Value, ["after"] = sides?.After.Value,
        ["diff"] = sides is ({ Status: MeasureResult.Priced } b, { Status: MeasureResult.Priced } a) ? a.Value - b.Value : null,
        ["beforeStatus"] = sides?.Before.Status, ["afterStatus"] = sides?.After.Status, ["reason"] = reason,
    };
}

/// <summary>
/// The profiles a request returns (the Profile tab, owner 2026-11-08), long: a row per case, scenario, side, profile,
/// series and date, its value in the row's currency. XVA's are the exposure profile CVA and FVA integrate, EE and ENE,
/// and the capital profile KVA charges for, EAD and capital, with the trades still alive at each date. A profile no
/// request returned has no rows, and the tab says which it has.
/// </summary>
public static class MeasureProfiles
{
    public const string Exposure = "exposure";
    public const string Capital = "capital";

    public static readonly IReadOnlyList<DatasetColumn> Columns =
    [
        new("case", DatasetColumnType.String, false),
        new("caseTitle", DatasetColumnType.String, false),
        new("scenario", DatasetColumnType.String, false),
        new("scenarioTitle", DatasetColumnType.String, false),
        new("side", DatasetColumnType.String, false),
        new("profile", DatasetColumnType.String, false),
        new("profileTitle", DatasetColumnType.String, false),
        new("series", DatasetColumnType.String, false),
        new("seriesTitle", DatasetColumnType.String, false),
        new("years", DatasetColumnType.Number, false),
        new("date", DatasetColumnType.String, false),
        new("value", DatasetColumnType.Number, false),
        new("currency", DatasetColumnType.String, false),
        new("trades", DatasetColumnType.Integer, true),
    ];

    /// <summary>One point of one series of a side's profile.</summary>
    public static JsonObject Row(PricedCase @case, string side, string profile, string profileTitle, string series, string seriesTitle, double years, DateOnly date,
        decimal value, string currency, int? trades = null) => new()
    {
        ["case"] = @case.Id, ["caseTitle"] = @case.Title, ["scenario"] = @case.Scenario.Id, ["scenarioTitle"] = @case.Scenario.Title,
        ["side"] = side, ["profile"] = profile, ["profileTitle"] = profileTitle, ["series"] = series, ["seriesTitle"] = seriesTitle,
        ["years"] = years, ["date"] = date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), ["value"] = value, ["currency"] = currency,
        ["trades"] = trades,
    };
}

/// <summary>
/// A case of an accepted input on one of its markets, in the shape a one-case input had (ADR-0010 §3's note of
/// 2026-11-08, §4's of 2026-11-09): what the cases share, and the case's own trades, module parts and measures, so a
/// provider loops over pairs with its body unchanged and writes each row and trace entry with the case's id and title and
/// the scenario's. The scenario's shifts are the provider's to apply to what it reads.
/// </summary>
public sealed record PricedCase(string Id, string Title, string Kind, JsonObject Input, PricedScenario Scenario)
{
    /// <summary>The market the pair is priced on, on the input's value date.</summary>
    public ScenarioMarket Market => new(Scenario.Shifts, DateOnly.ParseExact(
        (Input["context"]?["valueDate"] ?? throw new CapabilityFailure("InvalidInput", "The accepted input names no value date.")).GetValue<string>(),
        "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
}

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
        var scenarios = PortfolioScenarios.Priced(raw);
        // The cap counts the Base (ADR-0010 §3's note), and pairs of a case and a market where scenarios are included
        // (§4's note of 2026-11-09): a Run nobody can read is refused before it is made.
        var pairs = cases.Count * scenarios.Count;
        if (pairs > caseCap)
            throw new WorkspaceRefused("TooManyCases", scenarios.Count == 1
                ? $"Price prices {caseCap} cases at most, the Base among them; this workspace holds {cases.Count}, {cases.Count - caseCap} over. Remove {cases.Count - caseCap} to price."
                : $"Price prices {caseCap} pairs of a case and a market at most: {cases.Count} case(s) × {scenarios.Count} markets (the base market and {scenarios.Count - 1} scenario(s)) are {pairs}, {pairs - caseCap} over. Remove a case or exclude a scenario to price.");
        PortfolioCases.CheckAdditionSet(raw);
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
            // The markets every case is priced on, the base market first (ADR-0010 §4): each scenario with its shifts.
            ["scenarios"] = new JsonArray([.. scenarios.Select(sc => new JsonObject
            {
                ["id"] = sc.Id,
                ["title"] = sc.Title,
                ["shifts"] = new JsonArray([.. sc.Shifts.Select(sh => new JsonObject
                {
                    ["category"] = sh.Category, ["index"] = sh.Index, ["tenor"] = sh.Tenor, ["type"] = sh.Type, ["size"] = sh.Size,
                })]),
            })]),
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
        // A case of iterative addition prices its trade alone on top of the existing ones, never another set (docs/03 §5).
        if (@case.Kind == PortfolioCases.Add && trades.Where(t => t.IsNew && t.After).Select(t => t.TradeId).ToList() is var alone && !(alone.Count == 1 && alone[0] == @case.Trade))
            throw new WorkspaceRefused("AdditionSetChanged",
                $"{named}its After holds {(alone.Count == 0 ? "no new trade" : string.Join(", ", alone))}, not {@case.Trade} alone. Run add trades again to make the set anew.");
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
            // What a case of iterative addition carries in common is said wherever its title is (docs/03 §5).
            ["title"] = @case.Note is null ? @case.Title : $"{@case.Title} · {@case.Note}",
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
    /// Each case of an accepted input on each of its markets, the base market first and on each market the Base first
    /// (ADR-0010 §4: every scenario crossed with every case), in the shape a one-case input had: the shared fields with the
    /// case's own trades, module parts and measures. An input that holds no case, or no market, is one no provider can read.
    /// </summary>
    public static IReadOnlyList<PricedCase> Cases(JsonNode input)
    {
        var top = input as JsonObject ?? throw new CapabilityFailure("InvalidInput", "The accepted input is not an object.");
        if (top["cases"] is not JsonArray { Count: > 0 } cases)
            throw new CapabilityFailure("InvalidInput", "The accepted input holds no cases.");
        if (top["scenarios"] is not JsonArray { Count: > 0 } markets)
            throw new CapabilityFailure("InvalidInput", "The accepted input holds no market to price on.");
        var scenarios = markets.Select(Scenario).ToList();
        if (scenarios[0].Id != PortfolioScenarios.BaseId || scenarios[0].Shifts.Count > 0)
            throw new CapabilityFailure("InvalidInput", "The accepted input's first market is not the base market.");
        var shared = top.Where(p => p.Key is not ("cases" or "overrides" or "scenarios")).ToList();
        return [.. scenarios.SelectMany(scenario => cases.Select(node =>
        {
            var c = node as JsonObject ?? throw new CapabilityFailure("InvalidInput", "A case of the accepted input is not an object.");
            string Text(string key) => c[key]?.GetValue<string>() ?? throw new CapabilityFailure("InvalidInput", $"A case of the accepted input has no {key}.");
            var one = new JsonObject([.. shared.Select(p => KeyValuePair.Create(p.Key, p.Value?.DeepClone()))]);
            foreach (var key in new[] { "trades", "modules", "measures" })
                one[key] = (c[key] ?? throw new CapabilityFailure("InvalidInput", $"Case {Text("id")} of the accepted input has no {key}.")).DeepClone();
            return new PricedCase(Text("id"), Text("title"), Text("kind"), one, scenario);
        }))];
    }

    // A market of the accepted input as Price froze it: its id, title and shifts, each read whole.
    static PricedScenario Scenario(JsonNode? node)
    {
        var s = node as JsonObject ?? throw new CapabilityFailure("InvalidInput", "A market of the accepted input is not an object.");
        string Text(JsonNode? n, string what) => n?.GetValue<string>() ?? throw new CapabilityFailure("InvalidInput", $"A market of the accepted input has no {what}.");
        var id = Text(s["id"], "id");
        var shifts = (s["shifts"] as JsonArray ?? throw new CapabilityFailure("InvalidInput", $"Market {id} of the accepted input has no shifts.")).Select(x =>
            new Shift(Text(x?["category"], "shift category"), Text(x?["index"], "shift index"), x?["tenor"]?.GetValue<string>(), Text(x?["type"], "shift type"),
                x?["size"]?.GetValue<decimal>() ?? throw new CapabilityFailure("InvalidInput", $"A shift of market {id} has no size."))).ToList();
        return new PricedScenario(id, Text(s["title"], "title"), shifts);
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
