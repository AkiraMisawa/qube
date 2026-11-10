using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Qube.Composition.Application;
using Qube.Composition.Contracts;
using Qube.Samples.Rates;

namespace Qube.Samples.Simm;

public static class SimmContracts
{
    public const string Version = "1.0.0";
    public const string Registration = "simm.registration";
    public const string ValueDate = "simm.value-date";
    public const string MarketSnapshot = "simm.market-snapshot";
    public const string ParameterTable = "simm.parameter-table";
    public const string FrozenTrades = "simm.frozen-trades";
    public const string FrozenCsas = "simm.frozen-csas";
    public const string Crif = "simm.crif";
    public const string CrifHeader = "simm.crif-header";
    public const string CrifCoverage = "simm.crif-coverage";
    public const string Im = "simm.im";
    public const string RegimeResult = "simm.regime-result";
    public const string CrifInput = "simm.crif-input";
    public const string CalculateInput = "simm.calculate-input";
}

/// <summary>
/// SIMM's datasets on the wire. A CRIF line keeps CRIF's own vocabulary (<c>Risk_IRCurve</c>, regulations as a
/// comma-separated list), so a CRIF written here reads back line for line; the registration and value date it is of
/// are its header, in the dataset's metadata.
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

    /// <summary>Per trade, the CRIF lines it gave or why it gave none (ADR-0012 §2: out, with its reason).</summary>
    public static readonly IReadOnlyList<DatasetColumn> CoverageColumns =
    [
        new("tradeId", DatasetColumnType.String, false),
        new("description", DatasetColumnType.String, true),
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

    /// <summary>Regulations as CRIF writes them: a comma-separated list, or nothing when there are none.</summary>
    public static string? List(IReadOnlyList<string> regulations) => regulations.Count == 0 ? null : string.Join(",", regulations);

    static IReadOnlyList<string> List(string? text) => text is { Length: > 0 } t ? [.. t.Split(',').Select(r => r.Trim())] : [];

    public static string Name(ImDirection direction) => direction.ToString().ToLowerInvariant();

    /// <summary>CRIF's own name for a risk type (<c>Risk_IRCurve</c>).</summary>
    public static string Name(CrifRiskType riskType) => RiskTypeNames[riskType];

    public static JsonObject Crif(CrifDocument crif, string title, DatasetProvenance provenance)
    {
        var dataset = Datasets.Create(title, CrifColumns, crif.Lines.Select(l => new JsonObject
        {
            ["line"] = l.Line, ["tradeId"] = l.TradeId, ["riskType"] = Name(l.RiskType), ["qualifier"] = l.Qualifier,
            ["label1"] = l.Label1, ["label2"] = l.Label2, ["amount"] = l.Amount, ["amountCurrency"] = l.AmountCurrency, ["amountUsd"] = l.AmountUsd,
            ["collectRegulations"] = List(l.CollectRegulations), ["postRegulations"] = List(l.PostRegulations),
        }), provenance);
        dataset["metadata"] = new JsonObject
        {
            ["contractId"] = SimmContracts.CrifHeader, ["contractVersion"] = SimmContracts.Version,
            ["value"] = new JsonObject { ["registration"] = crif.Registration, ["valueDate"] = crif.ValueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) },
        };
        return dataset;
    }

    /// <summary>
    /// A CRIF dataset as written. A header, a value or a risk type the engine cannot read is refused by name, line by
    /// line, never skipped or defaulted.
    /// </summary>
    public static CrifDocument ReadCrif(JsonNode dataset)
    {
        var header = dataset["metadata"] is JsonObject metadata && Text(metadata["contractId"]) == SimmContracts.CrifHeader ? metadata["value"] : null;
        if ((Text(header?["registration"]), Date(Text(header?["valueDate"]))) is not ({ } registration, { } valueDate))
            throw new SimmInputException([$"The CRIF's header ({SimmContracts.CrifHeader}) names no registration and value date."]);
        if (dataset["rows"] is not JsonArray rows) throw new SimmInputException(["The CRIF has no rows."]);

        var problems = new List<string>();
        var lines = new List<CrifLine>();
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var missing = new List<string>();
            string? Required(string column) { var text = Text(row?[column]); if (text is null) missing.Add(column); return text; }
            double? Number(string column)
            {
                if (row?[column] is JsonValue v && v.TryGetValue<double>(out var number)) return number;
                missing.Add(column);
                return null;
            }
            string? Optional(string column) => row?[column] is null ? null : Required(column);
            var at = Text(row?["line"]) ?? $"Row {i + 1}";
            var read = (Required("line"), Required("tradeId"), Required("riskType"), Required("qualifier"), Required("amountCurrency"), Number("amount"), Number("amountUsd"));
            var (label1, label2, collect, post) = (Optional("label1"), Optional("label2"), Optional("collectRegulations"), Optional("postRegulations"));
            if (read is not ({ } line, { } tradeId, { } riskType, { } qualifier, { } currency, { } amount, { } amountUsd))
            {
                problems.Add($"{at}: {string.Join(", ", missing)} is missing or not of its type.");
                continue;
            }
            if (!RiskTypes.TryGetValue(riskType, out var type))
            {
                problems.Add($"{line}: the risk type {riskType} is not one the engine computes ({string.Join(", ", RiskTypeNames.Values)}).");
                continue;
            }
            lines.Add(new(line, tradeId, type, qualifier, label1, label2, amount, currency, amountUsd, List(collect), List(post)));
        }
        if (problems.Count > 0) throw new SimmInputException(problems);
        return new(registration, valueDate, lines);
    }

    static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var text) ? text : null;

    static DateOnly? Date(string? text) =>
        DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

    public static DatasetProvenance Provenance(JsonNode dataset) =>
        dataset["provenance"].Deserialize<DatasetProvenance>(WireJson.Options) ?? throw new SimmInputException(["The dataset has no provenance."]);

    /// <summary>Data from a synthetic source and data from a real one together are mixed, never either alone.</summary>
    public static DataOrigin Combine(DataOrigin a, DataOrigin b) => a == b ? a : DataOrigin.Mixed;
}

/// <summary>A capability's inputs, read or refused by name.</summary>
static class SimmInputs
{
    public static string Text(CapabilityInvocation invocation, string name) =>
        invocation.Inputs.GetValueOrDefault(name) is JsonValue v && v.TryGetValue<string>(out var text) ? text
            : throw new CapabilityFailure("InvalidInput", $"The input {name} is required.");

    public static DateOnly Date(CapabilityInvocation invocation, string name) =>
        DateOnly.TryParseExact(Text(invocation, name), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date
            : throw new CapabilityFailure("InvalidInput", $"The input {name} is not a date (yyyy-MM-dd).");

    /// <summary>The retained data a reference input names, refused when it is of another contract.</summary>
    public static JsonNode Reference(CapabilityInvocation invocation, string name, string contractId)
    {
        var reference = invocation.Inputs.GetValueOrDefault(name).Deserialize<TypedDataRef>(WireJson.Options)
            ?? throw new CapabilityFailure("InvalidInput", $"The input {name} is required.");
        if (reference.ContractId != contractId)
            throw new CapabilityFailure("InvalidInput", $"{reference.DataId} is a {reference.ContractId}, not a {contractId}.");
        return invocation.Data.ReadRequired(reference);
    }

    /// <summary>The wire options with nulls written, as the Rates module's frozen input is (an empty quote is part of it).</summary>
    public static JsonSerializerOptions Json => RatesAcceptedInput.Json;
}

/// <summary>
/// <c>im.source.freeze</c>: what the IM source holds for a registration, frozen for one calculation. Its trades are
/// resolved at the value date with the confirmed conventions, and its IM CSAs are read as they stand. Both are retained
/// on the Run, so a CRIF and the IM computed from it resolve the inputs they were computed from (ADR-0012 §4).
/// </summary>
public sealed class ImSourceFreeze(IImSource source, ConventionRegistry conventions) : ICapabilityHandler
{
    public CapabilityDescriptor Descriptor { get; } = new("im.source.freeze", SimmContracts.Version, "Freeze a registration's IM CSAs and trades",
        [new("registration", SimmContracts.Registration, SimmContracts.Version, Cardinality.One),
         new("valueDate", SimmContracts.ValueDate, SimmContracts.Version, Cardinality.One)],
        [new("trades", SimmContracts.FrozenTrades, SimmContracts.Version, Cardinality.One),
         new("csas", SimmContracts.FrozenCsas, SimmContracts.Version, Cardinality.One)],
        ExecutionKind.RequestResponse, [CapabilityEffect.ReadData]);

    public Task<IReadOnlyDictionary<string, JsonNode?>> InvokeAsync(CapabilityInvocation invocation, CancellationToken cancellationToken)
    {
        var registration = SimmInputs.Text(invocation, "registration");
        var portfolio = source.Find(registration)
            ?? throw new CapabilityFailure("UnknownRegistration", $"The IM source {source.Version} holds no IM CSA for {registration}.");
        var valueDate = SimmInputs.Date(invocation, "valueDate");
        return Task.FromResult<IReadOnlyDictionary<string, JsonNode?>>(new Dictionary<string, JsonNode?>
        {
            ["trades"] = JsonSerializer.SerializeToNode(portfolio.FreezeTrades(valueDate, conventions), SimmInputs.Json),
            ["csas"] = JsonSerializer.SerializeToNode(portfolio.FreezeCsas(), SimmInputs.Json),
        });
    }
}

/// <summary>
/// <c>risk.crif.generate</c> (ADR-0012 §4): frozen trades and a pinned market snapshot in; the CRIF, as of the trades'
/// value date and of their registration, and per trade its lines or why it has none, out. The pricing is the
/// configured Rates provider's, and the CRIF says the mode it answered in.
/// </summary>
public sealed class CrifGenerate(IRatesPricingProvider provider, Func<string, MarketSnapshot?> resolveMarket) : ICapabilityHandler
{
    public CapabilityDescriptor Descriptor { get; } = new("risk.crif.generate", SimmContracts.Version, "Generate CRIF from the Rates pricing",
        [new("trades", SimmContracts.FrozenTrades, SimmContracts.Version, Cardinality.One),
         new("marketSnapshotId", SimmContracts.MarketSnapshot, SimmContracts.Version, Cardinality.One)],
        [new("crif", SimmContracts.Crif, SimmContracts.Version, Cardinality.One),
         new("coverage", SimmContracts.CrifCoverage, SimmContracts.Version, Cardinality.One)],
        ExecutionKind.RequestResponse, [CapabilityEffect.Compute]);

    public async Task<IReadOnlyDictionary<string, JsonNode?>> InvokeAsync(CapabilityInvocation invocation, CancellationToken cancellationToken)
    {
        var frozen = SimmInputs.Reference(invocation, "trades", SimmContracts.FrozenTrades).Deserialize<FrozenTrades>(SimmInputs.Json)
            ?? throw new CapabilityFailure("InvalidInput", "The frozen trades are null.");
        var snapshotId = SimmInputs.Text(invocation, "marketSnapshotId");
        var market = resolveMarket(snapshotId) ?? throw new CapabilityFailure("MarketUnavailable", $"Market snapshot {snapshotId} cannot be resolved.");

        CrifResult crif;
        try
        {
            crif = await new CrifGenerator(provider).GenerateAsync(frozen.Trades, frozen.ValueDate, market, cancellationToken);
        }
        catch (SimmInputException refused)
        {
            throw new CapabilityFailure("InvalidInput", refused.Message);
        }
        var provenance = new DatasetProvenance(crif.CalculationMode == FakeRatesPricing.Mode ? ExecutionMode.Simulated : ExecutionMode.Actual,
            SimmDatasets.Combine(market.Origin == SyntheticMarket.Origin ? DataOrigin.Synthetic : DataOrigin.Source, frozen.Origin), invocation.InvocationId);
        var excluded = crif.Excluded.ToDictionary(e => e.TradeId, e => e.Reason, StringComparer.Ordinal);
        var counts = crif.Lines.GroupBy(l => l.TradeId).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var label = $"{crif.CalculationMode}, {market.Id}, {frozen.ValueDate:yyyy-MM-dd}";
        var coverage = frozen.Trades.Select(t => new JsonObject
        {
            ["tradeId"] = t.Id, ["description"] = t.Description, ["collectRegulations"] = SimmDatasets.List(t.CollectRegulations),
            ["postRegulations"] = SimmDatasets.List(t.PostRegulations), ["lines"] = counts.GetValueOrDefault(t.Id), ["excluded"] = excluded.GetValueOrDefault(t.Id),
        }).Concat(frozen.NotFrozen.Select(t => new JsonObject
        {
            ["tradeId"] = t.TradeId, ["description"] = null, ["collectRegulations"] = null, ["postRegulations"] = null, ["lines"] = 0, ["excluded"] = t.Reason,
        }));
        return new Dictionary<string, JsonNode?>
        {
            ["crif"] = SimmDatasets.Crif(new(frozen.Registration, frozen.ValueDate, crif.Lines), $"CRIF {frozen.Registration} ({label})", provenance),
            ["coverage"] = Datasets.Create($"CRIF coverage {frozen.Registration} ({label})", SimmDatasets.CoverageColumns, coverage, provenance),
        };
    }
}

/// <summary>
/// <c>margin.simm.calculate</c> (ADR-0012 §4): a CRIF, the registration's frozen IM CSAs and a parameter table in;
/// spot SIMM per direction and regime and the engine's calculation trace, whose leaf rows carry each CRIF line's
/// Euler contribution, out. A figure computed with a DEMO table says <c>SIMM PARAMETERS DEMO</c>; one computed from a
/// simulated CRIF, or with a DEMO table, is simulated.
/// </summary>
public sealed class SimmCalculate(IReadOnlyList<SimmParameters> tables) : ICapabilityHandler
{
    public CapabilityDescriptor Descriptor { get; } = new("margin.simm.calculate", SimmContracts.Version, "Calculate spot SIMM",
        [new("crif", SimmContracts.Crif, SimmContracts.Version, Cardinality.One),
         new("csas", SimmContracts.FrozenCsas, SimmContracts.Version, Cardinality.One),
         new("parameters", SimmContracts.ParameterTable, SimmContracts.Version, Cardinality.One)],
        [new("im", SimmContracts.Im, SimmContracts.Version, Cardinality.One),
         new("regimes", SimmContracts.RegimeResult, SimmContracts.Version, Cardinality.One),
         new("trace", CalculationTrace.ContractId, CalculationTrace.ContractVersion, Cardinality.One)],
        ExecutionKind.RequestResponse, [CapabilityEffect.Compute]);

    public Task<IReadOnlyDictionary<string, JsonNode?>> InvokeAsync(CapabilityInvocation invocation, CancellationToken cancellationToken)
    {
        var version = SimmInputs.Text(invocation, "parameters");
        var table = tables.FirstOrDefault(t => t.Version == version)
            ?? throw new CapabilityFailure("UnknownParameterTable", $"No SIMM parameter table {version} is loaded; loaded: {string.Join(", ", tables.Select(t => t.Version))}.");
        var csas = SimmInputs.Reference(invocation, "csas", SimmContracts.FrozenCsas).Deserialize<FrozenCsas>(SimmInputs.Json)
            ?? throw new CapabilityFailure("InvalidInput", "The frozen IM CSAs are null.");
        var dataset = SimmInputs.Reference(invocation, "crif", SimmContracts.Crif);

        CrifDocument crif;
        DatasetProvenance crifProvenance;
        SimmResult result;
        try
        {
            crif = SimmDatasets.ReadCrif(dataset);
            crifProvenance = SimmDatasets.Provenance(dataset);
            // Lines of one counterparty under another's IM CSAs would fall out of every scope and give no IM, quietly.
            if (crif.Registration != csas.Registration)
                throw new CapabilityFailure("RegistrationMismatch", $"The CRIF is {crif.Registration}'s, and the IM CSAs are {csas.Registration}'s.");
            var unknown = crif.Lines.SelectMany(l => l.CollectRegulations.Concat(l.PostRegulations).Distinct().Where(r => !csas.Regimes.Contains(r))
                .Select(r => $"{l.Line} names the regime {r}, which the IM source {csas.SourceVersion} does not know ({string.Join(", ", csas.Regimes)}).")).ToList();
            if (unknown.Count > 0) throw new CapabilityFailure("UnknownRegime", string.Join(" ", unknown));
            result = SimmEngine.Calculate(crif.Lines, csas.Csas, table);
        }
        catch (SimmInputException refused)
        {
            throw new CapabilityFailure("InvalidCrif", refused.Message);
        }

        var registration = crif.Registration;
        var provenance = new DatasetProvenance(crifProvenance.ExecutionMode == ExecutionMode.Simulated || table.IsDemo ? ExecutionMode.Simulated : ExecutionMode.Actual,
            SimmDatasets.Combine(crifProvenance.DataOrigin, csas.Origin), invocation.InvocationId);
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
            // The trace the engine wrote as it computed (ADR-0011 §3), with the request, the date and the subject it is of, labelled as its figures are.
            ["trace"] = JsonSerializer.SerializeToNode(result.Trace with
            {
                RequestId = invocation.InvocationId, ValuationDate = crif.ValueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                Subject = new("registration", registration), Provenance = provenance,
            }, WireJson.Options),
        });
    }
}

/// <summary>
/// Registers spot SIMM's step 1 (ADR-0012 delivery order): the capabilities, their input contracts, the trace catalogue
/// of each loaded table, and the CRIF workflow, which freezes the IM source's trades and IM CSAs and generates the
/// CRIF from a form. SIMM alone runs on a CRIF and IM CSAs a Run retained, so its workflow names them.
/// </summary>
public sealed class SimmModule(IImSource source, IRatesPricingProvider provider, Func<string, MarketSnapshot?> resolveMarket, ConventionRegistry conventions,
    IReadOnlyList<SimmParameters> tables) : IQubeModule
{
    public const string CrifWorkflow = "workflow.simm.crif";
    public const string CalculateWorkflow = "workflow.simm.calculate";

    public void Register(CompositionRegistryBuilder builder)
    {
        builder.AddCapability(new ImSourceFreeze(source, conventions));
        builder.AddCapability(new CrifGenerate(provider, resolveMarket));
        builder.AddCapability(new SimmCalculate(tables));
        builder.AddCapability(new SpotImPrice(provider, resolveMarket, conventions, tables));
        foreach (var table in tables) builder.AddTraceCatalog(SimmTraceCatalog.For(table));
        builder.AddContract(new ContractDefinition(SimmContracts.CrifInput, SimmContracts.Version, "CRIF input", new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["registration"] = new JsonObject
                {
                    ["type"] = "string", ["title"] = "Counterparty registration", ["enum"] = new JsonArray([.. source.Registrations.Select(r => JsonValue.Create(r))]),
                },
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
                ["parameters"] = new JsonObject
                {
                    ["type"] = "string", ["title"] = "SIMM parameter table", ["enum"] = new JsonArray([.. tables.Select(t => JsonValue.Create(t.Version))]),
                },
            },
            ["required"] = new JsonArray("parameters"),
            ["additionalProperties"] = false,
        }));
        builder.AddWorkflow(new WorkflowDefinition("workflow", CompositionValidator.SchemaVersion, CrifWorkflow, SimmContracts.Version,
            "Generate CRIF", SimmContracts.CrifInput, SimmContracts.Version,
            [new WorkflowStep("freeze", "im.source.freeze", SimmContracts.Version, [], new Dictionary<string, Binding>
             {
                 ["registration"] = new InputBinding("/registration"), ["valueDate"] = new InputBinding("/valueDate"),
             }, FailurePolicy.Stop),
             new WorkflowStep("crif", "risk.crif.generate", SimmContracts.Version, ["freeze"], new Dictionary<string, Binding>
             {
                 ["trades"] = new OutputBinding("freeze", "trades"), ["marketSnapshotId"] = new InputBinding("/marketSnapshotId"),
             }, FailurePolicy.Stop)],
            new Dictionary<string, Binding>
            {
                ["trades"] = new OutputBinding("freeze", "trades"), ["csas"] = new OutputBinding("freeze", "csas"),
                ["crif"] = new OutputBinding("crif", "crif"), ["coverage"] = new OutputBinding("crif", "coverage"),
            }));
    }

    /// <summary>Spot SIMM alone on a retained CRIF and the IM CSAs frozen with its trades (ADR-0012 §4); the table comes from the input.</summary>
    public static WorkflowDefinition Calculate(TypedDataRef crif, TypedDataRef csas) => new("workflow", CompositionValidator.SchemaVersion, CalculateWorkflow,
        SimmContracts.Version, "Calculate spot SIMM", SimmContracts.CalculateInput, SimmContracts.Version,
        [new WorkflowStep("simm", "margin.simm.calculate", SimmContracts.Version, [], new Dictionary<string, Binding>
        {
            ["crif"] = new DataReferenceBinding(crif), ["csas"] = new DataReferenceBinding(csas), ["parameters"] = new InputBinding("/parameters"),
        }, FailurePolicy.Stop)],
        new Dictionary<string, Binding>
        {
            ["im"] = new OutputBinding("simm", "im"), ["regimes"] = new OutputBinding("simm", "regimes"), ["trace"] = new OutputBinding("simm", "trace"),
        });
}
