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
        // A table of one row is addressed without a row where its fields' words say which (note 7).
        var alone = selector.Table is null && selector.SaysNoRows ? tables.Where(t => t.Descriptor.SingleRow && Assignments.Names(set, t)).ToList() : [];
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
        var changed = new HashSet<ITableRow>(ReferenceEqualityComparer.Instance);
        var written = new List<(TableField Field, JsonNode? Value)>();
        var whole = resolved.Whole?.ToDictionary(s => s.Name);
        foreach (var (field, value) in values)
        {
            var differing = resolved.Rows.Where(r => !Same(r.Value(field.Name), value)).ToList();
            if (differing.Count == 0) continue;
            written.Add((field, value));
            foreach (var group in differing.GroupBy(r => r.Subset))
            {
                // One edit for the whole subset where the field has one (ADR-0016 §2); it wins over each row's earlier edit.
                var all = group.Key is not null && whole is not null && whole.TryGetValue(group.Key, out var subset) ? bound.Table.SetAll(state, subset, field, value) : null;
                changes.AddRange(all ?? group.SelectMany(r => bound.Table.Set(state, r, field, value)));
            }
            changed.UnionWith(differing);
        }

        var touched = resolved.Rows.Where(changed.Contains).ToList();
        var held = resolved.Rows.Count - touched.Count;
        if (touched.Count == 0)
        {
            var holds = And(values.Select(v => $"{TableWords.Word(v.Field)} {FieldForms.Write(v.Field.Spec, v.Value)}"));
            return Unchanged(state, resolved, $"{Rows(bound, resolved.Rows, all: true)} already hold{(resolved.Rows.Count == 1 ? "s" : "")} {holds}");
        }
        var assigned = And(written.Select(v => $"{TableWords.Word(v.Field)} to {FieldForms.Write(v.Field.Spec, v.Value)}"));
        var already = held == 0 ? "" : $"; {held} already held {(written.Count == 1 ? "it" : "them")}";
        var said = touched.Count == 1 ? $"Set {touched[0].Key} {assigned}{already}." : $"Set {assigned} on {Rows(bound, touched)}{already}.";
        return Write(state, caller, resolved, changes, touched, held, said, Words("set", table, rows, set));
    }

    // The workspace a verb acts on, at the revision its caller read, with the rows as the caller said them.
    (WorkspaceState State, IReadOnlyList<BoundTable> Tables, RowSelector Selector) Open(string workspaceId, long? revision, Caller caller, string? table, string? rows)
    {
        var state = WorkspaceCommands.Owned(workspaces, workspaceId, caller);
        if (revision is { } read && read != state.Revision)
            throw new WorkspaceRefused("RevisionMoved", $"The workspace is at revision {state.Revision}, not {read}.");
        var tables = register.Of(state);
        var reading = SelectorReader.Read(rows ?? "", [.. tables.Select(t => t.Descriptor)]);
        if (reading.Rest.Count > 0)
            throw new TableRefused("UnreadRows",
                $"{string.Join(' ', reading.Rest)} is left over after the rows: rows are ids separated by commas, all, a subset, or where and its conditions.", reading.Rest[0]);
        if (table is null) return (state, tables, reading.Selector);
        var named = TableRegister.Find(tables, table);
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
        new("NotEditable", $"{TableWords.Word(field)} is not a field a command sets; {table.Word} takes {And(table.Descriptor.Fields.Where(f => f.Editable).Select(TableWords.Word))}.",
            TableWords.Word(field), [.. table.Descriptor.Fields.Where(f => f.Editable).Select(TableWords.Word)]);

    // A few rows by their keys, more by their number and the table's word.
    static string Rows(BoundTable table, IReadOnlyList<ITableRow> rows, bool all = false) =>
        rows.Count <= Named ? And(rows.Select(r => r.Key)) : $"{(all ? "all " : "")}{rows.Count.ToString(CultureInfo.InvariantCulture)} {table.Word}";

    // The command as its caller gave it, in the line's own spelling: what the log names it by (ADR-0016 note 3).
    static string Words(string verb, string? table, string? rows, string? rest)
    {
        var said = SelectorReader.Words(rows ?? "");
        var named = table is null || said.StartsWith(TableWords.Of(table), StringComparison.OrdinalIgnoreCase) ? null : table.Trim();
        return string.Join(' ', new[] { verb, named, said, rest is null ? null : SelectorReader.Words(rest) }.Where(w => !string.IsNullOrEmpty(w)));
    }

    /// <summary>Two values say the same thing: numbers by value (1.0 is 1), the rest as JSON.</summary>
    static bool Same(JsonNode? a, JsonNode? b) =>
        a is JsonValue x && b is JsonValue y && x.GetValueKind() == System.Text.Json.JsonValueKind.Number && y.GetValueKind() == System.Text.Json.JsonValueKind.Number
            ? Number(x) == Number(y) : JsonNode.DeepEquals(a, b);

    static decimal Number(JsonValue value) => value.TryGetValue<decimal>(out var n) ? n : decimal.Parse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture);

    static string And(IEnumerable<string> items)
    {
        var list = items.ToList();
        return list.Count <= 1 ? string.Concat(list) : $"{string.Join(", ", list.Take(list.Count - 1))} and {list[^1]}";
    }
}

/// <summary>Reads <c>name=value</c> pairs against a table's fields (ADR-0016 §3, note 12): a field by its words, which may be several, and a value that may be too.</summary>
static class Assignments
{
    const string How = "values are written name=value, one or several.";

    /// <summary>Whether every name in the text is a field of the table, so the text can be meant for it.</summary>
    public static bool Names(string text, BoundTable table)
    {
        try
        {
            return Read(text, table).Count > 0;
        }
        catch (TableRefused)
        {
            return false;
        }
    }

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
            // The longest run of words before the sign that names a field is the name; what stands before it ends the value before.
            var from = Enumerable.Range(0, pending.Count).FirstOrDefault(i => Fields(table, string.Join(' ', pending.Skip(i))).Count > 0, pending.Count - 1);
            if (pending.Count == 0 || (from > 0 && pairs.Count == 0)) throw new TableRefused("UnreadValues", $"{text.Trim()} names no field before a value: {How}", "=");
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

    static IReadOnlyList<TableField> Fields(BoundTable table, string name)
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

/// <summary>The edit verbs' contracts, flat as every contract is (ADR-0016 note 12), and how a verb's result and refusal are answered.</summary>
public static class TableContracts
{
    public const string Version = "1.0.0";
    public const string SetRequest = "entity.set-request";
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
            throw new CapabilityFailure(refused.Code, refused.Message);
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

    internal const string RowsTitle = "Rows as the command line spells them: ids separated by commas (E00003, E00004), all, a subset, or where and conditions (where ccy=USD and maturity>5y); left out for a table of one row";
}

/// <summary>The kernel's edit verbs over registered tables, and their contracts. Knows no table.</summary>
public sealed class TablesModule(Func<TableService> tables) : IQubeModule
{
    public void Register(CompositionRegistryBuilder builder)
    {
        builder.AddCapability(new EntitySet(tables));
        builder.AddContract(new ContractDefinition(TableContracts.SetRequest, TableContracts.Version, "Set fields of rows", TableContracts.Request(TableContracts.RowsTitle,
            ("set", new JsonObject { ["type"] = "string", ["minLength"] = 1, ["title"] = "Values as name=value, one or several (notional=5bn rate=1.25%), each written as its field is typed" }, true))));
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
