using System.Globalization;
using System.Text;
using Qube.Composition.Application.Workspaces;
using Qube.Composition.Contracts;

namespace Qube.Composition.Application.Tables;

// ADR-0016 §3 and its note 44 of 2026-11-11: `copy` and `export` take rows out of the workbench, as cells a spreadsheet
// reads. The server reads their lines, as it reads every line; a reading says the rows and the fields, and the content
// is written when the page asks for it at Enter. Neither is a capability, and neither writes a workspace.

/// <summary>What a line of a verb that takes rows out means where it says no rows.</summary>
enum RowsUnsaid
{
    /// <summary>
    /// The Selection of the grid behind the line, or the row the keyboard is on, as an edit verb reads rows left out
    /// (note 18 of step 2). A table named alone says no rows of it, and is refused.
    /// </summary>
    Selection,

    /// <summary>The rows the grid behind the line shows; and every row of a table the line names alone.</summary>
    Shown,
}

/// <summary>
/// The rows and the fields a line of <c>copy</c> or <c>export</c> means, read: the rows in the order they are written
/// in, the fields in the order named, or all of the table's (<paramref name="EveryField"/>), and each argument as read.
/// </summary>
sealed record RowsTaken(BoundTable Table, IReadOnlyList<ITableRow> Rows, IReadOnlyList<TableField> Fields, bool EveryField, bool Headers, IReadOnlyList<LineArgument> Arguments)
{
    /// <summary>The rows in words: one by its key, several by their count.</summary>
    public string RowsInWords => Rows.Count == 1 ? $"{Rows[0].Key} of {Table.Word}" : $"{TableWords.Counted(Rows.Count, "row")} of {Table.Word}";

    /// <summary>The fields in words: those named, or that they are all of the table's.</summary>
    public string FieldsInWords => EveryField ? $"every field ({Fields.Count.ToString(CultureInfo.InvariantCulture)})"
        : $"{TableWords.Counted(Fields.Count, "field")}: {TableWords.List(Fields.Select(TableWords.Word))}";

    /// <summary>The rows as a reading names them.</summary>
    public LineRows Reading => TableService.Titled(Table, $"{Table.Word}, {TableWords.Counted(Rows.Count, "row")}", Rows);

    /// <summary>The cells for a clipboard: tabs between cells, a header line where the line asked for one, and no line end after the last line.</summary>
    public string ForClipboard() => Lines('\t', Headers, endLast: false);

    /// <summary>
    /// The cells as a file of comma-separated values (RFC 4180): a header line always, a line end after every line, and
    /// a byte order mark first, so that a spreadsheet reads the file as UTF-8.
    /// </summary>
    public string ForFile() => $"\uFEFF{Lines(',', headers: true, endLast: true)}";

    // The cells as lines: the separator between cells and CRLF between lines. A header line names each field by the
    // word a command names it by. A cell is written as a spreadsheet reads it (FieldForms.Cell); one that holds the
    // separator, a quote or a line break is quoted, its quotes doubled (RFC 4180, which a spreadsheet's clipboard
    // follows with tabs). A line of one empty cell is written as a quoted empty cell: left blank, it is a line some
    // readers skip, and the last one is no line at all, so a row would be lost without a word.
    string Lines(char separator, bool headers, bool endLast)
    {
        var text = new StringBuilder();
        var lines = (headers ? new[] { Fields.Select(TableWords.Word) } : []).Concat(Rows.Select(row => Fields.Select(f => FieldForms.Cell(f.Spec, row.Value(f.Name)))));
        var first = true;
        foreach (var line in lines)
        {
            if (!first) text.Append("\r\n");
            first = false;
            IReadOnlyList<string> cells = [.. line];
            text.Append(cells is [""] ? "\"\"" : string.Join(separator, cells.Select(cell => Quoted(cell, separator))));
        }
        if (endLast) text.Append("\r\n");
        return text.ToString();
    }

    static string Quoted(string cell, char separator) =>
        cell.AsSpan().IndexOfAny([separator, '"', '\r', '\n']) < 0 ? cell : $"\"{cell.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}

/// <summary>The reader of <c>[&lt;table&gt;] [&lt;rows&gt;] [&lt;fields&gt;] [with headers]</c>, which <c>copy</c> and <c>export</c> share.</summary>
static class RowsOut
{
    const string With = "with";
    const string Headers = "headers";

    /// <param name="verb">The verb's word, for what it refuses.</param>
    /// <param name="unsaid">What a line that says no rows means.</param>
    /// <param name="mostCells">The most cells a line takes out: more are refused, never cut.</param>
    public static RowsTaken Read(LineContext line, TableRegister register, string verb, RowsUnsaid unsaid, int mostCells)
    {
        var tables = register.Of(line.State);
        var words = line.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var headers = words.Length >= 2 && SelectorReader.Is(words[^2], With) && SelectorReader.Is(words[^1], Headers);
        var (rowsSaid, fieldsSaid) = TableService.Split(string.Join(' ', headers ? words[..^2] : words), tables, line.View, fieldsFollow: true);

        var (resolved, rows, ofScreen) = Rows(rowsSaid, tables, line, unsaid);
        var table = resolved.Table;
        if (resolved.Rows.Count == 0) throw TableService.NoRowMatches(resolved, verb, $"{table.Word} has no row to {verb}.");
        // Rows said as the screen's come in the order the grid shows them, where the server knows that order.
        var ordered = ofScreen && line.View is { } view ? Views.InGridOrder(view, table, line.State, resolved.Rows) : resolved.Rows;

        var fields = Fields(table, fieldsSaid);
        var cells = (long)ordered.Count * fields.Count;
        if (cells > mostCells)
            throw new TableRefused("TooManyCells",
                $"{TableWords.Counted(ordered.Count, "row")} of {TableWords.Counted(fields.Count, "field")} are {cells.ToString("N0", CultureInfo.InvariantCulture)} cells, "
                + $"and {verb} takes {mostCells.ToString("N0", CultureInfo.InvariantCulture)} at most: name fewer rows or fewer fields.");
        return new(table, ordered, fields, fieldsSaid.Length == 0, headers, fieldsSaid.Length == 0 ? [rows] : [rows, new("fields", fieldsSaid)]);
    }

    // The rows a line means, the argument that says them, and whether they are rows of the screen: the rows as said,
    // or, where the line says none, those `unsaid` gives of the grid behind it. A line with no rows and no grid is
    // refused as a selector that says nothing is, with the ways to say rows.
    static (ResolvedRows Resolved, LineArgument Said, bool OfScreen) Rows(string? said, IReadOnlyList<BoundTable> tables, LineContext line, RowsUnsaid unsaid)
    {
        var (state, view) = (line.State, line.View);
        if (said is not null)
        {
            var (selector, text, _) = TableService.RowsSaid(tables, null, said, view);
            if (unsaid == RowsUnsaid.Shown && selector is { SaysNoRows: true, Table: { } alone } && !TableRegister.Find(tables, alone).Descriptor.SingleRow) selector = selector with { All = true };
            return (Selectors.Resolve(selector, tables, state, view), new("rows", text), selector.Screen is not null);
        }
        if (view is null) throw Selectors.NoRows(null, tables);
        if (unsaid == RowsUnsaid.Shown)
            return (Selectors.Resolve(new RowSelector(Screen: ScreenRows.Shown), tables, state, view), new("rows", SelectorReader.ShownWord, ArgumentSource.Context), true);
        var leftOut = Selectors.LeftOut(view, tables, state);
        return (leftOut, new("rows", TableService.Few([.. leftOut.Rows.Select(r => r.Key)]), ArgumentSource.Context), true);
    }

    // The fields a line names, each once, in the order named; all of the table's where it names none.
    static IReadOnlyList<TableField> Fields(BoundTable table, string said)
    {
        if (said.Length == 0) return table.Descriptor.Fields;
        IReadOnlyList<TableField> fields = [.. said.Split(',').Select(name => SelectorReader.Words(name) is { Length: > 0 } named
            ? TableRegister.Field(table, named) : throw new TableRefused("NoField", "A field is named after each comma."))];
        return fields.GroupBy(f => f.Name).FirstOrDefault(g => g.Count() > 1) is not { } twice ? fields
            : throw new TableRefused("FieldTwice", $"{TableWords.Word(twice.First())} is named twice; name each field once.", TableWords.Word(twice.First()));
    }

    /// <summary>
    /// What could follow the line's last word: the ways to say rows, then the table's fields, again after each comma,
    /// whether or not a command sets them; and <c>with headers</c> where <paramref name="headers"/> may be asked for.
    /// </summary>
    public static LineCompletions Complete(LineContext line, TableRegister register, RowsUnsaid unsaid, bool headers)
    {
        var offered = TableCompletions.Of(line.Text, register.Of(line.State), line.State, CommandArgumentKind.Field, line.View, fieldsFollow: true, unsaid);
        var (done, typed, at) = LineWords.Typing(line.Text);
        if (!headers || typed.Length == 0 || done.Count == 0 || !With.StartsWith(typed, StringComparison.OrdinalIgnoreCase)) return offered;
        return offered with { Items = [.. offered.Items, new($"{With} {Headers}", at)] };
    }
}

/// <summary>
/// <c>copy [&lt;rows&gt;] [&lt;fields&gt;] [with headers]</c> (note 44 of step 4): the rows' cells for the clipboard, tabs
/// between them, with no header line unless one is asked for, as a grid's own copy.
/// </summary>
sealed class CopyVerb(TableRegister register, int mostCells) : IContentVerb
{
    const string Word = "copy";

    public ViewVerbDescriptor Descriptor { get; } = new("copy", "Copy rows of a table to the clipboard, as cells a spreadsheet reads",
        new([Word], [new("rows", CommandArgumentKind.Rows, Required: false), new("fields", CommandArgumentKind.Text, Required: false)]));

    public ViewRead Read(LineContext line)
    {
        var taken = Taken(line);
        var what = $"{taken.RowsInWords}, {taken.FieldsInWords}{(taken.Headers ? ", with headers" : "")}";
        return new(taken.Arguments, taken.Reading, $"Copies {what}.", new LineCopy($"Copied {what}."));
    }

    public string Content(LineContext line) => Taken(line).ForClipboard();

    public LineCompletions Complete(LineContext line) => RowsOut.Complete(line, register, RowsUnsaid.Selection, headers: true);

    RowsTaken Taken(LineContext line) => RowsOut.Read(line, register, Word, RowsUnsaid.Selection, mostCells);
}

/// <summary>
/// <c>export [&lt;table&gt;] [&lt;rows&gt;] [&lt;fields&gt;]</c> (note 44 of step 4): the rows as a file of comma-separated
/// values with a header line, named by the table's word.
/// </summary>
sealed class ExportVerb(TableRegister register, int mostCells) : IContentVerb
{
    const string Word = "export";

    public ViewVerbDescriptor Descriptor { get; } = new("export", "Save rows of a table as a file a spreadsheet opens",
        new([Word], [new("rows", CommandArgumentKind.Rows, Required: false), new("fields", CommandArgumentKind.Text, Required: false)]));

    public ViewRead Read(LineContext line)
    {
        var taken = Taken(line);
        var file = $"{taken.Table.Word}.csv";
        var what = $"{taken.RowsInWords} to {file}, {taken.FieldsInWords}";
        return new(taken.Arguments, taken.Reading, $"Exports {what}.", new LineExport(file, $"Exported {what}."));
    }

    public string Content(LineContext line) => Taken(line).ForFile();

    public LineCompletions Complete(LineContext line) => RowsOut.Complete(line, register, RowsUnsaid.Shown, headers: false);

    RowsTaken Taken(LineContext line) => RowsOut.Read(line, register, Word, RowsUnsaid.Shown, mostCells);
}
