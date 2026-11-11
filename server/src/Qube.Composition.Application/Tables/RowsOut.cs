using System.Globalization;
using System.Text;
using Qube.Composition.Application.Workspaces;
using Qube.Composition.Contracts;

namespace Qube.Composition.Application.Tables;

// ADR-0016 §3 and its note 44 of 2026-11-11: `copy` and `export` take rows out of the workbench, as cells a spreadsheet
// reads. The server reads their lines, as it reads every line; a reading says the rows and the fields, and the content
// is written when the page asks for it at Enter. Neither is a capability, and neither writes a workspace.

/// <summary>
/// The rows and the fields a line of <c>copy</c> or <c>export</c> means, read: the rows in the order they are written
/// in, the fields in the order named, or all of the table's (<paramref name="EveryField"/>), and each argument as read.
/// </summary>
sealed record RowsTaken(BoundTable Table, IReadOnlyList<ITableRow> Rows, IReadOnlyList<TableField> Fields, bool EveryField, bool Headers, IReadOnlyList<LineArgument> Arguments)
{
    // The first rows a reading names, as a command's reading names them.
    const int Listed = 10;

    /// <summary>The rows in words: one by its key, several by their count.</summary>
    public string RowsSaid => Rows.Count == 1 ? $"{Rows[0].Key} of {Table.Word}" : $"{TableWords.Counted(Rows.Count, "row")} of {Table.Word}";

    /// <summary>The fields in words: those named, or that they are all of the table's.</summary>
    public string FieldsSaid => EveryField ? $"every field ({Fields.Count.ToString(CultureInfo.InvariantCulture)})"
        : $"{TableWords.Counted(Fields.Count, "field")}: {TableWords.List(Fields.Select(TableWords.Word))}";

    public LineRows Named => new(Table.Descriptor.TableId, $"{Table.Word}, {TableWords.Counted(Rows.Count, "row")}", Rows.Count, [.. Rows.Take(Listed).Select(r => r.Key)]);

    /// <summary>
    /// The cells as lines: <paramref name="separator"/> between cells and CRLF between lines, and after the last where
    /// <paramref name="endLast"/>. A header line names each field by the word a command names it by. A cell is written
    /// as a spreadsheet reads it (<see cref="FieldForms.Cell"/>); one that holds the separator, a quote or a line break
    /// is quoted, its quotes doubled (RFC 4180, which a spreadsheet's clipboard follows with tabs).
    /// </summary>
    public string Text(char separator, bool headers, bool endLast)
    {
        var text = new StringBuilder();
        var lines = (headers ? new[] { Fields.Select(TableWords.Word) } : []).Concat(Rows.Select(row => Fields.Select(f => FieldForms.Cell(f.Spec, row.Value(f.Name)))));
        var first = true;
        foreach (var line in lines)
        {
            if (!first) text.Append("\r\n");
            first = false;
            text.AppendJoin(separator, line.Select(cell => Quoted(cell, separator)));
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
    /// <param name="wholeGrid">
    /// What a line that says no rows means. For <c>export</c>, the rows the grid behind the line shows, and every row of
    /// a table it names alone; for <c>copy</c>, the Selection or the row the keyboard is on, as an edit verb reads rows
    /// left out (note 18 of step 2), and a table named alone is refused.
    /// </param>
    /// <param name="mostCells">The most cells a line takes out: more are refused, never cut.</param>
    public static RowsTaken Read(LineContext line, TableRegister register, string verb, bool wholeGrid, int mostCells)
    {
        var (state, view) = (line.State, line.View);
        var tables = register.Of(state);
        var words = line.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var headers = words.Length >= 2 && SelectorReader.Is(words[^2], With) && SelectorReader.Is(words[^1], Headers);
        var (rowsSaid, fieldsSaid) = TableService.Split(string.Join(' ', headers ? words[..^2] : words), tables, view, fieldsFollow: true);

        ResolvedRows resolved;
        LineArgument rows;
        var fromScreen = rowsSaid is null;
        if (rowsSaid is not null)
        {
            var (selector, said, _) = TableService.RowsSaid(tables, null, rowsSaid, view);
            if (wholeGrid && selector is { SaysNoRows: true, Table: { } alone } && !TableRegister.Find(tables, alone).Descriptor.SingleRow) selector = selector with { All = true };
            resolved = Selectors.Resolve(selector, tables, state, view);
            (rows, fromScreen) = (new("rows", said), selector.Screen is not null);
        }
        else if (view is null)
        {
            // No rows and no grid: refused as a selector that says nothing is, with the ways to say rows.
            resolved = Selectors.Resolve(new RowSelector(), tables, state);
            rows = new("rows", "");
        }
        else if (wholeGrid)
        {
            resolved = Selectors.Resolve(new RowSelector(Screen: ScreenRows.Shown), tables, state, view);
            rows = new("rows", SelectorReader.ShownWord, ArgumentSource.Context);
        }
        else
        {
            resolved = Selectors.LeftOut(view, tables, state);
            rows = new("rows", TableService.Few([.. resolved.Rows.Select(r => r.Key)]), ArgumentSource.Context);
        }

        var table = resolved.Table;
        if (resolved.Rows.Count == 0)
            throw new TableRefused("NoRowMatches", resolved.Where.Count > 0 ? $"No row of {table.Word} matches {string.Join(" and ", resolved.Where)}: there is nothing to {verb}."
                : $"{table.Word} has no row to {verb}.");
        // Rows said as the screen's come in the order the grid shows them, where the server knows that order.
        var ordered = fromScreen && view is not null ? Views.InGridOrder(view, table, state, resolved.Rows) : resolved.Rows;

        IReadOnlyList<TableField> fields = fieldsSaid.Length == 0 ? table.Descriptor.Fields : [.. fieldsSaid.Split(',').Select(name => SelectorReader.Words(name) is { Length: > 0 } named
            ? TableRegister.Field(table, named) : throw new TableRefused("NoField", "A field is named after each comma."))];
        if (fields.GroupBy(f => f.Name).FirstOrDefault(g => g.Count() > 1) is { } twice)
            throw new TableRefused("FieldTwice", $"{TableWords.Word(twice.First())} is named twice; name each field once.", TableWords.Word(twice.First()));

        var cells = (long)ordered.Count * fields.Count;
        if (cells > mostCells)
            throw new TableRefused("TooManyCells",
                $"{TableWords.Counted(ordered.Count, "row")} of {TableWords.Counted(fields.Count, "field")} are {cells.ToString("N0", CultureInfo.InvariantCulture)} cells, "
                + $"and {verb} takes {mostCells.ToString("N0", CultureInfo.InvariantCulture)} at most: name fewer rows or fewer fields.");
        IReadOnlyList<LineArgument> arguments = fieldsSaid.Length == 0 ? [rows] : [rows, new("fields", fieldsSaid)];
        return new(table, ordered, fields, fieldsSaid.Length == 0, headers, [.. arguments.Where(a => a.Text.Length > 0)]);
    }

    /// <summary>
    /// What could follow the line's last word: the ways to say rows, then the table's fields, again after each comma,
    /// whether or not a command sets them; and <c>with headers</c> where <paramref name="headers"/> may be asked for.
    /// </summary>
    public static LineCompletions Complete(LineContext line, TableRegister register, bool headers)
    {
        var offered = TableCompletions.Of(line.Text, register.Of(line.State), line.State, CommandArgumentKind.Field, line.View, fieldList: true);
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
        var what = $"{taken.RowsSaid}, {taken.FieldsSaid}{(taken.Headers ? ", with headers" : "")}";
        return new(taken.Arguments, taken.Named, $"Copies {what}.", new LineCopy($"Copied {what}."));
    }

    public string Content(LineContext line)
    {
        var taken = Taken(line);
        return taken.Text('\t', taken.Headers, endLast: false);
    }

    public LineCompletions Complete(LineContext line) => RowsOut.Complete(line, register, headers: true);

    RowsTaken Taken(LineContext line) => RowsOut.Read(line, register, Word, wholeGrid: false, mostCells);
}

/// <summary>
/// <c>export [&lt;table&gt;] [&lt;rows&gt;] [&lt;fields&gt;]</c> (note 44 of step 4): the rows as a file of comma-separated
/// values with a header line, named by the table's word, in UTF-8 with a byte order mark so that a spreadsheet reads
/// it as that.
/// </summary>
sealed class ExportVerb(TableRegister register, int mostCells) : IContentVerb
{
    const string Word = "export";

    public ViewVerbDescriptor Descriptor { get; } = new("export", "Save rows of a table as a file a spreadsheet opens",
        new([Word], [new("rows", CommandArgumentKind.Rows, Required: false), new("fields", CommandArgumentKind.Text, Required: false)]));

    public ViewRead Read(LineContext line)
    {
        var taken = Taken(line);
        var file = $"{taken.Table.Word.Replace(' ', '-')}.csv";
        var what = $"{taken.RowsSaid} to {file}, {taken.FieldsSaid}";
        return new(taken.Arguments, taken.Named, $"Exports {what}.", new LineExport(file, $"Exported {what}."));
    }

    public string Content(LineContext line) => $"\uFEFF{Taken(line).Text(',', headers: true, endLast: true)}";

    public LineCompletions Complete(LineContext line) => RowsOut.Complete(line, register, headers: false);

    RowsTaken Taken(LineContext line) => RowsOut.Read(line, register, Word, wholeGrid: true, mostCells);
}
