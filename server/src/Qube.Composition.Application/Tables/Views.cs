using Qube.Composition.Application.Workspaces;

namespace Qube.Composition.Application.Tables;

// ADR-0016 §2, §9 and its notes 16 and 17 of 2026-11-11: what a screen gives a line is a value, read once as the line
// opens and sent with each reading. The server keeps none of it, and no command, log or result holds one.

/// <summary>One column a grid is sorted by, and which way.</summary>
public sealed record ViewSort(string Column, bool Descending = false);

/// <summary>Rows by their positions in the order a grid is drawn from: the first, and how many.</summary>
public sealed record ViewRange(int Start, int Count);

/// <summary>The cell the keyboard is on: its row by key, or by position where the server pages the grid, and its field.</summary>
public sealed record ViewCell(string Field, string? Key = null, int? Position = null);

/// <summary>
/// What a user was looking at when the line opened (note 16): the table of the grid the keyboard was last in and its
/// subset; then the selection as row keys where the page holds every row, or as ranges of positions with the sort and
/// the order token the grid's reader last answered where the server pages it; and the focused cell. A page that holds
/// every row and filters them names the rows it shows, by key: what <c>shown</c> then means (§2).
/// </summary>
public sealed record LineView(
    string Table,
    string? Subset = null,
    IReadOnlyList<ViewSort>? Sorts = null,
    string? Order = null,
    IReadOnlyList<string>? Keys = null,
    IReadOnlyList<ViewRange>? Ranges = null,
    ViewCell? Focus = null,
    IReadOnlyList<string>? Shown = null)
{
    public IReadOnlyList<ViewSort> Sorts { get; init; } = Sorts ?? [];

    /// <summary>How many rows the page's filter leaves, where it names the rows it shows; none where it filters none.</summary>
    public int? Filtered => Shown?.Distinct(StringComparer.OrdinalIgnoreCase).Count();

    /// <summary>How many rows the selection names, counted without reading one: a row two ranges hold is one row.</summary>
    public int Selected
    {
        get
        {
            if (Keys is not null) return Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count();
            // Counted wide: a range that ends past the last number an int holds is still rows, never none.
            var (count, end) = (0L, 0L);
            foreach (var range in (Ranges ?? []).OrderBy(r => r.Start))
            {
                var from = Math.Max(range.Start, end);
                end = Math.Max(end, (long)range.Start + range.Count);
                count += Math.Max(0, end - from);
            }
            return (int)Math.Min(count, int.MaxValue);
        }
    }
}

/// <summary>
/// A table's rows in the order a page's reader gives them, and that order's token: equal tokens mean equal orders, so a
/// position read against another token is refused, never guessed (note 17).
/// </summary>
public sealed record OrderedRows(IReadOnlyList<ITableRow> Rows, string Token);

/// <summary>
/// A view in one sentence (note 19), and whether its positions are of an order that has moved since: the sentence says
/// so in words, and a client draws it as what is stale.
/// </summary>
public sealed record ViewSaid(string Sentence, bool Moved = false);

/// <summary>The rows a screen names (§2): those selected in the grid, or those the grid shows.</summary>
public enum ScreenRows
{
    Selected,
    Shown,
}

/// <summary>
/// Reads a view's rows (note 17). A view is a page's word about a moment: what no longer holds is refused by name
/// (<see cref="TableRefused"/>), and a view that names what the workspace never had is a page's fault, refused whole
/// (<see cref="WorkspaceRefused"/>, <c>InvalidView</c>).
/// </summary>
static class Views
{
    const string Moved = "ViewMoved";
    const string Unordered = "NoOrder";

    public static BoundTable TableOf(LineView view, IReadOnlyList<BoundTable> tables) =>
        tables.FirstOrDefault(t => t.Descriptor.TableId == view.Table) ?? throw Invalid($"The view names a table, {view.Table}, that this workspace has not.");

    /// <summary>The view's subset as the table names it, or none.</summary>
    public static TableSubset? SubsetOf(LineView view, BoundTable table) => view.Subset is not { } said ? null
        : table.Descriptor.Subsets.FirstOrDefault(s => s.Name == said) ?? throw Invalid($"The view names a subset, {said}, that {table.Word} has not.");

    static WorkspaceRefused Invalid(string why) => new("InvalidView", why);

    // What a view must be to be read at all, checked by each reader of its rows: a selection or a focused row said two
    // ways, or by positions that are none, is the page's fault. A range of no row is not "nothing selected", which a
    // line that leaves its rows out would read as the focused row.
    static void Shaped(LineView view)
    {
        if (view.Keys is not null && view.Ranges is not null) throw Invalid("A view names its selection by keys or by positions, not both.");
        if (view.Ranges is { } ranges && ranges.Any(r => r.Start < 0 || r.Count < 1))
            throw Invalid("A view names rows by position as ranges of one row or more, from the first row on.");
        if (view.Shown is not null && (view.Ranges is not null || view.Order is not null || view.Sorts.Count > 0 || view.Focus is { Position: not null }))
            throw Invalid("A view names the rows it shows by key where its page holds them all, and no row by position.");
        if (view.Focus is not { } focus) return;
        if (focus.Key is null == focus.Position is null) throw Invalid("A view names the focused row by its key or by its position, one of the two.");
        if (focus.Position < 0) throw Invalid("A view names the focused row by a position from the first row on.");
    }

    /// <summary>
    /// The view in one sentence (note 19): the table and its subset, how many rows are selected, and the cell the
    /// keyboard is on, by its row and its field's word: <c>readings (manual), 2 selected, R1 · note</c>. A column that
    /// is no field of the table names none. A grid its page filters says how many rows that leaves (<c>stations,
    /// filtered to 2, 1 selected</c>). Positions read against an order that has moved are said as that. The view
    /// is checked whole here, where a line's reading starts: its sort is one the table's reader takes though no
    /// position is read in it.
    /// </summary>
    public static ViewSaid Say(LineView view, IReadOnlyList<BoundTable> tables, WorkspaceState state)
    {
        var table = TableOf(view, tables);
        var named = Named(view, table);
        Shaped(view);
        string? cell = null;
        try
        {
            ITableRow? at = null;
            if (view.Ranges is not null || view.Focus is { Position: not null })
            {
                var ordered = Ordered(view, table, state);
                Within(view.Ranges ?? [], ordered.Count, table);
                if (view.Focus is { Position: { } position })
                {
                    Within([new(position, 1)], ordered.Count, table);
                    at = ordered[position];
                }
            }
            else if (view.Sorts.Count > 0 || view.Order is not null)
            {
                Sorted(view, table, state);
            }
            // A key is not looked up here: a command that acts on the row reads it, and refuses one that is gone.
            if ((at?.Key ?? view.Focus?.Key) is { } key)
                cell = FocusedField(view, table) is { } field ? $"{key} · {TableWords.Word(field)}" : key;
        }
        catch (TableRefused moved) when (moved.Code == Moved)
        {
            return new($"{named}, no longer in the order it was shown in", Moved: true);
        }
        catch (TableRefused unordered) when (unordered.Code == Unordered)
        {
            // A sort or positions in a table no reader pages are a page's fault, not a moment that passed.
            throw Invalid(unordered.Message);
        }
        var selected = view.Selected == 0 ? "nothing selected" : $"{view.Selected} selected";
        var shown = view.Filtered is { } left ? $"{named}, filtered to {left}" : named;
        return new(cell is null ? $"{shown}, {selected}" : $"{shown}, {selected}, {cell}");
    }

    /// <summary>
    /// The row the keyboard is on, read: by its key as the page names it, or at its position in the view's order. None
    /// where the view has no focus, and a row that is gone is refused.
    /// </summary>
    public static ITableRow? FocusedRow(LineView view, BoundTable table, WorkspaceState state, Func<string, ITableRow?> byKey)
    {
        Shaped(view);
        return view.Focus switch
        {
            null => null,
            { Position: { } position } => At(view, table, state, [new(position, 1)])[0],
            { Key: { } key } => byKey(key) ?? throw new TableRefused(Moved, $"{key} was the row the keyboard was on and is no longer a row of {table.Word}: select the rows again."),
            _ => throw new InvalidOperationException("A focused row has a key or a position."),
        };
    }

    /// <summary>The field of the cell the keyboard is on; none where the view has no focus, or its column is no field of the table.</summary>
    public static TableField? FocusedField(LineView view, BoundTable table) =>
        view.Focus is { } focus ? table.Descriptor.Fields.FirstOrDefault(f => f.Name == focus.Field) : null;

    /// <summary>The table's words for the view: <c>readings (manual)</c>.</summary>
    public static string Named(LineView view, BoundTable table) => SubsetOf(view, table) is { } subset ? $"{table.Word} ({subset.Name})" : table.Word;

    /// <summary>
    /// The rows the grid shows: those its page names where it filters them, each read by its key, and otherwise the
    /// table's, or its subset's. A row named that is gone is refused.
    /// </summary>
    public static IReadOnlyList<ITableRow> Shown(LineView view, BoundTable table, IReadOnlyList<ITableRow> all, Func<string, ITableRow?> byKey)
    {
        Shaped(view);
        if (view.Shown is { } keys)
        {
            return [.. keys.Distinct(StringComparer.OrdinalIgnoreCase).Select(key => byKey(key)
                ?? throw new TableRefused(Moved, $"{key} was shown and is no longer a row of {table.Word}: open the line again.", SelectorReader.ShownWord))];
        }
        return Unfiltered(view, table, all);
    }

    /// <summary>The rows of a grid whose page filters none: the table's, or its subset's.</summary>
    public static IReadOnlyList<ITableRow> Unfiltered(LineView view, BoundTable table, IReadOnlyList<ITableRow> all) =>
        SubsetOf(view, table) is { } subset ? [.. all.Where(r => r.Subset == subset.Name)] : all;

    /// <summary>The rows selected, in the order the view names them; a view with none is refused.</summary>
    public static IReadOnlyList<ITableRow> Selected(LineView view, BoundTable table, WorkspaceState state, Func<string, ITableRow?> byKey)
    {
        Shaped(view);
        if (view.Selected == 0) throw new TableRefused("NothingSelected", $"No row of {Named(view, table)} is selected.");
        if (view.Keys is { } keys)
        {
            return [.. keys.Distinct(StringComparer.OrdinalIgnoreCase).Select(key => byKey(key)
                ?? throw new TableRefused(Moved, $"{key} was selected and is no longer a row of {table.Word}: select the rows again.", SelectorReader.SelectedWord))];
        }
        return At(view, table, state, view.Ranges!);
    }

    // The table's rows in the view's sort, with that order's token. A sort the table's reader refuses is one its grid
    // never had: the page's fault. A table no reader pages has no order for a sort or a position to be read in.
    static OrderedRows Sorted(LineView view, BoundTable table, WorkspaceState state)
    {
        OrderedRows? ordered;
        try
        {
            ordered = table.Table.InOrder(state, SubsetOf(view, table)?.Name, view.Sorts);
        }
        catch (WorkspaceRefused refused) when (refused.Code != "InvalidView")
        {
            throw Invalid(refused.Message);
        }
        return ordered ?? throw new TableRefused(Unordered, $"{table.Word} has no order a position can be read in: its rows are named by their ids.");
    }

    // The table's rows in the view's order, where that is still the order the grid was drawn from.
    static IReadOnlyList<ITableRow> Ordered(LineView view, BoundTable table, WorkspaceState state)
    {
        var sorted = Sorted(view, table, state);
        return sorted.Token == view.Order ? sorted.Rows : throw NotInOrder(table);
    }

    static TableRefused NotInOrder(BoundTable table) =>
        new(Moved, $"The {table.Word} are no longer in the order they were selected in: select them again.", SelectorReader.SelectedWord);

    // Positions are positions of an order: one the order has no row at is of another order, whatever its token.
    static void Within(IReadOnlyList<ViewRange> ranges, int count, BoundTable table)
    {
        if (ranges.Any(r => r.Start < 0 || r.Count < 0 || r.Start > count - r.Count)) throw NotInOrder(table);
    }

    static IReadOnlyList<ITableRow> At(LineView view, BoundTable table, WorkspaceState state, IReadOnlyList<ViewRange> ranges)
    {
        var ordered = Ordered(view, table, state);
        Within(ranges, ordered.Count, table);
        var rows = new List<ITableRow>();
        var seen = new HashSet<int>();
        foreach (var range in ranges)
        {
            for (var at = range.Start; at < range.Start + range.Count; at++)
                if (seen.Add(at)) rows.Add(ordered[at]);
        }
        return rows;
    }
}
