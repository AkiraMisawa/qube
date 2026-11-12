using System.Text.Json.Nodes;
using Qube.Composition.Application;
using Qube.Composition.Application.Tables;
using Qube.Composition.Application.Workspaces;
using Qube.Composition.Contracts;

namespace Qube.Composition.Tests;

// ADR-0016 §7 and its note 38 of 2026-11-11: the AI describes and reads a workspace's tables through two kernel
// capabilities, each a command because it reads its caller's own workspace, invoked as any command is. The
// registration is station readings, with no financial word.
public class TableReadTests
{
    sealed class Rig
    {
        public WorkspaceService Workspaces { get; } = Stations.Service();
        public CompositionRegistry Registry { get; }
        public CommandService Commands { get; }
        public string Id { get; }

        public Rig(TableDescriptor? stations = null, bool sensors = false)
        {
            var tables = new TableService(new TableRegister([new Stations.Catalog(stations, sensors)]), Workspaces);
            Registry = new CompositionRegistry([new TablesModule(() => tables), new WorkspaceCommandsModule(() => Workspaces)]);
            Commands = new CommandService(Registry, new InMemoryDataStore());
            Id = Stations.Board(Workspaces).WorkspaceId;
        }

        public WorkspaceState Board => Workspaces.Find(Id)!;

        public Task<CommandResult> Invoke(string capability, JsonObject request, Caller? caller = null)
        {
            request["workspaceId"] ??= Id;
            return Commands.InvokeAsync(capability, "1.0.0", new JsonObject { ["request"] = request }, caller ?? new Caller("ana", RunOrigin.Ai), Guid.NewGuid().ToString(), default);
        }

        public async Task<JsonObject> Done(string capability, JsonObject request, Caller? caller = null)
        {
            var result = await Invoke(capability, request, caller);
            return result is CommandResult.Done done ? done.Outputs["answer"]!.AsObject()
                : throw new Xunit.Sdk.XunitException($"{capability} was refused: {string.Join(" ", ((CommandResult.Refused)result).Issues.Select(i => i.Message))}");
        }

        public async Task<(string Code, CompositionIssue Issue)> Refused(string capability, JsonObject request, Caller? caller = null)
        {
            var refused = Assert.IsType<CommandResult.Refused>(await Invoke(capability, request, caller));
            return (refused.Code, Assert.Single(refused.Issues));
        }

        /// <summary>The answer checked against the contract its capability declares for it: nothing it holds is undeclared.</summary>
        public void Meets(string capability, JsonObject answer)
        {
            var port = Registry.FindCapability(capability, "1.0.0")!.Descriptor.OutputPorts.Single();
            Assert.Empty(ContractChecks.Check(Registry.FindContract(port.ContractId, port.ContractVersion)!, answer, "/answer"));
        }
    }

    static string Joined(JsonNode? list) => string.Join("|", list!.AsArray().Select(i => i!.GetValue<string>()));

    static string? Text(JsonNode row, string name) => row[name]?.GetValue<string>();

    [Fact] // ADR-0016 §7, note 38: the registration is read out, each table with its words, its key, its subsets and how many rows it has
    public async Task Describe_answers_the_tables_of_the_workspaces_application()
    {
        var rig = new Rig();

        var answer = await rig.Done("tables.describe", new JsonObject());

        Assert.Equal((rig.Id, rig.Board.Revision, Stations.App), (Text(answer, "workspaceId"), answer["revision"]!.GetValue<long>(), Text(answer, "app")));
        Assert.Equal(
        [
            ("weather.readings", "Readings", "readings|obs", "id", 6, "manual|automatic", false, true),
            ("weather.stations", "Stations", "stations|site", "id", 3, "high|manual", false, false),
            ("weather.settings", "Board settings", "settings|board", "id", 1, "", true, false),
        ], answer["tables"]!.AsArray().Select(t => (Text(t!, "table"), Text(t!, "title"), Joined(t!["words"]), Text(t!, "key"), t!["rows"]!.GetValue<int>(), Joined(t["subsets"]),
            t["singleRow"]!.GetValue<bool>(), t["main"]!.GetValue<bool>())));
        rig.Meets("tables.describe", answer);
    }

    static (string Words, string Form, string Written, bool Editable, bool Optional, string? Choices, string? UnitField, string? PointsTo) Described(JsonNode field) =>
        (Joined(field["words"]), Text(field, "form")!, Text(field, "written")!, field["editable"]!.GetValue<bool>(), field["optional"]!.GetValue<bool>(),
            field["choices"] is { } choices ? Joined(choices) : null, Text(field, "unitField"), Text(field, "pointsTo"));

    [Fact] // ADR-0016 §7, note 38: a field says its words, how its value is written in the reader's own words, whether a command sets it, its choices, its unit's field and the table it points into
    public async Task Describe_answers_each_field_with_how_its_value_is_written()
    {
        var rig = new Rig();

        var answer = await rig.Done("tables.describe", new JsonObject());

        var fields = answer["fields"]!.AsArray();
        Assert.Equal(17, fields.Count);
        var readings = fields.Where(f => Text(f!, "table") == Stations.Readings).ToDictionary(f => Text(f!, "name")!, f => Described(f!));
        Assert.Equal(("volume|vol|limit", "amount", "an amount: 5mm, 250k, 1bn, or a bare number of millions", true, false, null, "unit", null), readings["volume"]);
        Assert.Equal(("station|site", "choice", "one of its choices", false, false, "NORTH|SOUTH|EAST", null, Stations.Sites), readings["station"]);
        Assert.Equal(("note|obs note", "id", "an identifier, or empty", true, true, null, null, null), readings["note"]);
        Assert.Equal(("drift", "rate", "a rate with its unit, 40% or 25bp, or empty", true, true, null, null, null), readings["drift"]);
        Assert.Equal(("checked|ok", "flag", "on or off", true, false, null, null, null), readings["checked"]);
    }

    [Fact] // ADR-0016 note 38: choices are listed to fifty, and how many more there are is said beside them, never cut quietly
    public async Task Describe_lists_fifty_choices_and_says_how_many_more_there_are()
    {
        var zones = Enumerable.Range(1, 60).ToDictionary(i => $"z{i}", i => $"Z{i}");
        var rig = new Rig(Stations.SitesTable with { Fields = [.. Stations.SitesTable.Fields, new("zone", "Zone", ["zone"], FieldForm.Choice, Choices: zones)] });

        var answer = await rig.Done("tables.describe", new JsonObject { ["table"] = "stations" });

        var zone = answer["fields"]!.AsArray().Single(f => Text(f!, "name") == "zone")!;
        Assert.Equal(string.Join("|", Enumerable.Range(1, 50).Select(i => $"Z{i}")), Joined(zone["choices"]));
        Assert.Equal(10, zone["moreChoices"]!.GetValue<int>());
        rig.Meets("tables.describe", answer);
    }

    [Fact] // ADR-0016 note 38: one table where one is named, by its words or its id; an unknown one is refused with the tables there are
    public async Task Describe_answers_one_table_by_its_words_or_its_id_and_refuses_an_unknown_one()
    {
        var rig = new Rig();

        var answer = await rig.Done("tables.describe", new JsonObject { ["table"] = "obs" });
        var byId = await rig.Done("tables.describe", new JsonObject { ["table"] = "weather.stations" });
        var (code, issue) = await rig.Refused("tables.describe", new JsonObject { ["table"] = "tides" });

        Assert.Equal([Stations.Readings], answer["tables"]!.AsArray().Select(t => Text(t!, "table")));
        Assert.All(answer["fields"]!.AsArray(), f => Assert.Equal(Stations.Readings, Text(f!, "table")));
        Assert.Equal(["weather.stations"], byId["tables"]!.AsArray().Select(t => Text(t!, "table")));
        Assert.Equal(("UnknownTable", "No table tides: the tables are readings, stations, settings.", "tides"), (code, issue.Message, issue.Word));
        Assert.Equal(["readings", "stations", "settings"], issue.Candidates);
    }

    [Fact] // ADR-0016 note 38, ADR-0003: a reading is of its caller's own workspace, at the revision the caller read
    public async Task A_reading_is_refused_for_another_user_and_at_a_revision_the_workspace_has_left()
    {
        var rig = new Rig();

        Assert.Equal("NotOwner", (await rig.Refused("tables.describe", new JsonObject(), new Caller("ben", RunOrigin.Ai))).Code);
        Assert.Equal("NotOwner", (await rig.Refused("tables.read", new JsonObject { ["rows"] = "all" }, new Caller("ben", RunOrigin.Ai))).Code);
        Assert.Equal("RevisionMoved", (await rig.Refused("tables.describe", new JsonObject { ["revision"] = rig.Board.Revision + 1 })).Code);
        Assert.Equal("RevisionMoved", (await rig.Refused("tables.read", new JsonObject { ["rows"] = "all", ["revision"] = rig.Board.Revision + 1 })).Code);
        // A workspace there is none of is refused as that, never read as an empty one.
        Assert.Equal("UnknownWorkspace", (await rig.Refused("tables.describe", new JsonObject { ["workspaceId"] = "ws-none" })).Code);
        Assert.Equal("UnknownWorkspace", (await rig.Refused("tables.read", new JsonObject { ["workspaceId"] = "ws-none", ["rows"] = "all" })).Code);
    }

    static JsonArray Fields(params string[] fields) => new([.. fields.Select(f => (JsonNode)f)]);

    static (string Key, string? Subset, string Cells) Row(JsonNode? row) => (Text(row!, "key")!, Text(row!, "subset"), Joined(row!["cells"]));

    static (string Table, int Count, int Start, string Fields, int More) Page(JsonObject answer) =>
        (Text(answer, "table")!, answer["count"]!.GetValue<int>(), answer["start"]!.GetValue<int>(), Joined(answer["fields"]), answer["more"]!.GetValue<int>());

    [Fact] // ADR-0016 §7, note 38: rows by a condition, with the fields asked by their words: the count, and each cell written as a command reads it back
    public async Task Read_answers_the_rows_a_condition_leaves_with_each_cell_as_the_line_types_it()
    {
        var rig = new Rig();

        var answer = await rig.Done("tables.read", new JsonObject { ["rows"] = "where station=north", ["fields"] = Fields("vol", "unit", "ok", "note") });

        Assert.Equal((Stations.Readings, 2, 0, "volume|unit|checked|note", 0), Page(answer));
        Assert.Equal([("R1", "manual", "5mm|M3|off|empty"), ("R2", "manual", "250k|M3|on|gusty")], answer["rows"]!.AsArray().Select(Row));
        Assert.Equal(rig.Board.Revision, answer["revision"]!.GetValue<long>());
        rig.Meets("tables.read", answer);
    }

    [Fact] // ADR-0016 note 38: what the agent read it can write: a cell's text, given to set, leaves the value as it was
    public async Task A_cell_as_read_is_a_value_set_reads_back()
    {
        var rig = new Rig();
        var read = await rig.Done("tables.read", new JsonObject { ["rows"] = "R3", ["fields"] = Fields("volume") });
        var cell = Row(read["rows"]!.AsArray().Single()).Cells;

        var set = await rig.Done("entity.set", new JsonObject { ["rows"] = "R3", ["set"] = $"volume={cell}" });

        Assert.Equal(("12mm", "Nothing changed: R3 already holds volume 12mm."), (cell, Text(set, "said")));
    }

    [Fact] // ADR-0016 note 38: with no field asked the rows are their keys alone, which is how rows are counted; rows are said as a command says them
    public async Task Read_with_no_field_answers_keys_alone_for_ids_a_subset_and_all()
    {
        var rig = new Rig();

        var all = await rig.Done("tables.read", new JsonObject { ["rows"] = "all" });
        var subset = await rig.Done("tables.read", new JsonObject { ["rows"] = "automatic where station=east" });
        var ids = await rig.Done("tables.read", new JsonObject { ["rows"] = "R5, R1" });
        var stations = await rig.Done("tables.read", new JsonObject { ["table"] = "stations", ["rows"] = "all" });

        Assert.Equal((Stations.Readings, 6, 0, "", 0), Page(all));
        Assert.Equal(["R1", "R2", "R3", "R4", "R5", "X1"], all["rows"]!.AsArray().Select(r => Row(r).Key));
        Assert.All(all["rows"]!.AsArray(), r => Assert.Equal("", Row(r).Cells));
        Assert.Equal([("R5", "automatic", ""), ("X1", "automatic", "")], subset["rows"]!.AsArray().Select(Row));
        Assert.Equal(["R5", "R1"], ids["rows"]!.AsArray().Select(r => Row(r).Key));
        Assert.Equal((Stations.Sites, 3, 0, "", 0), Page(stations));
    }

    [Fact] // ADR-0016 note 38: a table of one row is read with no row said, as it is set
    public async Task Read_answers_the_row_of_a_table_of_one_row_with_no_row_said()
    {
        var rig = new Rig();

        var answer = await rig.Done("tables.read", new JsonObject { ["table"] = "settings", ["fields"] = Fields("alarm", "daylight") });

        Assert.Equal((Stations.Settings, 1, 0, "alarm|daylight", 0), Page(answer));
        Assert.Equal([("board", null, "10mm|on")], answer["rows"]!.AsArray().Select(Row));
        rig.Meets("tables.read", answer);
    }

    [Fact] // ADR-0016 note 38: a page starts where asked, holds the rows asked, and says how many follow; past the end it holds none and still says the count
    public async Task Read_pages_from_where_it_is_asked_and_says_how_many_rows_follow()
    {
        var rig = new Rig();

        var first = await rig.Done("tables.read", new JsonObject { ["rows"] = "all", ["limit"] = 4 });
        var second = await rig.Done("tables.read", new JsonObject { ["rows"] = "all", ["limit"] = 4, ["start"] = 4 });
        var past = await rig.Done("tables.read", new JsonObject { ["rows"] = "all", ["start"] = 10 });

        Assert.Equal(((Stations.Readings, 6, 0, "", 2), "R1|R2|R3|R4"), (Page(first), string.Join("|", first["rows"]!.AsArray().Select(r => Row(r).Key))));
        Assert.Equal(((Stations.Readings, 6, 4, "", 0), "R5|X1"), (Page(second), string.Join("|", second["rows"]!.AsArray().Select(r => Row(r).Key))));
        Assert.Equal(((Stations.Readings, 6, 10, "", 0), 0), (Page(past), past["rows"]!.AsArray().Count));
    }

    [Fact] // ADR-0016 note 38: a condition no row meets is an answer of no row, counted as none; a start past the last position a page can hold is refused by the contract
    public async Task Read_answers_no_row_for_a_condition_none_meets_and_refuses_a_start_no_position_holds()
    {
        var rig = new Rig();

        var none = await rig.Done("tables.read", new JsonObject { ["rows"] = "where station=north and station=south", ["fields"] = Fields("station") });
        var (code, issue) = await rig.Refused("tables.read", new JsonObject { ["rows"] = "all", ["start"] = 3_000_000_000 });

        Assert.Equal(((Stations.Readings, 0, 0, "station", 0), 0), (Page(none), none["rows"]!.AsArray().Count));
        Assert.Equal(("ValidationFailed", "/inputs/request/start"), (code, issue.Path));
    }

    [Fact] // ADR-0016 note 38: 50 rows where none is asked, 200 at most, and at most 2,000 cells: a page of many fields holds fewer rows, and says how many follow
    public async Task Read_bounds_a_page_by_rows_and_by_cells()
    {
        var rig = new Rig();
        for (var i = 0; i < 220; i++) Stations.Made(rig.Workspaces, rig.Board, $"M{i:000}");
        var every = Fields("id", "station", "volume", "unit", "taken", "checked", "note", "wind speed", "wind direction", "window", "drift");

        var unasked = await rig.Done("tables.read", new JsonObject { ["rows"] = "all" });
        var most = await rig.Done("tables.read", new JsonObject { ["rows"] = "all", ["limit"] = 200 });
        var wide = await rig.Done("tables.read", new JsonObject { ["rows"] = "all", ["limit"] = 200, ["fields"] = every });
        var (code, issue) = await rig.Refused("tables.read", new JsonObject { ["rows"] = "all", ["limit"] = 201 });

        Assert.Equal((226, 50, 176), (unasked["count"]!.GetValue<int>(), unasked["rows"]!.AsArray().Count, unasked["more"]!.GetValue<int>()));
        Assert.Equal((200, 26), (most["rows"]!.AsArray().Count, most["more"]!.GetValue<int>()));
        // Eleven fields a row: 181 rows are 1,991 cells, and one more would pass 2,000.
        Assert.Equal((181, 45), (wide["rows"]!.AsArray().Count, wide["more"]!.GetValue<int>()));
        Assert.Equal(("ValidationFailed", "/inputs/request/limit"), (code, issue.Path));
    }

    [Fact] // ADR-0016 note 38, §6: a field the table has not is refused with the fields there are, words two fields share with both, and a field asked twice by name
    public async Task Read_refuses_a_field_it_cannot_read_by_name()
    {
        var rig = new Rig();

        var (unknown, colour) = await rig.Refused("tables.read", new JsonObject { ["rows"] = "all", ["fields"] = Fields("colour") });
        var (ambiguous, wind) = await rig.Refused("tables.read", new JsonObject { ["rows"] = "all", ["fields"] = Fields("wind") });
        var (twice, volume) = await rig.Refused("tables.read", new JsonObject { ["rows"] = "all", ["fields"] = Fields("vol", "volume") });

        Assert.Equal(("UnknownField", "colour"), (unknown, colour.Word));
        Assert.Equal(["id", "station", "volume", "unit", "taken", "checked", "note", "wind speed", "wind direction", "window", "drift"], colour.Candidates);
        Assert.Equal(("AmbiguousField", "wind could be wind speed or wind direction of readings."), (ambiguous, wind.Message));
        Assert.Equal(("FieldTwice", "volume is asked twice; ask each field once.", "volume"), (twice, volume.Message, volume.Word));
    }

    [Fact] // ADR-0016 notes 17 and 38: a reading has no screen, so selected and shown are refused by name; rows left unsaid are refused as a command's are
    public async Task Read_refuses_the_rows_of_a_screen_and_rows_left_unsaid()
    {
        var rig = new Rig();

        Assert.Equal("NoView", (await rig.Refused("tables.read", new JsonObject { ["rows"] = "selected" })).Code);
        Assert.Equal("NoView", (await rig.Refused("tables.read", new JsonObject { ["rows"] = "shown where station=north" })).Code);
        Assert.Equal("NoRows", (await rig.Refused("tables.read", new JsonObject { ["table"] = "readings" })).Code);
        Assert.Equal("UnknownRow", (await rig.Refused("tables.read", new JsonObject { ["rows"] = "R9" })).Code);
    }

    [Fact] // ADR-0016 note 38, docs/10: the handoff's schema of a catalog lists every effect a capability may declare, as the catalog writes it
    public void The_handoffs_schema_lists_every_effect_a_capability_declares()
    {
        var schema = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Contracts", "composition.schema.json")))!;
        var effects = Assert.Single(Effects(schema));

        Assert.Equal(
            ["readData", "compute", "persistAnalysis", "editWorkspace", "subscribe", "readRun", "saveItem", "execute", "readWorkspace", "readSettings", "editSettings"],
            Enum.GetNames<CapabilityEffect>().Select(n => System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(n)));
        Assert.Equal(Enum.GetNames<CapabilityEffect>().Select(n => System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(n)), effects);
    }

    // Every list of effects the schema declares, wherever it stands: the names its items may take.
    static IEnumerable<string[]> Effects(JsonNode? node) => node switch
    {
        JsonObject o => o.SelectMany(p => p.Key == "effects" && p.Value?["items"]?["enum"] is JsonArray names
            ? [[.. names.Select(n => n!.GetValue<string>())]]
            : Effects(p.Value)),
        JsonArray a => a.SelectMany(Effects),
        _ => [],
    };

    [Fact] // ADR-0005 §2, ADR-0016 note 38: a reading acts for a caller, so it is a command and never a workflow step
    public void A_reading_is_refused_as_a_workflow_step()
    {
        var rig = new Rig();
        var workflow = new WorkflowDefinition("workflow", CompositionValidator.SchemaVersion, "workflow.test.reading", "1.0.0", "A reading as a step",
            TableReadContracts.ReadRequest, TableContracts.Version,
            [new WorkflowStep("read", "tables.read", "1.0.0", [], new Dictionary<string, Binding> { ["request"] = new ConstantBinding(new JsonObject { ["workspaceId"] = rig.Id, ["rows"] = "all" }) }, FailurePolicy.Stop)],
            new Dictionary<string, Binding> { ["rows"] = new OutputBinding("read", "answer") });

        Assert.Contains(new CompositionValidator(rig.Registry).Validate(workflow), i => i.Code == "CommandAsStep");
    }

    [Fact] // ADR-0016 notes 46 and 47: a description says the table a table is under, and of a figure its table, its unit and how a write of it is written
    public async Task A_description_says_what_a_table_is_under_and_what_a_figure_is_of()
    {
        var rig = new Rig(sensors: true);

        var answer = await rig.Done("tables.describe", new JsonObject());

        var sensors = answer["tables"]!.AsArray().Single(t => Text(t!, "table") == Stations.Sensors)!;
        Assert.Equal(Stations.Sites, Text(sensors, "under"));
        Assert.Null(answer["tables"]!.AsArray().Single(t => Text(t!, "table") == Stations.Sites)!["under"]);
        var load = answer["fields"]!.AsArray().Single(f => Text(f!, "table") == Stations.Sites && Text(f!, "name") == "load")!;
        Assert.Equal((Stations.Sensors, "M3", "a number, alone or followed by what it is in: M3 or GAL"), (Text(load, "figureOf"), Text(load, "unit"), Text(load, "written")));
        rig.Meets("tables.describe", answer);
    }
}
