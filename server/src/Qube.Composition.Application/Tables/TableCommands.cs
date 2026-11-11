using System.Globalization;
using System.Text.Json.Nodes;
using Qube.Composition.Application.Workspaces;
using Qube.Composition.Contracts;

namespace Qube.Composition.Application.Tables;

// ADR-0016 §3, §6 and its notes 9, 10 and 12 of 2026-11-11: the kernel's edit verbs over any registered table. Each
// reads its rows with the one selector, lets the table say what a write is, and writes one all-or-none command.

/// <summary>
/// What an edit verb did (ADR-0016 §6): the workspace it left, the command it wrote, or none where nothing changed; a
/// sentence in the table's own words; the table; how many rows matched, changed and already held the value; and the
/// first rows to look at.
/// </summary>
public sealed record TableResult(WorkspaceState State, string? Command, string Said, string Table, int Matched, int Changed, int Held, IReadOnlyList<string> Rows);

/// <summary>
/// <c>set</c>, <c>reset</c> and <c>remove</c> over the rows of a registered table (ADR-0016 §3). The rows are read as
/// the line spells them and the values as <c>name=value</c> (note 12), by the one reader of each; a refusal names the
/// word at fault (<see cref="TableRefused"/>) or is the domain's own (<see cref="WorkspaceRefused"/>), and writes nothing.
/// </summary>
public sealed class TableService(TableRegister register, WorkspaceService workspaces)
{
    const int Named = 3;
    const int Listed = 10;

    // What a verb would do with a workspace as it stands: read and checked as the command checks, and not written. No
    // row touched is a command that changes nothing.
    /// <param name="Keyed">Where the rows were read from a screen: the table as a request names it, beside the rows by their keys (note 17 of step 2).</param>
    /// <param name="Context">What the line left out and the screen behind it gave (note 18 of step 2): its rows, and for <c>reset</c> alone the field too.</param>
    sealed record Plan(WorkspaceState State, ResolvedRows Resolved, IReadOnlyList<WorkspaceChange> Changes, IReadOnlyList<ITableRow> Touched, string Said, string Would, string Words,
        string? Keyed = null, TableField? Context = null);

    /// <summary>
    /// Writes one value or several into the rows (note 9): a row that holds a value gets no edit for it and is counted;
    /// rows said as a whole subset, or as all, take the field's whole-set edit where it has one; the rest is one batch.
    /// </summary>
    public TableResult Set(string workspaceId, long? revision, Caller caller, string? table, string? rows, string set) =>
        Write(caller, PlanSet(WorkspaceCommands.Owned(workspaces, workspaceId, caller, revision), caller, table, rows, set));

    /// <summary>
    /// Removes the rows' own edits, of one field or of every field where none is named (note 10): the field follows its
    /// source or its rule again. Rows said as a whole subset, or as all, lose the whole-set edit too. A row with no edit
    /// is counted as such.
    /// </summary>
    public TableResult Reset(string workspaceId, long? revision, Caller caller, string? table, string? rows, string? field) =>
        Write(caller, PlanReset(WorkspaceCommands.Owned(workspaces, workspaceId, caller, revision), table, rows, field));

    /// <summary>Removes rows the workspace made, with their edits (note 10); a row it did not make is refused by the table, by name, and nothing is removed.</summary>
    public TableResult Remove(string workspaceId, long? revision, Caller caller, string? table, string? rows) =>
        Write(caller, PlanRemove(WorkspaceCommands.Owned(workspaces, workspaceId, caller, revision), table, rows));

    /// <summary>
    /// <c>set</c> as a line types it (ADR-0016 §5, note 15): the rows the line starts with, then the values. Read and
    /// checked as the command checks, and nothing written; what it answers invokes <see cref="Set"/> on the revision read.
    /// </summary>
    public LineRead SetLine(LineContext line)
    {
        var (rows, rest) = Split(line);
        var plan = PlanSet(line.State, line.Caller, null, rows, rest, line.View);
        return Read(line, plan, Rows(rows, plan), new("set", rest));
    }

    /// <summary><c>reset</c> as a line types it: the rows, then the field where one is named.</summary>
    public LineRead ResetLine(LineContext line)
    {
        var (rows, rest) = Split(line);
        var plan = PlanReset(line.State, null, rows, rest.Length == 0 ? null : rest, line.View);
        // With no word at all, the field is the screen's too: the cell the keyboard is on.
        return Read(line, plan, Rows(rows, plan), plan.Context is { } cell ? new("field", TableWords.Word(cell), ArgumentSource.Context) : new("field", rest));
    }

    /// <summary><c>remove</c> as a line types it: the rows, and no other word.</summary>
    public LineRead RemoveLine(LineContext line)
    {
        var rows = SelectorReader.Words(line.Text);
        return Read(line, PlanRemove(line.State, null, rows, line.View), new LineArgument("rows", rows));
    }

    /// <summary>What could follow the last word of a line of <c>set</c>, <c>reset</c> or <c>remove</c>: the verb says what comes after its rows.</summary>
    public LineCompletions Complete(LineContext line, CommandArgumentKind? afterRows) =>
        TableCompletions.Of(line.Text, register.Of(line.State), line.State, afterRows, line.View);

    // A view is what a line was given (note 16 of step 2): a command invoked has none, and says its rows without one.
    Plan PlanSet(WorkspaceState state, Caller caller, string? table, string? rows, string set, LineView? view = null)
    {
        var (tables, selector, named) = ReadRows(state, table, rows);
        // A table of one row is addressed without a row where the first field's words say which (note 7).
        var alone = selector.Table is null && selector.SaysNoRows ? tables.Where(t => t.Descriptor.SingleRow && Assignments.StartsWithAFieldOf(set, t)).ToList() : [];
        if (alone.Count > 1)
            throw new TableRefused("AmbiguousTable", $"{set.Trim()} could be set on {TableWords.Either(alone.Select(t => t.Word))}; say which, as in {alone[0].Word} {set.Trim()}.",
                candidates: [.. alone.Select(t => t.Word)]);
        // With a screen behind the line, a field of its table acts on its selection (note 18 of step 2).
        var onScreen = OnScreen(selector, tables, view, t => Assignments.StartsWithAFieldOf(set, t) ? Assignments.Leading(set, t) : null, alone, set.Trim());
        var resolved = onScreen is not null ? Selectors.LeftOut(view!, tables, state)
            : Selectors.Resolve(alone.Count == 1 ? new RowSelector(alone[0].Descriptor.TableId) : selector, tables, state, view);
        var bound = resolved.Table;
        var keyed = Keyed(selector.Screen is not null || onScreen is not null, bound, tables);
        var subject = resolved.Rows.Count > 0 ? resolved.Rows[0].Key : bound.Word;
        var values = Assignments.Read(set, bound).Select(a =>
        {
            if (!a.Field.Editable) throw NotEditable(bound, a.Field);
            var reading = FieldForms.Read(a.Field.Spec, a.Text);
            return reading.Problem == FieldForms.Problem.None ? (a.Field, reading.Value)
                : throw new TableRefused("InvalidValue", $"{subject} {TableWords.Word(a.Field)} is {reading.What}; {a.Text} is not.", a.Text);
        }).ToList();

        var changes = new List<WorkspaceChange>();
        var changed = new HashSet<string>(StringComparer.Ordinal);
        var written = new List<(TableField Field, JsonNode? Value)>();
        var would = new List<string>();
        var whole = resolved.Whole?.ToDictionary(s => s.Name);
        // Several values are written in the order given, each checked against the rows as the ones before it left them
        // (note 9): what two commands would take, one takes, and what the second would refuse refuses both.
        var (standing, rowsNow, read) = (state, resolved.Rows, 0);
        foreach (var (field, value) in values)
        {
            if (read < changes.Count)
            {
                standing = workspaces.After(standing, caller.UserId, changes.GetRange(read, changes.Count - read), caller.Origin);
                var now = bound.Table.Rows(standing).ToDictionary(r => r.Key, StringComparer.Ordinal);
                rowsNow = [.. resolved.Rows.Select(r => now.GetValueOrDefault(r.Key)).OfType<ITableRow>()];
                read = changes.Count;
            }
            var differing = rowsNow.Where(r => !FieldForms.Same(r.Value(field.Name), value)).ToList();
            would.Add(WouldSet(field, value, rowsNow.Count, differing));
            if (differing.Count == 0) continue;
            written.Add((field, value));
            foreach (var group in differing.GroupBy(r => r.Subset))
            {
                // One edit for the whole subset where the field has one (ADR-0016 §2); it wins over each row's earlier edit.
                var all = group.Key is not null && whole is not null && whole.TryGetValue(group.Key, out var subset) ? bound.Table.SetAll(standing, subset, field, value) : null;
                changes.AddRange(all ?? group.SelectMany(r => bound.Table.Set(standing, r, field, value)));
            }
            changed.UnionWith(differing.Select(r => r.Key));
        }

        // One value is named as the line first spells it, `<field> <value>`; several keep their signs.
        var words = Words("set", table, named, set.Count(c => c == '=') == 1 ? set.Replace('=', ' ') : set);
        var touched = resolved.Rows.Where(r => changed.Contains(r.Key)).ToList();
        var held = resolved.Rows.Count - touched.Count;
        if (touched.Count == 0)
        {
            var holds = TableWords.And(values.Select(v => $"{TableWords.Word(v.Field)} {FieldForms.Write(v.Field.Spec, v.Value)}"));
            return Unchanged(state, resolved, $"{Rows(bound, resolved.Rows, all: true)} already hold{(resolved.Rows.Count == 1 ? "s" : "")} {holds}", words, keyed);
        }
        var assigned = TableWords.And(written.Select(v => $"{TableWords.Word(v.Field)} to {FieldForms.Write(v.Field.Spec, v.Value)}"));
        var already = held == 0 ? "" : $"; {held} already held {(written.Count == 1 ? "it" : "them")}";
        var said = touched.Count == 1 ? $"Set {touched[0].Key} {assigned}{already}." : $"Set {assigned} on {Rows(bound, touched)}{already}.";
        return new(state, resolved, changes, touched, said, string.Join("; ", would), words, keyed);
    }

    Plan PlanReset(WorkspaceState state, string? table, string? rows, string? field, LineView? view = null)
    {
        var (tables, selector, said) = ReadRows(state, table, rows);
        var alone = field is not null && selector.Table is null && selector.SaysNoRows ? tables.Where(t => t.Descriptor.SingleRow && Assignments.Fields(t, field).Count > 0).ToList() : [];
        if (alone.Count > 1)
            throw new TableRefused("AmbiguousTable", $"{field!.Trim()} could be reset on {TableWords.Either(alone.Select(t => t.Word))}; say which, as in {alone[0].Word}.",
                candidates: [.. alone.Select(t => t.Word)]);
        var onScreen = OnScreen(selector, tables, view, t => field is null ? [] : Assignments.Fields(t, field) is { Count: > 0 } fields ? fields : null, alone, field?.Trim() ?? "");
        // With no word at all, the cell the keyboard is on (note 18 of step 2): its row and its field, and no other.
        var cell = onScreen is not null && field is null ? Cell(view!, onScreen) : null;
        var resolved = cell is not null ? Selectors.Focused(view!, tables, state) ?? throw new InvalidOperationException("A focused cell has a row.")
            : onScreen is not null ? Selectors.LeftOut(view!, tables, state)
            : Selectors.Resolve(alone.Count == 1 ? new RowSelector(alone[0].Descriptor.TableId) : selector, tables, state, view);
        var bound = resolved.Table;
        var keyed = Keyed(selector.Screen is not null || onScreen is not null, bound, tables);
        var named = cell ?? (field is null ? null : TableRegister.Field(bound, field));
        var of = named is null ? "" : $" of {TableWords.Word(named)}";

        var changes = new List<WorkspaceChange>();
        var changed = new HashSet<ITableRow>(ReferenceEqualityComparer.Instance);
        foreach (var subset in resolved.Whole ?? [])
        {
            var whole = bound.Table.ResetAll(state, subset, named);
            if (whole.Count == 0) continue;
            changes.AddRange(whole);
            changed.UnionWith(resolved.Rows.Where(r => r.Subset == subset.Name));
        }
        foreach (var row in resolved.Rows)
        {
            var own = bound.Table.Reset(state, row, named);
            if (own.Count == 0) continue;
            changes.AddRange(own);
            changed.Add(row);
        }

        var words = Words("reset", table, said, field);
        var touched = resolved.Rows.Where(changed.Contains).ToList();
        var held = resolved.Rows.Count - touched.Count;
        if (touched.Count == 0)
            return Unchanged(state, resolved, $"{Rows(bound, resolved.Rows, all: true)} {(resolved.Rows.Count == 1 ? "has" : "have")} no edit{of}", words, keyed) with { Context = cell };
        var without = held == 0 ? "" : $"; {held} had no edit";
        var done = touched.Count == 1 ? $"Reset {touched[0].Key}{(named is null ? "" : $" {TableWords.Word(named)}")}{without}."
            : named is null ? $"Reset {Rows(bound, touched)}{without}." : $"Reset {TableWords.Word(named)} on {Rows(bound, touched)}{without}.";
        var would = resolved.Rows.Count == 1 ? "reset" : $"{Counted(touched.Count)} reset{(held == 0 ? "" : $", {Counted(held)} with no edit")}";
        return new(state, resolved, changes, touched, done, $"{(named is null ? "every field" : TableWords.Word(named))}: {would}", words, keyed, cell);
    }

    Plan PlanRemove(WorkspaceState state, string? table, string? rows, LineView? view = null)
    {
        var (tables, selector, said) = ReadRows(state, table, rows);
        var resolved = Selectors.Resolve(selector, tables, state, view);
        var keyed = Keyed(selector.Screen is not null, resolved.Table, tables);
        var words = Words("remove", table, said, null);
        if (resolved.Rows.Count == 0) return Unchanged(state, resolved, "", words, keyed);
        var changes = resolved.Rows.SelectMany(r => resolved.Table.Table.Remove(state, r)).ToList();
        var would = resolved.Rows.Count == 1 ? "removed, with its edits" : $"{Counted(resolved.Rows.Count)} removed, with their edits";
        return new(state, resolved, changes, resolved.Rows, $"Removed {Rows(resolved.Table, resolved.Rows)}.", would, words, keyed);
    }

    // The table as a request names it beside rows read from a screen: by its word, which the log then reads as a line
    // does, or by its id where another table answers to that word.
    static string? Keyed(bool fromScreen, BoundTable table, IReadOnlyList<BoundTable> tables) => !fromScreen ? null
        : tables.Count(t => TableWords.Names(t.Descriptor).Contains(TableWords.Of(table.Word))) == 1 ? table.Word : table.Descriptor.TableId;

    // The table on the screen behind a line that says no rows, where the line's first words name a field of it (note 18
    // of step 2); none with no screen, and for a table of one row, which needs no row. Words that name a field of a table
    // of one row too are refused with the two spellings that say which.
    static BoundTable? OnScreen(RowSelector selector, IReadOnlyList<BoundTable> tables, LineView? view, Func<BoundTable, IReadOnlyList<TableField>?> named,
        IReadOnlyList<BoundTable> alone, string said)
    {
        if (view is null || selector.Table is not null || !selector.SaysNoRows) return null;
        var table = Views.TableOf(view, tables);
        if (table.Descriptor.SingleRow || named(table) is not { } there) return null;
        if (alone.Count == 0 || there.Count == 0) return table;
        var other = named(alone[0]) ?? throw new InvalidOperationException($"{alone[0].Word} was read as having the field.");
        throw new TableRefused("AmbiguousField",
            $"{Assignments.Name(said)} could be {TableWords.Word(other[0])} of {alone[0].Word} or {TableWords.Word(there[0])} of the {table.Word} on screen; "
            + $"say which, as in {alone[0].Word} {said} or {SelectorReader.SelectedWord} {said}.",
            Assignments.Name(said), [$"{alone[0].Word} {said}", $"{SelectorReader.SelectedWord} {said}"]);
    }

    // The field of the cell the keyboard is on, for a line that names none.
    static TableField Cell(LineView view, BoundTable table) =>
        view.Focus is null ? throw new TableRefused("NoFocus", $"reset alone resets the cell the keyboard is on, and it is on no cell of {table.Word}: name the rows, or a field.")
        : Views.FocusedField(view, table) ?? throw new TableRefused("NoFocus", $"reset alone resets the cell the keyboard is on, and that cell is no field of {table.Word}: name the rows, or a field.");

    // The rows as a line's argument: its own words, or, where it left them out and a screen gave them, the rows by
    // their keys as a log names them. A table of one row has no rows to say.
    static LineArgument Rows(string? typed, Plan plan) => typed is not null || plan.Keyed is null ? new("rows", typed ?? "")
        : new("rows", plan.Resolved.Rows.Count <= Named ? string.Join(", ", plan.Resolved.Rows.Select(r => r.Key))
            : $"{string.Join(", ", plan.Resolved.Rows.Take(Named).Select(r => r.Key))} and {Counted(plan.Resolved.Rows.Count - Named)} more", ArgumentSource.Context);

    // The rows as the caller said them, read in the tables the workspace has, and as a log names them: in the caller's
    // words, a list of many ids by its first few and how many more there are (note 17 of step 2).
    (IReadOnlyList<BoundTable> Tables, RowSelector Selector, string Said) ReadRows(WorkspaceState state, string? table, string? rows)
    {
        var tables = register.Of(state);
        // The table said beside the rows is the one they are read in: its subsets, and no row where it has one.
        var named = table is null ? null : TableRegister.Find(tables, table);
        var words = (rows ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var scan = SelectorReader.Scan(words, [.. tables.Select(t => t.Descriptor)], named?.Descriptor);
        if (scan.Short is { } unread) throw unread;
        if (scan.Taken < words.Length)
            throw new TableRefused("UnreadRows",
                $"{string.Join(' ', words[scan.Taken..])} is left over after the rows: rows are ids separated by commas, all, a subset, or where and its conditions.", words[scan.Taken]);
        var ids = scan.Selector.Ids;
        var said = ids.Count <= Named ? string.Join(' ', words)
            : string.Join(' ', [.. words[..scan.IdsFrom], $"{string.Join(", ", ids.Take(Named))} and {Counted(ids.Count - Named)} more", .. words[scan.IdsTo..]]);
        if (named is null || table is null) return (tables, scan.Selector, said);
        if (scan.Selector.Table is { } other && other != named.Descriptor.TableId)
            throw new TableRefused("TwoTables", $"The rows are said to be of {TableRegister.Find(tables, other).Word} and the table to be {named.Word}.", table.Trim());
        return (tables, scan.Selector with { Table = named.Descriptor.TableId }, said);
    }

    // The rows a line starts with, in its own words, and the words they leave for the verb.
    (string? Rows, string Left) Split(LineContext line)
    {
        var words = line.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var tables = register.Of(line.State);
        var rest = SelectorReader.Read(line.Text, [.. tables.Select(t => t.Descriptor)], onScreen: line.View is { } view ? Views.TableOf(view, tables).Descriptor : null).Rest;
        return (rest.Count == words.Length ? null : string.Join(' ', words[..^rest.Count]), string.Join(' ', rest));
    }

    // A line's reading: the request that runs it on the revision read, each argument it gave, its rows and what would change.
    static LineRead Read(LineContext line, Plan plan, params LineArgument[] arguments)
    {
        var given = arguments.Where(a => a.Text.Length > 0).ToList();
        var request = new JsonObject { ["workspaceId"] = line.State.WorkspaceId, ["revision"] = line.State.Revision };
        foreach (var argument in given) request[argument.Name] = argument.Text;
        var (bound, rows) = (plan.Resolved.Table, plan.Resolved.Rows);
        // Rows read from a screen run as rows by key in their table (note 17 of step 2): no command takes a screen's word.
        if (plan.Keyed is { } table) (request["table"], request["rows"]) = (table, string.Join(", ", rows.Select(r => r.Key)));
        var title = bound.Descriptor.SingleRow ? bound.Word : $"{bound.Word}, {Counted(rows.Count)} row{(rows.Count == 1 ? "" : "s")}";
        // The one row of a table of one row has no id a line could name: its word says it, and none is listed.
        IReadOnlyList<string> first = bound.Descriptor.SingleRow ? [] : [.. rows.Take(Listed).Select(r => r.Key)];
        return new(new JsonObject { ["request"] = request }, given, new(bound.Descriptor.TableId, title, rows.Count, first), plan.Would);
    }

    // One accepted call is one command (ADR-0016 §11), named by the words it was given; the batch is all or none, and a
    // workspace that moved since the rows were read refuses it. A command that would change no row writes nothing,
    // keeps the revision and is done (note 9).
    TableResult Write(Caller caller, Plan plan)
    {
        var (state, resolved) = (plan.State, plan.Resolved);
        var held = resolved.Rows.Count - plan.Touched.Count;
        if (plan.Touched.Count == 0)
            return new(state, null, plan.Said, resolved.Table.Descriptor.TableId, resolved.Rows.Count, 0, held, [.. resolved.Rows.Take(Listed).Select(r => r.Key)]);
        var next = workspaces.Batch(state.WorkspaceId, state.Revision, caller.UserId, plan.Changes, caller.Origin, plan.Words);
        var command = next.Log[^1].Command?.Id ?? throw new InvalidOperationException("An accepted command has an id.");
        return new(next, command, plan.Said, resolved.Table.Descriptor.TableId, resolved.Rows.Count, plan.Touched.Count, held, [.. plan.Touched.Take(Listed).Select(r => r.Key)]);
    }

    static Plan Unchanged(WorkspaceState state, ResolvedRows resolved, string why, string words, string? keyed)
    {
        var what = resolved.Rows.Count > 0 ? why : $"no row of {resolved.Table.Word} matches{(resolved.Where.Count == 0 ? "" : $" {string.Join(" and ", resolved.Where)}")}";
        return new(state, resolved, [], [], $"Nothing changed: {what}.", $"Nothing would change: {what}.", words, keyed);
    }

    // One value's part of what a set would change (note 15): how many rows take it, from what where they all hold one
    // value, and how many hold it already. One row needs no count.
    static string WouldSet(TableField field, JsonNode? value, int rows, IReadOnlyList<ITableRow> differing)
    {
        var to = FieldForms.Write(field.Spec, value);
        var from = differing.Select(r => FieldForms.Write(field.Spec, r.Value(field.Name))).Distinct().ToList();
        var change = from.Count == 1 ? $"{from[0]} to {to}" : $"to {to}";
        var already = rows - differing.Count;
        IEnumerable<string> parts = rows == 1 ? [differing.Count == 1 ? change : $"already {to}"]
            : new[] { differing.Count > 0 ? $"{Counted(differing.Count)} {change}" : null, already > 0 ? $"{Counted(already)} already {to}" : null }.OfType<string>();
        return $"{TableWords.Word(field)}: {string.Join(", ", parts)}";
    }

    static TableRefused NotEditable(BoundTable table, TableField field) =>
        new("NotEditable", $"{TableWords.Word(field)} is not a field a command sets; {table.Word} takes {TableWords.And(table.Descriptor.Fields.Where(f => f.Editable).Select(TableWords.Word))}.",
            TableWords.Word(field), [.. table.Descriptor.Fields.Where(f => f.Editable).Select(TableWords.Word)]);

    // A few rows by their keys, more by their number and the table's word.
    static string Rows(BoundTable table, IReadOnlyList<ITableRow> rows, bool all = false) =>
        rows.Count <= Named ? TableWords.And(rows.Select(r => r.Key)) : $"{(all ? "all " : "")}{Counted(rows.Count)} {table.Word}";

    static string Counted(int count) => count.ToString(CultureInfo.InvariantCulture);

    // The command as its caller gave it, in the line's own spelling: what the log names it by (ADR-0016 note 3).
    static string Words(string verb, string? table, string said, string? rest)
    {
        var named = table is null || said.StartsWith(TableWords.Of(table), StringComparison.OrdinalIgnoreCase) ? null : table.Trim();
        return string.Join(' ', new[] { verb, named, said, rest is null ? null : SelectorReader.Words(rest) }.Where(w => !string.IsNullOrEmpty(w)));
    }
}

/// <summary>
/// Reads values against a table's fields (ADR-0016 §3, notes 12 and 15): <c>name=value</c> pairs, or one field and its
/// value with no sign, as the line first spells it. A field is said by its words, which may be several, and a value may be too.
/// </summary>
static class Assignments
{
    const string How = "one value is written after its field (<field> <value>), and several as name=value.";

    /// <summary>Whether the first name in the text is a field of the table, so the text is meant for it; the rest is then read against it, and refused in its words.</summary>
    public static bool StartsWithAFieldOf(string text, BoundTable table) =>
        text.IndexOf('=') is >= 0 and var sign ? sign > 0 && Fields(table, text[..sign]).Count > 0 : Leading(SelectorReader.Words(text).Split(' '), table) is not null;

    public static IReadOnlyList<(TableField Field, string Text)> Read(string text, BoundTable table)
    {
        if (!text.Contains('=')) return [One(text, table)];
        var pairs = new List<(TableField Field, List<string> Value)>();
        var pending = new List<string>();
        foreach (var word in SelectorReader.Words(text.Replace("=", " = ", StringComparison.Ordinal)).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (word != "=")
            {
                pending.Add(word);
                continue;
            }
            // The first name is every word before the first sign. A later one is the longest run of words before its sign
            // that names a field, and what stands before it ends the value before, which holds at least a word:
            // `note=wet wind speed=4` is a note of wet and a wind speed, never a `wet wind speed` after a note with no value.
            int? Named(int start) => Enumerable.Range(start, Math.Max(0, pending.Count - start)).Cast<int?>()
                .FirstOrDefault(i => i is { } at && Fields(table, string.Join(' ', pending.Skip(at))).Count > 0);
            if (pending.Count == 0) throw new TableRefused("UnreadValues", $"{text.Trim()} names no field before a value: {How}", "=");
            var from = pairs.Count == 0 ? 0 : Named(1) ?? Named(0) ?? pending.Count - 1;
            var field = TableRegister.Field(table, string.Join(' ', pending.Skip(from)));
            if (pairs.Any(p => p.Field == field)) throw new TableRefused("FieldTwice", $"{TableWords.Word(field)} is given twice; give each field one value.", TableWords.Word(field));
            if (pairs.Count > 0) pairs[^1].Value.AddRange(pending.Take(from));
            pairs.Add((field, []));
            pending.Clear();
        }
        pairs[^1].Value.AddRange(pending);
        if (pairs.FirstOrDefault(p => p.Value.Count == 0) is { Field: { } empty })
            throw new TableRefused("UnreadValues", $"{TableWords.Word(empty)} has no value: {How}", TableWords.Word(empty));
        return [.. pairs.Select(p => (p.Field, string.Join(' ', p.Value)))];
    }

    public static IReadOnlyList<TableField> Fields(BoundTable table, string name)
    {
        var words = TableWords.Of(name);
        return [.. table.Descriptor.Fields.Where(f => TableWords.Names(f).Contains(words))];
    }

    // One value as the line first spells it (ADR-0016 §3, note 15), with no sign: the field's words, the longest run from
    // the start that names one, and then the value, which is every word after them.
    static (TableField Field, string Text) One(string text, BoundTable table)
    {
        var words = SelectorReader.Words(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) throw new TableRefused("UnreadValues", $"A field and its value are missing: {How}");
        // Words that name no field are refused by the first of them, with the fields there are.
        var named = Leading(words, table) ?? 1;
        var name = string.Join(' ', words.Take(named));
        var field = TableRegister.Field(table, name);
        return named < words.Length ? (field, string.Join(' ', words.Skip(named))) : throw new TableRefused("UnreadValues", $"{TableWords.Word(field)} has no value: {How}", name);
    }

    /// <summary>The fields the text's first name could be, where it starts with one: the words before a sign, or the longest run of words that names a field.</summary>
    public static IReadOnlyList<TableField> Leading(string text, BoundTable table)
    {
        if (text.IndexOf('=') is > 0 and var sign) return Fields(table, text[..sign]);
        var words = SelectorReader.Words(text).Split(' ');
        return Leading(words, table) is { } count ? Fields(table, string.Join(' ', words.Take(count))) : [];
    }

    /// <summary>The text's first name as written: the words before a sign, or the first word.</summary>
    public static string Name(string text) => text.IndexOf('=') is > 0 and var sign ? text[..sign].Trim() : SelectorReader.Words(text).Split(' ')[0];

    // How many words from the start name a field, taking the most that do; none where no run does.
    static int? Leading(string[] words, BoundTable table) =>
        Enumerable.Range(1, words.Length).Reverse().Cast<int?>().FirstOrDefault(n => n is { } count && Fields(table, string.Join(' ', words.Take(count))).Count > 0);
}

/// <summary><c>entity.set</c> (ADR-0016 §3): one value or several, as written, into rows of a registered table of the caller's workspace.</summary>
public sealed class EntitySet(Func<TableService> tables) : ICapabilityHandler, ILineCommand
{
    public const string Id = "entity.set";

    public CapabilityDescriptor Descriptor { get; } = new(Id, TableContracts.Version, "Set fields of rows of a table of a workspace",
        [new("request", TableContracts.SetRequest, TableContracts.Version, Cardinality.One)],
        [new("answer", TableContracts.Edited, TableContracts.Version, Cardinality.One)],
        ExecutionKind.RequestResponse, [CapabilityEffect.EditWorkspace],
        new(["set"], [new("rows", CommandArgumentKind.Rows, Required: false), new("set", CommandArgumentKind.Assignments, Required: true)]));

    public LineRead Read(LineContext line) => tables().SetLine(line);

    public LineCompletions Complete(LineContext line) => tables().Complete(line, CommandArgumentKind.Assignments);

    public Task<IReadOnlyDictionary<string, JsonNode?>> InvokeAsync(CapabilityInvocation invocation, CancellationToken cancellationToken) =>
        TableContracts.Answer(invocation, (request, caller) => tables().Set(TableContracts.Text(request, "workspaceId"), request["revision"]?.GetValue<long>(), caller,
            request["table"]?.GetValue<string>(), request["rows"]?.GetValue<string>(), TableContracts.Text(request, "set")));
}

/// <summary><c>entity.reset</c> (ADR-0016 §3): removes the edits on rows of a registered table, of one field or of every field.</summary>
public sealed class EntityReset(Func<TableService> tables) : ICapabilityHandler, ILineCommand
{
    public const string Id = "entity.reset";

    public CapabilityDescriptor Descriptor { get; } = new(Id, TableContracts.Version, "Reset fields of rows of a table of a workspace to what they follow",
        [new("request", TableContracts.ResetRequest, TableContracts.Version, Cardinality.One)],
        [new("answer", TableContracts.Edited, TableContracts.Version, Cardinality.One)],
        ExecutionKind.RequestResponse, [CapabilityEffect.EditWorkspace],
        new(["reset"], [new("rows", CommandArgumentKind.Rows, Required: false), new("field", CommandArgumentKind.Field, Required: false)]));

    public LineRead Read(LineContext line) => tables().ResetLine(line);

    public LineCompletions Complete(LineContext line) => tables().Complete(line, CommandArgumentKind.Field);

    public Task<IReadOnlyDictionary<string, JsonNode?>> InvokeAsync(CapabilityInvocation invocation, CancellationToken cancellationToken) =>
        TableContracts.Answer(invocation, (request, caller) => tables().Reset(TableContracts.Text(request, "workspaceId"), request["revision"]?.GetValue<long>(), caller,
            request["table"]?.GetValue<string>(), request["rows"]?.GetValue<string>(), request["field"]?.GetValue<string>()));
}

/// <summary><c>entity.remove</c> (ADR-0016 §3): removes rows the workspace made from a registered table.</summary>
public sealed class EntityRemove(Func<TableService> tables) : ICapabilityHandler, ILineCommand
{
    public const string Id = "entity.remove";

    public CapabilityDescriptor Descriptor { get; } = new(Id, TableContracts.Version, "Remove rows a workspace made from a table",
        [new("request", TableContracts.RemoveRequest, TableContracts.Version, Cardinality.One)],
        [new("answer", TableContracts.Edited, TableContracts.Version, Cardinality.One)],
        ExecutionKind.RequestResponse, [CapabilityEffect.EditWorkspace],
        new(["remove"], [new("rows", CommandArgumentKind.Rows, Required: true)]));

    public LineRead Read(LineContext line) => tables().RemoveLine(line);

    public LineCompletions Complete(LineContext line) => tables().Complete(line, null);

    public Task<IReadOnlyDictionary<string, JsonNode?>> InvokeAsync(CapabilityInvocation invocation, CancellationToken cancellationToken) =>
        TableContracts.Answer(invocation, (request, caller) => tables().Remove(TableContracts.Text(request, "workspaceId"), request["revision"]?.GetValue<long>(), caller,
            request["table"]?.GetValue<string>(), request["rows"]?.GetValue<string>()));
}

/// <summary>The edit verbs' contracts, flat as every contract is (ADR-0016 note 12), and how a verb's result and refusal are answered.</summary>
public static class TableContracts
{
    public const string Version = "1.0.0";
    public const string SetRequest = "entity.set-request";
    public const string ResetRequest = "entity.reset-request";
    public const string RemoveRequest = "entity.remove-request";
    public const string Edited = "entity.rows-edited";

    internal static string Text(JsonNode request, string name) =>
        request[name]?.GetValue<string>() ?? throw new CapabilityFailure("InvalidInput", $"The request has no {name}.");

    internal static Task<IReadOnlyDictionary<string, JsonNode?>> Answer(CapabilityInvocation invocation, Func<JsonNode, Caller, TableResult> act)
    {
        var caller = invocation.Caller ?? throw new CapabilityFailure("NoCaller", "A command is invoked for a caller, through the command gateway.");
        var request = invocation.Inputs["request"] ?? throw new CapabilityFailure("InvalidInput", "The command takes a request.");
        try
        {
            var result = act(request, caller);
            var answer = new JsonObject
            {
                ["workspaceId"] = result.State.WorkspaceId,
                ["revision"] = result.State.Revision,
                ["said"] = result.Said,
                ["table"] = result.Table,
                ["matched"] = result.Matched,
                ["changed"] = result.Changed,
                ["held"] = result.Held,
                // Flat, as every contract is: the first rows to look at, by their keys.
                ["rows"] = string.Join(", ", result.Rows),
            };
            // The command to name to take this back; none where nothing was written.
            if (result.Command is { } command) answer["command"] = command;
            return Task.FromResult<IReadOnlyDictionary<string, JsonNode?>>(new Dictionary<string, JsonNode?> { ["answer"] = answer });
        }
        catch (TableRefused refused)
        {
            // The word at fault and what it could have been go with the sentence (ADR-0016 §6), for a caller that points at it.
            throw new CapabilityFailure(refused.Code, refused.Message,
                [new(refused.Code, "", refused.Message) { Word = refused.Word, Candidates = refused.Candidates.Count > 0 ? refused.Candidates : null }]);
        }
        catch (WorkspaceRefused refused)
        {
            throw new CapabilityFailure(refused.Code, refused.Message);
        }
    }

    internal static JsonObject Request(string rowsTitle, params (string Name, JsonObject Schema, bool Required)[] more)
    {
        var properties = new JsonObject
        {
            ["workspaceId"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["title"] = "Workspace" },
            ["revision"] = new JsonObject { ["type"] = "integer", ["title"] = "Revision the workspace was read at; the one it holds when left out" },
            ["table"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["title"] = "Table, by its id or its words; left out where the rows say it" },
            ["rows"] = new JsonObject { ["type"] = "string", ["title"] = rowsTitle },
        };
        foreach (var (name, schema, _) in more) properties[name] = schema;
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray([.. new[] { "workspaceId" }.Concat(more.Where(m => m.Required).Select(m => m.Name)).Select(n => (JsonNode)n)]),
            ["additionalProperties"] = false,
        };
    }

    internal const string RowsTitle = "Rows as the command line spells them: ids separated by commas, all, a subset, or where and conditions joined by and (where <field>=<value> and <field>><value>); left out for a table of one row";
}

/// <summary>The kernel's edit verbs over registered tables, and their contracts. Knows no table.</summary>
public sealed class TablesModule(Func<TableService> tables) : IQubeModule
{
    public void Register(CompositionRegistryBuilder builder)
    {
        builder.AddCapability(new EntitySet(tables));
        builder.AddCapability(new EntityReset(tables));
        builder.AddCapability(new EntityRemove(tables));
        builder.AddContract(new ContractDefinition(TableContracts.SetRequest, TableContracts.Version, "Set fields of rows", TableContracts.Request(TableContracts.RowsTitle,
            ("set", new JsonObject { ["type"] = "string", ["minLength"] = 1, ["title"] = "Values as name=value, one or several (<field>=<value> <field>=<value>), each written as its field is typed and checked in the order given; one value may follow its field with no sign (<field> <value>)" }, true))));
        builder.AddContract(new ContractDefinition(TableContracts.ResetRequest, TableContracts.Version, "Reset fields of rows", TableContracts.Request(TableContracts.RowsTitle,
            ("field", new JsonObject { ["type"] = "string", ["minLength"] = 1, ["title"] = "The field to reset, by its words; every field of the rows where left out" }, false))));
        builder.AddContract(new ContractDefinition(TableContracts.RemoveRequest, TableContracts.Version, "Remove rows", TableContracts.Request(TableContracts.RowsTitle)));
        builder.AddContract(new ContractDefinition(TableContracts.Edited, TableContracts.Version, "Rows edited", new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["workspaceId"] = new JsonObject { ["type"] = "string", ["title"] = "Workspace" },
                ["revision"] = new JsonObject { ["type"] = "integer", ["title"] = "Revision" },
                ["command"] = new JsonObject { ["type"] = "string", ["title"] = "This command's id, to take it back by; absent where nothing changed" },
                ["said"] = new JsonObject { ["type"] = "string", ["title"] = "What was done, in words" },
                ["table"] = new JsonObject { ["type"] = "string", ["title"] = "Table" },
                ["matched"] = new JsonObject { ["type"] = "integer", ["title"] = "Rows the selector matched" },
                ["changed"] = new JsonObject { ["type"] = "integer", ["title"] = "Rows changed" },
                ["held"] = new JsonObject { ["type"] = "integer", ["title"] = "Rows that already held the value" },
                ["rows"] = new JsonObject { ["type"] = "string", ["title"] = "The first rows to look at, by their keys" },
            },
            ["required"] = new JsonArray("workspaceId", "revision", "said", "table", "matched", "changed", "held", "rows"),
            ["additionalProperties"] = false,
        }));
    }
}
