namespace Qube.Samples.Desk;

/// <summary>
/// What one move of a sensitivity's input means (ADR-0019 §7). The result set's adapter declares it per kind; a kind
/// it does not declare has no unit, and nothing assumes one.
/// </summary>
public enum SensitivityPer
{
    /// <summary>Per 1.0 of a rate, so one basis point is 1e-4 of it.</summary>
    Rate,
    /// <summary>Per one unit of the input as it is quoted.</summary>
    Level,
}

/// <summary>
/// Where a result set came from (ADR-0019 §4): the engine and its commit, the input set it was computed from and the
/// valuation date. The scenario that is the book, the legal entity of each counterparty code, and the unit of each
/// sensitivity kind are the adapter's declarations about it.
/// </summary>
public sealed record DeskManifest(
    string Engine,
    string EngineCommit,
    string InputSet,
    DateOnly ValuationDate,
    string BaseScenario,
    IReadOnlyDictionary<string, string> LegalEntities,
    IReadOnlyDictionary<string, SensitivityPer> Units)
{
    /// <summary>
    /// The curve of the input set a hedge in a currency is valued on and has its risk on, by currency, as the adapter
    /// declares it. A currency it does not name has none, and a hedge in it is refused (ADR-0019 §7).
    /// </summary>
    public IReadOnlyDictionary<string, string> HedgeCurves { get; init; } = new Dictionary<string, string>();
}

/// <summary>A curve of the input set as the engine read it: discount factors at days from the valuation date, in order.</summary>
public sealed record DeskCurve(string Name, IReadOnlyList<DeskCurveNode> Nodes);

/// <param name="Key">The node as the engine's sensitivities name it (<c>1826d</c>).</param>
public sealed record DeskCurveNode(string Key, int Days, double DiscountFactor);

/// <summary>A counterparty of the results. A null legal entity is one the manifest's mapping does not hold: it is
/// shown as unmapped by its code, never given an entity (ADR-0019 §7).</summary>
public sealed record DeskCounterparty(string Code, string? LegalEntity);

/// <summary>One figure of the engine, with the engine's sign: a cost is negative (ADR-0019 §9).</summary>
public sealed record DeskValue(string Scenario, string Counterparty, string Metric, double Value, double StdError);

public sealed record DeskSensitivity(
    string Scenario, string Counterparty, string Metric, string Kind, string Input, string Key, double Sensitivity);

public sealed record DeskTrade(
    string Scenario, string Trade, string Counterparty, string NettingSet, double Pv, double PvStdError);

/// <summary>One result set as the engine wrote it. Every scenario's rows are here; the book is the base scenario's.</summary>
public sealed record DeskResultSet(
    string Reference,
    string ContentHash,
    DeskManifest Manifest,
    IReadOnlyList<DeskCounterparty> Counterparties,
    IReadOnlyList<DeskValue> Values,
    IReadOnlyList<DeskSensitivity> Sensitivities,
    IReadOnlyList<DeskTrade> Trades)
{
    /// <summary>The input set's curves the result set carries, by name; none where it carries none.</summary>
    public IReadOnlyDictionary<string, DeskCurve> Curves { get; init; } = new Dictionary<string, DeskCurve>();

    /// <summary>The declared unit of a kind, or null when the result set declares none.</summary>
    public SensitivityPer? UnitOf(string kind) =>
        Manifest.Units.TryGetValue(kind, out var per) ? per : null;
}

/// <summary>The results port (ADR-0019 §4): a result set by its reference, or a refusal that names what is wrong.</summary>
public interface IDeskResults
{
    DeskResultSet Read(string reference);
}

public enum DeskResultsRefusal
{
    NotConfigured,
    NotFound,
    Changed,
    FileMissing,
    ColumnMissing,
    RowUnreadable,
    NumberUnreadable,
    ManifestUnreadable,
    ManifestIncomplete,
    UnitUnknown,
}

/// <summary>A result set that cannot be read is refused whole: no row is skipped and no value is filled in.</summary>
public sealed class DeskResultsRefused(DeskResultsRefusal code, string message) : Exception(message)
{
    public DeskResultsRefusal Code { get; } = code;
}
