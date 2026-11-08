using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Workbench.Composition.Contracts;

namespace Workbench.Samples.Ccr;

/// <summary>κ, the capital ratio: a configured value with its key and version, not a constant (ADR-0013 §1).</summary>
public sealed record CapitalRatio(double Value, string Key, string Version);

/// <summary>Spot CCR capital, <c>K = κ · RWA</c> with <c>RWA = RW · EAD</c> (ADR-0013 §1), and what it was made of.</summary>
public sealed record SpotCapital(double Capital, double Rwa, CapitalRatio Kappa, RiskWeightDecision RiskWeight, SaCcrResult Exposure)
{
    public static SpotCapital Of(SaCcrResult exposure, RiskWeightDecision riskWeight, CapitalRatio kappa)
    {
        var rwa = riskWeight.RiskWeight * exposure.Ead;
        return new SpotCapital(kappa.Value * rwa, rwa, kappa, riskWeight, exposure);
    }
}

/// <summary>
/// What the trace's header says beyond the figure: the counterparty with its legal entity, the reporting currency, the
/// engine, and the Run's provenance. <c>TradeInstanceIds</c> maps a trade's source id to the instance the request
/// carried, so a row goes to the trade's row in its grid.
/// </summary>
public sealed record SpotCapitalTraceHeader(
    TraceSubject Counterparty,
    string ReportingCurrency,
    TraceProducer Engine,
    string? ValuationDate = null,
    string? RequestId = null,
    DatasetProvenance? Provenance = null,
    IReadOnlyDictionary<string, string>? TradeInstanceIds = null);

/// <summary>
/// The trace of spot CCR capital (ADR-0013 §4, §5): the kinds and leaf sets of method sa-ccr 2023-01-01, and the
/// reference implementation's writer, which writes every term it computed, from K to each trade's row. Ids run from
/// the netting set (<c>NS-002/pfe/addon/ir/USD/en/b3</c>); a margined netting set has <c>/m</c> and <c>/u</c>
/// beneath it. A segment of an id is the name it stands for with <c>%</c> and <c>/</c> escaped.
/// </summary>
public static class SpotCapitalTrace
{
    public const string Measure = "ccr.spotCapital";

    public static readonly TraceMethod Method = new("sa-ccr", "2023-01-01", "CRE52");

    /// <summary>The ids above the netting sets, which a netting set may not take.</summary>
    public static readonly IReadOnlyList<string> RootIds = ["capital", "rwa", "rw", "ead"];

    public static TraceCatalog Catalog { get; } = Kinds.Catalog();

    public static CalculationTrace Write(SpotCapital capital, SpotCapitalTraceHeader header) => new Writer(capital, header).Write();

    /// <summary>
    /// The trace of a side that was priced and could not be computed: its root, unavailable with why. The side then
    /// reads as unavailable, never as not priced, and its Diff says why there is none (ADR-0011 §1, §4).
    /// </summary>
    public static CalculationTrace Unavailable(string reason, SpotCapitalTraceHeader header) =>
        new(CalculationTrace.DocumentTypeName, CalculationTrace.ContractVersion, Measure, Method, "trade", 1e-9, ["capital"], [],
            [new TraceTerm("capital", "saccr.capital", TermState.Unavailable, 0, Reason: reason)], [],
            header.Engine, header.RequestId, header.ValuationDate, header.ReportingCurrency, header.Counterparty, header.Provenance);

    /// <summary>A name as one segment of a term id.</summary>
    public static string Segment(string name) => name.Replace("%", "%25", StringComparison.Ordinal).Replace("/", "%2F", StringComparison.Ordinal);

    internal static string ClassSegment(AssetClass c) => c switch
    {
        AssetClass.InterestRate => "ir",
        AssetClass.ForeignExchange => "fx",
        AssetClass.Credit => "credit",
        AssetClass.Equity => "equity",
        _ => "commodity",
    };

    internal static string LeafSet(AssetClass c, bool margined) => $"saccr.{ClassSegment(c)}Trade{(margined ? "Margined" : "")}";

    static class Kinds
    {
        static readonly TraceUnit Amount = new("amount");
        static readonly TraceUnit Ratio = new("ratio");
        static readonly TraceUnit Text = new("text");
        static readonly TraceUnit Years = new("years");

        static SlotExpr S(string name) => new(name);
        static ParamExpr P(string name) => new(name);
        static FieldExpr F(string name) => new(name);
        static ConstExpr C(double value) => new(value);
        static SumExpr Sum(params TraceExpr[] args) => new(args);
        static DifferenceExpr Minus(params TraceExpr[] args) => new(args);
        static ProductExpr Times(params TraceExpr[] args) => new(args);
        static QuotientExpr Over(TraceExpr a, TraceExpr b) => new([a, b]);
        static MaxExpr Max(params TraceExpr[] args) => new(args);
        static MinExpr Min(params TraceExpr[] args) => new(args);

        static TermKind Number(string kind, string label, string symbol, TraceUnit unit, string citation, string tex, TraceExpr? expression) =>
            new(kind, label, symbol, unit, citation, tex, TermValueType.Number, expression);

        static TermKind Decision(string kind, string label, string citation) =>
            new(kind, label, label, Text, citation, $@"\text{{{label.ToLowerInvariant()}}}", TermValueType.Text);

        static TermKind ClassAddOn(string kind, string label, string symbol, string citation) =>
            Number(kind, label, $@"\mathrm{{AddOn}}^{{{symbol}}}", Amount, citation, $@"\mathrm{{AddOn}}^{{{symbol}}} = \sum_{{hs}} \mathrm{{AddOn}}_{{hs}}", Sum(new ChildrenExpr()));

        static TermKind SingleFactorSet(string kind, string children, string citation) =>
            Number(kind, "{hedgingSet}", @"\mathrm{AddOn}_{hs}", Amount, citation,
                @"\mathrm{AddOn}_{hs} = \sqrt{\left(\sum_k \rho_k \mathrm{AddOn}_k\right)^2 + \sum_k (1 - \rho_k^2)\, \mathrm{AddOn}_k^2}",
                new SingleFactorExpr(new ChildrenExpr(children), "rho"));

        static TermKind RiskFactor(string kind, string citation) =>
            Number(kind, "{riskFactor}", @"\mathrm{AddOn}_k", Amount, citation, @"\mathrm{AddOn}_k = \param{SF} \cdot \slot{en}", Times(P("SF"), S("en")));

        internal static TraceCatalog Catalog() => new(Method,
        [
            Number("saccr.capital", "Spot CCR capital", "K", Amount, "Configured capital ratio (ADR-0013 §1)",
                @"K = \param{kappa} \cdot \slot{rwa}", Times(P("kappa"), S("rwa"))),
            Number("saccr.rwa", "Risk-weighted assets", "RWA", Amount, "CRE20", @"\mathrm{RWA} = \slot{rw} \cdot \slot{ead}", Times(S("rw"), S("ead"))),
            Number("saccr.counterpartyEad", "EAD of the counterparty", "EAD", Amount, "CRE52.1", @"\mathrm{EAD} = \sum_{ns} \mathrm{EAD}_{ns}",
                Sum(new ChildrenExpr())),
            Number("saccr.marginedEad", "EAD of {nettingSet}", "EAD", Amount, "CRE52.2", @"\mathrm{EAD} = \min(\slot{m}, \slot{u})", Min(S("m"), S("u"))),
            Number("saccr.ead", "EAD of {nettingSet}, {basis}", "EAD", Amount, "CRE52.1", @"\mathrm{EAD} = \param{alpha} \cdot (\slot{rc} + \slot{pfe})",
                Times(P("alpha"), Sum(S("rc"), S("pfe")))),
            Number("saccr.rcUnmargined", "Replacement cost", "RC", Amount, "CRE52.10", @"\mathrm{RC} = \max(\slot{v} - \slot{c}, 0)",
                Max(Minus(S("v"), S("c")), C(0))),
            Number("saccr.rcMargined", "Replacement cost", "RC", Amount, "CRE52.18-19",
                @"\mathrm{RC} = \max(\slot{v} - \slot{c}, \param{TH} + \param{MTA} - \slot{nica}, 0)",
                Max(Minus(S("v"), S("c")), Minus(Sum(P("TH"), P("MTA")), S("nica")), C(0))),
            Number("saccr.marketValue", "Market value", "V", Amount, "CRE52.10", @"V = \sum_i V_i", new SumLeavesExpr("marketValue")),
            Number("saccr.collateral", "Collateral after haircuts", "C", Amount, "CRE52.10, CRE22", @"C = \sum_j C_j", new SumLeavesExpr("value")),
            Number("saccr.nica", "Net independent collateral amount", "NICA", Amount, "CRE52.17", @"\mathrm{NICA} = \sum_{j \in IA} C_j", new SumLeavesExpr("value")),
            Number("saccr.pfe", "Potential future exposure", "PFE", Amount, "CRE52.20", @"\mathrm{PFE} = \slot{multiplier} \cdot \slot{addon}",
                Times(S("multiplier"), S("addon"))),
            Number("saccr.multiplier", "Multiplier", "m", Ratio, "CRE52.22-23",
                @"m = \min\left(1, \param{F} + (1 - \param{F}) \exp\left(\frac{\slot{v} - \slot{c}}{2 (1 - \param{F})\, \slot{addon}}\right)\right)",
                Min(C(1), Sum(P("F"), Times(Minus(C(1), P("F")), new ExpExpr(Over(Minus(S("v"), S("c")), Times(C(2), Minus(C(1), P("F")), S("addon")))))))),
            Number("saccr.multiplierWithoutAddOn", "Multiplier", "m", Ratio, "CRE52.23", "m = 1", C(1)),
            Number("saccr.addOnAggregate", "Aggregate add-on", @"\mathrm{AddOn}", Amount, "CRE52.25", @"\mathrm{AddOn} = \sum_a \mathrm{AddOn}^{(a)}",
                Sum(new ChildrenExpr())),
            ClassAddOn("saccr.irAddOn", "Interest rate add-on", "IR", "CRE52.56-57"),
            ClassAddOn("saccr.fxAddOn", "FX add-on", "FX", "CRE52.58-59"),
            ClassAddOn("saccr.creditAddOn", "Credit add-on", "Credit", "CRE52.60-64"),
            ClassAddOn("saccr.equityAddOn", "Equity add-on", "Equity", "CRE52.65-68"),
            ClassAddOn("saccr.commodityAddOn", "Commodity add-on", "Commodity", "CRE52.69-71"),
            Number("saccr.irHedgingSet", "{hedgingSet}", @"\mathrm{AddOn}_{hs}", Amount, "CRE52.57, CRE52.46-47",
                @"\mathrm{AddOn}_{hs} = \param{SF} \cdot \slot{en}", Times(P("SF"), S("en"))),
            Number("saccr.irEffectiveNotional", "Effective notional", "EN", Amount, "CRE52.57",
                @"\mathrm{EN} = \sqrt{\slot{b1}^2 + \slot{b2}^2 + \slot{b3}^2 + 1.4\, \slot{b1} \slot{b2} + 1.4\, \slot{b2} \slot{b3} + 0.6\, \slot{b1} \slot{b3}}",
                new SqrtExpr(Sum(Times(S("b1"), S("b1")), Times(S("b2"), S("b2")), Times(S("b3"), S("b3")),
                    Times(C(1.4), S("b1"), S("b2")), Times(C(1.4), S("b2"), S("b3")), Times(C(0.6), S("b1"), S("b3"))))),
            Number("saccr.irEffectiveNotionalWithoutOffset", "Effective notional, no offset across buckets", "EN", Amount, "CRE52.57",
                @"\mathrm{EN} = |\slot{b1}| + |\slot{b2}| + |\slot{b3}|",
                Sum(new AbsExpr(S("b1")), new AbsExpr(S("b2")), new AbsExpr(S("b3")))),
            Number("saccr.irBucket", "Maturity bucket {bucket}", "D_k", Amount, "CRE52.57, CRE52.31", @"D_k = \sum_i D_i", new SumLeavesExpr("effectiveNotional")),
            Number("saccr.fxHedgingSet", "{hedgingSet}", @"\mathrm{AddOn}_{hs}", Amount, "CRE52.58-59",
                @"\mathrm{AddOn}_{hs} = \param{SF} \cdot |\slot{en}|", Times(P("SF"), new AbsExpr(S("en")))),
            Number("saccr.effectiveNotional", "Effective notional", "EN", Amount, "CRE52.30", @"\mathrm{EN} = \sum_i D_i", new SumLeavesExpr("effectiveNotional")),
            SingleFactorSet("saccr.creditHedgingSet", "saccr.creditEntity", "CRE52.60-64"),
            RiskFactor("saccr.creditEntity", "CRE52.61, CRE52.72"),
            SingleFactorSet("saccr.equityHedgingSet", "saccr.equityEntity", "CRE52.65-68"),
            RiskFactor("saccr.equityEntity", "CRE52.66, CRE52.72"),
            SingleFactorSet("saccr.commodityHedgingSet", "saccr.commodityType", "CRE52.70"),
            RiskFactor("saccr.commodityType", "CRE52.70, CRE52.72"),

            Number("sarw.riskWeight", "Risk weight", "RW", Ratio, "CRE20", @"\mathrm{RW} = \max(\slot{table}, \slot{floor})", Max(S("table"), S("floor"))),
            Decision("sarw.exposureClass", "Exposure class", "CRE20"),
            Decision("sarw.approach", "Approach", "CRE20"),
            Decision("sarw.ratings", "Eligible ratings", "CRE21"),
            Decision("sarw.ratingUsed", "Rating used", "CRE21.9-11"),
            Decision("sarw.shortTerm", "Short-term treatment", "CRE20"),
            Decision("sarw.dueDiligence", "Due diligence", "CRE20"),
            Decision("sarw.bucket", "Table row", "CRE20"),
            Number("sarw.tableWeight", "Table weight", @"\mathrm{RW}_{table}", Ratio, "CRE20", @"\mathrm{RW}_{table} = \mathrm{lookup}(\slot{bucket})",
                new LookupExpr("rwTable", S("bucket"))),
            Number("sarw.floor", "Sovereign floor", @"\mathrm{RW}_{floor}", Ratio, "CRE20", @"\mathrm{RW}_{floor}", null),
        ],
        [
            new LeafSetKind("saccr.mtm", "tradeId",
            [
                new("tradeId", "Trade"), new("tradeInstanceId", "Trade instance"), new("marketValue", "Market value", Amount), new("valueSource", "Value from"),
            ]),
            new LeafSetKind("saccr.collateralItem", "balanceId",
            [
                new("balanceId", "Balance"), new("direction", "Direction"), new("purpose", "Purpose"), new("type", "Type"), new("currency", "Currency"),
                new("amount", "Amount", Amount), new("sign", "Sign"), new("supervisoryHaircut", "Haircut at 10 days", Ratio),
                new("scale", "Scale from 10 days", Ratio),
                new("haircut", "Haircut", Ratio, Min(C(1), Times(F("supervisoryHaircut"), F("scale"))), @"H = \min(1, \field{supervisoryHaircut} \cdot \field{scale})"),
                new("currencyMismatchHaircut", "Currency mismatch at 10 days", Ratio),
                new("currencyHaircut", "Currency mismatch haircut", Ratio, Min(C(1), Times(F("currencyMismatchHaircut"), F("scale"))),
                    @"H_{fx} = \min(1, \field{currencyMismatchHaircut} \cdot \field{scale})"),
                new("value", "Value", Amount,
                    Times(F("sign"), F("amount"), Max(C(0), Minus(C(1), Times(F("sign"), Sum(F("haircut"), F("currencyHaircut")))))),
                    @"C_j = \field{sign} \cdot \field{amount} \cdot \max(0, 1 - \field{sign} (\field{haircut} + \field{currencyHaircut}))"),
            ]),
            .. Enum.GetValues<AssetClass>().SelectMany(c => new[] { TradeLeaves(c, margined: false), TradeLeaves(c, margined: true) }),
        ]);

        static readonly TraceExpr TenBusinessDays = Over(C(10), C(250));

        static LeafSetKind TradeLeaves(AssetClass assetClass, bool margined)
        {
            var columns = new List<LeafColumn>
            {
                new("tradeId", "Trade"), new("tradeInstanceId", "Trade instance"), new("component", "Component"), new("hedgingSet", "Hedging set"),
                new("primaryRiskFactor", "Primary risk factor"), new("long", "Long"),
            };
            switch (assetClass)
            {
                case AssetClass.InterestRate or AssetClass.Credit:
                    columns.AddRange(
                    [
                        new("notional", "Notional", Amount), new("start", "Start", Years), new("end", "End", Years),
                        new("supervisoryDuration", "Supervisory duration", Years,
                            Max(Over(Minus(new ExpExpr(new NegateExpr(Times(C(0.05), F("start")))), new ExpExpr(new NegateExpr(Times(C(0.05), F("end"))))), C(0.05)), TenBusinessDays),
                            @"SD = \max\left(\frac{e^{-0.05\, \field{start}} - e^{-0.05\, \field{end}}}{0.05}, \frac{10}{250}\right)"),
                        new("adjustedNotional", "Adjusted notional", Amount, Times(F("notional"), F("supervisoryDuration")),
                            @"d = \field{notional} \cdot \field{supervisoryDuration}"),
                    ]);
                    if (assetClass == AssetClass.InterestRate) columns.Add(new("bucket", "Maturity bucket"));
                    break;
                case AssetClass.ForeignExchange:
                    columns.AddRange(
                    [
                        new("notional", "Foreign leg", Amount),
                        new("adjustedNotional", "Adjusted notional", Amount, F("notional"), @"d = \field{notional}"),
                    ]);
                    break;
                default:
                    columns.AddRange(
                    [
                        new("price", "Price of one unit", Amount), new("units", "Units"),
                        new("adjustedNotional", "Adjusted notional", Amount, Times(F("price"), F("units")), @"d = \field{price} \cdot \field{units}"),
                    ]);
                    break;
            }
            columns.AddRange(
            [
                new("optionType", "Option"), new("side", "Bought (+1) or sold (−1)"), new("callPut", "Call (+1) or put (−1)"),
                new("underlying", "Underlying"), new("strike", "Strike"), new("shift", "Shift λ"), new("volatility", "Supervisory volatility", Ratio),
                new("exerciseYears", "Latest exercise", Years),
                new("d1", "d₁", null,
                    Over(Sum(new LnExpr(Over(Sum(F("underlying"), F("shift")), Sum(F("strike"), F("shift")))), Times(C(0.5), F("volatility"), F("volatility"), F("exerciseYears"))),
                        Times(F("volatility"), new SqrtExpr(F("exerciseYears")))),
                    @"d_1 = \frac{\ln\left(\frac{\field{underlying} + \field{shift}}{\field{strike} + \field{shift}}\right) + 0.5\, \field{volatility}^2\, \field{exerciseYears}}{\field{volatility} \sqrt{\field{exerciseYears}}}"),
                new("attachment", "Attachment", Ratio), new("detachment", "Detachment", Ratio), new("protectionBought", "Protection bought"),
                new("delta", "Supervisory delta", null,
                    Times(F("side"), F("callPut"), new NormalCdfExpr(Times(F("callPut"), F("d1")))),
                    @"\delta = \field{side} \cdot \field{callPut} \cdot \Phi(\field{callPut} \cdot \field{d1})"),
                new("deltaBecause", "Delta, because"),
                new("maturity", "Maturity", Years),
                margined
                    ? new LeafColumn("maturityFactor", "Maturity factor", null, Times(C(1.5), new SqrtExpr(Over(P("mpor"), C(250)))), @"MF = 1.5 \sqrt{\param{mpor} / 250}")
                    : new LeafColumn("maturityFactor", "Maturity factor", null, new SqrtExpr(Min(Max(F("maturity"), TenBusinessDays), C(1))),
                        @"MF = \sqrt{\min(\max(\field{maturity}, 10/250), 1)}"),
                new("supervisoryFactor", "Supervisory factor", Ratio),
                new("effectiveNotional", "Effective notional", Amount, Times(F("delta"), F("adjustedNotional"), F("maturityFactor")),
                    @"D = \field{delta} \cdot \field{adjustedNotional} \cdot \field{maturityFactor}"),
            ]);
            return new LeafSetKind(LeafSet(assetClass, margined), "tradeId", columns, "component");
        }
    }

    /// <summary>Writes one trace as the calculator computed it: nothing here recomputes a figure the calculator kept.</summary>
    sealed class Writer(SpotCapital capital, SpotCapitalTraceHeader header)
    {
        readonly List<TraceTerm> terms = [];
        readonly List<TraceParameter> parameters = [];
        readonly List<JsonObject> leaves = [];

        static readonly TraceUnit Ratio = new("ratio");
        static readonly TraceUnit BusinessDays = new("businessDays");
        const string Table2 = "CRE52.72, Table 2";

        public CalculationTrace Write()
        {
            Term("capital", "saccr.capital", capital.Capital, null, 1, slots: new() { ["rwa"] = "rwa" });
            Term("rwa", "saccr.rwa", capital.Rwa, "capital", 2, slots: new() { ["rw"] = "rw", ["ead"] = "ead" });
            parameters.Add(new("kappa", ParameterSource.Configuration, Value: capital.Kappa.Value, Unit: Ratio, Config: new(capital.Kappa.Key, capital.Kappa.Version)));
            parameters.Add(new("alpha", ParameterSource.Regulation, Value: SaCcr.Alpha, Citation: "CRE52.1"));
            parameters.Add(new("F", ParameterSource.Regulation, Value: SaCcr.Floor, Unit: Ratio, Citation: "CRE52.23"));
            RiskWeight(capital.RiskWeight);
            var sets = capital.Exposure.NettingSets;
            Term("ead", "saccr.counterpartyEad", capital.Exposure.Ead, "rwa", sets.Count);
            var ids = new HashSet<string>(RootIds, StringComparer.Ordinal);
            foreach (var set in sets)
            {
                var id = Segment(set.Id);
                if (!ids.Add(id))
                    throw new SaCcrRefused($"The netting set {set.Id} has the id of another netting set or of a term above the netting sets ({string.Join(", ", RootIds)}).");
                NettingSet(id, set);
            }
            return new CalculationTrace(CalculationTrace.DocumentTypeName, CalculationTrace.ContractVersion, Measure, Method, "trade", 1e-9, ["capital"],
                parameters, terms, leaves, header.Engine, header.RequestId, header.ValuationDate, header.ReportingCurrency, header.Counterparty, header.Provenance);
        }

        TraceTerm Term(string id, string kind, double value, string? parent, int children = 0, Dictionary<string, string>? key = null,
            Dictionary<string, string>? slots = null, JsonNode? governed = null, TermLeaves? leafRows = null, IReadOnlyList<string>? because = null)
        {
            var term = new TraceTerm(id, kind, TermState.Value, children, parent, key, JsonValue.Create(value), null, slots, governed, leafRows, Because: because);
            terms.Add(term);
            return term;
        }

        void Absent(string id, string kind, string parent, string reason, Dictionary<string, string>? key = null) =>
            terms.Add(new TraceTerm(id, kind, TermState.Absent, 0, parent, key, Reason: reason));

        // The risk weight: each step of the decision, then the row and the weight the table gave, then the floor.

        void RiskWeight(RiskWeightDecision decision)
        {
            var steps = decision.Steps.ToDictionary(s => s.Key, StringComparer.Ordinal);
            var floorGoverns = decision.Floor is { } f && f > decision.Lookup.Weight;
            Term("rw", "sarw.riskWeight", decision.RiskWeight, "rwa", 9, governed: JsonValue.Create(floorGoverns ? "floor" : "table"));
            foreach (var (id, kind, key) in new[]
            {
                ("class", "sarw.exposureClass", "exposureClass"), ("approach", "sarw.approach", "approach"), ("ratings", "sarw.ratings", "ratings"),
                ("rating", "sarw.ratingUsed", "ratingUsed"), ("shortTerm", "sarw.shortTerm", "shortTerm"), ("dueDiligence", "sarw.dueDiligence", "dueDiligence"),
            })
            {
                var step = steps[key];
                terms.Add(new TraceTerm($"rw/{id}", kind, TermState.Value, 0, "rw", Value: JsonValue.Create(step.Outcome), Inputs: Inputs(step),
                    Because: [step.Because], Citation: step.Citation));
            }

            var lookup = decision.Lookup;
            var table = lookup.Table;
            terms.Add(new TraceTerm("rw/bucket", "sarw.bucket", TermState.Value, 0, "rw", Value: JsonValue.Create(lookup.Row),
                Because: [$"The weight is read from the row {lookup.Row} of {table.Name}, column {lookup.Column}."], Citation: table.Citation));
            var weighed = steps["tableWeight"];
            terms.Add(new TraceTerm("rw/table", "sarw.tableWeight", TermState.Value, 0, "rw", Value: JsonValue.Create(lookup.Weight),
                Slots: new Dictionary<string, string> { ["bucket"] = "rw/bucket" }, Inputs: Inputs(weighed), Because: [weighed.Because], Citation: weighed.Citation));
            var column = table.Columns.ToList().IndexOf(lookup.Column);
            parameters.Add(new("rwTable", table.Source, Scope: "rw/table", Unit: Ratio, Citation: table.Citation,
                Matrix: new TraceMatrix(table.Rows, [lookup.Column], table.Weights.Select(r => (IReadOnlyList<double>)new[] { r[column] }).ToList())));

            var floor = steps["floor"];
            terms.Add(decision.Floor is { } value
                ? new TraceTerm("rw/floor", "sarw.floor", TermState.Value, 0, "rw", Value: JsonValue.Create(value), Inputs: Inputs(floor), Because: [floor.Because],
                    Citation: floor.Citation)
                : new TraceTerm("rw/floor", "sarw.floor", TermState.Absent, 0, "rw", Reason: $"{floor.Outcome}: {floor.Because}", Inputs: Inputs(floor),
                    Because: [floor.Because], Citation: floor.Citation));
        }

        static Dictionary<string, JsonNode?>? Inputs(DecisionStep step)
        {
            if (step.Inputs.Count == 0) return null;
            var inputs = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
            foreach (var input in step.Inputs)
                inputs.Add(input.Name, new JsonObject
                {
                    ["value"] = input.Value,
                    ["source"] = JsonNamingPolicy.CamelCase.ConvertName(input.Source.ToString()),
                    ["where"] = input.Where,
                });
            return inputs;
        }

        // Netting sets, and each basis beneath a margined one.

        void NettingSet(string id, NettingSetExposure set)
        {
            IReadOnlyList<string>? note = set.Note is null ? null : [set.Note];
            if (set.Margined is not { } margined)
            {
                Basis(id, "ead", set, set.Unmargined, note);
                return;
            }
            Term(id, "saccr.marginedEad", set.Ead, "ead", 2, new() { ["nettingSet"] = set.Id }, governed: JsonValue.Create(set.MarginedGoverns ? "m" : "u"), because: note);
            var mpor = set.Mpor ?? throw new InvalidOperationException($"The margined netting set {set.Id} has no MPOR.");
            parameters.Add(new("mpor", ParameterSource.Derived, Scope: id, Value: mpor.Days, Unit: BusinessDays, Citation: "CRE52.50-51", Because: mpor.Because));
            Basis(id + "/m", id, set, margined, null);
            Basis(id + "/u", id, set, set.Unmargined, null);
        }

        void Basis(string b, string parent, NettingSetExposure set, ExposureBasis basis, IReadOnlyList<string>? because)
        {
            var name = basis.Margined ? "margined" : "unmargined";
            Term(b, "saccr.ead", basis.Ead, parent, 2, new() { ["nettingSet"] = set.Id, ["basis"] = name }, because: because);

            var rc = b + "/rc";
            int? mpor = basis.Margined ? (set.Mpor ?? throw new InvalidOperationException($"The margined netting set {set.Id} has no MPOR.")).Days : null;
            var uncollateralised = basis.MarketValue - basis.Collateral;
            if (basis.Margined)
            {
                var margin = set.Set.Margin ?? throw new InvalidOperationException($"The margined netting set {set.Id} has no margining terms.");
                var nica = basis.Nica ?? throw new InvalidOperationException($"The margined basis of {set.Id} has no NICA.");
                var thresholdAndMta = basis.ThresholdAndMta ?? throw new InvalidOperationException($"The margined basis of {set.Id} has no threshold and MTA.");
                double[] operands = [uncollateralised, thresholdAndMta - nica, 0];
                Term(rc, "saccr.rcMargined", basis.ReplacementCost, b, 3, governed: JsonValue.Create(Array.IndexOf(operands, operands.Max())));
                parameters.Add(new("TH", ParameterSource.Input, Scope: rc, Value: margin.Threshold, Input: new($"csa/{margin.CsaId}", "threshold")));
                parameters.Add(new("MTA", ParameterSource.Input, Scope: rc, Value: margin.MinimumTransferAmount, Input: new($"csa/{margin.CsaId}", "minimumTransferAmount")));
            }
            else
            {
                Term(rc, "saccr.rcUnmargined", basis.ReplacementCost, b, 2, governed: JsonValue.Create(uncollateralised >= 0 ? 0 : 1));
            }
            MarketValue(rc + "/v", rc, set.Set.Trades);
            Collateral(rc + "/c", rc, "saccr.collateral", basis.CollateralRows.Where(r => r.InCollateral).ToList(), CollateralBecause(set, basis, mpor));
            if (basis.Margined)
                Collateral(rc + "/nica", rc, "saccr.nica", basis.CollateralRows.Where(r => r.InNica).ToList(), null);

            var pfe = b + "/pfe";
            Term(pfe, "saccr.pfe", basis.Pfe, b, 2);
            var addon = pfe + "/addon";
            if (basis.AddOn > 0)
                Term(pfe + "/multiplier", "saccr.multiplier", basis.Multiplier, pfe, slots: new() { ["v"] = rc + "/v", ["c"] = rc + "/c", ["addon"] = addon },
                    governed: JsonValue.Create(basis.Multiplier >= 1 ? 0 : 1));
            else
                Term(pfe + "/multiplier", "saccr.multiplierWithoutAddOn", basis.Multiplier, pfe,
                    because: ["With no add-on the potential future exposure is nil, whatever the multiplier, which is then 1."]);

            Term(addon, "saccr.addOnAggregate", basis.AddOn, pfe, 5);
            foreach (var assetClass in Enum.GetValues<AssetClass>())
            {
                var id = addon + "/" + ClassSegment(assetClass);
                var kind = $"saccr.{ClassSegment(assetClass)}AddOn";
                var added = basis.AssetClasses.FirstOrDefault(a => a.AssetClass == assetClass);
                if (added is null)
                {
                    Absent(id, kind, addon, $"The netting set has no {Name(assetClass)} exposure.");
                    continue;
                }
                Term(id, kind, added.AddOn, addon, added.HedgingSets.Count);
                foreach (var hedgingSet in added.HedgingSets) HedgingSet(id, hedgingSet, basis.Margined);
            }
        }

        static IReadOnlyList<string>? CollateralBecause(NettingSetExposure set, ExposureBasis basis, int? mpor)
        {
            if (basis.CollateralRows.Count == 0) return null;
            var because = new List<string>();
            if (mpor is { } days)
                because.Add($"Haircuts are scaled from ten business days to the MPOR of {days} business days (CRE52.5, CRE22.59).");
            else
            {
                var longest = set.Set.Trades.SelectMany(t => t.Exposures).Select(e => e.MaturityYears).DefaultIfEmpty(0).Max();
                var horizon = Math.Min(SaCcr.BusinessDaysPerYear, longest * SaCcr.BusinessDaysPerYear) + 9;
                because.Add($"Unmargined, haircuts are scaled from ten business days to the longest trade's maturity, at most a year, plus nine: {Format(horizon)} business days (CRE52.11, CRE22.59).");
            }
            var segregated = basis.CollateralRows.Where(r => !r.InCollateral).Select(r => r.Balance.Id).ToList();
            if (segregated.Count > 0)
                because.Add($"Collateral posted to a segregated, bankruptcy-remote account is not counted: {string.Join(", ", segregated)} (CRE52.10).");
            return because;
        }

        void MarketValue(string id, string parent, IReadOnlyList<SaCcrTrade> trades)
        {
            if (trades.Count == 0)
            {
                Absent(id, "saccr.marketValue", parent, "The netting set has no trade.");
                return;
            }
            Term(id, "saccr.marketValue", trades.Sum(t => t.MarketValue), parent, leafRows: new("saccr.mtm", trades.Count));
            foreach (var trade in trades)
            {
                var row = new JsonObject { ["leafSet"] = "saccr.mtm", ["parent"] = id, ["tradeId"] = trade.TradeId };
                Instance(row, trade.TradeId);
                row["marketValue"] = trade.MarketValue;
                row["valueSource"] = trade.ValueSource;
                leaves.Add(row);
            }
        }

        void Collateral(string id, string parent, string kind, IReadOnlyList<CollateralRow> rows, IReadOnlyList<string>? because)
        {
            if (rows.Count == 0)
            {
                Absent(id, kind, parent, kind == "saccr.nica" ? "No independent amount is held or posted." : "No collateral is held or posted that counts.");
                if (because is not null) terms[^1] = terms[^1] with { Because = because };
                return;
            }
            Term(id, kind, rows.Sum(r => r.Value), parent, leafRows: new("saccr.collateralItem", rows.Count), because: because);
            foreach (var r in rows)
            {
                var balance = r.Balance;
                leaves.Add(new JsonObject
                {
                    ["leafSet"] = "saccr.collateralItem", ["parent"] = id, ["balanceId"] = balance.Id,
                    ["direction"] = JsonNamingPolicy.CamelCase.ConvertName(balance.Direction.ToString()),
                    ["purpose"] = JsonNamingPolicy.CamelCase.ConvertName(balance.Purpose.ToString()),
                    ["type"] = balance.Type, ["currency"] = balance.Currency, ["amount"] = balance.Amount,
                    ["sign"] = balance.Direction == CollateralDirection.Received ? 1.0 : -1.0,
                    ["supervisoryHaircut"] = balance.SupervisoryHaircut, ["scale"] = r.Scale, ["haircut"] = r.Haircut,
                    ["currencyMismatchHaircut"] = r.CurrencyHaircut > 0 ? SaCcr.CurrencyMismatchHaircut : 0,
                    ["currencyHaircut"] = r.CurrencyHaircut, ["value"] = r.Value,
                });
            }
        }

        void Instance(JsonObject row, string tradeId)
        {
            if (header.TradeInstanceIds?.GetValueOrDefault(tradeId) is { } instance) row["tradeInstanceId"] = instance;
        }

        // Hedging sets, down to the maturity bucket or the risk factor, and the trade rows beneath them.

        void HedgingSet(string parent, HedgingSetAddOn set, bool margined)
        {
            var suffix = set.Type switch { HedgingSetType.Basis => " (basis)", HedgingSetType.Volatility => " (volatility)", _ => "" };
            var id = $"{parent}/{Segment(set.Key)}{suffix}";
            var key = new Dictionary<string, string> { ["hedgingSet"] = set.Key, ["type"] = JsonNamingPolicy.CamelCase.ConvertName(set.Type.ToString()) };
            switch (set.AssetClass)
            {
                case AssetClass.InterestRate:
                {
                    Term(id, "saccr.irHedgingSet", set.AddOn, parent, 1, key);
                    SupervisoryFactor(id, set.SupervisoryFactor!.Value, set.Type);
                    var offset = capital.Exposure.Settings.IrOffsetAcrossBuckets;
                    var en = id + "/en";
                    Term(en, offset ? "saccr.irEffectiveNotional" : "saccr.irEffectiveNotionalWithoutOffset", set.EffectiveNotional!.Value, id, 3,
                        because: offset ? null : [$"The bank does not offset across maturity buckets ({capital.Exposure.Settings.Version})."]);
                    foreach (var (bucket, range) in SaCcr.BucketNames.Select((name, i) => (i + 1, name)))
                    {
                        var bucketId = $"{en}/b{bucket}";
                        var bucketKey = new Dictionary<string, string> { ["bucket"] = range };
                        if (set.Buckets.FirstOrDefault(x => x.Bucket == bucket) is not { } held)
                        {
                            Absent(bucketId, "saccr.irBucket", en, "No trade in this hedging set ends in this bucket.", bucketKey);
                            continue;
                        }
                        Term(bucketId, "saccr.irBucket", held.EffectiveNotional, en, 0, bucketKey, leafRows: new(LeafSet(AssetClass.InterestRate, margined), held.Rows.Count));
                        Rows(bucketId, held.Rows, margined);
                    }
                    break;
                }
                case AssetClass.ForeignExchange:
                {
                    Term(id, "saccr.fxHedgingSet", set.AddOn, parent, 1, key);
                    SupervisoryFactor(id, set.SupervisoryFactor!.Value, set.Type);
                    var en = id + "/en";
                    Term(en, "saccr.effectiveNotional", set.EffectiveNotional!.Value, id, leafRows: new(LeafSet(AssetClass.ForeignExchange, margined), set.Rows.Count));
                    Rows(en, set.Rows, margined);
                    break;
                }
                default:
                {
                    var (setKind, factorKind) = set.AssetClass switch
                    {
                        AssetClass.Credit => ("saccr.creditHedgingSet", "saccr.creditEntity"),
                        AssetClass.Equity => ("saccr.equityHedgingSet", "saccr.equityEntity"),
                        _ => ("saccr.commodityHedgingSet", "saccr.commodityType"),
                    };
                    Term(id, setKind, set.AddOn, parent, set.RiskFactors.Count, key);
                    foreach (var factor in set.RiskFactors)
                    {
                        var factorId = $"{id}/{Segment(factor.Key)}";
                        Term(factorId, factorKind, factor.AddOn, id, 1, new() { ["riskFactor"] = factor.Key });
                        SupervisoryFactor(factorId, factor.SupervisoryFactor, set.Type);
                        parameters.Add(new("rho", ParameterSource.Regulation, Scope: factorId, Value: factor.Correlation, Unit: Ratio, Citation: Table2));
                        var en = factorId + "/en";
                        Term(en, "saccr.effectiveNotional", factor.EffectiveNotional, factorId, leafRows: new(LeafSet(set.AssetClass, margined), factor.Rows.Count));
                        Rows(en, factor.Rows, margined);
                    }
                    break;
                }
            }
        }

        void SupervisoryFactor(string scope, double factor, HedgingSetType type) => parameters.Add(type switch
        {
            HedgingSetType.Basis => new("SF", ParameterSource.Derived, Scope: scope, Value: factor, Unit: Ratio, Citation: "CRE52.46",
                Because: [$"Half of Table 2's {Pct(factor * 2)}, for a basis hedging set."]),
            HedgingSetType.Volatility => new("SF", ParameterSource.Derived, Scope: scope, Value: factor, Unit: Ratio, Citation: "CRE52.47",
                Because: [$"Five times Table 2's {Pct(factor / 5)}, for a volatility hedging set."]),
            _ => new("SF", ParameterSource.Regulation, Scope: scope, Value: factor, Unit: Ratio, Citation: Table2),
        });

        void Rows(string parent, IReadOnlyList<ExposureRow> rows, bool margined)
        {
            foreach (var r in rows)
            {
                var e = r.Exposure;
                var row = new JsonObject { ["leafSet"] = LeafSet(e.AssetClass, margined), ["parent"] = parent, ["tradeId"] = e.TradeId };
                Instance(row, e.TradeId);
                if (e.Component.Length > 0) row["component"] = e.Component;
                row["hedgingSet"] = e.HedgingSet;
                row["primaryRiskFactor"] = e.RiskFactor ?? e.HedgingSet;
                row["long"] = r.Delta > 0;
                switch (e.AssetClass)
                {
                    case AssetClass.InterestRate or AssetClass.Credit:
                        row["notional"] = e.Notional;
                        row["start"] = e.StartYears;
                        row["end"] = e.EndYears;
                        row["supervisoryDuration"] = r.SupervisoryDuration;
                        if (r.Bucket is { } bucket) row["bucket"] = bucket;
                        break;
                    case AssetClass.ForeignExchange:
                        row["notional"] = e.Notional;
                        break;
                    default:
                        row["price"] = e.Notional;
                        row["units"] = e.Units;
                        break;
                }
                row["adjustedNotional"] = r.AdjustedNotional;
                switch (e.Delta)
                {
                    case OptionTerms o:
                        row["optionType"] = o.Type == OptionType.Call ? "call" : "put";
                        row["side"] = o.Bought ? 1.0 : -1.0;
                        row["callPut"] = o.Type == OptionType.Call ? 1.0 : -1.0;
                        row["underlying"] = o.Underlying;
                        row["strike"] = o.Strike;
                        row["shift"] = o.Shift;
                        row["volatility"] = r.OptionVolatility;
                        row["exerciseYears"] = o.ExerciseYears;
                        row["d1"] = r.D1;
                        break;
                    case TrancheTerms t:
                        row["attachment"] = t.Attachment;
                        row["detachment"] = t.Detachment;
                        row["protectionBought"] = t.ProtectionBought;
                        break;
                }
                row["delta"] = r.Delta;
                row["deltaBecause"] = r.DeltaBecause;
                row["maturity"] = e.MaturityYears;
                row["maturityFactor"] = r.MaturityFactor;
                row["supervisoryFactor"] = r.SupervisoryFactor;
                row["effectiveNotional"] = r.EffectiveNotional;
                leaves.Add(row);
            }
        }

        static string Name(AssetClass c) => c switch
        {
            AssetClass.InterestRate => "interest rate",
            AssetClass.ForeignExchange => "FX",
            AssetClass.Credit => "credit",
            AssetClass.Equity => "equity",
            _ => "commodity",
        };

        static string Pct(double ratio) => (ratio * 100).ToString("0.####", CultureInfo.InvariantCulture) + "%";

        static string Format(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
