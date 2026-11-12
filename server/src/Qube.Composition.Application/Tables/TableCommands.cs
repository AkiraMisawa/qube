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
public sealed partial class TableService(TableRegister register, WorkspaceService workspaces)
{
    const int Named = 3;
    const int Listed = 10;

    // What a verb would do with a workspace as it stands: read and checked as the command checks, and not written. No
    // row touched is a command that changes nothing.
    /// <param name="Keyed">Where the rows were read from a screen: how a request names them (note 17 of step 2).</param>
    /// <param name="Context">What the line left out and the screen behind it gave (note 18 of step 2): its rows, and for <c>reset</c> alone the field too.</param>
    /// <param name="TableFromScreen">Where the line named no table and the screen gave one that is not the main table: that table, as a request names it (note 23 of step 2).</param>
    sealed record Plan(WorkspaceState State, ResolvedRows Resolved, IReadOnlyList<WorkspaceChange> Changes, IReadOnlyList<ITableRow> Touched, string Said, string Would, string Words,
        ScreenKeys? Keyed = null, TableField? Context = null, string? TableFromScreen = null);

    /// <summary>
    /// Rows read from a screen as a request names them (note 17 of step 2): their table, and their keys as a line lists
    /// ids. A table of one row has no row to say: its word is its row.
    /// </summary>
    sealed record ScreenKeys(string Table, string? Rows);

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
        var (tables, selector, named, fromScreen) = ReadRows(state, table, rows, view);
        // After the rows, `as` says a template of their table and its terms, where a field and its value would be (note 50).
        var template = Templates.Leads(set);
        bool Meant(BoundTable t) => template ? Templates.Names(set, t) : Assignments.StartsWithAFieldOf(set, t);
        // A table of one row is addressed without a row where the first field's words say which (note 7).
        var alone = selector.Table is null && selector.SaysNoRows ? tables.Where(t => t.Descriptor.SingleRow && Meant(t)).ToList() : [];
        if (alone.Count > 1)
            throw new TableRefused("AmbiguousTable", $"{set.Trim()} could be set on {TableWords.Either(alone.Select(t => t.Word))}; say which, as in {alone[0].Word} {set.Trim()}.",
                candidates: [.. alone.Select(t => t.Word)]);
        // With a screen behind the line, a field of its table acts on its selection (note 18 of step 2).
        var implied = Implied(selector, tables, view, t => !Meant(t) ? null : template ? [] : Assignments.Leading(set, t), alone, set.Trim());
        var resolved = implied is not null ? Selectors.LeftOut(view!, tables, state)
            : Selectors.Resolve(alone.Count == 1 ? new RowSelector(alone[0].Descriptor.TableId) : selector, tables, state, view);
        var bound = resolved.Table;
        var keyed = selector.Screen is null && implied is null ? null : Keyed(resolved, tables, selector.Screen);
        var subject = resolved.Rows.Count > 0 ? resolved.Rows[0].Key : bound.Word;
        var asTemplate = template ? Templates.Read(set, bound) : null;
        IReadOnlyList<(TableField Field, JsonNode? Value)> values = asTemplate is not null ? Templates.Writes(state, bound, asTemplate) : [.. Assignments.Read(set, bound).Select(a =>
        {
            if (!a.Field.Editable) throw NotEditable(bound, a.Field);
            var reading = FieldForms.Read(a.Field.Written, a.Text);
            return reading.Problem == FieldForms.Problem.None ? (a.Field, reading.Value)
                : throw new TableRefused("InvalidValue", $"{subject} {TableWords.Word(a.Field)} is {reading.What}; {a.Text} is not.", a.Text);
        })];

        var changes = new List<WorkspaceChange>();
        var changed = new HashSet<string>(StringComparer.Ordinal);
        var written = new List<(TableField Field, JsonNode? Value)>();
        var would = new List<string>();
        // A template writes many cells: of one row, the line names those that would change and counts the rest (note 50).
        var heldCells = 0;
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
            // What a write of a figure means is its table's (note 47): each row's changes are asked for, and a row
            // with none holds the value, since the figure alone cannot say whether the rows under it would change.
            var figure = field.Figure is null ? null : rowsNow.ToDictionary(r => r.Key, r => bound.Table.Set(standing, r, field, value), StringComparer.Ordinal);
            var differing = rowsNow.Where(r => figure is null ? !FieldForms.Same(r.Value(field.Name), value) : figure[r.Key].Count > 0).ToList();
            if (asTemplate is not null && rowsNow.Count == 1 && differing.Count == 0) heldCells++;
            else would.Add(figure is null ? WouldSet(field, value, rowsNow.Count, differing)
                : WouldSetFigure(field, value, rowsNow.Count, differing, differing is [var one] && rowsNow.Count == 1 ? bound.Table.SetSays(standing, one, field, value) : null));
            if (differing.Count == 0) continue;
            written.Add((field, value));
            if (figure is not null) changes.AddRange(differing.SelectMany(r => figure[r.Key]));
            else foreach (var group in differing.GroupBy(r => r.Subset))
            {
                // One edit for the whole subset where the field has one (ADR-0016 §2); it wins over each row's earlier edit.
                var all = group.Key is not null && whole is not null && whole.TryGetValue(group.Key, out var subset) ? bound.Table.SetAll(standing, subset, field, value) : null;
                changes.AddRange(all ?? group.SelectMany(r => bound.Table.Set(standing, r, field, value)));
            }
            changed.UnionWith(differing.Select(r => r.Key));
        }

        // One value is named as the line first spells it, `<field> <value>`; several keep their signs; a template is
        // named with its terms as read, each by its name.
        var words = Words("set", table, named, asTemplate is not null ? $"{Templates.Word} {asTemplate.Says}" : set.Count(c => c == '=') == 1 ? set.Replace('=', ' ') : set);
        var touched = resolved.Rows.Where(r => changed.Contains(r.Key)).ToList();
        var held = resolved.Rows.Count - touched.Count;
        if (touched.Count == 0)
        {
            var holds = asTemplate is not null ? $"every value of {asTemplate.Says}"
                : TableWords.And(values.Select(v => $"{TableWords.Word(v.Field)} {FieldForms.Write(v.Field.Written, v.Value)}"));
            return Unchanged(state, resolved, $"{Rows(bound, resolved.Rows, all: true)} already hold{(resolved.Rows.Count == 1 ? "s" : "")} {holds}", words, keyed) with { TableFromScreen = fromScreen };
        }
        if (asTemplate is not null)
        {
            var rest = heldCells == 0 ? "" : $"; {TableWords.Counted(heldCells, "value")} already held";
            return new(state, resolved, changes, touched, $"Set {Rows(bound, touched)} as {asTemplate.Says}{(held == 0 ? "" : $"; {held} already held its values")}.",
                $"{string.Join("; ", would)}{rest}", words, keyed, TableFromScreen: fromScreen);
        }
        var assigned = TableWords.And(written.Select(v => $"{TableWords.Word(v.Field)} to {FieldForms.Write(v.Field.Written, v.Value)}"));
        var already = held == 0 ? "" : $"; {held} already held {(written.Count == 1 ? "it" : "them")}";
        var said = touched.Count == 1 ? $"Set {touched[0].Key} {assigned}{already}." : $"Set {assigned} on {Rows(bound, touched)}{already}.";
        return new(state, resolved, changes, touched, said, string.Join("; ", would), words, keyed, TableFromScreen: fromScreen);
    }

    Plan PlanReset(WorkspaceState state, string? table, string? rows, string? field, LineView? view = null)
    {
        var (tables, selector, said, fromScreen) = ReadRows(state, table, rows, view);
        var alone = field is not null && selector.Table is null && selector.SaysNoRows ? tables.Where(t => t.Descriptor.SingleRow && Assignments.Fields(t, field).Count > 0).ToList() : [];
        if (alone.Count > 1)
            throw new TableRefused("AmbiguousTable", $"{field!.Trim()} could be reset on {TableWords.Either(alone.Select(t => t.Word))}; say which, as in {alone[0].Word}.",
                candidates: [.. alone.Select(t => t.Word)]);
        var implied = Implied(selector, tables, view, t => field is null ? [] : Assignments.Fields(t, field) is { Count: > 0 } fields ? fields : null, alone, field?.Trim() ?? "");
        // With no word at all, the cell the keyboard is on (note 18 of step 2): its row and its field, and no other.
        var cell = implied is not null && field is null ? Cell(view!, implied) : null;
        var resolved = cell is not null ? Selectors.Focused(view!, tables, state) ?? throw new InvalidOperationException("A focused cell has a row.")
            : implied is not null ? Selectors.LeftOut(view!, tables, state)
            : Selectors.Resolve(alone.Count == 1 ? new RowSelector(alone[0].Descriptor.TableId) : selector, tables, state, view);
        var bound = resolved.Table;
        var keyed = selector.Screen is null && implied is null ? null : Keyed(resolved, tables, selector.Screen);
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
            return Unchanged(state, resolved, $"{Rows(bound, resolved.Rows, all: true)} {(resolved.Rows.Count == 1 ? "has" : "have")} no edit{of}", words, keyed) with { Context = cell, TableFromScreen = fromScreen };
        var without = held == 0 ? "" : $"; {held} had no edit";
        var done = touched.Count == 1 ? $"Reset {touched[0].Key}{(named is null ? "" : $" {TableWords.Word(named)}")}{without}."
            : named is null ? $"Reset {Rows(bound, touched)}{without}." : $"Reset {TableWords.Word(named)} on {Rows(bound, touched)}{without}.";
        var would = resolved.Rows.Count == 1 ? "reset" : $"{Counted(touched.Count)} reset{(held == 0 ? "" : $", {Counted(held)} with no edit")}";
        return new(state, resolved, changes, touched, done, $"{(named is null ? "every field" : TableWords.Word(named))}: {would}", words, keyed, cell, fromScreen);
    }

    Plan PlanRemove(WorkspaceState state, string? table, string? rows, LineView? view = null)
    {
        var (tables, selector, said, fromScreen) = ReadRows(state, table, rows, view);
        var resolved = Selectors.Resolve(selector, tables, state, view);
        var keyed = selector.Screen is null ? null : Keyed(resolved, tables, selector.Screen);
        var words = Words("remove", table, said, null);
        if (resolved.Rows.Count == 0) return Unchanged(state, resolved, "", words, keyed) with { TableFromScreen = fromScreen };
        var changes = resolved.Rows.SelectMany(r => resolved.Table.Table.Remove(state, r)).ToList();
        var would = resolved.Rows.Count == 1 ? "removed, with its edits" : $"{Counted(resolved.Rows.Count)} removed, with their edits";
        return new(state, resolved, changes, resolved.Rows, $"Removed {Rows(resolved.Table, resolved.Rows)}.", would, words, keyed, TableFromScreen: fromScreen);
    }

    // Rows read from a screen run by their keys in their table (note 17 of step 2). The table is named by its word,
    // which the log then reads as a line does, or by its id where another table answers to that word. The keys are
    // written as a line lists ids and read here as the command will read them at Enter: keys it would read as
    // anything but those rows (a first key that is a subset's word, a key of two words) are refused now, with the word
    // that said them where one did, and never run as other rows.
    static ScreenKeys Keyed(ResolvedRows resolved, IReadOnlyList<BoundTable> tables, ScreenRows? said)
    {
        var table = resolved.Table;
        var word = Asked(table, tables);
        if (table.Descriptor.SingleRow) return new(word, null);
        string[] keys = [.. resolved.Rows.Select(r => r.Key)];
        TableDescriptor[] descriptors = [.. tables.Select(t => t.Descriptor)];
        var rows = string.Join(", ", keys);
        if (Reads(rows, keys)) return new(word, rows);
        throw new TableRefused("UnsayableRow",
            $"{keys.FirstOrDefault(k => !Reads(k, [k])) ?? keys[0]} is a row on screen whose id a command would read as something else: say the rows another way, such as with where.",
            said is null ? null : SelectorReader.Word(said.Value));

        bool Reads(string text, IReadOnlyList<string> ids)
        {
            var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            try
            {
                var scan = SelectorReader.Scan(words, descriptors, table.Descriptor);
                return scan.Short is null && scan.Taken == words.Length && scan.Selector is { Table: null, All: false, Subset: null, Screen: null, Where.Count: 0 }
                    && scan.Selector.Ids.SequenceEqual(ids);
            }
            catch (TableRefused)
            {
                return false;
            }
        }
    }

    // A table as a request names it: by its word, which the log then reads as a line does, or by its id where another
    // table answers to that word.
    static string Asked(BoundTable table, IReadOnlyList<BoundTable> tables) =>
        tables.Count(t => TableWords.Names(t.Descriptor).Contains(TableWords.Of(table.Word))) == 1 ? table.Word : table.Descriptor.TableId;

    // The table on the screen behind a line that says no rows, where the line's first words name a field of it (note 18
    // of step 2): the table the line implies; none with no screen, and for a table of one row, which needs no row. Words
    // that name a field of a table of one row too are refused with the two spellings that say which. `named` answers
    // the fields of a table that the line's first words name: an empty list where the line names no field at all
    // (`reset` alone), and null where its words name no field of that table.
    static BoundTable? Implied(RowSelector selector, IReadOnlyList<BoundTable> tables, LineView? view, Func<BoundTable, IReadOnlyList<TableField>?> named,
        IReadOnlyList<BoundTable> alone, string said)
    {
        if (view is null || selector.Table is not null || !selector.SaysNoRows) return null;
        var table = Views.TableOf(view, tables);
        if (table.Descriptor.SingleRow || named(table) is not { } there) return null;
        if (alone.Count == 0 || there.Count == 0) return table;
        // A field of the screen's rows that no command sets is not what the words meant where a table of one row has them.
        if (there.All(f => !f.Editable)) return null;
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
        : new("rows", Few([.. plan.Resolved.Rows.Select(r => r.Key)]), ArgumentSource.Context);

    // A list of ids as a log and a line name it: all of a few, or the first few and how many more there are.
    internal static string Few(IReadOnlyList<string> ids) => ids.Count <= Named ? string.Join(", ", ids) : $"{string.Join(", ", ids.Take(Named))} and {Counted(ids.Count - Named)} more";

    // The rows as the caller said them, read in the tables the workspace has, and as a log names them: in the caller's
    // words, a list of many ids by its first few and how many more there are (note 17 of step 2). Rows said with no
    // table, where a screen is behind the line, are of the table the line's reader gives them (note 23 of step 2): the
    // selector then names it, and so does the request (`TableFromScreen`, as a request names a table) where it is not the main
    // one, so that what runs is rows of that table whoever reads the request.
    (IReadOnlyList<BoundTable> Tables, RowSelector Selector, string Said, string? TableFromScreen) ReadRows(WorkspaceState state, string? table, string? rows, LineView? view)
    {
        var tables = register.Of(state);
        var (selector, said, fromScreen) = RowsSaid(tables, table, rows, view);
        return (tables, selector, said, fromScreen);
    }

    /// <summary>
    /// The rows as said, read in the tables given: what every verb that takes rows reads them with, an edit verb and a
    /// verb of the screen alike (notes 43 and 44 of step 4).
    /// </summary>
    internal static (RowSelector Selector, string Said, string? TableFromScreen) RowsSaid(IReadOnlyList<BoundTable> tables, string? table, string? rows, LineView? view)
    {
        // The table said beside the rows is the one they are read in: its subsets, and no row where it has one.
        var named = table is null ? null : TableRegister.Find(tables, table);
        var words = (rows ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        // With a screen behind the line the rows are read as the line was split: a subset's word may be its table's.
        var scan = SelectorReader.Scan(words, [.. tables.Select(t => t.Descriptor)], named?.Descriptor, view is null ? null : Views.TableOf(view, tables).Descriptor);
        if (scan.Short is { } unread) throw unread;
        if (scan.Taken < words.Length)
            throw new TableRefused("UnreadRows",
                $"{string.Join(' ', words[scan.Taken..])} is left over after the rows: rows are ids separated by commas, all, a subset, or where and its conditions.", words[scan.Taken]);
        var ids = scan.Selector.Ids;
        var said = ids.Count <= Named ? string.Join(' ', words) : string.Join(' ', [.. words[..scan.IdsFrom], Few(ids), .. words[scan.IdsTo..]]);
        if (scan.Unsaid is { } given)
            return (scan.Selector with { Table = given.TableId }, said, given.Main ? null : Asked(TableRegister.Find(tables, given.TableId), tables));
        if (named is null || table is null) return (scan.Selector, said, null);
        if (scan.Selector.Table is { } other && other != named.Descriptor.TableId)
            throw new TableRefused("TwoTables", $"The rows are said to be of {TableRegister.Find(tables, other).Word} and the table to be {named.Word}.", table.Trim());
        return (scan.Selector with { Table = named.Descriptor.TableId }, said, null);
    }

    // The rows a line starts with, in its own words, and the words they leave for the verb.
    (string? Rows, string Left) Split(LineContext line) => Split(line.Text, register.Of(line.State), line.View);

    /// <summary>The rows a text starts with, in its own words, and the words they leave for the verb that reads it.</summary>
    /// <param name="fieldsFollow">The verb reads a list of fields after the rows (<see cref="SelectorReader.Read"/>).</param>
    /// <summary>The rows of a reading: their table, the reading's title for them, how many they are, and the first of them by key.</summary>
    internal static LineRows Titled(BoundTable table, string title, IReadOnlyList<ITableRow> rows) =>
        new(table.Descriptor.TableId, title, rows.Count, [.. rows.Take(Listed).Select(r => r.Key)]);

    /// <summary>
    /// A line whose rows came to none, refused: by its conditions where it had some, and in the words of
    /// <paramref name="otherwise"/> where it had none.
    /// </summary>
    internal static TableRefused NoRowMatches(ResolvedRows resolved, string verb, string otherwise) => new("NoRowMatches",
        resolved.Where.Count > 0 ? $"No row of {resolved.Table.Word} matches {string.Join(" and ", resolved.Where)}: there is nothing to {verb}." : otherwise);

    internal static (string? Rows, string Left) Split(string text, IReadOnlyList<BoundTable> tables, LineView? view, bool fieldsFollow = false)
    {
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var rest = SelectorReader.Read(text, [.. tables.Select(t => t.Descriptor)], onScreen: view is null ? null : Views.TableOf(view, tables).Descriptor, fieldsFollow: fieldsFollow).Rest;
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
        if (plan.Keyed is { } keyed)
        {
            request["table"] = keyed.Table;
            if (keyed.Rows is { } keys) request["rows"] = keys;
            else request.Remove("rows");
        }
        else if (plan.TableFromScreen is { } fromScreen)
        {
            request["table"] = fromScreen;
        }
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

    static Plan Unchanged(WorkspaceState state, ResolvedRows resolved, string why, string words, ScreenKeys? keyed)
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

    // A figure's part of what a set would change (note 47): for one row what its table says the write would do to the
    // rows under it, where it says; else the count, as for any field, with no value it changes from, since what a
    // figure reads is not what is written.
    static string WouldSetFigure(TableField field, JsonNode? value, int rows, IReadOnlyList<ITableRow> differing, string? says)
    {
        var to = FieldForms.Write(field.Written, value);
        var already = rows - differing.Count;
        IEnumerable<string> parts = rows == 1 ? [differing.Count == 1 ? says ?? $"to {to}" : $"already {to}"]
            : new[] { differing.Count > 0 ? $"{Counted(differing.Count)} to {to}" : null, already > 0 ? $"{Counted(already)} already {to}" : null }.OfType<string>();
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
        TableContracts.Answer(invocation, (request, caller) => tables().Set(CommandRequests.Text(request, "workspaceId"), request["revision"]?.GetValue<long>(), caller,
            request["table"]?.GetValue<string>(), request["rows"]?.GetValue<string>(), CommandRequests.Text(request, "set")));
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
        TableContracts.Answer(invocation, (request, caller) => tables().Reset(CommandRequests.Text(request, "workspaceId"), request["revision"]?.GetValue<long>(), caller,
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
        TableContracts.Answer(invocation, (request, caller) => tables().Remove(CommandRequests.Text(request, "workspaceId"), request["revision"]?.GetValue<long>(), caller,
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

    internal static Task<IReadOnlyDictionary<string, JsonNode?>> Answer(CapabilityInvocation invocation, Func<JsonNode, Caller, TableResult> act) => Answered(invocation, (request, caller) =>
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
        return answer;
    });

    /// <summary>
    /// A command of the tables answers its request for its caller, or is refused as the tables refuse: by name, with the
    /// word at fault and what it could have been (ADR-0016 §6), or as the workspace does.
    /// </summary>
    internal static Task<IReadOnlyDictionary<string, JsonNode?>> Answered(CapabilityInvocation invocation, Func<JsonNode, Caller, JsonObject> act)
    {
        var caller = invocation.Caller ?? throw new CapabilityFailure("NoCaller", "A command is invoked for a caller, through the command gateway.");
        var request = invocation.Inputs["request"] ?? throw new CapabilityFailure("InvalidInput", "The command takes a request.");
        try
        {
            return Task.FromResult<IReadOnlyDictionary<string, JsonNode?>>(new Dictionary<string, JsonNode?> { ["answer"] = act(request, caller) });
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

/// <summary>The kernel's edit verbs over registered tables and its readings of them (ADR-0016 §3, §7), with their contracts. Knows no table.</summary>
public sealed class TablesModule(Func<TableService> tables) : IQubeModule
{
    public void Register(CompositionRegistryBuilder builder)
    {
        builder.AddCapability(new EntitySet(tables));
        builder.AddCapability(new EntityReset(tables));
        builder.AddCapability(new EntityRemove(tables));
        builder.AddCapability(new TablesDescribe(tables));
        builder.AddCapability(new TablesRead(tables));
        TableReadContracts.Register(builder);
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
