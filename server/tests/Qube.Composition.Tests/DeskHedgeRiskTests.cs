using Qube.Composition.Contracts;
using Qube.Samples.Desk;

namespace Qube.Composition.Tests;

/// <summary>
/// ADR-0019 §7: the first slice's hedge risk is a synthetic single-curve calculator, <c>Simulated</c>, on the book's
/// own inputs and keys. Its figures are checked against values worked by hand and against a bump of the curve.
/// </summary>
public class DeskHedgeRiskTests
{
    static readonly DateOnly Valuation = new(2026, 11, 13);

    static DeskResultSet Book(params (int Days, double Df)[] nodes) => new("tiny", new string('1', 64),
        new DeskManifest("alpha", "abc123", "synthetic/tiny", Valuation, "S0000", new Dictionary<string, string>(),
            new Dictionary<string, SensitivityPer> { ["zero_rate"] = SensitivityPer.Rate })
        { HedgeCurves = new Dictionary<string, string> { ["JPY"] = "JPY-OIS" } },
        [], [], [], [])
    {
        Curves = new Dictionary<string, DeskCurve>
        {
            ["JPY-OIS"] = new("JPY-OIS", [.. nodes.Select(n => new DeskCurveNode($"{n.Days}d", n.Days, n.Df))]),
        },
    };

    static (int, double)[] Flat(double zero, params int[] days) => [.. days.Select(d => (d, Math.Exp(-zero * d / 365.0)))];

    static HedgeSwap Swap(string end, double ratePercent, bool payFixed = true, double notional = 1_000_000, string currency = "JPY") =>
        new("H1", currency, notional, payFixed, DateOnly.ParseExact(end, "yyyy-MM-dd"), ratePercent);

    [Fact] // one period of exactly 365 days: value = N[(1 - DF) - K DF], and d/dz = N (1 + K) DF t, with t = 1
    public void A_one_year_payer_swap_is_worth_what_the_formula_says()
    {
        var df = Math.Exp(-0.02);
        var answer = new SyntheticHedgeRisk().Risk(Swap("2027-11-13", 1.0), Book((365, df)));

        Assert.Equal((SyntheticHedgeRisk.Name, ExecutionMode.Simulated), (answer.Provider, answer.Mode));
        Assert.Equal(1_000_000 * (1 - df - 0.01 * df), answer.Value, 6);
        var node = Assert.Single(answer.Sensitivities);
        Assert.Equal(("zero_rate", "JPY-OIS", "365d"), (node.Kind, node.Input, node.Key));
        Assert.Equal(1_000_000 * 1.01 * df, node.Sensitivity, 6);
    }

    [Fact]
    public void A_receiver_is_the_payer_with_its_sign_turned()
    {
        var book = Book(Flat(0.015, 365, 1826, 3652));
        var payer = new SyntheticHedgeRisk().Risk(Swap("2033-11-13", 1.2), book);
        var receiver = new SyntheticHedgeRisk().Risk(Swap("2033-11-13", 1.2, payFixed: false), book);

        Assert.Equal(-payer.Value, receiver.Value, 9);
        Assert.Equal(payer.Sensitivities.Select(s => -s.Sensitivity), receiver.Sensitivities.Select(s => s.Sensitivity));
    }

    [Fact] // each node's sensitivity against the value's change when that node's zero rate alone moves by 0.01bp
    public void Each_nodes_sensitivity_agrees_with_a_bump_of_that_nodes_zero_rate()
    {
        (int Days, double Df)[] nodes = [(365, 0.990), (730, 0.975), (1826, 0.930), (3652, 0.840), (5479, 0.760)];
        var swap = Swap("2034-05-13", 1.75);   // seven and a half years: a short last period, between nodes
        var calculator = new SyntheticHedgeRisk();
        var answer = calculator.Risk(swap, Book(nodes));

        const double bump = 1e-6;
        for (var i = 0; i < nodes.Length; i++)
        {
            double Bumped(double by) => calculator.Risk(swap, Book([.. nodes.Select((n, j) =>
                j == i ? (n.Days, n.Df * Math.Exp(-by * n.Days / 365.0)) : n)])).Value;
            var central = (Bumped(bump) - Bumped(-bump)) / (2 * bump);
            // Stated tolerance: 1e-4 of the notional per unit of rate, far below one basis point's effect on any node.
            Assert.InRange(answer.Sensitivities[i].Sensitivity - central, -100, 100);
        }
        // A payer gains when rates rise: its largest sensitivities are positive.
        Assert.True(answer.Sensitivities.Sum(s => s.Sensitivity) > 0);
    }

    [Fact] // a par swap on a flat curve is worth nothing: K = (1 - DF_n) / sum of fraction * DF_i
    public void A_swap_at_the_par_rate_is_worth_nothing()
    {
        const double zero = 0.02;
        var annuity = Enumerable.Range(1, 5).Sum(y => (Valuation.AddYears(y).DayNumber - Valuation.AddYears(y - 1).DayNumber) / 365.0
            * Math.Exp(-zero * (Valuation.AddYears(y).DayNumber - Valuation.DayNumber) / 365.0));
        var end = Valuation.AddYears(5);
        var par = (1 - Math.Exp(-zero * (end.DayNumber - Valuation.DayNumber) / 365.0)) / annuity;

        var answer = new SyntheticHedgeRisk().Risk(new HedgeSwap("H1", "JPY", 1e9, true, end, par * 100), Book(Flat(zero, 365, 1826, 3652)));

        Assert.Equal(0, answer.Value, 3);
    }

    [Fact]
    public void A_hedge_in_a_currency_with_no_declared_curve_is_refused_by_name()
    {
        var refused = Assert.Throws<HedgeRiskRefused>(() => new SyntheticHedgeRisk().Risk(Swap("2030-11-13", 1, currency: "USD"), Book(Flat(0.02, 365))));

        Assert.Equal("NoCurve", refused.Code);
        Assert.Contains("USD", refused.Message);
    }

    [Fact]
    public void A_hedge_that_has_ended_is_refused()
    {
        var refused = Assert.Throws<HedgeRiskRefused>(() => new SyntheticHedgeRisk().Risk(Swap("2026-11-13", 1), Book(Flat(0.02, 365))));

        Assert.Equal("Ended", refused.Code);
    }
}
