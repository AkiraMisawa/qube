using System.Globalization;
using Qube.Composition.Application.Workspaces;
using Qube.Composition.Contracts;

namespace Qube.Composition.Application.Tables;

// ADR-0016 §3, §9 and its notes 21 and 39 to 41 of 2026-11-11: the verbs of the screen whose words name rows or
// fields. The server reads their lines, since the reading is its own (§5) and a row the page has not loaded is only
// its to find; the page does what the reading asks. None is a capability, and none writes a workspace.

/// <summary>
/// The grid behind a line (note 39 of step 4): the view the line was given, its table, and the grid in the table's
/// words (<c>readings (manual)</c>). A verb of the screen acts on this grid and on no other.
/// </summary>
sealed record GridBehind(LineView View, BoundTable Table, string Named, IReadOnlyList<BoundTable> Tables)
{
    /// <param name="does">What the verb does to the grid, for a line with none behind it: <c>sort sorts the grid on screen</c>.</param>
    public static GridBehind Of(LineContext line, TableRegister register, string does)
    {
        var view = line.View ?? throw new TableRefused("NoView", $"{does}, and no grid of a table is behind this line: go to one first.");
        var tables = register.Of(line.State);
        var table = Views.TableOf(view, tables);
        return new(view, table, Views.Named(view, table), tables);
    }

    /// <summary>
    /// How many of the line's first words name the grid itself, by its table and then its subset, where the line says
    /// it (§3 spells these verbs with a table): none where it leaves the grid out. Words that name another table, or
    /// another subset of this one, are refused: such a grid is gone to first.
    /// </summary>
    public int Said(string[] words)
    {
        var at = 0;
        if (SelectorReader.Longest(words, at, [.. Tables.Select(t => t.Descriptor)], TableWords.Names) is (var named, var taken))
        {
            if (named.All(t => t.TableId != Table.Descriptor.TableId)) throw Other(TableWords.Word(named[0]), "table: go to its grid first", string.Join(' ', words, at, taken));
            at += taken;
        }
        if (SelectorReader.Longest(words, at, Table.Descriptor.Subsets, TableWords.Names) is (var subsets, var length))
        {
            if (subsets[0].Name != View.Subset) throw Other(subsets[0].Name, "grid: go to it first", string.Join(' ', words, at, length));
            at += length;
        }
        return at;
    }

    TableRefused Other(string other, string what, string word) => new("OtherGrid", $"The grid behind the line is of {Named}, and {other} is another {what}.", word);

    /// <summary>
    /// The grid behind a line as far as it is typed, for what could follow the verb's own word: none until a space
    /// follows that word, and none where the grid can no longer be read, which the line's reading says and no offer does.
    /// </summary>
    public static GridBehind? Typing(LineContext line, TableRegister register, string does)
    {
        if (line.View is null || line.Text.Length == 0) return null;
        try
        {
            return Of(line, register, does);
        }
        catch (TableRefused)
        {
            return null;
        }
    }

    /// <summary>The word that takes a verb's work off the grid: <c>sort off</c>, <c>filter off</c>.</summary>
    public const string Off = "off";

    /// <summary>
    /// Whether the word at <paramref name="at"/> takes the verb's work off the grid: <c>off</c>, with no word after it.
    /// Else it is the word the verb's own words follow (<c>by</c>, <c>where</c>); any other, and none, is refused with
    /// <paramref name="code"/> and what to say instead.
    /// </summary>
    public static bool TakesOff(string verb, string then, string[] words, int at, string code, string say)
    {
        var word = at < words.Length ? words[at] : null;
        if (word is not null && SelectorReader.Is(word, Off))
            return at + 1 < words.Length ? throw new TableRefused("UnreadWords", $"{verb} {Off} takes no words after it.", words[at + 1]) : true;
        return word is not null && SelectorReader.Is(word, then) ? false : throw new TableRefused(code, say, word);
    }
}

/// <summary>The fields a line could be naming, as far as it is typed (note 15 of step 1): whole words before, and the start of a word after them.</summary>
static class FieldsTyped
{
    public static IEnumerable<TableField> Of(BoundTable table, string before, string typed)
    {
        var start = before.Length == 0 ? typed.ToLowerInvariant() : $"{before} {typed.ToLowerInvariant()}";
        return table.Descriptor.Fields.Where(f => TableWords.Names(f).Any(n => n.StartsWith(start, StringComparison.Ordinal)));
    }
}

/// <summary>
/// <c>find &lt;row&gt; [&lt;field&gt;]</c> (note 21): the place of a row, or of one of its cells, in the grid behind the
/// line. The row is looked for among the rows that grid shows, by its id as written and then by the end of an id; the
/// field is one of the table's, by its words, and where it is left out the Focus keeps the column it was on.
/// </summary>
sealed class FindVerb(TableRegister register) : IViewVerb
{
    const string Word = "find";

    // Rows are offered a few at a time; a grid may show ten thousand.
    const int RowsListed = 20;

    public ViewVerbDescriptor Descriptor { get; } = new("find", "Put the keyboard on a row of the grid on screen, or on one of its cells",
        new([Word], [new("row", CommandArgumentKind.Rows, Required: true), new("field", CommandArgumentKind.Field, Required: false)]));

    public ViewRead Read(LineContext line)
    {
        var screen = Screen(line);
        var (view, table) = (screen.View, screen.Table);
        var words = line.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) throw new TableRefused("NoRows", $"Say which row of {screen.Named} to find: its id, or the end of it.");
        var id = words[0];
        if (id.Contains(',') || (words.Length > 1 && words[1].StartsWith(',')))
            throw new TableRefused("OneRow", $"{Word} puts the keyboard on one row: name one.", id);
        var typed = string.Join(' ', words[1..]);
        var field = typed.Length == 0 ? null : TableRegister.Field(table, typed);

        var row = Selectors.RowsNamed(id, new(table, screen.Shown)) switch
        {
            [var one] => one,
            [] => throw Missing(id, screen),
            var several => throw Selectors.AmbiguousRow(id, several, screen.Named),
        };
        var cell = field is null ? row.Key : $"{row.Key} · {TableWords.Word(field)}";
        var place = new LinePlace(table.Descriptor.TableId, view.Subset, row.Key, Views.PositionOf(view, table, line.State, row), field?.Name ?? view.Focus?.Field,
            $"The keyboard is on {cell} in {screen.Named}.");
        IReadOnlyList<LineArgument> arguments = field is null ? [new("row", id)] : [new("row", id), new("field", typed)];
        return new(arguments, new(table.Descriptor.TableId, $"{table.Word}, 1 row", 1, [row.Key]), $"The keyboard goes to {cell}.", place);
    }

    public LineCompletions Complete(LineContext line)
    {
        // No space after the verb's own word: it is still being typed, and nothing follows yet.
        if (line.View is null || line.Text.Length == 0) return LineCompletions.None;
        Seen screen;
        try
        {
            screen = Screen(line);
        }
        catch (TableRefused)
        {
            // The grid behind the line can no longer be read: the refusal says so, and nothing is offered.
            return LineCompletions.None;
        }
        var words = LineWords.Of(line.Text);
        var typing = !char.IsWhiteSpace(line.Text[^1]);
        var last = typing ? words[^1] : (Word: "", Start: line.Text.Length);
        IReadOnlyList<(string Word, int Start)> done = typing ? [.. words.Take(words.Count - 1)] : words;
        if (done.Count == 0)
        {
            // The rows the grid shows, as far as typed: an id starts or ends with it, as it is read.
            var keys = screen.Shown.Select(r => r.Key)
                .Where(k => k.StartsWith(last.Word, StringComparison.OrdinalIgnoreCase) || k.EndsWith(last.Word, StringComparison.OrdinalIgnoreCase)).ToList();
            return new([.. keys.Take(RowsListed).Select(k => new LineCompletion(k, last.Start))], Math.Max(0, keys.Count - RowsListed));
        }
        // After the row, a field: whole words before, and the start of a word after them. Words that already name one await nothing.
        var before = TableWords.Of(string.Join(' ', done.Skip(1).Select(w => w.Word)));
        if (last.Word.Length == 0 && before.Length > 0 && Assignments.Fields(screen.Table, before).Count == 1) return LineCompletions.None;
        var from = done.Count > 1 ? done[1].Start : last.Start;
        return new([.. FieldsTyped.Of(screen.Table, before, last.Word).Select(f => new LineCompletion(TableWords.Word(f), from))]);
    }

    // The grid behind the line: its view, its table, the rows it shows and all the table's, and the grid in the table's words.
    sealed record Seen(LineView View, BoundTable Table, IReadOnlyList<ITableRow> Shown, IReadOnlyList<ITableRow> All, string Named);

    Seen Screen(LineContext line)
    {
        var (view, table, named, _) = GridBehind.Of(line, register, $"{Word} looks for a row in the grid on screen");
        var all = table.Table.Rows(line.State);
        Selectors.RowIndex? index = null;
        return new(view, table, Views.Shown(view, table, all, key => (index ??= new(table, all)).Row(key), line.State), all, named);
    }

    // An id that names no row the grid shows: one that names a row of the table which the grid does not show says where
    // that row is, and any other is no row.
    static TableRefused Missing(string id, Seen screen)
    {
        if (screen.Shown.Count == screen.All.Count || Selectors.RowsNamed(id, new(screen.Table, screen.All)) is not [var elsewhere]) return Selectors.UnknownRow(id, screen.Shown, screen.Named);
        var table = screen.Table.Word;
        // A row of the grid's own subset is one its page's filter hides; any other is a row of another grid.
        return new("NotOnScreen", screen.View.Subset is null || elsewhere.Subset == screen.View.Subset
            ? $"{elsewhere.Key} is a row of {table} that the filter of the grid on screen leaves out."
            : $"{elsewhere.Key} is no row of the grid on screen, {screen.Named}: it is one of {(elsewhere.Subset is null ? table : $"{table} ({elsewhere.Subset})")}.", id);
    }
}

/// <summary>
/// <c>sort [&lt;table&gt;] by &lt;field&gt; [desc] [, &lt;field&gt; [desc]]</c> and <c>sort off</c> (note 40 of step 4): the
/// sort the page gives the grid behind the line. A field is one of the table's, by its words. Where a reader pages the
/// table it is asked whether it sorts by each field, and one it does not sort by is refused; where the page holds
/// every row the grid sorts them itself.
/// </summary>
sealed class SortVerb(TableRegister register) : IViewVerb
{
    const string Word = "sort";
    const string By = "by";
    const string Off = GridBehind.Off;
    const string Down = "desc";

    public ViewVerbDescriptor Descriptor { get; } = new("sort", "Sort the grid on screen by fields of its table, or take its sort off",
        new([Word], [new("sort", CommandArgumentKind.Text, Required: true)]));

    public ViewRead Read(LineContext line)
    {
        var grid = Grid(line);
        var (view, table) = (grid.View, grid.Table);
        var words = line.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var at = grid.Said(words);
        var off = GridBehind.TakesOff(Word, By, words, at, "NoSort", $"Say what to sort {grid.Named} by: {Word} {By} a field, or {Word} {Off}.");
        IReadOnlyList<LineArgument> arguments = [new("sort", string.Join(' ', words[at..]))];
        if (off) return new(arguments, null, $"Takes the sort off {grid.Named}.", new LineSort(table.Descriptor.TableId, view.Subset, [], $"Took the sort off {grid.Named}."));

        var sorts = new List<(TableField Field, bool Descending)>();
        foreach (var part in string.Join(' ', words[(at + 1)..]).Split(','))
        {
            var said = part.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (said.Length == 0) throw new TableRefused("NoField", $"Say which field to sort {grid.Named} by.");
            // The words name a field whole, or a field and then which way it runs.
            var way = said.Length > 1 && Assignments.Fields(table, string.Join(' ', said)).Count == 0 ? Way(said[^1]) : null;
            var name = string.Join(' ', way is null ? said : said[..^1]);
            var field = TableRegister.Field(table, name);
            if (sorts.Any(s => s.Field == field)) throw new TableRefused("FieldTwice", $"{TableWords.Word(field)} is named twice; name each field once.", name);
            var sort = new ViewSort(field.Name, way == true);
            // Where a reader pages the table it is asked first: a field it does not sort by is refused, never ignored.
            try
            {
                table.Table.InOrder(line.State, view.Subset, [sort]);
            }
            catch (WorkspaceRefused unsorted)
            {
                throw new TableRefused("NotSortable", unsorted.Message, name);
            }
            sorts.Add((field, sort.Descending));
        }
        var by = string.Join(", then ", sorts.Select(s => s.Descending ? $"{TableWords.Word(s.Field)}, descending" : TableWords.Word(s.Field)));
        return new(arguments, null, $"Sorts {grid.Named} by {by}.",
            new LineSort(table.Descriptor.TableId, view.Subset, [.. sorts.Select(s => new ViewSort(s.Field.Name, s.Descending))], $"Sorted {grid.Named} by {by}."));
    }

    // Which way a word after a field turns it: true for descending, false for ascending, none where it is no such word.
    static bool? Way(string word) => word.ToLowerInvariant() switch
    {
        "desc" or "descending" => true,
        "asc" or "ascending" => false,
        _ => null,
    };

    public LineCompletions Complete(LineContext line)
    {
        if (GridBehind.Typing(line, register, Does) is not { } grid) return LineCompletions.None;
        var (done, typed, at) = LineWords.Typing(line.Text);
        if (done.Count == 0) return new([.. new[] { By, Off }.Where(w => w.StartsWith(typed, StringComparison.OrdinalIgnoreCase)).Select(w => new LineCompletion(w, at))]);
        // After `by`, the field being typed is what follows the last comma: its whole words, and the start of one.
        if (LineWords.Of(line.Text).Where(w => SelectorReader.Is(w.Word, By)).Select(w => (int?)w.Start).FirstOrDefault() is not { } by) return LineCompletions.None;
        var from = Math.Max(by + By.Length, line.Text.LastIndexOf(',') + 1);
        var (before, word, start) = LineWords.Typing(line.Text[from..]);
        var whole = TableWords.Of(string.Join(' ', before));
        var first = before.Count == 0 ? from + start : from + LineWords.Of(line.Text[from..])[0].Start;
        var items = FieldsTyped.Of(grid.Table, whole, word).Select(f => new LineCompletion(TableWords.Word(f), first)).ToList();
        // Words that name one field await which way it runs.
        if (whole.Length > 0 && Assignments.Fields(grid.Table, whole).Count == 1 && Down.StartsWith(word, StringComparison.OrdinalIgnoreCase)) items.Add(new(Down, from + start));
        return new(items);
    }

    const string Does = $"{Word} sorts the grid on screen";

    GridBehind Grid(LineContext line) => GridBehind.Of(line, register, Does);
}

/// <summary>
/// <c>filter [&lt;table&gt;] where &lt;conditions&gt;</c> and <c>filter off</c> (note 41 of step 4): the conditions the
/// page gives the grid behind the line. They are the selector's (§2), read by its reader and refused as <c>where</c>
/// is, and the reading says how many rows of the grid they leave. The view then carries them as typed.
/// </summary>
sealed class FilterVerb(TableRegister register) : IViewVerb
{
    const string Word = "filter";
    const string Off = GridBehind.Off;

    public ViewVerbDescriptor Descriptor { get; } = new("filter", "Show the rows of the grid on screen that meet conditions, or take its filter off",
        new([Word], [new("where", CommandArgumentKind.Text, Required: true)]));

    public ViewRead Read(LineContext line)
    {
        var grid = Grid(line);
        var (view, table) = (grid.View, grid.Table);
        var words = line.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var at = grid.Said(words);
        if (GridBehind.TakesOff(Word, SelectorReader.WhereWord, words, at, "NoFilter", $"Say which rows of {grid.Named} to show: {Word} {SelectorReader.WhereWord} and conditions, or {Word} {Off}."))
            return new([new("where", Off)], null, $"Takes the filter off {grid.Named}.", new LineFilter(table.Descriptor.TableId, view.Subset, null, $"Took the filter off {grid.Named}."));

        var typed = string.Join(' ', words[(at + 1)..]);
        var all = Views.Unfiltered(view, table, table.Table.Rows(line.State));
        var conditions = GridFilters.Read(typed, table, all, line.State);
        IReadOnlyList<ITableRow> left = [.. all.Where(r => conditions.All(c => c.Holds(r)))];
        var of = $"{left.Count.ToString(CultureInfo.InvariantCulture)} of {TableWords.Counted(all.Count, "row")}";
        return new([new("where", typed)], new(table.Descriptor.TableId, $"{grid.Named}, {of}", left.Count, [.. left.Take(Listed).Select(r => r.Key)]), $"Shows {of} of {grid.Named}.",
            new LineFilter(table.Descriptor.TableId, view.Subset, typed, $"Showing {of} of {grid.Named} where {string.Join(" and ", conditions.Select(c => c.Text))}."));
    }

    // The first rows a reading names, as a command's reading names them.
    const int Listed = 10;

    public LineCompletions Complete(LineContext line)
    {
        if (GridBehind.Typing(line, register, Does) is not { } grid) return LineCompletions.None;
        var (done, typed, at) = LineWords.Typing(line.Text);
        if (done.Count == 0)
            return new([.. new[] { SelectorReader.WhereWord, Off }.Where(w => w.StartsWith(typed, StringComparison.OrdinalIgnoreCase)).Select(w => new LineCompletion(w, at))]);
        // After `where`, what the selector's reader awaits in the grid's table: a field, an operator, a value, `and`.
        if (LineWords.Of(line.Text).Where(w => SelectorReader.Is(w.Word, SelectorReader.WhereWord)).Select(w => (int?)w.Start).FirstOrDefault() is not { } where
            || where + SelectorReader.WhereWord.Length == line.Text.Length)
            return LineCompletions.None;
        var conditions = TableCompletions.Of(line.Text[where..], [grid.Table], line.State, afterRows: null, new LineView(grid.Table.Descriptor.TableId));
        return conditions with { Items = [.. conditions.Items.Select(c => c with { Start = c.Start + where })] };
    }

    const string Does = $"{Word} narrows the grid on screen";

    GridBehind Grid(LineContext line) => GridBehind.Of(line, register, Does);
}

/// <summary>
/// <c>select &lt;rows&gt;</c> (note 43 of step 4): the rows the page places the Selection of the grid behind the line
/// on. The rows are the selector's (§2), read with the view. They are rows that grid shows, and one run in its order:
/// a page places one range. Where the view names its order the server says whether they are one run, and answers its
/// first position; where it names none, the page holds every row and orders them itself, and is answered their keys.
/// </summary>
sealed class SelectVerb(TableRegister register) : IViewVerb
{
    const string Word = "select";
    const string Does = $"{Word} selects rows of the grid on screen";

    // The first rows a reading names, as a command's reading names them.
    const int Listed = 10;

    public ViewVerbDescriptor Descriptor { get; } = new("select", "Select rows of the grid on screen", new([Word], [new("rows", CommandArgumentKind.Rows, Required: true)]));

    public ViewRead Read(LineContext line)
    {
        var grid = GridBehind.Of(line, register, Does);
        var (view, table, state) = (grid.View, grid.Table, line.State);
        var said = SelectorReader.Words(line.Text);
        var (selector, _, _) = TableService.RowsSaid(grid.Tables, null, said, view);
        if (selector.Table is { } id && TableRegister.Find(grid.Tables, id) is var other && other.Descriptor.TableId != table.Descriptor.TableId)
            throw new TableRefused("OtherGrid", $"The grid behind the line is of {grid.Named}, and the rows are said to be of {other.Word}: go to its grid first.");
        // The rows are read in the grid's table: an id is looked for there, and a line that says no row is asked for its rows.
        var resolved = Selectors.Resolve(selector with { Table = table.Descriptor.TableId }, grid.Tables, state, view);
        var rows = resolved.Rows;
        if (rows.Count == 0)
            throw new TableRefused("NoRowMatches", resolved.Where.Count > 0 ? $"No row of {table.Word} matches {string.Join(" and ", resolved.Where)}: there is nothing to select."
                : $"{said} names no row of {table.Word}: there is nothing to select.");

        var all = table.Table.Rows(state);
        Selectors.RowIndex? index = null;
        var shown = Views.Shown(view, table, all, key => (index ??= new(table, all)).Row(key), state).Select(r => r.Key).ToHashSet(StringComparer.Ordinal);
        IReadOnlyList<string> outside = [.. rows.Where(r => !shown.Contains(r.Key)).Select(r => r.Key)];
        if (outside.Count > 0)
            throw new TableRefused("NotOnScreen",
                $"{TableService.Few(outside)} {(outside.Count == 1 ? "is a row" : "are rows")} of {table.Word} that the grid on screen, {grid.Named}, does not show: {Word} places the Selection of that grid, and {SelectorReader.ShownWord} says its rows alone.");

        var positions = Views.PositionsOf(view, table, state, rows)?.Order().ToList();
        if (positions is not null && 1 + positions.Zip(positions.Skip(1)).Count(next => next.Second != next.First + 1) is > 1 and var runs)
            throw new TableRefused("SeveralRuns",
                $"The {rows.Count} rows are in {runs} runs in {grid.Named}, and the line selects rows that are next to one another: filter or sort the grid so that they are, then {Word} {SelectorReader.ShownWord}.");

        var what = rows.Count == 1 ? $"{rows[0].Key} in {grid.Named}" : $"{TableWords.Counted(rows.Count, "row")} of {grid.Named}";
        var asks = new LineSelect(table.Descriptor.TableId, view.Subset, positions is null ? [.. rows.Select(r => r.Key)] : null, positions?[0], rows.Count, view.Focus?.Field, $"Selected {what}.");
        return new([new("rows", said)], new(table.Descriptor.TableId, $"{grid.Named}, {TableWords.Counted(rows.Count, "row")}", rows.Count, [.. rows.Take(Listed).Select(r => r.Key)]), $"Selects {what}.", asks);
    }

    // The ways to say rows (note 15) of the grid's table, with its view: nothing where no grid is behind the line.
    public LineCompletions Complete(LineContext line) =>
        GridBehind.Typing(line, register, Does) is not { } grid ? LineCompletions.None : TableCompletions.Of(line.Text, [grid.Table], line.State, afterRows: null, grid.View);
}

/// <summary>
/// A grid's conditions as typed (note 41 of step 4), read by the selector's one reader against the grid's table: the
/// same fields, operators and values as <c>where</c>, refused as it refuses them.
/// </summary>
static class GridFilters
{
    /// <param name="rows">The rows the conditions are asked of: an amount is compared across them only where they hold one unit (§2).</param>
    public static IReadOnlyList<Conditions.Compiled> Read(string conditions, BoundTable table, IReadOnlyList<ITableRow> rows, WorkspaceState state)
    {
        var reading = SelectorReader.Read($"{SelectorReader.WhereWord} {conditions}", [table.Descriptor], within: table.Descriptor);
        if (reading.Rest.Count > 0)
            throw new TableRefused("UnreadWords", $"{reading.Rest[0]} belongs to no condition: conditions are joined by {SelectorReader.AndWord}.", reading.Rest[0]);
        return Conditions.Compile(reading.Selector.Where, table, rows, state);
    }
}
