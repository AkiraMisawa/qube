using System.Text.Json;
using System.Text.Json.Nodes;
using Workbench.Composition.Contracts;

namespace Workbench.Composition.Application.Traces;

/// <summary>A trace a provider wrote that the kernel cannot read: a duplicate id, a parent that is not there.</summary>
public sealed class TraceInvalid(string message) : Exception(message);

/// <summary>One side's trace, indexed for reading by term: terms by id, children by parent, leaf rows by parent.</summary>
public sealed class TraceIndex
{
    readonly Dictionary<string, TraceTerm> terms = new(StringComparer.Ordinal);
    readonly Dictionary<string, List<TraceTerm>> children = new(StringComparer.Ordinal);
    readonly Dictionary<string, List<JsonObject>> leaves = new(StringComparer.Ordinal);
    readonly Dictionary<string, List<TraceParameter>> parameters = new(StringComparer.Ordinal);

    public TraceIndex(CalculationTrace trace)
    {
        Trace = trace;
        if (trace.DocumentType != CalculationTrace.DocumentTypeName)
            throw new TraceInvalid($"A calculation trace has documentType '{CalculationTrace.DocumentTypeName}', not '{trace.DocumentType}'.");
        if (!(trace.Tolerance >= 0)) throw new TraceInvalid("The tolerance must be zero or more.");
        foreach (var term in trace.Terms)
        {
            if (!terms.TryAdd(term.Id, term)) throw new TraceInvalid($"Term {term.Id} appears twice.");
            if (term.State == TermState.Value && term.Value is null) throw new TraceInvalid($"Term {term.Id} has state value and no value.");
            if (term.State != TermState.Value && term.Value is not null) throw new TraceInvalid($"Term {term.Id} has state {term.State} and a value.");
            if (term.State is TermState.Unavailable or TermState.NotReturned && string.IsNullOrWhiteSpace(term.Reason))
                throw new TraceInvalid($"Term {term.Id} is {term.State} without a reason.");
        }
        foreach (var term in trace.Terms)
        {
            if (term.Parent is null)
            {
                if (!trace.Roots.Contains(term.Id)) throw new TraceInvalid($"Term {term.Id} has no parent and is not a root.");
                continue;
            }
            if (!terms.ContainsKey(term.Parent)) throw new TraceInvalid($"Term {term.Id} names parent {term.Parent}, which is not in the trace.");
            (children.TryGetValue(term.Parent, out var list) ? list : children[term.Parent] = []).Add(term);
        }
        foreach (var root in trace.Roots)
            if (!terms.ContainsKey(root)) throw new TraceInvalid($"Root {root} is not in the trace.");
        foreach (var row in trace.Leaves)
        {
            var parent = row["parent"]?.GetValue<string>() ?? throw new TraceInvalid("A leaf row has no parent.");
            if (!terms.ContainsKey(parent)) throw new TraceInvalid($"A leaf row names parent {parent}, which is not in the trace.");
            (leaves.TryGetValue(parent, out var list) ? list : leaves[parent] = []).Add(row);
        }
        foreach (var parameter in trace.Parameters)
        {
            var given = (parameter.Value is null ? 0 : 1) + (parameter.Text is null ? 0 : 1) + (parameter.Matrix is null ? 0 : 1);
            if (given != 1) throw new TraceInvalid($"Parameter {parameter.Name} gives {given} of value, text and matrix; exactly one is required.");
            if (parameter.Scope is not null && !terms.ContainsKey(parameter.Scope))
                throw new TraceInvalid($"Parameter {parameter.Name} is scoped to {parameter.Scope}, which is not in the trace.");
            (parameters.TryGetValue(parameter.Name, out var list) ? list : parameters[parameter.Name] = []).Add(parameter);
        }
    }

    public CalculationTrace Trace { get; }

    public TraceTerm? Find(string id) => terms.GetValueOrDefault(id);

    public IReadOnlyList<TraceTerm> Children(string id) => children.TryGetValue(id, out var list) ? list : [];

    public IReadOnlyList<JsonObject> LeafRows(string id) => leaves.TryGetValue(id, out var list) ? list : [];

    /// <summary>The term itself, then its parent, and so up to its root.</summary>
    public IEnumerable<string> SelfAndAncestors(string id)
    {
        for (var current = Find(id); current is not null; current = current.Parent is null ? null : Find(current.Parent))
            yield return current.Id;
    }

    /// <summary>The parameter of that name in the nearest scope at or above the term, else the trace-wide one.</summary>
    public TraceParameter? Parameter(string name, string? fromTermId)
    {
        if (!parameters.TryGetValue(name, out var candidates)) return null;
        if (fromTermId is not null)
            foreach (var scope in SelfAndAncestors(fromTermId))
            {
                var scoped = candidates.FirstOrDefault(p => p.Scope == scope);
                if (scoped is not null) return scoped;
            }
        return candidates.FirstOrDefault(p => p.Scope is null);
    }

    /// <summary>
    /// What a term the provider did not write means: below a term that says it has more children than were written,
    /// it was not returned; otherwise nothing contributes to it, and it is absent (ADR-0011 §1).
    /// </summary>
    public TermState StateOfMissing(TraceTerm referrer) =>
        referrer.ChildCount > Children(referrer.Id).Count ? TermState.NotReturned : TermState.Absent;
}

public enum CheckStatus { Matches, Inconsistent, NotChecked, NotApplicable }

/// <summary>A term's reconciliation against its kind's formula. The term's value is never replaced.</summary>
public sealed record TermCheck(CheckStatus Status, double? Computed = null, string? Reason = null)
{
    public static readonly TermCheck NotApplicable = new(CheckStatus.NotApplicable);
}

/// <summary>A leaf row's computed column that does not equal its formula over the row's other columns.</summary>
public sealed record LeafMismatch(string Column, double Returned, double Computed);

/// <summary>
/// Evaluates kinds' expressions over one side's trace and checks every term against its kind (ADR-0011 §2):
/// <c>|computed − value| ≤ tolerance × max(1, |value|)</c>. An absent operand contributes nothing to a sum, a
/// difference, a max, a min or a sum of leaf rows; any other operand without a value leaves the term not checked.
/// </summary>
public sealed class TraceEvaluator(TraceIndex index, TraceCatalog catalog)
{
    readonly Dictionary<string, TermKind> kinds = catalog.Kinds.ToDictionary(k => k.Kind, StringComparer.Ordinal);
    readonly Dictionary<string, LeafSetKind> leafSets = catalog.LeafSets.ToDictionary(l => l.LeafSet, StringComparer.Ordinal);

    public TermKind? KindOf(string kind) => kinds.GetValueOrDefault(kind);

    public LeafSetKind? LeafSetOf(string leafSet) => leafSets.GetValueOrDefault(leafSet);

    public TermCheck Check(TraceTerm term)
    {
        if (term.State != TermState.Value) return TermCheck.NotApplicable;
        var kind = KindOf(term.Kind);
        if (kind is null) return new(CheckStatus.NotChecked, Reason: $"the kind {term.Kind} is not in the catalogue of {catalog.Method.Id} {catalog.Method.Version}");
        if (kind.Expression is null) return TermCheck.NotApplicable;
        if (term.Value is not JsonValue v || !v.TryGetValue<double>(out var returned))
            return new(CheckStatus.NotChecked, Reason: "the term's value is not a number");
        return Evaluate(kind.Expression, new Scope(term, null)) switch
        {
            Ev.Num n => Compare(returned, n.Value),
            Ev.Missing m => new(CheckStatus.NotChecked, Reason: m.Reason),
            Ev.Txt => new(CheckStatus.NotChecked, Reason: "the formula gives a text"),
            Ev.Items => new(CheckStatus.NotChecked, Reason: "the formula gives a list"),
            _ => throw new InvalidOperationException("Unknown evaluation result."),
        };
    }

    /// <summary>The computed columns of a leaf row that do not match their formula.</summary>
    public IReadOnlyList<LeafMismatch> CheckRow(JsonObject row)
    {
        var leafSet = row["leafSet"]?.GetValue<string>();
        var kind = leafSet is null ? null : LeafSetOf(leafSet);
        if (kind is null) return [];
        var parent = index.Find(row["parent"]!.GetValue<string>());
        var mismatches = new List<LeafMismatch>();
        foreach (var column in kind.Columns.Where(c => c.Expression is not null))
        {
            if (row[column.Name] is not JsonValue cell || !cell.TryGetValue<double>(out var returned)) continue;
            if (Evaluate(column.Expression!, new Scope(parent, row)) is Ev.Num n && Compare(returned, n.Value).Status == CheckStatus.Inconsistent)
                mismatches.Add(new(column.Name, returned, n.Value));
        }
        return mismatches;
    }

    TermCheck Compare(double returned, double computed) =>
        Math.Abs(computed - returned) <= index.Trace.Tolerance * Math.Max(1, Math.Abs(returned))
            ? new(CheckStatus.Matches, computed)
            : new(CheckStatus.Inconsistent, computed, $"{Format(computed)} by its terms, {Format(returned)} returned");

    static string Format(double value) => value.ToString("G10", System.Globalization.CultureInfo.InvariantCulture);

    sealed record Scope(TraceTerm? Term, JsonObject? Row);

    abstract record Ev
    {
        public sealed record Num(double Value) : Ev;
        public sealed record Txt(string Value) : Ev;
        public sealed record Items(IReadOnlyList<(TraceTerm Term, Ev Value)> List) : Ev;
        public sealed record Missing(TermState State, string Reason) : Ev;
    }

    static Ev.Missing NotChecked(string reason) => new(TermState.Unavailable, reason);

    Ev Evaluate(TraceExpr expr, Scope scope) => expr switch
    {
        ConstExpr c => new Ev.Num(c.Value),
        SlotExpr s => Slot(s.Name, scope),
        ChildrenExpr c => Children(c.Kind, scope),
        ParamExpr p => Param(p.Name, scope),
        FieldExpr f => Field(f.Name, scope),
        SumExpr s => Fold(s.Args, scope, dropAbsent: true, Sum),
        DifferenceExpr d => Difference(d.Args, scope),
        ProductExpr p => Fold(p.Args, scope, dropAbsent: false, values => values.Aggregate(1.0, (a, b) => a * b)),
        QuotientExpr q => Fold(q.Args, scope, dropAbsent: false, values => values.Count == 2 ? values[0] / values[1] : double.NaN),
        NegateExpr n => Unary(n.Arg, scope, x => -x),
        MaxExpr m => Fold(m.Args, scope, dropAbsent: true, values => values.Max()),
        MinExpr m => Fold(m.Args, scope, dropAbsent: true, values => values.Min()),
        AbsExpr a => Unary(a.Arg, scope, Math.Abs),
        SqrtExpr s => Unary(s.Arg, scope, Math.Sqrt),
        ExpExpr e => Unary(e.Arg, scope, Math.Exp),
        LnExpr l => Unary(l.Arg, scope, Math.Log),
        NormalCdfExpr n => Unary(n.Arg, scope, NormalDistribution.Cdf),
        ClampExpr c => Fold([c.X, c.Low, c.High], scope, dropAbsent: false, v => Math.Max(Math.Min(v[0], v[2]), v[1])),
        SumLeavesExpr s => SumLeaves(s.Column, scope),
        QuadraticFormExpr q => QuadraticForm(q, scope),
        SingleFactorExpr f => SingleFactor(f, scope),
        LookupExpr l => Lookup(l, scope),
        _ => throw new InvalidOperationException($"Unknown operation {expr.GetType().Name}."),
    };

    static double Sum(IReadOnlyList<double> values) => values.Sum();

    Ev Slot(string name, Scope scope)
    {
        var term = scope.Term ?? throw new InvalidOperationException("A slot is read in a term's scope.");
        var id = term.Slots?.GetValueOrDefault(name) ?? $"{term.Id}/{name}";
        var target = index.Find(id);
        if (target is null)
        {
            var state = index.StateOfMissing(term);
            return new Ev.Missing(state, state == TermState.Absent ? $"{id} is absent" : $"{id} was not returned");
        }
        return ValueOf(target);
    }

    static Ev ValueOf(TraceTerm term) => term.State switch
    {
        TermState.Value when term.Value is JsonValue v && v.TryGetValue<double>(out var number) => new Ev.Num(number),
        TermState.Value when term.Value is JsonValue v && v.TryGetValue<string>(out var text) => new Ev.Txt(text),
        TermState.Value => NotChecked($"{term.Id} has a value that is neither a number nor a text"),
        TermState.Absent => new Ev.Missing(TermState.Absent, $"{term.Id} is absent"),
        _ => new Ev.Missing(term.State, $"{term.Id} is {term.State}: {term.Reason}"),
    };

    Ev Children(string? kind, Scope scope)
    {
        var term = scope.Term ?? throw new InvalidOperationException("Children are read in a term's scope.");
        var list = index.Children(term.Id).Where(c => kind is null || c.Kind == kind).Select(c => (c, ValueOf(c))).ToList();
        if (index.StateOfMissing(term) == TermState.NotReturned)
            return new Ev.Missing(TermState.NotReturned, $"some children of {term.Id} were not returned");
        return new Ev.Items(list);
    }

    Ev Param(string name, Scope scope)
    {
        var parameter = index.Parameter(name, scope.Term?.Id);
        if (parameter is null) return NotChecked($"the parameter {name} is not in the trace");
        if (parameter.Value is { } value) return new Ev.Num(value);
        if (parameter.Text is { } text) return new Ev.Txt(text);
        return NotChecked($"the parameter {name} is a matrix where a number is needed");
    }

    static Ev Field(string name, Scope scope)
    {
        var row = scope.Row ?? throw new InvalidOperationException("A field is read in a leaf row's scope.");
        return row[name] switch
        {
            JsonValue v when v.TryGetValue<double>(out var number) => new Ev.Num(number),
            JsonValue v when v.TryGetValue<string>(out var text) => new Ev.Txt(text),
            JsonValue v when v.TryGetValue<bool>(out var flag) => new Ev.Num(flag ? 1 : 0),
            _ => NotChecked($"the row has no column {name}"),
        };
    }

    /// <summary>Flattens the operands into numbers. Absent operands drop out where <paramref name="dropAbsent"/>.</summary>
    Ev Fold(IReadOnlyList<TraceExpr> args, Scope scope, bool dropAbsent, Func<IReadOnlyList<double>, double> combine)
    {
        var values = new List<double>();
        foreach (var arg in args)
        {
            var result = Evaluate(arg, scope);
            var flat = result is Ev.Items items ? items.List.Select(i => i.Value) : [result];
            foreach (var item in flat)
                switch (item)
                {
                    case Ev.Num n: values.Add(n.Value); break;
                    case Ev.Missing { State: TermState.Absent } when dropAbsent: break;
                    case Ev.Missing m: return m;
                    default: return NotChecked("an operand is a text where a number is needed");
                }
        }
        if (values.Count == 0) return new Ev.Missing(TermState.Absent, "every operand is absent");
        var combined = combine(values);
        return double.IsFinite(combined) ? new Ev.Num(combined) : NotChecked("the formula is not finite for these operands");
    }

    Ev Difference(IReadOnlyList<TraceExpr> args, Scope scope)
    {
        if (args.Count == 0) return NotChecked("a difference needs operands");
        var first = Evaluate(args[0], scope);
        double start;
        switch (first)
        {
            case Ev.Num n: start = n.Value; break;
            case Ev.Missing { State: TermState.Absent }: start = 0; break; // an absent minuend contributes nothing
            case Ev.Missing m: return m;
            default: return NotChecked("an operand is a text where a number is needed");
        }
        var rest = Fold(args.Skip(1).ToList(), scope, dropAbsent: true, Sum);
        return rest switch
        {
            Ev.Num n => new Ev.Num(start - n.Value),
            Ev.Missing { State: TermState.Absent } when first is Ev.Num => new Ev.Num(start),
            Ev.Missing { State: TermState.Absent } => new Ev.Missing(TermState.Absent, "every operand is absent"),
            _ => rest,
        };
    }

    Ev Unary(TraceExpr arg, Scope scope, Func<double, double> f) => Evaluate(arg, scope) switch
    {
        Ev.Num n when double.IsFinite(f(n.Value)) => new Ev.Num(f(n.Value)),
        Ev.Num => NotChecked("the formula is not finite for this operand"),
        Ev.Missing m => m,
        _ => NotChecked("an operand is not a number"),
    };

    Ev SumLeaves(string column, Scope scope)
    {
        var term = scope.Term ?? throw new InvalidOperationException("Leaf rows are read in a term's scope.");
        var rows = index.LeafRows(term.Id);
        if (rows.Count == 0)
            return term.Leaves is { RowCount: > 0 }
                ? new Ev.Missing(TermState.NotReturned, $"the {term.Leaves.RowCount} rows of {term.Id} were not returned")
                : new Ev.Missing(TermState.Absent, $"{term.Id} has no rows");
        if (term.Leaves is { } declared && declared.RowCount != rows.Count)
            return NotChecked($"{term.Id} declares {declared.RowCount} rows and {rows.Count} were written");
        var total = 0.0;
        foreach (var row in rows)
        {
            if (row[column] is not JsonValue v || !v.TryGetValue<double>(out var number))
                return NotChecked($"a row of {term.Id} has no number in {column}");
            total += number;
        }
        return new Ev.Num(total);
    }

    Ev QuadraticForm(QuadraticFormExpr q, Scope scope)
    {
        var diagonal = Keyed(Evaluate(q.Diagonal, scope), q.Index);
        if (diagonal.Error is not null) return diagonal.Error;
        var offDiagonal = q.OffDiagonal is null ? diagonal : Keyed(Evaluate(q.OffDiagonal, scope), q.Index);
        if (offDiagonal.Error is not null) return offDiagonal.Error;
        var matrices = new List<TraceMatrix>();
        foreach (var name in q.Matrices)
        {
            var parameter = index.Parameter(name, scope.Term?.Id);
            if (parameter?.Matrix is null) return NotChecked($"the matrix parameter {name} is not in the trace");
            matrices.Add(parameter.Matrix);
        }
        if (diagonal.Values.Count == 0) return new Ev.Missing(TermState.Absent, "every operand is absent");
        var total = diagonal.Values.Values.Sum(d => d * d);
        foreach (var (k, ok) in offDiagonal.Values)
            foreach (var (l, ol) in offDiagonal.Values)
            {
                if (k == l) continue;
                var c = 1.0;
                foreach (var matrix in matrices)
                {
                    var row = IndexOf(matrix.Rows, k);
                    var col = IndexOf(matrix.Columns, l);
                    if (row < 0 || col < 0) return NotChecked($"the matrix has no entry for ({k}, {l})");
                    c *= matrix.Values[row][col];
                }
                total += c * ok * ol;
            }
        return total >= 0 ? new Ev.Num(Math.Sqrt(total)) : NotChecked("the quadratic form is negative");
    }

    static int IndexOf(IReadOnlyList<string> labels, string label)
    {
        for (var i = 0; i < labels.Count; i++) if (labels[i] == label) return i;
        return -1;
    }

    sealed record KeyedValues(IReadOnlyDictionary<string, double> Values, Ev? Error);

    static KeyedValues Keyed(Ev list, string index)
    {
        if (list is Ev.Missing m) return new(new Dictionary<string, double>(), m);
        if (list is not Ev.Items items) return new(new Dictionary<string, double>(), NotChecked("a quadratic form takes lists of terms"));
        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (term, value) in items.List)
        {
            if (value is Ev.Missing { State: TermState.Absent }) continue;
            if (value is Ev.Missing missing) return new(values, missing);
            if (value is not Ev.Num n) return new(values, NotChecked($"{term.Id} is not a number"));
            var label = term.Key?.GetValueOrDefault(index);
            if (label is null) return new(values, NotChecked($"{term.Id} has no key {index}"));
            if (!values.TryAdd(label, n.Value)) return new(values, NotChecked($"two terms have {index} {label}"));
        }
        return new(values, null);
    }

    Ev SingleFactor(SingleFactorExpr f, Scope scope)
    {
        if (Evaluate(f.Terms, scope) is not Ev.Items items)
            return NotChecked("a single-factor aggregation takes a list of terms");
        double systematic = 0, idiosyncratic = 0;
        var any = false;
        foreach (var (term, value) in items.List)
        {
            if (value is Ev.Missing { State: TermState.Absent }) continue;
            if (value is Ev.Missing missing) return missing;
            if (value is not Ev.Num n) return NotChecked($"{term.Id} is not a number");
            var rho = index.Parameter(f.Correlation, term.Id)?.Value;
            if (rho is null) return NotChecked($"the parameter {f.Correlation} is not in scope of {term.Id}");
            systematic += rho.Value * n.Value;
            idiosyncratic += (1 - rho.Value * rho.Value) * n.Value * n.Value;
            any = true;
        }
        if (!any) return new Ev.Missing(TermState.Absent, "every operand is absent");
        return new Ev.Num(Math.Sqrt(systematic * systematic + idiosyncratic));
    }

    Ev Lookup(LookupExpr l, Scope scope)
    {
        var table = index.Parameter(l.Table, scope.Term?.Id)?.Matrix;
        if (table is null) return NotChecked($"the table parameter {l.Table} is not in the trace");
        if (table.Columns.Count != 1) return NotChecked($"the table {l.Table} has {table.Columns.Count} columns; a lookup reads one");
        switch (Evaluate(l.Row, scope))
        {
            case Ev.Txt t:
                var row = IndexOf(table.Rows, t.Value);
                return row >= 0 ? new Ev.Num(table.Values[row][0]) : NotChecked($"the table {l.Table} has no row {t.Value}");
            case Ev.Missing m:
                return m;
            default:
                return NotChecked("a lookup's row is a text, and this operand is not");
        }
    }
}

/// <summary>
/// The standard normal distribution's CDF, to an absolute error of about 1e-16: the error function by its positive
/// series below 3, and the complementary error function by its continued fraction above, where the series would lose
/// its digits to cancellation.
/// </summary>
public static class NormalDistribution
{
    public static double Cdf(double z) => 0.5 * Erfc(-z / Math.Sqrt(2));

    public static double Erfc(double x)
    {
        if (double.IsNaN(x)) return double.NaN;
        if (x < 0) return 2 - Erfc(-x);
        if (x < 3) return 1 - Erf(x);
        // erfc(x) = exp(-x²)/√π · 1/(x + (1/2)/(x + 1/(x + (3/2)/(x + 2/(x + …))))), by the modified Lentz method.
        const double tiny = 1e-300;
        var f = x;
        var c = x;
        var d = 0.0;
        for (var n = 1; n < 500; n++)
        {
            var a = n / 2.0;
            d = x + a * d;
            d = Math.Abs(d) < tiny ? tiny : d;
            c = x + a / c;
            c = Math.Abs(c) < tiny ? tiny : c;
            d = 1 / d;
            var delta = c * d;
            f *= delta;
            if (Math.Abs(delta - 1) < 1e-16) break;
        }
        return Math.Exp(-x * x) / Math.Sqrt(Math.PI) / f;
    }

    static double Erf(double x)
    {
        // erf(x) = 2/√π · exp(-x²) · Σ_n 2ⁿ x^(2n+1) / (1·3·…·(2n+1)): every term is positive.
        var term = x;
        var sum = x;
        for (var n = 1; n < 200; n++)
        {
            term *= 2 * x * x / (2 * n + 1);
            sum += term;
            if (term < sum * 1e-17) break;
        }
        return 2 / Math.Sqrt(Math.PI) * Math.Exp(-x * x) * sum;
    }
}
