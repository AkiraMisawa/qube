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

public sealed record SimmResult(string ParametersVersion, IReadOnlyList<DirectionIm> Directions)
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
public static class SimmEngine
{
    public static SimmResult Calculate(IReadOnlyList<CrifLine> crif, IReadOnlyList<ImCsa> csas, SimmParameters parameters)
    {
        Validate(crif, parameters);
        var directions = csas.Select(csa =>
        {
            // A regime is computed when the CSA serves it and a line it holds names it (ADR-0012 §1). With none, the
            // direction has no IM: nothing is in scope, which is not an IM of 0.
            var held = csa.Trades.ToHashSet(StringComparer.Ordinal);
            var inScope = crif.Where(l => held.Contains(l.TradeId)).ToList();
            var regimes = csa.Regimes
                .Select(regime => (regime, lines: inScope.Where(l => Regulations(l, csa.Direction).Contains(regime)).ToList()))
                .Where(r => r.lines.Count > 0)
                .Select(r => new RegimeSimm(r.regime, Regime(r.regime, r.lines, parameters)))
                .ToList();
            var governing = regimes.MaxBy(r => r.Total.Value);
            return new DirectionIm(csa.Direction, csa.Id, governing?.Total.Value, governing?.Regime, regimes);
        }).ToList();
        return new SimmResult(parameters.Version, directions);
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

    sealed class Factor(string name, CrifRiskType riskType, string? tenor, string? subCurve, IReadOnlyList<CrifLine> lines, double rw, double cr)
    {
        public string Name { get; } = name;
        public CrifRiskType RiskType { get; } = riskType;
        public string? Tenor { get; } = tenor;
        public string? SubCurve { get; } = subCurve;
        public IReadOnlyList<CrifLine> Lines { get; } = lines;
        public double Scale { get; } = rw * cr;
        public double Ws { get; } = rw * cr * lines.Sum(l => l.AmountUsd);
        public double Gradient { get; set; }
    }

    sealed record Bucket(string Name, IReadOnlyList<Factor> Factors, double[,] Correlation, double K, double S, int Bound, double Cr);

    /// <summary>
    /// SIMM under one regime: Σ_p SIMM_p over the product classes the lines reach. The PoC's risks are all in Rates/FX,
    /// whose SIMM is √(Σ_r IM_r² + Σ_{r≠s} ψ_rs IM_r IM_s) over IR and FX; a risk class with no line is absent.
    /// </summary>
    static SimmTerm Regime(string regime, IReadOnlyList<CrifLine> lines, SimmParameters parameters)
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
        return Term(regime, "simm.regime", product, [productTerm]);
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
                for (var l = 0; l < n; l++) dK += bucket.Correlation[k, l] * bucket.Factors[l].Ws;
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
        var cr = Math.Max(1, Math.Sqrt(Math.Abs(curveSum) / parameters.IrConcentrationThresholds[group]));
        var factors = lines.GroupBy(l => (l.RiskType, l.Label1, l.Label2)).Select(g => g.Key.RiskType == CrifRiskType.XCcyBasis
            ? new Factor("XCcyBasis", g.Key.RiskType, null, null, [.. g], parameters.XCcyBasisRiskWeight, cr)
            : new Factor($"{g.Key.Label2} {g.Key.Label1}", g.Key.RiskType, g.Key.Label1, g.Key.Label2, [.. g],
                parameters.IrRiskWeights[group][Index(parameters.Tenors, g.Key.Label1!)], cr)).ToList();
        var rho = new double[factors.Count, factors.Count];
        for (var i = 0; i < factors.Count; i++)
            for (var j = 0; j < factors.Count; j++)
                rho[i, j] = i == j ? 1 : Correlation(factors[i], factors[j], parameters);
        var k = Norm(factors, rho);
        var sum = factors.Sum(f => f.Ws);
        var bound = sum > k ? 1 : sum < -k ? -1 : 0;
        return new(currency, factors, rho, k, bound == 0 ? sum : bound * k, bound, cr);
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
            return new Factor(g.Key, CrifRiskType.Fx, null, null, [.. g], parameters.FxRiskWeight, cr);
        }).ToList();
        var rho = new double[factors.Count, factors.Count];
        for (var i = 0; i < factors.Count; i++)
            for (var j = 0; j < factors.Count; j++)
                rho[i, j] = i == j ? 1 : parameters.FxCorrelation;
        var k = Norm(factors, rho);
        return new("FX", factors, rho, k, k, 0, 1);
    }

    static double Norm(IReadOnlyList<Factor> factors, double[,] rho)
    {
        double sum = 0;
        for (var i = 0; i < factors.Count; i++)
            for (var j = 0; j < factors.Count; j++)
                sum += rho[i, j] * factors[i].Ws * factors[j].Ws;
        return Math.Sqrt(Math.Max(sum, 0));
    }

    /// <summary>ρ_kl φ_kl between two risk factors of one currency: the tenor correlation times the sub-curve one, or the basis one.</summary>
    static double Correlation(Factor a, Factor b, SimmParameters parameters)
    {
        if (a.RiskType == CrifRiskType.XCcyBasis || b.RiskType == CrifRiskType.XCcyBasis) return parameters.XCcyBasisCorrelation;
        var rho = a.Tenor == b.Tenor ? 1
            : Math.Max(0.30, Math.Exp(-0.35 * Math.Abs(Math.Log(SimmParameters.TenorYears[a.Tenor!] / SimmParameters.TenorYears[b.Tenor!]))));
        var phi = a.SubCurve == b.SubCurve ? 1 : parameters.SubCurveCorrelation;
        return rho * phi;
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
