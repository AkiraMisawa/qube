using System.Text.Json;
using System.Text.Json.Nodes;
using Qube.Composition.Application.Workspaces;
using Qube.Composition.Contracts;
using static Qube.Composition.Application.ContractShapes;

namespace Qube.Composition.Application.Tables;

// ADR-0016 §7 and its note 38 of 2026-11-11: what the AI has in place of a screen. A workspace's tables read out, and
// rows of one for a selector and the fields wanted, as a bounded page that says its bounds. Each reads its caller's
// own workspace and writes nothing.

/// <summary>A workspace's tables as registered, each with how many rows it has as the workspace stands.</summary>
public sealed record TablesDescribed(WorkspaceState State, IReadOnlyList<(BoundTable Table, int Rows)> Tables);

/// <summary>
/// Rows of a table as read: how many the selector matched, where the page starts among them, the fields asked, and the
/// rows of the page. What follows the page is the count less what came before and with it.
/// </summary>
public sealed record RowsRead(WorkspaceState State, BoundTable Table, int Count, int Start, IReadOnlyList<TableField> Fields, IReadOnlyList<ITableRow> Rows)
{
    public int More => Math.Max(0, Count - Start - Rows.Count);
}

public sealed partial class TableService
{
    /// <summary>The rows of a page where none is asked.</summary>
    public const int PageRows = 50;

    /// <summary>The most rows a page holds.</summary>
    public const int MostRows = 200;

    /// <summary>The most cells an answer holds: a page of many fields holds fewer rows, and one at least.</summary>
    public const int MostCells = 2_000;

    /// <summary>The tables of the caller's own workspace, or the one named, by its id or its words.</summary>
    public TablesDescribed Describe(string workspaceId, long? revision, Caller caller, string? table)
    {
        var state = WorkspaceCommands.Owned(workspaces, workspaceId, caller, revision);
        var tables = register.Of(state);
        IReadOnlyList<BoundTable> asked = table is null ? tables : [TableRegister.Find(tables, table)];
        return new(state, [.. asked.Select(t => (t, t.Table.Rows(state).Count))]);
    }

    /// <summary>
    /// Rows of a table of the caller's own workspace, said as a command says them and read by the one selector, with the
    /// fields asked by their words: the count, and the page that starts at <paramref name="start"/>. The rows come in
    /// the table's own order, or as their ids were listed.
    /// </summary>
    public RowsRead Read(string workspaceId, long? revision, Caller caller, string? table, string? rows, IReadOnlyList<string> fields, int start, int? limit)
    {
        var state = WorkspaceCommands.Owned(workspaces, workspaceId, caller, revision);
        var (tables, selector, _, _) = ReadRows(state, table, rows, null);
        var resolved = Selectors.Resolve(selector, tables, state);
        var bound = resolved.Table;
        IReadOnlyList<TableField> asked = [.. fields.Select(f => TableRegister.Field(bound, f))];
        if (asked.GroupBy(f => f.Name).FirstOrDefault(g => g.Count() > 1) is { } twice)
            throw new TableRefused("FieldTwice", $"{TableWords.Word(twice.First())} is asked twice; ask each field once.", TableWords.Word(twice.First()));
        var page = Math.Min(limit ?? PageRows, asked.Count == 0 ? MostRows : Math.Max(1, MostCells / asked.Count));
        return new(state, bound, resolved.Rows.Count, start, asked, [.. resolved.Rows.Skip(start).Take(page)]);
    }
}

/// <summary><c>tables.describe</c> (ADR-0016 §7, note 38): the registration of a workspace's tables, read out for a caller with no screen.</summary>
public sealed class TablesDescribe(Func<TableService> tables) : ICapabilityHandler
{
    public const string Id = "tables.describe";

    /// <summary>The most choices of a field that are listed; how many more there are is said beside them.</summary>
    public const int MostChoices = 50;

    public CapabilityDescriptor Descriptor { get; } = new(Id, TableContracts.Version, "Describe the tables of a workspace: their fields, how each value is written, and which a command sets",
        [new("request", TableReadContracts.DescribeRequest, TableContracts.Version, Cardinality.One)],
        [new("answer", TableReadContracts.DescribeAnswer, TableContracts.Version, Cardinality.One)],
        ExecutionKind.RequestResponse, [CapabilityEffect.ReadWorkspace]);

    public Task<IReadOnlyDictionary<string, JsonNode?>> InvokeAsync(CapabilityInvocation invocation, CancellationToken cancellationToken) =>
        TableContracts.Answered(invocation, (request, caller) =>
        {
            var described = tables().Describe(CommandRequests.Text(request, "workspaceId"), request["revision"]?.GetValue<long>(), caller, request["table"]?.GetValue<string>());
            return new JsonObject
            {
                ["workspaceId"] = described.State.WorkspaceId,
                ["revision"] = described.State.Revision,
                ["app"] = described.State.App,
                ["tables"] = new JsonArray([.. described.Tables.Select(t => (JsonNode)Table(t.Table.Descriptor, t.Rows))]),
                ["fields"] = new JsonArray([.. described.Tables.SelectMany(t => t.Table.Descriptor.Fields.Select(f => (JsonNode)Field(t.Table.Descriptor, f)))]),
                // What `set <rows> as <template> [terms]` takes (note 50): each table's templates, and each term of each.
                ["templates"] = new JsonArray([.. Templates(described).Select(x => (JsonNode)Template(x.Table, x.Template))]),
                ["templateTerms"] = new JsonArray([.. Templates(described).SelectMany(x => x.Template.Takes.Select(term => (JsonNode)Term(x.Table, x.Template, term)))]),
            };
        });

    static IEnumerable<(TableDescriptor Table, TableTemplate Template)> Templates(TablesDescribed described) =>
        described.Tables.SelectMany(t => (t.Table.Descriptor.Templates ?? []).Select(template => (t.Table.Descriptor, template)));

    static JsonObject Template(TableDescriptor table, TableTemplate template) => new()
    {
        ["table"] = table.TableId,
        ["name"] = template.Name,
        ["title"] = template.Title,
        ["words"] = List(template.Words),
        ["writes"] = List(template.Writes),
    };

    static JsonObject Term(TableDescriptor table, TableTemplate template, Products.ProductField term)
    {
        var described = new JsonObject
        {
            ["table"] = table.TableId,
            ["template"] = template.Name,
            ["name"] = term.Name,
            ["title"] = term.Title,
            ["form"] = JsonNamingPolicy.CamelCase.ConvertName(term.Form.ToString()),
            ["written"] = FieldForms.Takes(term.Spec, choicesBeside: true),
            ["required"] = term.Required,
            ["typedAlone"] = term.Unnamed,
        };
        if (term.Aliases is { Count: > 0 } names) described["names"] = List(names);
        if (term.Choices is { } choices) described["choices"] = List(choices.Values.Distinct().Select(v => FieldForms.Write(term.Spec, v)));
        return described;
    }

    static JsonObject Table(TableDescriptor table, int rows)
    {
        var described = new JsonObject
        {
            ["table"] = table.TableId,
            ["version"] = table.Version,
            ["title"] = table.Title,
            ["words"] = List(table.Words),
            ["key"] = table.Key,
            ["rows"] = rows,
            ["subsets"] = List(table.Subsets.Select(s => s.Name)),
            ["singleRow"] = table.SingleRow,
            ["main"] = table.Main,
        };
        if (table.Under is { } under) described["under"] = under;
        return described;
    }

    static JsonObject Field(TableDescriptor table, TableField field)
    {
        var described = new JsonObject
        {
            ["table"] = table.TableId,
            ["name"] = field.Name,
            ["title"] = field.Title,
            ["words"] = List(field.Words),
            ["form"] = JsonNamingPolicy.CamelCase.ConvertName(field.Form.ToString()),
            ["written"] = FieldForms.Takes(field.Written, choicesBeside: true),
            ["editable"] = field.Editable,
            ["optional"] = field.Optional,
        };
        if (field.Choices is { } choices)
        {
            // Each value once, as a cell writes it and a command reads it back.
            IReadOnlyList<string> written = [.. choices.Values.Distinct().Select(v => FieldForms.Write(field.Spec, v))];
            described["choices"] = List(written.Take(MostChoices));
            if (written.Count > MostChoices) described["moreChoices"] = written.Count - MostChoices;
        }
        if (field.UnitField is { } unitField) described["unitField"] = unitField;
        if (field.Unit is { } unit) described["unit"] = unit;
        if (field.Figure is { } figure) described["figureOf"] = figure.Of;
        if (field.Table is { } pointsTo) described["pointsTo"] = pointsTo;
        if (field.Group is { } group) described["group"] = group;
        return described;
    }
}

/// <summary><c>tables.read</c> (ADR-0016 §7, note 38): rows of a registered table of the caller's workspace, as a count and a bounded page of cells written as the line types them.</summary>
public sealed class TablesRead(Func<TableService> tables) : ICapabilityHandler
{
    public const string Id = "tables.read";

    public CapabilityDescriptor Descriptor { get; } = new(Id, TableContracts.Version, "Read rows of a table of a workspace: how many match, and a page of the fields asked",
        [new("request", TableReadContracts.ReadRequest, TableContracts.Version, Cardinality.One)],
        [new("answer", TableReadContracts.ReadAnswer, TableContracts.Version, Cardinality.One)],
        ExecutionKind.RequestResponse, [CapabilityEffect.ReadWorkspace]);

    public Task<IReadOnlyDictionary<string, JsonNode?>> InvokeAsync(CapabilityInvocation invocation, CancellationToken cancellationToken) =>
        TableContracts.Answered(invocation, (request, caller) =>
        {
            IReadOnlyList<string> fields = request["fields"] is JsonArray asked ? [.. asked.Select(f => f!.GetValue<string>())] : [];
            var read = tables().Read(CommandRequests.Text(request, "workspaceId"), request["revision"]?.GetValue<long>(), caller, request["table"]?.GetValue<string>(),
                request["rows"]?.GetValue<string>(), fields, request["start"] is { } start ? start.GetValue<int>() : 0, request["limit"]?.GetValue<int>());
            return new JsonObject
            {
                ["workspaceId"] = read.State.WorkspaceId,
                ["revision"] = read.State.Revision,
                ["table"] = read.Table.Descriptor.TableId,
                ["count"] = read.Count,
                ["start"] = read.Start,
                ["fields"] = List(read.Fields.Select(f => f.Name)),
                ["rows"] = new JsonArray([.. read.Rows.Select(row => (JsonNode)Row(row, read.Fields))]),
                ["more"] = read.More,
            };
        });

    // A row of the page: its key, the subset it is in where its table has subsets, and each cell as the line types it.
    static JsonObject Row(ITableRow row, IReadOnlyList<TableField> fields)
    {
        var read = new JsonObject { ["key"] = row.Key };
        if (row.Subset is { } subset) read["subset"] = subset;
        read["cells"] = List(fields.Select(f => FieldForms.Write(f.Spec, row.Value(f.Name))));
        return read;
    }
}

/// <summary>The readings' contracts (ADR-0016 note 38): flat, with lists of values and of flat records, as every contract is (notes 12 and 37).</summary>
public static class TableReadContracts
{
    public const string DescribeRequest = "tables.describe-request";
    public const string DescribeAnswer = "tables.described";
    public const string ReadRequest = "tables.read-request";
    public const string ReadAnswer = "tables.rows-read";

    internal static void Register(CompositionRegistryBuilder builder)
    {
        builder.AddContract(new ContractDefinition(DescribeRequest, TableContracts.Version, "Describe tables", Record(new JsonObject
        {
            ["workspaceId"] = Given("Workspace"),
            ["revision"] = Whole("Revision the workspace was read at; the one it holds when left out"),
            ["table"] = Given("One table, by its id or its words; every table of the workspace when left out"),
        }, "workspaceId")));

        builder.AddContract(new ContractDefinition(DescribeAnswer, TableContracts.Version, "Tables described", Record(new JsonObject
        {
            ["workspaceId"] = Text("Workspace"),
            ["revision"] = Whole("Revision read"),
            ["app"] = Text("The workspace's application"),
            ["tables"] = Records("The tables", new JsonObject
            {
                ["table"] = Text("Its id"),
                ["version"] = Text("Its version"),
                ["title"] = Text("Its title"),
                ["words"] = Texts("The words that name it where rows are said"),
                ["key"] = Text("The field whose value names a row"),
                ["rows"] = Whole("How many rows it has as the workspace stands"),
                ["subsets"] = Texts("Its subsets, each a word that says its rows"),
                ["singleRow"] = Flag("True for a table of one row, which is said with no row"),
                ["main"] = Flag("True for the table that all, a subset or where mean when no table is named"),
                ["under"] = Text("The id of the table its rows are under: there, the id of a row above names every row under it"),
            }, "table", "version", "title", "words", "key", "rows", "subsets", "singleRow", "main"),
            ["fields"] = Records("The fields of those tables", new JsonObject
            {
                ["table"] = Text("The id of its table"),
                ["name"] = Text("Its name"),
                ["title"] = Text("Its title, as a grid's column says it"),
                ["words"] = Texts("The words that name it in a command, a condition or a reading"),
                ["form"] = Text("How its value is written, by name"),
                ["written"] = Text("How its value is written, in words"),
                ["editable"] = Flag("Whether a command sets it"),
                ["optional"] = Flag("Whether it may be empty, written empty"),
                ["choices"] = Texts($"The values it takes, where it takes one of a few; the first {TablesDescribe.MostChoices}"),
                ["moreChoices"] = Whole("How many more values it takes than are listed"),
                ["unitField"] = Text("The field of the same row that holds its unit"),
                ["unit"] = Text("Its unit, where the table fixes one for the workspace"),
                ["figureOf"] = Text("The id of the table under its row whose rows it is worked out from; what a write of it does to them is the table's rule"),
                ["pointsTo"] = Text("The id of the table whose row its value names"),
                ["group"] = Text("The group it is drawn under"),
            }, "table", "name", "title", "words", "form", "written", "editable", "optional"),
            ["templates"] = Records("The templates of those tables: set <rows> as <template> [terms] writes the fields one names", new JsonObject
            {
                ["table"] = Text("The id of its table"),
                ["name"] = Text("Its name"),
                ["title"] = Text("Its title"),
                ["words"] = Texts("The words that name it after as"),
                ["writes"] = Texts("The fields it writes, by name, in the order it writes them; no other field of a row is written"),
            }, "table", "name", "title", "words", "writes"),
            ["templateTerms"] = Records("The terms those templates take, typed after the template", new JsonObject
            {
                ["table"] = Text("The id of the template's table"),
                ["template"] = Text("The name of its template"),
                ["name"] = Text("Its name, as in name=value"),
                ["title"] = Text("Its title"),
                ["form"] = Text("How its value is written, by name"),
                ["written"] = Text("How its value is written, in words"),
                ["required"] = Flag("Whether the template is refused without it"),
                ["typedAlone"] = Flag("Whether its value may be typed without its name"),
                ["names"] = Texts("Its other names"),
                ["choices"] = Texts("The values it takes, where it takes one of a few"),
            }, "table", "template", "name", "title", "form", "written", "required", "typedAlone"),
        }, "workspaceId", "revision", "app", "tables", "fields", "templates", "templateTerms")));

        var read = TableContracts.Request(TableContracts.RowsTitle + "; all for every row",
            ("fields", Texts("The fields wanted, each by its name or its words, in the order wanted; the rows' keys alone when left out"), false),
            // A start past the last number a position holds is refused here, as a limit over the most is: never read as another.
            ("start", new JsonObject { ["type"] = "integer", ["minimum"] = 0, ["maximum"] = int.MaxValue, ["title"] = "How many matching rows to pass over before the page; 0 when left out" }, false),
            ("limit", new JsonObject
            {
                ["type"] = "integer", ["minimum"] = 1, ["maximum"] = TableService.MostRows,
                ["title"] = $"The most rows of the page; {TableService.PageRows} when left out. An answer holds at most {TableService.MostCells} cells, so a page of many fields holds fewer rows: more says how many follow",
            }, false));
        builder.AddContract(new ContractDefinition(ReadRequest, TableContracts.Version, "Read rows", read));

        builder.AddContract(new ContractDefinition(ReadAnswer, TableContracts.Version, "Rows read", Record(new JsonObject
        {
            ["workspaceId"] = Text("Workspace"),
            ["revision"] = Whole("Revision read"),
            ["table"] = Text("The id of the rows' table"),
            ["count"] = Whole("How many rows the selector matched"),
            ["start"] = Whole("How many of them come before the page"),
            ["fields"] = Texts("The fields read, by name, in the order of each row's cells"),
            ["rows"] = Records("The rows of the page", new JsonObject
            {
                ["key"] = Text("The row's key, which names it in a command"),
                ["subset"] = Text("The subset it is in"),
                ["cells"] = Texts("Its values, one for each field, each written as a command reads it; empty for none"),
            }, "key", "cells"),
            ["more"] = Whole("How many matching rows follow the page; read them from start plus the rows answered"),
        }, "workspaceId", "revision", "table", "count", "start", "fields", "rows", "more")));
    }
}
