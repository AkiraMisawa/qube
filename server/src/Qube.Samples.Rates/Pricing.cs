namespace Qube.Samples.Rates;

/// <summary>What one line asks the provider for. The legs are the effective ones: the user's cashflow edits applied.</summary>
/// <summary>
/// A line as a provider takes it. <paramref name="Collateral"/> is the line's Collateral field as typed (null when empty);
/// a provider that discounts on the collateral's curve reads it, and refuses the line when it is needed and absent.
/// </summary>
public sealed record PricingLine(string RowKey, long InputRevision, Direction Direction, IReadOnlyList<EffectiveLeg> Legs, int? ParLeg, bool Risk,
    string? Collateral = null);

/// <summary>A fixed batch against a fixed snapshot (docs/07 §5). No mutable "latest market" is looked up inside.</summary>
public sealed record RatesBatchInput(long InputRevision, DateOnly ValuationDate, MarketSnapshot Market, IReadOnlyList<PricingLine> Lines);

public sealed record RatesBatchResult(string Provider, string CalculationMode, IReadOnlyList<RowResult> Rows);

/// <summary>A product, at a version of its description, that a provider can price (ADR-0008 §8).</summary>
public sealed record PricedProduct(string Id, string Version)
{
    public override string ToString() => $"{Id} {Version}";
}

/// <summary>The rates pricing port (docs/07 §4). A real worker and the fake implement the same contract.</summary>
public interface IRatesPricingProvider
{
    string Name { get; }

    /// <summary>
    /// The products this provider prices (ADR-0008 §8). A line of any other product is not sent to it: it is listed
    /// as not priced with that reason, never priced by another provider or by a mock.
    /// </summary>
    IReadOnlyList<PricedProduct> Products { get; }

    Task<RatesBatchResult> PriceBatchAsync(RatesBatchInput input, CancellationToken cancellationToken);
}

/// <summary>
/// A provider that builds something per market snapshot (an out-of-process engine's curves and Jacobians) can do it
/// once, when the snapshot arrives, before any subscription prices on it (docs/reports/rates-live-30-users.html §6).
/// Optional: a provider without it builds on demand. Preparing is not pricing: a failure here is counted (<see cref="RatesLiveService.PreparationFailures"/>),
/// and the calculations that follow still ask the provider, which answers for itself.
/// </summary>
public interface IRatesMarketPreparation
{
    Task PrepareMarketAsync(MarketSnapshot market, CancellationToken cancellationToken);
}

/// <summary>Measure names on the wire. A par rate and a fair spread are different measures (docs/08).</summary>
public static class Measures
{
    public const string Pv = "pv";
    public const string ParRate = "parRate";
    public const string FairSpread = "fairSpread";
    /// <summary>A par delta: the PV's change for 1bp on one quote of a quote set.</summary>
    public static string Delta(string quoteSet, string quote) => $"delta:{quoteSet}:{quote}";
    /// <summary>A zero delta: the PV's change for 1bp on one node's zero rate of a curve.</summary>
    public static string Zero(string curve, string node) => $"zero:{curve}:{node}";
}

/// <summary>
/// The deterministic fake provider. It values on the snapshot's curves (discount factors, log-linear between nodes)
/// and says <c>simulated</c>: its figures move with edits and ticks and mean nothing financially, because it projects and
/// discounts every leg on its index's curve and ignores collateral, conventions it does not read and convexity. Zero
/// deltas are a 1bp difference per node; par deltas are those times the snapshot's Jacobian, as a real provider's are.
/// </summary>
public sealed class FakeRatesPricing : IRatesPricingProvider
{
    public const string Mode = "simulated";
    public string Name => "fake-rates/1";
    public IReadOnlyList<PricedProduct> Products { get; } = [new(RatesProducts.Irs, RatesProducts.Version), new(RatesProducts.Ccs, RatesProducts.Version)];

    public Task<RatesBatchResult> PriceBatchAsync(RatesBatchInput input, CancellationToken cancellationToken)
    {
        var rows = input.Lines.Select(line =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return Price(line, input); }
            catch (Exception e) when (e is PricingFailure or NotSupportedException) { return new RowResult(line.RowKey, new Dictionary<string, Measure>(), e.Message); }
        }).ToList();
        return Task.FromResult(new RatesBatchResult(Name, Mode, rows));
    }

    sealed class PricingFailure(string message) : Exception(message);

    const double Bp = 1e-4;

    static RowResult Price(PricingLine line, RatesBatchInput input)
    {
        var reporting = line.Legs[0].Terms.Currency;
        double Pv(IReadOnlyDictionary<string, MarketCurve> curves, double? parQuote) => Value(line, input.ValuationDate, curves, input.Market, reporting, parQuote);
        var measures = new Dictionary<string, Measure>();
        var curves = input.Market.Curves;

        double? par = null;
        if (line.ParLeg is { } p)
        {
            // PV is linear in the solved leg's quote, so two evaluations give the quote at which it is zero.
            var (a, b) = (Pv(curves, 0), Pv(curves, 1));
            if (Math.Abs(b - a) < 1e-12) throw new PricingFailure("The solved leg's quote does not move the PV.");
            par = -a / (b - a);
            measures[line.Legs[p - 1].Terms.Floating ? Measures.FairSpread : Measures.ParRate] = new(par.Value, $"%/leg{p}");
        }
        var pv = Pv(curves, par);
        measures[Measures.Pv] = new(pv, reporting);

        if (line.Risk)
        {
            var unit = $"{reporting}/bp";
            var zeros = new Dictionary<string, double[]>();
            foreach (var name in line.Legs.Select(l => CurveOf(l, input.Market).Name).Distinct())
            {
                var curve = curves[name];
                var deltas = new double[curve.Nodes.Count];
                for (var j = 0; j < curve.Nodes.Count; j++)
                {
                    var bumped = CurveMath.ShiftZeros(curve, days => days == curve.Nodes[j].Days ? Bp : 0);
                    deltas[j] = Pv(curves.ToDictionary(c => c.Key, c => c.Key == name ? bumped : c.Value), par) - pv;
                    measures[Measures.Zero(name, curve.Nodes[j].Tenor)] = new(deltas[j], unit);
                }
                zeros[name] = deltas;
            }
            // Par delta = Jᵀ × zero delta, summed over every curve of the quote set this line moves.
            var parDeltas = new Dictionary<(string Set, int Quote), double>();
            foreach (var j in input.Market.Jacobians.Where(j => zeros.ContainsKey(j.Curve)))
            {
                var z = zeros[j.Curve];
                for (var i = 0; i < j.Rows.Count; i++)
                {
                    if (j.Rows[i].Count != z.Length)
                        throw new PricingFailure($"The Jacobian of {j.QuoteSet} and {j.Curve} has {j.Rows[i].Count} columns for {z.Length} nodes.");
                    double sum = 0;
                    for (var k = 0; k < z.Length; k++) sum += j.Rows[i][k] * z[k];
                    parDeltas[(j.QuoteSet, i)] = parDeltas.GetValueOrDefault((j.QuoteSet, i)) + sum;
                }
            }
            foreach (var ((set, i), value) in parDeltas)
            {
                var quotes = input.Market.Quotes.TryGetValue(set, out var q) ? q : throw new PricingFailure($"The market has a Jacobian for {set} and no such quotes.");
                measures[Measures.Delta(set, quotes.Quotes[i].Tenor)] = new(value, unit);
            }
        }
        return new RowResult(line.RowKey, measures, null);
    }

    /// <summary>The line's PV in the reporting currency: receive leg 2 and pay leg 1 when paying, the reverse when receiving.</summary>
    static double Value(PricingLine line, DateOnly valuationDate, IReadOnlyDictionary<string, MarketCurve> curves, MarketSnapshot market, string reporting,
        double? parQuote)
    {
        double total = 0;
        for (var n = 1; n <= line.Legs.Count; n++)
        {
            var leg = line.Legs[n - 1];
            var curve = curves[CurveOf(leg, market).Name];
            double Df(DateOnly d) => d <= valuationDate ? 1 : CurveMath.Df(curve, d.DayNumber - valuationDate.DayNumber);
            var gearing = leg.Terms.Detail.Gearing ?? 1;
            double value = 0;
            foreach (var p in leg.Periods)
            {
                if (p.PaymentDate <= valuationDate) continue;
                var quote = n == line.ParLeg ? parQuote : p.Rate;
                if (quote is null) throw new PricingFailure($"{p.Entity} has no rate.");
                var coupon = leg.Terms.Floating
                    ? (gearing * (Df(p.AccrualStart) / Df(p.AccrualEnd) - 1) / p.YearFraction + quote.Value / 100) * p.YearFraction
                    : quote.Value / 100 * p.YearFraction;
                value += p.Notional * coupon * Df(p.PaymentDate);
            }
            foreach (var e in leg.Exchanges.Where(e => e.PaymentDate > valuationDate))
                value += (e.Kind == ExchangeKind.Final ? e.Amount : -e.Amount) * Df(e.PaymentDate);
            var fx = leg.Terms.Currency == reporting ? 1 : Fx(market, leg.Terms.Currency, reporting);
            var received = (line.Direction == Direction.Pay) == (n == 2);
            total += (received ? 1 : -1) * value * fx;
        }
        return total;
    }

    /// <summary>
    /// The curve a leg is projected and discounted on: its index's curve, or for a fixed leg the currency's only
    /// curve. A real provider discounts on the collateral's curve; the fake does not pretend to.
    /// </summary>
    static MarketCurve CurveOf(EffectiveLeg leg, MarketSnapshot market)
    {
        if (market.Curves.TryGetValue(MarketSnapshot.CurveKey(leg.Terms.Currency, leg.Terms.Index), out var own)) return own;
        if (leg.Terms.Floating) throw new PricingFailure($"The market has no curve {leg.Terms.Currency}/{leg.Terms.Index}.");
        var candidates = market.Curves.Values.Where(c => c.Currency == leg.Terms.Currency).ToList();
        return candidates.Count == 1 ? candidates[0]
            : throw new PricingFailure($"The market has {candidates.Count} {leg.Terms.Currency} curves; the fixed leg's is not determined.");
    }

    /// <summary>Units of <paramref name="to"/> per unit of <paramref name="from"/>, from rates quoted as units of each currency per base unit.</summary>
    static double Fx(MarketSnapshot market, string from, string to)
    {
        double PerBase(string ccy) => market.Fx.TryGetValue(ccy, out var r) ? r : throw new PricingFailure($"The market has no FX rate for {ccy}.");
        return PerBase(to) / PerBase(from);
    }
}

public static class PricingLines
{
    /// <summary>The priceable lines of a workspace revision, as the provider takes them. Lines that are not priceable are left to the caller.</summary>
    public static PricingLine From(EffectiveLine line, long inputRevision) => line.Priceable
        ? new(line.Line, inputRevision, line.Terms!.Direction, line.Legs, line.ParLeg, line.Terms.Risk, line.Terms.Collateral)
        : throw new InvalidOperationException($"{line.Line} is not priceable: {string.Join("; ", line.Issues.Concat(line.Held))}");
}
