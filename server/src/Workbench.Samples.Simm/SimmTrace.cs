using System.Text.Json.Nodes;
using Workbench.Composition.Contracts;

namespace Workbench.Samples.Simm;

/// <summary>
/// Spot IM's kinds and leaf set in ADR-0011's catalogue (ADR-0012 §6), once per parameter table: a kind carries the
/// formula as an expression and as TeX, and the engine sends only values. Vega, curvature and base correlation wait for
/// a product with options; <c>simm.addon</c> waits for an add-on to be configured, and joins a regime's children then,
/// so that the regime's sum takes it.
/// </summary>
public static class SimmTraceCatalog
{
    public const string Method = "simm";
    /// <summary>The trace's measure: spot IM's ID on the page, which the standalone workflow's trace is read under too.</summary>
    public const string Measure = "simm.spotIm";
    public const string LeafSet = "simm.crif";

    static readonly TraceUnit Usd = new("amount", "USD");
    const string Citation = "ISDA SIMM v2.x";

    static TermKind Kind(string kind, string label, string symbol, string citation, string tex, TraceExpr expression) =>
        new(kind, label, symbol, Usd, $"{Citation}: {citation}", tex, TermValueType.Number, expression);

    public static TraceCatalog For(SimmParameters parameters) => new(new TraceMethod(Method, parameters.Version, "ISDA SIMM"),
    [
        Kind("simm.im", "IM {direction}", "IM", "the margin is the largest over the regimes the IM CSA serves",
            @"\mathrm{IM} = \max_g \mathrm{SIMM}_g", new MaxExpr([new ChildrenExpr("simm.regime")])),
        Kind("simm.regime", "SIMM {regime}", "SIMM_g", "the total over the product classes",
            @"\mathrm{SIMM} = \sum_p \mathrm{SIMM}_p", new SumExpr([new ChildrenExpr()])),
        Kind("simm.product", "{product}", "SIMM_p", "a product class over its risk classes",
            @"\mathrm{SIMM}_p = \sqrt{\sum_r \mathrm{IM}_r^2 + \sum_{r \ne s} \psi_{rs} \, \mathrm{IM}_r \, \mathrm{IM}_s}",
            new QuadraticFormExpr(new ChildrenExpr("simm.riskClass"), ["psi"], "riskClass")),
        Kind("simm.riskClass", "{riskClass}", "IM_r", "a risk class over its margin types",
            @"\mathrm{IM}_r = \mathrm{DM} + \mathrm{VM} + \mathrm{CM} + \mathrm{BCM}", new SumExpr([new ChildrenExpr()])),
        Kind("simm.delta", "Delta margin", "DM", "delta margin across buckets",
            @"\mathrm{DM} = \sqrt{\sum_b K_b^2 + \sum_{b \ne c} \gamma_{bc} \, g_{bc} \, S_b \, S_c}",
            new QuadraticFormExpr(new ChildrenExpr("simm.bucket"), ["gamma", "g"], "bucket", new ChildrenExpr("simm.bucketSum"))),
        Kind("simm.bucket", "Bucket {bucket}", "K_b", "delta margin within a bucket",
            @"K_b = \sqrt{\sum_k WS_k^2 + \sum_{k \ne l} \rho_{kl} \, \phi_{kl} \, f_{kl} \, WS_k \, WS_l}",
            new QuadraticFormExpr(new ChildrenExpr("simm.factor"), ["rho", "phi", "f"], "factor")),
        Kind("simm.bucketSum", "Bucket sum {bucket}", "S_b", "a bucket's weighted sensitivities, bounded by its margin",
            @"S_b = \max\left(\min\left(\sum_k WS_k, \slot{k}\right), -\slot{k}\right)",
            new ClampExpr(new SumExpr([new ChildrenExpr("simm.factor", "k")]), new NegateExpr(new SlotExpr("k")), new SlotExpr("k"))),
        Kind("simm.factor", "{factor}", "WS_k", "a risk factor's weighted sensitivity",
            @"WS_k = \param{rw} \cdot \param{cr} \cdot \sum_i \mathrm{AmountUSD}_i",
            new ProductExpr([new ParamExpr("rw"), new ParamExpr("cr"), new SumLeavesExpr("amountUsd")])),
    ],
    [
        new LeafSetKind(LeafSet, "tradeId",
        [
            new LeafColumn("line", "CRIF line"),
            new LeafColumn("tradeId", "Trade"),
            new LeafColumn("riskType", "Risk type"),
            new LeafColumn("qualifier", "Qualifier"),
            new LeafColumn("label1", "Label1"),
            new LeafColumn("label2", "Label2"),
            new LeafColumn("amount", "Amount"),
            new LeafColumn("amountCurrency", "Amount currency"),
            new LeafColumn("amountUsd", "Amount USD", Usd),
            new LeafColumn("contribution", "Contribution (Euler)", Usd),
        ], "line"),
    ]);
}

public static partial class SimmEngine
{
    /// <summary>
    /// Writes the trace as the engine computes: one root per direction (ADR-0011 §1), a term for every level of the
    /// formula with its key and its Euler allocation, the parameters each used in its own scope, and the CRIF lines as
    /// leaf rows under their risk factors. Ids are paths, so the same term on Before and After pairs.
    /// </summary>
    sealed class TraceWriter(SimmParameters table)
    {
        readonly List<string> roots = [];
        readonly List<TraceTerm> terms = [];
        readonly List<TraceParameter> parameters = [];
        readonly List<JsonObject> leaves = [];
        readonly string citation = table.IsDemo ? $"SIMM PARAMETERS {table.Version} (made up, not ISDA's calibration)" : $"SIMM parameters {table.Version}";

        static TermAllocation Euler(double contribution) => new("euler", contribution);

        // Which of a clamp's operands governed (ADR-0011 §2): S_b inside its bound is the sum, else −K_b or K_b.
        const int SumGoverned = 0, LowerBoundGoverned = 1, UpperBoundGoverned = 2;

        static Dictionary<string, string> Key(string name, string value) => new() { [name] = value };

        void Add(string id, string kind, double value, int children, string? parent, IReadOnlyDictionary<string, string>? key, TermAllocation? allocation) =>
            terms.Add(new(id, kind, TermState.Value, children, parent, key, JsonValue.Create(value), Allocation: allocation));

        TraceMatrix Matrix(IReadOnlyList<string> labels, Func<int, int, double> value) =>
            new(labels, labels, [.. labels.Select((_, i) => (IReadOnlyList<double>)[.. labels.Select((_, j) => value(i, j))])]);

        void Table(string name, string scope, TraceMatrix matrix) =>
            parameters.Add(new(name, ParameterSource.Regulation, scope, Matrix: matrix, Unit: new("ratio"), Citation: citation));

        public void Direction(ImCsa csa, IReadOnlyList<(string Regime, RegimeCalc Calc)> regimes, int governing)
        {
            var id = SimmDatasets.Name(csa.Direction);
            roots.Add(id);
            var key = new Dictionary<string, string> { ["direction"] = id, ["csa"] = csa.Id };
            if (governing < 0)
            {
                // Nothing in scope: the direction has no IM, which is not an IM of 0 (ADR-0012 §1).
                terms.Add(new(id, "simm.im", TermState.Absent, 0, Key: key));
                return;
            }
            terms.Add(new(id, "simm.im", TermState.Value, regimes.Count, Key: key, Value: JsonValue.Create(regimes[governing].Calc.Term.Value),
                Governed: JsonValue.Create(governing)));
            foreach (var (regime, calc) in regimes) Regime($"{id}/{regime}", id, regime, calc);
        }

        void Regime(string id, string parent, string regime, RegimeCalc calc)
        {
            var term = calc.Term;
            Add(id, "simm.regime", term.Value, term.Children.Count, parent, Key("regime", regime), Euler(term.Value));
            var product = term.Children.Single();
            var productId = $"{id}/{product.Name}";
            Add(productId, "simm.product", product.Value, product.Children.Count, id, Key("product", product.Name), Euler(product.Contribution));
            var classes = product.Children.Select(c => c.Name).ToList();
            Table("psi", productId, Matrix(classes, (r, s) => r == s ? 1 : Psi(classes[r], classes[s], table)));
            foreach (var riskClass in product.Children)
            {
                var classId = $"{productId}/{riskClass.Name}";
                Add(classId, "simm.riskClass", riskClass.Value, riskClass.Children.Count, productId, Key("riskClass", riskClass.Name), Euler(riskClass.Contribution));
                var delta = riskClass.Children.Single();
                var buckets = riskClass.Name == "IR" ? calc.IrBuckets
                    : calc.Fx is { } fx ? [fx] : throw new InvalidOperationException("An FX risk class has its bucket.");
                Delta($"{classId}/delta", classId, delta, buckets, sums: riskClass.Name == "IR");
            }
        }

        void Delta(string id, string parent, SimmTerm delta, IReadOnlyList<Bucket> buckets, bool sums)
        {
            Add(id, "simm.delta", delta.Value, buckets.Count * (sums ? 2 : 1), parent, null, Euler(delta.Contribution));
            var names = buckets.Select(b => b.Name).ToList();
            Table("gamma", id, Matrix(names, (b, c) => b == c ? 1 : table.IrInterBucketCorrelation));
            parameters.Add(new("g", ParameterSource.Derived, id, Matrix: Matrix(names, (b, c) => Math.Min(buckets[b].Cr, buckets[c].Cr) / Math.Max(buckets[b].Cr, buckets[c].Cr)),
                Unit: new("ratio"), Citation: "SIMM concentration risk",
                Because: ["g_bc = min(CR_b, CR_c) / max(CR_b, CR_c)."]));
            for (var b = 0; b < buckets.Count; b++)
            {
                var bucket = buckets[b];
                var bucketId = $"{id}/{bucket.Name}";
                Bucket(bucketId, id, delta.Children[b], bucket, crPerFactor: !sums);
                // S_b sits beside K_b, where the delta margin's cross terms read it; FX has one bucket and no cross term.
                if (sums)
                    terms.Add(new($"{bucketId}:S", "simm.bucketSum", TermState.Value, 0, id, Key("bucket", bucket.Name), JsonValue.Create(bucket.S),
                        Slots: new Dictionary<string, string> { ["k"] = bucketId }, Governed: JsonValue.Create(bucket.Bound switch { 1 => UpperBoundGoverned, -1 => LowerBoundGoverned, _ => SumGoverned })));
            }
        }

        /// <summary>
        /// A bucket's K_b and its risk factors. An IR bucket has one CR for all its factors, so f is 1 throughout; FX
        /// gives each currency its own CR, and has no sub-curves, so φ is 1 throughout.
        /// </summary>
        void Bucket(string id, string parent, SimmTerm term, Bucket bucket, bool crPerFactor)
        {
            Add(id, "simm.bucket", bucket.K, bucket.Factors.Count, parent, Key("bucket", bucket.Name), Euler(term.Contribution));
            var names = bucket.Factors.Select(f => f.Name).ToList();
            Table("rho", id, Matrix(names, (k, l) => bucket.Rho[k, l]));
            if (crPerFactor)
                parameters.Add(new("phi", ParameterSource.Derived, id, Matrix: Matrix(names, (k, l) => bucket.Phi[k, l]), Unit: new("ratio"),
                    Citation: "SIMM sub-curve correlation", Because: ["FX has no sub-curves, so φ_kl = 1."]));
            else Table("phi", id, Matrix(names, (k, l) => bucket.Phi[k, l]));
            parameters.Add(new("f", ParameterSource.Derived, id, Matrix: Matrix(names, (k, l) => bucket.F[k, l]), Unit: new("ratio"),
                Citation: "SIMM concentration risk",
                Because: crPerFactor ? ["f_kl = min(CR_k, CR_l) / max(CR_k, CR_l)."] : ["Every risk factor of the bucket has its CR_b, so f_kl = 1."]));
            if (!crPerFactor)
                parameters.Add(new("cr", ParameterSource.Derived, id, Value: bucket.Cr, Unit: new("ratio"), Citation: "SIMM concentration risk", Because: bucket.CrBecause));
            for (var k = 0; k < bucket.Factors.Count; k++)
            {
                var factor = bucket.Factors[k];
                var factorTerm = term.Children[k];
                var factorId = $"{id}/{factor.Name}";
                terms.Add(new(factorId, "simm.factor", TermState.Value, 0, id, Key("factor", factor.Name), JsonValue.Create(factor.Ws),
                    Leaves: new(SimmTraceCatalog.LeafSet, factor.Lines.Count), Allocation: Euler(factorTerm.Contribution)));
                parameters.Add(new("rw", ParameterSource.Regulation, factorId, Value: factor.Rw, Unit: new("ratio"), Citation: citation, Because: factor.RwBecause));
                if (crPerFactor)
                    parameters.Add(new("cr", ParameterSource.Derived, factorId, Value: factor.Cr, Unit: new("ratio"), Citation: "SIMM concentration risk",
                        Because: ["CR_k = max(1, √(|s_k| / T_FX)).", $"s_k = {Usd(factor.Lines.Sum(l => l.AmountUsd))}.",
                            $"T_FX = {Usd(table.FxConcentrationThreshold)} in {table.Version}."]));
                foreach (var (line, contribution) in factor.Lines.Zip(factorTerm.Leaves))
                    leaves.Add(new()
                    {
                        ["leafSet"] = SimmTraceCatalog.LeafSet, ["parent"] = factorId, ["line"] = line.Line, ["tradeId"] = line.TradeId,
                        ["riskType"] = SimmDatasets.Name(line.RiskType), ["qualifier"] = line.Qualifier, ["label1"] = line.Label1, ["label2"] = line.Label2,
                        ["amount"] = line.Amount, ["amountCurrency"] = line.AmountCurrency, ["amountUsd"] = line.AmountUsd,
                        ["contribution"] = contribution.Contribution,
                    });
            }
        }

        public CalculationTrace Build() => new(CalculationTrace.DocumentTypeName, CalculationTrace.ContractVersion, SimmTraceCatalog.Measure,
            new TraceMethod(SimmTraceCatalog.Method, table.Version, "ISDA SIMM"), "line", 1e-9, roots, parameters, terms, leaves,
            Engine: new TraceProducer("workbench-simm-reference", "1"), ReportingCurrency: "USD");
    }
}
