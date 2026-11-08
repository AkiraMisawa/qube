using Workbench.Samples.Ccr;

namespace Workbench.Composition.Tests;

/// <summary>
/// ADR-0013 §3: a trade is decomposed into the exposures SA-CCR reads, one leaf row each with its component. Times are
/// years from the valuation date on ACT/365; amounts are converted to the netting set's currency.
/// </summary>
public class CcrDecompositionTests
{
    static readonly DateOnly Today = new(2026, 11, 9);
    static readonly Dictionary<string, double> ToUsd = new() { ["USD"] = 1, ["EUR"] = 1.08, ["JPY"] = 0.0067 };

    static void Near(double expected, double actual) => Assert.Equal(expected, actual, 1e-12);

    [Fact] // ADR-0013 §3, CRE52.31, CRE52.39: a swap that pays fixed is long the rate; one that receives fixed is short
    public void An_interest_rate_swap_is_one_exposure_long_when_it_pays_fixed()
    {
        var pay = Decompose.InterestRateSwap("T-1", "USD", 10_000, Today, new(2026, 11, 11), new(2036, 11, 11), payFixed: true);
        Assert.Equal(AssetClass.InterestRate, pay.AssetClass);
        Assert.Equal("USD", pay.HedgingSet);
        Near(2 / 365.0, pay.StartYears!.Value);
        Near(3655 / 365.0, pay.EndYears!.Value);
        Near(3655 / 365.0, pay.MaturityYears);
        Assert.Equal(new LinearTerms(true), pay.Delta);

        var receive = Decompose.InterestRateSwap("T-2", "USD", 10_000, Today, new(2025, 1, 15), new(2030, 1, 15), payFixed: false);
        Assert.Equal(0, receive.StartYears);
        Assert.Equal(new LinearTerms(false), receive.Delta);
    }

    [Fact] // ADR-0013 §3, CRE52.46: a basis swap goes to the basis hedging set of its pair of indices
    public void A_basis_swap_goes_to_the_hedging_set_of_its_pair()
    {
        var swap = Decompose.BasisSwap("T-3", "USD", 10_000, Today, Today, new(2031, 11, 9), payIndex: "USD-SOFR", receiveIndex: "USD-TERM-3M");
        Assert.Equal(HedgingSetType.Basis, swap.HedgingSetType);
        Assert.Equal("USD USD-SOFR/USD-TERM-3M", swap.HedgingSet);
        Assert.Equal(new LinearTerms(true), swap.Delta);

        var other = Decompose.BasisSwap("T-4", "USD", 10_000, Today, Today, new(2031, 11, 9), payIndex: "USD-TERM-3M", receiveIndex: "USD-SOFR");
        Assert.Equal("USD USD-SOFR/USD-TERM-3M", other.HedgingSet);
        Assert.Equal(new LinearTerms(false), other.Delta);
    }

    [Fact] // ADR-0013 §3, CRE52.35: a cross-currency swap is its fixed legs' interest rate exposures and one FX exposure
    public void A_cross_currency_swap_is_its_fixed_legs_and_an_fx_exposure()
    {
        var exposures = Decompose.CrossCurrencySwap("T-5", pay: new SwapLeg("USD", 10_800, Fixed: false), receive: new SwapLeg("EUR", 10_000, Fixed: true),
            Today, Today, new(2031, 11, 9), "USD", ToUsd);
        Assert.Equal(["EUR fixed leg", "FX"], exposures.Select(e => e.Component));

        var leg = exposures[0];
        Assert.Equal(("EUR", AssetClass.InterestRate), (leg.HedgingSet, leg.AssetClass));
        Near(10_800, leg.Notional);
        Assert.Equal(new LinearTerms(false), leg.Delta);

        var fx = exposures[1];
        Assert.Equal(("EURUSD", AssetClass.ForeignExchange), (fx.HedgingSet, fx.AssetClass));
        Near(10_800, fx.Notional);
        Assert.Equal(new LinearTerms(true), fx.Delta);
        Assert.All(exposures, e => Assert.Equal("T-5", e.TradeId));
    }

    [Fact] // ADR-0013 §3, CRE52.35: with both legs foreign, the FX notional is the larger leg in the netting set's currency
    public void Both_legs_foreign_take_the_larger_leg()
    {
        var exposures = Decompose.CrossCurrencySwap("T-6", pay: new SwapLeg("JPY", 1_600_000, Fixed: true), receive: new SwapLeg("EUR", 10_000, Fixed: true),
            Today, Today, new(2031, 11, 9), "USD", ToUsd);
        Assert.Equal(["JPY fixed leg", "EUR fixed leg", "FX"], exposures.Select(e => e.Component));
        Assert.Equal(new LinearTerms(true), exposures[0].Delta);
        Near(1_600_000 * 0.0067, exposures[0].Notional);
        Assert.Equal("EURJPY", exposures[2].HedgingSet);
        Near(10_800, exposures[2].Notional);
    }

    [Fact] // ADR-0013 §3, CRE52.35: an FX forward is long the pair's first currency when it buys it
    public void An_fx_forward_is_one_fx_exposure()
    {
        var buy = Decompose.FxForward("T-7", "EUR", 3_000, "USD", 3_240, Today, new(2027, 5, 10), "USD", ToUsd);
        Assert.Equal(("EURUSD", "FX"), (buy.HedgingSet, buy.Component));
        Near(3_240, buy.Notional);
        Near(182 / 365.0, buy.MaturityYears);
        Assert.Equal(new LinearTerms(true), buy.Delta);

        var sellYen = Decompose.FxForward("T-8", "USD", 1_000, "JPY", 149_000, Today, new(2027, 5, 10), "USD", ToUsd);
        Assert.Equal("JPYUSD", sellYen.HedgingSet);
        Near(149_000 * 0.0067, sellYen.Notional);
        Assert.Equal(new LinearTerms(false), sellYen.Delta);

        Assert.Contains("GBP", Assert.Throws<SaCcrRefused>(() =>
            Decompose.FxForward("T-9", "GBP", 1_000, "USD", 1_300, Today, new(2027, 5, 10), "USD", ToUsd)).Message);
    }

    [Fact] // ADR-0013 §3, CRE52.32, CRE52.40: a swaption starts at its exercise; a receiver is a put on the rate
    public void A_swaption_is_an_option_on_its_underlying_swap()
    {
        var physical = Decompose.Swaption("T-10", "EUR", 5_000, Today, new(2027, 11, 9), new(2037, 11, 9), payer: false, bought: true,
            forwardRate: 0.06, strike: 0.05, cashSettled: false);
        Near(1, physical.StartYears!.Value);
        Near(4018 / 365.0, physical.EndYears!.Value);
        Near(4018 / 365.0, physical.MaturityYears);
        Assert.Equal(new OptionTerms(OptionType.Put, true, 0.06, 0.05, 1), physical.Delta);

        var cash = Decompose.Swaption("T-11", "EUR", 5_000, Today, new(2027, 11, 9), new(2037, 11, 9), payer: true, bought: false,
            forwardRate: 0.06, strike: 0.05, cashSettled: true);
        Near(1, cash.MaturityYears);
        Assert.Equal(new OptionTerms(OptionType.Call, false, 0.06, 0.05, 1), cash.Delta);
    }
}
