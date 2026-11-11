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

    /// <summary>How many rows the selection names, counted without reading one.</summary>
    public int Selected => Keys?.Count ?? Ranges?.Sum(r => r.Count) ?? 0;
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

/// <summary>Reads a view's rows (note 17). A view is a page's word about a moment; whatever no longer holds is refused by name.</summary>
static class Views
{
    public static BoundTable TableOf(LineView view, IReadOnlyList<BoundTable> tables) => TableRegister.Find(tables, view.Table);

    /// <summary>The view's subset as the table names it, or none.</summary>
    public static TableSubset? SubsetOf(LineView view, BoundTable table) => view.Subset is not { } said ? null
        : table.Descriptor.Subsets.FirstOrDefault(s => s.Name == said)
            ?? throw new TableRefused("UnknownSubset", $"{table.Word} has no subset {said}.", said, [.. table.Descriptor.Subsets.Select(s => s.Name)]);

    /// <summary>The table's words for the view: <c>readings (manual)</c>.</summary>
    public static string Named(LineView view, BoundTable table) => SubsetOf(view, table) is { } subset ? $"{table.Word} ({subset.Name})" : table.Word;

    /// <summary>The rows the grid shows: the table's, or its subset's. A view has no conditions until <c>filter</c> gives it some.</summary>
    public static IReadOnlyList<ITableRow> Shown(LineView view, BoundTable table, IReadOnlyList<ITableRow> all) =>
        SubsetOf(view, table) is { } subset ? [.. all.Where(r => r.Subset == subset.Name)] : all;

    /// <summary>The rows selected, in the order the view names them; a view with none is refused.</summary>
    public static IReadOnlyList<ITableRow> Selected(LineView view, BoundTable table, WorkspaceState state, Func<string, ITableRow?> byKey)
    {
        if (view.Keys is not null && view.Ranges is not null)
            throw new TableRefused("InvalidView", "A view names its selection by keys or by positions, not both.");
        if (view.Selected == 0) throw new TableRefused("NothingSelected", $"No row of {Named(view, table)} is selected.");
        if (view.Keys is { } keys)
        {
            return [.. keys.Distinct(StringComparer.OrdinalIgnoreCase).Select(key => byKey(key)
                ?? throw new TableRefused("ViewMoved", $"{key} was selected and is no longer a row of {table.Word}: select the rows again.", SelectorReader.SelectedWord))];
        }
        return At(view, table, state, view.Ranges!);
    }

    /// <summary>The row at a position of the view's order, as <see cref="Selected"/> reads a range.</summary>
    public static ITableRow At(LineView view, BoundTable table, WorkspaceState state, int position) => At(view, table, state, [new(position, 1)])[0];

    static IReadOnlyList<ITableRow> At(LineView view, BoundTable table, WorkspaceState state, IReadOnlyList<ViewRange> ranges)
    {
        var ordered = table.Table.InOrder(state, SubsetOf(view, table)?.Name, view.Sorts)
            ?? throw new TableRefused("NoOrder", $"{table.Word} has no order a position can be read in: its rows are named by their ids.");
        var moved = new TableRefused("ViewMoved", $"The {table.Word} are no longer in the order they were selected in: select them again.", SelectorReader.SelectedWord);
        if (ordered.Token != view.Order) throw moved;
        var rows = new List<ITableRow>();
        var seen = new HashSet<int>();
        foreach (var range in ranges)
        {
            if (range.Start < 0 || range.Count < 0 || range.Start > ordered.Rows.Count - range.Count) throw moved;
            for (var at = range.Start; at < range.Start + range.Count; at++)
                if (seen.Add(at)) rows.Add(ordered.Rows[at]);
        }
        return rows;
    }
}
