using Workbench.Samples.Rates;
using Workbench.Samples.Simm;

namespace Workbench.Composition.Tests;

// CRIF from the Rates pricing at the value date (ADR-0012 §5), at its seam: trades, a market and a value date in, CRIF
// lines out. The pricing provider is the system boundary, so a scripted one answers here with figures chosen by hand.
public class CrifGenerationTests
{
    static readonly DateOnly ValueDate = new(2026, 11, 6);

    static ConventionProfile Profile(string ccy, string index, bool overnight) =>
        new($"test.{ccy}.{index}", "1", ccy, index, DayCountBasis.Act360, "weekends-only", BusinessDayAdjustment.ModifiedFollowing,
            2, 2, StubRule.ShortFront, false, overnight);

    static EffectiveLeg Leg(string ccy, string index, bool floating, double rate, bool overnight = true, double notional = 1e9,
        IReadOnlyList<NotionalExchange>? exchanges = null) =>
        new(new LegTerms(ccy, index, notional, new LegQuote(floating ? QuoteKind.FloatSpreadPercent : QuoteKind.FixedRatePercent, rate),
                Tenor.Parse("12m"), floating ? Tenor.Parse(overnight ? "12m" : "6m") : null, floating),
            Profile(ccy, index, overnight),
            [new EffectivePeriod("p1", ValueDate, ValueDate.AddYears(1), ValueDate.AddYears(1), notional, rate, 1.0, [])],
            exchanges ?? []);

    static PricingLine JpyIrs(string id) => new(id, 1, Direction.Pay, [Leg("JPY", "FIXED", false, 1.0), Leg("JPY", "TONA", true, 0)], null, true);

    static PricingLine JpyUsdCcs(string id) => new(id, 1, Direction.Receive,
        [Leg("JPY", "TONA", true, -0.25, exchanges: [new(ExchangeKind.Final, ValueDate.AddYears(5), 7.5e9)]),
         Leg("USD", "SOFR", true, 0, notional: 50e6, exchanges: [new(ExchangeKind.Final, ValueDate.AddYears(5), 50e6)])], null, true);

    static SimmTrade Trade(string id, PricingLine pricing) => new(id, pricing, null, ["JFSA", "CFTC"], ["JFSA"]);

    static readonly MarketSnapshot Market = new("test:simm:1", 1, new DateTimeOffset(2026, 11, 6, 0, 0, 0, TimeSpan.Zero), "synthetic", 1, "test",
        new Dictionary<string, CurveQuotes>
        {
            ["JPY/TONA"] = new("JPY", "TONA", [(Tenor.Parse("1y"), 0.5), (Tenor.Parse("10y"), 1.0)]),
            ["USD/SOFR"] = new("USD", "SOFR", [(Tenor.Parse("1y"), 4.0), (Tenor.Parse("10y"), 4.0)]),
        },
        new Dictionary<string, double> { ["USD"] = 1, ["JPY"] = 150 });

    /// <summary>A provider whose every figure is chosen by the test: a PV in the line's first leg's currency, and deltas.</summary>
    sealed class ScriptedPricing(Func<PricingLine, MarketSnapshot, (double Pv, IReadOnlyDictionary<string, double> Deltas)> answer) : IRatesPricingProvider
    {
        public string Name => "scripted";
        public IReadOnlyList<PricedProduct> Products { get; } = [new(RatesProducts.Irs, RatesProducts.Version), new(RatesProducts.Ccs, RatesProducts.Version)];

        public Task<RatesBatchResult> PriceBatchAsync(RatesBatchInput input, CancellationToken cancellationToken) =>
            Task.FromResult(new RatesBatchResult(Name, "simulated", [.. input.Lines.Where(line => line.RowKey != "SILENT").Select(line =>
            {
                if (line.RowKey == "FAILS") return new RowResult(line.RowKey, new Dictionary<string, Measure>(), "No curve for this line.");
                var (pv, deltas) = answer(line, input.Market);
                var reporting = line.Legs[0].Terms.Currency;
                var measures = new Dictionary<string, Measure> { [Measures.Pv] = new(pv, reporting) };
                if (line.Risk) foreach (var (key, value) in deltas) measures[key] = new(value, $"{reporting}/bp");
                return new RowResult(line.RowKey, measures, null);
            })]));
    }

    static readonly IReadOnlyDictionary<string, double> NoDelta = new Dictionary<string, double>();

    static CrifResult Generate(IRatesPricingProvider provider, params SimmTrade[] trades) =>
        new CrifGenerator(provider).GenerateAsync(trades, ValueDate, Market, CancellationToken.None).Result;

    [Fact] // ADR-0012 §5: a delta on one of SIMM's tenors is that tenor's line, in USD at the market's FX, on the OIS sub-curve
    public void A_delta_on_a_simm_tenor_is_its_line()
    {
        var provider = new ScriptedPricing((_, _) => (0, new Dictionary<string, double> { ["delta:JPY/TONA:5y"] = 15_000 }));

        var line = Generate(provider, Trade("T1", JpyIrs("T1"))).Lines.Single(l => l.RiskType == CrifRiskType.IrCurve);

        Assert.Equal(("JPY", "5y", "OIS", 15_000.0, "JPY", 100.0), (line.Qualifier, line.Label1, line.Label2, line.Amount, line.AmountCurrency, line.AmountUsd));
        Assert.Equal(["JFSA", "CFTC"], line.CollectRegulations);
        Assert.Equal(["JFSA"], line.PostRegulations);
    }

    [Fact] // ADR-0012 §5: a delta between SIMM's tenors is split between its neighbours, linearly in years
    public void A_delta_between_simm_tenors_is_split_between_its_neighbours()
    {
        // 7y lies 2/5 of the way from 5y to 10y: 300 × 3/5 to 5y and 300 × 2/5 to 10y
        var provider = new ScriptedPricing((_, _) => (0, new Dictionary<string, double> { ["delta:JPY/TONA:7y"] = 300 }));

        var lines = Generate(provider, Trade("T1", JpyIrs("T1"))).Lines.Where(l => l.RiskType == CrifRiskType.IrCurve).ToDictionary(l => l.Label1!, l => l.Amount);

        Assert.Equal(2, lines.Count);
        Assert.Equal(180, lines["5y"], 9);
        Assert.Equal(120, lines["10y"], 9);
    }

    [Fact] // SIMM: FX delta is the USD value's change for a 1% rise of the currency against USD
    public void A_trades_value_in_another_currency_is_its_fx_delta()
    {
        // PV 1.5mm JPY is 10,000 USD at 150; 1% stronger JPY makes it 10,100 USD
        var provider = new ScriptedPricing((_, _) => (1_500_000, NoDelta));

        var fx = Generate(provider, Trade("T1", JpyIrs("T1"))).Lines.Single(l => l.RiskType == CrifRiskType.Fx);

        Assert.Equal(("JPY", 100.0), (fx.Qualifier, Math.Round(fx.AmountUsd, 9)));
    }

    [Fact] // ADR-0012 §5: a cross-currency swap's principal exchange is exempt, so its FX delta is from its coupons only
    public void A_cross_currency_swaps_principal_exchange_is_left_out_of_its_fx_delta()
    {
        // The scripted PV counts the JPY principal (7.5bn) only when the line still carries it, and 3mm JPY of coupons
        var provider = new ScriptedPricing((line, _) => (3e6 + (line.Legs[0].Exchanges.Count > 0 ? 7.5e9 : 0), NoDelta));

        var fx = Generate(provider, Trade("T4", JpyUsdCcs("T4"))).Lines.Single(l => l.RiskType == CrifRiskType.Fx);

        Assert.Equal(("JPY", 200.0), (fx.Qualifier, Math.Round(fx.AmountUsd, 9)));
    }

    [Fact] // ADR-0012 §5: cross-currency basis is the change for 1bp more spread on the non-USD floating leg
    public void A_cross_currency_swaps_basis_is_its_non_usd_spread_delta()
    {
        // The scripted PV moves 7.5mm JPY per 1% of the JPY leg's spread: 75,000 JPY = 500 USD per bp
        var provider = new ScriptedPricing((line, _) => (7.5e6 * line.Legs[0].Periods[0].Rate!.Value, NoDelta));

        var basis = Generate(provider, Trade("T4", JpyUsdCcs("T4"))).Lines.Single(l => l.RiskType == CrifRiskType.XCcyBasis);

        Assert.Equal(("JPY", 500.0), (basis.Qualifier, Math.Round(basis.AmountUsd, 9)));
    }

    [Fact] // ADR-0012 §2, §5: an exempt trade gives no line and is listed as excluded with its reason
    public void An_exempt_trade_is_excluded_with_its_reason()
    {
        var provider = new ScriptedPricing((_, _) => (1e6, new Dictionary<string, double> { ["delta:JPY/TONA:5y"] = 1 }));
        var forward = new SimmTrade("T5", null, "Physically settled FX forward: exempt from IM in every regime.", [], []);

        var result = Generate(provider, forward);

        Assert.Empty(result.Lines);
        Assert.Equal(("T5", "Physically settled FX forward: exempt from IM in every regime."), (result.Excluded.Single().TradeId, result.Excluded.Single().Reason));
    }

    [Fact] // ADR-0012 §2: an exempt trade is frozen with its exemption and nothing to price, even where the source holds its terms
    public void An_exempt_trade_is_frozen_with_nothing_to_price_even_with_its_terms()
    {
        var portfolio = new SyntheticImSource().Find("ABC/LE-01")!;
        var irs = portfolio.Trades.Single(t => t.Id == "T-1001");
        Assert.NotNull(irs.Line);

        // The exemption is decided before any term is resolved, so no convention is needed to freeze it.
        var frozen = (portfolio with { Trades = [irs with { Exemption = "Exempt by test." }] }).FreezeTrades(ValueDate, new ConventionRegistry([], []));

        var trade = Assert.Single(frozen.Trades);
        Assert.Equal(("T-1001", "Exempt by test."), (trade.Id, trade.Exemption));
        Assert.Null(trade.Pricing);
        Assert.Empty(frozen.NotFrozen);
    }

    [Fact] // Spine 1: a trade the provider could not price is excluded with the provider's reason, never left out silently
    public void A_trade_the_provider_failed_is_excluded_with_its_reason()
    {
        var provider = new ScriptedPricing((_, _) => (0, new Dictionary<string, double> { ["delta:JPY/TONA:5y"] = 1 }));

        var result = Generate(provider, Trade("T1", JpyIrs("T1")), Trade("FAILS", JpyIrs("FAILS")));

        Assert.All(result.Lines, l => Assert.Equal("T1", l.TradeId));
        Assert.Equal(("FAILS", "No curve for this line."), (result.Excluded.Single().TradeId, result.Excluded.Single().Reason));
    }

    [Fact] // Spine 1: a trade the provider returned no row for is excluded, saying so, never priced as nothing
    public void A_trade_the_provider_did_not_answer_is_excluded_saying_so()
    {
        var provider = new ScriptedPricing((_, _) => (0, new Dictionary<string, double> { ["delta:JPY/TONA:5y"] = 1 }));

        var result = Generate(provider, Trade("T1", JpyIrs("T1")), Trade("SILENT", JpyIrs("SILENT")));

        Assert.All(result.Lines, l => Assert.Equal("T1", l.TradeId));
        Assert.Equal(("SILENT", "The provider scripted returned no row for the trade."), (result.Excluded.Single().TradeId, result.Excluded.Single().Reason));
    }

    [Fact] // Spine 1: a curve of an index that is not overnight has no SIMM sub-curve until one is configured, and says so
    public void A_term_index_without_a_sub_curve_is_refused_by_name()
    {
        var provider = new ScriptedPricing((_, _) => (0, new Dictionary<string, double> { ["delta:JPY/TIBOR6M:5y"] = 1 }));
        var tibor = new PricingLine("T6", 1, Direction.Pay, [Leg("JPY", "FIXED", false, 1.0), Leg("JPY", "TIBOR6M", true, 0, overnight: false)], null, true);

        var result = Generate(provider, Trade("T6", tibor));

        Assert.Empty(result.Lines);
        Assert.Contains("TIBOR6M", result.Excluded.Single().Reason);
    }
}
