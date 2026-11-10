using System.Text.Json;
using System.Text.Json.Nodes;
using Qube.Composition.Application;
using Qube.Composition.Application.Traces;
using Qube.Composition.Contracts;
using Qube.Samples.Ccr;

namespace Qube.Composition.Tests;

/// <summary>
/// ADR-0013 §4, §5: the reference implementation writes spot capital's trace, from K down to each trade's row, with the
/// kinds of method sa-ccr 2023-01-01. Every term it writes reconciles against its kind's formula, so the figures here
/// are not recomputed: the kernel checks each against its operands.
/// </summary>
public class CcrTraceTests
{
    static readonly SaCcrSettings Basel = new("sa-ccr · 2023-01-01");
    static readonly RiskWeightSettings Ratings = new(ExternalRatingsAllowed: true, Version: "basel-iii · 2023-01-01");
    static readonly CapitalRatio Kappa = new(0.08, "ccr.capitalRatio", "1");
    static readonly LinearTerms Long = new(Long: true);
    static readonly LinearTerms Short = new(Long: false);

    static SaCcrExposure Ir(string trade, string currency, double notional, double start, double end, DeltaTerms delta,
        HedgingSetType type = HedgingSetType.Regular, string? hedgingSet = null) =>
        new(trade, "", AssetClass.InterestRate, hedgingSet ?? currency, delta, end, notional, StartYears: start, EndYears: end, HedgingSetType: type);

    static SaCcrExposure Fx(string trade, string pair, double notional, double maturity, DeltaTerms delta) =>
        new(trade, "", AssetClass.ForeignExchange, pair, delta, maturity, notional);

    static SaCcrExposure Cds(string trade, string entity, CreditSubclass subclass, double notional, double end, DeltaTerms delta) =>
        new(trade, "", AssetClass.Credit, "credit", delta, end, notional, StartYears: 0, EndYears: end, RiskFactor: entity, CreditSubclass: subclass);

    static SaCcrExposure Commodity(string trade, string set, string type, double price, double units, double maturity, DeltaTerms delta) =>
        new(trade, "", AssetClass.Commodity, set, delta, maturity, price, Units: units, RiskFactor: type);

    static SaCcrExposure Equity(string trade, string entity, double price, double units, double maturity, DeltaTerms delta,
        HedgingSetType type = HedgingSetType.Regular, string hedgingSet = "equity") =>
        new(trade, "", AssetClass.Equity, hedgingSet, delta, maturity, price, Units: units, RiskFactor: entity, HedgingSetType: type);

    static SaCcrTrade Trade(string id, double value, params SaCcrExposure[] exposures) => new(id, value, exposures);

    // The BCBS interest rate netting set, and a new seven-year pay-fixed swap that After adds to it.
    static SaCcrTrade[] InterestRateTrades =>
    [
        Trade("T-1", 30, Ir("T-1", "USD", 10_000, 0, 10, Long)),
        Trade("T-2", -20, Ir("T-2", "USD", 10_000, 0, 4, Short)),
        Trade("T-3", 50, Ir("T-3", "EUR", 5_000, 1, 11, new OptionTerms(OptionType.Put, Bought: true, Underlying: 0.06, Strike: 0.05, ExerciseYears: 1))),
    ];

    static readonly SaCcrTrade NewSwap = Trade("T-NEW-01", -3, Ir("T-NEW-01", "USD", 4_000, 0, 7, Short));

    static SaCcrNettingSet InterestRate(params SaCcrTrade[] more) => new("NS-002", "USD", [.. InterestRateTrades, .. more]);

    static CounterpartyRiskFacts Bank(params ExternalRating[] ratings) =>
        new("CP-0042", "R-7", InstitutionType.Bank, Ratings: ratings, CountryOfIncorporation: "GB",
            SovereignOfIncorporation: new(Ratings: [new("Agency 1", "AA")]), DueDiligenceBucketsUp: 0);

    static readonly CounterpartyRiskFacts ThreeRatings = Bank(new("Agency 1", "AA-"), new("Agency 2", "A+"), new("Agency 3", "BBB+"));

    // Under SCRA a foreign-currency exposure is floored at a B+ sovereign's 100%.
    static readonly CounterpartyRiskFacts FlooredBank = Bank() with
    {
        Scra = new ScraAssessment(ScraGrade.A),
        SovereignOfIncorporation = new(Ratings: [new("Agency 1", "B+")]),
    };

    static readonly CounterpartyRiskFacts UnratedCorporate = new("CP-0077", "R-9", InstitutionType.Corporate, Sme: false, DueDiligenceBucketsUp: 0);

    static ExposureFacts Exposure(bool local = true) => new(new OriginalMaturity("T-1", new(2026, 11, 9), new(2036, 11, 9)), local);

    static SpotCapital Capital(IReadOnlyList<SaCcrNettingSet> sets, CounterpartyRiskFacts? counterparty = null, ExposureFacts? exposure = null,
        SaCcrSettings? settings = null) =>
        SpotCapital.Of(SaCcr.Calculate(sets, settings ?? Basel), RiskWeights.Resolve(counterparty ?? ThreeRatings, exposure ?? Exposure(), Ratings), Kappa);

    static readonly SpotCapitalTraceHeader Header = new(new TraceSubject("counterparty", "CP-0042", "R-7"), "USD", new TraceProducer("reference sa-ccr", "1"),
        ValuationDate: "2026-11-09", Provenance: new DatasetProvenance(ExecutionMode.Simulated, DataOrigin.Synthetic, "inv-1"),
        TradeInstanceIds: new Dictionary<string, string> { ["T-1"] = "ti-1", ["T-NEW-01"] = "ti-new" });

    static CalculationTrace Trace(SpotCapital capital) => SpotCapitalTrace.Write(capital, Header);

    static double Number(TraceTerm term) => term.Value!.GetValue<double>();

    // The portfolios the reconciliation runs over: each rule of ADR-0013 §3 that shapes the tree.
    public static TheoryData<string> Portfolios => new()
    {
        "bcbs interest rate", "bcbs credit", "bcbs commodity", "several asset classes", "margined fx with collateral",
        "threshold, mta and nica", "haircuts and segregated collateral", "basis, volatility and shifted options",
        "tranche", "no offset across buckets", "not legally enforceable", "floored risk weight", "unrated corporate",
        "three buckets, a short fx and a ten-day trade",
    };

    static SpotCapital Portfolio(string name) => name switch
    {
        "bcbs interest rate" => Capital([InterestRate()]),
        "bcbs credit" => Capital([new("NS-2", "USD",
        [
            Trade("T-4", 20, Cds("T-4", "Firm A", CreditSubclass.AA, 10_000, 3, Long)),
            Trade("T-5", -40, Cds("T-5", "Firm B", CreditSubclass.BBB, 10_000, 6, Short)),
            Trade("T-6", 0, Cds("T-6", "CDX.IG 5y", CreditSubclass.IndexInvestmentGrade, 10_000, 5, Long)),
        ])]),
        "bcbs commodity" => Capital([new("NS-3", "USD",
        [
            Trade("T-7", -50, Commodity("T-7", "energy", "crude oil", 1, 10_000, 0.75, Long)),
            Trade("T-8", -30, Commodity("T-8", "energy", "crude oil", 1, 20_000, 2, Short)),
            Trade("T-9", 100, Commodity("T-9", "metals", "silver", 1, 10_000, 5, Long)),
        ])]),
        "several asset classes" => Capital([InterestRate(NewSwap, Trade("T-30", 4, Fx("T-30", "EURUSD", 3_240, 0.5, Long)),
            Trade("T-31", 1, Equity("T-31", "Name X", 40, 100, 1, Long)), Trade("T-32", 0, Cds("T-32", "Firm A", CreditSubclass.A, 1_000, 3, Short)))]),
        "margined fx with collateral" => Capital([new("NS-5", "USD",
            [Trade("T-10", 12, Fx("T-10", "EURUSD", 3_240, 0.5, Long)), Trade("T-11", -4, Fx("T-11", "EURUSD", 1_080, 1, Short))],
            new Margining("CSA-B", 1, 0, 0.5),
            [new CollateralBalance("VM-1", CollateralDirection.Received, CollateralPurpose.VariationMargin, "cash", "USD", 6, 0)])]),
        "threshold, mta and nica" => Capital([new("NS-8", "USD", [Trade("T-13", -30, Ir("T-13", "USD", 1_000, 0, 5, Long))],
            new Margining("CSA-B", 1, 50, 5),
            [
                new CollateralBalance("VM-2", CollateralDirection.Posted, CollateralPurpose.VariationMargin, "cash", "USD", 20, 0),
                new CollateralBalance("IA-1", CollateralDirection.Received, CollateralPurpose.IndependentAmount, "cash", "USD", 15, 0),
            ])]),
        "haircuts and segregated collateral" => Capital([new("NS-9", "USD", [Trade("T-14", 0, Ir("T-14", "USD", 1_000, 0, 0.5, Long))],
            new Margining("CSA-C", 3, 0, 0, IlliquidCollateralOrHardToReplace: true, DisputesLongerThanMpor: 3),
            [
                new("B-1", CollateralDirection.Received, CollateralPurpose.VariationMargin, "sovereign bond", "EUR", 100, 0.02),
                new("B-2", CollateralDirection.Posted, CollateralPurpose.VariationMargin, "cash", "USD", 50, 0, Segregated: true),
                new("B-3", CollateralDirection.Posted, CollateralPurpose.VariationMargin, "cash", "USD", 10, 0),
                new("B-4", CollateralDirection.Posted, CollateralPurpose.VariationMargin, "corporate bond", "EUR", 30, 0.04),
            ])]),
        "basis, volatility and shifted options" => Capital([new("NS-11", "USD",
        [
            Trade("T-19", 0, Ir("T-19", "USD", 10_000, 0, 5, Long, HedgingSetType.Basis, "USD SOFR/TERM-3M")),
            Trade("T-20", 0, Equity("T-20", "Name X", 0.04, 100_000, 1, Long, HedgingSetType.Volatility, "equity volatility")),
            Trade("T-21", 2, Ir("T-21", "EUR", 1_000, 1, 6, new OptionTerms(OptionType.Call, true, Underlying: -0.002, Strike: 0.001, ExerciseYears: 1, Shift: 0.01))),
            Trade("T-22", -1, Ir("T-22", "EUR", 1_000, 1, 6, new OptionTerms(OptionType.Put, false, Underlying: 0.03, Strike: 0.02, ExerciseYears: 1))),
        ])]),
        "tranche" => Capital([new("NS-12", "USD",
            [Trade("T-23", 5, new SaCcrExposure("T-23", "", AssetClass.Credit, "credit", new TrancheTerms(0.03, 0.07, true), 5, 1_000,
                StartYears: 0, EndYears: 5, RiskFactor: "CDX.IG 5y 3-7%", CreditSubclass: CreditSubclass.IndexInvestmentGrade))])]),
        "no offset across buckets" => Capital([InterestRate()], settings: Basel with { IrOffsetAcrossBuckets = false }),
        "not legally enforceable" => Capital([new SaCcrNettingSet("NS-20", "USD", InterestRateTrades[..2], null,
            [new CollateralBalance("VM-4", CollateralDirection.Received, CollateralPurpose.VariationMargin, "cash", "USD", 10, 0)], LegallyEnforceable: false)]),
        "floored risk weight" => Capital([InterestRate()], FlooredBank, Exposure(local: false)),
        "unrated corporate" => Capital([InterestRate()], UnratedCorporate),
        "three buckets, a short fx and a ten-day trade" => Capital([new("NS-13", "USD",
        [
            Trade("T-40", 1, Ir("T-40", "USD", 2_000, 0, 0.5, Long)),
            Trade("T-41", 2, Ir("T-41", "USD", 3_000, 0, 3, Short)),
            Trade("T-42", -1, Ir("T-42", "USD", 1_000, 0, 8, Long)),
            Trade("T-43", -2, Fx("T-43", "USDJPY", 1_500, 0.02, Short)),
        ])]),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Fact] // ADR-0011 §2, ADR-0013 §4: the catalogue's TeX marks exactly what each expression reads, and it registers
    public void The_catalogue_has_no_problems_and_registers()
    {
        Assert.Empty(TraceCatalogs.Problems(SpotCapitalTrace.Catalog));
        var registry = new CompositionRegistry([new CatalogModule()]);
        Assert.Same(SpotCapitalTrace.Catalog, registry.FindTraceCatalog("sa-ccr", "2023-01-01"));
    }

    sealed class CatalogModule : IQubeModule
    {
        public void Register(CompositionRegistryBuilder builder) => builder.AddTraceCatalog(SpotCapitalTrace.Catalog);
    }

    [Theory] // ADR-0011 §2, ADR-0013 §4: every term the reference implementation writes reconciles, and so does every row
    [MemberData(nameof(Portfolios))]
    public void Every_term_and_every_row_reconciles(string portfolio)
    {
        var capital = Portfolio(portfolio);
        var trace = Trace(capital);
        var index = new TraceIndex(trace);
        var evaluator = new TraceEvaluator(index, SpotCapitalTrace.Catalog);
        foreach (var term in trace.Terms)
        {
            Assert.NotNull(evaluator.KindOf(term.Kind));
            var check = evaluator.Check(term);
            Assert.True(check.Status is CheckStatus.Matches or CheckStatus.NotApplicable, $"{term.Id} ({term.Kind}): {check.Status}, {check.Reason}");
            if (term.State == TermState.Value && evaluator.KindOf(term.Kind)!.Expression is not null)
                Assert.Equal(CheckStatus.Matches, check.Status);
        }
        foreach (var row in trace.Leaves)
        {
            Assert.NotNull(evaluator.LeafSetOf(row["leafSet"]!.GetValue<string>()));
            Assert.Empty(evaluator.CheckRow(row));
        }
        Assert.Equal(capital.Capital, Number(index.Find("capital")!));
        Assert.Equal(capital.Exposure.Ead, Number(index.Find("ead")!));
        Assert.Equal(capital.RiskWeight.RiskWeight, Number(index.Find("rw")!));
        foreach (var ns in capital.Exposure.NettingSets)
            Assert.Equal(ns.Ead, Number(index.Find(index.Children("ead").Single(t => t.Key!["nettingSet"] == ns.Id).Id)!));
    }

    [Fact] // ADR-0013 §4: ids run from the netting set to the maturity bucket, the header says what the trace is
    public void The_trace_is_written_from_capital_down_to_each_trade()
    {
        var trace = Trace(Portfolio("bcbs interest rate"));
        var index = new TraceIndex(trace);
        Assert.Equal(("ccr.spotCapital", "sa-ccr", "2023-01-01", "trade"), (trace.Measure, trace.Method.Id, trace.Method.Version, trace.Depth));
        Assert.Equal(["capital"], trace.Roots);
        Assert.Equal(ExecutionMode.Simulated, trace.Provenance!.ExecutionMode);
        Assert.Equal(["rwa"], index.Children("capital").Select(t => t.Id));
        Assert.Equal(["rw", "ead"], index.Children("rwa").Select(t => t.Id));
        Assert.Equal(["NS-002/rc", "NS-002/pfe"], index.Children("NS-002").Select(t => t.Id));
        Assert.Equal(["NS-002/rc/v", "NS-002/rc/c"], index.Children("NS-002/rc").Select(t => t.Id));
        Assert.Equal(TermState.Absent, index.Find("NS-002/rc/c")!.State);
        Assert.Equal(["ir", "fx", "credit", "equity", "commodity"], index.Children("NS-002/pfe/addon").Select(t => t.Id.Split('/')[^1]));
        Assert.Equal([TermState.Value, TermState.Absent, TermState.Absent, TermState.Absent, TermState.Absent],
            index.Children("NS-002/pfe/addon").Select(t => t.State));

        var b3 = index.Find("NS-002/pfe/addon/ir/USD/en/b3")!;
        Assert.Equal(("saccr.irBucket", "over 5y"), (b3.Kind, b3.Key!["bucket"]));
        Assert.Equal(("under 1y", "1-5y"), (index.Find("NS-002/pfe/addon/ir/USD/en/b1")!.Key!["bucket"], index.Find("NS-002/pfe/addon/ir/USD/en/b2")!.Key!["bucket"]));
        Assert.Equal(0, index.Find("NS-002/rc")!.Governed!.GetValue<int>()); // V − C = 60
        Assert.Equal(0, index.Find("NS-002/pfe/multiplier")!.Governed!.GetValue<int>()); // V > 0 takes the multiplier to 1
        Assert.Equal(TermState.Absent, index.Find("NS-002/pfe/addon/ir/USD/en/b1")!.State);
        Assert.Equal(new TermLeaves("saccr.irTrade", 1), b3.Leaves);
        var t1 = index.LeafRows(b3.Id).Single();
        Assert.Equal(("T-1", "ti-1"), (t1["tradeId"]!.GetValue<string>(), t1["tradeInstanceId"]!.GetValue<string>()));
        Assert.Equal(3, index.LeafRows("NS-002/rc/v").Count);

        var swaption = index.LeafRows("NS-002/pfe/addon/ir/EUR/en/b3").Single();
        Assert.Equal(("put", 1.0, -1.0), (swaption["optionType"]!.GetValue<string>(), swaption["side"]!.GetValue<double>(), swaption["callPut"]!.GetValue<double>()));
        Assert.Equal(-0.2693952177105327, swaption["delta"]!.GetValue<double>(), 1e-12);

        var alpha = trace.Parameters.Single(p => p.Name == "alpha");
        Assert.Equal((ParameterSource.Regulation, 1.4, (string?)null), (alpha.Source, alpha.Value, alpha.Scope));
        var sf = new TraceIndex(trace).Parameter("SF", "NS-002/pfe/addon/ir/USD")!;
        Assert.Equal((0.005, "NS-002/pfe/addon/ir/USD"), (sf.Value, sf.Scope));
        var kappa = trace.Parameters.Single(p => p.Name == "kappa");
        Assert.Equal((ParameterSource.Configuration, new ParameterConfig("ccr.capitalRatio", "1")), (kappa.Source, kappa.Config));
    }

    [Fact] // ADR-0013 §4: a margined netting set has its margined and unmargined figures beneath it, and says which governs
    public void A_margined_netting_set_has_both_figures_and_its_mpor()
    {
        var trace = Trace(Portfolio("threshold, mta and nica"));
        var index = new TraceIndex(trace);
        var ns = index.Find("NS-8")!;
        Assert.Equal(("saccr.marginedEad", "u"), (ns.Kind, ns.Governed!.GetValue<string>()));
        Assert.Equal(["NS-8/m", "NS-8/u"], index.Children("NS-8").Select(t => t.Id));
        Assert.Equal(["NS-8/m/rc/v", "NS-8/m/rc/c", "NS-8/m/rc/nica"], index.Children("NS-8/m/rc").Select(t => t.Id));
        Assert.Equal(1, index.Find("NS-8/m/rc")!.Governed!.GetValue<int>()); // TH + MTA − NICA = 40
        var threshold = index.Parameter("TH", "NS-8/m/rc")!;
        Assert.Equal((50.0, ParameterSource.Input, new ParameterInput("csa/CSA-B", "threshold")), (threshold.Value!.Value, threshold.Source, threshold.Input));
        var mpor = index.Parameter("mpor", "NS-8/m/pfe")!;
        Assert.Equal((10.0, ParameterSource.Derived, "NS-8"), (mpor.Value!.Value, mpor.Source, mpor.Scope));
        Assert.NotEmpty(mpor.Because!);
        Assert.Equal("saccr.irTradeMargined", index.Find("NS-8/m/pfe/addon/ir/USD/en/b2")!.Leaves!.LeafSet);
        Assert.Equal("saccr.irTrade", index.Find("NS-8/u/pfe/addon/ir/USD/en/b2")!.Leaves!.LeafSet);
        Assert.Equal(["IA-1"], index.LeafRows("NS-8/m/rc/nica").Select(r => r["balanceId"]!.GetValue<string>()));
        Assert.Equal(["VM-2", "IA-1"], index.LeafRows("NS-8/m/rc/c").Select(r => r["balanceId"]!.GetValue<string>()));

        var credit = new TraceIndex(Trace(Portfolio("bcbs credit")));
        Assert.Equal(1, credit.Find("NS-2/rc")!.Governed!.GetValue<int>()); // V − C = −20, so RC is 0
        Assert.Equal(1, credit.Find("NS-2/pfe/multiplier")!.Governed!.GetValue<int>()); // below 1

        var segregated = new TraceIndex(Trace(Portfolio("haircuts and segregated collateral")));
        Assert.Equal(["B-1", "B-3", "B-4"], segregated.LeafRows("NS-9/m/rc/c").Select(r => r["balanceId"]!.GetValue<string>()));
        Assert.Contains(segregated.Find("NS-9/m/rc/c")!.Because!, b => b.Contains("B-2", StringComparison.Ordinal));
    }

    [Fact] // ADR-0013 §5: every step of the risk weight is a term, with its outcome, inputs, reasons and rule, and the table it read
    public void The_risk_weight_is_written_as_its_decision()
    {
        var trace = Trace(Portfolio("bcbs interest rate"));
        var index = new TraceIndex(trace);
        Assert.Equal(["rw/class", "rw/approach", "rw/ratings", "rw/rating", "rw/shortTerm", "rw/dueDiligence", "rw/bucket", "rw/table", "rw/floor"],
            index.Children("rw").Select(t => t.Id));
        var rating = index.Find("rw/rating")!;
        Assert.Equal(("A+ (Agency 2)", "CRE21.9-11"), (rating.Value!.GetValue<string>(), rating.Citation));
        Assert.Contains("two lowest", rating.Because!.Single(), StringComparison.Ordinal);
        var type = index.Find("rw/class")!.Inputs!["institutionType"]!.AsObject();
        Assert.Equal(("Bank", "input"), (type["value"]!.GetValue<string>(), type["source"]!.GetValue<string>()));
        Assert.Equal("A+ to A-", index.Find("rw/bucket")!.Value!.GetValue<string>());
        var table = index.Find("rw/table")!;
        Assert.Equal((0.30, "CRE20.18"), (Number(table), table.Citation));
        Assert.Equal(TermState.Absent, index.Find("rw/floor")!.State);
        Assert.Equal("table", index.Find("rw")!.Governed!.GetValue<string>());
        var lookup = index.Parameter("rwTable", "rw/table")!;
        Assert.Equal(["Base"], lookup.Matrix!.Columns);
        Assert.Equal("CRE20.18", lookup.Citation);

        var floored = new TraceIndex(Trace(Portfolio("floored risk weight")));
        Assert.Equal(("floor", 1.00), (floored.Find("rw")!.Governed!.GetValue<string>(), Number(floored.Find("rw")!)));
        Assert.Equal(1.00, Number(floored.Find("rw/floor")!));
        Assert.Equal(0.40, Number(floored.Find("rw/table")!));
    }

    [Fact] // ADR-0011 §1: the trace crosses the wire as written, and still reconciles
    public void The_trace_round_trips_through_the_wire_options()
    {
        var trace = Trace(Portfolio("several asset classes"));
        var json = JsonSerializer.Serialize(trace, WireJson.Options);
        var back = JsonSerializer.Deserialize<CalculationTrace>(json, WireJson.Options)!;
        Assert.Equal(json, JsonSerializer.Serialize(back, WireJson.Options));
        var evaluator = new TraceEvaluator(new TraceIndex(back), SpotCapitalTrace.Catalog);
        Assert.All(back.Terms, t => Assert.NotEqual(CheckStatus.Inconsistent, evaluator.Check(t).Status));
        Assert.Equal(CheckStatus.Matches, evaluator.Check(back.Terms.Single(t => t.Id == "capital")).Status);
        var catalogue = JsonSerializer.Serialize(SpotCapitalTrace.Catalog, WireJson.Options);
        Assert.Equal(catalogue, JsonSerializer.Serialize(JsonSerializer.Deserialize<TraceCatalog>(catalogue, WireJson.Options), WireJson.Options));
    }

    [Fact] // ADR-0011 §4, ADR-0013 §1: Before and After pair by id; a trade only in After has no Before row
    public void Before_and_after_pair_by_id_and_a_new_trade_is_absent_before()
    {
        var before = Trace(Capital([InterestRate()]));
        var after = Trace(Capital([InterestRate(NewSwap)]));
        var reader = new TraceReader(SpotCapitalTrace.Catalog, before, after);
        var capital = reader.Read("capital")!;
        Assert.Equal(Number(after.Terms[0]) - Number(before.Terms[0]), capital.Diff!.Value!.Value, 1e-12);
        Assert.Equal(CheckStatus.Matches, capital.After!.Check.Status);
        var b3 = reader.Read("NS-002/pfe/addon/ir/USD/en/b3")!;
        Assert.Equal(DiffState.Value, b3.Diff!.State);
        var rows = reader.Leaves("NS-002/pfe/addon/ir/USD/en/b3").Rows;
        var added = rows.Single(r => r.Entity == "T-NEW-01");
        Assert.Null(added.Before);
        Assert.NotNull(added.After);
        Assert.NotNull(rows.Single(r => r.Entity == "T-1").Before);
        Assert.Equal("A+ (Agency 2)", reader.Read("rw/rating")!.Before!.Value!.GetValue<string>());
        Assert.Equal("CRE21.9-11", reader.Read("rw/rating")!.After!.Citation);

        var alone = new TraceReader(SpotCapitalTrace.Catalog, null, after);
        Assert.Null(alone.Read("capital")!.Diff);
        Assert.Equal(["rwa"], alone.Open("capital")!.Children.Select(c => c.Id));
        Assert.Equal(2, alone.Leaves("NS-002/pfe/addon/ir/USD/en/b3").Total);
    }
}
