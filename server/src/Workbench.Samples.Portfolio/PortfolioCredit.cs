namespace Workbench.Samples.Portfolio;

// Synthetic reference data on the counterparties and the collateral their CSAs hold (ADR-0013 §3, §5, §6), as a
// company's reference data and collateral systems would give it. Every name, rating and amount here is made up.

/// <summary>A long-term issuer rating, as the ratings feed has it: the agency and its grade.</summary>
public sealed record SourceRating(string Agency, string Grade);

/// <summary>
/// What the reference data says about a registration, for a measure that weighs its credit (ADR-0013 §5): what it is
/// registered as (<c>bank</c>, <c>securitiesFirm</c>, <c>corporate</c>, …), its country of incorporation, its ratings
/// and its sovereign's, and the bank's own assessments of it. An attribute left null is not known, and a measure that
/// needs it refuses rather than assume it.
/// </summary>
public sealed record CounterpartyProfile(
    string RegistrationKey,
    string InstitutionType,
    string? Country,
    IReadOnlyList<SourceRating> Ratings,
    IReadOnlyList<SourceRating> SovereignRatings,
    bool? SupervisedAsBank = null,
    string? ScraGrade = null,
    int? DueDiligenceBucketsUp = null,
    bool? Sme = null,
    bool? InvestmentGrade = null);

/// <summary>
/// Collateral held (<c>received</c>) or posted under a CSA on the value date, in its currency: variation margin or an
/// independent amount, cash or a sovereign bond with its issuer's grade and residual maturity. Posted collateral in a
/// segregated, bankruptcy-remote account says so.
/// </summary>
public sealed record CollateralHolding(
    string Id, string CsaId, string Direction, string Purpose, string Type, string Currency, decimal Amount,
    bool Segregated = false, string? IssuerGrade = null, decimal? ResidualYears = null);

public static class PortfolioCredit
{
    // ABC Holdings is a rated bank with three ratings; ABC Securities a securities firm supervised as a bank, unrated,
    // so its SCRA grade applies; XYZ Industries an unrated corporate that is not an SME.
    public static readonly IReadOnlyList<CounterpartyProfile> Profiles =
    [
        new("ABC/LE-01", "bank", "JP", [new("Agency 1", "AA-"), new("Agency 2", "A1"), new("Agency 3", "A")], [new("Agency 1", "AA")],
            DueDiligenceBucketsUp: 0),
        new("ABC/LE-02", "securitiesFirm", "US", [], [new("Agency 1", "AA+")], SupervisedAsBank: true, ScraGrade: "A"),
        new("XYZ/LE-03", "corporate", "JP", [], [new("Agency 1", "AA")], Sme: false),
    ];

    public static readonly IReadOnlyList<CollateralHolding> Collateral =
    [
        new("COL-A1", "CSA-A", "received", "variationMargin", "cash", "JPY", 120_000_000m),
        new("COL-A2", "CSA-A", "posted", "variationMargin", "cash", "JPY", 30_000_000m),
        new("COL-B1", "CSA-B", "received", "variationMargin", "sovereignBond", "USD", 600_000m, IssuerGrade: "AA", ResidualYears: 3m),
        new("COL-C1", "CSA-C", "posted", "variationMargin", "cash", "USD", 300_000m),
        new("COL-C2", "CSA-C", "received", "independentAmount", "cash", "USD", 500_000m),
        new("COL-C3", "CSA-C", "posted", "independentAmount", "sovereignBond", "USD", 400_000m, Segregated: true, IssuerGrade: "AA+", ResidualYears: 7m),
    ];

    /// <summary>A registration's reference data, or null where the reference data has none.</summary>
    public static CounterpartyProfile? ProfileOf(string registrationKey) => Profiles.SingleOrDefault(p => p.RegistrationKey == registrationKey);

    public static IReadOnlyList<CollateralHolding> CollateralOf(string csaId) => [.. Collateral.Where(c => c.CsaId == csaId)];
}
