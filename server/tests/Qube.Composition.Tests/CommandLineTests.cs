using System.Text.Json.Nodes;
using Qube.Composition.Application;
using Qube.Composition.Application.Tables;
using Qube.Composition.Application.Workspaces;

namespace Qube.Composition.Tests;

// ADR-0016 §4, §5 and its notes 11 and 15 of 2026-11-11: a command says how it is typed, and the server reads a line
// into the command, its inputs, its rows and what would change, or the word it cannot read, and executes nothing. The
// registration is station readings, with no financial word.
public class CommandLineTests
{
    sealed class Rig
    {
        public WorkspaceService Workspaces { get; } = Stations.Service();
        public CompositionRegistry Registry { get; }
        public CommandService Commands { get; }
        public CommandLines Lines { get; }
        public string Id { get; }

        public Rig()
        {
            var register = new TableRegister([new Stations.Catalog()]);
            var tables = new TableService(register, Workspaces);
            Registry = new CompositionRegistry([new TablesModule(() => tables), new WorkspaceCommandsModule(() => Workspaces)]);
            Commands = new CommandService(Registry, new InMemoryDataStore());
            Lines = new CommandLines(Registry, Workspaces, register);
            Id = Stations.Board(Workspaces).WorkspaceId;
        }

        public WorkspaceState Board => Workspaces.Find(Id)!;

        public LinePreview Preview(string line, LineView? view = null, string user = "ana") => Lines.Preview(line, Id, null, new Caller(user, RunOrigin.Pricer), view);

        /// <summary>The token of the order the readings' reader gives, as a page holds it from the reader's last answer.</summary>
        public string Order(string? subset, IReadOnlyList<ViewSort> sorts) =>
            new Stations.Catalog().Tables.Single(t => t.TableId == Stations.Readings).InOrder(Board, subset, sorts)!.Token;

        /// <summary>Invokes the command a preview read, with the inputs it answered, as Enter does.</summary>
        public async Task<JsonObject> Run(LinePreview preview, RunOrigin origin = RunOrigin.Pricer)
        {
            var result = await Commands.InvokeAsync(preview.Command!.Id, preview.Command.Version, preview.Inputs!, new Caller("ana", origin), Guid.NewGuid().ToString(), default);
            return result is CommandResult.Done done ? done.Outputs["answer"]!.AsObject()
                : throw new Xunit.Sdk.XunitException($"{preview.Command.Id} was refused: {string.Join(" ", ((CommandResult.Refused)result).Issues.Select(i => i.Message))}");
        }

        public async Task<string> Said(string line) => (await Run(Preview(line)))["said"]!.GetValue<string>();
    }

    static string Read(LinePreview preview) =>
        preview.Refused is { } refused ? $"{refused.Code}: {refused.Sentence}" : $"{preview.Rows?.Title} | {preview.Would}";

    [Fact] // ADR-0016 §4: a command capability says how it is typed, its words and its arguments
    public void The_edit_verbs_and_undo_and_redo_say_how_they_are_typed()
    {
        var rig = new Rig();
        string Form(string id) => rig.Registry.CapabilityDescriptors.Single(c => c.CapabilityId == id).Command is { } form
            ? $"{string.Join("|", form.Words)}: {string.Join(", ", form.Arguments.Select(a => $"{a.Name} {a.Kind}{(a.Required ? "" : "?")}"))}"
            : "none";

        Assert.Equal("set: rows Rows?, set Assignments", Form("entity.set"));
        Assert.Equal("reset: rows Rows?, field Field?", Form("entity.reset"));
        Assert.Equal("remove: rows Rows", Form("entity.remove"));
        Assert.Equal("undo: ", Form("workspace.undo"));
        Assert.Equal("redo: ", Form("workspace.redo"));
    }

    [Fact] // ADR-0016 §5, note 11: a line is read into its command, the inputs to invoke it with, its rows and what would change, and nothing is written
    public async Task A_line_is_read_into_its_command_its_rows_and_what_would_change_and_nothing_is_written()
    {
        var rig = new Rig();
        var before = rig.Board.Revision;

        var preview = rig.Preview("set where station=north checked on");

        Assert.Equal(("entity.set", "1.0.0"), (preview.Command!.Id, preview.Command.Version));
        Assert.Equal("rows: where station=north (Typed); set: checked on (Typed)", Given(preview));
        Assert.Equal(("weather.readings", "readings, 2 rows", 2), (preview.Rows!.Table, preview.Rows.Title, preview.Rows.Count));
        Assert.Equal(["R1", "R2"], preview.Rows.First);
        Assert.Equal("checked: 1 off to on, 1 already on", preview.Would);
        Assert.Null(preview.Refused);
        Assert.Equal((before, before), (preview.Revision, rig.Board.Revision));
        Assert.Empty(rig.Board.Overrides);

        // Enter runs the reading it was shown: the inputs go to the command as they are.
        var ran = await rig.Run(preview);
        Assert.Equal("Set R1 checked to on; 1 already held it.", ran["said"]!.GetValue<string>());
        Assert.Equal(before + 1, rig.Board.Revision);
    }

    [Fact] // ADR-0016 §5, note 15: a table of one row is said by its word alone; its row has no id a line could name, so none is listed
    public void The_rows_of_a_table_of_one_row_are_its_word_and_name_no_row()
    {
        var rows = new Rig().Preview("set daylight off").Rows!;

        Assert.Equal(("weather.settings", "settings", 1), (rows.Table, rows.Title, rows.Count));
        Assert.Empty(rows.First);
    }

    [Theory] // ADR-0016 §5, note 15: each spelling of set is read as the command reads it, and says its rows and what would change
    [InlineData("set R1, R2 note=wet drift=1%", "readings, 2 rows | note: 2 to wet; drift: 2 empty to 1%")]
    [InlineData("set all checked on", "readings, 6 rows | checked: 4 off to on, 2 already on")]
    [InlineData("set automatic unit gal", "readings, 3 rows | unit: 2 M3 to GAL, 1 already GAL")]
    [InlineData("set stations N1 altitude 130", "stations, 1 row | altitude: 120 to 130")]
    [InlineData("set alarm 5mm", "settings | alarm volume: 10mm to 5mm")]
    [InlineData("set settings daylight off", "settings | daylight: on to off")]
    [InlineData("SET  r2   checked   on", "readings, 1 row | Nothing would change: R2 already holds checked on.")]
    [InlineData("set where wind speed>99 checked on", "readings, 0 rows | Nothing would change: no row of readings matches wind speed > 99.")]
    [InlineData("reset R1 note", "readings, 1 row | Nothing would change: R1 has no edit of note.")]
    [InlineData("remove R1", "NotRemovable: R1 is a reading a station sent, and stays; set R1 checked off leaves it out.")]
    [InlineData("set", "NoRows: Say which rows: their ids, all, manual, automatic, or where and a condition; or name a table (readings, stations, settings).")]
    [InlineData("set R1", "UnreadValues: A field and its value are missing: one value is written after its field (<field> <value>), and several as name=value.")]
    [InlineData("set R1 checked", "UnreadValues: checked has no value: one value is written after its field (<field> <value>), and several as name=value.")]
    [InlineData("remove R1 now", "UnreadRows: now is left over after the rows: rows are ids separated by commas, all, a subset, or where and its conditions.")]
    public void A_line_says_its_rows_and_what_would_change_or_why_it_cannot_be_read(string line, string read) =>
        Assert.Equal(read, Read(new Rig().Preview(line)));

    [Fact] // ADR-0016 §5, note 15: reset and remove are previewed against the edits and the rows the workspace has, and Enter does what was shown
    public async Task Reset_and_remove_say_what_they_would_do_to_the_edits_and_rows_there_are()
    {
        var rig = new Rig();
        await rig.Said("set R1, R2 note=wet");
        Stations.Made(rig.Workspaces, rig.Board, "M1");
        var before = rig.Board.Revision;

        var one = rig.Preview("reset R1, R2, R3 note");
        var whole = rig.Preview("reset R1");
        var made = rig.Preview("remove M1");

        Assert.Equal("readings, 3 rows | note: 2 reset, 1 with no edit", Read(one));
        Assert.Equal(["rows: R1, R2, R3", "field: note"], one.Arguments!.Select(a => $"{a.Name}: {a.Text}"));
        Assert.Equal("readings, 1 row | every field: reset", Read(whole));
        Assert.Equal("readings, 1 row | removed, with its edits", Read(made));
        Assert.Equal(before, rig.Board.Revision);
        Assert.Equal("Removed M1.", (await rig.Run(made))["said"]!.GetValue<string>());
        // A preview is of the revision it read: once the workspace has moved, what it answered is refused and nothing is written.
        var moved = await rig.Commands.InvokeAsync(one.Command!.Id, one.Command.Version, one.Inputs!, new Caller("ana", RunOrigin.Pricer), Guid.NewGuid().ToString(), default);
        Assert.Equal("RevisionMoved", Assert.IsType<CommandResult.Refused>(moved).Code);
        Assert.Equal("Reset note on R1 and R2; 1 had no edit.", await rig.Said("reset R1, R2, R3 note"));
    }

    static string Given(LinePreview preview) => string.Join("; ", preview.Arguments!.Select(a => $"{a.Name}: {a.Text} ({a.From})"));

    static (string? Table, string? Rows) Asked(LinePreview preview) =>
        (preview.Inputs!["request"]!["table"]?.GetValue<string>(), preview.Inputs["request"]!["rows"]?.GetValue<string>());

    [Fact] // ADR-0016 §2, note 17: the preview resolves `selected` with the view; what it answers runs as rows by key, at the revision read
    public async Task A_line_that_says_selected_answers_rows_by_key_and_running_it_writes_those_rows_and_no_other()
    {
        var rig = new Rig();
        var before = rig.Board.Revision;

        var preview = rig.Preview("set selected checked on", new LineView(Stations.Readings, "manual", Keys: ["R1", "R3"]));

        Assert.Equal("rows: selected (Typed); set: checked on (Typed)", Given(preview));
        Assert.Equal(("readings", "R1, R3"), Asked(preview));
        Assert.Equal(("weather.readings", "readings, 2 rows", "checked: 2 off to on"), (preview.Rows!.Table, preview.Rows.Title, preview.Would));
        Assert.Equal(["R1", "R3"], preview.Rows.First);
        Assert.Equal(before, rig.Board.Revision);

        Assert.Equal("Set checked to on on R1 and R3.", (await rig.Run(preview))["said"]!.GetValue<string>());
        Assert.Equal(["reading:R1", "reading:R3"], rig.Board.Overrides.Keys.Select(t => t.Entity).Order());
        // The log names rows, and knows no screen.
        Assert.Equal("Takes back: set readings R1, R3 checked on (you, 2 edits)", rig.Preview("undo").Would);
    }

    [Fact] // ADR-0016 §2, note 17: `where` narrows the rows of a screen before they are named; positions are read in the order the grid was drawn from
    public void The_rows_of_a_screen_are_narrowed_and_read_by_position_before_they_are_named()
    {
        var rig = new Rig();
        ViewSort[] largest = [new("volume", Descending: true)];
        var paged = new LineView(Stations.Readings, "manual", largest, rig.Order("manual", largest), Ranges: [new(0, 2)]);

        var narrowed = rig.Preview("set selected where unit=m3 checked on", new LineView(Stations.Readings, Keys: ["R1", "R3", "R4"]));
        var positions = rig.Preview("reset selected note", paged);
        var shown = rig.Preview("set shown where checked=off note wet", new LineView(Stations.Readings, "manual"));
        var moved = rig.Preview("set selected checked on", paged with { Order = "an earlier order" });
        var none = rig.Preview("set selected where unit=gal checked on", new LineView(Stations.Readings, Keys: ["R1"]));
        var other = rig.Preview("set shown altitude 130", new LineView(Stations.Sites, "high"));

        Assert.Equal((("readings", "R1"), "rows: selected where unit=m3 (Typed); set: checked on (Typed)"), (Asked(narrowed), Given(narrowed)));
        Assert.Equal(("readings", "R3, R1"), Asked(positions));
        Assert.Equal((("readings", "R1, R3"), "note: 2 empty to wet"), (Asked(shown), shown.Would));
        Assert.Equal(("ViewMoved", "selected", 4), (moved.Refused!.Code, moved.Refused.Word, moved.Refused.Start));
        Assert.Equal("NoRowMatches: No selected row of readings matches unit = GAL.", Read(none));
        Assert.Equal(("stations", "N1"), Asked(other));
    }

    [Fact] // ADR-0016 §9, note 19: every preview asked with a view says the view in one sentence, whatever the line; with no view it says none
    public void A_preview_asked_with_a_view_says_the_view_in_one_sentence()
    {
        var rig = new Rig();
        ViewSort[] largest = [new("volume", Descending: true)];
        var paged = new LineView(Stations.Readings, "manual", largest, rig.Order("manual", largest), Ranges: [new(0, 1), new(2, 1)], Focus: new("windSpeed", Position: 1));
        string? Said(LineView? view, string line = "") => rig.Preview(line, view).View;

        // The line asks once as it opens, with no text: the answer is the sentence, and the commands there are.
        var opened = rig.Preview("", new LineView(Stations.Readings, "manual", Keys: ["R1", "R3"], Focus: new("note", Key: "R1")));

        Assert.Equal(("readings (manual), 2 selected, R1 · note", "UnknownCommand"), (opened.View, opened.Refused!.Code));
        Assert.Equal("readings (manual), 2 selected, R1 · wind speed", Said(paged));
        Assert.Equal("readings (manual), 2 selected, R1 · wind speed", Said(paged, "set R9 colour red"));
        Assert.Equal("readings, 1 selected", Said(new LineView(Stations.Readings, Keys: ["R5"])));
        Assert.Equal("stations (high), nothing selected", Said(new LineView(Stations.Sites, "high")));
        // A column of the grid that is no field of the table is a place the keyboard can be, and names no field.
        Assert.Equal("readings, nothing selected, R2", Said(new LineView(Stations.Readings, Focus: new("age", Key: "R2"))));
        Assert.Equal("readings (manual), no longer in the order it was shown in", Said(paged with { Order = "an earlier order" }));
        Assert.Null(Said(null, "set R1 checked on"));
    }

    [Theory] // ADR-0016 §5, note 17: where rows are awaited, `selected` and `shown` are offered with their counts, only where the view has them
    [InlineData("set ", "R1,R3", "all | selected (2 rows) | shown (3 rows) | manual | automatic | where | readings")]
    [InlineData("set s", "R1,R3", "selected@4 (2 rows) | shown@4 (3 rows) | stations@4 | settings@4")]
    [InlineData("set ", "", "all | shown (3 rows) | manual | automatic | where | readings")]
    [InlineData("set readings ", "R1", "all | selected (1 row) | shown (3 rows) | manual | automatic | where | R1 | R2")]
    [InlineData("set stations ", "R1", "all | high | where | N1 | S1 | X1")]
    [InlineData("set selected ", "R1", "volume | unit | checked | note | wind speed | wind direction | drift | where")]
    [InlineData("set shown where ", "R1", "id | station | volume | unit | taken | checked | note | wind speed | wind direction | window | drift")]
    [InlineData("set selected where unit=", "R1", "unit=M3@19 | unit=GAL@19")]
    [InlineData("remove sel", "R1", "selected@7 (1 row)")]
    public void Where_rows_are_awaited_the_rows_of_the_screen_are_offered_with_their_counts(string line, string selected, string offered)
    {
        var keys = selected.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var preview = new Rig().Preview(line, new LineView(Stations.Readings, "manual", Keys: keys));

        var all = preview.Complete.Select(c => $"{c.Value}{(c.Start == line.Length ? "" : $"@{c.Start}")}{(c.Detail is null ? "" : $" ({c.Detail})")}").ToList();

        // The first of them: the tables' rows follow as they did.
        Assert.Equal(offered, string.Join(" | ", all.Take(offered.Split(" | ").Length)));
    }

    [Fact] // ADR-0016 §11, note 17: a command of many rows by id is named by its first three and how many more there are
    public async Task A_command_of_many_rows_is_named_by_its_first_three_and_how_many_more()
    {
        var rig = new Rig();

        await rig.Said("set R1, R2, R3, R4, R5 checked on");
        var typed = rig.Preview("undo").Would;
        await rig.Run(rig.Preview("undo"));
        await rig.Run(rig.Preview("set selected where wind speed>1 note wet", new LineView(Stations.Readings, Keys: ["R1", "R2", "R3", "R4", "R5"])));

        Assert.Equal("Takes back: set R1, R2, R3 and 2 more checked on (you, 3 edits)", typed);
        Assert.Equal("Takes back: set readings R1, R2, R3 and 2 more note wet (you, 5 edits)", rig.Preview("undo").Would);
        // Three are named whole, and a condition after them stays as it was written.
        Assert.Equal("Set note to dry on R1, R3 and R4.", await rig.Said("set R1, R2, R3, R4 where checked=off and unit in m3, gal note dry"));
        Assert.Equal("Takes back: set R1, R2, R3 and 1 more where checked=off and unit in m3, gal note dry (you, 3 edits)", rig.Preview("undo").Would);
    }

    [Theory] // ADR-0016 §5, §6: a line that cannot be read answers the word at fault, where it stands in the line, and what it could have been
    [InlineData("set R1 colour red", "UnknownField", "colour", 7, 13, "id, station, volume, unit, taken, checked, note, wind speed, wind direction, window, drift")]
    [InlineData("set readings 1 checked on", "AmbiguousRow", "1", 13, 14, "R1, X1")]
    [InlineData("set R1 volume lots", "InvalidValue", "lots", 14, 18, "")]
    [InlineData("set R1 wind 4", "AmbiguousField", "wind", 7, 11, "wind speed, wind direction")]
    [InlineData("set where unit==m3 checked on", "UnknownOperator", "==", 14, 16, "")]
    [InlineData("sett R1", "UnknownCommand", "sett", 0, 4, "set, reset, remove, undo, redo")]
    [InlineData("undo that", "UnreadWords", "that", 5, 9, "")]
    public void A_line_that_cannot_be_read_says_the_word_its_place_and_what_it_could_be(string line, string code, string word, int start, int end, string candidates)
    {
        var rig = new Rig();
        var before = rig.Board.Revision;

        var refused = rig.Preview(line).Refused!;

        Assert.Equal((code, word, start, end, candidates), (refused.Code, refused.Word, refused.Start, refused.End, string.Join(", ", refused.Candidates)));
        Assert.Equal(word, line[start..end]);
        Assert.Equal(before, rig.Board.Revision);
    }

    [Fact] // ADR-0016 §5, note 15: a word at fault that stands twice in the line is named and given no place, never the place of the other
    public void A_word_at_fault_that_stands_twice_in_the_line_is_given_no_place()
    {
        var refused = new Rig().Preview("set R1 note=lots volume=lots").Refused!;

        Assert.Equal(("InvalidValue", "lots", null, null), (refused.Code, refused.Word, refused.Start, refused.End));
    }

    [Fact] // ADR-0016 §5, §11: undo and redo say the command they would act on, whose it was and when, name it in their inputs, and take back that one
    public async Task Undo_and_redo_say_the_command_they_would_act_on_and_name_it()
    {
        var rig = new Rig();
        var nothing = rig.Preview("undo");
        await rig.Said("set R1 checked on");
        var command = rig.Board.Log[^1].Command!.Id;
        var at = rig.Board.Log[^1].At;

        var undo = rig.Preview("undo");
        var none = rig.Preview("redo");
        var took = await rig.Run(undo);
        var redo = rig.Preview("Redo ");

        Assert.Equal("NothingToUndo: There is nothing to take back: the workspace is as it was made.", Read(nothing));
        Assert.Equal(("workspace.undo", "Takes back: set R1 checked on (you, 1 edit)", at), (undo.Command!.Id, undo.Would, undo.At));
        Assert.Equal(command, undo.Inputs!["request"]!["command"]!.GetValue<string>());
        Assert.Null(undo.Rows);
        Assert.Equal("NothingToRedo: There is nothing to put back: no command was taken back since the last one ran.", Read(none));
        Assert.Equal("Took back: set R1 checked on. redo puts it back.", took["said"]!.GetValue<string>());
        Assert.Equal(("workspace.redo", "Puts back: set R1 checked on (you, 1 edit)"), (redo.Command!.Id, redo.Would));
        // A command the AI ran for the user is said to be the AI's: what undo would take back is not the user's own typing.
        await rig.Run(rig.Preview("set R2 note=wet"), RunOrigin.Ai);
        Assert.Equal("Takes back: set R2 note wet (the AI for you, 1 edit)", rig.Preview("undo").Would);
        // Another user's workspace is not read at all.
        Assert.Equal("NotOwner", Assert.Throws<WorkspaceRefused>(() => rig.Preview("undo", user: "ben")).Code);
    }

    // What could follow, each with the place it is written from where that is not the line's end, and what it is where it says.
    static string Offered(string line)
    {
        var preview = new Rig().Preview(line);
        var offered = preview.Complete.Select(c => $"{c.Value}{(c.Start == line.Length ? "" : $"@{c.Start}")}{(c.Detail is null ? "" : $" ({c.Detail})")}");
        return string.Join(" | ", preview.More > 0 ? offered.Append($"+{preview.More}") : offered);
    }

    [Theory] // ADR-0016 §5, note 15: where rows are awaited, the ways to say them: all, the subsets, where, the tables, a one-row table's fields, and rows
    [InlineData("set ", "all | manual | automatic | where | readings | stations | settings | alarm volume (settings) | daylight (settings) | R1 (readings) | R2 (readings) | R3 (readings) | R4 (readings) | R5 (readings) | readings X1 | N1 (stations) | S1 (stations) | stations X1")]
    [InlineData("set R", "readings@4 | R1@4 (readings) | R2@4 (readings) | R3@4 (readings) | R4@4 (readings) | R5@4 (readings)")]
    // A row is said by the end of its id too, and one that two tables have is offered with its table.
    [InlineData("set 1", "R1@4 (readings) | readings X1@4 | N1@4 (stations) | S1@4 (stations) | stations X1@4")]
    [InlineData("set al", "all@4 | alarm volume@4 (settings)")]
    [InlineData("set stations ", "all | high | where | N1 | S1 | X1")]
    [InlineData("set R1, ", "R2 | R3 | R4 | R5")]
    [InlineData("set R1,r", "R1,R2@4 | R1,R3@4 | R1,R4@4 | R1,R5@4")]
    [InlineData("remove ", "all | manual | automatic | where | readings | stations | settings | R1 (readings) | R2 (readings) | R3 (readings) | R4 (readings) | R5 (readings) | readings X1 | N1 (stations) | S1 (stations) | stations X1")]
    public void Where_rows_are_awaited_the_ways_to_say_them_are_offered(string line, string offered) => Assert.Equal(offered, Offered(line));

    [Theory] // ADR-0016 §5, note 15: after the rows, the fields the verb takes; after a field, the values it has to choose from
    [InlineData("set R1 ", "volume | unit | checked | note | wind speed | wind direction | drift")]
    [InlineData("set all ", "volume | unit | checked | note | wind speed | wind direction | drift | where")]
    [InlineData("set R1 w", "wind speed@7 | wind direction@7")]
    [InlineData("set R1 wind ", "wind speed@7 | wind direction@7")]
    [InlineData("set R1 wind sp", "wind speed@7")]
    [InlineData("set R1 ok", "checked@7")]
    [InlineData("set R1 unit ", "M3 | GAL")]
    [InlineData("set R1 unit g", "GAL@12")]
    [InlineData("set R1 checked ", "on | off")]
    [InlineData("set R1 note ", "empty")]
    [InlineData("set R1 volume ", "")]
    [InlineData("set R1 note very ", "")]
    [InlineData("set settings ", "alarm volume | daylight")]
    [InlineData("set daylight ", "on | off")]
    // A field that points into a table takes one of its rows.
    [InlineData("set stations S1 backup ", "N1 | S1 | X1 | empty")]
    // Several values are written with their signs, and a field is given once.
    [InlineData("set R1 unit=", "unit=M3@7 | unit=GAL@7")]
    [InlineData("set R1 note=wet ", "volume= | unit= | checked= | wind speed= | wind direction= | drift=")]
    [InlineData("set R1 note=wet unit=g", "unit=GAL@16")]
    [InlineData("reset R1 ", "volume | unit | checked | note | wind speed | wind direction | drift")]
    [InlineData("reset R1 note ", "")]
    [InlineData("remove R1 ", "")]
    public void After_the_rows_the_verbs_fields_and_a_fields_values_are_offered(string line, string offered) => Assert.Equal(offered, Offered(line));

    [Theory] // ADR-0016 §5, note 15: in a condition, the fields, then the operators its field takes, then the field's values, then and or the verb's words
    [InlineData("set where ", "id | station | volume | unit | taken | checked | note | wind speed | wind direction | window | drift")]
    [InlineData("set where u", "unit@10")]
    [InlineData("set where wind ", "wind speed@10 | wind direction@10")]
    [InlineData("set where unit ", "= | != | ~ | in")]
    [InlineData("set where volume ", "= | != | < | <= | > | >= | in")]
    [InlineData("set where wind speed ", "= | != | < | <= | > | >= | in")]
    [InlineData("set where checked ", "= | != | in")]
    [InlineData("set where unit=", "unit=M3@10 | unit=GAL@10")]
    [InlineData("set where UNIT=g", "UNIT=GAL@10")]
    [InlineData("set where unit = ", "M3 | GAL")]
    [InlineData("set where unit in m3, ", "GAL")]
    [InlineData("set where checked=", "checked=on@10 | checked=off@10")]
    [InlineData("set where note=", "note=empty@10")]
    [InlineData("set where volume>", "")]
    [InlineData("set where unit=m3 ", "and | volume | unit | checked | note | wind speed | wind direction | drift")]
    [InlineData("set where unit=m3 and ", "id | station | volume | unit | taken | checked | note | wind speed | wind direction | window | drift")]
    [InlineData("set where unit=m3 and ch", "checked@22")]
    [InlineData("set where unit=m3 ch", "checked@18")]
    [InlineData("remove where unit=m3 ", "and")]
    [InlineData("set stations where backup=", "backup=N1@19 | backup=S1@19 | backup=X1@19 | backup=empty@19")]
    public void In_a_condition_fields_operators_and_values_are_offered(string line, string offered) => Assert.Equal(offered, Offered(line));

    [Theory] // ADR-0016 §5: a command half typed is completed, and one that takes no argument is followed by nothing
    [InlineData("und", "undo@0")]
    [InlineData("re", "reset@0 | remove@0 | redo@0")]
    [InlineData("undo ", "")]
    [InlineData("set", "")]
    public void A_command_half_typed_is_completed(string line, string offered) => Assert.Equal(offered, Offered(line));

    [Fact] // ADR-0016 §5, note 15: rows are offered a few at a time, with how many more there are
    public void Rows_are_offered_a_few_at_a_time_with_how_many_more_there_are()
    {
        var rig = new Rig();
        for (var made = 1; made <= 25; made++) Stations.Made(rig.Workspaces, rig.Board, $"M{made:00}");

        var preview = rig.Preview("set M");

        Assert.Equal(["manual", .. Enumerable.Range(1, 20).Select(n => $"M{n:00}")], preview.Complete.Select(c => c.Value));
        Assert.Equal(5, preview.More);
    }

    // A command that says how it is typed and reads no line, and one that reads its own.
    class Typed(string id, string words) : ICapabilityHandler
    {
        public Qube.Composition.Contracts.CapabilityDescriptor Descriptor { get; } =
            new(id, "1.0.0", id, [], [], Qube.Composition.Contracts.ExecutionKind.RequestResponse, [Qube.Composition.Contracts.CapabilityEffect.EditWorkspace], new([words], []));

        public Task<IReadOnlyDictionary<string, JsonNode?>> InvokeAsync(CapabilityInvocation invocation, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    sealed class Reading(string id, string words) : Typed(id, words), ILineCommand
    {
        public LineRead Read(LineContext line) => new(new JsonObject(), [], null, $"{Descriptor.CapabilityId} would run");
        public LineCompletions Complete(LineContext line) => LineCompletions.None;
    }

    sealed class Commands(params ICapabilityHandler[] handlers) : IQubeModule
    {
        public void Register(CompositionRegistryBuilder builder)
        {
            foreach (var handler in handlers) builder.AddCapability(handler);
        }
    }

    [Fact] // ADR-0016 §4: a command's words are its own and it reads its own line, both checked at start; of two commands the longer words are read first
    public void Words_two_commands_share_and_a_typed_command_that_reads_no_line_fail_at_start()
    {
        var workspaces = Stations.Service();
        CommandLines Lines(params ICapabilityHandler[] handlers) => new(new CompositionRegistry([new Commands(handlers)]), workspaces, new TableRegister([]));

        var shared = Assert.Throws<InvalidOperationException>(() => Lines(new Reading("gauge.add", "Add"), new Reading("gauge.put", "add")));
        var unread = Assert.Throws<InvalidOperationException>(() => Lines(new Typed("gauge.add", "add")));
        var longer = Lines(new Reading("gauge.add", "add"), new Reading("gauge.each", "add each")).Preview("add each", Stations.Board(workspaces).WorkspaceId, null, new Caller("ana", RunOrigin.Pricer));

        Assert.Equal("gauge.add and gauge.put answer to the same words, add.", shared.Message);
        Assert.Equal("gauge.add says how it is typed and does not read its line.", unread.Message);
        Assert.Equal(("gauge.each", "gauge.each would run"), (longer.Command!.Id, longer.Would));
    }
}
