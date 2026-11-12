using System.Text.Json;
using Qube.Composition.Application.Tables;
using Qube.Composition.Application.Workspaces;

namespace Qube.Composition.Application;

/// <summary>
/// A draft as a page holds it (ADR-0016 §10, note 54): the row it is of, the revision of the workspace it was opened
/// at, and the lines accepted so far, each a whole line of the workbench that names the row. Nothing of it is stored on
/// the server: whoever holds the lines can have them read again.
/// </summary>
public sealed record LineDraft(string Table, string Key, long Revision, IReadOnlyList<string> Lines);

/// <summary>
/// What a page draws a field's input from: the word a line says it by, whether a command may write it, the form of its
/// value and, where it is one of a few, each as a line writes it.
/// </summary>
/// <param name="Takes">What a value of it looks like, in words, as the table's description says it.</param>
/// <param name="FigureOf">The table under its own whose rows it is a figure of (note 47), where it is one.</param>
public sealed record FieldInput(string Name, string Title, string Word, string? Group, bool Editable, bool Optional, string Form, string Takes, IReadOnlyList<string>? Choices,
    string? Unit, string? FigureOf)
{
    public static FieldInput Of(TableField field) => new(field.Name, field.Title, TableWords.Word(field), field.Group, field.Editable, field.Optional, FormOf(field.Form),
        FieldForms.Takes(field.Written, choicesBeside: true), Written(field.Spec), field.Unit, field.Figure?.Of);

    public static string FormOf(FieldForm form) => JsonNamingPolicy.CamelCase.ConvertName(form.ToString());

    /// <summary>Each value a choice may be, once, as a cell writes it and a command reads it back.</summary>
    public static IReadOnlyList<string>? Written(FieldSpec spec) => spec.Choices is { } choices ? [.. choices.Values.Distinct().Select(v => FieldForms.Write(spec, v))] : null;
}

/// <summary>A field of a draft's row: its value as a command writes it, and whether the draft's lines changed it.</summary>
public sealed record DraftField(FieldInput Field, string Written, bool Changed);

/// <summary>
/// A row under a draft's row as the draft leaves it: each field's value as written, and what the draft did to the row.
/// A row the draft removed is as it was when the draft was opened.
/// </summary>
/// <param name="Mark"><see cref="Made"/>, <see cref="Changed"/>, <see cref="Removed"/>, or null for a row the draft found and left.</param>
public sealed record DraftRow(string Key, IReadOnlyList<string> Written, string? Mark)
{
    public const string Made = "made", Changed = "changed", Removed = "removed";
}

/// <summary>A table under a draft's row: the word a line says it by, its fields, and the rows under the row.</summary>
public sealed record DraftTable(string Table, string Title, string Word, IReadOnlyList<FieldInput> Fields, IReadOnlyList<DraftRow> Rows);

/// <summary>A term a template takes (note 50), as a page asks for it before the template is written.</summary>
public sealed record DraftTerm(string Name, string Title, bool Required, string Form, string Takes, IReadOnlyList<string>? Choices);

/// <summary>A template of a draft's table (note 50): <c>set &lt;row&gt; as &lt;word&gt; [name=value …]</c> writes it over the row.</summary>
public sealed record DraftTemplate(string Word, string Title, IReadOnlyList<DraftTerm> Takes);

/// <summary>A line of a draft as it was read: what it said it did, or why it can no longer be read.</summary>
public sealed record DraftLine(string Line, string? Said, LineRefusal? Refused);

/// <summary>A draft's row as the source alone gives it: each field's value as written, in the order of the form's fields, and the rows under it.</summary>
public sealed record DraftOriginal(IReadOnlyList<string> Written, IReadOnlyList<DraftTable> Under);

/// <summary>
/// What a page draws a draft's dialog from (note 54): the row's fields in the table's order, the tables under it, its
/// table's templates, and each line as read. Where a line is refused, the form is that of the lines before it, and the
/// lines after are not read.
/// </summary>
/// <param name="Word">The word a line says the row's table by.</param>
/// <param name="Original">The row before anyone changed it; null for a row the workspace made.</param>
public sealed record DraftForm(string Table, string Word, string Key, long Revision, IReadOnlyList<DraftField> Fields, IReadOnlyList<DraftTable> Under,
    IReadOnlyList<DraftTemplate> Templates, IReadOnlyList<DraftLine> Lines, DraftOriginal? Original);

/// <summary>
/// The form of a draft's row, read from three states of its workspace: the one the draft was opened on, the one its
/// lines left, and the one with no edit at all.
/// </summary>
static class DraftForms
{
    public static DraftForm Of(LineDraft draft, IReadOnlyList<BoundTable> tables, WorkspaceState opened, WorkspaceState left, WorkspaceState source, IReadOnlyList<DraftLine> lines)
    {
        var table = tables.SingleOrDefault(t => t.Descriptor.TableId == draft.Table) ?? throw new TableRefused("UnknownTable", $"The workspace has no table {draft.Table}.");
        var under = tables.Where(t => t.Descriptor.Under == draft.Table).ToList();
        var fields = table.Descriptor.Fields;
        var (before, after) = (Row(table, opened, draft.Key), Row(table, left, draft.Key));
        return new(draft.Table, table.Word, draft.Key, draft.Revision,
            [.. fields.Select(f => new DraftField(FieldInput.Of(f), Written(f, after), Written(f, after) != Written(f, before)))],
            [.. under.Select(t => Under(t, draft.Key, opened, left))],
            [.. (table.Descriptor.Templates ?? []).Select(Template)],
            lines,
            // The source's rows are read on the state with no edit, where a row the workspace made is not.
            table.Table.Rows(source).SingleOrDefault(r => r.Key == draft.Key) is { } original
                ? new([.. fields.Select(f => Written(f, original))], [.. under.Select(t => Under(t, draft.Key, source, source))])
                : null);
    }

    static ITableRow Row(BoundTable table, WorkspaceState state, string key) =>
        table.Table.Rows(state).SingleOrDefault(r => r.Key == key) ?? throw new TableRefused("UnknownRow", $"{table.Word} has no row {key}.", key);

    static string Written(TableField field, ITableRow row) => FieldForms.Write(field.Spec, row.Value(field.Name));

    static DraftTemplate Template(TableTemplate template) => new(template.Words.Count > 0 ? template.Words[0] : template.Name, template.Title,
        [.. template.Takes.Select(t => new DraftTerm(t.Name, t.Title, t.Required, FieldInput.FormOf(t.Form), FieldForms.Takes(t.Spec, choicesBeside: true), FieldInput.Written(t.Spec)))]);

    // The rows the draft was opened on in their order, each as the draft leaves it or, removed, as it was; then the rows
    // the draft made. A row made and removed within the draft is in neither state, and is not listed.
    static DraftTable Under(BoundTable table, string key, WorkspaceState opened, WorkspaceState left)
    {
        var fields = table.Descriptor.Fields;
        IReadOnlyList<string> Row(ITableRow row) => [.. fields.Select(f => Written(f, row))];
        var (before, after) = (Rows(opened), Rows(left));
        var (was, now) = (before.ToDictionary(r => r.Key, Row), after.ToDictionary(r => r.Key, Row));
        return new(table.Descriptor.TableId, table.Descriptor.Title, table.Word, [.. fields.Select(FieldInput.Of)],
        [
            .. before.Select(r => !now.TryGetValue(r.Key, out var written) ? new DraftRow(r.Key, was[r.Key], DraftRow.Removed)
                : new DraftRow(r.Key, written, written.SequenceEqual(was[r.Key]) ? null : DraftRow.Changed)),
            .. after.Where(r => !was.ContainsKey(r.Key)).Select(r => new DraftRow(r.Key, now[r.Key], DraftRow.Made)),
        ]);

        List<ITableRow> Rows(WorkspaceState state) => [.. table.Table.Rows(state).Where(r => r.Under == key)];
    }
}
