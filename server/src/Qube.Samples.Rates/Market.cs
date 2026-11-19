namespace Qube.Samples.Rates;

/// <summary>A node of a curve: its days from the valuation date, the tenor it is labelled by, and its discount factor.</summary>
public sealed record CurveNode(string Tenor, int Days, double DiscountFactor);

/// <summary>
/// A curve as production delivers it and as alpha reads it (alpha docs/spec/inputs.md §3, <c>market/curves</c> and
/// <c>market/curve_settings.csv</c>): discount factors at nodes counted in days from the valuation date, with the
/// curve's role and interpolation. <c>IndexMonths</c> is the term of the index a forward curve projects (null for an
/// overnight index or a discount curve). <c>Composition</c> is <c>base</c>, or <c>ratio</c> of two other curves.
/// </summary>
public sealed record MarketCurve(string Name, string Currency, string Index, string Role, string Composition, string Interpolation,
    IReadOnlyList<CurveNode> Nodes, int? IndexMonths = null, string? Numerator = null, string? Denominator = null);

/// <summary>One quote of a quote set: the instrument's tenor label, its days, and its par rate in percent.</summary>
public sealed record QuotePoint(string Tenor, int Days, double RatePercent);

/// <summary>The quotes a curve was solved from (alpha <c>market/quotes</c>).</summary>
public sealed record QuoteSet(string Name, IReadOnlyList<QuotePoint> Quotes);

/// <summary>
/// The par-delta Jacobian of one quote set and one curve (alpha <c>market/jacobians/{set}__{curve}.csv</c>):
/// <c>Rows[i][j]</c> is ∂z_j/∂q_i, quote i of the set against the curve's node j's zero rate (continuously compounded,
/// ACT/365F). Production delivers it with the curves; nothing here solves curves from quotes.
/// </summary>
public sealed record QuoteJacobian(string QuoteSet, string Curve, IReadOnlyList<IReadOnlyList<double>> Rows);

/// <summary>
/// A fixed market snapshot (docs/07 §7): everything needed to compute with it again, under one identifier, in the shape
/// production delivers every second: finished curves, the quotes they were solved from with their Jacobians, and FX.
/// The origin says whether it is synthetic; a seed and a scenario version make a synthetic one reproducible.
/// </summary>
public sealed record MarketSnapshot(
    string Id,
    long Sequence,
    DateTimeOffset AsOf,
    string Origin,
    int Seed,
    string ScenarioVersion,
    IReadOnlyDictionary<string, MarketCurve> Curves,
    IReadOnlyDictionary<string, QuoteSet> Quotes,
    IReadOnlyList<QuoteJacobian> Jacobians,
    IReadOnlyDictionary<string, double> Fx)
{
    public static string CurveKey(string currency, string index) => $"{currency}/{index}";

    public MarketUpdate Update => new(Sequence, Id, AsOf, Origin);

    /// <summary>The snapshot with one curve replaced, and its quote set and Jacobian replaced with it.</summary>
    public MarketSnapshot With(MarketCurve curve, QuoteSet quotes, QuoteJacobian jacobian) => this with
    {
        Curves = Curves.ToDictionary(p => p.Key, p => p.Key == curve.Name ? curve : p.Value),
        Quotes = Quotes.ToDictionary(p => p.Key, p => p.Key == quotes.Name ? quotes : p.Value),
        Jacobians = [.. Jacobians.Select(j => j.QuoteSet == jacobian.QuoteSet && j.Curve == jacobian.Curve ? jacobian : j)],
    };
}

/// <summary>A synthetic curve's starting point: its zero rates in percent (continuously compounded, ACT/365F) by tenor.</summary>
public sealed record SyntheticCurve(string Currency, string Index, IReadOnlyList<(Tenor Tenor, double ZeroPercent)> Pillars);

/// <summary>The starting point of a synthetic market: its curves, FX rates against a base currency, and step bounds.</summary>
public sealed record SyntheticMarketConfig(
    string ScenarioVersion,
    int Seed,
    DateTimeOffset Start,
    TimeSpan Step,
    IReadOnlyList<SyntheticCurve> Curves,
    IReadOnlyDictionary<string, double> Fx,
    double MaxLevelStepBp,
    double MaxSlopeStepBp,
    double MaxFxLogStep);

/// <summary>
/// A repeatable synthetic market (docs/07 §7): snapshot n is a pure function of the configuration and n. Each curve is
/// one base OIS curve per currency and index, its quotes are annual OIS par rates at its nodes and its Jacobian is
/// their exact inverse (<see cref="CurveMath.OisQuotes"/>), so it has production's shape. Each step moves every
/// curve's zero rates by a bounded level and slope change shared within a currency, and every FX rate by a bounded
/// change in log space. Nothing here claims to be realistic; every snapshot says synthetic.
/// </summary>
public sealed class SyntheticMarket(SyntheticMarketConfig config)
{
    public const string Origin = "synthetic";

    readonly Lock gate = new();
    readonly List<MarketSnapshot> snapshots = [];

    public SyntheticMarketConfig Config => config;

    public MarketSnapshot Snapshot(long sequence)
    {
        if (sequence < 0) throw new ArgumentOutOfRangeException(nameof(sequence));
        lock (gate)
        {
            while (snapshots.Count <= sequence) snapshots.Add(snapshots.Count == 0 ? First() : Next(snapshots[^1]));
            return snapshots[(int)sequence];
        }
    }

    /// <summary>Resolves an identifier this market issued, or null: an unknown identifier is never taken as the latest.</summary>
    public MarketSnapshot? Resolve(string id)
    {
        var prefix = $"{Origin}:{config.ScenarioVersion}:{config.Seed}:";
        return id.StartsWith(prefix, StringComparison.Ordinal) && long.TryParse(id[prefix.Length..], out var n) && n >= 0 ? Snapshot(n) : null;
    }

    /// <summary>A snapshot of these curves (zero rates by tenor) with their OIS quotes and Jacobians: for a market made by hand.</summary>
    public static MarketSnapshot Build(string id, long sequence, DateTimeOffset asOf, string origin, int seed, string scenarioVersion,
        IEnumerable<SyntheticCurve> curves, IReadOnlyDictionary<string, double> fx) =>
        Assemble(id, sequence, asOf, origin, seed, scenarioVersion, curves.Select(Curve), fx);

    /// <summary>A base OIS curve from zero rates by tenor; a tenor's days are its years × 365, rounded.</summary>
    public static MarketCurve Curve(SyntheticCurve c) => new(MarketSnapshot.CurveKey(c.Currency, c.Index), c.Currency, c.Index, "discount", "base",
        CurveMath.LogLinearDf,
        [.. c.Pillars.OrderBy(p => YearsOf(p.Tenor)).Select(p =>
        {
            var days = (int)Math.Round(YearsOf(p.Tenor) * 365);
            return new CurveNode(p.Tenor.ToString(), days, Math.Exp(-p.ZeroPercent / 100 * days / 365.0));
        })]);

    static MarketSnapshot Assemble(string id, long n, DateTimeOffset asOf, string origin, int seed, string version, IEnumerable<MarketCurve> curves,
        IReadOnlyDictionary<string, double> fx)
    {
        var all = curves.ToList();
        var quoted = all.Select(c => (Curve: c, Quoted: CurveMath.OisQuotes(c))).ToList();
        return new(id, n, asOf, origin, seed, version,
            all.ToDictionary(c => c.Name),
            quoted.ToDictionary(q => q.Quoted.Quotes.Name, q => q.Quoted.Quotes),
            [.. quoted.Select(q => q.Quoted.Jacobian)],
            fx);
    }

    MarketSnapshot First() => Make(0, config.Curves.Select(Curve), config.Fx);

    MarketSnapshot Next(MarketSnapshot previous)
    {
        var n = previous.Sequence + 1;
        // HashCode is salted per process, so the step's seed is mixed by hand to stay the same across restarts.
        var random = new Random(unchecked((int)(config.Seed * 1_000_003L + n * 7_919L)));
        double Bounded(double max) => (random.NextDouble() * 2 - 1) * max;
        var shocks = previous.Curves.Values.Select(c => c.Currency).Distinct().Order(StringComparer.Ordinal)
            .ToDictionary(c => c, _ => (Level: Bounded(config.MaxLevelStepBp) / 1e4, Slope: Bounded(config.MaxSlopeStepBp) / 1e4));
        var curves = previous.Curves.Values.OrderBy(c => c.Name, StringComparer.Ordinal).Select(c =>
        {
            var (level, slope) = shocks[c.Currency];
            var longest = c.Nodes.Max(x => x.Days);
            return CurveMath.ShiftZeros(c, days => level + slope * days / longest);
        });
        var fx = previous.Fx.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value * Math.Exp(Bounded(config.MaxFxLogStep)));
        return Make(n, curves, fx);
    }

    MarketSnapshot Make(long n, IEnumerable<MarketCurve> curves, IReadOnlyDictionary<string, double> fx) =>
        Assemble($"{Origin}:{config.ScenarioVersion}:{config.Seed}:{n}", n, config.Start + config.Step * n, Origin, config.Seed, config.ScenarioVersion, curves, fx);

    public static double YearsOf(Tenor tenor) => tenor.Unit switch
    {
        TenorUnit.Day => tenor.Count / 365.0,
        TenorUnit.Week => tenor.Count * 7 / 365.0,
        TenorUnit.Month => tenor.Count / 12.0,
        _ => tenor.Count,
    };
}
