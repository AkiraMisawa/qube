using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Qube.Composition.Application.Workspaces;

namespace Qube.Composition.Application.Tables;

// ADR-0016 §2 and its notes 7 and 8 of 2026-11-11: one selector says which rows. It is a typed value; the command line
// is one spelling of it, read here; and one resolver serves the line, the AI and a workflow.

/// <summary>One condition of <c>where</c>: a field by its words, an operator, and the value as written (<c>in</c>: values separated by commas).</summary>
public sealed record Condition(string Field, string Operator, string Value);

/// <summary>
/// Which rows (ADR-0016 §2): the table, by id or by words, or none where the rows say it; then every row, a subset, or
/// rows by id, each narrowed by conditions joined by <c>and</c>.
/// </summary>
public sealed record RowSelector(string? Table = null, bool All = false, string? Subset = null, IReadOnlyList<string>? Ids = null, IReadOnlyList<Condition>? Where = null)
{
    public IReadOnlyList<string> Ids { get; init; } = Ids ?? [];
    public IReadOnlyList<Condition> Where { get; init; } = Where ?? [];

    /// <summary>True where no row is said: the rows of a table of one row, or nothing a command can act on.</summary>
    public bool SaysNoRows => !All && Subset is null && Ids.Count == 0 && Where.Count == 0;
}

/// <summary>A selector read from the start of a line, and the words it left for the verb.</summary>
public sealed record SelectorReading(RowSelector Selector, IReadOnlyList<string> Rest);

/// <summary>
/// The rows a selector means in a workspace. <paramref name="Whole"/> names the subsets the rows are the whole of, where
/// they were said as <c>all</c> or a subset and nothing narrowed them, so a verb may write a whole-set edit; null
/// otherwise. <paramref name="Where"/> is each condition as it was read (<c>taken &gt; 2031-11-11</c>).
/// </summary>
public sealed record ResolvedRows(BoundTable Table, IReadOnlyList<ITableRow> Rows, IReadOnlyList<TableSubset>? Whole, IReadOnlyList<string> Where);

/// <summary>Reads the line's spelling of a selector (ADR-0016 §2, note 8). Pure: it reads words against descriptions and looks at no row.</summary>
public static partial class SelectorReader
{
    public const string AllWord = "all";
    public const string WhereWord = "where";
    public const string AndWord = "and";
    public const string InWord = "in";

    const string HowACondition = "a condition is a field, an operator (=, !=, <, <=, >, >=, ~, in) and a value, and conditions are joined by and.";

    /// <summary>
    /// Reads the rows a line starts with: a table's words where it names one, then <c>all</c>, a subset, or ids separated
    /// by commas, then <c>where</c> and its conditions. What follows is the verb's. A line that starts with a field of a
    /// table of one row says no rows. Where the table is said beside the rows (<paramref name="within"/>), the rows are
    /// read as that table's: its subsets, and no row where it has one.
    /// </summary>
    public static SelectorReading Read(string text, IReadOnlyList<TableDescriptor> tables, TableDescriptor? within = null)
    {
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var at = 0;

        TableDescriptor? table = null;
        if (Longest(words, at, tables, TableWords.Names) is (var named, var taken))
        {
            if (named.Count > 1)
                throw new TableRefused("AmbiguousTable", $"{Said(words, at, taken)} could be {TableWords.Either(named.Select(t => t.TableId))}; name one by its id.",
                    Said(words, at, taken), [.. named.Select(t => t.TableId)]);
            (table, at) = (named[0], at + taken);
        }

        var all = false;
        string? subset = null;
        List<string> ids = [];
        var said = table ?? within;
        var scope = said ?? tables.FirstOrDefault(t => t.Main);
        if (at < words.Length && Is(words[at], AllWord)) (all, at) = (true, at + 1);
        // A registration's subsets share no word (TableRegister.Of), so the words name one.
        else if (scope is not null && Longest(words, at, scope.Subsets, TableWords.Names) is (var subsets, var length)) (subset, at) = (subsets[0].Name, at + length);
        else if (at < words.Length && !Is(words[at], WhereWord) && said is not { SingleRow: true } && !(said is null && StartsWithAFieldOfOneRow(words, at, tables)))
            at = List(words, at, ids);

        List<Condition> where = [];
        if (at < words.Length && Is(words[at], WhereWord))
        {
            do
            {
                at++;
                at = ReadCondition(words, at, where);
            }
            while (at < words.Length && Is(words[at], AndWord));
        }
        return new(new RowSelector(table?.TableId, all, subset, ids, where), words[at..]);
    }

    // The longest run of words from `at` that names one of the items, and each item it names.
    static (IReadOnlyList<T> Items, int Length)? Longest<T>(string[] words, int at, IReadOnlyList<T> items, Func<T, IReadOnlySet<string>> names)
    {
        var named = items.Select(i => (Item: i, Names: names(i))).ToList();
        var longest = named.SelectMany(n => n.Names).Select(n => n.Count(c => c == ' ') + 1).DefaultIfEmpty(0).Max();
        for (var length = Math.Min(longest, words.Length - at); length > 0; length--)
        {
            var said = TableWords.Of(string.Join(' ', words, at, length));
            var found = named.Where(n => n.Names.Contains(said)).Select(n => n.Item).ToList();
            if (found.Count > 0) return (found, length);
        }
        return null;
    }

    static bool StartsWithAFieldOfOneRow(string[] words, int at, IReadOnlyList<TableDescriptor> tables) =>
        Longest(words, at, [.. tables.Where(t => t.SingleRow).SelectMany(t => t.Fields)], TableWords.Names) is not null;

    // Values separated by commas: a word that ends with a comma is followed by another.
    static int List(string[] words, int at, List<string> into)
    {
        var more = true;
        while (more && at < words.Length)
        {
            var word = words[at++];
            into.AddRange(word.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            more = word.EndsWith(',') || (at < words.Length && words[at].StartsWith(','));
        }
        return at;
    }

    // One condition from `at`: the field's words up to an operator, the operator, and a value, or for `in` a list.
    static int ReadCondition(string[] words, int at, List<Condition> into)
    {
        var start = at;
        List<string> field = [];
        while (at < words.Length)
        {
            var word = words[at++];
            if (Is(word, InWord) && field.Count > 0)
            {
                var from = at;
                at = List(words, at, []);
                if (at == from) throw Unread(words, start, at, "names no value after in");
                // The list as it was written: whether a comma parts two values or groups one number is its field's to say.
                into.Add(new(string.Join(' ', field), InWord, Said(words, from, at - from)));
                return at;
            }
            var op = OperatorPattern().Match(word);
            if (!op.Success)
            {
                field.Add(word);
                continue;
            }
            if (op.Index > 0) field.Add(word[..op.Index]);
            var value = word[(op.Index + op.Length)..];
            // Two signs are no operator (`==`, `=>`): refused, never read as a value that starts with a sign.
            if (OperatorPattern().Match(value) is { Success: true, Index: 0 } second)
                throw new TableRefused("UnknownOperator", $"{op.Value}{second.Value} {Conditions.NoOperator}", op.Value + second.Value);
            if (value.Length == 0 && at < words.Length && !Is(words[at], AndWord)) value = words[at++];
            if (field.Count == 0) throw Unread(words, start, at, "names no field");
            if (value.Length == 0) throw Unread(words, start, at, $"names no value after {op.Value}");
            into.Add(new(string.Join(' ', field), op.Value, value));
            return at;
        }
        throw Unread(words, start, at, field.Count == 0 ? "names no condition" : "has no operator");
    }

    static TableRefused Unread(string[] words, int start, int end, string why)
    {
        var said = Said(words, start, end - start);
        return new("UnreadCondition", $"where {said}".TrimEnd() + $" {why}: {HowACondition}", said.Length == 0 ? WhereWord : said);
    }

    static string Said(string[] words, int at, int length) => string.Join(' ', words, at, length);

    /// <summary>A line's words with one space between them, as a log names a command.</summary>
    public static string Words(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    static bool Is(string word, string keyword) => word.Equals(keyword, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex("!=|<=|>=|=|<|>|~")]
    private static partial Regex OperatorPattern();
}

/// <summary>Resolves a selector to rows (ADR-0016 §2): once for a command, in a time linear in the rows.</summary>
public static class Selectors
{
    const int Shown = 5;

    public static ResolvedRows Resolve(RowSelector selector, IReadOnlyList<BoundTable> tables, WorkspaceState state)
    {
        // The rows of each table are read once, and only when the selector asks for them.
        var read = new Dictionary<string, IReadOnlyList<ITableRow>>();
        IReadOnlyList<ITableRow> RowsOf(BoundTable t) => read.TryGetValue(t.Descriptor.TableId, out var rows) ? rows : read[t.Descriptor.TableId] = t.Table.Rows(state);

        var indexed = new Dictionary<string, RowIndex>();
        RowIndex IndexOf(BoundTable t) => indexed.TryGetValue(t.Descriptor.TableId, out var index) ? index : indexed[t.Descriptor.TableId] = new(t, RowsOf(t));

        var table = selector.Table is { } name ? TableRegister.Find(tables, name)
            : selector.Ids.Count > 0 ? TableOf(selector.Ids, tables, IndexOf)
            : selector.SaysNoRows ? throw NoRows(null, tables)
            : tables.FirstOrDefault(t => t.Descriptor.Main) ?? throw NoRows(null, tables);
        var all = RowsOf(table);

        IReadOnlyList<ITableRow> rows;
        IReadOnlyList<TableSubset>? whole = null;
        if (selector.All && selector.Subset is not null) throw new TableRefused("RowsSaidTwice", "Rows are said as all or as a subset, not both.");
        if (selector.Ids.Count > 0)
        {
            if (selector.All || selector.Subset is not null)
                throw new TableRefused("RowsSaidTwice", "Rows are said by their ids, or as all or a subset, not both.");
            rows = ById(selector.Ids, table, IndexOf(table), all);
        }
        else if (selector.Subset is { } said)
        {
            var subset = table.Descriptor.Subsets.FirstOrDefault(s => TableWords.Names(s).Contains(TableWords.Of(said)))
                ?? throw new TableRefused("UnknownSubset", table.Descriptor.Subsets.Count == 0 ? $"{table.Word} has no subsets."
                    : $"{table.Word} has no subset {said.Trim()}; its subsets are {TableWords.List(table.Descriptor.Subsets.Select(s => s.Name))}.",
                    said.Trim(), [.. table.Descriptor.Subsets.Select(s => s.Name)]);
            rows = [.. all.Where(r => r.Subset == subset.Name)];
            whole = [subset];
        }
        else if (selector.All || selector.Where.Count > 0)
        {
            rows = all;
            if (selector.All) whole = table.Descriptor.Subsets;
        }
        else if (table.Descriptor.SingleRow)
        {
            // A table of one row may have none for now, as a choice not made yet: refused in its own words, never an empty edit.
            rows = all.Count switch
            {
                1 => all,
                0 => throw new TableRefused("NoRow", table.Table.WhyEmpty(state) ?? $"{table.Word} has no row in this workspace."),
                _ => throw new InvalidOperationException($"{table.Descriptor.TableId} is a table of one row and has {all.Count}."),
            };
        }
        else
        {
            throw NoRows(table, tables);
        }

        if (selector.Where.Count == 0) return new(table, rows, whole, []);
        var conditions = Conditions.Compile(selector.Where, table, rows, state);
        return new(table, [.. rows.Where(r => conditions.All(c => c.Holds(r)))], null, [.. conditions.Select(c => c.Text)]);
    }

    // The one table that has the rows named (note 7): by an id as written first, then by the end of one.
    static BoundTable TableOf(IReadOnlyList<string> ids, IReadOnlyList<BoundTable> tables, Func<BoundTable, RowIndex> indexOf)
    {
        (string Id, BoundTable Table)? first = null;
        foreach (var id in ids)
        {
            var having = tables.Where(t => indexOf(t).Row(id) is not null).ToList();
            if (having.Count == 0) having = [.. tables.Where(t => indexOf(t).HasEnd(id))];
            if (having.Count == 0)
                throw new TableRefused("UnknownRow", $"No row {id} in {TableWords.Or(tables.Select(t => t.Word))}.", id);
            if (having.Count > 1)
                throw new TableRefused("AmbiguousRow", $"{id} is a row of {string.Join(" and of ", having.Select(t => t.Word))}; say which, as in {having[0].Word} {id}.",
                    id, [.. having.Select(t => t.Word)]);
            first ??= (id, having[0]);
            if (having[0] != first.Value.Table)
                throw new TableRefused("RowsOfTwoTables", $"{first.Value.Id} is a row of {first.Value.Table.Word} and {id} of {having[0].Word}; a command takes the rows of one table.", id);
        }
        return first is { } found ? found.Table : throw new InvalidOperationException("Rows are located by at least one id.");
    }

    static IReadOnlyList<ITableRow> ById(IReadOnlyList<string> ids, BoundTable table, RowIndex index, IReadOnlyList<ITableRow> all)
    {
        var named = new List<ITableRow>(ids.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in ids)
        {
            var row = index.Row(id);
            if (row is null)
            {
                var ending = index.Ending(id);
                row = ending.Count switch
                {
                    1 => ending[0],
                    0 => throw new TableRefused("UnknownRow", all.Count <= Shown ? $"No row {id} in {table.Word}: its rows are {TableWords.List(all.Select(r => r.Key))}."
                        : $"No row {id} in {table.Word}.", id),
                    _ => throw new TableRefused("AmbiguousRow",
                        $"{id} could be {TableWords.Or(ending.Take(Shown).Select(r => r.Key))}{(ending.Count > Shown ? $" or {ending.Count - Shown} more" : "")} of {table.Word}; write more of the id.",
                        id, [.. ending.Take(Shown).Select(r => r.Key)]),
                };
            }
            if (!seen.Add(row.Key)) throw new TableRefused("RowTwice", $"{row.Key} is named twice; name each row once.", id);
            named.Add(row);
        }
        return named;
    }

    static TableRefused NoRows(BoundTable? table, IReadOnlyList<BoundTable> tables)
    {
        var of = table ?? tables.FirstOrDefault(t => t.Descriptor.Main);
        var ways = TableWords.List(new[] { "their ids", SelectorReader.AllWord }.Concat(of?.Descriptor.Subsets.Select(s => s.Name) ?? [])) + ", or where and a condition";
        return table is not null ? new("NoRows", $"Say which {table.Word}: {ways}.")
            : new("NoRows", $"Say which rows: {ways}; or name a table ({TableWords.List(tables.Select(t => t.Word))}).");
    }

    /// <summary>
    /// A table's rows by their ids as written, and by the end of an id (note 7). The ends are found in the ids reversed
    /// and sorted, built only when an id is not one as written, so a command that names thousands of rows by the last
    /// characters of their ids reads the table once and not once for each.
    /// </summary>
    sealed class RowIndex
    {
        readonly IReadOnlyList<ITableRow> rows;
        readonly Dictionary<string, ITableRow> byKey = new(StringComparer.OrdinalIgnoreCase);
        (string Reversed, int At)[]? ends;

        public RowIndex(BoundTable table, IReadOnlyList<ITableRow> rows)
        {
            this.rows = rows;
            foreach (var row in rows)
                if (!byKey.TryAdd(row.Key, row)) throw new InvalidOperationException($"{table.Descriptor.TableId} has two rows {row.Key}.");
        }

        public ITableRow? Row(string id) => byKey.GetValueOrDefault(id);

        public bool HasEnd(string id) => Run(id).Count > 0;

        /// <summary>The rows whose ids end with the text, case ignored, in the table's order.</summary>
        public IReadOnlyList<ITableRow> Ending(string id) => [.. Run(id).Select(e => e.At).Order().Select(at => rows[at])];

        // The ids that end with a text are those that, reversed, start with it reversed: one run of the sorted ends.
        ArraySegment<(string Reversed, int At)> Run(string id)
        {
            var sorted = ends ??= [.. rows.Select((r, at) => (Reversed(r.Key), at)).OrderBy(e => e.Item1, StringComparer.Ordinal)];
            var start = Reversed(id);
            var from = Before(e => string.CompareOrdinal(e, start) >= 0, 0);
            return new(sorted, from, Before(e => !e.StartsWith(start, StringComparison.Ordinal), from) - from);

            // The place of the first end from `low` on that holds, where those that hold come last.
            int Before(Func<string, bool> holds, int low)
            {
                var high = sorted.Length;
                while (low < high)
                {
                    var middle = low + (high - low) / 2;
                    if (holds(sorted[middle].Reversed)) high = middle;
                    else low = middle + 1;
                }
                return low;
            }
        }

        static string Reversed(string text)
        {
            var characters = text.ToUpperInvariant().ToCharArray();
            Array.Reverse(characters);
            return new(characters);
        }
    }
}

/// <summary>The conditions of <c>where</c>, read once against a table and then asked of each row (ADR-0016 §2, note 8).</summary>
static partial class Conditions
{
    internal const string NoOperator = "is no operator: a condition takes =, !=, <, <=, >, >=, ~ or in.";

    /// <param name="Compares">Whether it sets a value beside the rows'; false where it only asks whether a row has one.</param>
    public sealed record Compiled(string Text, Func<ITableRow, bool> Holds, bool Compares = true);

    enum Kind { Number, Date, Flag, Text }

    public static IReadOnlyList<Compiled> Compile(IReadOnlyList<Condition> conditions, BoundTable table, IReadOnlyList<ITableRow> rows, WorkspaceState state)
    {
        var fields = conditions.Select(c => (Condition: c, Field: TableRegister.Field(table, c.Field))).ToList();
        var compiled = fields.Select(f => One(f.Condition, f.Field, table, state)).ToList();
        foreach (var ((condition, field), one) in fields.Zip(compiled))
        {
            // An amount is not compared across units (ADR-0016 §2): its unit is pinned, or the rows asked hold one. Whether a
            // row has a value at all compares none.
            if (!one.Compares || field.UnitField is not { } unit || fields.Any(f => f.Field.Name == unit && f.Condition.Operator == "=")) continue;
            var units = rows.Select(r => r.Value(unit)).Where(v => v is not null).Select(v => v!.GetValue<string>()).Distinct().ToList();
            if (units.Count <= 1) continue;
            var unitWord = TableWords.Word(table.Descriptor.Fields.FirstOrDefault(f => f.Name == unit)
                ?? throw new InvalidOperationException($"{table.Descriptor.TableId} has no field {unit}, the unit of {field.Name}."));
            throw new TableRefused("MixedUnits",
                $"{TableWords.Word(field)} is in {TableWords.And(units)} here, and an amount is not compared across them; say which, as in where {unitWord}={units[0]} and {TableWords.Word(field)}{condition.Operator}{condition.Value}.",
                condition.Field, units);
        }
        return compiled;
    }

    static Compiled One(Condition condition, TableField field, BoundTable table, WorkspaceState state)
    {
        var word = TableWords.Word(field);
        var kind = field.Form switch
        {
            FieldForm.Amount or FieldForm.SignedAmount or FieldForm.Percent or FieldForm.Rate or FieldForm.Spread or FieldForm.Number or FieldForm.Days => Kind.Number,
            FieldForm.Date => Kind.Date,
            FieldForm.Flag => Kind.Flag,
            FieldForm.Period or FieldForm.PeriodOrDate or FieldForm.Choice or FieldForm.Id => Kind.Text,
            _ => throw new InvalidOperationException($"No comparing of {field.Form}."),
        };
        var what = kind switch { Kind.Number => "a number", Kind.Date => "a date", Kind.Flag => "a flag", _ => "text" };
        switch (condition.Operator)
        {
            case "<" or "<=" or ">" or ">=" when kind is Kind.Text or Kind.Flag:
                throw new TableRefused("UnorderedField",
                    $"{word} is {what}, which takes {(kind == Kind.Text ? "=, !=, ~ and in" : "=, != and in")}; {condition.Operator} is for numbers and dates.", condition.Operator);
            case "~" when kind != Kind.Text:
                throw new TableRefused("NotText", $"{word} is {what}, and ~ is for text.", condition.Operator);
            case "~":
                return new($"{word} ~ {condition.Value}", row => row.Value(field.Name) is { } held && Written(held).Contains(condition.Value, StringComparison.OrdinalIgnoreCase));
        }

        // A value is read by the field's form; `empty` and `none` stand for no value; on a date a period counts from the table's date.
        var spec = new FieldSpec(kind == Kind.Date ? FieldForm.PeriodOrDate : field.Form, Optional: true, field.Choices);
        var several = condition.Operator == SelectorReader.InWord;
        // A comma before three digits groups a number as it is typed and parts two values after `in`: which was meant is
        // not guessed.
        if (several && kind == Kind.Number && condition.Value.Split(' ').FirstOrDefault(w => GroupedDigits().IsMatch(w)) is { } grouped)
            throw new TableRefused("AmbiguousValues",
                $"{grouped} could be one value or two after in: write two as {grouped.Replace(",", ", ", StringComparison.Ordinal)} and one without its separator, {grouped.Replace(",", "", StringComparison.Ordinal)}.",
                grouped);
        var texts = several ? condition.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : [condition.Value.Trim()];
        var values = texts.Select(text =>
        {
            var reading = FieldForms.Read(spec, text);
            if (reading.Problem != FieldForms.Problem.None) throw new TableRefused("InvalidValue", $"{word} is {reading.What}; {text} is not.", text);
            return kind == Kind.Date && reading.Value is { } typed ? Dated(typed.GetValue<string>(), word, table, state) : reading.Value;
        }).ToList();
        var written = TableWords.List(values.Select(v => FieldForms.Write(field.Spec with { Optional = true }, v)));
        var text = $"{word} {condition.Operator} {written}";
        var ordered = condition.Operator is "<" or "<=" or ">" or ">=";
        // What is empty is in no order: asked for one, the rows that match would be none, and no one meant that.
        if (ordered && values[0] is null)
            throw new TableRefused("UnorderedValue", $"{text} asks for an order, and what is empty has none; write {word}=empty or {word}!=empty.", condition.Value.Trim());

        bool Same(JsonNode? held, JsonNode? asked) => (held, asked) switch
        {
            (null, null) => true,
            (null, _) or (_, null) => false,
            _ => kind switch
            {
                Kind.Number => FieldForms.Held(held) == FieldForms.Held(asked),
                Kind.Flag => held.GetValue<bool>() == asked.GetValue<bool>(),
                Kind.Date => string.Equals(Written(held), Written(asked), StringComparison.Ordinal),
                _ => string.Equals(Written(held), Written(asked), StringComparison.OrdinalIgnoreCase),
            },
        };
        // Less than nothing and more than nothing are both false: a row with no value is in no order.
        int? Order(JsonNode? held) => held is null || values[0] is not { } asked ? null
            : kind == Kind.Number ? FieldForms.Held(held).CompareTo(FieldForms.Held(asked)) : string.CompareOrdinal(Written(held), Written(asked));

        var compares = values.Any(v => v is not null);
        return condition.Operator switch
        {
            "=" => new(text, row => Same(row.Value(field.Name), values[0]), compares),
            "!=" => new(text, row => !Same(row.Value(field.Name), values[0]), compares),
            SelectorReader.InWord => new(text, row => row.Value(field.Name) is var held && values.Any(v => Same(held, v)), compares),
            "<" => new(text, row => Order(row.Value(field.Name)) < 0),
            "<=" => new(text, row => Order(row.Value(field.Name)) <= 0),
            ">" => new(text, row => Order(row.Value(field.Name)) > 0),
            ">=" => new(text, row => Order(row.Value(field.Name)) >= 0),
            _ => throw new TableRefused("UnknownOperator", $"{condition.Operator} {NoOperator}", condition.Operator),
        };
    }

    // A date as written, or the date a period means: counted from the table's date in calendar days, weeks, months and
    // years, with no business-day rule (note 8).
    static JsonNode Dated(string typed, string word, BoundTable table, WorkspaceState state)
    {
        if (DateOnly.TryParseExact(typed, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) return typed;
        var from = table.Table.AsOf(state)
            ?? throw new TableRefused("NoDate", $"{table.Word} has no date to count {typed} from; write {word} as a date, yyyy-mm-dd.", typed);
        var count = int.Parse(typed[..^1], CultureInfo.InvariantCulture);
        var date = typed[^1] switch
        {
            'd' => from.AddDays(count),
            'w' => from.AddDays(7 * count),
            'm' => from.AddMonths(count),
            'y' => from.AddYears(count),
            _ => throw new InvalidOperationException($"No period {typed}."),
        };
        return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    static string Written(JsonNode value) => value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : value.ToJsonString();

    [GeneratedRegex("[0-9],[0-9]{3}(?![0-9])")]
    private static partial Regex GroupedDigits();
}
