using System.Text.Json.Nodes;
using Workbench.Composition.Contracts;

namespace Workbench.Composition.Application.Traces;

public enum TraceSide { Before, After }

/// <summary>
/// A term on one side: its state, its value or the reason it has none, and its reconciliation. A decision's inputs,
/// reasons and the rule it applied are a side's, because each side decided on its own, and so are the leaf rows the
/// side wrote beneath it (<c>Leaves</c>: which leaf set, and how many).
/// </summary>
public sealed record SideValue(
    TermState State,
    JsonNode? Value,
    string? Reason,
    TermCheck Check,
    JsonNode? Governed = null,
    TermAllocation? Allocation = null,
    IReadOnlyDictionary<string, JsonNode?>? Inputs = null,
    IReadOnlyList<string>? Because = null,
    bool Written = true,
    string? Citation = null,
    TermLeaves? Leaves = null);

public enum DiffState { Value, Absent, Unavailable, NotNumeric }

/// <summary>
/// A term's Diff (ADR-0011 §4): After − Before, an absent side contributing nothing, and unavailable when either side
/// has no value it can stand behind. <c>Changed</c> says, for a text, whether the two sides differ.
/// </summary>
public sealed record TermDiff(DiffState State, double? Value = null, string? Reason = null, bool? Changed = null);

/// <summary>
/// One term, read on every side its case priced. <c>Slots</c> names the child playing each role of its formula where
/// the provider named it; a role it did not name is the child <c>{id}/{role}</c> (ADR-0011 §2).
/// </summary>
public sealed record TermRead(
    string Id,
    string Kind,
    string Label,
    string Symbol,
    string? Citation,
    TraceUnit? Unit,
    IReadOnlyDictionary<string, string>? Key,
    int ChildCount,
    SideValue? Before,
    SideValue? After,
    TermDiff? Diff,
    IReadOnlyDictionary<string, string>? Slots = null);

public sealed record TermCrumb(string Id, string Label, string Symbol);

public enum ChildOrder { Formula, Size }

/// <summary>What the view asks for when it opens a term: the term, its path, a page of its children and its parameters.</summary>
public sealed record TermPage(
    TermRead Term,
    IReadOnlyList<TermCrumb> Path,
    IReadOnlyList<TermRead> Children,
    int ChildTotal,
    int Offset,
    IReadOnlyList<TraceParameter>? BeforeParameters,
    IReadOnlyList<TraceParameter>? AfterParameters);

/// <summary>A leaf row on both sides, paired by its entity and component.</summary>
public sealed record PairedLeafRow(
    string LeafSet,
    string Entity,
    string? Component,
    JsonObject? Before,
    JsonObject? After,
    IReadOnlyList<LeafMismatch> BeforeMismatches,
    IReadOnlyList<LeafMismatch> AfterMismatches);

public sealed record LeafPage(IReadOnlyList<PairedLeafRow> Rows, int Total, int Offset);

/// <summary>
/// The traces of one (Run, case, scenario, measure): one per side the case priced. A case priced on one side is read
/// on that side alone, with no Diff, and the side it did not price is not absent: it is not there (ADR-0011 §4).
/// </summary>
public sealed class TraceReader
{
    readonly TraceCatalog catalog;
    readonly Side? before;
    readonly Side? after;

    sealed record Side(TraceSide Name, TraceIndex Index, TraceEvaluator Evaluator);

    public TraceReader(TraceCatalog catalog, CalculationTrace? before, CalculationTrace? after)
    {
        if (before is null && after is null) throw new TraceInvalid("A case has a trace on at least one side.");
        this.catalog = catalog;
        foreach (var trace in new[] { before, after }.OfType<CalculationTrace>())
            if (trace.Method.Id != catalog.Method.Id || trace.Method.Version != catalog.Method.Version)
                throw new TraceInvalid($"The trace follows {trace.Method.Id} {trace.Method.Version}, and the catalogue is {catalog.Method.Id} {catalog.Method.Version}.");
        if (before is not null && after is not null && before.Measure != after.Measure)
            throw new TraceInvalid($"Before is a trace of {before.Measure} and After of {after.Measure}.");
        this.before = before is null ? null : Open(TraceSide.Before, before);
        this.after = after is null ? null : Open(TraceSide.After, after);
    }

    Side Open(TraceSide name, CalculationTrace trace)
    {
        var index = new TraceIndex(trace);
        return new Side(name, index, new TraceEvaluator(index, catalog));
    }

    public bool PricedBothSides => before is not null && after is not null;

    public IReadOnlyList<TraceSide> Sides => new[] { before, after }.OfType<Side>().Select(s => s.Name).ToList();

    /// <summary>The roots of the traces, in the order the provider wrote them, After's first.</summary>
    public IReadOnlyList<string> Roots =>
        new[] { after, before }.OfType<Side>().SelectMany(s => s.Index.Trace.Roots).Distinct(StringComparer.Ordinal).ToList();

    IEnumerable<Side> Present => new[] { before, after }.OfType<Side>();

    /// <summary>The term written on some side, or null when no side has it.</summary>
    TraceTerm? Written(string id) => Present.Select(s => s.Index.Find(id)).FirstOrDefault(t => t is not null);

    public TermRead? Read(string id)
    {
        var any = Written(id);
        return any is null ? null : Describe(any);
    }

    /// <summary>Opens a term: the term, its path from the root, a page of its children and the parameters in scope.</summary>
    public TermPage? Open(string id, ChildOrder order = ChildOrder.Formula, int offset = 0, int limit = 200)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        var term = Read(id);
        if (term is null) return null;
        var children = ChildIds(id).Select(c => Read(c)!).ToList();
        if (order == ChildOrder.Size)
            children = children.Select((c, i) => (c, i)).OrderByDescending(x => SizeOf(x.c)).ThenBy(x => x.i).Select(x => x.c).ToList();
        return new TermPage(term, PathTo(id), children.Skip(offset).Take(limit).ToList(), children.Count, offset,
            before is null ? null : ParametersInScope(before, id),
            after is null ? null : ParametersInScope(after, id));
    }

    /// <summary>
    /// The size a term is ordered by: the change when both sides were priced, else the allocation the provider gave,
    /// else the value. A term with none of these sorts last.
    /// </summary>
    double SizeOf(TermRead term)
    {
        if (PricedBothSides) return term.Diff is { State: DiffState.Value, Value: { } d } ? Math.Abs(d) : -1;
        var side = term.Before ?? term.After!;
        if (side.Allocation is { } allocation) return Math.Abs(allocation.Value);
        return side.State == TermState.Value && Number(side.Value) is { } v ? Math.Abs(v) : -1;
    }

    /// <summary>The children in the formula's order: Before's as written, then those only After has.</summary>
    IReadOnlyList<string> ChildIds(string id) =>
        new[] { before, after }.OfType<Side>().SelectMany(s => s.Index.Children(id)).Select(t => t.Id).Distinct(StringComparer.Ordinal).ToList();

    IReadOnlyList<TermCrumb> PathTo(string id)
    {
        var chain = new List<TermCrumb>();
        for (var term = Written(id); term is not null; term = term.Parent is null ? null : Written(term.Parent))
        {
            var kind = KindOf(term);
            chain.Add(new TermCrumb(term.Id, LabelOf(term, kind), kind?.Symbol ?? term.Id));
        }
        chain.Reverse();
        return chain;
    }

    static IReadOnlyList<TraceParameter> ParametersInScope(Side side, string id)
    {
        if (side.Index.Find(id) is null) return [];
        var names = side.Index.Trace.Parameters.Select(p => p.Name).Distinct(StringComparer.Ordinal);
        return names.Select(n => side.Index.Parameter(n, id)).OfType<TraceParameter>().ToList();
    }

    /// <summary>
    /// The leaf rows below a term on each side, paired by entity and component, a page at a time: in the order the
    /// provider wrote them, or by a column. A row sorts by its After value, else its Before value; numbers in order,
    /// then texts, and a row without the column last in either direction.
    /// </summary>
    public LeafPage Leaves(string id, int offset = 0, int limit = 200, string? sort = null, bool descending = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        var rows = new List<PairedLeafRow>();
        var at = new Dictionary<(string, string, string?), int>();
        foreach (var side in Present)
            foreach (var row in side.Index.LeafRows(id))
            {
                var leafSet = row["leafSet"]?.GetValue<string>() ?? throw new TraceInvalid($"A leaf row under {id} has no leaf set.");
                var kind = side.Evaluator.LeafSetOf(leafSet) ?? throw new TraceInvalid($"The leaf set {leafSet} is not in the catalogue of {catalog.Method.Id} {catalog.Method.Version}.");
                var entity = Text(row[kind.EntityField]) ?? throw new TraceInvalid($"A {leafSet} row under {id} has no {kind.EntityField}.");
                var component = kind.ComponentField is null ? null : Text(row[kind.ComponentField]);
                var key = (leafSet, entity, component);
                var mismatches = side.Evaluator.CheckRow(row);
                if (at.TryGetValue(key, out var i))
                {
                    if (side.Name == TraceSide.Before ? rows[i].Before is not null : rows[i].After is not null)
                        throw new TraceInvalid($"Two {leafSet} rows under {id} are {entity}{(component is null ? "" : " " + component)} on {side.Name}.");
                    rows[i] = side.Name == TraceSide.Before ? rows[i] with { Before = row, BeforeMismatches = mismatches } : rows[i] with { After = row, AfterMismatches = mismatches };
                }
                else
                {
                    at[key] = rows.Count;
                    rows.Add(side.Name == TraceSide.Before
                        ? new PairedLeafRow(leafSet, entity, component, row, null, mismatches, [])
                        : new PairedLeafRow(leafSet, entity, component, null, row, [], mismatches));
                }
            }
        if (sort is not null) rows = Sorted(rows, sort, descending);
        return new LeafPage(rows.Skip(offset).Take(limit).ToList(), rows.Count, offset);
    }

    static List<PairedLeafRow> Sorted(List<PairedLeafRow> rows, string column, bool descending)
    {
        var keyed = rows.Select((row, i) => (row, i, value: (row.After ?? row.Before)?[column])).ToList();
        var known = keyed.Where(r => r.value is not null).ToList();
        var numbers = known.Where(r => Number(r.value) is not null).ToList();
        var texts = known.Where(r => Number(r.value) is null).ToList();
        var ordered = descending
            ? texts.OrderByDescending(r => Text(r.value), StringComparer.Ordinal).ThenBy(r => r.i)
                .Concat(numbers.OrderByDescending(r => Number(r.value)!.Value).ThenBy(r => r.i))
            : numbers.OrderBy(r => Number(r.value)!.Value).ThenBy(r => r.i)
                .Concat(texts.OrderBy(r => Text(r.value), StringComparer.Ordinal).ThenBy(r => r.i));
        return [.. ordered.Concat(keyed.Where(r => r.value is null)).Select(r => r.row)];
    }

    static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : node?.ToJsonString();

    static double? Number(JsonNode? node) => node is JsonValue v && v.TryGetValue<double>(out var x) ? x : null;

    TermKind? KindOf(TraceTerm term) => (before ?? after)!.Evaluator.KindOf(term.Kind);

    static string LabelOf(TraceTerm term, TermKind? kind)
    {
        if (kind is null) return term.Id;
        var label = kind.Label;
        if (term.Key is not null)
            foreach (var (attribute, value) in term.Key) label = label.Replace("{" + attribute + "}", value, StringComparison.Ordinal);
        return label;
    }

    TermRead Describe(TraceTerm written)
    {
        var kind = KindOf(written);
        var b = before is null ? null : OnSide(before, written);
        var a = after is null ? null : OnSide(after, written);
        var childCount = Present.Select(s => s.Index.Find(written.Id)?.ChildCount ?? 0).Max();
        var slots = Present.Select(s => s.Index.Find(written.Id)?.Slots).FirstOrDefault(s => s is not null);
        return new TermRead(written.Id, written.Kind, LabelOf(written, kind), kind?.Symbol ?? written.Id, kind?.Citation, kind?.Unit,
            written.Key, childCount, b, a, b is not null && a is not null ? Diff(b, a) : null, slots);
    }

    SideValue OnSide(Side side, TraceTerm written)
    {
        var term = side.Index.Find(written.Id);
        if (term is not null)
            return new SideValue(term.State, term.Value, term.Reason, side.Evaluator.Check(term), term.Governed, term.Allocation, term.Inputs, term.Because,
                Citation: term.Citation, Leaves: term.Leaves);
        var (state, reason) = Missing(side, written);
        return new SideValue(state, null, reason, TermCheck.NotApplicable, Written: false);
    }

    /// <summary>
    /// What a term the side did not write means (ADR-0011 §1): below the nearest ancestor the side wrote, absent when
    /// that ancestor wrote all its children, else not returned.
    /// </summary>
    (TermState, string) Missing(Side side, TraceTerm written)
    {
        for (var parentId = written.Parent; parentId is not null; parentId = Written(parentId)?.Parent)
        {
            var parent = side.Index.Find(parentId);
            if (parent is null) continue;
            if (parent.State is TermState.Unavailable or TermState.NotReturned)
                return (parent.State, $"{parent.Id} is {parent.State}: {parent.Reason}");
            return side.Index.StateOfMissing(parent) == TermState.NotReturned
                ? (TermState.NotReturned, $"{parent.Id} has {parent.ChildCount} terms below it and {side.Index.Children(parent.Id).Count} were returned")
                : (TermState.Absent, "nothing contributes to it on this side");
        }
        return (TermState.Absent, "nothing contributes to it on this side");
    }

    static TermDiff Diff(SideValue b, SideValue a)
    {
        foreach (var (side, name) in new[] { (b, "Before"), (a, "After") })
        {
            if (side.State is TermState.Unavailable or TermState.NotReturned)
                return new(DiffState.Unavailable, Reason: $"{name} is {(side.State == TermState.NotReturned ? "not returned" : "unavailable")}: {side.Reason}");
            if (side.Check.Status == CheckStatus.Inconsistent)
                return new(DiffState.Unavailable, Reason: $"{name} does not reconcile: {side.Check.Reason}");
        }
        if (b.State == TermState.Absent && a.State == TermState.Absent) return new(DiffState.Absent);
        var bNumber = b.State == TermState.Value ? Number(b.Value) : null;
        var aNumber = a.State == TermState.Value ? Number(a.Value) : null;
        if ((b.State == TermState.Value && bNumber is null) || (a.State == TermState.Value && aNumber is null))
            return new(DiffState.NotNumeric, Changed: b.State != a.State || !JsonNode.DeepEquals(b.Value, a.Value));
        return new(DiffState.Value, (aNumber ?? 0) - (bNumber ?? 0)); // an absent side contributes nothing
    }
}
