using System.Text.Json.Nodes;
using Qube.Composition.Application.Workspaces;

namespace Qube.Composition.Application.Tables;

// ADR-0016 §1 and its note 6 of 2026-11-11: a domain registers its tables as data, through a catalog port as products
// are (ADR-0008 §7), and the kernel's verbs address `table row field`. Nothing here knows a word of a domain.

/// <summary>A group of a table's rows the domain names (<c>existing</c>, <c>new</c>), and the words it answers to.</summary>
public sealed record TableSubset(string Name, string Title, IReadOnlyList<string> Words);

/// <param name="Name">The field's name in an edit's target.</param>
/// <param name="Words">What a person types for it, the first the one a sentence uses (<c>vol</c>, <c>volume</c>).</param>
/// <param name="Editable">Whether a command may set it. The domain may still refuse a row's (a cell read-only for now).</param>
/// <param name="Optional">Whether it may be empty, written <c>empty</c> or <c>none</c> (the field's, not its form's).</param>
/// <param name="Table">For an id, the table it points into (a reading's <c>station</c> names a row of the stations).</param>
/// <param name="UnitField">The field of the row that carries its unit, for an amount: a unit is never inferred from a title.</param>
/// <param name="Group">The group it is drawn under, where the table has groups.</param>
/// <param name="Unit">Its unit where the table as the workspace has it fixes one, and no field of the row carries it (note 47).</param>
/// <param name="Figure">Where it is a figure of the rows under its row (note 47): their table, and how a write of it is written.</param>
public sealed record TableField(
    string Name,
    string Title,
    IReadOnlyList<string> Words,
    FieldForm Form,
    bool Editable = false,
    bool Optional = false,
    IReadOnlyDictionary<string, string>? Choices = null,
    string? Table = null,
    string? UnitField = null,
    string? Group = null,
    string? Unit = null,
    TableFigure? Figure = null)
{
    /// <summary>How its values are written and read.</summary>
    public FieldSpec Spec => new(Form, Optional, Choices);

    /// <summary>How a value a command writes into it is written: a figure's own way where it has one (note 47), else as it is read.</summary>
    public FieldSpec Written => Figure?.Written ?? Spec;
}

/// <summary>
/// A field that is worked out from the rows under its row (ADR-0016 §1, note 47), as a total is from its parts.
/// What a write of it means is its table's (<see cref="IWorkspaceTable.Set"/>), and never the kernel's guess.
/// </summary>
/// <param name="Of">The table under its own whose rows it is a figure of.</param>
/// <param name="Written">How a write is written where that differs from how the figure is read, such as a number with what it is in.</param>
public sealed record TableFigure(string Of, FieldSpec? Written = null);

/// <summary>
/// A named set of field values a table registers (ADR-0016 §1, note 50): where a row starts, which holds nothing
/// afterwards. <c>set &lt;rows&gt; as &lt;template&gt; [terms]</c> writes it over rows.
/// </summary>
/// <param name="Words">What a person types for it after <c>as</c>, the first the one a sentence uses.</param>
/// <param name="Takes">The terms it takes, read as a product's are (ADR-0008 §3): by name, or by form where one may be typed without its name.</param>
/// <param name="Writes">The fields it writes, in the order they are written and checked; no other field of a row is.</param>
public sealed record TableTemplate(string Name, string Title, IReadOnlyList<string> Words, IReadOnlyList<Products.ProductField> Takes, IReadOnlyList<string> Writes);

/// <summary>
/// A table as a workspace has it (ADR-0016 §1): its id and version, which a saved definition and the AI name; the words
/// a person types for it; the field that names a row; its fields, resolved for the workspace, and its subsets.
/// </summary>
/// <param name="SingleRow">A table of one row is addressed without a row: <c>set &lt;field&gt; &lt;value&gt;</c>.</param>
/// <param name="Main">The application's main table, which <c>all</c>, a subset or <c>where</c> mean where no table is named (note 7).</param>
/// <param name="Actions">What a grid bound to the table offers beside the kernel's own (note 25 of step 2); none where it is left out.</param>
/// <param name="Under">The table its rows are under, where it belongs to a row (§1, note 46): the lines of an order are under the orders.</param>
/// <param name="Templates">The named sets of field values it registers (§1, note 50); none where it is left out.</param>
public sealed record TableDescriptor(
    string TableId,
    string Version,
    string App,
    string Title,
    IReadOnlyList<string> Words,
    string Key,
    IReadOnlyList<TableField> Fields,
    IReadOnlyList<TableSubset> Subsets,
    bool SingleRow = false,
    bool Main = false,
    IReadOnlyList<TableAction>? Actions = null,
    string? Under = null,
    IReadOnlyList<TableTemplate>? Templates = null);

/// <summary>
/// An act on a table's rows that a screen offers by its title (ADR-0016 §4, note 25 of step 2): the line that does it,
/// read with the screen's view when it is run (<c>Mark as checked</c>, <c>set selected checked on</c>).
/// </summary>
public sealed record TableAction(string Title, string Line);

/// <summary>A table as a screen is told of it (ADR-0016 §4, note 25 of step 2): its id, its title and the actions a grid bound to it offers.</summary>
public sealed record TableOnScreen(string TableId, string Title, IReadOnlyList<TableAction> Actions);

/// <summary>The rows a view's selection names (ADR-0016 note 37 of step 3): their table, and their keys in the order the view names them.</summary>
public sealed record ViewSelection(string Table, IReadOnlyList<string> Keys);

/// <summary>One row at a revision: its key, the subset it is in, and its values by field as they stand.</summary>
public interface ITableRow
{
    string Key { get; }

    /// <summary>The subset's name, where the table has subsets.</summary>
    string? Subset { get; }

    /// <summary>The key of the row it is under, where its table is under another (ADR-0016 note 46).</summary>
    string? Under => null;

    /// <summary>The value the row holds in a field of its table's description, as an edit would write it; null for none.</summary>
    JsonNode? Value(string field);
}

/// <summary>
/// One registered table (ADR-0016 §1, note 6): its description for a workspace, its rows at a revision, and what a write
/// is. A write answers the workspace changes that make it, after the domain checked it as it checks a typed cell, or
/// refuses by name (<see cref="WorkspaceRefused"/>): the kernel turns rows, a field and a value into edits and knows no
/// financial word.
/// </summary>
public interface IWorkspaceTable
{
    string TableId { get; }

    /// <summary>The workspace application whose workspaces have it.</summary>
    string App { get; }

    /// <summary>The table as this workspace has it: what it has is asked of a workspace, since a selection may add fields.</summary>
    TableDescriptor Describe(WorkspaceState state);

    /// <summary>Every row as the workspace stands, in the table's own order. Read once by a command.</summary>
    IReadOnlyList<ITableRow> Rows(WorkspaceState state);

    /// <summary>
    /// The rows of the table, or of one subset, in the order a page's reader gives them for a sort, with that order's
    /// token (ADR-0016 note 17 of step 2): the one reader that pages the grid, so a position on a screen names the row
    /// the screen drew there. Null where no reader pages the table, and its rows are then named by their keys. A sort
    /// the reader does not take is refused as the reader refuses it (<see cref="WorkspaceRefused"/>), and nothing else
    /// is: a view that names such a sort is read as the page's fault.
    /// </summary>
    OrderedRows? InOrder(WorkspaceState state, string? subset, IReadOnlyList<ViewSort> sorts) => null;

    /// <summary>The date a period counts from in a condition on a date (<c>&lt;field&gt;&gt;5y</c>); none where the table has none.</summary>
    DateOnly? AsOf(WorkspaceState state) => null;

    /// <summary>Why a table of one row has none as the workspace stands, in the domain's words; null where it does not say.</summary>
    string? WhyEmpty(WorkspaceState state) => null;

    /// <summary>The changes that write a value into a row's field.</summary>
    IReadOnlyList<WorkspaceChange> Set(WorkspaceState state, ITableRow row, TableField field, JsonNode? value);

    /// <summary>
    /// What writing a value into a row's figure would do to the rows under it, in the table's words, for a line to say
    /// before Enter (ADR-0016 note 47: "2 parts replaced by 1 part, M3"); null where the table says nothing more than
    /// that the row changes.
    /// </summary>
    string? SetSays(WorkspaceState state, ITableRow row, TableField field, JsonNode? value) => null;

    /// <summary>
    /// The value each field of a template takes for the terms given (ADR-0016 note 50), one for each field the template
    /// says it writes. A template holds nothing of a row, so the answer is the same for every row it is written over.
    /// </summary>
    IReadOnlyDictionary<string, JsonNode?> Template(WorkspaceState state, TableTemplate template, IReadOnlyDictionary<string, JsonNode?> terms) =>
        throw new InvalidOperationException($"Table {TableId} lists the template {template.Name} and does not say what it writes.");

    /// <summary>
    /// The changes that write a value for every row of a subset at once, where the field has a whole-set edit (one edit
    /// on the set, a row's later edit wins); null where it has none, and the rows are then written one by one.
    /// </summary>
    IReadOnlyList<WorkspaceChange>? SetAll(WorkspaceState state, TableSubset subset, TableField field, JsonNode? value) => null;

    /// <summary>The changes that remove the row's own edit of a field, or of every field where none is named; none where it follows already.</summary>
    IReadOnlyList<WorkspaceChange> Reset(WorkspaceState state, ITableRow row, TableField? field);

    /// <summary>The changes that remove a subset's whole-set edit of a field, or of every field; none where there is none.</summary>
    IReadOnlyList<WorkspaceChange> ResetAll(WorkspaceState state, TableSubset subset, TableField? field) => [];

    /// <summary>The changes that remove a row the workspace made; a row it did not make is refused by name, saying what to do instead.</summary>
    IReadOnlyList<WorkspaceChange> Remove(WorkspaceState state, ITableRow row);
}

/// <summary>
/// Where tables come from (ADR-0016 §1): a module's own registrations in this repository; an adapter over a company
/// service's tables at the company, with no kernel or UI change.
/// </summary>
public interface ITableCatalog
{
    IEnumerable<IWorkspaceTable> Tables { get; }
}

/// <summary>
/// What a command cannot read or do, said so that a line can point at it (ADR-0016 §6): a stable code, a sentence, the
/// word at fault where there is one, and what it could have been.
/// </summary>
public sealed class TableRefused(string code, string message, string? word = null, IReadOnlyList<string>? candidates = null) : Exception(message)
{
    public string Code { get; } = code;
    public string? Word { get; } = word;
    public IReadOnlyList<string> Candidates { get; } = candidates ?? [];
}

/// <summary>A registered table with its description for one workspace.</summary>
public sealed record BoundTable(IWorkspaceTable Table, TableDescriptor Descriptor)
{
    /// <summary>The word a sentence uses for the table.</summary>
    public string Word => TableWords.Word(Descriptor);
}

/// <summary>The registered tables (ADR-0016 §1): found for a workspace by id or by words, and their fields by words.</summary>
public sealed class TableRegister
{
    readonly ILookup<string, IWorkspaceTable> byApp;

    /// <summary>Fails at start where two tables have one id.</summary>
    public TableRegister(IEnumerable<ITableCatalog> catalogs)
    {
        var tables = catalogs.SelectMany(c => c.Tables).ToList();
        if (tables.GroupBy(t => t.TableId, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1) is { } twice)
            throw new InvalidOperationException($"Table {twice.Key} is registered twice.");
        byApp = tables.ToLookup(t => t.App);
    }

    /// <summary>Whether an application registers a table of this id, which is known before any workspace describes it.</summary>
    public bool Registers(string app, string tableId) => byApp[app].Any(t => t.TableId == tableId);

    /// <summary>The tables of the workspace's application, each as this workspace has it, in the order registered.</summary>
    public IReadOnlyList<BoundTable> Of(WorkspaceState state)
    {
        var bound = byApp[state.App].Select(t => new BoundTable(t, t.Describe(state))).ToList();
        foreach (var table in bound)
        {
            if (table.Descriptor.TableId != table.Table.TableId || table.Descriptor.App != state.App)
                throw new InvalidOperationException($"Table {table.Table.TableId} describes itself as {table.Descriptor.TableId} of {table.Descriptor.App}.");
            if (table.Descriptor.Fields.All(f => f.Name != table.Descriptor.Key))
                throw new InvalidOperationException($"Table {table.Descriptor.TableId} has no field {table.Descriptor.Key}, its key.");
            // A subset is said by one of its words alone, so no two share one.
            if (table.Descriptor.Subsets.SelectMany(TableWords.Names).GroupBy(w => w).FirstOrDefault(g => g.Count() > 1) is { } shared)
                throw new InvalidOperationException($"Table {table.Descriptor.TableId} has two subsets that answer to {shared.Key}.");
            // A choice is read, written and offered by what it has to choose from (FieldForms).
            if (table.Descriptor.Fields.FirstOrDefault(f => f.Form == FieldForm.Choice && f.Choices is null) is { } unchosen)
                throw new InvalidOperationException($"Table {table.Descriptor.TableId}'s field {unchosen.Name} is a choice with nothing to choose from.");
            // A number with what it is in is read and written by the units it may be in (FieldForms), as it reads or as its figure is written.
            if (table.Descriptor.Fields.FirstOrDefault(f => f.Written is { Form: FieldForm.Money, Choices: not { Count: > 0 } }) is { } unitless)
                throw new InvalidOperationException($"Table {table.Descriptor.TableId}'s field {unitless.Name} is a number with what it is in, and has nothing it may be in.");
            // A figure is of a table under its own (note 47).
            if (table.Descriptor.Fields.FirstOrDefault(f => f.Figure is { } figure && bound.All(t => t.Descriptor.TableId != figure.Of || t.Descriptor.Under != table.Descriptor.TableId)) is { } astray)
                throw new InvalidOperationException($"Table {table.Descriptor.TableId}'s field {astray.Name} is a figure of {astray.Figure!.Of}, which is not under it.");
            // A table under another (note 46): the table above is the workspace's, and its rows are said with no row or as the main table's never.
            if (table.Descriptor.Under is { } above)
            {
                if (bound.All(t => t.Descriptor.TableId != above) || above == table.Descriptor.TableId)
                    throw new InvalidOperationException($"Table {table.Descriptor.TableId} is under {above}, which this workspace has not.");
                if (table.Descriptor.Main) throw new InvalidOperationException($"Table {table.Descriptor.TableId} is under another and cannot be the main table.");
                if (table.Descriptor.SingleRow) throw new InvalidOperationException($"Table {table.Descriptor.TableId} is under another and cannot be a table of one row.");
            }
        }
        return bound;
    }

    /// <summary>The table an id or words name, case ignored; an unknown one is refused with the tables there are, and words two tables share with both.</summary>
    public static BoundTable Find(IReadOnlyList<BoundTable> tables, string name)
    {
        var words = TableWords.Of(name);
        var found = tables.Where(t => TableWords.Names(t.Descriptor).Contains(words)).ToList();
        return found.Count switch
        {
            1 => found[0],
            0 => throw new TableRefused("UnknownTable", tables.Count == 0 ? $"No table {name.Trim()}: this workspace has no tables."
                : $"No table {name.Trim()}: the tables are {TableWords.List(tables.Select(t => t.Word))}.", name.Trim(), [.. tables.Select(t => t.Word)]),
            _ => throw new TableRefused("AmbiguousTable", $"{name.Trim()} could be {TableWords.Either(found.Select(t => t.Descriptor.TableId))}; name one by its id.",
                name.Trim(), [.. found.Select(t => t.Descriptor.TableId)]),
        };
    }

    /// <summary>The field its words name, case ignored; an unknown one is refused with the fields there are, and words two fields share with both.</summary>
    public static TableField Field(BoundTable table, string name)
    {
        var words = TableWords.Of(name);
        var found = table.Descriptor.Fields.Where(f => TableWords.Names(f).Contains(words)).ToList();
        return found.Count switch
        {
            1 => found[0],
            0 => throw new TableRefused("UnknownField", $"{table.Word} has no field {name.Trim()}; its fields are {TableWords.List(table.Descriptor.Fields.Select(TableWords.Word))}.",
                name.Trim(), [.. table.Descriptor.Fields.Select(TableWords.Word)]),
            _ => throw new TableRefused("AmbiguousField", $"{name.Trim()} could be {TableWords.Either(found.Select(TableWords.Word))} of {table.Word}.",
                name.Trim(), [.. found.Select(TableWords.Word)]),
        };
    }
}

/// <summary>How a table's and a field's words are compared and listed: lower case, one space between words.</summary>
public static class TableWords
{
    public static string Of(string text) => string.Join(' ', text.Trim().ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Every name a table answers to: its id and its words.</summary>
    public static IReadOnlySet<string> Names(TableDescriptor table) => new HashSet<string>(table.Words.Select(Of).Append(Of(table.TableId)));

    /// <summary>Every name a field answers to: its name and its words.</summary>
    public static IReadOnlySet<string> Names(TableField field) => new HashSet<string>(field.Words.Select(Of).Append(Of(field.Name)));

    /// <summary>Every name a subset answers to: its name and its words.</summary>
    public static IReadOnlySet<string> Names(TableSubset subset) => new HashSet<string>(subset.Words.Select(Of).Append(Of(subset.Name)));

    /// <summary>The word a sentence uses for a table: its first.</summary>
    public static string Word(TableDescriptor table) => table.Words.Count > 0 ? table.Words[0] : table.TableId;

    /// <summary>The word a sentence uses for a field: its first, as the screen names its column.</summary>
    public static string Word(TableField field) => field.Words.Count > 0 ? field.Words[0] : field.Name;

    public static string List(IEnumerable<string> items) => string.Join(", ", items);

    public static string Either(IEnumerable<string> items) => string.Join(" or ", items);

    /// <summary>Items as a sentence joins them: <c>a, b and c</c>.</summary>
    public static string And(IEnumerable<string> items) => Joined(items, "and");

    /// <summary>Items as a sentence offers them: <c>a, b or c</c>.</summary>
    public static string Or(IEnumerable<string> items) => Joined(items, "or");

    /// <summary>A count with what it counts, as a sentence says it: <c>1 case</c>, <c>3 cases</c>.</summary>
    public static string Counted(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";

    /// <summary>Names as a reading lists them: the first three, then how many more, since a list may hold dozens.</summary>
    public static string Some(IReadOnlyList<string> names) => names.Count <= 3 ? And(names) : $"{string.Join(", ", names.Take(3))} and {names.Count - 3} more";

    static string Joined(IEnumerable<string> items, string last)
    {
        var list = items.ToList();
        return list.Count <= 1 ? string.Concat(list) : $"{string.Join(", ", list.Take(list.Count - 1))} {last} {list[^1]}";
    }
}
