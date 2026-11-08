using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Workbench.Composition.Contracts;

// The calculation trace of ADR-0011: the terms a provider writes for one (Run, case, scenario, side, measure), and
// the kinds a domain module registers for them. Nothing here knows a regulation: a kind's name, label and formula are
// the module's data.

/// <summary>The method a trace follows and its version. Term ids are stable within one version.</summary>
public sealed record TraceMethod(string Id, string Version, string? Regulation = null);

/// <summary>The engine or implementation that produced a trace.</summary>
public sealed record TraceProducer(string Name, string Version);

/// <summary>What the figure is about, opaque to the kernel (a counterparty with its legal entity, for one).</summary>
public sealed record TraceSubject(string Kind, string Id, string? Label = null);

public enum TermState { Value, Absent, Unavailable, NotReturned }

public sealed record TraceUnit(string Kind, string? Currency = null);

/// <summary>The leaf rows beneath a term: which leaf set, and how many rows the provider wrote.</summary>
public sealed record TermLeaves(string LeafSet, int RowCount);

/// <summary>An additive share of the root, by a named method. Never a replacement for the literal terms.</summary>
public sealed record TermAllocation(string Method, double Value);

/// <summary>
/// One term of a regulatory formula. <c>Value</c> is a number or, for a decision, a text. <c>Governed</c> names the
/// operand of a max, min or clamp that the value took, by slot name or position. A decision says what it read
/// (<c>Inputs</c>), why (<c>Because</c>) and, in <c>Citation</c>, the rule it applied, which is narrower than its
/// kind's (one step of a risk weight cites the paragraph that decided it).
/// </summary>
public sealed record TraceTerm(
    string Id,
    string Kind,
    TermState State,
    int ChildCount,
    string? Parent = null,
    IReadOnlyDictionary<string, string>? Key = null,
    JsonNode? Value = null,
    string? Reason = null,
    IReadOnlyDictionary<string, string>? Slots = null,
    JsonNode? Governed = null,
    TermLeaves? Leaves = null,
    TermAllocation? Allocation = null,
    IReadOnlyDictionary<string, JsonNode?>? Inputs = null,
    IReadOnlyList<string>? Because = null,
    string? Citation = null);

public enum ParameterSource { Regulation, Configuration, Input, Data, Derived }

/// <summary>A matrix or a table (one column) with labelled rows and columns.</summary>
public sealed record TraceMatrix(IReadOnlyList<string> Rows, IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<double>> Values);

public sealed record ParameterConfig(string Key, string Version);

public sealed record ParameterInput(string Entity, string Field);

public sealed record ParameterData(string Source, string Version);

/// <summary>
/// A value an expression uses that is not a term. <c>Scope</c> is the term it applies to and below; none is the whole
/// trace. Exactly one of <c>Value</c>, <c>Text</c> and <c>Matrix</c> is given.
/// </summary>
public sealed record TraceParameter(
    string Name,
    ParameterSource Source,
    string? Scope = null,
    double? Value = null,
    string? Text = null,
    TraceMatrix? Matrix = null,
    TraceUnit? Unit = null,
    string? Citation = null,
    ParameterConfig? Config = null,
    ParameterInput? Input = null,
    ParameterData? Data = null,
    IReadOnlyList<string>? Because = null);

/// <summary>
/// One trace: the header, the parameters, the terms and the leaf rows. A leaf row is an object with <c>leafSet</c>,
/// <c>parent</c> and the columns its leaf set declares.
/// </summary>
public sealed record CalculationTrace(
    string DocumentType,
    string FormatVersion,
    string Measure,
    TraceMethod Method,
    string Depth,
    double Tolerance,
    IReadOnlyList<string> Roots,
    IReadOnlyList<TraceParameter> Parameters,
    IReadOnlyList<TraceTerm> Terms,
    IReadOnlyList<JsonObject> Leaves,
    TraceProducer? Engine = null,
    string? RequestId = null,
    string? ValuationDate = null,
    string? ReportingCurrency = null,
    TraceSubject? Subject = null,
    DatasetProvenance? Provenance = null)
{
    public const string DocumentTypeName = "calculationTrace";
    public const string ContractId = "calculation-trace";
    public const string ContractVersion = "1.0";
}

/// <summary>
/// One trace of a request's output, by what it is of (ADR-0011 §1: one per case, scenario, side and measure).
/// <c>Side</c> is <c>before</c> or <c>after</c>.
/// </summary>
public sealed record TraceEntry(string Case, string Scenario, string Side, string Measure, CalculationTrace Trace);

/// <summary>
/// The traces one invocation wrote, when it priced several sides, cases or measures at once, as one XVA request prices
/// Before and After (ADR-0013 §2). A request that writes one trace may give it alone, as a <see cref="CalculationTrace"/>.
/// </summary>
public sealed record TraceSet(IReadOnlyList<TraceEntry> Traces)
{
    public const string ContractId = "calculation-traces";
    public const string ContractVersion = "1.0";
}

/// <summary>
/// An expression from ADR-0011 §2's closed set of operations. It is data: the kernel evaluates it, and nothing in it
/// is code or text to run.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "op", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(SlotExpr), "slot")]
[JsonDerivedType(typeof(ChildrenExpr), "children")]
[JsonDerivedType(typeof(ParamExpr), "param")]
[JsonDerivedType(typeof(FieldExpr), "field")]
[JsonDerivedType(typeof(ConstExpr), "const")]
[JsonDerivedType(typeof(SumExpr), "sum")]
[JsonDerivedType(typeof(DifferenceExpr), "difference")]
[JsonDerivedType(typeof(ProductExpr), "product")]
[JsonDerivedType(typeof(QuotientExpr), "quotient")]
[JsonDerivedType(typeof(NegateExpr), "negate")]
[JsonDerivedType(typeof(MaxExpr), "max")]
[JsonDerivedType(typeof(MinExpr), "min")]
[JsonDerivedType(typeof(AbsExpr), "abs")]
[JsonDerivedType(typeof(SqrtExpr), "sqrt")]
[JsonDerivedType(typeof(ExpExpr), "exp")]
[JsonDerivedType(typeof(LnExpr), "ln")]
[JsonDerivedType(typeof(NormalCdfExpr), "normalCdf")]
[JsonDerivedType(typeof(ClampExpr), "clamp")]
[JsonDerivedType(typeof(SumLeavesExpr), "sumLeaves")]
[JsonDerivedType(typeof(QuadraticFormExpr), "quadraticForm")]
[JsonDerivedType(typeof(SingleFactorExpr), "singleFactor")]
[JsonDerivedType(typeof(LookupExpr), "lookup")]
public abstract record TraceExpr;

/// <summary>The child term playing a role: the term's <c>slots</c> name it, else the id <c>{term}/{name}</c>.</summary>
public sealed record SlotExpr(string Name) : TraceExpr;

/// <summary>The term's children, optionally of one kind, as a list for an aggregate.</summary>
public sealed record ChildrenExpr(string? Kind = null) : TraceExpr;

/// <summary>The parameter of that name in the nearest scope.</summary>
public sealed record ParamExpr(string Name) : TraceExpr;

/// <summary>In a leaf set's column only: another column of the same row.</summary>
public sealed record FieldExpr(string Name) : TraceExpr;

public sealed record ConstExpr(double Value) : TraceExpr;

public sealed record SumExpr(IReadOnlyList<TraceExpr> Args) : TraceExpr;

/// <summary>The first operand less the others.</summary>
public sealed record DifferenceExpr(IReadOnlyList<TraceExpr> Args) : TraceExpr;

public sealed record ProductExpr(IReadOnlyList<TraceExpr> Args) : TraceExpr;

/// <summary>The first operand divided by the second.</summary>
public sealed record QuotientExpr(IReadOnlyList<TraceExpr> Args) : TraceExpr;

public sealed record NegateExpr(TraceExpr Arg) : TraceExpr;

public sealed record MaxExpr(IReadOnlyList<TraceExpr> Args) : TraceExpr;

public sealed record MinExpr(IReadOnlyList<TraceExpr> Args) : TraceExpr;

public sealed record AbsExpr(TraceExpr Arg) : TraceExpr;

public sealed record SqrtExpr(TraceExpr Arg) : TraceExpr;

public sealed record ExpExpr(TraceExpr Arg) : TraceExpr;

public sealed record LnExpr(TraceExpr Arg) : TraceExpr;

/// <summary>The standard normal cumulative distribution.</summary>
public sealed record NormalCdfExpr(TraceExpr Arg) : TraceExpr;

/// <summary><c>max(min(x, high), low)</c>.</summary>
public sealed record ClampExpr(TraceExpr X, TraceExpr Low, TraceExpr High) : TraceExpr;

/// <summary>The sum of one column over the term's leaf rows.</summary>
public sealed record SumLeavesExpr(string Column) : TraceExpr;

/// <summary>
/// <c>sqrt(Σ_k d_k² + Σ_{k≠l} c_kl · o_k · o_l)</c>. The operands are lists of terms, matched to the matrices' rows and
/// columns by the key attribute <c>Index</c>; <c>c</c> is the element-wise product of the matrix parameters; the
/// off-diagonal operands default to the diagonal ones.
/// </summary>
public sealed record QuadraticFormExpr(TraceExpr Diagonal, IReadOnlyList<string> Matrices, string Index, TraceExpr? OffDiagonal = null) : TraceExpr;

/// <summary>
/// <c>sqrt((Σ_k ρ_k x_k)² + Σ_k (1 − ρ_k²) x_k²)</c> over a list of terms, with <c>ρ_k</c> the parameter
/// <c>Correlation</c> in each term's own scope.
/// </summary>
public sealed record SingleFactorExpr(TraceExpr Terms, string Correlation) : TraceExpr;

/// <summary>The row of a table parameter (a one-column matrix) named by a text operand.</summary>
public sealed record LookupExpr(string Table, TraceExpr Row) : TraceExpr;

public enum TermValueType { Number, Text }

/// <summary>
/// A kind of term, registered once per method version. <c>Label</c> takes the term's key as <c>{attribute}</c>;
/// <c>Tex</c> marks each slot operand with <c>\slot{name}</c> and each parameter with <c>\param{name}</c>. A decision
/// kind (a text value) may have no expression.
/// </summary>
public sealed record TermKind(
    string Kind,
    string Label,
    string Symbol,
    TraceUnit Unit,
    string Citation,
    string Tex,
    TermValueType ValueType = TermValueType.Number,
    TraceExpr? Expression = null);

/// <summary>A column of a leaf set; a computed one has an expression over the row's fields, marked <c>\field{name}</c>.</summary>
public sealed record LeafColumn(string Name, string Label, TraceUnit? Unit = null, TraceExpr? Expression = null, string? Tex = null);

/// <summary>
/// The rows beneath a term. <c>EntityField</c> holds the entity's identity, so the page can go to its row;
/// <c>ComponentField</c>, when given, tells apart the rows of one entity (the legs of a swap).
/// </summary>
public sealed record LeafSetKind(string LeafSet, string EntityField, IReadOnlyList<LeafColumn> Columns, string? ComponentField = null);

/// <summary>The kinds and leaf sets a domain module registers for one method version.</summary>
public sealed record TraceCatalog(TraceMethod Method, IReadOnlyList<TermKind> Kinds, IReadOnlyList<LeafSetKind> LeafSets);
