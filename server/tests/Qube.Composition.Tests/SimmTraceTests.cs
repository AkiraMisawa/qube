using System.Text.Json.Nodes;
using Qube.Composition.Application.Traces;
using Qube.Composition.Contracts;
using Qube.Samples.Simm;

namespace Qube.Composition.Tests;

// The SIMM engine's trace (ADR-0012 §6) in ADR-0011's terms: the kinds it registers, and the values it writes as it
// computes. Every term must reconcile against its kind, so the formulas the page draws are the ones that gave the figure.
public class SimmTraceTests
{
    static readonly SimmParameters Demo = SimmParameters.Demo;
    static readonly TraceCatalog Catalog = SimmTraceCatalog.For(Demo);

    static ImCsa Csa(ImDirection direction, string[] regimes, IEnumerable<CrifLine> crif) =>
        new(direction == ImDirection.Collect ? "IMR-1" : "IMP-1", direction, regimes, [.. crif.Select(l => l.TradeId).Distinct()]);

    static CalculationTrace Sample() =>
        SimmEngine.Calculate(SimmSampleTests.Crif(), [Csa(ImDirection.Collect, ["JFSA", "CFTC"], SimmSampleTests.Crif()),
            Csa(ImDirection.Post, ["JFSA", "EMIR"], SimmSampleTests.Crif())], Demo).Trace;

    static CrifLine Ir(string line, string trade, string currency, string tenor, double usd) =>
        new(line, trade, CrifRiskType.IrCurve, currency, tenor, "OIS", usd, "USD", usd, ["JFSA"], ["JFSA"]);

    [Fact] // ADR-0012 §6, ADR-0011 §2: the kinds register, and every term and leaf row the engine writes reconciles
    public void Every_term_the_engine_writes_reconciles_against_its_kind()
    {
        Assert.Empty(TraceCatalogs.Problems(Catalog));
        var cases = new[] { SimmEngineTests.Mixed, SimmEngineTests.ConcentratedFx }.Select(crif => SimmEngine.Calculate(crif, [Csa(ImDirection.Collect, ["JFSA"], crif)], Demo).Trace);
        foreach (var trace in cases.Prepend(Sample()))
        {
            var index = new TraceIndex(trace);
            var evaluator = new TraceEvaluator(index, Catalog);
            var valued = trace.Terms.Where(t => t.State == TermState.Value).ToList();
            Assert.NotEmpty(valued);
            foreach (var term in valued)
            {
                Assert.NotNull(evaluator.KindOf(term.Kind));
                var check = evaluator.Check(term);
                Assert.True(check.Status == CheckStatus.Matches, $"{term.Id} ({term.Kind}): {check.Status} {check.Reason}");
            }
            Assert.All(trace.Leaves, row => Assert.Empty(evaluator.CheckRow(row)));
            Assert.Equal(("simm.spotIm", "simm", Demo.Version, "USD"), (trace.Measure, trace.Method.Id, trace.Method.Version, trace.ReportingCurrency));
        }
    }

    [Fact] // ADR-0011 §1, ADR-0012 §1: one root per direction, marking the regime that governs; a direction with nothing in scope is absent
    public void Each_direction_is_a_root_that_marks_its_governing_regime()
    {
        var crif = new[] { Ir("L1", "T1", "JPY", "5y", 1_000) with { CollectRegulations = ["JFSA", "CFTC"] }, Ir("L2", "T2", "JPY", "10y", -500) };
        var trace = SimmEngine.Calculate(crif, [Csa(ImDirection.Collect, ["JFSA", "CFTC"], crif), new("IMP-1", ImDirection.Post, ["EMIR"], ["T1"])], Demo).Trace;
        var index = new TraceIndex(trace);

        Assert.Equal(["collect", "post"], trace.Roots);
        var collect = index.Find("collect")!;
        var regimes = index.Children("collect");
        Assert.Equal(["collect/JFSA", "collect/CFTC"], regimes.Select(r => r.Id));
        // CFTC holds L1 alone, 21,000; JFSA holds both, 13,939.0057: the largest, CFTC, governs
        Assert.Equal((21_000, 1), (collect.Value!.GetValue<double>(), collect.Governed!.GetValue<int>()));
        Assert.Equal((TermState.Absent, (JsonNode?)null), (index.Find("post")!.State, index.Find("post")!.Value));
        // Each regime says whether it governs, and the root is named as the page's Metrics name it.
        var reader = new TraceReader(SimmTraceCatalog.For(Demo), null, trace);
        Assert.Equal([false, true], regimes.Select(r => reader.Read(r.Id)!.After!.Governs!.Value));
        Assert.Equal(("IM-R · collect", "CFTC regime"), (reader.Read("collect")!.Label, reader.Read("collect/CFTC")!.Label));
    }

    [Fact] // ADR-0012 §6, simm.bucket: S_b sits beside K_b and marks the bound it took
    public void A_bucket_sum_marks_the_bound_it_took()
    {
        // As the engine's hand-worked case: JPY K = 30,857.8048 < ΣWS = 32,500, so S = K; USD's 10,000 is inside its bound
        var crif = new[] { Ir("L1", "T1", "JPY", "5y", 1_000), Ir("L2", "T2", "JPY", "10y", 500), Ir("L3", "T3", "USD", "5y", 200) };
        var trace = SimmEngine.Calculate(crif, [Csa(ImDirection.Collect, ["JFSA"], crif)], Demo).Trace;
        var index = new TraceIndex(trace);

        var delta = index.Find("collect/JFSA/RatesFX/IR/delta")!;
        Assert.Equal(35_175.9996, delta.Value!.GetValue<double>(), 3);
        var jpy = index.Find("collect/JFSA/RatesFX/IR/delta/JPY:S")!;
        Assert.Equal((30_857.8048, 2), (Math.Round(jpy.Value!.GetValue<double>(), 4), jpy.Governed!.GetValue<int>()));
        Assert.Equal("collect/JFSA/RatesFX/IR/delta/JPY", jpy.Slots!["k"]);
        var usd = index.Find("collect/JFSA/RatesFX/IR/delta/USD:S")!;
        Assert.Equal((10_000, 0), (usd.Value!.GetValue<double>(), usd.Governed!.GetValue<int>()));
    }

    [Fact] // ADR-0012 §6: a regime's allocation is its SIMM, and below it every term's children's allocations add up to its own
    public void Allocations_add_up_from_the_crif_line_to_the_regime()
    {
        var trace = Sample();
        var index = new TraceIndex(trace);

        foreach (var regime in trace.Terms.Where(t => t.Kind == "simm.regime"))
            Assert.Equal(regime.Value!.GetValue<double>(), regime.Allocation!.Value, 6);
        foreach (var term in trace.Terms.Where(t => t.Allocation is not null && t.Kind != "simm.factor"))
        {
            var children = index.Children(term.Id).Where(c => c.Allocation is not null).ToList();
            Assert.NotEmpty(children);
            Assert.Equal(term.Allocation!.Value, children.Sum(c => c.Allocation!.Value), 6);
        }
        foreach (var factor in trace.Terms.Where(t => t.Kind == "simm.factor"))
            Assert.Equal(factor.Allocation!.Value, index.LeafRows(factor.Id).Sum(r => r["contribution"]!.GetValue<double>()), 6);
        Assert.All(trace.Terms.Where(t => t.Allocation is not null), t => Assert.Equal("euler", t.Allocation!.Method));
    }

    [Fact] // ADR-0012 §6: the risk weight says why: the currency's volatility group, then the tenor, then the table
    public void A_risk_weight_says_why_it_is_what_it_is()
    {
        var crif = new[] { Ir("L1", "T1", "JPY", "5y", 1_000) };
        var trace = SimmEngine.Calculate(crif, [Csa(ImDirection.Collect, ["JFSA"], crif)], Demo).Trace;

        var rw = new TraceIndex(trace).Parameter("rw", "collect/JFSA/RatesFX/IR/delta/JPY/OIS 5y")!;
        Assert.Equal(21, rw.Value);
        Assert.Collection(rw.Because!, b => Assert.Contains("low volatility", b), b => Assert.Contains("5y", b), b => Assert.Contains(Demo.Version, b));
        var row = Assert.Single(trace.Leaves);
        Assert.Equal(("simm.crif", "collect/JFSA/RatesFX/IR/delta/JPY/OIS 5y", "L1", "T1"),
            (row["leafSet"]!.GetValue<string>(), row["parent"]!.GetValue<string>(), row["line"]!.GetValue<string>(), row["tradeId"]!.GetValue<string>()));
    }
}
