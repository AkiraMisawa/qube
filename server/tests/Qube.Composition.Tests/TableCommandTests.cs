using System.Text.Json.Nodes;
using Qube.Composition.Application;
using Qube.Composition.Application.Tables;
using Qube.Composition.Application.Workspaces;

namespace Qube.Composition.Tests;

// ADR-0016 §3, §6 and its notes 9, 10 and 12 of 2026-11-11: the kernel's edit verbs over any registered table, invoked
// as any command is, through the command service. The registration is station readings, with no financial word.
public class TableCommandTests
{
    sealed class Rig
    {
        public WorkspaceService Workspaces { get; } = Stations.Service();
        public CommandService Commands { get; }
        public string Id { get; }

        public Rig()
        {
            var tables = new TableService(new TableRegister([new Stations.Catalog()]), Workspaces);
            Commands = new CommandService(new CompositionRegistry([new TablesModule(() => tables), new WorkspaceCommandsModule(() => Workspaces)]), new InMemoryDataStore());
            Id = Stations.Board(Workspaces).WorkspaceId;
        }

        public WorkspaceState Board => Workspaces.Find(Id)!;

        public Task<CommandResult> Invoke(string capability, JsonObject request, Caller? caller = null)
        {
            request["workspaceId"] ??= Id;
            return Commands.InvokeAsync(capability, "1.0.0", new JsonObject { ["request"] = request }, caller ?? new Caller("ana", RunOrigin.Pricer), Guid.NewGuid().ToString(), default);
        }

        public async Task<JsonObject> Done(string capability, JsonObject request, Caller? caller = null)
        {
            var result = await Invoke(capability, request, caller);
            return result is CommandResult.Done done ? done.Outputs["answer"]!.AsObject()
                : throw new Xunit.Sdk.XunitException($"{capability} was refused: {string.Join(" ", ((CommandResult.Refused)result).Issues.Select(i => i.Message))}");
        }

        public async Task<(string Code, string Message)> Refused(string capability, JsonObject request, Caller? caller = null)
        {
            var refused = Assert.IsType<CommandResult.Refused>(await Invoke(capability, request, caller));
            return (refused.Code, string.Join(" ", refused.Issues.Select(i => i.Message)));
        }

        public Task<JsonObject> Set(string? rows, string set, string? table = null) =>
            Done("entity.set", Request(rows, table, ("set", set)));

        public Task<(string Code, string Message)> SetRefused(string? rows, string set, string? table = null) =>
            Refused("entity.set", Request(rows, table, ("set", set)));

        public static JsonObject Request(string? rows, string? table, params (string Name, string? Value)[] more)
        {
            var request = new JsonObject();
            if (rows is not null) request["rows"] = rows;
            if (table is not null) request["table"] = table;
            foreach (var (name, value) in more)
                if (value is not null) request[name] = value;
            return request;
        }

        /// <summary>The value a reading's field holds by the user's own edit, or "-" where it follows what was sent.</summary>
        public string Edit(string entity, string field) =>
            Board.Overrides.TryGetValue(Stations.Target(entity, field), out var edit) ? edit.Value?.ToJsonString() ?? "null" : "-";
    }

    static (long Revision, string? Command, string Said, string Table, int Matched, int Changed, int Held, string Rows) Told(JsonObject answer) =>
        (answer["revision"]!.GetValue<long>(), answer["command"]?.GetValue<string>(), answer["said"]!.GetValue<string>(), answer["table"]!.GetValue<string>(),
            answer["matched"]!.GetValue<int>(), answer["changed"]!.GetValue<int>(), answer["held"]!.GetValue<int>(), answer["rows"]!.GetValue<string>());

    [Fact] // ADR-0016 §3, §6: a row by id takes a value as written, and the result says what was done, where, and what takes it back
    public async Task A_row_named_by_id_takes_a_value_and_the_result_says_what_was_done()
    {
        var rig = new Rig();
        var before = rig.Board.Revision;

        var told = Told(await rig.Set("R1", "note=gusty"));

        Assert.Equal((before + 1, $"c{before + 1}", "Set R1 note to gusty.", Stations.Readings, 1, 1, 0, "R1"), told);
        Assert.Equal("\"gusty\"", rig.Edit("reading:R1", "note"));
        Assert.Equal(("set R1 note gusty", RunOrigin.Pricer), (rig.Board.Log[^1].Command?.Said, rig.Board.Log[^1].Origin));
    }

    [Fact] // ADR-0016 §2, §6: rows by their values are one batch, counted, and the first of them are named
    public async Task Rows_named_by_where_are_one_batch_under_one_command()
    {
        var rig = new Rig();
        var before = rig.Board.Revision;

        var told = Told(await rig.Set("where station=south", "note=wet"));
        var all = Told(await rig.Set("all", "wind speed=9"));

        Assert.Equal((before + 2, $"c{before + 1}", "Set note to wet on R3 and R4.", Stations.Readings, 2, 2, 0, "R3, R4"), told);
        Assert.Equal((before + 8, $"c{before + 3}", "Set wind speed to 9 on 6 readings.", Stations.Readings, 6, 6, 0, "R1, R2, R3, R4, R5, X1"), all);
        Assert.Equal(6, rig.Board.Log.Count(e => e.Command?.Id == $"c{before + 3}"));
    }

    [Fact] // ADR-0016 §3: several fields as name=value are one all-or-none batch at one revision
    public async Task Several_fields_are_set_at_once()
    {
        var rig = new Rig();

        var told = Told(await rig.Set("R1", "note=very wet wind speed=14 unit=gal drift=empty vol=250k"));

        Assert.Equal(("Set R1 note to very wet, wind speed to 14, unit to GAL and volume to 250k.", 1, 1, 0), (told.Said, told.Matched, told.Changed, told.Held));
        Assert.Equal(("\"very wet\"", "14", "\"GAL\"", "250000", "-"),
            (rig.Edit("reading:R1", "note"), rig.Edit("reading:R1", "windSpeed"), rig.Edit("reading:R1", "unit"), rig.Edit("reading:R1", "volume"), rig.Edit("reading:R1", "drift")));
    }

    [Fact] // ADR-0016 §3: several values are written in the order given, each checked against what the ones before it left
    public async Task Each_value_is_checked_against_what_the_ones_before_it_left()
    {
        var rig = new Rig();

        // R1 has no note: the note is written first, so the drift it explains is taken with it, as two commands would be.
        var told = Told(await rig.Set("R1", "note=wet drift=1%"));

        Assert.Equal("Set R1 note to wet and drift to 1%.", told.Said);
        Assert.Equal(("\"wet\"", "0.01"), (rig.Edit("reading:R1", "note"), rig.Edit("reading:R1", "drift")));
    }

    [Fact] // ADR-0016 §2: a subset is read as the named table's, whether the table is said with the rows or beside them
    public async Task A_subset_is_read_as_the_named_tables()
    {
        var beside = Told(await new Rig().Set("high", "altitude=130", table: "stations"));
        var with = Told(await new Rig().Set("stations upland", "altitude=130"));

        Assert.Equal(("Set N1 altitude to 130.", Stations.Sites, 1), (beside.Said, beside.Table, beside.Changed));
        Assert.Equal(beside.Said, with.Said);
    }

    [Fact] // ADR-0016 §6: a refusal carries its code, a sentence, the word at fault and what it could have been
    public async Task A_refusal_carries_the_word_at_fault_and_what_it_could_have_been()
    {
        var rig = new Rig();

        var refused = Assert.IsType<CommandResult.Refused>(await rig.Invoke("entity.set", Rig.Request("R1", null, ("set", "wind=3"))));
        var issue = Assert.Single(refused.Issues);

        Assert.Equal(("AmbiguousField", "wind could be wind speed or wind direction of readings."), (refused.Code, issue.Message));
        Assert.Equal("wind", issue.Word);
        Assert.Equal(["wind speed", "wind direction"], issue.Candidates);
    }

    [Fact] // ADR-0016 §3, note 12: a value is at least a word, so a field's name never takes the only word of the value before it
    public async Task A_field_name_never_takes_the_only_word_of_the_value_before_it()
    {
        var rig = new Rig();

        var told = Told(await rig.Set("R1", "note=wind speed=3"));
        // Where no reading leaves the value a word, the name is the longer one and the refusal says what is missing.
        var refused = await rig.SetRefused("R2", "note=wind direction=N");

        Assert.Equal("Set R1 note to wind and wind speed to 3.", told.Said);
        Assert.Equal(("UnreadValues", "note has no value: values are written name=value, one or several."), refused);
    }

    [Theory] // ADR-0016 §3: one value that cannot be read, or that the domain refuses, leaves nothing written
    [InlineData("R1, R2", "note=wet volume=lots", "InvalidValue", "R1 volume is an amount: 5mm, 250k, 1bn, or a bare number of millions; lots is not.")]
    [InlineData("R2, R1", "note=wet volume=60mm", "InvalidValue", "R2 holds at most 50mm.")]
    [InlineData("R1", "drift=4", "InvalidValue", "R1 drift is a rate with its unit, 40% or 25bp, or empty; 4 is not.")]
    [InlineData("R1", "wind sped=5", "UnknownField", "readings has no field wind sped; its fields are id, station, volume, unit, taken, checked, note, wind speed, wind direction, window, drift.")]
    [InlineData("R1", "note=wet sped=5", "UnknownField", "readings has no field sped; its fields are id, station, volume, unit, taken, checked, note, wind speed, wind direction, window, drift.")]
    [InlineData(null, "alarm=5 alarm=6", "FieldTwice", "alarm volume is given twice; give each field one value.")]
    [InlineData("R2", "note=none drift=1%", "InvalidValue", "R2 has no note, and a drift is explained by one.")]
    [InlineData("R1", "drift=1% note=wet", "InvalidValue", "R1 has no note, and a drift is explained by one.")]
    [InlineData("R1", "note=wet colour=red", "UnknownField", "readings has no field colour; its fields are id, station, volume, unit, taken, checked, note, wind speed, wind direction, window, drift.")]
    [InlineData("R1", "wind=3", "AmbiguousField", "wind could be wind speed or wind direction of readings.")]
    [InlineData("R1", "station=south", "NotEditable", "station is not a field a command sets; readings takes volume, unit, checked, note, wind speed, wind direction and drift.")]
    [InlineData("R1", "note=wet note=dry", "FieldTwice", "note is given twice; give each field one value.")]
    [InlineData("R1", "note wet", "UnreadValues", "note wet says no value: values are written name=value, one or several.")]
    [InlineData("R1 note", "note=wet", "UnreadRows", "note is left over after the rows: rows are ids separated by commas, all, a subset, or where and its conditions.")]
    [InlineData("Q9", "note=wet", "UnknownRow", "No row Q9 in readings, stations or settings.")]
    [InlineData(null, "note=wet", "NoRows", "Say which rows: their ids, all, manual, automatic, or where and a condition; or name a table (readings, stations, settings).")]
    public async Task A_command_that_cannot_be_read_or_checked_writes_nothing(string? rows, string set, string code, string message)
    {
        var rig = new Rig();
        var before = rig.Board.Revision;

        var refused = await rig.SetRefused(rows, set);

        Assert.Equal((code, message, before), (refused.Code, refused.Message, rig.Board.Revision));
    }

    [Fact] // note 9: a row that holds the value gets no edit and is counted; a command that changes nothing keeps the revision and is done
    public async Task A_row_that_holds_the_value_is_counted_and_not_written()
    {
        var rig = new Rig();
        var before = rig.Board.Revision;

        var some = Told(await rig.Set("where station=north", "checked=on"));
        var none = Told(await rig.Set("R2, R5", "checked=on"));
        var one = Told(await rig.Set("R2", "checked=yes note=gusty"));
        var nobody = Told(await rig.Set("where wind speed>99", "checked=on"));

        Assert.Equal((before + 1, "Set R1 checked to on; 1 already held it.", 2, 1, 1, "R1"), (some.Revision, some.Said, some.Matched, some.Changed, some.Held, some.Rows));
        Assert.Equal((before + 1, null, "Nothing changed: R2 and R5 already hold checked on.", Stations.Readings, 2, 0, 2, "R2, R5"), none);
        Assert.Equal((before + 1, null, "Nothing changed: R2 already holds checked on and note gusty."), (one.Revision, one.Command, one.Said));
        Assert.Equal((before + 1, null, "Nothing changed: no row of readings matches wind speed > 99.", 0), (nobody.Revision, nobody.Command, nobody.Said, nobody.Matched));
        Assert.Equal("-", rig.Edit("reading:R2", "checked"));
    }

    [Fact] // ADR-0016 §2, note 9: a subset or all, not narrowed, writes the field's whole-set edit where it has one; a row's later edit wins
    public async Task A_whole_set_is_one_edit_and_a_rows_later_edit_wins()
    {
        var rig = new Rig();
        var before = rig.Board.Revision;

        var manual = Told(await rig.Set("manual", "checked=on"));
        var row = Told(await rig.Set("R1", "checked=off"));
        var every = Told(await rig.Set("all", "checked=on"));
        var again = Told(await rig.Set("all", "checked=on"));
        // A field with no whole-set edit is written row by row, whatever the rows were called.
        var notes = Told(await rig.Set("auto", "note=seen"));

        Assert.Equal((before + 1, "Set checked to on on R1 and R3; 1 already held it.", 3, 2, 1), (manual.Revision, manual.Said, manual.Matched, manual.Changed, manual.Held));
        Assert.Equal(("true", "-", "-"), (rig.Edit("readings:manual", "checked"), rig.Edit("reading:R3", "checked"), rig.Edit("reading:R2", "checked")));
        Assert.Equal((before + 2, "Set R1 checked to off."), (row.Revision, row.Said));
        // One edit for each subset that has a row to change: the manual one for R1, the automatic one for R4 and X1.
        Assert.Equal((before + 4, "Set checked to on on R1, R4 and X1; 3 already held it.", 6, 3, 3), (every.Revision, every.Said, every.Matched, every.Changed, every.Held));
        Assert.Equal((before + 4, null, "Nothing changed: all 6 readings already hold checked on."), (again.Revision, again.Command, again.Said));
        Assert.Equal((before + 7, "Set note to seen on R4, R5 and X1."), (notes.Revision, notes.Said));
    }

    [Fact] // ADR-0016 §6: the sentence says the value as it was read, and a table of one row needs no row
    public async Task The_sentence_says_the_value_as_read_and_a_table_of_one_row_needs_no_row()
    {
        var rig = new Rig();

        var named = Told(await rig.Set("settings", "alarm=5"));
        var unnamed = Told(await rig.Set(null, "daylight=no alarm volume=7.5"));
        var site = Told(await rig.Set("X1", "altitude=45", table: "stations"));
        var both = await rig.SetRefused("readings R1", "note=wet", table: "stations");

        Assert.Equal(("Set board alarm volume to 5mm.", Stations.Settings, "board"), (named.Said, named.Table, named.Rows));
        Assert.Equal(("Set board daylight to off and alarm volume to 7.5mm.", Stations.Settings), (unnamed.Said, unnamed.Table));
        Assert.Equal(("Set X1 altitude to 45.", Stations.Sites), (site.Said, site.Table));
        Assert.Equal(("TwoTables", "The rows are said to be of readings and the table to be stations."), both);
        Assert.Equal("set stations X1 altitude 45", rig.Board.Log[^1].Command?.Said);
    }

    [Fact] // ADR-0016 §11: one command, so `undo` takes the whole batch back, and `redo` puts it back
    public async Task Undo_takes_the_whole_batch_back()
    {
        var rig = new Rig();
        var before = rig.Board.Revision;
        var set = await rig.Set("where station=south", "note=wet wind speed=1");

        var undone = await rig.Done("workspace.undo", new JsonObject { ["command"] = set["command"]!.GetValue<string>() });
        var cleared = (rig.Edit("reading:R3", "note"), rig.Edit("reading:R4", "windSpeed"));
        var redone = await rig.Done("workspace.redo", new JsonObject { ["command"] = undone["command"]!.GetValue<string>() });

        Assert.Equal(("Took back: set where station=south note=wet wind speed=1. redo puts it back.", 4), (undone["said"]!.GetValue<string>(), undone["edits"]!.GetValue<int>()));
        Assert.Equal(("-", "-"), cleared);
        Assert.Equal(("\"wet\"", "1", before + 12), (rig.Edit("reading:R3", "note"), rig.Edit("reading:R4", "windSpeed"), redone["revision"]!.GetValue<long>()));
    }

    [Fact] // ADR-0016 §2 rule 2, ADR-0005 §2: a command carries the revision it was read at, acts for its caller, and records where it came from
    public async Task A_command_acts_at_the_revision_named_for_the_caller_the_server_names()
    {
        var rig = new Rig();
        var before = rig.Board.Revision;

        var moved = await rig.Refused("entity.set", new JsonObject { ["rows"] = "R1", ["set"] = "note=wet", ["revision"] = before - 1 });
        var other = await rig.Refused("entity.set", new JsonObject { ["rows"] = "R1", ["set"] = "note=wet" }, new Caller("ben", RunOrigin.Pricer));
        var unknown = await rig.Refused("entity.set", new JsonObject { ["rows"] = "R1", ["set"] = "note=wet", ["table"] = "tides" });
        var agent = Told(await rig.Done("entity.set", new JsonObject { ["rows"] = "R1", ["set"] = "note=wet", ["revision"] = before }, new Caller("ana", RunOrigin.Ai)));

        Assert.Equal(("RevisionMoved", $"The workspace is at revision {before}, not {before - 1}."), moved);
        Assert.Equal("NotOwner", other.Code);
        Assert.Equal(("UnknownTable", "No table tides: the tables are readings, stations, settings."), unknown);
        Assert.Equal((before + 1, RunOrigin.Ai), (agent.Revision, rig.Board.Log[^1].Origin));
    }

    [Fact] // ADR-0016 §3, note 10: reset removes the rows' own edits, of one field or of every field; a row with none is counted as such
    public async Task Reset_removes_the_edits_of_a_field_or_of_every_field()
    {
        var rig = new Rig();
        await rig.Set("R1, R3", "note=wet wind speed=1");
        var before = rig.Board.Revision;

        var field = Told(await rig.Done("entity.reset", Rig.Request("R1, R2", null, ("field", "note"))));
        var every = Told(await rig.Done("entity.reset", Rig.Request("where station=south", null)));
        var none = Told(await rig.Done("entity.reset", Rig.Request("R2, R4", null, ("field", "wind speed"))));
        var nothing = Told(await rig.Done("entity.reset", Rig.Request("R4", null)));

        Assert.Equal((before + 1, $"c{before + 1}", "Reset R1 note; 1 had no edit.", Stations.Readings, 2, 1, 1, "R1"), field);
        Assert.Equal((before + 3, $"c{before + 2}", "Reset R3; 1 had no edit.", Stations.Readings, 2, 1, 1, "R3"), every);
        Assert.Equal((before + 3, null, "Nothing changed: R2 and R4 have no edit of wind speed.", Stations.Readings, 2, 0, 2, "R2, R4"), none);
        Assert.Equal((before + 3, null, "Nothing changed: R4 has no edit."), (nothing.Revision, nothing.Command, nothing.Said));
        Assert.Equal(("-", "1", "-", "-"), (rig.Edit("reading:R1", "note"), rig.Edit("reading:R1", "windSpeed"), rig.Edit("reading:R3", "note"), rig.Edit("reading:R3", "windSpeed")));
        Assert.Equal("reset where station=south", rig.Board.Log[^1].Command?.Said);

        // ADR-0016 §11: a reset is one command, so undo puts back the edits it removed.
        var undone = await rig.Done("workspace.undo", new JsonObject { ["command"] = every.Command });

        Assert.Equal("Took back: reset where station=south. redo puts it back.", undone["said"]!.GetValue<string>());
        Assert.Equal(("\"wet\"", "1"), (rig.Edit("reading:R3", "note"), rig.Edit("reading:R3", "windSpeed")));
    }

    [Fact] // note 10: over a subset whose field has a whole-set edit, reset removes that edit and each row's own
    public async Task Reset_over_a_subset_removes_the_whole_set_edit_and_each_rows_own()
    {
        var rig = new Rig();
        await rig.Set("manual", "checked=on");
        await rig.Set("R1", "checked=off note=wet");
        await rig.Set("readings X1", "checked=on");
        var before = rig.Board.Revision;

        var manual = Told(await rig.Done("entity.reset", Rig.Request("manual", null, ("field", "checked"))));
        var after = (rig.Edit("readings:manual", "checked"), rig.Edit("reading:R1", "checked"), rig.Edit("reading:R1", "note"), rig.Edit("reading:X1", "checked"));
        var all = Told(await rig.Done("entity.reset", Rig.Request("all", null)));

        // The whole-set edit and R1's own: two entries of one command, and every row of the subset follows what was sent again.
        Assert.Equal((before + 2, "Reset checked on R1, R2 and R3.", 3, 3, 0), (manual.Revision, manual.Said, manual.Matched, manual.Changed, manual.Held));
        Assert.Equal(("-", "-", "\"wet\"", "true"), after);
        Assert.Equal((before + 4, "Reset R1 and X1; 4 had no edit.", 6, 2, 4), (all.Revision, all.Said, all.Matched, all.Changed, all.Held));
        Assert.Empty(rig.Board.Overrides);
    }

    [Fact] // ADR-0016 §3: a table of one row is reset as it is set, by its words or by a field's
    public async Task A_table_of_one_row_is_reset_without_a_row()
    {
        var rig = new Rig();
        await rig.Set(null, "alarm=5 daylight=off");

        var field = Told(await rig.Done("entity.reset", Rig.Request(null, null, ("field", "alarm"))));
        var whole = Told(await rig.Done("entity.reset", Rig.Request("settings", null)));
        var unknown = await rig.Refused("entity.reset", Rig.Request("R1", null, ("field", "colour")));
        var unsaid = await rig.Refused("entity.reset", Rig.Request(null, null));

        Assert.Equal(("Reset board alarm volume.", Stations.Settings), (field.Said, field.Table));
        Assert.Equal("Reset board.", whole.Said);
        Assert.Equal("UnknownField", unknown.Code);
        Assert.Equal("NoRows", unsaid.Code);
    }

    [Fact] // ADR-0016 §3, note 10: remove takes rows the workspace made, with their edits; undo brings the row back as it was
    public async Task Remove_takes_a_row_the_workspace_made_and_undo_brings_it_back()
    {
        var rig = new Rig();
        Stations.Made(rig.Workspaces, rig.Board, "R7");
        await rig.Set("R7", "note=wet");
        var before = rig.Board.Revision;

        var removed = Told(await rig.Done("entity.remove", Rig.Request("R7", null)));
        var gone = (rig.Board.Created.ContainsKey("reading:R7"), rig.Edit("reading:R7", "note"));
        var undone = await rig.Done("workspace.undo", new JsonObject { ["command"] = removed.Command });

        Assert.Equal((before + 1, $"c{before + 1}", "Removed R7.", Stations.Readings, 1, 1, 0, "R7"), removed);
        Assert.Equal((false, "-"), gone);
        Assert.Equal("Took back: remove R7. redo puts it back.", undone["said"]!.GetValue<string>());
        Assert.Equal((true, "\"wet\""), (rig.Board.Created.ContainsKey("reading:R7"), rig.Edit("reading:R7", "note")));
    }

    [Fact] // note 10: a row the workspace did not make is refused by name, saying what to do instead, and nothing is removed
    public async Task Remove_refuses_a_row_the_workspace_did_not_make()
    {
        var rig = new Rig();
        Stations.Made(rig.Workspaces, rig.Board, "R7");
        var before = rig.Board.Revision;

        var sent = await rig.Refused("entity.remove", Rig.Request("R7, R1", null));
        var unsaid = await rig.Refused("entity.remove", Rig.Request(null, null));
        var nobody = Told(await rig.Done("entity.remove", Rig.Request("where wind speed>99", null)));

        Assert.Equal(("NotRemovable", "R1 is a reading a station sent, and stays; set R1 checked off leaves it out."), sent);
        Assert.Equal("NoRows", unsaid.Code);
        Assert.Equal("Nothing changed: no row of readings matches wind speed > 99.", nobody.Said);
        Assert.Equal((before, true), (rig.Board.Revision, rig.Board.Created.ContainsKey("reading:R7")));
    }
}
