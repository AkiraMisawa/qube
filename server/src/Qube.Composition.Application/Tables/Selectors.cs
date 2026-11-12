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
/// Which rows (ADR-0016 §2): the table, by id or by words, or none where the rows say it; then every row, a subset,
/// rows by id, or the rows a screen names (<c>selected</c>, <c>shown</c>), each narrowed by conditions joined by
/// <c>and</c>. The rows of a screen are read with the view the line was given (note 17 of step 2).
/// </summary>
public sealed record RowSelector(
    string? Table = null,
    bool All = false,
    string? Subset = null,
    IReadOnlyList<string>? Ids = null,
    IReadOnlyList<Condition>? Where = null,
    ScreenRows? Screen = null)
{
    public IReadOnlyList<string> Ids { get; init; } = Ids ?? [];
    public IReadOnlyList<Condition> Where { get; init; } = Where ?? [];

    /// <summary>True where no row is said: the rows of a table of one row, or nothing a command can act on.</summary>
    public bool SaysNoRows => !All && Subset is null && Ids.Count == 0 && Where.Count == 0 && Screen is null;
}

/// <summary>A selector read from the start of a line, and the words it left for the verb.</summary>
public sealed record SelectorReading(RowSelector Selector, IReadOnlyList<string> Rest);

/// <summary>A condition a line has started and not finished: its field's words so far, and its operator once it has one.</summary>
sealed record OpenCondition(IReadOnlyList<string> Field, string? Operator);

/// <summary>
/// A line's rows as far as they could be read: the selector, how many words it took, and, where the last condition is
/// not finished, that condition and the refusal a command would get. What could follow is said from this (note 15).
/// </summary>
/// <param name="IdsFrom">The first word of the rows' ids, where rows are said by id, and <paramref name="IdsTo"/> the word after the last.</param>
/// <param name="Unsaid">
/// The table of rows the line says with no table, by <c>all</c>, a subset or <c>where</c>, where a screen is behind
/// it (ADR-0016 §9, note 23 of step 2): the screen's table, or the table a subset's word was read in. None where the
/// line names its table, says its rows by id or as the screen's, says none, or has no screen behind it.
/// </param>
sealed record SelectorScan(RowSelector Selector, int Taken, OpenCondition? Open, TableRefused? Short, int IdsFrom = 0, int IdsTo = 0, TableDescriptor? Unsaid = null);

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
    public const string SelectedWord = "selected";
    public const string ShownWord = "shown";
    public const string WhereWord = "where";
    public const string AndWord = "and";
    public const string InWord = "in";
    /// <summary>The word after which <c>set</c> reads a template (note 50): it ends the rows, as <c>where</c> does, and is no row's id said by itself.</summary>
    public const string AsWord = "as";

    const string HowACondition = "a condition is a field, an operator (=, !=, <, <=, >, >=, ~, in) and a value, and conditions are joined by and.";

    /// <summary>
    /// Reads the rows a line starts with: a table's words where it names one, then <c>all</c>, <c>selected</c>,
    /// <c>shown</c>, a subset, or ids separated by commas, then <c>where</c> and its conditions. What follows is the
    /// verb's. A line that starts with a field of a table of one row says no rows, and so does one that starts with a
    /// field of the table on the screen behind the line (<paramref name="onScreen"/>, note 18 of step 2): its rows are
    /// then the screen's to say. Where the table is said beside the rows (<paramref name="within"/>), the rows are
    /// read as that table's: its subsets, and no row where it has one.
    /// </summary>
    /// <param name="fieldsFollow">
    /// The verb reads a list of fields after the rows, separated by commas (<c>copy</c>, <c>export</c>; note 44 of step
    /// 4). Words after a table's own that name a field of it then say no rows, as words that name a field of the
    /// screen's table do, and the first field of the list ends at its comma.
    /// </param>
    public static SelectorReading Read(string text, IReadOnlyList<TableDescriptor> tables, TableDescriptor? within = null, TableDescriptor? onScreen = null, bool fieldsFollow = false)
    {
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var scan = Scan(words, tables, within, onScreen, fieldsFollow);
        return scan.Short is { } unread ? throw unread : new(scan.Selector, words[scan.Taken..]);
    }

    /// <summary>The one reading of a line's rows, which stops at a condition that is not finished and says so, where <see cref="Read"/> refuses.</summary>
    internal static SelectorScan Scan(string[] words, IReadOnlyList<TableDescriptor> tables, TableDescriptor? within = null, TableDescriptor? onScreen = null, bool fieldsFollow = false)
    {
        var at = 0;

        TableDescriptor? table = null;
        if (Longest(words, at, tables, TableWords.Names) is (var named, var taken))
        {
            if (named.Count > 1)
                throw new TableRefused("AmbiguousTable", $"{Said(words, at, taken)} could be {TableWords.Either(named.Select(t => t.TableId))}; name one by its id.",
                    Said(words, at, taken), [.. named.Select(t => t.TableId)]);
            // Words that name a table and a field of the screen's table: which was meant is not guessed (note 18 of step
            // 2). More words that name a field are that field and no table (a field `site code`, where `site` is a
            // table's word; note 44 of step 4), as the longest words are a command's: the line goes on as one that
            // names no table. Where a list of fields follows, the first ends at a comma.
            var fieldWords = fieldsFollow ? BeforeComma(words, at) : words[at..];
            if (within is null && onScreen is not null && Longest(fieldWords, 0, onScreen.Fields, TableWords.Names) is (var fields, var length))
            {
                if (length <= taken)
                {
                    var word = Said(words, at, taken);
                    var other = named[0].Words.FirstOrDefault(w => !TableWords.Names(fields[0]).Contains(TableWords.Of(w))) ?? named[0].TableId;
                    throw new TableRefused("AmbiguousWords",
                        $"{word} could be the table {TableWords.Word(named[0])} or {TableWords.Word(fields[0])} of the {TableWords.Word(onScreen)} on screen; write {other} for the table, or {SelectedWord} {word} for the rows on screen.",
                        word, [other, $"{SelectedWord} {word}"]);
                }
            }
            else (table, at) = (named[0], at + taken);
        }

        var all = false;
        ScreenRows? screen = null;
        string? subset = null;
        TableDescriptor? subsetOf = null;
        List<string> ids = [];
        var said = table ?? within;
        if (at < words.Length && Is(words[at], AllWord)) (all, at) = (true, at + 1);
        else if (at < words.Length && ScreenWord(words[at]) is { } rows) (screen, at) = (rows, at + 1);
        else
        {
            // A table's subsets share no word (TableRegister.Of), so the words name one of a table's. With no table
            // said, the table is the screen's where it has such a subset, and the main one otherwise (note 23 of step 2).
            foreach (var scope in SubsetScopes(said, onScreen, tables))
            {
                if (Longest(words, at, scope.Subsets, TableWords.Names) is not (var subsets, var length)) continue;
                (subset, subsetOf, at) = (subsets[0].Name, scope, at + length);
                break;
            }
        }
        var idsFrom = at;
        // Where a list of fields follows the rows, its first field ends at a comma, with or without a space.
        var first = fieldsFollow ? BeforeComma(words, at) : null;
        bool FieldFirst(IReadOnlyList<TableField> fields) => first is null ? StartsWithAField(words, at, fields) : StartsWithAField(first, 0, fields);
        if (!all && screen is null && subset is null && at < words.Length && !Is(words[at], WhereWord) && !Is(words[at], AsWord) && said is not { SingleRow: true }
            && !(said is null && FieldFirst([.. tables.Where(t => t.SingleRow).SelectMany(t => t.Fields), .. onScreen?.Fields ?? []]))
            && !(fieldsFollow && table is not null && FieldFirst(table.Fields)))
            at = List(words, at, ids);
        var idsTo = at;

        List<Condition> where = [];
        OpenCondition? open = null;
        TableRefused? unread = null;
        var narrowed = at < words.Length && Is(words[at], WhereWord);
        if (narrowed)
        {
            do
            {
                at++;
                (at, open, unread) = ReadCondition(words, at, where);
            }
            while (unread is null && at < words.Length && Is(words[at], AndWord));
        }
        // This is the one place that says which table such rows are of: the verbs and what could follow read it here.
        var unsaid = said is not null || onScreen is null || screen is not null || ids.Count > 0 ? null
            : subset is not null ? subsetOf
            : all || narrowed ? onScreen : null;
        return new(new RowSelector(table?.TableId, all, subset, ids, where, screen), at, open, unread, idsFrom, idsTo, unsaid);
    }

    /// <summary>The words from <paramref name="from"/> up to the first comma: the first of a list of fields, written with or without a space.</summary>
    internal static string[] BeforeComma(IReadOnlyList<string> words, int from) =>
        string.Join(' ', words.Skip(from)).Split(',')[0].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    // The tables a subset's word is looked for in, in turn: the one said; or the screen's, then the main one.
    static IEnumerable<TableDescriptor> SubsetScopes(TableDescriptor? said, TableDescriptor? onScreen, IReadOnlyList<TableDescriptor> tables) =>
        said is not null ? [said] : new[] { onScreen, tables.FirstOrDefault(t => t.Main) }.OfType<TableDescriptor>().Distinct();

    /// <summary>The rows of a screen a word names, or none: the two words are kept, so no subset or row is said by one.</summary>
    internal static ScreenRows? ScreenWord(string word) => Is(word, SelectedWord) ? ScreenRows.Selected : Is(word, ShownWord) ? ScreenRows.Shown : null;

    /// <summary>The word that names the rows of a screen.</summary>
    public static string Word(ScreenRows rows) => rows == ScreenRows.Selected ? SelectedWord : ShownWord;

    // The longest run of words from `at` that names one of the items, and each item it names.
    internal static (IReadOnlyList<T> Items, int Length)? Longest<T>(string[] words, int at, IReadOnlyList<T> items, Func<T, IReadOnlySet<string>> names)
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

    // Whether the words name a field first: the longest run of them that names one, or, where a value follows with its
    // sign, every word before the sign (`wind speed=4`).
    static bool StartsWithAField(string[] words, int at, IReadOnlyList<TableField> fields)
    {
        if (Longest(words, at, fields, TableWords.Names) is not null) return true;
        var rest = string.Join(' ', words, at, words.Length - at);
        return rest.IndexOf('=') is > 0 and var sign && fields.Any(f => TableWords.Names(f).Contains(TableWords.Of(rest[..sign])));
    }

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

    // One condition from `at`: the field's words up to an operator, the operator, and a value, or for `in` a list. One
    // that the words end before finishing is answered as far as it goes, with the refusal a command would get.
    static (int At, OpenCondition? Open, TableRefused? Unread) ReadCondition(string[] words, int at, List<Condition> into)
    {
        var start = at;
        List<string> field = [];
        (int, OpenCondition?, TableRefused?) Short(string? op, string why) => (at, new(field, op), Unread(words, start, at, why));
        while (at < words.Length)
        {
            var word = words[at++];
            if (Is(word, InWord) && field.Count > 0)
            {
                var from = at;
                at = List(words, at, []);
                if (at == from) return Short(InWord, "names no value after in");
                // The list as it was written: whether a comma parts two values or groups one number is its field's to say.
                into.Add(new(string.Join(' ', field), InWord, Said(words, from, at - from)));
                return (at, null, null);
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
            if (field.Count == 0) return Short(op.Value, "names no field");
            if (value.Length == 0) return Short(op.Value, $"names no value after {op.Value}");
            into.Add(new(string.Join(' ', field), op.Value, value));
            return (at, null, null);
        }
        return Short(null, field.Count == 0 ? "names no condition" : "has no operator");
    }

    static TableRefused Unread(string[] words, int start, int end, string why)
    {
        var said = Said(words, start, end - start);
        return new("UnreadCondition", $"where {said}".TrimEnd() + $" {why}: {HowACondition}", said.Length == 0 ? WhereWord : said);
    }

    static string Said(string[] words, int at, int length) => string.Join(' ', words, at, length);

    /// <summary>A line's words with one space between them, as a log names a command.</summary>
    public static string Words(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    internal static bool Is(string word, string keyword) => word.Equals(keyword, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex("!=|<=|>=|=|<|>|~")]
    internal static partial Regex OperatorPattern();
}

/// <summary>Resolves a selector to rows (ADR-0016 §2): once for a command, in a time linear in the rows.</summary>
public static class Selectors
{
    const int Shown = 5;

    /// <param name="view">What the screen behind the command showed, where there is one: <c>selected</c> and <c>shown</c> are read with it.</param>
    public static ResolvedRows Resolve(RowSelector selector, IReadOnlyList<BoundTable> tables, WorkspaceState state, LineView? view = null)
    {
        // The rows of each table are read once, and only when the selector asks for them.
        var read = new Dictionary<string, IReadOnlyList<ITableRow>>();
        IReadOnlyList<ITableRow> RowsOf(BoundTable t) => read.TryGetValue(t.Descriptor.TableId, out var rows) ? rows : read[t.Descriptor.TableId] = Views.InDraft(view, t, t.Table.Rows(state));

        var indexed = new Dictionary<string, RowIndex>();
        RowIndex IndexOf(BoundTable t) => indexed.TryGetValue(t.Descriptor.TableId, out var index) ? index : indexed[t.Descriptor.TableId] = new(t, RowsOf(t));

        // The rows of a screen, where the selector says them, and the word that says them.
        var screen = selector.Screen;
        var screenWord = screen is null ? null : SelectorReader.Word(screen.Value);
        var table = screen is not null ? ScreenTable(selector, screen.Value, tables, view)
            : selector.Table is { } name ? TableRegister.Find(tables, name)
            : selector.Ids.Count > 0 ? TableOf(selector.Ids, tables, IndexOf)
            : selector.SaysNoRows ? throw NoRows(null, tables)
            // A selector that names no table and no row is the main table's. Where a screen is behind a line its reader
            // names the table (SelectorScan.Unsaid), so no selector reaches here for the screen's table to be guessed.
            : tables.FirstOrDefault(t => t.Descriptor.Main) ?? throw NoRows(null, tables);
        var all = RowsOf(table);

        IReadOnlyList<ITableRow> rows;
        IReadOnlyList<TableSubset>? whole = null;
        if (selector.All && selector.Subset is not null) throw new TableRefused("RowsSaidTwice", "Rows are said as all or as a subset, not both.");
        if (screen is not null)
        {
            if (selector.All || selector.Subset is not null || selector.Ids.Count > 0)
                throw new TableRefused("RowsSaidTwice", $"Rows are said as {screenWord}, or by their ids, as all or as a subset, not both.");
            // Rows of a screen are rows, never a whole set: no one edit stands for them.
            rows = screen == ScreenRows.Shown ? Views.Shown(view!, table, all, IndexOf(table).Row, state) : Views.Selected(view!, table, state, IndexOf(table).Row);
        }
        else if (selector.Ids.Count > 0)
        {
            if (selector.All || selector.Subset is not null)
                throw new TableRefused("RowsSaidTwice", "Rows are said by their ids, or as all or a subset, not both.");
            rows = ById(selector.Ids, table, IndexOf(table), all, Above(table, tables, IndexOf));
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

        if (selector.Where.Count == 0)
            return screen is null || rows.Count > 0 ? new(table, rows, whole, []) : throw new TableRefused("NothingShown", $"{Views.Named(view!, table)} shows no row.", SelectorReader.ShownWord);
        var conditions = Conditions.Compile(selector.Where, table, rows, state);
        IReadOnlyList<ITableRow> matching = [.. rows.Where(r => conditions.All(c => c.Holds(r)))];
        // The rows of a screen are run by their keys (note 17 of step 2), so none of them is nothing to run: refused.
        if (screen is not null && matching.Count == 0)
            throw new TableRefused("NoRowMatches", $"No {screenWord} row of {table.Word} matches {string.Join(" and ", conditions.Select(c => c.Text))}.", screenWord);
        return new(table, matching, null, [.. conditions.Select(c => c.Text)]);
    }

    /// <summary>
    /// The rows a line that says none acts on, where a screen is behind it (note 18 of step 2): the screen's selection,
    /// or the row the keyboard is on where nothing is selected. With neither, the line is refused as one that says
    /// <c>selected</c> is.
    /// </summary>
    public static ResolvedRows LeftOut(LineView view, IReadOnlyList<BoundTable> tables, WorkspaceState state) =>
        view.Selected == 0 && Focused(view, tables, state) is { } focused ? focused : Resolve(new RowSelector(Screen: ScreenRows.Selected), tables, state, view);

    /// <summary>The row the keyboard is on, as rows; none where the view has no focus.</summary>
    public static ResolvedRows? Focused(LineView view, IReadOnlyList<BoundTable> tables, WorkspaceState state)
    {
        var table = Views.TableOf(view, tables);
        // A key names its row as a selected row's key does, whatever its case.
        RowIndex? index = null;
        return Views.FocusedRow(view, table, state, key => (index ??= new(table, table.Table.Rows(state))).Row(key)) is { } row ? new(table, [row], null, []) : null;
    }

    // The table of the rows on screen (note 17 of step 2): the view's, and no other may be said beside it.
    static BoundTable ScreenTable(RowSelector selector, ScreenRows screen, IReadOnlyList<BoundTable> tables, LineView? view)
    {
        var word = SelectorReader.Word(screen);
        if (view is null)
            throw new TableRefused("NoView", $"{word} means the rows {word} on a screen, and no screen is behind this command: name the rows by their ids, all, a subset or where.", word);
        var table = Views.TableOf(view, tables);
        if (selector.Table is { } said && TableRegister.Find(tables, said) is var other && other.Descriptor.TableId != table.Descriptor.TableId)
            throw new TableRefused("TwoTables", $"The rows on screen are of {table.Word} and the table is said to be {other.Word}.", said);
        return table;
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

    // The rows ids name in a table. In a table under another (note 46), an id that names no row of it and one row of
    // the table above names every row under that row; `above` reads an id among the rows above, where there are any.
    static IReadOnlyList<ITableRow> ById(IReadOnlyList<string> ids, BoundTable table, RowIndex index, IReadOnlyList<ITableRow> all, Func<string, ITableRow?>? above)
    {
        var named = new List<ITableRow>(ids.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in ids)
        {
            IReadOnlyList<ITableRow> rows = RowsNamed(id, index) switch
            {
                [var one] => [one],
                [] when above?.Invoke(id) is { } over => all.Where(r => r.Under == over.Key).ToList() is { Count: > 0 } under ? under
                    : throw new TableRefused("NoRowMatches", $"{over.Key} has no {table.Word}.", id),
                [] => throw UnknownRow(id, all, table.Word),
                var several => throw AmbiguousRow(id, several, table.Word),
            };
            foreach (var row in rows)
            {
                if (!seen.Add(row.Key)) throw new TableRefused("RowTwice", $"{row.Key} is named twice; name each row once.", id);
                named.Add(row);
            }
        }
        return named;
    }

    // How an id is read among the rows of the table above a table (note 46), as that table's ids are read: the row it
    // names, none where it names none, and a refusal where its end names several. Null for a table under no other.
    static Func<string, ITableRow?>? Above(BoundTable table, IReadOnlyList<BoundTable> tables, Func<BoundTable, RowIndex> indexOf)
    {
        if (table.Descriptor.Under is not { } under) return null;
        var above = tables.Single(t => t.Descriptor.TableId == under);
        return id => RowsNamed(id, indexOf(above)) switch
        {
            [var one] => one,
            [] => null,
            var several => throw AmbiguousRow(id, several, above.Word),
        };
    }

    /// <summary>The rows an id names among those indexed (note 7): the one of that id as written, or else every row whose id ends with it.</summary>
    internal static IReadOnlyList<ITableRow> RowsNamed(string id, RowIndex index) => index.Row(id) is { } row ? [row] : index.Ending(id);

    /// <summary>An id that names no row of those looked in, which are said in <paramref name="within"/>'s words and listed where they are few.</summary>
    internal static TableRefused UnknownRow(string id, IReadOnlyList<ITableRow> among, string within) =>
        new("UnknownRow", among.Count is > 0 and <= Shown ? $"No row {id} in {within}: its rows are {TableWords.List(among.Select(r => r.Key))}." : $"No row {id} in {within}.", id);

    /// <summary>The end of an id that several rows have, refused with the first of them.</summary>
    internal static TableRefused AmbiguousRow(string id, IReadOnlyList<ITableRow> ending, string within) =>
        new("AmbiguousRow",
            $"{id} could be {TableWords.Or(ending.Take(Shown).Select(r => r.Key))}{(ending.Count > Shown ? $" or {ending.Count - Shown} more" : "")} of {within}; write more of the id.",
            id, [.. ending.Take(Shown).Select(r => r.Key)]);

    internal static TableRefused NoRows(BoundTable? table, IReadOnlyList<BoundTable> tables)
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
    internal sealed class RowIndex
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

    // Money is a number with what it is in (note 58): compared with its unit said, and never across units.
    enum Kind { Number, Date, Flag, Text, Money }

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

    static Kind KindOf(TableField field) => FieldForms.IsNumber(field.Form) ? Kind.Number : field.Form switch
    {
        FieldForm.Date => Kind.Date,
        FieldForm.Flag => Kind.Flag,
        FieldForm.Money => Kind.Money,
        FieldForm.Period or FieldForm.PeriodOrDate or FieldForm.Choice or FieldForm.Id => Kind.Text,
        _ => throw new InvalidOperationException($"No comparing of {field.Form}."),
    };

    /// <summary>The operators a condition on the field takes, as <see cref="One"/> accepts them: an order for numbers and dates, <c>~</c> for text.</summary>
    public static IReadOnlyList<string> OperatorsOf(TableField field) => KindOf(field) switch
    {
        Kind.Number or Kind.Date or Kind.Money => ["=", "!=", "<", "<=", ">", ">=", SelectorReader.InWord],
        Kind.Flag => ["=", "!=", SelectorReader.InWord],
        _ => ["=", "!=", "~", SelectorReader.InWord],
    };

    static Compiled One(Condition condition, TableField field, BoundTable table, WorkspaceState state)
    {
        var word = TableWords.Word(field);
        var kind = KindOf(field);
        var what = kind switch { Kind.Number => "a number", Kind.Date => "a date", Kind.Flag => "a flag", Kind.Money => "a number with what it is in", _ => "text" };
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
            // A number alone would be compared with values in every unit, which is no comparison (ADR-0016 §2).
            if (kind == Kind.Money && reading.Value is { } money && money["unit"] is null)
            {
                var units = (field.Choices ?? throw new InvalidOperationException($"{field.Name} is in nothing a condition could say.")).Values.Distinct().ToList();
                throw new TableRefused("InvalidValue", $"{word} is compared with what it is in, {TableWords.Or(units)}; {text} says {(units.Count == 2 ? "neither" : "none")}.", text, units);
            }
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
                Kind.Money => FieldForms.Same(held, asked),
                Kind.Flag => held.GetValue<bool>() == asked.GetValue<bool>(),
                Kind.Date => string.Equals(Written(held), Written(asked), StringComparison.Ordinal),
                _ => string.Equals(Written(held), Written(asked), StringComparison.OrdinalIgnoreCase),
            },
        };
        // Less than nothing and more than nothing are both false: a row with no value is in no order.
        // And so is a number in another unit than the one asked: it is neither less nor more.
        int? Order(JsonNode? held) => held is null || values[0] is not { } asked ? null
            : kind == Kind.Number ? FieldForms.Held(held).CompareTo(FieldForms.Held(asked))
            : kind == Kind.Money ? FieldForms.Same(held["unit"], asked["unit"]) ? FieldForms.Held(Amount(held)).CompareTo(FieldForms.Held(Amount(asked))) : null
            : string.CompareOrdinal(Written(held), Written(asked));

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

    static JsonNode Amount(JsonNode money) => money["amount"] ?? throw new InvalidOperationException("A number with what it is in holds its amount.");

    [GeneratedRegex("[0-9],[0-9]{3}(?![0-9])")]
    private static partial Regex GroupedDigits();
}
