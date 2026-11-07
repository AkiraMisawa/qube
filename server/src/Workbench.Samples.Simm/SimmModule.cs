using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Workbench.Composition.Application;
using Workbench.Composition.Contracts;
using Workbench.Samples.Rates;

namespace Workbench.Samples.Simm;

public static class SimmContracts
{
    public const string Version = "1.0.0";
    public const string Registration = "simm.registration";
    public const string ValueDate = "simm.value-date";
    public const string MarketSnapshot = "simm.market-snapshot";
    public const string ParameterTable = "simm.parameter-table";
    public const string Crif = "simm.crif";
    public const string CrifTrades = "simm.crif-trades";
    public const string Im = "simm.im";
    public const string RegimeResult = "simm.regime-result";
    public const string LineContribution = "simm.line-contribution";
    public const string CrifInput = "simm.crif-input";
    public const string CalculateInput = "simm.calculate-input";
}

/// <summary>
/// SIMM's datasets on the wire. A CRIF line keeps CRIF's own vocabulary (<c>Risk_IRCurve</c>, regulations as a
/// comma-separated list), so a CRIF written here reads back line for line.
/// </summary>
public static class SimmDatasets
{
    static readonly IReadOnlyDictionary<CrifRiskType, string> RiskTypeNames = new Dictionary<CrifRiskType, string>
    {
        [CrifRiskType.IrCurve] = "Risk_IRCurve", [CrifRiskType.XCcyBasis] = "Risk_XCcyBasis", [CrifRiskType.Fx] = "Risk_FX",
    };

    static readonly IReadOnlyDictionary<string, CrifRiskType> RiskTypes = RiskTypeNames.ToDictionary(p => p.Value, p => p.Key, StringComparer.Ordinal);

    public static readonly IReadOnlyList<DatasetColumn> CrifColumns =
    [
        new("line", DatasetColumnType.String, false),
        new("tradeId", DatasetColumnType.String, false),
        new("riskType", DatasetColumnType.String, false),
        new("qualifier", DatasetColumnType.String, false),
        new("label1", DatasetColumnType.String, true),
        new("label2", DatasetColumnType.String, true),
        new("amount", DatasetColumnType.Number, false),
        new("amountCurrency", DatasetColumnType.String, false),
        new("amountUsd", DatasetColumnType.Number, false),
        new("collectRegulations", DatasetColumnType.String, true),
        new("postRegulations", DatasetColumnType.String, true),
    ];

    public static readonly IReadOnlyList<DatasetColumn> TradeColumns =
    [
        new("tradeId", DatasetColumnType.String, false),
        new("description", DatasetColumnType.String, false),
        new("collectRegulations", DatasetColumnType.String, true),
        new("postRegulations", DatasetColumnType.String, true),
        new("lines", DatasetColumnType.Integer, false),
        new("excluded", DatasetColumnType.String, true),
    ];

    public static readonly IReadOnlyList<DatasetColumn> ImColumns =
    [
        new("direction", DatasetColumnType.String, false),
        new("csa", DatasetColumnType.String, false),
        new("im", DatasetColumnType.Number, true),
        new("governingRegime", DatasetColumnType.String, true),
        new("parameters", DatasetColumnType.String, false),
    ];

    public static readonly IReadOnlyList<DatasetColumn> RegimeColumns =
    [
        new("direction", DatasetColumnType.String, false),
        new("csa", DatasetColumnType.String, false),
        new("regime", DatasetColumnType.String, false),
        new("simm", DatasetColumnType.Number, false),
        new("governing", DatasetColumnType.Boolean, false),
        new("parameters", DatasetColumnType.String, false),
    ];

    public static readonly IReadOnlyList<DatasetColumn> ContributionColumns =
    [
        new("direction", DatasetColumnType.String, false),
        new("regime", DatasetColumnType.String, false),
        new("line", DatasetColumnType.String, false),
        new("tradeId", DatasetColumnType.String, false),
        new("amountUsd", DatasetColumnType.Number, false),
        new("contribution", DatasetColumnType.Number, false),
    ];

    static string? List(IReadOnlyList<string> regulations) => regulations.Count == 0 ? null : string.Join(",", regulations);

    static IReadOnlyList<string> List(JsonNode? text) =>
        text?.GetValue<string>() is { Length: > 0 } t ? [.. t.Split(',').Select(r => r.Trim())] : [];

    public static string Name(ImDirection direction) => direction.ToString().ToLowerInvariant();

    public static JsonObject Crif(IEnumerable<CrifLine> lines, string title, DatasetProvenance provenance) => Datasets.Create(title, CrifColumns,
        lines.Select(l => new JsonObject
        {
            ["line"] = l.Line, ["tradeId"] = l.TradeId, ["riskType"] = RiskTypeNames[l.RiskType], ["qualifier"] = l.Qualifier,
            ["label1"] = l.Label1, ["label2"] = l.Label2, ["amount"] = l.Amount, ["amountCurrency"] = l.AmountCurrency, ["amountUsd"] = l.AmountUsd,
            ["collectRegulations"] = List(l.CollectRegulations), ["postRegulations"] = List(l.PostRegulations),
        }), provenance);

    /// <summary>A CRIF dataset's lines, as written. A risk type outside the PoC's is refused by line, not skipped.</summary>
    public static IReadOnlyList<CrifLine> ReadCrif(JsonNode dataset)
    {
        var problems = new List<string>();
        var lines = new List<CrifLine>();
        foreach (var row in dataset["rows"]?.AsArray() ?? throw new SimmInputException(["The CRIF has no rows."]))
        {
            var line = row!["line"]!.GetValue<string>();
            var name = row["riskType"]!.GetValue<string>();
            if (!RiskTypes.TryGetValue(name, out var type))
            {
                problems.Add($"{line}: the risk type {name} is not one the engine computes ({string.Join(", ", RiskTypeNames.Values)}).");
                continue;
            }
            lines.Add(new(line, row["tradeId"]!.GetValue<string>(), type, row["qualifier"]!.GetValue<string>(),
                row["label1"]?.GetValue<string>(), row["label2"]?.GetValue<string>(), row["amount"]!.GetValue<double>(),
                row["amountCurrency"]!.GetValue<string>(), row["amountUsd"]!.GetValue<double>(), List(row["collectRegulations"]), List(row["postRegulations"])));
        }
        if (problems.Count > 0) throw new SimmInputException(problems);
        return lines;
    }

    public static DatasetProvenance Provenance(JsonNode dataset) =>
        dataset["provenance"].Deserialize<DatasetProvenance>(WireJson.Options) ?? throw new SimmInputException(["The dataset has no provenance."]);
}

/// <summary>
/// <c>risk.crif.generate</c> (ADR-0012 §4): the source's trades for a registration, frozen at the value date, and a
/// pinned market snapshot in; the CRIF and, per trade, its lines or why it has none, out. The pricing is the configured
/// Rates provider's, and the CRIF says the mode it answered in.
/// </summary>
public sealed class CrifGenerate(IRatesPricingProvider provider, Func<string, MarketSnapshot?> resolveMarket, ConventionRegistry conventions) : ICapabilityHandler
{
    public CapabilityDescriptor Descriptor { get; } = new("risk.crif.generate", SimmContracts.Version, "Generate CRIF from the Rates pricing",
        [new("registration", SimmContracts.Registration, SimmContracts.Version, Cardinality.One),
         new("valueDate", SimmContracts.ValueDate, SimmContracts.Version, Cardinality.One),
         new("marketSnapshotId", SimmContracts.MarketSnapshot, SimmContracts.Version, Cardinality.One)],
        [new("crif", SimmContracts.Crif, SimmContracts.Version, Cardinality.One),
         new("trades", SimmContracts.CrifTrades, SimmContracts.Version, Cardinality.One)],
        ExecutionKind.RequestResponse, [CapabilityEffect.Compute]);

    public async Task<IReadOnlyDictionary<string, JsonNode?>> InvokeAsync(CapabilityInvocation invocation, CancellationToken cancellationToken)
    {
        var registration = Text(invocation, "registration");
        var portfolio = SyntheticImSource.Find(registration)
            ?? throw new CapabilityFailure("UnknownRegistration", $"The IM source {SyntheticImSource.Version} holds no IM CSA for {registration}.");
        var valueDate = DateOnly.ParseExact(Text(invocation, "valueDate"), "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var snapshotId = Text(invocation, "marketSnapshotId");
        var market = resolveMarket(snapshotId) ?? throw new CapabilityFailure("MarketUnavailable", $"Market snapshot {snapshotId} cannot be resolved.");

        var (frozen, notFrozen) = portfolio.Freeze(valueDate, conventions);
        CrifResult crif;
        try
        {
            crif = await new CrifGenerator(provider).GenerateAsync(frozen, valueDate, market, cancellationToken);
        }
        catch (SimmInputException refused)
        {
            throw new CapabilityFailure("InvalidInput", refused.Message);
        }
        var provenance = new DatasetProvenance(crif.CalculationMode == FakeRatesPricing.Mode ? ExecutionMode.Simulated : ExecutionMode.Actual,
            market.Origin == SyntheticMarket.Origin ? DataOrigin.Synthetic : DataOrigin.Source, invocation.InvocationId);
        var excluded = crif.Excluded.Concat(notFrozen).ToDictionary(e => e.TradeId, e => e.Reason, StringComparer.Ordinal);
        var counts = crif.Lines.GroupBy(l => l.TradeId).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var label = $"{crif.CalculationMode}, {market.Id}, {valueDate:yyyy-MM-dd}";
        return new Dictionary<string, JsonNode?>
        {
            ["crif"] = SimmDatasets.Crif(crif.Lines, $"CRIF {registration} ({label})", provenance),
            ["trades"] = Datasets.Create($"CRIF trades {registration} ({label})", SimmDatasets.TradeColumns, portfolio.Trades.Select(t => new JsonObject
            {
                ["tradeId"] = t.Id, ["description"] = t.Description,
                ["collectRegulations"] = t.CollectRegulations.Count == 0 ? null : string.Join(",", t.CollectRegulations),
                ["postRegulations"] = t.PostRegulations.Count == 0 ? null : string.Join(",", t.PostRegulations),
                ["lines"] = counts.GetValueOrDefault(t.Id), ["excluded"] = excluded.GetValueOrDefault(t.Id),
            }), provenance),
        };
    }

    internal static string Text(CapabilityInvocation invocation, string name) =>
        invocation.Inputs.GetValueOrDefault(name)?.GetValue<string>() ?? throw new CapabilityFailure("InvalidInput", $"The input {name} is required.");
}

/// <summary>
/// <c>margin.simm.calculate</c> (ADR-0012 §4): a CRIF, the registration's IM CSAs and a parameter table in; spot SIMM per
/// direction and regime, and each line's Euler contribution, out. Figures computed with a DEMO table, or from a
/// simulated CRIF, are simulated and say <c>SIMM PARAMETERS DEMO</c>.
/// </summary>
public sealed class SimmCalculate(IReadOnlyList<SimmParameters> tables) : ICapabilityHandler
{
    public CapabilityDescriptor Descriptor { get; } = new("margin.simm.calculate", SimmContracts.Version, "Calculate spot SIMM",
        [new("crif", SimmContracts.Crif, SimmContracts.Version, Cardinality.One),
         new("registration", SimmContracts.Registration, SimmContracts.Version, Cardinality.One),
         new("parameters", SimmContracts.ParameterTable, SimmContracts.Version, Cardinality.One)],
        [new("im", SimmContracts.Im, SimmContracts.Version, Cardinality.One),
         new("regimes", SimmContracts.RegimeResult, SimmContracts.Version, Cardinality.One),
         new("contributions", SimmContracts.LineContribution, SimmContracts.Version, Cardinality.One)],
        ExecutionKind.RequestResponse, [CapabilityEffect.Compute]);

    public Task<IReadOnlyDictionary<string, JsonNode?>> InvokeAsync(CapabilityInvocation invocation, CancellationToken cancellationToken)
    {
        var reference = invocation.Inputs.GetValueOrDefault("crif").Deserialize<TypedDataRef>(WireJson.Options)
            ?? throw new CapabilityFailure("InvalidInput", "The input crif is required.");
        if (reference.ContractId != SimmContracts.Crif)
            throw new CapabilityFailure("InvalidInput", $"{reference.DataId} is a {reference.ContractId}, not a CRIF ({SimmContracts.Crif}).");
        var registration = CrifGenerate.Text(invocation, "registration");
        var portfolio = SyntheticImSource.Find(registration)
            ?? throw new CapabilityFailure("UnknownRegistration", $"The IM source {SyntheticImSource.Version} holds no IM CSA for {registration}.");
        var version = CrifGenerate.Text(invocation, "parameters");
        var table = tables.FirstOrDefault(t => t.Version == version)
            ?? throw new CapabilityFailure("UnknownParameterTable", $"No SIMM parameter table {version} is loaded; loaded: {string.Join(", ", tables.Select(t => t.Version))}.");

        var dataset = invocation.Data.ReadRequired(reference);
        SimmResult result;
        DatasetProvenance crifProvenance;
        try
        {
            var crif = SimmDatasets.ReadCrif(dataset);
            crifProvenance = SimmDatasets.Provenance(dataset);
            var unknown = crif.SelectMany(l => l.CollectRegulations.Concat(l.PostRegulations).Distinct().Where(r => !portfolio.Regimes.Contains(r))
                .Select(r => $"{l.Line} names the regime {r}, which the IM source {portfolio.SourceVersion} does not know ({string.Join(", ", portfolio.Regimes)}).")).ToList();
            if (unknown.Count > 0) throw new CapabilityFailure("UnknownRegime", string.Join(" ", unknown));
            result = SimmEngine.Calculate(crif, portfolio.Csas, table);
        }
        catch (SimmInputException refused)
        {
            throw new CapabilityFailure("InvalidCrif", refused.Message);
        }

        var provenance = new DatasetProvenance(crifProvenance.ExecutionMode == ExecutionMode.Simulated || table.IsDemo ? ExecutionMode.Simulated : ExecutionMode.Actual,
            crifProvenance.DataOrigin, invocation.InvocationId);
        var label = $"{(table.IsDemo ? "SIMM PARAMETERS DEMO" : $"SIMM {table.Version}")}, {(provenance.ExecutionMode == ExecutionMode.Simulated ? "simulated" : "actual")}";
        return Task.FromResult<IReadOnlyDictionary<string, JsonNode?>>(new Dictionary<string, JsonNode?>
        {
            ["im"] = Datasets.Create($"Spot IM {registration} ({label})", SimmDatasets.ImColumns, result.Directions.Select(d => new JsonObject
            {
                ["direction"] = SimmDatasets.Name(d.Direction), ["csa"] = d.CsaId, ["im"] = d.Im, ["governingRegime"] = d.GoverningRegime, ["parameters"] = table.Version,
            }), provenance),
            ["regimes"] = Datasets.Create($"Spot SIMM per regime {registration} ({label})", SimmDatasets.RegimeColumns, result.Directions.SelectMany(d => d.Regimes.Select(r => new JsonObject
            {
                ["direction"] = SimmDatasets.Name(d.Direction), ["csa"] = d.CsaId, ["regime"] = r.Regime, ["simm"] = r.Total.Value,
                ["governing"] = r.Regime == d.GoverningRegime, ["parameters"] = table.Version,
            })), provenance),
            ["contributions"] = Datasets.Create($"Spot SIMM line contributions {registration} ({label})", SimmDatasets.ContributionColumns,
                result.Directions.SelectMany(d => d.Regimes.SelectMany(r => r.Total.Lines().OrderBy(l => l.Line, StringComparer.Ordinal).Select(l => new JsonObject
                {
                    ["direction"] = SimmDatasets.Name(d.Direction), ["regime"] = r.Regime, ["line"] = l.Line, ["tradeId"] = l.TradeId,
                    ["amountUsd"] = l.AmountUsd, ["contribution"] = l.Contribution,
                }))), provenance),
        });
    }
}

/// <summary>
/// Registers spot SIMM's step 1 (ADR-0012 delivery order): the two capabilities, their input contracts, and the CRIF
/// workflow, which runs from a form. SIMM alone runs on a CRIF a caller retained, so its workflow names that CRIF.
/// </summary>
public sealed class SimmModule(IRatesPricingProvider provider, Func<string, MarketSnapshot?> resolveMarket, ConventionRegistry conventions,
    IReadOnlyList<SimmParameters> tables) : IWorkbenchModule
{
    public const string CrifWorkflow = "workflow.simm.crif";
    public const string CalculateWorkflow = "workflow.simm.calculate";

    static JsonObject Registration() => new()
    {
        ["type"] = "string", ["title"] = "Counterparty registration", ["enum"] = new JsonArray([.. SyntheticImSource.Registrations.Select(r => JsonValue.Create(r))]),
    };

    public void Register(CompositionRegistryBuilder builder)
    {
        builder.AddCapability(new CrifGenerate(provider, resolveMarket, conventions));
        builder.AddCapability(new SimmCalculate(tables));
        builder.AddContract(new ContractDefinition(SimmContracts.CrifInput, SimmContracts.Version, "CRIF input", new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["registration"] = Registration(),
                ["valueDate"] = new JsonObject { ["type"] = "string", ["title"] = "Value date", ["format"] = "date" },
                ["marketSnapshotId"] = new JsonObject { ["type"] = "string", ["title"] = "Market snapshot", ["minLength"] = 1 },
            },
            ["required"] = new JsonArray("registration", "valueDate", "marketSnapshotId"),
            ["additionalProperties"] = false,
        }));
        // The tables a calculation may name are the ones loaded: another is refused at submission, by its field.
        builder.AddContract(new ContractDefinition(SimmContracts.CalculateInput, SimmContracts.Version, "Spot SIMM input", new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["registration"] = Registration(),
                ["parameters"] = new JsonObject
                {
                    ["type"] = "string", ["title"] = "SIMM parameter table", ["enum"] = new JsonArray([.. tables.Select(t => JsonValue.Create(t.Version))]),
                },
            },
            ["required"] = new JsonArray("registration", "parameters"),
            ["additionalProperties"] = false,
        }));
        builder.AddWorkflow(new WorkflowDefinition("workflow", CompositionValidator.SchemaVersion, CrifWorkflow, SimmContracts.Version,
            "Generate CRIF", SimmContracts.CrifInput, SimmContracts.Version,
            [new WorkflowStep("crif", "risk.crif.generate", SimmContracts.Version, [], new Dictionary<string, Binding>
            {
                ["registration"] = new InputBinding("/registration"), ["valueDate"] = new InputBinding("/valueDate"),
                ["marketSnapshotId"] = new InputBinding("/marketSnapshotId"),
            }, FailurePolicy.Stop)],
            new Dictionary<string, Binding> { ["crif"] = new OutputBinding("crif", "crif"), ["trades"] = new OutputBinding("crif", "trades") }));
    }

    /// <summary>Spot SIMM alone on a retained CRIF (ADR-0012 §4): the registration's IM CSAs and the table come from the input.</summary>
    public static WorkflowDefinition Calculate(TypedDataRef crif) => new("workflow", CompositionValidator.SchemaVersion, CalculateWorkflow, SimmContracts.Version,
        "Calculate spot SIMM", SimmContracts.CalculateInput, SimmContracts.Version,
        [new WorkflowStep("simm", "margin.simm.calculate", SimmContracts.Version, [], new Dictionary<string, Binding>
        {
            ["crif"] = new DataReferenceBinding(crif), ["registration"] = new InputBinding("/registration"), ["parameters"] = new InputBinding("/parameters"),
        }, FailurePolicy.Stop)],
        new Dictionary<string, Binding>
        {
            ["im"] = new OutputBinding("simm", "im"), ["regimes"] = new OutputBinding("simm", "regimes"), ["contributions"] = new OutputBinding("simm", "contributions"),
        });
}
