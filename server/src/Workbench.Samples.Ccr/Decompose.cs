using System.Globalization;

namespace Workbench.Samples.Ccr;

/// <summary>A swap leg: its currency, its notional in that currency, and whether its rate is fixed.</summary>
public sealed record SwapLeg(string Currency, double Notional, bool Fixed);

/// <summary>
/// The decomposition of a trade into the exposures SA-CCR reads (ADR-0013 §3). Times are years from the valuation date
/// on ACT/365 (CRE52.31); a start already past is today. Amounts are converted to the netting set's currency with
/// <c>toReporting</c>, the value of one unit of each currency in it.
/// </summary>
public static class Decompose
{
    public static double Years(DateOnly valuation, DateOnly date) => (date.DayNumber - valuation.DayNumber) / 365.0;

    static double Start(DateOnly valuation, DateOnly start) => Math.Max(0, Years(valuation, start));

    /// <summary>A fixed against floating swap: long the rate when it pays fixed (CRE52.39).</summary>
    public static SaCcrExposure InterestRateSwap(string tradeId, string currency, double notional, DateOnly valuation, DateOnly start, DateOnly end, bool payFixed)
    {
        var e = Years(valuation, end);
        return new SaCcrExposure(tradeId, "", AssetClass.InterestRate, currency, new LinearTerms(payFixed), e, notional,
            StartYears: Start(valuation, start), EndYears: e);
    }

    /// <summary>
    /// A floating against floating swap in one currency, in the basis hedging set of its two indices (CRE52.46). The pair
    /// is named in ordinal order, and the swap is long the spread of the second index over the first when it receives the
    /// second.
    /// </summary>
    public static SaCcrExposure BasisSwap(string tradeId, string currency, double notional, DateOnly valuation, DateOnly start, DateOnly end,
        string payIndex, string receiveIndex)
    {
        if (payIndex == receiveIndex)
            throw new SaCcrRefused($"{tradeId}: a basis swap pays and receives the same index, {payIndex}.");
        var (first, second) = string.CompareOrdinal(payIndex, receiveIndex) < 0 ? (payIndex, receiveIndex) : (receiveIndex, payIndex);
        var e = Years(valuation, end);
        return new SaCcrExposure(tradeId, "", AssetClass.InterestRate, $"{currency} {first}/{second}", new LinearTerms(receiveIndex == second), e, notional,
            StartYears: Start(valuation, start), EndYears: e, HedgingSetType: HedgingSetType.Basis);
    }

    /// <summary>
    /// A cross-currency swap: an interest rate exposure for each fixed leg, in its own currency (a floating leg resets and
    /// carries none), and one FX exposure on the pair (CRE52.35).
    /// </summary>
    public static IReadOnlyList<SaCcrExposure> CrossCurrencySwap(string tradeId, SwapLeg pay, SwapLeg receive, DateOnly valuation, DateOnly start, DateOnly end,
        string domesticCurrency, IReadOnlyDictionary<string, double> toReporting)
    {
        var s = Start(valuation, start);
        var e = Years(valuation, end);
        var exposures = new List<SaCcrExposure>();
        foreach (var (leg, paid) in new[] { (pay, true), (receive, false) })
        {
            if (!leg.Fixed) continue;
            exposures.Add(new SaCcrExposure(tradeId, $"{leg.Currency} fixed leg", AssetClass.InterestRate, leg.Currency, new LinearTerms(paid), e,
                leg.Notional * Rate(tradeId, leg.Currency, toReporting), StartYears: s, EndYears: e));
        }
        exposures.Add(Fx(tradeId, "FX", receive.Currency, receive.Notional, pay.Currency, pay.Notional, e, domesticCurrency, toReporting));
        return exposures;
    }

    /// <summary>An FX forward, or one leg of an FX swap: it buys one currency and sells the other at settlement.</summary>
    public static SaCcrExposure FxForward(string tradeId, string boughtCurrency, double boughtAmount, string soldCurrency, double soldAmount,
        DateOnly valuation, DateOnly settlement, string domesticCurrency, IReadOnlyDictionary<string, double> toReporting) =>
        Fx(tradeId, "FX", boughtCurrency, boughtAmount, soldCurrency, soldAmount, Years(valuation, settlement), domesticCurrency, toReporting);

    /// <summary>
    /// A European swaption on a swap from its exercise date to <c>swapEnd</c>. A payer is a call on the swap rate and a
    /// receiver a put (CRE52.40). Its maturity is the exercise date when cash-settled, else the swap's end (CRE52.32).
    /// </summary>
    public static SaCcrExposure Swaption(string tradeId, string currency, double notional, DateOnly valuation, DateOnly exercise, DateOnly swapEnd,
        bool payer, bool bought, double forwardRate, double strike, bool cashSettled, double shift = 0)
    {
        var t = Years(valuation, exercise);
        var e = Years(valuation, swapEnd);
        var option = new OptionTerms(payer ? OptionType.Call : OptionType.Put, bought, forwardRate, strike, t, shift);
        return new SaCcrExposure(tradeId, "", AssetClass.InterestRate, currency, option, cashSettled ? t : e, notional, StartYears: t, EndYears: e);
    }

    static double Rate(string tradeId, string currency, IReadOnlyDictionary<string, double> toReporting) =>
        toReporting.TryGetValue(currency, out var rate) && rate > 0
            ? rate
            : throw new SaCcrRefused($"{tradeId}: no FX rate to the netting set's currency for {currency}.");

    /// <summary>CRE52.35: the foreign leg (the one not in the bank's domestic currency) in the reporting currency, or with both legs foreign the larger; long the pair's first currency when receiving it.</summary>
    static SaCcrExposure Fx(string tradeId, string component, string receivedCurrency, double receivedAmount, string paidCurrency, double paidAmount,
        double maturity, string domesticCurrency, IReadOnlyDictionary<string, double> toReporting)
    {
        if (receivedCurrency == paidCurrency)
            throw new SaCcrRefused($"{tradeId}: both legs are in {receivedCurrency}, so it has no FX exposure.");
        var received = receivedAmount * Rate(tradeId, receivedCurrency, toReporting);
        var paid = paidAmount * Rate(tradeId, paidCurrency, toReporting);
        var notional = receivedCurrency == domesticCurrency ? paid
            : paidCurrency == domesticCurrency ? received
            : Math.Max(received, paid);
        var first = string.CompareOrdinal(receivedCurrency, paidCurrency) < 0 ? receivedCurrency : paidCurrency;
        var second = first == receivedCurrency ? paidCurrency : receivedCurrency;
        return new SaCcrExposure(tradeId, component, AssetClass.ForeignExchange, string.Create(CultureInfo.InvariantCulture, $"{first}{second}"),
            new LinearTerms(receivedCurrency == first), maturity, notional);
    }
}
