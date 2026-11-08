using System.Text.RegularExpressions;
using Workbench.Composition.Contracts;

namespace Workbench.Composition.Application.Traces;

/// <summary>
/// The catalogues of kinds and leaf sets the modules registered, one per method version (ADR-0011 §2). A kind's TeX is
/// checked against its expression when it is registered, so the page never draws a formula that is not the one the
/// kernel evaluates.
/// </summary>
public static partial class TraceCatalogs
{
    [GeneratedRegex(@"\\slot\{([^{}]+)\}")] private static partial Regex SlotMarker();
    [GeneratedRegex(@"\\param\{([^{}]+)\}")] private static partial Regex ParamMarker();
    [GeneratedRegex(@"\\field\{([^{}]+)\}")] private static partial Regex FieldMarker();

    /// <summary>
    /// What is wrong with a catalogue, by kind: a TeX template whose <c>\slot</c>, <c>\param</c> or <c>\field</c>
    /// markers are not exactly the expression's slot, parameter and field operands; a field outside a leaf set; a
    /// duplicate kind, leaf set or column; a column a computed column reads that its leaf set does not declare.
    /// </summary>
    public static IEnumerable<string> Problems(TraceCatalog catalog)
    {
        var at = $"{catalog.Method.Id} {catalog.Method.Version}";
        foreach (var duplicate in catalog.Kinds.GroupBy(k => k.Kind, StringComparer.Ordinal).Where(g => g.Count() > 1))
            yield return $"{at}: the kind {duplicate.Key} is registered twice.";
        foreach (var kind in catalog.Kinds)
        {
            if (string.IsNullOrWhiteSpace(kind.Tex)) yield return $"{at}: {kind.Kind} has no TeX.";
            if (kind.Expression is null)
            {
                if (kind.ValueType == TermValueType.Number && SlotMarker().IsMatch(kind.Tex))
                    yield return $"{at}: {kind.Kind} marks slots in its TeX but has no expression.";
                continue;
            }
            var operands = Operands(kind.Expression);
            if (operands.Fields.Count > 0) yield return $"{at}: {kind.Kind} reads a field, which only a leaf set's column may.";
            foreach (var problem in Mismatch(at, kind.Kind, "slot", Marked(SlotMarker(), kind.Tex), operands.Slots)) yield return problem;
            foreach (var problem in Mismatch(at, kind.Kind, "param", Marked(ParamMarker(), kind.Tex), operands.Params)) yield return problem;
        }
        foreach (var duplicate in catalog.LeafSets.GroupBy(l => l.LeafSet, StringComparer.Ordinal).Where(g => g.Count() > 1))
            yield return $"{at}: the leaf set {duplicate.Key} is registered twice.";
        foreach (var leafSet in catalog.LeafSets)
        {
            var columns = leafSet.Columns.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
            if (columns.Count != leafSet.Columns.Count) yield return $"{at}: {leafSet.LeafSet} declares a column twice.";
            foreach (var column in leafSet.Columns.Where(c => c.Expression is not null))
            {
                var name = $"{leafSet.LeafSet}.{column.Name}";
                var operands = Operands(column.Expression!);
                if (operands.Slots.Count > 0) yield return $"{at}: {name} reads a slot; a column reads its row's fields.";
                foreach (var field in operands.Fields.Where(f => !columns.Contains(f)))
                    yield return $"{at}: {name} reads {field}, which {leafSet.LeafSet} does not declare.";
                if (column.Tex is null) { yield return $"{at}: {name} is computed and has no TeX."; continue; }
                foreach (var problem in Mismatch(at, name, "field", Marked(FieldMarker(), column.Tex), operands.Fields)) yield return problem;
                foreach (var problem in Mismatch(at, name, "param", Marked(ParamMarker(), column.Tex), operands.Params)) yield return problem;
            }
        }
    }

    static HashSet<string> Marked(Regex marker, string tex) =>
        marker.Matches(tex).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

    static IEnumerable<string> Mismatch(string at, string kind, string marker, HashSet<string> marked, HashSet<string> used)
    {
        foreach (var name in used.Except(marked).Order(StringComparer.Ordinal))
            yield return $"{at}: {kind}'s TeX does not mark the {marker} {name} that its expression reads.";
        foreach (var name in marked.Except(used).Order(StringComparer.Ordinal))
            yield return $"{at}: {kind}'s TeX marks the {marker} {name}, which its expression does not read.";
    }

    sealed record OperandNames(HashSet<string> Slots, HashSet<string> Params, HashSet<string> Fields);

    static OperandNames Operands(TraceExpr root)
    {
        var names = new OperandNames(new(StringComparer.Ordinal), new(StringComparer.Ordinal), new(StringComparer.Ordinal));
        void Walk(TraceExpr expr)
        {
            switch (expr)
            {
                case SlotExpr s: names.Slots.Add(s.Name); break;
                case ParamExpr p: names.Params.Add(p.Name); break;
                case FieldExpr f: names.Fields.Add(f.Name); break;
                case ChildrenExpr { Of: { } of }: names.Slots.Add(of); break;
                case ChildrenExpr or ConstExpr or SumLeavesExpr: break;
                case SumExpr e: e.Args.ToList().ForEach(Walk); break;
                case DifferenceExpr e: e.Args.ToList().ForEach(Walk); break;
                case ProductExpr e: e.Args.ToList().ForEach(Walk); break;
                case QuotientExpr e: e.Args.ToList().ForEach(Walk); break;
                case MaxExpr e: e.Args.ToList().ForEach(Walk); break;
                case MinExpr e: e.Args.ToList().ForEach(Walk); break;
                case NegateExpr e: Walk(e.Arg); break;
                case AbsExpr e: Walk(e.Arg); break;
                case SqrtExpr e: Walk(e.Arg); break;
                case ExpExpr e: Walk(e.Arg); break;
                case LnExpr e: Walk(e.Arg); break;
                case NormalCdfExpr e: Walk(e.Arg); break;
                case ClampExpr e: Walk(e.X); Walk(e.Low); Walk(e.High); break;
                case QuadraticFormExpr e: Walk(e.Diagonal); if (e.OffDiagonal is not null) Walk(e.OffDiagonal); break;
                case SingleFactorExpr e: Walk(e.Terms); break;
                case LookupExpr e: Walk(e.Row); break;
                default: throw new InvalidOperationException($"Unknown operation {expr.GetType().Name}.");
            }
        }
        Walk(root);
        return names;
    }
}
