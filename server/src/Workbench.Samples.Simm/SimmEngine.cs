using Workbench.Composition.Contracts;

namespace Workbench.Samples.Simm;

/// <summary>One direction's IM: the largest of its regimes' SIMM, and which regime that is (ADR-0012 §1).</summary>
public sealed record DirectionIm(ImDirection Direction, string CsaId, double? Im, string? GoverningRegime, IReadOnlyList<RegimeSimm> Regimes);

/// <summary>SIMM under one regime, with its term tree.</summary>
public sealed record RegimeSimm(string Regime, SimmTerm Total);

/// <summary>A CRIF line's contribution to its regime's SIMM (the Euler allocation, ADR-0012 §6).</summary>
public sealed record LineContribution(string Line, string TradeId, double AmountUsd, double Contribution);

/// <summary>
/// A term of the SIMM formula: its value, its contribution to the regime's SIMM, and its children. A risk factor's
/// <see cref="Leaves"/> are the CRIF lines it sums. The formula's own terms do not add up, because correlations add
/// cross terms; contributions do: a term's children's contributions add up to its own.
/// </summary>
public sealed record SimmTerm(string Name, string Kind, double Value, double Contribution, IReadOnlyList<SimmTerm> Children, IReadOnlyList<LineContribution> Leaves)
{
    /// <summary>Every CRIF line under this term.</summary>
    public IEnumerable<LineContribution> Lines() => Leaves.Concat(Children.SelectMany(c => c.Lines()));

    /// <summary>Each trade's contribution to this term's: the sum of its lines' under it (ADR-0012 §6).</summary>
    public IReadOnlyDictionary<string, double> TradeContributions() =>
        Lines().GroupBy(l => l.TradeId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Sum(l => l.Contribution), StringComparer.Ordinal);
}

/// <summary>The IM of each direction, and the calculation trace the engine wrote as it computed them (ADR-0011, ADR-0012 §6).</summary>
public sealed record SimmResult(string ParametersVersion, IReadOnlyList<DirectionIm> Directions, CalculationTrace Trace)
{
    public DirectionIm Direction(ImDirection direction) =>
        Directions.SingleOrDefault(d => d.Direction == direction) ?? throw new KeyNotFoundException($"The result has no {direction} direction.");
}

/// <summary>
/// The PoC's reference SIMM engine (ADR-0012 §5): the aggregation of ISDA SIMM v2.x from risk factor to bucket, to
/// margin, to risk class, to product class, to the total, then the worst of the regimes. Its numbers are only as real
/// as the parameter table it is given.
/// </summary>
/// <remarks>
/// The allocation is Euler's: a line's contribution is its sensitivity times the SIMM's derivative by it, with each
/// concentration factor held at its value. So held, the SIMM is homogeneous of degree one in the sensitivities, and the
/// contributions add up to it exactly. A term whose value is 0 passes no derivative down: its lines contribute 0.
/// </remarks>
public static partial class SimmEngine
{
    public static SimmResult Calculate(IReadOnlyList<CrifLine> crif, IReadOnlyList<ImCsa> csas, SimmParameters parameters)
    {
        Validate(crif, parameters);
        var trace = new TraceWriter(parameters);
        var directions = csas.Select(csa =>
        {
            // A regime is computed when the CSA serves it and a line it holds names it (ADR-0012 §1). With none, the
            // direction has no IM: nothing is in scope, which is not an IM of 0.
            var held = csa.Trades.ToHashSet(StringComparer.Ordinal);
            var inScope = crif.Where(l => held.Contains(l.TradeId)).ToList();
            var regimes = csa.Regimes
                .Select(regime => (regime, lines: inScope.Where(l => Regulations(l, csa.Direction).Contains(regime)).ToList()))
                .Where(r => r.lines.Count > 0)
                .Select(r => (r.regime, calc: Regime(r.regime, r.lines, parameters)))
                .ToList();
            var governing = regimes.Count == 0 ? -1 : regimes.IndexOf(regimes.MaxBy(r => r.calc.Term.Value));
            trace.Direction(csa, regimes, governing);
            return new DirectionIm(csa.Direction, csa.Id, governing < 0 ? null : regimes[governing].calc.Term.Value,
                governing < 0 ? null : regimes[governing].regime, [.. regimes.Select(r => new RegimeSimm(r.regime, r.calc.Term))]);
        }).ToList();
        return new SimmResult(parameters.Version, directions, trace.Build());
    }

    /// <summary>Refuses a line the table cannot weigh, naming it: a currency it places in no volatility group, a tenor it lacks.</summary>
    static void Validate(IReadOnlyList<CrifLine> crif, SimmParameters parameters)
    {
        var problems = new List<string>();
        foreach (var line in crif.Where(l => l.RiskType is CrifRiskType.IrCurve or CrifRiskType.XCcyBasis))
        {
            if (!parameters.VolatilityGroups.ContainsKey(line.Qualifier))
                problems.Add($"{line.Line}: the parameter table {parameters.Version} places the currency {line.Qualifier} in no volatility group.");
            if (line.RiskType == CrifRiskType.IrCurve && (line.Label1 is null || !parameters.Tenors.Contains(line.Label1)))
                problems.Add($"{line.Line}: the tenor {line.Label1 ?? "(none)"} is not one of SIMM's ({string.Join(", ", parameters.Tenors)}).");
            if (line.RiskType == CrifRiskType.IrCurve && line.Label2 is null)
                problems.Add($"{line.Line}: an IR curve line names its sub-curve in Label2.");
        }
        if (problems.Count > 0) throw new SimmInputException(problems);
    }

    static IReadOnlyList<string> Regulations(CrifLine line, ImDirection direction) =>
        direction == ImDirection.Collect ? line.CollectRegulations : line.PostRegulations;

    // The forward pass keeps what the backward pass needs; the terms are built last, once the lines' contributions are known.

    sealed class Factor(string name, CrifRiskType riskType, string? tenor, string? subCurve, IReadOnlyList<CrifLine> lines, double rw, double cr,
        IReadOnlyList<string> rwBecause)
    {
        public string Name { get; } = name;
        public double Rw { get; } = rw;
        public double Cr { get; } = cr;
        public IReadOnlyList<string> RwBecause { get; } = rwBecause;
        public CrifRiskType RiskType { get; } = riskType;
        public string? Tenor { get; } = tenor;
        public string? SubCurve { get; } = subCurve;
        public IReadOnlyList<CrifLine> Lines { get; } = lines;
        public double Scale { get; } = rw * cr;
        public double Ws { get; } = rw * cr * lines.Sum(l => l.AmountUsd);
        public double Gradient { get; set; }
    }

    /// <summary>
    /// One bucket as computed: its factors, the tenor correlation ρ and the sub-curve one φ between them (their product is
    /// the correlation used), K_b, S_b with the bound it took, and the concentration factor with the figures it came from.
    /// </summary>
    sealed record Bucket(string Name, IReadOnlyList<Factor> Factors, double[,] Rho, double[,] Phi, double K, double S, int Bound, double Cr,
        IReadOnlyList<string> CrBecause)
    {
        public double Correlation(int k, int l) => Rho[k, l] * Phi[k, l];
    }

    /// <summary>One regime's calculation: its term tree, and the buckets and margins the trace writes from.</summary>
    sealed record RegimeCalc(SimmTerm Term, IReadOnlyList<Bucket> IrBuckets, Bucket? Fx);

    /// <summary>
    /// SIMM under one regime: Σ_p SIMM_p over the product classes the lines reach. The PoC's risks are all in Rates/FX,
    /// whose SIMM is √(Σ_r IM_r² + Σ_{r≠s} ψ_rs IM_r IM_s) over IR and FX; a risk class with no line is absent.
    /// </summary>
    static RegimeCalc Regime(string regime, IReadOnlyList<CrifLine> lines, SimmParameters parameters)
    {
        var ir = lines.Where(l => l.RiskType is CrifRiskType.IrCurve or CrifRiskType.XCcyBasis).ToList();
        var fx = lines.Where(l => l.RiskType == CrifRiskType.Fx).ToList();
        var irBuckets = ir.GroupBy(l => l.Qualifier, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => IrBucket(g.Key, [.. g], parameters)).ToList();
        var fxBucket = fx.Count > 0 ? FxBucket(fx, parameters) : null;

        // Forward: the margins.
        var classes = new List<(string Name, double Im)>();
        double irDelta = 0;
        if (irBuckets.Count > 0)
        {
            double sum = irBuckets.Sum(b => b.K * b.K);
            for (var b = 0; b < irBuckets.Count; b++)
                for (var c = 0; c < irBuckets.Count; c++)
                    if (b != c) sum += Gamma(irBuckets[b], irBuckets[c], parameters) * irBuckets[b].S * irBuckets[c].S;
            irDelta = Math.Sqrt(Math.Max(sum, 0));
            classes.Add(("IR", irDelta));
        }
        if (fxBucket is not null) classes.Add(("FX", fxBucket.K));
        double productSum = 0;
        foreach (var r in classes)
            foreach (var s in classes)
                productSum += (r.Name == s.Name ? 1 : Psi(r.Name, s.Name, parameters)) * r.Im * s.Im;
        var product = Math.Sqrt(Math.Max(productSum, 0));

        // Backward: ∂SIMM/∂WS_k for every risk factor.
        double ImGradient(string name) => product == 0 ? 0
            : classes.Sum(s => (s.Name == name ? 1 : Psi(name, s.Name, parameters)) * s.Im) / product;
        if (irBuckets.Count > 0 && irDelta > 0)
        {
            var g = ImGradient("IR");
            for (var b = 0; b < irBuckets.Count; b++)
            {
                var bucket = irBuckets[b];
                var gK = g * bucket.K / irDelta;
                var gS = g * irBuckets.Where((_, c) => c != b).Sum(c => Gamma(bucket, c, parameters) * c.S) / irDelta;
                Backward(bucket, gK, gS);
            }
        }
        if (fxBucket is not null) Backward(fxBucket, ImGradient("FX"), 0);

        // The terms, with every contribution summed from the lines'.
        var classTerms = new List<SimmTerm>();
        if (irBuckets.Count > 0) classTerms.Add(Class("IR", Delta(irDelta, [.. irBuckets.Select(BucketTerm)])));
        if (fxBucket is not null) classTerms.Add(Class("FX", Delta(fxBucket.K, [BucketTerm(fxBucket)])));
        var productTerm = Term("RatesFX", "simm.product", product, classTerms);
        return new(Term(regime, "simm.regime", product, [productTerm]), irBuckets, fxBucket);
    }

    /// <summary>∂SIMM/∂WS_k from the bucket's K_b and S_b: ∂K_b/∂WS_k = Σ_l ρ_kl WS_l / K_b; S_b moves one for one inside ±K_b, with the bound outside it.</summary>
    static void Backward(Bucket bucket, double gK, double gS)
    {
        var n = bucket.Factors.Count;
        for (var k = 0; k < n; k++)
        {
            double dK = 0;
            if (bucket.K > 0)
            {
                for (var l = 0; l < n; l++) dK += bucket.Correlation(k, l) * bucket.Factors[l].Ws;
                dK /= bucket.K;
            }
            var dS = bucket.Bound == 0 ? 1 : bucket.Bound * dK;
            bucket.Factors[k].Gradient = gK * dK + gS * dS;
        }
    }

    static SimmTerm Class(string name, SimmTerm delta) => Term(name, "simm.riskClass", delta.Value, [delta]);

    static SimmTerm Delta(double value, IReadOnlyList<SimmTerm> buckets) => Term("Delta", "simm.delta", value, buckets);

    static SimmTerm BucketTerm(Bucket bucket) => Term(bucket.Name, "simm.bucket", bucket.K, [.. bucket.Factors.Select(f =>
    {
        IReadOnlyList<LineContribution> leaves = [.. f.Lines.Select(l => new LineContribution(l.Line, l.TradeId, l.AmountUsd, l.AmountUsd * f.Scale * f.Gradient))];
        return new SimmTerm(f.Name, "simm.factor", f.Ws, leaves.Sum(l => l.Contribution), [], leaves);
    })]);

    static SimmTerm Term(string name, string kind, double value, IReadOnlyList<SimmTerm> children) =>
        new(name, kind, value, children.Sum(c => c.Contribution), children, []);

    /// <summary>γ_bc g_bc between two currencies' IR buckets, g_bc = min(CR_b, CR_c) / max(CR_b, CR_c).</summary>
    static double Gamma(Bucket b, Bucket c, SimmParameters parameters) =>
        parameters.IrInterBucketCorrelation * Math.Min(b.Cr, c.Cr) / Math.Max(b.Cr, c.Cr);

    /// <summary>
    /// One currency's IR delta: WS_k = RW_k s_k CR_b, K_b = √(Σ_k Σ_l ρ_kl φ_kl WS_k WS_l), S_b = ΣWS_k bounded by ±K_b.
    /// CR_b sums the currency's IR curve sensitivities, not its cross-currency basis, and scales every WS of it.
    /// </summary>
    static Bucket IrBucket(string currency, IReadOnlyList<CrifLine> lines, SimmParameters parameters)
    {
        var group = parameters.VolatilityGroups[currency];
        var curveSum = lines.Where(l => l.RiskType == CrifRiskType.IrCurve).Sum(l => l.AmountUsd);
        var threshold = parameters.IrConcentrationThresholds[group];
        var cr = Math.Max(1, Math.Sqrt(Math.Abs(curveSum) / threshold));
        var factors = lines.GroupBy(l => (l.RiskType, l.Label1, l.Label2)).Select(g =>
        {
            if (g.Key.RiskType == CrifRiskType.XCcyBasis)
                return new Factor("XCcyBasis", g.Key.RiskType, null, null, [.. g], parameters.XCcyBasisRiskWeight, cr,
                    [$"Cross-currency basis has one risk weight in {parameters.Version}: {parameters.XCcyBasisRiskWeight}."]);
            var tenor = g.Key.Label1 ?? throw new InvalidOperationException("An IR curve line without a tenor passed validation."); // Validate refuses it first
            var rw = parameters.IrRiskWeights[group][Index(parameters.Tenors, tenor)];
            return new Factor($"{g.Key.Label2} {tenor}", g.Key.RiskType, tenor, g.Key.Label2, [.. g], rw, cr,
                [$"{currency} is in the {group} volatility group.", $"The tenor is {tenor}.", $"{parameters.Version}'s IR risk weight for {group} volatility at {tenor} is {rw}."]);
        }).ToList();
        var (rho, phi) = (new double[factors.Count, factors.Count], new double[factors.Count, factors.Count]);
        for (var i = 0; i < factors.Count; i++)
            for (var j = 0; j < factors.Count; j++)
                (rho[i, j], phi[i, j]) = i == j ? (1, 1) : Correlation(factors[i], factors[j], parameters);
        var k = Norm(factors, rho, phi);
        var sum = factors.Sum(f => f.Ws);
        var bound = sum > k ? 1 : sum < -k ? -1 : 0;
        return new(currency, factors, rho, phi, k, bound == 0 ? sum : bound * k, bound, cr,
            ["CR_b = max(1, √(|Σ s_k| / T_b)).",
             $"Σ s_k = {Usd(curveSum)} over {currency}'s IR curve lines; cross-currency basis is not in it.",
             $"T_b = {Usd(threshold)} for the {group} volatility group in {parameters.Version}."]);
    }

    /// <summary>
    /// FX delta: one bucket of currencies against the calculation currency, WS_k = RW s_k CR_k with
    /// CR_k = max(1, √(|s_k| / T_FX)), and K = √(Σ_k Σ_l ρ_FX WS_k WS_l).
    /// </summary>
    static Bucket FxBucket(IReadOnlyList<CrifLine> lines, SimmParameters parameters)
    {
        var factors = lines.GroupBy(l => l.Qualifier, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g =>
        {
            var cr = Math.Max(1, Math.Sqrt(Math.Abs(g.Sum(l => l.AmountUsd)) / parameters.FxConcentrationThreshold));
            return new Factor(g.Key, CrifRiskType.Fx, null, null, [.. g], parameters.FxRiskWeight, cr,
                [$"FX has one risk weight in {parameters.Version}: {parameters.FxRiskWeight}."]);
        }).ToList();
        var (rho, phi) = (new double[factors.Count, factors.Count], new double[factors.Count, factors.Count]);
        for (var i = 0; i < factors.Count; i++)
            for (var j = 0; j < factors.Count; j++)
                (rho[i, j], phi[i, j]) = (i == j ? 1 : parameters.FxCorrelation, 1); // FX has no sub-curves
        var k = Norm(factors, rho, phi);
        // One bucket: S is never read, since no other bucket crosses it. Each currency's CR is its own.
        return new("FX", factors, rho, phi, k, k, 0, 1, []);
    }

    static double Norm(IReadOnlyList<Factor> factors, double[,] rho, double[,] phi)
    {
        double sum = 0;
        for (var i = 0; i < factors.Count; i++)
            for (var j = 0; j < factors.Count; j++)
                sum += rho[i, j] * phi[i, j] * factors[i].Ws * factors[j].Ws;
        return Math.Sqrt(Math.Max(sum, 0));
    }

    static string Usd(double amount) => $"{amount.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} USD";

    /// <summary>
    /// ρ_kl and φ_kl between two risk factors of one currency: the tenor correlation and the sub-curve one, or the basis
    /// correlation against anything, where no sub-curve applies.
    /// </summary>
    static (double Rho, double Phi) Correlation(Factor a, Factor b, SimmParameters parameters)
    {
        if (a.RiskType == CrifRiskType.XCcyBasis || b.RiskType == CrifRiskType.XCcyBasis) return (parameters.XCcyBasisCorrelation, 1);
        static double Years(Factor f) => SimmParameters.TenorYears[f.Tenor ?? throw new InvalidOperationException($"The IR curve factor {f.Name} has no tenor.")];
        var rho = a.Tenor == b.Tenor ? 1 : Math.Max(0.30, Math.Exp(-0.35 * Math.Abs(Math.Log(Years(a) / Years(b)))));
        var phi = a.SubCurve == b.SubCurve ? 1 : parameters.SubCurveCorrelation;
        return (rho, phi);
    }

    /// <summary>ψ between two risk classes, which the table holds once for either order.</summary>
    static double Psi(string r, string s, SimmParameters parameters) =>
        parameters.RiskClassCorrelations.TryGetValue((r, s), out var psi) || parameters.RiskClassCorrelations.TryGetValue((s, r), out psi) ? psi
            : throw new ArgumentException($"The parameter table {parameters.Version} has no correlation between the risk classes {r} and {s}.");

    static int Index(IReadOnlyList<string> tenors, string tenor)
    {
        for (var i = 0; i < tenors.Count; i++) if (tenors[i] == tenor) return i;
        throw new InvalidOperationException($"The tenor {tenor} passed validation but is not in the table."); // Validate refuses it first
    }
}
