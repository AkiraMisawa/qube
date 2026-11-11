using Qube.Composition.Application.Workspaces;

namespace Qube.Composition.Application.Tables;

// ADR-0016 §2, §9 and its notes 16 and 17 of 2026-11-11: what a screen gives a line is a value, read once as the line
// opens and sent with each reading. The server keeps none of it, and no command, log or result holds one.

/// <summary>One column a grid is sorted by, as the grid's reader takes it.</summary>
public sealed record ViewSort(string Column, bool Descending = false);

/// <summary>Rows by their positions in the order a grid is drawn from: the first, and how many.</summary>
public sealed record ViewRange(int Start, int Count);

/// <summary>The cell the keyboard is on: its row by key, or by position where the server pages the grid, and its field.</summary>
public sealed record ViewCell(string Field, string? Key = null, int? Position = null);

/// <summary>
/// What a user was looking at when the line opened (note 16): the table of the grid the keyboard was last in and its
/// subset; then the selection as row keys where the page holds every row, or as ranges of positions with the sort and
/// the order token the grid's reader last answered where the server pages it; and the focused cell.
/// </summary>
public sealed record LineView(
    string Table,
    string? Subset = null,
    IReadOnlyList<ViewSort>? Sorts = null,
    string? Order = null,
    IReadOnlyList<string>? Keys = null,
    IReadOnlyList<ViewRange>? Ranges = null,
    ViewCell? Focus = null)
{
    public IReadOnlyList<ViewSort> Sorts { get; init; } = Sorts ?? [];

    /// <summary>How many rows the selection names, counted without reading one: a row two ranges hold is one row.</summary>
    public int Selected
    {
        get
        {
            if (Keys is not null) return Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count();
            var (count, end) = (0, 0);
            foreach (var range in (Ranges ?? []).OrderBy(r => r.Start))
            {
                var from = Math.Max(range.Start, end);
                end = Math.Max(end, range.Start + range.Count);
                count += Math.Max(0, end - from);
            }
            return count;
        }
    }
}

/// <summary>
/// A table's rows in the order a page's reader gives them, and that order's token: equal tokens mean equal orders, so a
/// position read against another token is refused, never guessed (note 17).
/// </summary>
public sealed record OrderedRows(IReadOnlyList<ITableRow> Rows, string Token);

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

    /// <summary>
    /// The view in one sentence (note 19): the table and its subset, how many rows are selected, and the cell the
    /// keyboard is on, by its row and its field's word: <c>readings (manual), 2 selected, R1 · note</c>. A column that
    /// is no field of the table names none. Positions read against an order that has moved are said as that.
    /// </summary>
    public static string Say(LineView view, IReadOnlyList<BoundTable> tables, WorkspaceState state)
    {
        var table = TableOf(view, tables);
        var named = Named(view, table);
        if (view.Keys is not null && view.Ranges is not null) throw Invalid("A view names its selection by keys or by positions, not both.");
        string? cell = null;
        try
        {
            if (view.Ranges is not null) Ordered(view, table, state);
            if (view.Focus is { } focus)
            {
                var key = Focused(view, table, state, focus).Key;
                cell = FocusedField(view, table) is { } field ? $"{key} · {TableWords.Word(field)}" : key;
            }
        }
        catch (TableRefused moved) when (moved.Code == Moved)
        {
            return $"{named}, no longer in the order it was shown in";
        }
        catch (TableRefused unordered) when (unordered.Code == Unordered)
        {
            // Positions in a table no reader pages are a page's fault, not a moment that passed.
            throw Invalid(unordered.Message);
        }
        var selected = view.Selected == 0 ? "nothing selected" : $"{view.Selected} selected";
        return cell is null ? $"{named}, {selected}" : $"{named}, {selected}, {cell}";
    }

    // The row the keyboard is on: by its key as the page names it, or at its position in the view's order. A key is
    // not looked up here: a command that acts on the row reads it, and refuses one that is gone.
    static (string Key, ITableRow? Row) Focused(LineView view, BoundTable table, WorkspaceState state, ViewCell focus) => focus switch
    {
        { Key: { } key, Position: null } => (key, null),
        { Key: null, Position: { } position } => At(view, table, state, [new(position, 1)])[0] is var row ? (row.Key, row) : default,
        _ => throw Invalid("A view names the focused row by its key or by its position, one of the two."),
    };

    /// <summary>The row the keyboard is on, read; none where the view has no focus, and a row that is gone is refused.</summary>
    public static ITableRow? FocusedRow(LineView view, BoundTable table, WorkspaceState state, Func<string, ITableRow?> byKey)
    {
        if (view.Focus is not { } focus) return null;
        var (key, row) = Focused(view, table, state, focus);
        return row ?? byKey(key) ?? throw new TableRefused(Moved, $"{key} was the row the keyboard was on and is no longer a row of {table.Word}: select the rows again.");
    }

    /// <summary>The field of the cell the keyboard is on; none where the view has no focus, or its column is no field of the table.</summary>
    public static TableField? FocusedField(LineView view, BoundTable table) =>
        view.Focus is { } focus ? table.Descriptor.Fields.FirstOrDefault(f => f.Name == focus.Field) : null;

    /// <summary>The table's words for the view: <c>readings (manual)</c>.</summary>
    public static string Named(LineView view, BoundTable table) => SubsetOf(view, table) is { } subset ? $"{table.Word} ({subset.Name})" : table.Word;

    /// <summary>The rows the grid shows: the table's, or its subset's. A view has no conditions until <c>filter</c> gives it some.</summary>
    public static IReadOnlyList<ITableRow> Shown(LineView view, BoundTable table, IReadOnlyList<ITableRow> all) =>
        SubsetOf(view, table) is { } subset ? [.. all.Where(r => r.Subset == subset.Name)] : all;

    /// <summary>The rows selected, in the order the view names them; a view with none is refused.</summary>
    public static IReadOnlyList<ITableRow> Selected(LineView view, BoundTable table, WorkspaceState state, Func<string, ITableRow?> byKey)
    {
        if (view.Keys is not null && view.Ranges is not null) throw Invalid("A view names its selection by keys or by positions, not both.");
        if (view.Selected == 0) throw new TableRefused("NothingSelected", $"No row of {Named(view, table)} is selected.");
        if (view.Keys is { } keys)
        {
            return [.. keys.Distinct(StringComparer.OrdinalIgnoreCase).Select(key => byKey(key)
                ?? throw new TableRefused(Moved, $"{key} was selected and is no longer a row of {table.Word}: select the rows again.", SelectorReader.SelectedWord))];
        }
        return At(view, table, state, view.Ranges!);
    }

    // The table's rows in the view's order, where that is still the order the grid was drawn from. A sort the table
    // refuses is one its grid never had: the page's fault.
    static IReadOnlyList<ITableRow> Ordered(LineView view, BoundTable table, WorkspaceState state)
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
        if (ordered is null) throw new TableRefused(Unordered, $"{table.Word} has no order a position can be read in: its rows are named by their ids.");
        return ordered.Token == view.Order ? ordered.Rows : throw NotInOrder(table);
    }

    static TableRefused NotInOrder(BoundTable table) =>
        new(Moved, $"The {table.Word} are no longer in the order they were selected in: select them again.", SelectorReader.SelectedWord);

    static IReadOnlyList<ITableRow> At(LineView view, BoundTable table, WorkspaceState state, IReadOnlyList<ViewRange> ranges)
    {
        var ordered = Ordered(view, table, state);
        var rows = new List<ITableRow>();
        var seen = new HashSet<int>();
        foreach (var range in ranges)
        {
            if (range.Start < 0 || range.Count < 0 || range.Start > ordered.Count - range.Count) throw NotInOrder(table);
            for (var at = range.Start; at < range.Start + range.Count; at++)
                if (seen.Add(at)) rows.Add(ordered[at]);
        }
        return rows;
    }
}
