using Qube.Composition.Application.Tables;
using Qube.Composition.Application.Workspaces;

namespace Qube.Composition.Application;

/// <summary>
/// A draft as a page holds it (ADR-0016 §10, note 54): the row it is of, the revision of the workspace it was opened
/// at, and the lines accepted so far, each a whole line of the workbench that names the row. Nothing of it is stored on
/// the server: whoever holds the lines can have them read again.
/// </summary>
public sealed record LineDraft(string Table, string Key, long Revision, IReadOnlyList<string> Lines);

/// <summary>A field of a draft's row: its value as a command writes it, and whether the draft's lines changed it.</summary>
public sealed record DraftField(string Name, string Title, string? Group, bool Editable, string Written, bool Changed);

/// <summary>
/// A row under a draft's row as the draft leaves it: each field's value as written, and what the draft did to the row.
/// A row the draft removed is as it was when the draft was opened.
/// </summary>
/// <param name="Mark"><see cref="Made"/>, <see cref="Changed"/>, <see cref="Removed"/>, or null for a row the draft found and left.</param>
public sealed record DraftRow(string Key, IReadOnlyList<string> Written, string? Mark)
{
    public const string Made = "made", Changed = "changed", Removed = "removed";
}

/// <summary>A table under a draft's row: its fields' titles, and the rows under the row.</summary>
public sealed record DraftTable(string Table, string Title, IReadOnlyList<string> Fields, IReadOnlyList<DraftRow> Rows);

/// <summary>A line of a draft as it was read: what it said it did, or why it can no longer be read.</summary>
public sealed record DraftLine(string Line, string? Said, LineRefusal? Refused);

/// <summary>
/// What a page draws a draft's dialog from (note 54): the row's fields in the table's order, the tables under it, and
/// each line as read. Where a line is refused, the form is that of the lines before it, and the lines after are not read.
/// </summary>
public sealed record DraftForm(string Table, string Key, long Revision, IReadOnlyList<DraftField> Fields, IReadOnlyList<DraftTable> Under, IReadOnlyList<DraftLine> Lines);

/// <summary>The form of a draft's row, read from a state the draft's lines left and the state it was opened on.</summary>
static class DraftForms
{
    public static DraftForm Of(LineDraft draft, IReadOnlyList<BoundTable> tables, WorkspaceState opened, WorkspaceState left, IReadOnlyList<DraftLine> lines)
    {
        var table = tables.SingleOrDefault(t => t.Descriptor.TableId == draft.Table) ?? throw new TableRefused("UnknownTable", $"The workspace has no table {draft.Table}.");
        var (before, after) = (Row(table, opened, draft.Key), Row(table, left, draft.Key));
        return new(draft.Table, draft.Key, draft.Revision,
            [.. table.Descriptor.Fields.Select(f => new DraftField(f.Name, f.Title, f.Group, f.Editable, Written(f, after), Written(f, after) != Written(f, before)))],
            [.. tables.Where(t => t.Descriptor.Under == draft.Table).Select(under => Under(under, draft.Key, opened, left))],
            lines);
    }

    static ITableRow Row(BoundTable table, WorkspaceState state, string key) =>
        table.Table.Rows(state).SingleOrDefault(r => r.Key == key) ?? throw new TableRefused("UnknownRow", $"{table.Word} has no row {key}.", key);

    static string Written(TableField field, ITableRow row) => FieldForms.Write(field.Spec, row.Value(field.Name));

    // The rows the draft was opened on in their order, each as the draft leaves it or, removed, as it was; then the rows
    // the draft made. A row made and removed within the draft is in neither state, and is not listed.
    static DraftTable Under(BoundTable table, string key, WorkspaceState opened, WorkspaceState left)
    {
        var fields = table.Descriptor.Fields;
        IReadOnlyList<string> Row(ITableRow row) => [.. fields.Select(f => Written(f, row))];
        var (before, after) = (Rows(opened), Rows(left));
        var (was, now) = (before.ToDictionary(r => r.Key, Row), after.ToDictionary(r => r.Key, Row));
        return new(table.Descriptor.TableId, table.Descriptor.Title, [.. fields.Select(f => f.Title)],
        [
            .. before.Select(r => !now.TryGetValue(r.Key, out var written) ? new DraftRow(r.Key, was[r.Key], DraftRow.Removed)
                : new DraftRow(r.Key, written, written.SequenceEqual(was[r.Key]) ? null : DraftRow.Changed)),
            .. after.Where(r => !was.ContainsKey(r.Key)).Select(r => new DraftRow(r.Key, now[r.Key], DraftRow.Made)),
        ]);

        List<ITableRow> Rows(WorkspaceState state) => [.. table.Table.Rows(state).Where(r => r.Under == key)];
    }
}
