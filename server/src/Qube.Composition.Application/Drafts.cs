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

/// <summary>A row under a draft's row as the draft leaves it: each field's value as written, and whether the draft made the row or removed it.</summary>
/// <param name="Mark"><see cref="Made"/>, <see cref="Removed"/>, or null for a row the draft found and left.</param>
public sealed record DraftRow(string Key, IReadOnlyList<string> Written, string? Mark)
{
    public const string Made = "made", Removed = "removed";
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
            [.. tables.Where(t => t.Descriptor.Under == draft.Table).Select(under => Under(under, draft.Key, left))],
            lines);
    }

    static ITableRow Row(BoundTable table, WorkspaceState state, string key) =>
        table.Table.Rows(state).SingleOrDefault(r => r.Key == key) ?? throw new TableRefused("UnknownRow", $"{table.Word} has no row {key}.", key);

    static string Written(TableField field, ITableRow row) => FieldForms.Write(field.Spec, row.Value(field.Name));

    static DraftTable Under(BoundTable table, string key, WorkspaceState left) => new(table.Descriptor.TableId, table.Descriptor.Title,
        [.. table.Descriptor.Fields.Select(f => f.Title)],
        [.. table.Table.Rows(left).Where(r => r.Under == key).Select(r => new DraftRow(r.Key, [.. table.Descriptor.Fields.Select(f => Written(f, r))], null))]);
}
