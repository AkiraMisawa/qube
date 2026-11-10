using Qube.Composition.Application.Traces;
using Qube.Composition.Contracts;
using Qube.Samples.Ccr;

namespace Qube.Composition.Tests;

/// <summary>
/// ADR-0013 §2, §3, §6: spot capital over a portfolio as the pricer freezes it. A portfolio trade becomes SA-CCR's
/// exposures, its CSAs SA-CCR's netting sets and margining, its collateral SA-CCR's balances, and each side is priced
/// on its own, with its trace, and a capital profile that KVA reads.
/// </summary>
public class CcrPricingTests
{
    static readonly DateOnly Valuation = new(2026, 11, 6);
    static readonly DatasetProvenance Simulated = new(ExecutionMode.Simulated, DataOrigin.Synthetic, "run-1/xva");

    static readonly CounterpartyRiskFacts Bank = new("ABC", "LE-01", InstitutionType.Bank,
        Ratings: [new("Agency 1", "AA-"), new("Agency 2", "A1"), new("Agency 3", "A")], CountryOfIncorporation: "JP",
        SovereignOfIncorporation: new(Ratings: [new("Agency 1", "AA")]), DueDiligenceBucketsUp: 0);

    static readonly CapitalCsa CsaA = new("CSA-A", "JPY", "daily", Exchange: true, OneWay: false, Threshold: 0, Mta: 50_000_000, ThresholdCurrency: "JPY");
    static readonly CapitalCsa CsaB = new("CSA-B", "USD", "daily", Exchange: true, OneWay: false, Threshold: 1_000_000, Mta: 500_000, ThresholdCurrency: "USD", Disputes: 3);

    static CapitalTrade Irs(string id, string currency, double notional, string start, string end, string direction, string csa, double? pv,
        bool before = true, bool after = true, bool isNew = false) =>
        new(id, isNew, "IRS", currency, notional, DateOnly.Parse(end), csa, before, after, pv, DateOnly.Parse(start), direction);

    static readonly CapitalTrade E1 = Irs("E1", "JPY", 1_000_000_000, "2025-03-03", "2030-03-03", "payFixed", "CSA-A", 2_000_000);
    static readonly CapitalTrade E2 = Irs("E2", "USD", 10_000_000, "2024-06-03", "2027-06-03", "receiveFixed", "CSA-B", -50_000);
    static readonly CapitalTrade E3 = new("E3", false, "FXForward", "USD", 5_000_000, new(2027, 2, 1), "CSA-A", true, true, 10_000,
        new(2026, 8, 1), "buy", "JPY", 740_000_000);
    static readonly CapitalTrade N1 = Irs("N1", "JPY", 2_000_000_000, "2026-11-06", "2031-11-06", "receiveFixed", "CSA-A", null, before: false, isNew: true);

    static CapitalPortfolio Portfolio(IReadOnlyList<CapitalTrade>? trades = null, IReadOnlyList<CapitalCsa>? csas = null,
        IReadOnlyList<CapitalCollateral>? collateral = null, string nettingSet = "NS-001") =>
        new(Valuation, "JPY", new Dictionary<string, double> { ["JPY"] = 1, ["USD"] = 150, ["EUR"] = 160 },
            new TraceSubject("counterparty", "ABC/LE-01", "ABC Holdings (synthetic)"), Bank, nettingSet, Enforceable: true,
            csas ?? [CsaA, CsaB],
            collateral ??
            [
                new("COL-1", "CSA-A", CollateralDirection.Received, CollateralPurpose.VariationMargin, "cash", "JPY", 1_500_000),
                new("COL-2", "CSA-B", CollateralDirection.Received, CollateralPurpose.VariationMargin, "sovereignBond", "USD", 20_000, IssuerGrade: "AA", ResidualYears: 3),
            ],
            trades ?? [E1, E2, E3, N1],
            new(new CapitalRatio(0.08, "ccr.capitalRatio", "1"), new SaCcrSettings("sa-ccr · 2023-01-01"),
                new RiskWeightSettings(ExternalRatingsAllowed: true, Version: "basel-iii · 2023-01-01"), DomesticCurrency: "JPY"));

    [Fact] // ADR-0013 §3: an IRS is one interest rate exposure in its currency, an FX forward one on its pair (CRE52.35, CRE52.39)
    public void A_portfolio_trade_is_decomposed_by_its_product()
    {
        var p = Portfolio();
        var swap = Assert.Single(SpotCapitalPricing.Exposures(E2, p.Market));
        Assert.Equal((AssetClass.InterestRate, "USD", 1_500_000_000.0, false), (swap.AssetClass, swap.HedgingSet, swap.Notional, ((LinearTerms)swap.Delta).Long));
        Assert.Equal(0, swap.StartYears);
        // Bought USD against JPY, the reporting currency: the notional is the USD leg in JPY, short the pair's first, JPY.
        var forward = Assert.Single(SpotCapitalPricing.Exposures(E3, p.Market));
        Assert.Equal((AssetClass.ForeignExchange, "JPYUSD", 750_000_000.0, false), (forward.AssetClass, forward.HedgingSet, forward.Notional, ((LinearTerms)forward.Delta).Long));
        Assert.Equal("Interest rate · JPY · 1-5y · long", SpotCapitalPricing.Classification(SpotCapitalPricing.Exposures(E1, p.Market)));
        Assert.Equal("Interest rate · JPY · over 5y · short", SpotCapitalPricing.Classification(SpotCapitalPricing.Exposures(N1, p.Market)));
        Assert.Equal("FX · JPYUSD · short", SpotCapitalPricing.Classification(SpotCapitalPricing.Exposures(E3, p.Market)));
    }

    [Fact] // ADR-0013 §7, CRE52.35: the foreign leg is the one not in the bank's domestic currency, whatever currency the figures are shown in
    public void An_FX_trades_foreign_leg_follows_the_domestic_currency_not_the_display()
    {
        var inUsd = Portfolio() with { ReportingCurrency = "USD", ToReporting = new Dictionary<string, double> { ["JPY"] = 1.0 / 150, ["USD"] = 1, ["EUR"] = 160.0 / 150 } };
        // Bought USD against JPY with JPY domestic: the USD leg, 5,000,000 in USD, not the JPY leg converted (4,933,333).
        var forward = Assert.Single(SpotCapitalPricing.Exposures(E3, inUsd.Market));
        Assert.Equal(5_000_000, forward.Notional, 6);
        var usdBank = inUsd with { Settings = inUsd.Settings with { DomesticCurrency = "USD" } };
        Assert.Equal(740_000_000.0 / 150, Assert.Single(SpotCapitalPricing.Exposures(E3, usdBank.Market)).Notional, 6);
    }

    [Fact] // ADR-0013 §6: a trade without the fields its decomposition needs is refused by name, never decomposed by a guess
    public void A_trade_its_decomposition_cannot_read_is_refused_by_name()
    {
        var p = Portfolio().Market;
        Assert.Contains("E1: the source gives no start date", Assert.Throws<SaCcrRefused>(() => SpotCapitalPricing.Exposures(E1 with { Start = null }, p)).Message);
        Assert.Contains("E1: the direction is not given", Assert.Throws<SaCcrRefused>(() => SpotCapitalPricing.Exposures(E1 with { Direction = null }, p)).Message);
        Assert.Contains("E3: the source gives no counter amount", Assert.Throws<SaCcrRefused>(() => SpotCapitalPricing.Exposures(E3 with { CounterNotional = null }, p)).Message);
        Assert.Contains("a Swaption is not a product", Assert.Throws<SaCcrRefused>(() => SpotCapitalPricing.Exposures(E1 with { Product = "Swaption" }, p)).Message);
        Assert.Contains("E2: the request froze no FX rate from GBP", Assert.Throws<SaCcrRefused>(() => SpotCapitalPricing.Exposures(E2 with { Currency = "GBP" }, p)).Message);
    }

    [Fact] // ADR-0013 §3 (DEFAULT 2026-11-08): a netting set's trades are calculated by CSA, and the names do not move with the side
    public void A_netting_set_under_several_CSAs_is_calculated_by_CSA_under_names_both_sides_share()
    {
        var p = Portfolio();
        var before = SpotCapitalPricing.NettingSets(p, SpotCapitalPricing.On(p, SpotCapitalPricing.Before));
        var after = SpotCapitalPricing.NettingSets(p, SpotCapitalPricing.On(p, SpotCapitalPricing.After));
        Assert.Equal(["NS-001 CSA-A", "NS-001 CSA-B"], before.Select(s => s.Id));
        Assert.Equal(["NS-001 CSA-A", "NS-001 CSA-B"], after.Select(s => s.Id));
        Assert.Equal(["E1", "E3", "N1"], after[0].Trades.Select(t => t.TradeId));
        // The CSA's own terms in the reporting currency: CSA-B's threshold and MTA are USD.
        Assert.Equal(new Margining("CSA-B", 1, 150_000_000, 75_000_000, false, 3), after[1].Margin);
        Assert.Equal(("USD", 20_000 * 150.0, 0.02), (after[1].Collateral![0].Currency, after[1].Collateral![0].Amount, after[1].Collateral![0].SupervisoryHaircut));

        // One CSA: its trades keep the netting set's name, and those taken off it are a netting set of their own.
        var one = Portfolio([E1, E3 with { Csa = "nocsa_MA-002" }], [CsaA], [], "NS-002");
        Assert.Equal(["NS-002", "NS-002 nocsa_MA-002"], SpotCapitalPricing.NettingSets(one, one.Trades).Select(s => (s.Id)));
        Assert.Null(SpotCapitalPricing.NettingSets(one, one.Trades)[1].Margin);
        var none = Portfolio([E1 with { Csa = "nocsa_MA-003" }], [], [], "NS-003");
        Assert.Equal(["NS-003"], SpotCapitalPricing.NettingSets(none, none.Trades).Select(s => s.Id));
    }

    [Fact] // ADR-0013 §3, §5: each side is priced on its own, and its trace reconciles from capital to each trade
    public void Each_side_is_priced_on_its_own_and_its_trace_reconciles()
    {
        var p = Portfolio();
        var before = SpotCapitalPricing.Price(p, SpotCapitalPricing.Before, "run-1/xva", Simulated);
        var after = SpotCapitalPricing.Price(p, SpotCapitalPricing.After, "run-1/xva", Simulated);
        Assert.Equal((3, 4), (before.Trades, after.Trades));
        foreach (var side in new[] { before, after })
        {
            Assert.Null(side.Refused);
            var trace = side.Trace!;
            var evaluator = new TraceEvaluator(new TraceIndex(trace), SpotCapitalTrace.Catalog);
            Assert.All(trace.Terms, t => Assert.True(evaluator.Check(t).Status is CheckStatus.Matches or CheckStatus.NotApplicable, $"{t.Id}: {evaluator.Check(t).Reason}"));
            Assert.All(trace.Leaves, row => Assert.Empty(evaluator.CheckRow(row)));
            Assert.Equal(("run-1/xva", "2026-11-06", "JPY", ExecutionMode.Simulated), (trace.RequestId, trace.ValuationDate, trace.ReportingCurrency, trace.Provenance!.ExecutionMode));
            Assert.Equal(side.Capital!.Capital, trace.Terms.Single(t => t.Id == "capital").Value!.GetValue<double>());
        }
        // Three ratings: 20%, 30% and 30%; the higher of the two lowest applies (CRE21.11).
        Assert.Equal(0.30, after.Capital!.RiskWeight.RiskWeight);
        // CSA-B's three disputes double its ten days (CRE52.51).
        Assert.Equal(20, after.Capital.Exposure.NettingSets.Single(n => n.Id == "NS-001 CSA-B").Mpor!.Days);
        Assert.NotEqual(before.Capital!.Capital, after.Capital.Capital);
        // The new trade is valued at market, and says so; an existing trade's value is its official PV. A margined
        // netting set writes V under both of its bases.
        var leaves = after.Trace!.Leaves.Where(r => r["leafSet"]!.GetValue<string>() == "saccr.mtm")
            .GroupBy(r => r["tradeId"]!.GetValue<string>()).ToDictionary(g => g.Key, g => g.First());
        Assert.Equal((0.0, "new trade, at market (simulated)"), (leaves["N1"]["marketValue"]!.GetValue<double>(), leaves["N1"]["valueSource"]!.GetValue<string>()));
        Assert.Equal((-50_000 * 150.0, "official PV"), (leaves["E2"]["marketValue"]!.GetValue<double>(), leaves["E2"]["valueSource"]!.GetValue<string>()));
    }

    [Fact] // ADR-0011 §4, ADR-0013 §1: a side with no trade is not priced, never priced as 0, and the other side stands alone
    public void A_side_without_trades_is_not_priced()
    {
        var p = Portfolio([.. new[] { E1, E2, E3, N1 }.Select(t => t with { Before = false })]);
        var before = SpotCapitalPricing.Price(p, SpotCapitalPricing.Before, "run-1/xva", Simulated);
        Assert.Equal(new SideCapital(SpotCapitalPricing.Before, 0), before);
        var after = SpotCapitalPricing.Price(p, SpotCapitalPricing.After, "run-1/xva", Simulated);
        Assert.NotNull(after.Trace);
    }

    [Fact] // ADR-0011 §1, §4: a side priced and not computed writes its root unavailable, so it reads as unavailable, never as not priced
    public void A_side_that_could_not_be_computed_reads_as_unavailable_with_why()
    {
        // A product SA-CCR does not know, on Before alone.
        var p = Portfolio([E1, E2, E3 with { After = false, Product = "Swaption" }, N1]);
        var before = SpotCapitalPricing.Price(p, SpotCapitalPricing.Before, "run-1/xva", Simulated);
        var after = SpotCapitalPricing.Price(p, SpotCapitalPricing.After, "run-1/xva", Simulated);
        Assert.Null(before.Capital);
        Assert.Contains("a Swaption is not a product", before.Refused);

        var root = new TraceReader(SpotCapitalTrace.Catalog, before.Trace, after.Trace).Open("capital")!;

        Assert.Equal((TermState.Unavailable, before.Refused), (root.Term.Before!.State, root.Term.Before.Reason));
        Assert.Equal(TermState.Value, root.Term.After!.State);
        Assert.Equal(DiffState.Unavailable, root.Term.Diff!.State);
        Assert.Contains("a Swaption is not a product", root.Term.Diff.Reason);
        // Below it, Before says why it has nothing, rather than that nothing contributes.
        Assert.Equal(TermState.Unavailable, Assert.Single(root.Children).Before!.State);
    }

    [Fact] // ADR-0013 §6: a CSA whose margining SA-CCR cannot read refuses the measure by name
    public void A_CSA_that_does_not_say_who_posts_is_refused_rather_than_assumed()
    {
        var oneWay = SpotCapitalPricing.Price(Portfolio(csas: [CsaA, CsaB with { OneWay = true }]), SpotCapitalPricing.After, "run-1/xva", Simulated);
        Assert.Equal((4, null), (oneWay.Trades, oneWay.Capital));
        Assert.Equal("CSA-B is one-way, and its terms do not say which party posts, so its margined exposure cannot be decided.", oneWay.Refused);
        Assert.Contains("CSA-A exchanges no collateral", SpotCapitalPricing.MarginingProblem(CsaA with { Exchange = false }));
        Assert.Contains("CSA-A margins quarterly", SpotCapitalPricing.MarginingProblem(CsaA with { Frequency = "quarterly" }));
        Assert.Null(SpotCapitalPricing.MarginingProblem(CsaA with { Frequency = "weekly" }));
        // A counterparty the risk weight cannot weigh is refused by name too.
        var unrated = Portfolio() with { Facts = Bank with { Ratings = [] } };
        Assert.Contains("SCRA", SpotCapitalPricing.Price(unrated, SpotCapitalPricing.After, "run-1/xva", Simulated).Refused);
    }

    [Theory] // CRE22.49: the sovereign rows by grade and residual maturity, at ten business days
    [InlineData("AA", 0.5, 0.005)]
    [InlineData("AA-", 3, 0.02)]
    [InlineData("AAA", 5, 0.02)]
    [InlineData("AA", 7, 0.04)]
    [InlineData("A+", 1, 0.01)]
    [InlineData("BBB-", 4, 0.03)]
    [InlineData("A", 12, 0.06)]
    [InlineData("BB", 2, 0.15)]
    public void A_sovereign_bond_takes_its_supervisory_haircut(string grade, double years, double haircut) =>
        Assert.Equal(haircut, SpotCapitalPricing.Haircut(new("B", "CSA-A", CollateralDirection.Received, CollateralPurpose.VariationMargin, "sovereignBond", "USD", 1, IssuerGrade: grade, ResidualYears: years)));

    [Fact] // CRE22.49: cash takes none, and collateral the table does not hold is refused, never given a haircut
    public void Collateral_outside_the_haircut_table_is_refused()
    {
        CapitalCollateral Of(string type, string? grade = null, double? years = null) =>
            new("C-9", "CSA-A", CollateralDirection.Received, CollateralPurpose.VariationMargin, type, "JPY", 1, IssuerGrade: grade, ResidualYears: years);
        Assert.Equal(0, SpotCapitalPricing.Haircut(Of("cash")));
        Assert.Contains("equity collateral", Assert.Throws<SaCcrRefused>(() => SpotCapitalPricing.Haircut(Of("equity"))).Message);
        Assert.Contains("residual maturity is not given", Assert.Throws<SaCcrRefused>(() => SpotCapitalPricing.Haircut(Of("sovereignBond", "AA"))).Message);
        Assert.Contains("B+ is not eligible", Assert.Throws<SaCcrRefused>(() => SpotCapitalPricing.Haircut(Of("sovereignBond", "B+", 2))).Message);
    }

    [Fact] // ADR-0013 §5: the risk weight reads the longest original maturity and whether every currency is local
    public void The_risk_weight_reads_the_sides_longest_trade_and_its_currencies()
    {
        var p = Portfolio();
        var after = SpotCapitalPricing.ExposureFacts(p, SpotCapitalPricing.On(p, SpotCapitalPricing.After));
        // E1 and N1 both run five years; the tie goes to the first by id, the same every time.
        Assert.Equal((new OriginalMaturity("E1", new(2025, 3, 3), new(2030, 3, 3)), false), (after.Longest, after.InLocalCurrency));
        Assert.True(SpotCapitalPricing.ExposureFacts(p, [E1, N1]).InLocalCurrency);
        // An FX forward is in both its currencies: one bought in yen against dollars is not in yen alone.
        Assert.False(SpotCapitalPricing.ExposureFacts(p, [E1, E3 with { Currency = "JPY", CounterCurrency = "USD" }]).InLocalCurrency);
        Assert.Contains("no local currency is known for its country of incorporation XX",
            Assert.Throws<RiskWeightRefused>(() => SpotCapitalPricing.ExposureFacts(p with { Facts = Bank with { CountryOfIncorporation = "XX" } }, [E1])).Message);
    }

    [Fact] // ADR-0013 §1: the capital profile is the spot rules on the trades still alive at each date, from today's figure
    public void The_capital_profile_runs_off_with_the_trades()
    {
        var p = Portfolio();
        var after = SpotCapitalPricing.Price(p, SpotCapitalPricing.After, "run-1/xva", Simulated);
        var profile = SpotCapitalPricing.Profile(p, SpotCapitalPricing.After, after.Capital!);
        Assert.Equal([0, 0.25, 0.5, 1, 2, 3, 5], profile.Select(x => x.Years));
        // E3 settles in February 2027, E2 in June 2027, E1 in March 2030; N1 runs to November 2031.
        Assert.Equal([4, 3, 3, 2, 2, 2, 1], profile.Select(x => x.Trades));
        Assert.Equal(after.Capital!.Capital, profile[0].Capital);
        Assert.Equal(new DateOnly(2027, 11, 6), profile[3].Date);
        // A later point is the spot rules on what is left: N1 alone, rolled five years on.
        var left = Portfolio([N1]);
        var alone = SaCcr.Calculate(SpotCapitalPricing.NettingSets(left, left.Trades, 5), left.Settings.SaCcr);
        Assert.Equal(alone.Ead, profile[6].Ead);
        Assert.Equal(0.08 * 0.30 * alone.Ead, profile[6].Capital, 9);
    }
}
