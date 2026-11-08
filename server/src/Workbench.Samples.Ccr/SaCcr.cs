using System.Globalization;
using Workbench.Composition.Application.Traces;

namespace Workbench.Samples.Ccr;

public enum AssetClass { InterestRate, ForeignExchange, Credit, Equity, Commodity }

/// <summary>CRE52.46-47: a basis hedging set takes half the supervisory factor, a volatility hedging set five times it.</summary>
public enum HedgingSetType { Regular, Basis, Volatility }

public enum OptionType { Call, Put }

/// <summary>Table 2's credit rows: a single name by its rating, an index by investment or speculative grade.</summary>
public enum CreditSubclass { AAA, AA, A, BBB, BB, B, CCC, IndexInvestmentGrade, IndexSpeculativeGrade }

/// <summary>What the supervisory delta reads (CRE52.38-41).</summary>
public abstract record DeltaTerms;

/// <summary>Neither an option nor a CDO tranche: +1 long in the primary risk factor, −1 short (CRE52.39).</summary>
public sealed record LinearTerms(bool Long) : DeltaTerms;

/// <summary>
/// An option (CRE52.40): the underlying price, the strike, the years to the latest exercise date, and the shift λ that
/// makes a negative rate or price positive (CRE52.40 FAQ2).
/// </summary>
public sealed record OptionTerms(OptionType Type, bool Bought, double Underlying, double Strike, double ExerciseYears, double Shift = 0) : DeltaTerms;

/// <summary>A CDO tranche (CRE52.41), by its attachment and detachment points.</summary>
public sealed record TrancheTerms(double Attachment, double Detachment, bool ProtectionBought) : DeltaTerms;

/// <summary>
/// One exposure of a trade, as SA-CCR reads it: a trade is decomposed into one or more (ADR-0013 §3). The amounts are in
/// the netting set's currency. <c>Notional</c> is the trade notional for interest rate and credit, the foreign leg for
/// FX (CRE52.35), and the price of one unit for equity and commodity, multiplied by <c>Units</c> (CRE52.36). Times are
/// in years from the valuation date (CRE52.31). <c>RiskFactor</c> is the entity or index for credit and equity and the
/// commodity type for commodity; the regular commodity hedging sets are energy, metals, agricultural and other.
/// </summary>
public sealed record SaCcrExposure(
    string TradeId,
    string Component,
    AssetClass AssetClass,
    string HedgingSet,
    DeltaTerms Delta,
    double MaturityYears,
    double Notional,
    double Units = 1,
    double? StartYears = null,
    double? EndYears = null,
    string? RiskFactor = null,
    HedgingSetType HedgingSetType = HedgingSetType.Regular,
    CreditSubclass? CreditSubclass = null,
    bool Index = false,
    bool Electricity = false);

/// <summary>A trade: its market value (V's row), where that value came from, and its exposures.</summary>
public sealed record SaCcrTrade(string TradeId, double MarketValue, IReadOnlyList<SaCcrExposure> Exposures, string ValueSource = "provider");

/// <summary>
/// The CSA's margining terms the netting set uses: the remargining period in business days (1 for daily), the
/// counterparty's threshold and minimum transfer amount, and what CRE52.51 reads.
/// </summary>
public sealed record Margining(
    string CsaId,
    int RemarginingPeriodDays,
    double Threshold,
    double MinimumTransferAmount,
    bool IlliquidCollateralOrHardToReplace = false,
    int DisputesLongerThanMpor = 0);

public enum CollateralDirection { Received, Posted }

public enum CollateralPurpose { VariationMargin, IndependentAmount }

/// <summary>
/// A collateral balance, in the netting set's currency. <c>SupervisoryHaircut</c> is the asset's haircut at ten business
/// days (CRE22), 0 for cash; the calculator scales it to the period its basis uses and adds the currency mismatch.
/// Collateral the bank posted to a segregated, bankruptcy-remote account is not counted.
/// </summary>
public sealed record CollateralBalance(
    string Id,
    CollateralDirection Direction,
    CollateralPurpose Purpose,
    string Type,
    string Currency,
    double Amount,
    double SupervisoryHaircut,
    bool Segregated = false);

/// <summary>A netting set: unmargined when it has no margining terms.</summary>
public sealed record SaCcrNettingSet(
    string Id,
    string Currency,
    IReadOnlyList<SaCcrTrade> Trades,
    Margining? Margin = null,
    IReadOnlyList<CollateralBalance>? Collateral = null,
    bool LegallyEnforceable = true);

/// <summary>The bank's choices SA-CCR leaves open, as configuration with a version.</summary>
public sealed record SaCcrSettings(string Version, bool IrOffsetAcrossBuckets = true);

/// <summary>One exposure's terms: <c>D = δ · d · MF</c> (CRE52.30), with what each read; <c>D1</c> is an option's d₁.</summary>
public sealed record ExposureRow(
    SaCcrExposure Exposure,
    double AdjustedNotional,
    double? SupervisoryDuration,
    double Delta,
    string DeltaBecause,
    double MaturityFactor,
    double EffectiveNotional,
    double SupervisoryFactor,
    double? OptionVolatility,
    int? Bucket,
    double? D1 = null);

/// <summary>An interest rate hedging set's maturity bucket (CRE52.57): 1 under one year, 2 one to five, 3 over five.</summary>
public sealed record MaturityBucket(int Bucket, double EffectiveNotional, IReadOnlyList<ExposureRow> Rows);

/// <summary>An entity or index (credit, equity) or a commodity type, with its own add-on.</summary>
public sealed record RiskFactorAddOn(string Key, double EffectiveNotional, double SupervisoryFactor, double Correlation, double AddOn, IReadOnlyList<ExposureRow> Rows);

/// <summary>
/// A hedging set's add-on. Interest rate and FX have a supervisory factor and an effective notional (with buckets for
/// interest rate); credit, equity and commodity aggregate their risk factors by the single factor.
/// </summary>
public sealed record HedgingSetAddOn(
    AssetClass AssetClass,
    string Key,
    HedgingSetType Type,
    double AddOn,
    double? SupervisoryFactor,
    double? EffectiveNotional,
    IReadOnlyList<MaturityBucket> Buckets,
    IReadOnlyList<RiskFactorAddOn> RiskFactors,
    IReadOnlyList<ExposureRow> Rows);

public sealed record AssetClassAddOn(AssetClass AssetClass, double AddOn, IReadOnlyList<HedgingSetAddOn> HedgingSets);

/// <summary>
/// A collateral balance as one basis counts it: the haircuts it took, scaled from ten business days by <c>Scale</c>,
/// and its signed value.
/// </summary>
public sealed record CollateralRow(CollateralBalance Balance, double Scale, double Haircut, double CurrencyHaircut, double Value, bool InCollateral, bool InNica);

/// <summary>
/// The EAD on one basis, margined or unmargined. <c>Nica</c> and <c>ThresholdAndMta</c> are given on the margined
/// basis only, where the replacement cost reads them.
/// </summary>
public sealed record ExposureBasis(
    bool Margined,
    double Ead,
    double ReplacementCost,
    double MarketValue,
    double Collateral,
    double? Nica,
    double? ThresholdAndMta,
    double Pfe,
    double Multiplier,
    double AddOn,
    IReadOnlyList<AssetClassAddOn> AssetClasses,
    IReadOnlyList<ExposureRow> Rows,
    IReadOnlyList<CollateralRow> CollateralRows);

/// <summary>The margin period of risk, in business days, and how it was derived (CRE52.50-51).</summary>
public sealed record MarginPeriod(int Days, IReadOnlyList<string> Because);

/// <summary>
/// A netting set's EAD: the unmargined figure, and for a margined netting set the margined one, which the unmargined
/// one caps (CRE52.2). <c>Set</c> is the netting set as calculated: one trade's own, when the netting set it came from
/// is not legally enforceable.
/// </summary>
public sealed record NettingSetExposure(
    string Id,
    SaCcrNettingSet Set,
    double Ead,
    ExposureBasis Unmargined,
    ExposureBasis? Margined = null,
    MarginPeriod? Mpor = null,
    string? Note = null)
{
    public bool MarginedGoverns => Margined is { } margined && margined.Ead <= Unmargined.Ead;
}

/// <summary>The counterparty's EAD, the sum over its netting sets (CRE52.1), and the settings it was calculated with.</summary>
public sealed record SaCcrResult(double Ead, IReadOnlyList<NettingSetExposure> NettingSets, SaCcrSettings Settings);

/// <summary>SA-CCR cannot read a trade or a netting set, and says which (ADR-0013 §6: the measure is refused by name).</summary>
public sealed class SaCcrRefused(string message) : Exception(message);

/// <summary>
/// SA-CCR (Basel Framework CRE52 as in force from 2023-01-01), the reference implementation of ADR-0013 §3. Every
/// figure is kept, from the netting set's EAD down to each exposure's effective notional, so that the trace can write
/// it term by term.
/// </summary>
public static class SaCcr
{
    /// <summary>CRE52.1.</summary>
    public const double Alpha = 1.4;

    /// <summary>The multiplier's floor, CRE52.23.</summary>
    public const double Floor = 0.05;

    /// <summary>CRE52.49.</summary>
    public const double BusinessDaysPerYear = 250;

    const double TenBusinessDays = 10 / BusinessDaysPerYear;

    /// <summary>The currency mismatch haircut at ten business days (CRE22).</summary>
    public const double CurrencyMismatchHaircut = 0.08;

    public static SaCcrResult Calculate(IReadOnlyList<SaCcrNettingSet> nettingSets, SaCcrSettings settings)
    {
        var results = new List<NettingSetExposure>();
        foreach (var set in nettingSets)
        {
            if (set.LegallyEnforceable)
            {
                results.Add(Calculate(set, settings));
                continue;
            }
            var note = $"The netting set {set.Id} is not legally enforceable, so each trade is its own netting set"
                + ((set.Collateral ?? []).Count > 0 ? ", and its collateral is not recognised." : ".");
            foreach (var trade in set.Trades)
            {
                var alone = new SaCcrNettingSet($"{set.Id} · {trade.TradeId}", set.Currency, [trade]);
                results.Add(Calculate(alone, settings) with { Note = note });
            }
        }
        return new SaCcrResult(results.Sum(r => r.Ead), results, settings);
    }

    /// <summary>One legally enforceable netting set.</summary>
    public static NettingSetExposure Calculate(SaCcrNettingSet set, SaCcrSettings settings)
    {
        if (!set.LegallyEnforceable)
            throw new ArgumentException($"The netting set {set.Id} is not legally enforceable; calculate it with the list overload, which splits it.", nameof(set));
        foreach (var trade in set.Trades)
            foreach (var exposure in trade.Exposures.Where(e => e.TradeId != trade.TradeId))
                throw new SaCcrRefused($"{trade.TradeId}: it carries an exposure of {exposure.TradeId}.");

        var longest = set.Trades.SelectMany(t => t.Exposures).Select(e => e.MaturityYears).DefaultIfEmpty(0).Max();
        var unmargined = Basis(set, settings, mpor: null, longest);
        if (set.Margin is not { } margin)
            return new NettingSetExposure(set.Id, set, unmargined.Ead, unmargined);

        var mpor = Mpor(set, margin);
        var margined = Basis(set, settings, mpor.Days, longest);
        return new NettingSetExposure(set.Id, set, Math.Min(margined.Ead, unmargined.Ead), unmargined, margined, mpor);
    }

    /// <summary>CRE52.50-51.</summary>
    static MarginPeriod Mpor(SaCcrNettingSet set, Margining margin)
    {
        if (margin.RemarginingPeriodDays < 1)
            throw new SaCcrRefused($"{set.Id}: the remargining period of {margin.CsaId} is {margin.RemarginingPeriodDays} business days; it must be 1 or more.");
        var because = new List<string>();
        var days = 9 + margin.RemarginingPeriodDays;
        because.Add(margin.RemarginingPeriodDays == 1
            ? $"{margin.CsaId} margins daily: 10 business days (CRE52.50)."
            : $"{margin.CsaId} remargins every {margin.RemarginingPeriodDays} business days: 9 + {margin.RemarginingPeriodDays} = {days} business days (CRE52.50).");
        if (set.Trades.Count > 5_000)
        {
            days = Math.Max(days, 20);
            because.Add($"The netting set has {set.Trades.Count.ToString("N0", CultureInfo.InvariantCulture)} trades, more than 5,000: at least 20 business days (CRE52.51).");
        }
        if (margin.IlliquidCollateralOrHardToReplace)
        {
            days = Math.Max(days, 20);
            because.Add("It holds illiquid collateral or a derivative that cannot easily be replaced: at least 20 business days (CRE52.51).");
        }
        if (margin.DisputesLongerThanMpor > 2)
        {
            days *= 2;
            because.Add($"{margin.DisputesLongerThanMpor} margin call disputes longer than the MPOR in the last two quarters: doubled, to {days} business days (CRE52.51).");
        }
        else
        {
            because.Add($"{margin.DisputesLongerThanMpor} margin call disputes longer than the MPOR in the last two quarters, not more than two: not doubled.");
        }
        return new MarginPeriod(days, because);
    }

    static ExposureBasis Basis(SaCcrNettingSet set, SaCcrSettings settings, int? mpor, double longestMaturity)
    {
        var rows = set.Trades.SelectMany(t => t.Exposures).Select(e => Row(e, mpor)).ToList();
        var classes = AssetClasses(rows, settings);
        var addOn = classes.Sum(c => c.AddOn);
        var value = set.Trades.Sum(t => t.MarketValue);
        var collateralRows = (set.Collateral ?? []).Select(c => Collateral(set, c, mpor, longestMaturity)).ToList();
        var collateral = collateralRows.Where(r => r.InCollateral).Sum(r => r.Value);

        double replacementCost;
        double? nica = null, thresholdAndMta = null;
        if (mpor is null)
        {
            replacementCost = Math.Max(value - collateral, 0); // CRE52.10
        }
        else
        {
            var margin = set.Margin!;
            nica = collateralRows.Where(r => r.InNica).Sum(r => r.Value); // CRE52.17
            thresholdAndMta = margin.Threshold + margin.MinimumTransferAmount;
            replacementCost = Math.Max(Math.Max(value - collateral, thresholdAndMta.Value - nica.Value), 0); // CRE52.18-19
        }

        // CRE52.23. With no add-on the potential future exposure is nil whatever the multiplier, which is then 1.
        var multiplier = addOn > 0
            ? Math.Min(1, Floor + (1 - Floor) * Math.Exp((value - collateral) / (2 * (1 - Floor) * addOn)))
            : 1;
        var pfe = multiplier * addOn;
        var ead = Alpha * (replacementCost + pfe);
        return new ExposureBasis(mpor is not null, ead, replacementCost, value, collateral, nica, thresholdAndMta, pfe, multiplier, addOn, classes, rows, collateralRows);
    }

    /// <summary>CRE52.5, CRE52.11, CRE22.59: the haircut over the MPOR, or unmargined over the longest trade's maturity up to a year.</summary>
    static CollateralRow Collateral(SaCcrNettingSet set, CollateralBalance balance, int? mpor, double longestMaturity)
    {
        if (balance.Amount < 0)
            throw new SaCcrRefused($"{set.Id}: the collateral balance {balance.Id} is negative; its direction says which way it went.");
        if (balance.SupervisoryHaircut is < 0 or > 1)
            throw new SaCcrRefused($"{set.Id}: the collateral balance {balance.Id} has a haircut of {balance.SupervisoryHaircut}, outside 0 to 1.");
        var scale = mpor is { } days
            ? Math.Sqrt(days / 10.0)
            : Math.Sqrt((Math.Min(BusinessDaysPerYear, longestMaturity * BusinessDaysPerYear) + 9) / 10.0);
        var haircut = Math.Min(1, balance.SupervisoryHaircut * scale);
        var currencyHaircut = balance.Currency == set.Currency ? 0 : Math.Min(1, CurrencyMismatchHaircut * scale);
        var independent = balance.Purpose == CollateralPurpose.IndependentAmount;
        if (balance.Direction == CollateralDirection.Received)
            return new CollateralRow(balance, scale, haircut, currencyHaircut, balance.Amount * Math.Max(0, 1 - haircut - currencyHaircut), true, independent);
        var posted = -balance.Amount * (1 + haircut + currencyHaircut);
        return balance.Segregated
            ? new CollateralRow(balance, scale, haircut, currencyHaircut, posted, false, false)
            : new CollateralRow(balance, scale, haircut, currencyHaircut, posted, true, independent);
    }

    // Trade level: the adjusted notional, the supervisory delta and the maturity factor.

    static ExposureRow Row(SaCcrExposure e, int? mpor)
    {
        if (e.Notional < 0)
            throw new SaCcrRefused($"{e.TradeId}: a negative notional ({e.Notional.ToString(CultureInfo.InvariantCulture)}); the direction is the delta's.");
        if (e.Units < 0)
            throw new SaCcrRefused($"{e.TradeId}: a negative number of units; the direction is the delta's.");
        if (e.MaturityYears < 0)
            throw new SaCcrRefused($"{e.TradeId}: a negative maturity.");

        var (factor, _, volatility) = Table2(e);
        var scale = e.HedgingSetType switch { HedgingSetType.Basis => 0.5, HedgingSetType.Volatility => 5.0, _ => 1.0 };

        double adjusted;
        double? duration = null;
        int? bucket = null;
        switch (e.AssetClass)
        {
            case AssetClass.InterestRate or AssetClass.Credit:
                if (e.StartYears is not { } start || e.EndYears is not { } end)
                    throw new SaCcrRefused($"{e.TradeId}: an {Name(e.AssetClass)} exposure needs its start and end (CRE52.31, CRE52.34).");
                if (start < 0 || end < start)
                    throw new SaCcrRefused($"{e.TradeId}: the start {start} and end {end} are not a period from today (CRE52.31).");
                duration = Math.Max((Math.Exp(-0.05 * start) - Math.Exp(-0.05 * end)) / 0.05, TenBusinessDays); // CRE52.34
                adjusted = e.Notional * duration.Value;
                if (e.AssetClass == AssetClass.InterestRate)
                    bucket = end < 1 ? 1 : end <= 5 ? 2 : 3; // CRE52.57
                break;
            case AssetClass.ForeignExchange:
                adjusted = e.Notional; // CRE52.35
                break;
            default:
                if (e.RiskFactor is null)
                    throw new SaCcrRefused($"{e.TradeId}: an {Name(e.AssetClass)} exposure needs its {(e.AssetClass == AssetClass.Commodity ? "commodity type" : "entity or index")}.");
                adjusted = e.Notional * e.Units; // CRE52.36
                break;
        }
        if (e.AssetClass == AssetClass.Credit && e.RiskFactor is null)
            throw new SaCcrRefused($"{e.TradeId}: a credit exposure needs its reference entity or index.");
        if (e.AssetClass == AssetClass.Commodity && e.HedgingSetType == HedgingSetType.Regular && !CommoditySets.Contains(e.HedgingSet))
            throw new SaCcrRefused($"{e.TradeId}: the commodity hedging set {e.HedgingSet} is not energy, metals, agricultural or other (CRE52.70).");

        var (delta, because, d1) = Delta(e, volatility);
        var maturityFactor = mpor is { } days
            ? 1.5 * Math.Sqrt(days / BusinessDaysPerYear) // CRE52.52
            : Math.Sqrt(Math.Min(Math.Max(e.MaturityYears, TenBusinessDays), 1)); // CRE52.48
        return new ExposureRow(e, adjusted, duration, delta, because, maturityFactor, delta * adjusted * maturityFactor,
            factor * scale, e.Delta is OptionTerms ? volatility : null, bucket, d1);
    }

    static readonly string[] CommoditySets = ["energy", "metals", "agricultural", "other"];

    static string Name(AssetClass c) => c switch
    {
        AssetClass.InterestRate => "interest rate",
        AssetClass.ForeignExchange => "FX",
        AssetClass.Credit => "credit",
        AssetClass.Equity => "equity",
        _ => "commodity",
    };

    /// <summary>CRE52.72, Table 2: the supervisory factor, the correlation and the option volatility.</summary>
    static (double Factor, double Correlation, double Volatility) Table2(SaCcrExposure e) => e.AssetClass switch
    {
        AssetClass.InterestRate => (0.005, double.NaN, 0.50),
        AssetClass.ForeignExchange => (0.04, double.NaN, 0.15),
        AssetClass.Credit => e.CreditSubclass switch
        {
            CreditSubclass.AAA or CreditSubclass.AA => (0.0038, 0.5, 1.0),
            CreditSubclass.A => (0.0042, 0.5, 1.0),
            CreditSubclass.BBB => (0.0054, 0.5, 1.0),
            CreditSubclass.BB => (0.0106, 0.5, 1.0),
            CreditSubclass.B => (0.016, 0.5, 1.0),
            CreditSubclass.CCC => (0.06, 0.5, 1.0),
            CreditSubclass.IndexInvestmentGrade => (0.0038, 0.8, 0.8),
            CreditSubclass.IndexSpeculativeGrade => (0.0106, 0.8, 0.8),
            _ => throw new SaCcrRefused($"{e.TradeId}: a credit exposure needs its rating or index grade for Table 2 (CRE52.72)."),
        },
        AssetClass.Equity => e.Index ? (0.20, 0.8, 0.75) : (0.32, 0.5, 1.20),
        _ => e.Electricity ? (0.40, 0.4, 1.50) : (0.18, 0.4, 0.70),
    };

    /// <summary>CRE52.39-41.</summary>
    static (double Delta, string Because, double? D1) Delta(SaCcrExposure e, double volatility)
    {
        switch (e.Delta)
        {
            case LinearTerms linear:
                return linear.Long ? (1, "Long in the primary risk factor (CRE52.39).", null) : (-1, "Short in the primary risk factor (CRE52.39).", null);
            case OptionTerms o:
                var price = o.Underlying + o.Shift;
                var strike = o.Strike + o.Shift;
                if (price <= 0 || strike <= 0)
                    throw new SaCcrRefused($"{e.TradeId}: the option's price and strike, shifted by {o.Shift.ToString(CultureInfo.InvariantCulture)}, must both be positive (CRE52.40).");
                if (o.ExerciseYears <= 0)
                    throw new SaCcrRefused($"{e.TradeId}: the option's latest exercise date must be after today (CRE52.40).");
                var d1 = (Math.Log(price / strike) + 0.5 * volatility * volatility * o.ExerciseYears) / (volatility * Math.Sqrt(o.ExerciseYears));
                var delta = (o.Type, o.Bought) switch
                {
                    (OptionType.Call, true) => NormalDistribution.Cdf(d1),
                    (OptionType.Call, false) => -NormalDistribution.Cdf(d1),
                    (OptionType.Put, true) => -NormalDistribution.Cdf(-d1),
                    _ => NormalDistribution.Cdf(-d1),
                };
                var side = o.Bought ? "bought" : "sold";
                return (delta, $"A {side} {o.Type.ToString().ToLowerInvariant()}, with the supervisory volatility of its class (CRE52.40).", d1);
            case TrancheTerms t:
                if (e.AssetClass != AssetClass.Credit)
                    throw new SaCcrRefused($"{e.TradeId}: a CDO tranche is a credit exposure.");
                if (t.Attachment < 0 || t.Detachment > 1 || t.Attachment >= t.Detachment)
                    throw new SaCcrRefused($"{e.TradeId}: the tranche's attachment {t.Attachment} and detachment {t.Detachment} are not 0 ≤ A < D ≤ 1 (CRE52.41).");
                var size = 15 / ((1 + 14 * t.Attachment) * (1 + 14 * t.Detachment));
                return t.ProtectionBought
                    ? (size, "A tranche with protection bought (CRE52.41).", null)
                    : (-size, "A tranche with protection sold (CRE52.41).", null);
            default:
                throw new SaCcrRefused($"{e.TradeId}: no supervisory delta for {e.Delta.GetType().Name}.");
        }
    }

    // Aggregation: hedging sets and asset classes.

    static List<AssetClassAddOn> AssetClasses(List<ExposureRow> rows, SaCcrSettings settings) =>
        Enum.GetValues<AssetClass>()
            .Select(c => (Class: c, Rows: rows.Where(r => r.Exposure.AssetClass == c).ToList()))
            .Where(c => c.Rows.Count > 0)
            .Select(c =>
            {
                var sets = c.Rows
                    .GroupBy(r => (r.Exposure.HedgingSet, r.Exposure.HedgingSetType))
                    .Select(g => HedgingSet(c.Class, g.Key.HedgingSet, g.Key.HedgingSetType, g.ToList(), settings))
                    .ToList();
                return new AssetClassAddOn(c.Class, sets.Sum(s => s.AddOn), sets);
            })
            .ToList();

    static HedgingSetAddOn HedgingSet(AssetClass assetClass, string key, HedgingSetType type, List<ExposureRow> rows, SaCcrSettings settings)
    {
        var factor = rows[0].SupervisoryFactor;
        switch (assetClass)
        {
            case AssetClass.InterestRate:
            {
                var buckets = rows.GroupBy(r => r.Bucket!.Value).OrderBy(g => g.Key)
                    .Select(g => new MaturityBucket(g.Key, g.Sum(r => r.EffectiveNotional), g.ToList())).ToList();
                double D(int b) => buckets.FirstOrDefault(x => x.Bucket == b)?.EffectiveNotional ?? 0; // an empty bucket holds no trade
                var (d1, d2, d3) = (D(1), D(2), D(3));
                var notional = settings.IrOffsetAcrossBuckets
                    ? Math.Sqrt(d1 * d1 + d2 * d2 + d3 * d3 + 1.4 * d1 * d2 + 1.4 * d2 * d3 + 0.6 * d1 * d3)
                    : Math.Abs(d1) + Math.Abs(d2) + Math.Abs(d3);
                return new HedgingSetAddOn(assetClass, key, type, factor * notional, factor, notional, buckets, [], rows);
            }
            case AssetClass.ForeignExchange:
            {
                var notional = rows.Sum(r => r.EffectiveNotional);
                return new HedgingSetAddOn(assetClass, key, type, factor * Math.Abs(notional), factor, notional, [], [], rows);
            }
            default:
            {
                var factors = rows.GroupBy(r => r.Exposure.RiskFactor!).Select(g => RiskFactor(assetClass, g.Key, g.ToList())).ToList();
                var systematic = factors.Sum(f => f.Correlation * f.AddOn);
                var idiosyncratic = factors.Sum(f => (1 - f.Correlation * f.Correlation) * f.AddOn * f.AddOn);
                return new HedgingSetAddOn(assetClass, key, type, Math.Sqrt(systematic * systematic + idiosyncratic), null, null, [], factors, rows);
            }
        }
    }

    static RiskFactorAddOn RiskFactor(AssetClass assetClass, string key, List<ExposureRow> rows)
    {
        var parameters = rows.Select(r => (r.SupervisoryFactor, Table2(r.Exposure).Correlation)).Distinct().ToList();
        if (parameters.Count > 1)
            throw new SaCcrRefused($"The {Name(assetClass)} risk factor {key} has trades with different supervisory factors ({string.Join(", ", rows.Select(r => r.Exposure.TradeId))}): one entity, index or commodity type takes one row of Table 2.");
        var (factor, correlation) = parameters[0];
        var notional = rows.Sum(r => r.EffectiveNotional);
        return new RiskFactorAddOn(key, notional, factor, correlation, factor * notional, rows);
    }
}
