using Workbench.Samples.Ccr;

namespace Workbench.Composition.Tests;

/// <summary>
/// ADR-0013 §3: SA-CCR in full (CRE52 as in force from 2023-01-01). The BCBS worked examples hold their published
/// EADs (569, 381 and 5,406); every other expected figure is a hand calculation of the rule it names, done apart from
/// this code, and none is recomputed here.
/// </summary>
public class CcrSaCcrTests
{
    static readonly SaCcrSettings Basel = new("sa-ccr · 2023-01-01");

    static readonly LinearTerms Long = new(Long: true);
    static readonly LinearTerms Short = new(Long: false);

    static SaCcrExposure Ir(string trade, string currency, double notional, double start, double end, DeltaTerms delta,
        double? maturity = null, HedgingSetType type = HedgingSetType.Regular, string? hedgingSet = null) =>
        new(trade, "", AssetClass.InterestRate, hedgingSet ?? currency, delta, maturity ?? end, notional, StartYears: start, EndYears: end, HedgingSetType: type);

    static SaCcrExposure Fx(string trade, string pair, double notional, double maturity, DeltaTerms delta) =>
        new(trade, "", AssetClass.ForeignExchange, pair, delta, maturity, notional);

    static SaCcrExposure Cds(string trade, string entity, CreditSubclass subclass, double notional, double end, DeltaTerms delta) =>
        new(trade, "", AssetClass.Credit, "credit", delta, end, notional, StartYears: 0, EndYears: end, RiskFactor: entity, CreditSubclass: subclass);

    static SaCcrExposure Commodity(string trade, string set, string type, double price, double units, double maturity, DeltaTerms delta, bool electricity = false) =>
        new(trade, "", AssetClass.Commodity, set, delta, maturity, price, Units: units, RiskFactor: type, Electricity: electricity);

    static SaCcrExposure Equity(string trade, string entity, double price, double units, double maturity, DeltaTerms delta, bool index = false,
        HedgingSetType type = HedgingSetType.Regular, string hedgingSet = "equity") =>
        new(trade, "", AssetClass.Equity, hedgingSet, delta, maturity, price, Units: units, RiskFactor: entity, Index: index, HedgingSetType: type);

    static SaCcrTrade Trade(string id, double value, params SaCcrExposure[] exposures) => new(id, value, exposures);

    static SaCcrNettingSet Unmargined(string id, params SaCcrTrade[] trades) => new(id, "USD", trades);

    static void Near(double expected, double actual) => Assert.Equal(expected, actual, Math.Max(1e-9, Math.Abs(expected) * 1e-12));

    // The BCBS netting sets: interest rate (1), credit (2) and commodity (3).

    static SaCcrTrade[] InterestRateTrades =>
    [
        Trade("T-1", 30, Ir("T-1", "USD", 10_000, 0, 10, Long)),
        Trade("T-2", -20, Ir("T-2", "USD", 10_000, 0, 4, Short)),
        Trade("T-3", 50, Ir("T-3", "EUR", 5_000, 1, 11, new OptionTerms(OptionType.Put, Bought: true, Underlying: 0.06, Strike: 0.05, ExerciseYears: 1))),
    ];

    static SaCcrTrade[] CommodityTrades =>
    [
        Trade("T-7", -50, Commodity("T-7", "energy", "crude oil", 1, 10_000, 0.75, Long)),
        Trade("T-8", -30, Commodity("T-8", "energy", "crude oil", 1, 20_000, 2, Short)),
        Trade("T-9", 100, Commodity("T-9", "metals", "silver", 1, 10_000, 5, Long)),
    ];

    [Fact] // ADR-0013 §3, CRE52: the BCBS example's first netting set, three interest rate trades, EAD 569
    public void The_bcbs_interest_rate_netting_set_has_ead_569()
    {
        var ns = SaCcr.Calculate(Unmargined("NS-1", InterestRateTrades), Basel);
        Assert.Equal(569, Math.Round(ns.Ead));
        Near(569.4701409373457, ns.Ead);

        var basis = ns.Unmargined;
        Assert.Null(ns.Margined);
        Near(60, basis.ReplacementCost);
        Near(1, basis.Multiplier);
        Near(346.7643863838184, basis.AddOn);
        var ir = basis.AssetClasses.Single(a => a.AssetClass == AssetClass.InterestRate);
        Near(59269.9634637104, ir.HedgingSets.Single(h => h.Key == "USD").EffectiveNotional!.Value);
        Near(10082.913813053281, ir.HedgingSets.Single(h => h.Key == "EUR").EffectiveNotional!.Value);
        var swaption = basis.Rows.Single(r => r.Exposure.TradeId == "T-3");
        Near(-0.2693952177105327, swaption.Delta);
        Near(7.485592282404547, swaption.SupervisoryDuration!.Value);
        Assert.Equal(3, swaption.Bucket);
    }

    [Fact] // ADR-0013 §3, CRE52.60-64: the BCBS example's credit netting set, single-factor over entities, EAD 381
    public void The_bcbs_credit_netting_set_has_ead_381()
    {
        var ns = SaCcr.Calculate(Unmargined("NS-2",
            Trade("T-4", 20, Cds("T-4", "Firm A", CreditSubclass.AA, 10_000, 3, Long)),
            Trade("T-5", -40, Cds("T-5", "Firm B", CreditSubclass.BBB, 10_000, 6, Short)),
            Trade("T-6", 0, Cds("T-6", "CDX.IG 5y", CreditSubclass.IndexInvestmentGrade, 10_000, 5, Long))), Basel);
        Assert.Equal(381, Math.Round(ns.Ead));
        Near(381.2383187469392, ns.Ead);
        Near(0, ns.Unmargined.ReplacementCost);
        Near(0.965208280997999, ns.Unmargined.Multiplier);
        Near(282.1288318596666, ns.Unmargined.AddOn);
        var credit = ns.Unmargined.AssetClasses.Single().HedgingSets.Single();
        Assert.Equal([("CDX.IG 5y", 0.8), ("Firm A", 0.5), ("Firm B", 0.5)], credit.RiskFactors.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => (f.Key, f.Correlation)));
    }

    [Fact] // ADR-0013 §3, CRE52.69-71: the BCBS example's commodity netting set, two hedging sets, EAD 5,406
    public void The_bcbs_commodity_netting_set_has_ead_5406()
    {
        var ns = SaCcr.Calculate(Unmargined("NS-3", CommodityTrades), Basel);
        Assert.Equal(5406, Math.Round(ns.Ead));
        Near(5405.615982463214, ns.Ead);
        var sets = ns.Unmargined.AssetClasses.Single().HedgingSets;
        Near(2041.1542731880104, sets.Single(h => h.Key == "energy").AddOn);
        Near(1800, sets.Single(h => h.Key == "metals").AddOn);
        Near(Math.Sqrt(0.75), ns.Unmargined.Rows.Single(r => r.Exposure.TradeId == "T-7").MaturityFactor);
    }

    [Fact] // ADR-0013 §3, CRE52.25: the asset classes' add-ons are summed, without diversification
    public void Asset_classes_add_up_without_diversification()
    {
        var ns = SaCcr.Calculate(Unmargined("NS-4", [.. InterestRateTrades, .. CommodityTrades]), Basel);
        Near(5975.086123400561, ns.Ead);
        Assert.Equal([AssetClass.InterestRate, AssetClass.Commodity], ns.Unmargined.AssetClasses.Select(a => a.AssetClass));
    }

    // Margined netting sets.

    static Margining Daily(double threshold = 0, double mta = 0.5) => new("CSA-B", 1, threshold, mta);

    static SaCcrNettingSet FxMargined(Margining margin) => new("NS-5", "USD",
        [
            Trade("T-10", 12, Fx("T-10", "EURUSD", 3_240, 0.5, Long)),
            Trade("T-11", -4, Fx("T-11", "EURUSD", 1_080, 1, Short)),
        ],
        margin,
        [new CollateralBalance("VM-1", CollateralDirection.Received, CollateralPurpose.VariationMargin, "cash", "USD", 6, 0)]);

    [Fact] // ADR-0013 §3, CRE52.2, CRE52.18, CRE52.52: a margined netting set takes the smaller of its margined and unmargined EAD
    public void A_margined_netting_set_takes_the_smaller_of_its_two_eads()
    {
        var ns = SaCcr.Calculate(FxMargined(Daily()), Basel);
        Near(39.088, ns.Margined!.Ead);
        Near(70.6174543784872, ns.Unmargined.Ead);
        Near(39.088, ns.Ead);
        Assert.True(ns.MarginedGoverns);
        Near(2, ns.Margined.ReplacementCost);
        Near(0.3, ns.Margined.Rows[0].MaturityFactor);
        Assert.Equal(10, ns.Mpor!.Days);
    }

    [Fact] // ADR-0013 §3, CRE52.2: the margined EAD is capped at the unmargined one
    public void The_unmargined_ead_caps_the_margined_one()
    {
        var margin = new Margining("CSA-C", 1, 0, 0, IlliquidCollateralOrHardToReplace: true, DisputesLongerThanMpor: 3);
        var ns = SaCcr.Calculate(new SaCcrNettingSet("NS-6", "USD", [Trade("T-12", 5, Fx("T-12", "EURUSD", 1_000, 0.1, Long))], margin), Basel);
        Assert.Equal(40, ns.Mpor!.Days);
        Near(40.6, ns.Margined!.Ead);
        Near(24.708754896942928, ns.Ead);
        Assert.False(ns.MarginedGoverns);
    }

    [Theory] // ADR-0013 §3, CRE52.50-51: the margin period of risk is derived from the CSA and the netting set
    [InlineData(1, false, 0, 10)]
    [InlineData(5, false, 0, 14)]
    [InlineData(1, true, 0, 20)]
    [InlineData(1, false, 3, 20)]
    [InlineData(1, false, 2, 10)]
    [InlineData(5, false, 3, 28)]
    [InlineData(1, true, 3, 40)]
    public void The_mpor_is_derived_from_its_rules(int remargining, bool illiquid, int disputes, int days)
    {
        var margin = new Margining("CSA-B", remargining, 0, 0.5, illiquid, disputes);
        var ns = SaCcr.Calculate(FxMargined(margin), Basel);
        Assert.Equal(days, ns.Mpor!.Days);
        Assert.NotEmpty(ns.Mpor.Because);
    }

    [Fact] // ADR-0013 §3, CRE52.51: more than 5,000 trades in the netting set set a floor of 20 business days
    public void More_than_five_thousand_trades_take_twenty_days()
    {
        var trades = Enumerable.Range(1, 5_001).Select(i => Trade($"T-{i}", 0, Fx($"T-{i}", "EURUSD", 1, 1, Long))).ToArray();
        var ns = SaCcr.Calculate(new SaCcrNettingSet("NS-7", "USD", trades, Daily()), Basel);
        Assert.Equal(20, ns.Mpor!.Days);
        Assert.Equal(10, SaCcr.Calculate(new SaCcrNettingSet("NS-7", "USD", trades[..5_000], Daily()), Basel).Mpor!.Days);
    }

    [Fact] // ADR-0013 §3, CRE52.20, CRE52.52: a longer MPOR raises the margined maturity factor
    public void The_margined_maturity_factor_follows_the_mpor()
    {
        var ns = SaCcr.Calculate(FxMargined(Daily() with { IlliquidCollateralOrHardToReplace = true }), Basel);
        Near(54.11898175139487, ns.Margined!.Ead);
    }

    [Fact] // ADR-0013 §3, CRE52.17-19: the margined replacement cost can be set by TH + MTA - NICA
    public void The_threshold_and_mta_less_nica_can_set_the_margined_replacement_cost()
    {
        var ns = SaCcr.Calculate(new SaCcrNettingSet("NS-8", "USD",
            [Trade("T-13", -30, Ir("T-13", "USD", 1_000, 0, 5, Long))],
            Daily(threshold: 50, mta: 5),
            [
                new CollateralBalance("VM-2", CollateralDirection.Posted, CollateralPurpose.VariationMargin, "cash", "USD", 20, 0),
                new CollateralBalance("IA-1", CollateralDirection.Received, CollateralPurpose.IndependentAmount, "cash", "USD", 15, 0),
            ]), Basel);
        var margined = ns.Margined!;
        Near(-30, margined.MarketValue);
        Near(-5, margined.Collateral);
        Near(15, margined.Nica!.Value);
        Near(55, margined.ThresholdAndMta!.Value);
        Near(40, margined.ReplacementCost);
        Near(0.1807974379350697, margined.Multiplier);
        Near(57.679674571145206, margined.Ead);
        Near(17.777624540519078, ns.Ead);
    }

    [Fact] // ADR-0013 §3, CRE52.5, CRE52.11, CRE22.59: haircuts scale to the MPOR margined, to the longest trade unmargined
    public void Collateral_is_haircut_over_the_period_its_basis_uses()
    {
        var collateral = new CollateralBalance[]
        {
            new("B-1", CollateralDirection.Received, CollateralPurpose.VariationMargin, "sovereign bond", "EUR", 100, 0.02),
            new("B-2", CollateralDirection.Posted, CollateralPurpose.VariationMargin, "cash", "USD", 50, 0, Segregated: true),
            new("B-3", CollateralDirection.Posted, CollateralPurpose.VariationMargin, "cash", "USD", 10, 0),
        };
        var margin = Daily() with { IlliquidCollateralOrHardToReplace = true };
        var five = SaCcr.Calculate(new SaCcrNettingSet("NS-9", "USD", [Trade("T-14", 0, Ir("T-14", "USD", 1_000, 0, 5, Long))], margin, collateral), Basel);
        Near(75.85786437626905, five.Margined!.Collateral);
        Near(39.107957400002114, five.Unmargined.Collateral);
        var bond = five.Margined.CollateralRows.Single(r => r.Balance.Id == "B-1");
        Near(0.02 * Math.Sqrt(2), bond.Haircut);
        Near(0.08 * Math.Sqrt(2), bond.CurrencyHaircut);
        Assert.False(five.Margined.CollateralRows.Single(r => r.Balance.Id == "B-2").InCollateral);

        var half = SaCcr.Calculate(new SaCcrNettingSet("NS-9", "USD", [Trade("T-15", 0, Ir("T-15", "USD", 1_000, 0, 0.5, Long))], margin, collateral), Basel);
        Near(53.39398956455374, half.Unmargined.Collateral);
    }

    [Fact] // ADR-0013 §3, CRE52.22-23: the multiplier falls towards its 5% floor as V - C turns negative
    public void The_multiplier_falls_towards_its_floor()
    {
        var ns = SaCcr.Calculate(new SaCcrNettingSet("NS-10", "USD",
            [Trade("T-16", 0, Fx("T-16", "EURUSD", 2_500, 1, Long))], null,
            [new CollateralBalance("VM-3", CollateralDirection.Received, CollateralPurpose.VariationMargin, "cash", "USD", 1_000, 0)]), Basel);
        Near(100, ns.Unmargined.AddOn);
        Near(0.054919978152067866, ns.Unmargined.Multiplier);
    }

    // Interest rate.

    [Fact] // ADR-0013 §3, CRE52.57: the three buckets offset with 1.4 between neighbours and 0.6 between the first and third
    public void The_three_maturity_buckets_offset_by_their_correlations()
    {
        var ns = SaCcr.Calculate(Unmargined("NS-24",
            Trade("T-35", 0, Ir("T-35", "USD", 10_000, 0, 0.5, Long)),
            Trade("T-36", 0, Ir("T-36", "USD", 10_000, 0, 3, Short)),
            Trade("T-37", 0, Ir("T-37", "USD", 10_000, 0, 10, Long))), Basel);
        var usd = ns.Unmargined.AssetClasses.Single().HedgingSets.Single();
        Assert.Equal([1, 2, 3], usd.Buckets.Select(b => b.Bucket));
        Near(3491.705726571684, usd.Buckets[0].EffectiveNotional);
        Near(62773.326526035235, usd.EffectiveNotional!.Value);
        Near(313.8666326301762, usd.AddOn);
    }

    [Fact] // ADR-0013 §3, CRE52.57: without offset across buckets the effective notional is the sum of their sizes
    public void Without_offset_across_buckets_the_buckets_add_up()
    {
        var ns = SaCcr.Calculate(Unmargined("NS-1", InterestRateTrades), Basel with { IrOffsetAcrossBuckets = false });
        var usd = ns.Unmargined.AssetClasses.Single().HedgingSets.Single(h => h.Key == "USD");
        Near(574.7385872093847, usd.AddOn);
    }

    [Theory] // ADR-0013 §3, CRE52.31, CRE52.57: the bucket is set by the end date: under one year, one to five, over five
    [InlineData(0.99, 1)]
    [InlineData(1.0, 2)]
    [InlineData(5.0, 2)]
    [InlineData(5.01, 3)]
    public void An_interest_rate_trade_falls_in_the_bucket_of_its_end(double end, int bucket)
    {
        var ns = SaCcr.Calculate(Unmargined("NS-11", Trade("T-17", 0, Ir("T-17", "USD", 1_000, 0, end, Long))), Basel);
        Assert.Equal(bucket, ns.Unmargined.Rows.Single().Bucket);
    }

    [Fact] // ADR-0013 §3, CRE52.34, CRE52.48: the supervisory duration and the maturity are floored at ten business days
    public void Duration_and_maturity_are_floored_at_ten_business_days()
    {
        var row = SaCcr.Calculate(Unmargined("NS-12", Trade("T-18", 0, Ir("T-18", "USD", 1_000, 2, 2.01, Long, maturity: 0.01))), Basel).Unmargined.Rows.Single();
        Near(0.04, row.SupervisoryDuration!.Value);
        Near(0.2, row.MaturityFactor);
    }

    [Fact] // ADR-0013 §3, CRE52.46-47: a basis hedging set takes half the factor, a volatility hedging set five times it
    public void Basis_and_volatility_hedging_sets_scale_the_supervisory_factor()
    {
        var basis = SaCcr.Calculate(Unmargined("NS-13",
            Trade("T-19", 0, Ir("T-19", "USD", 10_000, 0, 5, Long, type: HedgingSetType.Basis, hedgingSet: "USD SOFR/TERM-3M"))), Basel);
        var set = basis.Unmargined.AssetClasses.Single().HedgingSets.Single();
        Assert.Equal(HedgingSetType.Basis, set.Type);
        Near(0.0025, set.SupervisoryFactor!.Value);
        Near(110.59960846429755, set.AddOn);

        var volatility = SaCcr.Calculate(Unmargined("NS-14",
            Trade("T-20", 0, Equity("T-20", "Name X", 0.04, 100_000, 1, Long, type: HedgingSetType.Volatility, hedgingSet: "equity volatility"))), Basel);
        Near(6400, volatility.Unmargined.AddOn);
    }

    // Supervisory delta.

    [Theory] // ADR-0013 §3, CRE52.40: the delta of an option, by type and side
    [InlineData(OptionType.Call, true, 0.6860603105063084)]
    [InlineData(OptionType.Call, false, -0.6860603105063084)]
    [InlineData(OptionType.Put, true, -0.31393968949369155)]
    [InlineData(OptionType.Put, false, 0.31393968949369155)]
    public void An_option_takes_its_supervisory_delta(OptionType type, bool bought, double delta)
    {
        var option = new OptionTerms(type, bought, Underlying: 100, Strike: 95, ExerciseYears: 0.5);
        var row = SaCcr.Calculate(Unmargined("NS-15", Trade("T-21", 0, Equity("T-21", "Name X", 100, 10, 0.5, option))), Basel).Unmargined.Rows.Single();
        Near(delta, row.Delta);
        Near(1.2, row.OptionVolatility!.Value);
    }

    [Fact] // ADR-0013 §3, CRE52.40 FAQ: a negative rate takes the shift, and a price still not positive is refused
    public void A_negative_underlying_takes_the_shift_or_is_refused()
    {
        var shifted = new OptionTerms(OptionType.Call, true, Underlying: -0.002, Strike: 0.001, ExerciseYears: 1, Shift: 0.01);
        var row = SaCcr.Calculate(Unmargined("NS-16", Trade("T-22", 0, Ir("T-22", "EUR", 1_000, 1, 6, shifted))), Basel).Unmargined.Rows.Single();
        Near(0.34941235826182565, row.Delta);

        var refused = Assert.Throws<SaCcrRefused>(() => SaCcr.Calculate(Unmargined("NS-16",
            Trade("T-22", 0, Ir("T-22", "EUR", 1_000, 1, 6, shifted with { Shift = 0 }))), Basel));
        Assert.Contains("T-22", refused.Message);
    }

    [Fact] // ADR-0013 §3, CRE52.41: a CDO tranche's delta from its attachment and detachment
    public void A_cdo_tranche_takes_its_delta_from_attachment_and_detachment()
    {
        var bought = new TrancheTerms(Attachment: 0.03, Detachment: 0.07, ProtectionBought: true);
        var rows = SaCcr.Calculate(Unmargined("NS-17",
            Trade("T-23", 0, Cds("T-23", "CDX.IG 3-7%", CreditSubclass.IndexInvestmentGrade, 1_000, 5, bought)),
            Trade("T-24", 0, Cds("T-24", "CDX.IG 3-7%", CreditSubclass.IndexInvestmentGrade, 1_000, 5, bought with { ProtectionBought = false }))), Basel).Unmargined.Rows;
        Near(5.335040546308152, rows[0].Delta);
        Near(-5.335040546308152, rows[1].Delta);
    }

    // FX, equity, and netting.

    [Fact] // ADR-0013 §3, CRE52.58-59: one hedging set per currency pair, each its own absolute effective notional
    public void Each_currency_pair_is_its_own_hedging_set()
    {
        var ns = SaCcr.Calculate(Unmargined("NS-18",
            Trade("T-25", 0, Fx("T-25", "EURUSD", 3_000, 1, Long)),
            Trade("T-26", 0, Fx("T-26", "EURUSD", 1_000, 1, Short)),
            Trade("T-27", 0, Fx("T-27", "JPYUSD", 5_000, 1, Short))), Basel);
        var sets = ns.Unmargined.AssetClasses.Single().HedgingSets;
        Near(80, sets.Single(h => h.Key == "EURUSD").AddOn);
        Near(200, sets.Single(h => h.Key == "JPYUSD").AddOn);
    }

    [Fact] // ADR-0013 §3, CRE52.65-68: equity names and indices aggregate by the single factor, 32% and 20%
    public void Equity_names_and_indices_aggregate_by_the_single_factor()
    {
        var ns = SaCcr.Calculate(Unmargined("NS-19",
            Trade("T-28", 0, Equity("T-28", "Name X", 50, 1_000, 1, Long)),
            Trade("T-29", 0, Equity("T-29", "Name Y", 20, 1_000, 1, Short)),
            Trade("T-30", 0, Equity("T-30", "Index Z", 4_000, 10, 1, Long, index: true))), Basel);
        Near(19266.551326067674, ns.Unmargined.AddOn);
    }

    [Fact] // ADR-0013 §3: a netting set that is not legally enforceable is one netting set per trade, without collateral
    public void A_netting_set_that_is_not_enforceable_is_one_per_trade()
    {
        var set = new SaCcrNettingSet("NS-20", "USD", InterestRateTrades[..2], null,
            [new CollateralBalance("VM-4", CollateralDirection.Received, CollateralPurpose.VariationMargin, "cash", "USD", 10, 0)],
            LegallyEnforceable: false);
        var result = SaCcr.Calculate([set], Basel);
        Assert.Equal(["NS-20 · T-1", "NS-20 · T-2"], result.NettingSets.Select(n => n.Id));
        Near(592.8570764023132, result.NettingSets[0].Ead);
        Near(240.17568070800226, result.NettingSets[1].Ead);
        Near(833.0327571103154, result.Ead);
        Assert.Contains("not legally enforceable", result.NettingSets[0].Note);
    }

    // Validation (ADR-0013 §6): a trade the calculator cannot read refuses the measure by name.

    [Fact]
    public void An_exposure_missing_what_its_class_needs_is_refused()
    {
        Assert.Contains("T-31", Assert.Throws<SaCcrRefused>(() => SaCcr.Calculate(Unmargined("NS-21",
            Trade("T-31", 0, new SaCcrExposure("T-31", "", AssetClass.InterestRate, "USD", Long, 5, 1_000))), Basel)).Message);
        Assert.Contains("Firm A", Assert.Throws<SaCcrRefused>(() => SaCcr.Calculate(Unmargined("NS-22",
            Trade("T-32", 0, Cds("T-32", "Firm A", CreditSubclass.AA, 1_000, 3, Long)),
            Trade("T-33", 0, Cds("T-33", "Firm A", CreditSubclass.BBB, 1_000, 3, Long))), Basel)).Message);
        Assert.Contains("negative", Assert.Throws<SaCcrRefused>(() => SaCcr.Calculate(Unmargined("NS-23",
            Trade("T-34", 0, Fx("T-34", "EURUSD", -1, 1, Long))), Basel)).Message);
    }
}
