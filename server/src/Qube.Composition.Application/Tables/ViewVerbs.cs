using Qube.Composition.Application.Workspaces;
using Qube.Composition.Contracts;

namespace Qube.Composition.Application.Tables;

// ADR-0016 §3, §9 and its note 21 of 2026-11-11: the verbs of the screen whose words name rows. The server reads
// their lines, since the reading is its own (§5) and a row the page has not loaded is only its to find; the page does
// what the reading asks. None is a capability, and none writes a workspace.

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

        var row = Selectors.Named(id, new(table, screen.Shown)) switch
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
        var start = before.Length == 0 ? last.Word.ToLowerInvariant() : $"{before} {last.Word.ToLowerInvariant()}";
        var from = done.Count > 1 ? done[1].Start : last.Start;
        return new([.. screen.Table.Descriptor.Fields.Where(f => TableWords.Names(f).Any(n => n.StartsWith(start, StringComparison.Ordinal))).Select(f => new LineCompletion(TableWords.Word(f), from))]);
    }

    // The grid behind the line: its view, its table, the rows it shows and all the table's, and the grid in the table's words.
    sealed record Seen(LineView View, BoundTable Table, IReadOnlyList<ITableRow> Shown, IReadOnlyList<ITableRow> All, string Named);

    Seen Screen(LineContext line)
    {
        var view = line.View ?? throw new TableRefused("NoView", $"{Word} looks for a row in the grid on screen, and no grid of a table is behind this line: go to one first.");
        var table = Views.TableOf(view, register.Of(line.State));
        var all = table.Table.Rows(line.State);
        Selectors.RowIndex? index = null;
        return new(view, table, Views.Shown(view, table, all, key => (index ??= new(table, all)).Row(key)), all, Views.Named(view, table));
    }

    // An id that names no row the grid shows: one that names a row of the table which the grid does not show says where
    // that row is, and any other is no row.
    static TableRefused Missing(string id, Seen screen)
    {
        if (screen.Shown.Count == screen.All.Count || Selectors.Named(id, new(screen.Table, screen.All)) is not [var elsewhere]) return Selectors.UnknownRow(id, screen.Shown, screen.Named);
        var table = screen.Table.Word;
        // A row of the grid's own subset is one its page's filter hides; any other is a row of another grid.
        return new("NotOnScreen", screen.View.Subset is null || elsewhere.Subset == screen.View.Subset
            ? $"{elsewhere.Key} is a row of {table} that the filter of the grid on screen leaves out."
            : $"{elsewhere.Key} is no row of the grid on screen, {screen.Named}: it is one of {(elsewhere.Subset is null ? table : $"{table} ({elsewhere.Subset})")}.", id);
    }
}
