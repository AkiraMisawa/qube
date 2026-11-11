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

    /// <summary>
    /// Writes one value or several into the rows (note 9): a row that holds a value gets no edit for it and is counted;
    /// rows said as a whole subset, or as all, take the field's whole-set edit where it has one; the rest is one batch.
    /// </summary>
    public TableResult Set(string workspaceId, long? revision, Caller caller, string? table, string? rows, string set)
    {
        var (state, tables, selector) = Open(workspaceId, revision, caller, table, rows);
        // A table of one row is addressed without a row where the first field's words say which (note 7).
        var alone = selector.Table is null && selector.SaysNoRows ? tables.Where(t => t.Descriptor.SingleRow && Assignments.StartsWithAFieldOf(set, t)).ToList() : [];
        if (alone.Count > 1)
            throw new TableRefused("AmbiguousTable", $"{set.Trim()} could be set on {TableWords.Either(alone.Select(t => t.Word))}; say which, as in {alone[0].Word} {set.Trim()}.",
                candidates: [.. alone.Select(t => t.Word)]);
        var resolved = Selectors.Resolve(alone.Count == 1 ? new RowSelector(alone[0].Descriptor.TableId) : selector, tables, state);
        var bound = resolved.Table;
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

        var touched = resolved.Rows.Where(r => changed.Contains(r.Key)).ToList();
        var held = resolved.Rows.Count - touched.Count;
        if (touched.Count == 0)
        {
            var holds = TableWords.And(values.Select(v => $"{TableWords.Word(v.Field)} {FieldForms.Write(v.Field.Spec, v.Value)}"));
            return Unchanged(state, resolved, $"{Rows(bound, resolved.Rows, all: true)} already hold{(resolved.Rows.Count == 1 ? "s" : "")} {holds}");
        }
        var assigned = TableWords.And(written.Select(v => $"{TableWords.Word(v.Field)} to {FieldForms.Write(v.Field.Spec, v.Value)}"));
        var already = held == 0 ? "" : $"; {held} already held {(written.Count == 1 ? "it" : "them")}";
        var said = touched.Count == 1 ? $"Set {touched[0].Key} {assigned}{already}." : $"Set {assigned} on {Rows(bound, touched)}{already}.";
        // One value is named as the line first spells it, `<field> <value>`; several keep their signs.
        return Write(state, caller, resolved, changes, touched, held, said, Words("set", table, rows, set.Count(c => c == '=') == 1 ? set.Replace('=', ' ') : set));
    }

    /// <summary>
    /// Removes the rows' own edits, of one field or of every field where none is named (note 10): the field follows its
    /// source or its rule again. Rows said as a whole subset, or as all, lose the whole-set edit too. A row with no edit
    /// is counted as such.
    /// </summary>
    public TableResult Reset(string workspaceId, long? revision, Caller caller, string? table, string? rows, string? field)
    {
        var (state, tables, selector) = Open(workspaceId, revision, caller, table, rows);
        var alone = field is not null && selector.Table is null && selector.SaysNoRows ? tables.Where(t => t.Descriptor.SingleRow && Assignments.Fields(t, field).Count > 0).ToList() : [];
        if (alone.Count > 1)
            throw new TableRefused("AmbiguousTable", $"{field!.Trim()} could be reset on {TableWords.Either(alone.Select(t => t.Word))}; say which, as in {alone[0].Word}.",
                candidates: [.. alone.Select(t => t.Word)]);
        var resolved = Selectors.Resolve(alone.Count == 1 ? new RowSelector(alone[0].Descriptor.TableId) : selector, tables, state);
        var bound = resolved.Table;
        var named = field is null ? null : TableRegister.Field(bound, field);
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

        var touched = resolved.Rows.Where(changed.Contains).ToList();
        var held = resolved.Rows.Count - touched.Count;
        if (touched.Count == 0) return Unchanged(state, resolved, $"{Rows(bound, resolved.Rows, all: true)} {(resolved.Rows.Count == 1 ? "has" : "have")} no edit{of}");
        var without = held == 0 ? "" : $"; {held} had no edit";
        var said = touched.Count == 1 ? $"Reset {touched[0].Key}{(named is null ? "" : $" {TableWords.Word(named)}")}{without}."
            : named is null ? $"Reset {Rows(bound, touched)}{without}." : $"Reset {TableWords.Word(named)} on {Rows(bound, touched)}{without}.";
        return Write(state, caller, resolved, changes, touched, held, said, Words("reset", table, rows, field));
    }

    /// <summary>Removes rows the workspace made, with their edits (note 10); a row it did not make is refused by the table, by name, and nothing is removed.</summary>
    public TableResult Remove(string workspaceId, long? revision, Caller caller, string? table, string? rows)
    {
        var (state, tables, selector) = Open(workspaceId, revision, caller, table, rows);
        var resolved = Selectors.Resolve(selector, tables, state);
        if (resolved.Rows.Count == 0) return Unchanged(state, resolved, "");
        var changes = resolved.Rows.SelectMany(r => resolved.Table.Table.Remove(state, r)).ToList();
        return Write(state, caller, resolved, changes, resolved.Rows, 0, $"Removed {Rows(resolved.Table, resolved.Rows)}.", Words("remove", table, rows, null));
    }

    // The workspace a verb acts on, at the revision its caller read, with the rows as the caller said them.
    (WorkspaceState State, IReadOnlyList<BoundTable> Tables, RowSelector Selector) Open(string workspaceId, long? revision, Caller caller, string? table, string? rows)
    {
        var state = WorkspaceCommands.Owned(workspaces, workspaceId, caller);
        if (revision is { } read && read != state.Revision)
            throw new WorkspaceRefused("RevisionMoved", $"The workspace is at revision {state.Revision}, not {read}.");
        var tables = register.Of(state);
        // The table said beside the rows is the one they are read in: its subsets, and no row where it has one.
        var named = table is null ? null : TableRegister.Find(tables, table);
        var reading = SelectorReader.Read(rows ?? "", [.. tables.Select(t => t.Descriptor)], named?.Descriptor);
        if (reading.Rest.Count > 0)
            throw new TableRefused("UnreadRows",
                $"{string.Join(' ', reading.Rest)} is left over after the rows: rows are ids separated by commas, all, a subset, or where and its conditions.", reading.Rest[0]);
        if (named is null || table is null) return (state, tables, reading.Selector);
        if (reading.Selector.Table is { } said && said != named.Descriptor.TableId)
            throw new TableRefused("TwoTables", $"The rows are said to be of {TableRegister.Find(tables, said).Word} and the table to be {named.Word}.", table.Trim());
        return (state, tables, reading.Selector with { Table = named.Descriptor.TableId });
    }

    // One accepted call is one command (ADR-0016 §11), named by the words it was given; the batch is all or none, and a
    // workspace that moved since the rows were read refuses it.
    TableResult Write(WorkspaceState state, Caller caller, ResolvedRows resolved, IReadOnlyList<WorkspaceChange> changes, IReadOnlyList<ITableRow> touched, int held, string said,
        string words)
    {
        var next = workspaces.Batch(state.WorkspaceId, state.Revision, caller.UserId, changes, caller.Origin, words);
        var command = next.Log[^1].Command?.Id ?? throw new InvalidOperationException("An accepted command has an id.");
        return new(next, command, said, resolved.Table.Descriptor.TableId, resolved.Rows.Count, touched.Count, held, [.. touched.Take(Listed).Select(r => r.Key)]);
    }

    // A command that would change no row writes nothing, keeps the revision and is done (note 9).
    static TableResult Unchanged(WorkspaceState state, ResolvedRows resolved, string why)
    {
        var said = resolved.Rows.Count > 0 ? $"Nothing changed: {why}."
            : $"Nothing changed: no row of {resolved.Table.Word} matches{(resolved.Where.Count == 0 ? "" : $" {string.Join(" and ", resolved.Where)}")}.";
        return new(state, null, said, resolved.Table.Descriptor.TableId, resolved.Rows.Count, 0, resolved.Rows.Count, [.. resolved.Rows.Take(Listed).Select(r => r.Key)]);
    }

    static TableRefused NotEditable(BoundTable table, TableField field) =>
        new("NotEditable", $"{TableWords.Word(field)} is not a field a command sets; {table.Word} takes {TableWords.And(table.Descriptor.Fields.Where(f => f.Editable).Select(TableWords.Word))}.",
            TableWords.Word(field), [.. table.Descriptor.Fields.Where(f => f.Editable).Select(TableWords.Word)]);

    // A few rows by their keys, more by their number and the table's word.
    static string Rows(BoundTable table, IReadOnlyList<ITableRow> rows, bool all = false) =>
        rows.Count <= Named ? TableWords.And(rows.Select(r => r.Key)) : $"{(all ? "all " : "")}{rows.Count.ToString(CultureInfo.InvariantCulture)} {table.Word}";

    // The command as its caller gave it, in the line's own spelling: what the log names it by (ADR-0016 note 3).
    static string Words(string verb, string? table, string? rows, string? rest)
    {
        var said = SelectorReader.Words(rows ?? "");
        var named = table is null || said.StartsWith(TableWords.Of(table), StringComparison.OrdinalIgnoreCase) ? null : table.Trim();
        return string.Join(' ', new[] { verb, named, said, rest is null ? null : SelectorReader.Words(rest) }.Where(w => !string.IsNullOrEmpty(w)));
    }
}

/// <summary>Reads <c>name=value</c> pairs against a table's fields (ADR-0016 §3, note 12): a field by its words, which may be several, and a value that may be too.</summary>
static class Assignments
{
    const string How = "values are written name=value, one or several.";

    /// <summary>Whether the first name in the text is a field of the table, so the text is meant for it; the rest is then read against it, and refused in its words.</summary>
    public static bool StartsWithAFieldOf(string text, BoundTable table) => text.IndexOf('=') is > 0 and var sign && Fields(table, text[..sign]).Count > 0;

    public static IReadOnlyList<(TableField Field, string Text)> Read(string text, BoundTable table)
    {
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
        if (pairs.Count == 0) throw new TableRefused("UnreadValues", $"{text.Trim()} says no value: {How}");
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
}

/// <summary><c>entity.set</c> (ADR-0016 §3): one value or several, as written, into rows of a registered table of the caller's workspace.</summary>
public sealed class EntitySet(Func<TableService> tables) : ICapabilityHandler
{
    public const string Id = "entity.set";

    public CapabilityDescriptor Descriptor { get; } = new(Id, TableContracts.Version, "Set fields of rows of a table of a workspace",
        [new("request", TableContracts.SetRequest, TableContracts.Version, Cardinality.One)],
        [new("answer", TableContracts.Edited, TableContracts.Version, Cardinality.One)],
        ExecutionKind.RequestResponse, [CapabilityEffect.EditWorkspace]);

    public Task<IReadOnlyDictionary<string, JsonNode?>> InvokeAsync(CapabilityInvocation invocation, CancellationToken cancellationToken) =>
        TableContracts.Answer(invocation, (request, caller) => tables().Set(TableContracts.Text(request, "workspaceId"), request["revision"]?.GetValue<long>(), caller,
            request["table"]?.GetValue<string>(), request["rows"]?.GetValue<string>(), TableContracts.Text(request, "set")));
}

/// <summary><c>entity.reset</c> (ADR-0016 §3): removes the edits on rows of a registered table, of one field or of every field.</summary>
public sealed class EntityReset(Func<TableService> tables) : ICapabilityHandler
{
    public const string Id = "entity.reset";

    public CapabilityDescriptor Descriptor { get; } = new(Id, TableContracts.Version, "Reset fields of rows of a table of a workspace to what they follow",
        [new("request", TableContracts.ResetRequest, TableContracts.Version, Cardinality.One)],
        [new("answer", TableContracts.Edited, TableContracts.Version, Cardinality.One)],
        ExecutionKind.RequestResponse, [CapabilityEffect.EditWorkspace]);

    public Task<IReadOnlyDictionary<string, JsonNode?>> InvokeAsync(CapabilityInvocation invocation, CancellationToken cancellationToken) =>
        TableContracts.Answer(invocation, (request, caller) => tables().Reset(TableContracts.Text(request, "workspaceId"), request["revision"]?.GetValue<long>(), caller,
            request["table"]?.GetValue<string>(), request["rows"]?.GetValue<string>(), request["field"]?.GetValue<string>()));
}

/// <summary><c>entity.remove</c> (ADR-0016 §3): removes rows the workspace made from a registered table.</summary>
public sealed class EntityRemove(Func<TableService> tables) : ICapabilityHandler
{
    public const string Id = "entity.remove";

    public CapabilityDescriptor Descriptor { get; } = new(Id, TableContracts.Version, "Remove rows a workspace made from a table",
        [new("request", TableContracts.RemoveRequest, TableContracts.Version, Cardinality.One)],
        [new("answer", TableContracts.Edited, TableContracts.Version, Cardinality.One)],
        ExecutionKind.RequestResponse, [CapabilityEffect.EditWorkspace]);

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
            ("set", new JsonObject { ["type"] = "string", ["minLength"] = 1, ["title"] = "Values as name=value, one or several (<field>=<value> <field>=<value>), each written as its field is typed and checked in the order given" }, true))));
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
