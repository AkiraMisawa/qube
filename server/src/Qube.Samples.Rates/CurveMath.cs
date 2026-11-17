namespace Qube.Samples.Rates;

/// <summary>
/// Reading a <see cref="MarketCurve"/>: discount factors between nodes, zero rates, and the synthetic market's quote
/// definitions. Time is days / 365 (ACT/365F), as in alpha. Only <c>log_linear_df</c> base curves are read here; any
/// other curve is refused, never read as if it were one.
/// </summary>
public static class CurveMath
{
    public const string LogLinearDf = "log_linear_df";

    static double Years(double days) => days / 365.0;

    static void Readable(MarketCurve curve)
    {
        if (curve.Interpolation != LogLinearDf)
            throw new NotSupportedException($"The curve {curve.Name} interpolates by {curve.Interpolation}; only {LogLinearDf} is read here.");
        if (curve.Composition != "base")
            throw new NotSupportedException($"The curve {curve.Name} is a {curve.Composition} curve; only base curves are read here.");
        if (curve.Nodes.Count == 0) throw new NotSupportedException($"The curve {curve.Name} has no nodes.");
    }

    /// <summary>
    /// ∂ln P(days)/∂ln P_j for each node j: ln P is linear between the origin (ln P = 0) and the nodes, and its zero rate
    /// is flat beyond the last node.
    /// </summary>
    public static double[] Weights(MarketCurve curve, double days)
    {
        Readable(curve);
        var nodes = curve.Nodes;
        var w = new double[nodes.Count];
        if (days <= 0) return w;
        if (days <= nodes[0].Days)
        {
            w[0] = days / nodes[0].Days;
            return w;
        }
        for (var k = 1; k < nodes.Count; k++)
            if (days <= nodes[k].Days)
            {
                var (a, b) = (nodes[k - 1].Days, nodes[k].Days);
                w[k - 1] = (b - days) / (b - a);
                w[k] = (days - a) / (b - a);
                return w;
            }
        w[^1] = days / nodes[^1].Days;
        return w;
    }

    /// <summary>ln P at a number of days from the valuation date.</summary>
    public static double LnDf(MarketCurve curve, double days)
    {
        var w = Weights(curve, days);
        double sum = 0;
        for (var j = 0; j < w.Length; j++)
            if (w[j] != 0) sum += w[j] * Math.Log(curve.Nodes[j].DiscountFactor);
        return sum;
    }

    public static double Df(MarketCurve curve, double days) => Math.Exp(LnDf(curve, days));

    /// <summary>A node's zero rate (decimal, continuously compounded, ACT/365F).</summary>
    public static double Zero(CurveNode node) => -Math.Log(node.DiscountFactor) / Years(node.Days);

    /// <summary>The curve with each node's zero rate moved by <paramref name="shift"/>(days), a decimal.</summary>
    public static MarketCurve ShiftZeros(MarketCurve curve, Func<int, double> shift) => curve with
    {
        Nodes = [.. curve.Nodes.Select(n => n with { DiscountFactor = n.DiscountFactor * Math.Exp(-shift(n.Days) * Years(n.Days)) })],
    };

    /// <summary>
    /// The synthetic market's quote definitions for a base OIS curve: at each node, the par rate of an OIS paying an
    /// annual fixed coupon against compounded overnight, both ACT/365F on the node's day grid (one period up to a year;
    /// beyond, yearly periods counted back from the end, the first one short). Its Jacobian is exact: A = ∂q/∂z in closed
    /// form, inverted. The quote set takes the curve's name. These definitions are the synthetic market's own, not any
    /// market's conventions; production delivers its quotes and Jacobians instead.
    /// </summary>
    public static (QuoteSet Quotes, QuoteJacobian Jacobian) OisQuotes(MarketCurve curve)
    {
        Readable(curve);
        var n = curve.Nodes.Count;
        var quotes = new List<QuotePoint>(n);
        var a = new double[n, n];   // ∂q_i/∂z_j
        for (var i = 0; i < n; i++)
        {
            var (q, dLn) = ParAndGradient(curve, curve.Nodes[i].Days);
            quotes.Add(new QuotePoint(curve.Nodes[i].Tenor, curve.Nodes[i].Days, q * 100));
            for (var j = 0; j < n; j++) a[i, j] = dLn[j] * -Years(curve.Nodes[j].Days);
        }
        var inverse = Invert(a, curve.Name);   // inverse[j, i] = ∂z_j/∂q_i
        var rows = new List<IReadOnlyList<double>>(n);
        for (var i = 0; i < n; i++)
        {
            var row = new double[n];
            for (var j = 0; j < n; j++) row[j] = inverse[j, i];
            rows.Add(row);
        }
        return (new QuoteSet(curve.Name, quotes), new QuoteJacobian(curve.Name, curve.Name, rows));
    }

    /// <summary>The par rate (decimal) of the synthetic OIS ending at <paramref name="days"/>, and ∂q/∂ln P_j.</summary>
    public static (double Rate, double[] DLnDf) ParAndGradient(MarketCurve curve, int days)
    {
        var n = curve.Nodes.Count;
        var grad = new double[n];
        if (days <= 365)
        {
            var tau = Years(days);
            var p = Df(curve, days);
            var w = Weights(curve, days);
            for (var j = 0; j < n; j++) grad[j] = -w[j] / p / tau;
            return ((1 / p - 1) / tau, grad);
        }
        var periods = (int)Math.Round(days / 365.0);
        var end = Df(curve, days);
        double annuity = 0;
        var dAnnuity = new double[n];
        var previous = 0;
        for (var k = 1; k <= periods; k++)
        {
            var d = days - 365 * (periods - k);
            var tau = Years(d - previous);
            var p = Df(curve, d);
            annuity += tau * p;
            var w = Weights(curve, d);
            for (var j = 0; j < n; j++) dAnnuity[j] += tau * p * w[j];
            previous = d;
        }
        var q = (1 - end) / annuity;
        var wEnd = Weights(curve, days);
        for (var j = 0; j < n; j++) grad[j] = (-end * wEnd[j] - q * dAnnuity[j]) / annuity;
        return (q, grad);
    }

    /// <summary>Gauss-Jordan inversion with partial pivoting; a singular matrix is refused.</summary>
    static double[,] Invert(double[,] m, string name)
    {
        var n = m.GetLength(0);
        var a = (double[,])m.Clone();
        var inv = new double[n, n];
        for (var i = 0; i < n; i++) inv[i, i] = 1;
        for (var c = 0; c < n; c++)
        {
            var pivot = c;
            for (var r = c + 1; r < n; r++) if (Math.Abs(a[r, c]) > Math.Abs(a[pivot, c])) pivot = r;
            if (Math.Abs(a[pivot, c]) < 1e-14) throw new InvalidOperationException($"The quotes of {name} do not determine its zero rates.");
            if (pivot != c)
                for (var k = 0; k < n; k++)
                {
                    (a[c, k], a[pivot, k]) = (a[pivot, k], a[c, k]);
                    (inv[c, k], inv[pivot, k]) = (inv[pivot, k], inv[c, k]);
                }
            var d = a[c, c];
            for (var k = 0; k < n; k++) { a[c, k] /= d; inv[c, k] /= d; }
            for (var r = 0; r < n; r++)
            {
                if (r == c || a[r, c] == 0) continue;
                var f = a[r, c];
                for (var k = 0; k < n; k++) { a[r, k] -= f * a[c, k]; inv[r, k] -= f * inv[c, k]; }
            }
        }
        return inv;
    }
}
