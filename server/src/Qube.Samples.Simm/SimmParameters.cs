namespace Qube.Samples.Simm;

/// <summary>
/// The parameters of one SIMM methodology version: risk weights, correlations and concentration thresholds
/// (ADR-0012 §5). <see cref="Demo"/> is made up and says so in its version; ISDA's calibration is licensed, is loaded
/// from a file kept outside the repository, and is never committed.
/// </summary>
public sealed record SimmParameters(
    string Version,
    bool IsDemo,
    IReadOnlyList<string> Tenors,
    IReadOnlyDictionary<string, string> VolatilityGroups,
    IReadOnlyDictionary<string, IReadOnlyList<double>> IrRiskWeights,
    IReadOnlyDictionary<string, double> IrConcentrationThresholds,
    IReadOnlyList<IReadOnlyList<double>> IrTenorCorrelations,
    double XCcyBasisRiskWeight,
    double XCcyBasisCorrelation,
    double SubCurveCorrelation,
    double IrInterBucketCorrelation,
    double FxRiskWeight,
    double FxCorrelation,
    double FxConcentrationThreshold,
    IReadOnlyDictionary<(string, string), double> RiskClassCorrelations)
{
    /// <summary>Years of each SIMM tenor, for placing a curve pillar between two of them.</summary>
    public static readonly IReadOnlyDictionary<string, double> TenorYears = new Dictionary<string, double>
    {
        ["2w"] = 14 / 365.0, ["1m"] = 1 / 12.0, ["3m"] = 0.25, ["6m"] = 0.5, ["1y"] = 1, ["2y"] = 2,
        ["3y"] = 3, ["5y"] = 5, ["10y"] = 10, ["15y"] = 15, ["20y"] = 20, ["30y"] = 30,
    };

    /// <summary>
    /// The DEMO table: the SIMM v2.x structure with made-up numbers, the same as the drill-down sample's. Every
    /// figure computed with it says <c>SIMM PARAMETERS DEMO</c>.
    /// </summary>
    public static SimmParameters Demo { get; } = new(
        "DEMO-2.x",
        true,
        ["2w", "1m", "3m", "6m", "1y", "2y", "3y", "5y", "10y", "15y", "20y", "30y"],
        new Dictionary<string, string> { ["USD"] = "regular", ["EUR"] = "regular", ["JPY"] = "low" },
        new Dictionary<string, IReadOnlyList<double>>
        {
            ["regular"] = [80, 78, 72, 64, 60, 56, 54, 50, 48, 48, 50, 54],
            ["low"] = [20, 18, 12, 11, 13, 17, 19, 21, 23, 23, 22, 24],
        },
        new Dictionary<string, double> { ["regular"] = 250e6, ["low"] = 120e6 },
        DemoTenorCorrelations(["2w", "1m", "3m", "6m", "1y", "2y", "3y", "5y", "10y", "15y", "20y", "30y"]),
        XCcyBasisRiskWeight: 20,
        XCcyBasisCorrelation: 0.25,
        SubCurveCorrelation: 0.95,
        IrInterBucketCorrelation: 0.30,
        FxRiskWeight: 8,
        FxCorrelation: 0.50,
        FxConcentrationThreshold: 5000e6,
        new Dictionary<(string, string), double> { [("IR", "FX")] = 0.25 });

    /// <summary>The DEMO table's tenor correlations, made up as the drill-down sample's are: max(0.30, e^(−0.35 |ln(t_k / t_l)|)).</summary>
    static IReadOnlyList<IReadOnlyList<double>> DemoTenorCorrelations(IReadOnlyList<string> tenors) =>
        [.. tenors.Select(k => (IReadOnlyList<double>)[.. tenors.Select(l => k == l ? 1 : Math.Max(0.30, Math.Exp(-0.35 * Math.Abs(Math.Log(TenorYears[k] / TenorYears[l])))))])];
}
