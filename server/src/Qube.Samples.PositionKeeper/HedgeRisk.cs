using Qube.Composition.Contracts;

namespace Qube.Samples.PositionKeeper;

/// <summary>A hypothetical fixed-float interest rate swap, as the Position Keeper holds it (ADR-0019 §7).</summary>
/// <param name="PayFixed">True where the pk pays the fixed rate.</param>
/// <param name="RatePercent">The fixed rate, in percent.</param>
public sealed record HedgeSwap(string Hedge, string Currency, double Notional, bool PayFixed, DateOnly End, double RatePercent);

/// <summary>One sensitivity of a hedge's value, on an input and key of the book's (<c>zero_rate</c>, <c>JPY-OIS</c>, <c>1826d</c>).</summary>
public sealed record HedgeSensitivity(string Kind, string Input, string Key, double Sensitivity);

/// <summary>A hedge's value and first-order risk, with who computed them and how (ADR-0019 §4).</summary>
public sealed record HedgeRiskAnswer(string Provider, ExecutionMode Mode, double Value, IReadOnlyList<HedgeSensitivity> Sensitivities);

/// <summary>A hedge whose risk the provider cannot answer, by name; nothing is answered in its place.</summary>
public sealed class HedgeRiskRefused(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// The engine port for a hedge's own risk (ADR-0019 §7): its value's sensitivities on the same inputs and keys as the
/// book's, so that the two add. The answer says which provider gave it and whether it is simulated.
/// </summary>
public interface IHedgeRisk
{
    HedgeRiskAnswer Risk(HedgeSwap hedge, PkResultSet book);
}

/// <summary>
/// The first slice's provider (ADR-0019 §7, DEFAULT): a single-curve valuation in the Position Keeper module, <c>Simulated</c>. It
/// is not the engine and is replaced by the engine's adapter. What it does, so that its figures can be checked by hand:
/// the swap starts on the valuation date; the fixed leg pays yearly from the start, with a short last period where the
/// end is not a whole number of years, each period's fraction its days over 365; the floating leg is worth the
/// notional's discount from start to end; discount factors are read off the curve the result set declares for the
/// currency, with the zero rate times time interpolated linearly between nodes and the zero rate held flat outside
/// them. A zero rate is continuous on days over 365, and a node's sensitivity moves that node's zero rate alone, as
/// the engine's <c>zero_rate</c> rows do.
/// </summary>
public sealed class SyntheticHedgeRisk : IHedgeRisk
{
    public const string Name = "pk.synthetic-hedge-risk";
    const string ZeroRate = "zero_rate";

    public HedgeRiskAnswer Risk(HedgeSwap hedge, PkResultSet book)
    {
        var valuation = book.Manifest.ValuationDate;
        if (!book.Manifest.HedgeCurves.TryGetValue(hedge.Currency, out var name))
            throw new HedgeRiskRefused("NoCurve", $"Result set '{book.Reference}' declares no curve for a hedge in {hedge.Currency}.");
        if (!book.Curves.TryGetValue(name, out var curve) || curve.Nodes.Count == 0)
            throw new HedgeRiskRefused("NoCurve", $"Result set '{book.Reference}' declares {name} for {hedge.Currency} and does not hold that curve.");
        if (book.UnitOf(ZeroRate) != SensitivityPer.Rate)
            throw new HedgeRiskRefused("NoUnit", $"Result set '{book.Reference}' does not declare {ZeroRate} as per unit of rate, so a hedge's risk cannot be put beside it.");
        if (hedge.End <= valuation)
            throw new HedgeRiskRefused("Ended", $"{hedge.Hedge} ends on {hedge.End:yyyy-MM-dd}, which is not after the valuation date {valuation:yyyy-MM-dd}.");

        // The fixed leg's payments: yearly from the start, the last at the end.
        var payments = new List<(int Days, double Fraction)>();
        var previous = valuation;
        for (var year = 1; ; year++)
        {
            var next = valuation.AddYears(year);
            var pay = next < hedge.End ? next : hedge.End;
            payments.Add((pay.DayNumber - valuation.DayNumber, (pay.DayNumber - previous.DayNumber) / 365.0));
            if (pay == hedge.End) break;
            previous = pay;
        }

        // The value as a sum of discount factors: -1 at the end for the floating leg received, and the fixed coupons paid.
        var sign = hedge.PayFixed ? 1.0 : -1.0;
        var rate = hedge.RatePercent / 100.0;
        var end = payments[^1].Days;
        var value = hedge.Notional * sign;                        // the floating leg's notional at the start, discounted by 1
        var delta = new double[curve.Nodes.Count];
        void Add(int days, double amount)
        {
            var (df, weights) = Discount(curve, days);
            value += amount * df;
            for (var i = 0; i < weights.Length; i++)
                if (weights[i] != 0)
                    // d DF(t) / d z_i = -DF(t) * w_i * t_i, where z(t) t = sum of w_i z_i t_i.
                    delta[i] += amount * -df * weights[i] * curve.Nodes[i].Days / 365.0;
        }
        Add(end, -hedge.Notional * sign);
        foreach (var (days, fraction) in payments) Add(days, -hedge.Notional * sign * rate * fraction);

        return new(Name, ExecutionMode.Simulated, value,
            [.. curve.Nodes.Select((node, i) => new HedgeSensitivity(ZeroRate, curve.Name, node.Key, delta[i]))]);
    }

    /// <summary>The discount factor at a day, and the weight of each node's zero rate times its time in the day's.</summary>
    static (double Df, double[] Weights) Discount(PkCurve curve, int days)
    {
        var nodes = curve.Nodes;
        var weights = new double[nodes.Count];
        double Z(int i) => -Math.Log(nodes[i].DiscountFactor);  // zero rate times time at the node
        if (days <= nodes[0].Days) weights[0] = (double)days / nodes[0].Days;
        else if (days >= nodes[^1].Days) weights[^1] = (double)days / nodes[^1].Days;
        else
        {
            var b = 1;
            while (nodes[b].Days < days) b++;
            var a = b - 1;
            var w = (double)(nodes[b].Days - days) / (nodes[b].Days - nodes[a].Days);
            weights[a] = w;
            weights[b] = 1 - w;
        }
        var z = 0.0;
        for (var i = 0; i < weights.Length; i++)
            if (weights[i] != 0) z += weights[i] * Z(i);
        return (Math.Exp(-z), weights);
    }
}
