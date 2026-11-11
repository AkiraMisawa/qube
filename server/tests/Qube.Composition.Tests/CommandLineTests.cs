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

        public Rig(TableDescriptor? stations = null)
        {
            var register = new TableRegister([new Stations.Catalog(stations)]);
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
    [InlineData("set alarm=5mm daylight=off", "settings | alarm volume: 10mm to 5mm; daylight: on to off")]
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

        Assert.Equal(("readings (manual), 2 selected, R1 · note", false, "UnknownCommand"), (opened.View, opened.ViewMoved, opened.Refused!.Code));
        Assert.Equal("readings (manual), 2 selected, R1 · wind speed", Said(paged));
        Assert.Equal("readings (manual), 2 selected, R1 · wind speed", Said(paged, "set R9 colour red"));
        Assert.Equal("readings, 1 selected", Said(new LineView(Stations.Readings, Keys: ["R5"])));
        Assert.Equal("stations (high), nothing selected", Said(new LineView(Stations.Sites, "high")));
        // A column of the grid that is no field of the table is a place the keyboard can be, and names no field.
        Assert.Equal("readings, nothing selected, R2", Said(new LineView(Stations.Readings, Focus: new("age", Key: "R2"))));
        Assert.Equal("readings (manual), no longer in the order it was shown in", Said(paged with { Order = "an earlier order" }));
        // Said apart from the sentence too, for a client to draw as what is stale: the screen's rows can no longer be said.
        Assert.Equal((true, false), (rig.Preview("", paged with { Order = "an earlier order" }).ViewMoved, rig.Preview("", paged).ViewMoved));
        // Positions the order has no rows at are of another order, whatever its token: none is counted as selected.
        Assert.Equal("readings (manual), no longer in the order it was shown in", Said(paged with { Ranges = [new(2, 2)] }));
        Assert.Equal("readings (manual), no longer in the order it was shown in", Said(paged with { Ranges = [new(int.MaxValue, 2)] }));
        Assert.Null(Said(null, "set R1 checked on"));
    }

    [Fact] // ADR-0016 §9, note 16: a view the workspace cannot have is the page's fault, refused whole whatever the line, never read as fewer rows or as other rows
    public void A_view_the_workspace_cannot_have_is_refused_whole()
    {
        var rig = new Rig();
        ViewSort[] largest = [new("volume", Descending: true)];
        var paged = new LineView(Stations.Readings, "manual", largest, rig.Order("manual", largest));
        string Why(LineView view, string line = "")
        {
            var refused = Assert.Throws<WorkspaceRefused>(() => rig.Preview(line, view));
            return $"{refused.Code}: {refused.Message}";
        }
        const string Unordered = "InvalidView: stations has no order a position can be read in: its rows are named by their ids.";
        const string NoRange = "InvalidView: A view names rows by position as ranges of one row or more, from the first row on.";

        Assert.Equal("InvalidView: The view names a table, weather.tides, that this workspace has not.", Why(new LineView("weather.tides")));
        Assert.Equal("InvalidView: The view names a subset, low, that stations has not.", Why(new LineView(Stations.Sites, "low")));
        Assert.Equal("InvalidView: A view names its selection by keys or by positions, not both.", Why(paged with { Keys = ["R1"], Ranges = [new(0, 1)] }));
        Assert.Equal("InvalidView: A view names the focused row by its key or by its position, one of the two.", Why(paged with { Focus = new("note") }));
        Assert.Equal(Unordered, Why(new LineView(Stations.Sites, Ranges: [new(0, 1)])));
        // A sort the grid never had is refused though no position is read in it, and so is a sort of a table no reader pages.
        Assert.Equal("InvalidView: Readings cannot be sorted by mood.", Why(new LineView(Stations.Readings, "manual", [new("mood")])));
        Assert.Equal("InvalidView: Readings cannot be sorted by mood.", Why(new LineView(Stations.Readings, Sorts: [new("mood")], Keys: ["R1"]), "set selected checked on"));
        Assert.Equal(Unordered, Why(new LineView(Stations.Sites, Sorts: [new("altitude")])));
        // A range of no row or fewer, or from before the first, is no selection: never "nothing selected", which acts on the focused row.
        Assert.Equal(NoRange, Why(paged with { Ranges = [new(0, -1)], Focus = new("note", Position: 1) }, "set note wet"));
        Assert.Equal(NoRange, Why(paged with { Ranges = [new(0, 0)] }));
        Assert.Equal(NoRange, Why(paged with { Ranges = [new(-1, 2)] }));
        Assert.Equal("InvalidView: A view names the focused row by a position from the first row on.", Why(paged with { Focus = new("note", Position: -1) }));
    }

    [Fact] // ADR-0016 §2, note 17: `shown` is the rows the grid's filter leaves: a page that holds every row and filters them says the rows it shows by key
    public async Task Shown_is_the_rows_a_filtered_grid_shows()
    {
        var rig = new Rig();
        var filtered = new LineView(Stations.Sites, Shown: ["S1", "X1"], Keys: ["S1"]);

        var shown = rig.Preview("set shown altitude 130", filtered);
        var narrowed = rig.Preview("set shown where altitude>20 altitude 130", filtered);
        var offered = rig.Preview("set stations ", filtered);
        var gone = rig.Preview("set shown altitude 130", filtered with { Shown = ["S1", "S9"] });
        var none = rig.Preview("set shown altitude 130", filtered with { Shown = [], Keys = null });
        var paged = Assert.Throws<WorkspaceRefused>(() => rig.Preview("", new LineView(Stations.Readings, Order: "sent", Shown: ["R1"], Ranges: [new(0, 1)])));

        Assert.Equal(("stations, filtered to 2, 1 selected", ("stations", "S1, X1"), "stations, 2 rows | altitude: 2 to 130"), (shown.View, Asked(shown), Read(shown)));
        Assert.Equal(("stations", "X1"), Asked(narrowed));
        Assert.Contains("shown (2 rows)", offered.Complete.Select(c => $"{c.Value} ({c.Detail})"));
        Assert.Equal("ViewMoved: S9 was shown and is no longer a row of stations: open the line again.", Read(gone));
        Assert.Equal(("stations, filtered to 0, nothing selected", "NothingShown: stations shows no row."), (none.View, Read(none)));
        Assert.Equal(("InvalidView", "A view names the rows it shows by key where its page holds them all, and no row by position."), (paged.Code, paged.Message));

        Assert.Equal("Set altitude to 130 on S1 and X1.", (await rig.Run(shown))["said"]!.GetValue<string>());
        Assert.Equal(["station:S1", "station:X1"], rig.Board.Overrides.Keys.Select(t => t.Entity).Order(StringComparer.Ordinal));
    }

    [Fact] // ADR-0016 §2, note 17: rows of a screen run by their keys; a key a command would read as other rows is refused before Enter, and the row of a table of one row is said by its table alone
    public async Task Rows_of_a_screen_run_as_those_rows_or_are_refused()
    {
        var rig = new Rig();
        Stations.Made(rig.Workspaces, rig.Board, "auto");
        Stations.Made(rig.Workspaces, rig.Board, "wet 1");

        // A subset's word alone is the subset; in a list of ids it is an id, as a line reads it.
        var subset = rig.Preview("set selected checked on", new LineView(Stations.Readings, Keys: ["auto"]));
        var listed = rig.Preview("set selected checked on", new LineView(Stations.Readings, Keys: ["auto", "R1"]));
        var spaced = rig.Preview("set note dry", new LineView(Stations.Readings, Keys: ["wet 1"]));
        var where = rig.Preview("set where id=auto checked on");
        var one = rig.Preview("set selected daylight off", new LineView(Stations.Settings, Keys: ["board"]));

        Assert.Equal("UnsayableRow: auto is a row on screen whose id a command would read as something else: say the rows another way, such as with where.", Read(subset));
        Assert.Equal(("selected", 4, 12), (subset.Refused!.Word, subset.Refused.Start, subset.Refused.End));
        Assert.Equal("UnsayableRow: wet 1 is a row on screen whose id a command would read as something else: say the rows another way, such as with where.", Read(spaced));
        Assert.Null(spaced.Refused!.Word);
        Assert.Equal("readings, 1 row | checked: off to on", Read(where));
        Assert.Equal((("settings", null), "settings | daylight: on to off"), (Asked(one), Read(one)));
        Assert.Equal((("readings", "auto, R1"), "readings, 2 rows | checked: 2 off to on"), (Asked(listed), Read(listed)));
        Assert.Equal("Set checked to on on auto and R1.", (await rig.Run(listed))["said"]!.GetValue<string>());
        Assert.Equal(["reading:R1", "reading:auto"], rig.Board.Overrides.Keys.Select(t => t.Entity).Order(StringComparer.Ordinal));
        Assert.Equal("Set board daylight to off.", (await rig.Run(rig.Preview("set selected daylight off", new LineView(Stations.Settings, Keys: ["board"]))))["said"]!.GetValue<string>());
    }

    [Theory] // ADR-0016 §5, note 17: where rows are awaited, `selected` and `shown` are offered with their counts, only where the view has them
    [InlineData("set ", "R1,R3", "all | selected (2 rows) | shown (3 rows) | manual | automatic | where | readings")]
    [InlineData("set s", "R1,R3", "selected@4 (2 rows) | shown@4 (3 rows) | stations@4 | settings@4")]
    [InlineData("set ", "", "all | shown (3 rows) | manual | automatic | where | readings")]
    [InlineData("set readings ", "R1", "all | selected (1 row) | shown (3 rows) | manual | automatic | where | R1 | R2")]
    [InlineData("set stations ", "R1", "all | high | manual | where | N1 | S1 | X1")]
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

    [Fact] // ADR-0016 §9, note 18: set and reset with no rows act on the selection of the screen behind the line, or on the focused row where nothing is selected
    public async Task Rows_left_out_are_the_selection_or_the_focused_row()
    {
        var rig = new Rig();
        var two = new LineView(Stations.Readings, "manual", Keys: ["R1", "R3"], Focus: new("note", Key: "R3"));
        var focused = new LineView(Stations.Readings, "manual", Focus: new("note", Key: "R2"));
        ViewSort[] largest = [new("volume", Descending: true)];
        var paged = new LineView(Stations.Readings, "manual", largest, rig.Order("manual", largest), Focus: new("checked", Position: 0));

        var set = rig.Preview("set note wet", two);
        var several = rig.Preview("set note=wet checked=on", two);
        var one = rig.Preview("set checked off", focused);
        // A key names its row whatever its case, the focused row's as a selected row's.
        var cased = rig.Preview("set checked off", focused with { Focus = new("note", Key: "r2") });
        var at = rig.Preview("set checked on", paged);
        var reset = rig.Preview("reset note", two);
        var nothing = rig.Preview("set note wet", new LineView(Stations.Readings, "manual"));
        var gone = rig.Preview("set note wet", new LineView(Stations.Readings, Focus: new("note", Key: "R9")));

        Assert.Equal("rows: R1, R3 (Context); set: note wet (Typed)", Given(set));
        Assert.Equal((("readings", "R1, R3"), "readings, 2 rows", "note: 2 empty to wet"), (Asked(set), set.Rows!.Title, set.Would));
        Assert.Equal("note: 2 empty to wet; checked: 2 off to on", several.Would);
        Assert.Equal((("readings", "R2"), "rows: R2 (Context); set: checked off (Typed)", "checked: on to off"), (Asked(one), Given(one), one.Would));
        Assert.Equal(("readings", "R2"), Asked(cased));
        Assert.Equal(("readings", "R3"), Asked(at));
        Assert.Equal((("readings", "R1, R3"), "rows: R1, R3 (Context); field: note (Typed)"), (Asked(reset), Given(reset)));
        Assert.Equal("NothingSelected: No row of readings (manual) is selected.", Read(nothing));
        Assert.Equal("ViewMoved: R9 was the row the keyboard was on and is no longer a row of readings: select the rows again.", Read(gone));

        Assert.Equal("Set note to wet on R1 and R3.", (await rig.Run(set))["said"]!.GetValue<string>());
        Assert.Equal("Takes back: set readings R1, R3 note wet (you, 2 edits)", rig.Preview("undo").Would);
    }

    [Fact] // ADR-0016 §9, note 18: reset with no word at all resets the cell the keyboard is on, and no other
    public async Task Reset_alone_is_the_cell_the_keyboard_is_on()
    {
        var rig = new Rig();
        await rig.Said("set R1, R3 note=wet checked=on");

        var reset = rig.Preview("reset", new LineView(Stations.Readings, "manual", Keys: ["R1", "R3"], Focus: new("note", Key: "R3")));
        var nowhere = rig.Preview("reset", new LineView(Stations.Readings, Keys: ["R1"]));
        var column = rig.Preview("reset", new LineView(Stations.Readings, Focus: new("age", Key: "R2")));

        Assert.Equal("rows: R3 (Context); field: note (Context)", Given(reset));
        Assert.Equal((("readings", "R3"), "note", "readings, 1 row", "note: reset"), (Asked(reset), reset.Inputs!["request"]!["field"]!.GetValue<string>(), reset.Rows!.Title, reset.Would));
        Assert.Equal("NoFocus: reset alone resets the cell the keyboard is on, and it is on no cell of readings: name the rows, or a field.", Read(nowhere));
        Assert.Equal("NoFocus: reset alone resets the cell the keyboard is on, and that cell is no field of readings: name the rows, or a field.", Read(column));
        // With no screen behind it, the line reads as it did.
        Assert.Equal("NoRows: Say which rows: their ids, all, manual, automatic, or where and a condition; or name a table (readings, stations, settings).", Read(rig.Preview("reset")));

        Assert.Equal("Reset R3 note.", (await rig.Run(reset))["said"]!.GetValue<string>());
        Assert.Equal(["reading:R1 checked", "reading:R1 note", "reading:R3 checked"], rig.Board.Overrides.Keys.Select(t => $"{t.Entity} {t.Field}").Order());
    }

    [Theory] // ADR-0016 §9, note 18: a field of a table of one row is read first, as before; words that name both, or a table and a field of the screen's, are refused with the spellings that say which
    [InlineData("set alarm 5mm", true, "settings | alarm volume: 10mm to 5mm")]
    [InlineData("set limit 5mm", true, "AmbiguousField: limit could be alarm volume of settings or volume of the readings on screen; say which, as in settings limit 5mm or selected limit 5mm.")]
    [InlineData("reset limit", true, "AmbiguousField: limit could be alarm volume of settings or volume of the readings on screen; say which, as in settings limit or selected limit.")]
    [InlineData("set site N1 altitude 130", true, "AmbiguousWords: site could be the table stations or station of the readings on screen; write stations for the table, or selected site for the rows on screen.")]
    [InlineData("set stations N1 altitude 130", true, "stations, 1 row | altitude: 120 to 130")]
    [InlineData("set readings note wet", true, "UnknownRow: No row note in readings.")]
    [InlineData("remove", true, "NoRows: Say which rows: their ids, all, manual, automatic, or where and a condition; or name a table (readings, stations, settings).")]
    // With no screen behind it, every line reads as it did in step 1.
    [InlineData("set limit 5mm", false, "settings | alarm volume: 10mm to 5mm")]
    [InlineData("set site N1 altitude 130", false, "stations, 1 row | altitude: 120 to 130")]
    [InlineData("set note wet", false, "UnknownRow: No row note in readings, stations or settings.")]
    public void Words_that_name_a_field_of_the_screens_table_and_something_else_are_refused_with_both_spellings(string line, bool screen, string read) =>
        Assert.Equal(read, Read(new Rig().Preview(line, screen ? new LineView(Stations.Readings, "manual", Keys: ["R1", "R3"], Focus: new("note", Key: "R3")) : null)));

    [Theory] // ADR-0016 §5, note 18: where rows may be left out, the fields of the screen's table are offered, saying what they would act on
    [InlineData("set ", "R1,R3", "daylight (settings) | volume (2 selected) | unit (2 selected) | checked (2 selected) | note (2 selected) | wind speed (2 selected) | wind direction (2 selected) | drift (2 selected) | R1 (readings)")]
    [InlineData("set ", "", "daylight (settings) | volume (R2) | unit (R2)")]
    [InlineData("set no", "R1", "note@4 (1 selected)")]
    [InlineData("set note ", "R1", "empty")]
    [InlineData("set wind ", "R1", "wind speed@4 | wind direction@4")]
    [InlineData("set note=wet ", "R1", "volume= | unit= | checked= | wind speed= | wind direction= | drift=")]
    [InlineData("reset ", "R1", "volume (1 selected) | unit (1 selected)")]
    [InlineData("reset no", "R1", "note@6 (1 selected)")]
    public void Where_rows_may_be_left_out_the_fields_of_the_screens_table_are_offered(string line, string selected, string offered)
    {
        var keys = selected.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var preview = new Rig().Preview(line, new LineView(Stations.Readings, "manual", Keys: keys, Focus: new("note", Key: "R2")));

        var all = preview.Complete.Select(c => $"{c.Value}{(c.Start == line.Length ? "" : $"@{c.Start}")}{(c.Detail is null ? "" : $" ({c.Detail})")}").ToList();
        var from = all.IndexOf(offered.Split(" | ")[0]);

        Assert.Equal(offered, string.Join(" | ", all.Skip(from).Take(offered.Split(" | ").Length)));
    }

    [Fact] // ADR-0016 §9, note 21: find is read by the server, which answers the place the page puts the Focus on, and no command; nothing is written
    public void Find_answers_the_place_of_a_row_in_the_grid_on_screen_and_no_command()
    {
        var rig = new Rig();
        var before = rig.Board.Revision;
        ViewSort[] largest = [new("volume", Descending: true)];
        // The manual readings, largest first: R3, R1, R2.
        var paged = new LineView(Stations.Readings, "manual", largest, rig.Order("manual", largest), Focus: new("note", Position: 0));

        var cell = rig.Preview("find R2 wind speed", paged);
        var row = rig.Preview("find r1", paged);
        var held = rig.Preview("find n1 altitude", new LineView(Stations.Sites));

        Assert.Equal((null, null), (cell.Command, cell.Inputs));
        Assert.Equal(new LinePlace(Stations.Readings, "manual", "R2", 2, "windSpeed", "The keyboard is on R2 · wind speed in readings (manual)."), cell.Place);
        Assert.Equal(("readings, 1 row | The keyboard goes to R2 · wind speed.", "row: R2 (Typed); field: wind speed (Typed)"), (Read(cell), Given(cell)));
        Assert.Equal(["R2"], cell.Rows!.First);
        // With no field the Focus keeps the column it was on, and the sentence names the row alone.
        Assert.Equal(new LinePlace(Stations.Readings, "manual", "R1", 1, "note", "The keyboard is on R1 in readings (manual)."), row.Place);
        // Where the page holds every row the place is the key alone, and the column none where the grid had no Focus.
        Assert.Equal(new LinePlace(Stations.Sites, null, "N1", null, "altitude", "The keyboard is on N1 · altitude in stations."), held.Place);
        Assert.Equal(new LinePlace(Stations.Sites, null, "S1", null, null, "The keyboard is on S1 in stations."), rig.Preview("find S1", new LineView(Stations.Sites)).Place);
        Assert.Equal(before, rig.Board.Revision);
    }

    [Theory] // ADR-0016 §9, note 21: find looks among the rows the grid on screen shows, and refuses by name what it cannot place
    // The end of an id names its row; one that several rows have is refused with them.
    [InlineData("find 2", "manual", "readings, 1 row | The keyboard goes to R2.")]
    [InlineData("find 1", null, "AmbiguousRow: 1 could be R1 or X1 of readings; write more of the id.")]
    [InlineData("find R9", "manual", "UnknownRow: No row R9 in readings (manual): its rows are R1, R2, R3.")]
    // A row of the table that the grid does not show is said as that, with where it is.
    [InlineData("find R4", "manual", "NotOnScreen: R4 is no row of the grid on screen, readings (manual): it is one of readings (automatic).")]
    [InlineData("find 4 note", "manual", "NotOnScreen: R4 is no row of the grid on screen, readings (manual): it is one of readings (automatic).")]
    [InlineData("find R1 colour", "manual", "UnknownField: readings has no field colour; its fields are id, station, volume, unit, taken, checked, note, wind speed, wind direction, window, drift.")]
    [InlineData("find R1 wind", "manual", "AmbiguousField: wind could be wind speed or wind direction of readings.")]
    [InlineData("find", "manual", "NoRows: Say which row of readings (manual) to find: its id, or the end of it.")]
    [InlineData("find R1, R2", "manual", "OneRow: find puts the keyboard on one row: name one.")]
    public void Find_refuses_by_name_what_it_cannot_place(string line, string? subset, string read)
    {
        var rig = new Rig();

        Assert.Equal(read, Read(rig.Preview(line, new LineView(Stations.Readings, subset, Order: rig.Order(subset, [])))));
    }

    [Fact] // ADR-0016 §9, note 21: find needs a grid behind the line, the order that grid was drawn from, and a row its filter has not hidden
    public void Find_is_refused_with_no_screen_a_moved_order_or_a_row_the_filter_hides()
    {
        var rig = new Rig();
        ViewSort[] largest = [new("volume", Descending: true)];
        var paged = new LineView(Stations.Readings, "manual", largest, rig.Order("manual", largest));

        var none = rig.Preview("find R1");
        var moved = rig.Preview("find R1", paged with { Order = "an earlier order" });
        var hidden = rig.Preview("find N1", new LineView(Stations.Sites, Shown: ["S1", "X1"]));
        var shown = rig.Preview("find 1", new LineView(Stations.Sites, Shown: ["S1"]));

        Assert.Equal("NoView: find looks for a row in the grid on screen, and no grid of a table is behind this line: go to one first.", Read(none));
        Assert.Equal("ViewMoved: The readings are no longer in the order the grid shows them in: open the line again.", Read(moved));
        Assert.Equal("NotOnScreen: N1 is a row of stations that the filter of the grid on screen leaves out.", Read(hidden));
        // Under a filter an id's end is read among the rows it leaves: 1 is S1 alone, though three stations end with it.
        Assert.Equal("S1", shown.Place!.Key);
        Assert.All([none, moved, hidden], refused => Assert.Null(refused.Place));
    }

    [Fact] // ADR-0016 §9, note 21: a position is answered in the order the view names; a view that names none is answered the key alone
    public void Find_answers_a_position_only_in_the_order_the_view_names()
    {
        var rig = new Rig();
        ViewSort[] largest = [new("volume", Descending: true)];

        var paged = rig.Preview("find R2", new LineView(Stations.Readings, "manual", largest, rig.Order("manual", largest)));
        // The readings have a reader that pages them, and this page holds them all: it names its rows by key and no order.
        var held = rig.Preview("find R2", new LineView(Stations.Readings, "manual", Keys: ["R1"]));
        // A grid the server pages whose rows in hand were of no one order names none either: its page says it cannot place.
        var unsaid = rig.Preview("find R2", new LineView(Stations.Readings, "manual", largest));

        Assert.Equal(("R2", 2), (paged.Place!.Key, paged.Place.Position));
        Assert.Equal(("R2", null), (held.Place!.Key, held.Place.Position));
        Assert.Equal(("R2", null), (unsaid.Place!.Key, unsaid.Place.Position));
    }

    [Theory] // ADR-0016 §5, note 21: after find, the rows the grid shows are offered, a few at a time, and after a row the table's fields
    [InlineData("find ", "manual", "R1 | R2 | R3")]
    [InlineData("find 2", "manual", "R2@5")]
    [InlineData("find r", null, "R1@5 | R2@5 | R3@5 | R4@5 | R5@5")]
    [InlineData("find R2 ", "manual", "id | station | volume | unit | taken | checked | note | wind speed | wind direction | window | drift")]
    [InlineData("find R2 wi", "manual", "wind speed@8 | wind direction@8 | window@8")]
    [InlineData("find R2 wind ", "manual", "wind speed@8 | wind direction@8")]
    [InlineData("find R2 wind s", "manual", "wind speed@8")]
    [InlineData("find R2 note ", "manual", "")]
    [InlineData("find", "manual", "")]
    public void After_find_the_rows_the_grid_shows_and_then_the_fields_are_offered(string line, string? subset, string offered)
    {
        var preview = new Rig().Preview(line, new LineView(Stations.Readings, subset));

        Assert.Equal(offered, string.Join(" | ", preview.Complete.Select(c => $"{c.Value}{(c.Start == line.Length ? "" : $"@{c.Start}")}")));
    }

    [Fact] // ADR-0016 §9, note 23: with a screen behind the line, rows said as all or by where alone are rows of the screen's table, and the request names that table
    public async Task Rows_said_with_no_table_are_the_screens_and_the_request_names_the_table()
    {
        var rig = new Rig();
        var stations = new LineView(Stations.Sites);

        var where = rig.Preview("set where altitude>30 altitude 50", stations);
        var all = rig.Preview("reset all altitude", stations);
        // A subset's word names the screen's table where that table has it, and the main table where it has not.
        var high = rig.Preview("set high altitude 130", stations);
        // The stations have a subset of the readings' name, `manual`: said by that name it is theirs, and by a word the
        // readings' alone has, `by hand`, it is the readings'.
        var manual = rig.Preview("set manual altitude 20", stations);
        var byHand = rig.Preview("set by hand checked on", stations);
        // With no screen (the AI, a saved sequence) the main table, as in step 1; and a screen of the main table names none.
        var none = rig.Preview("set where unit=m3 checked on");
        var main = rig.Preview("reset where unit=m3 note", new LineView(Stations.Readings, "manual"));

        Assert.Equal((("stations", "where altitude>30"), "stations, 2 rows", "altitude: 2 to 50"), (Asked(where), where.Rows!.Title, where.Would));
        Assert.Equal((("stations", "all"), "stations, 3 rows"), (Asked(all), all.Rows!.Title));
        Assert.Equal((("stations", "high"), "stations, 1 row"), (Asked(high), high.Rows!.Title));
        Assert.Equal((("stations", "manual"), "stations, 1 row"), (Asked(manual), manual.Rows!.Title));
        Assert.Equal(((null, "by hand"), "readings, 3 rows"), (Asked(byHand), byHand.Rows!.Title));
        Assert.Equal(((null, "where unit=m3"), "readings, 4 rows"), (Asked(none), none.Rows!.Title));
        // The table, not the grid's subset: `shown` says the rows of the grid.
        Assert.Equal(((null, "where unit=m3"), "readings, 4 rows"), (Asked(main), main.Rows!.Title));
        // What is run is rows of that table, whoever reads the request, and the log names it as a line that could be typed anywhere.
        Assert.Equal("Set altitude to 50 on N1 and X1.", (await rig.Run(where))["said"]!.GetValue<string>());
        Assert.Equal("Takes back: set stations where altitude>30 altitude 50 (you, 2 edits)", rig.Preview("undo").Would);
    }

    [Theory] // ADR-0016 §9, note 23: what could follow is said for the table the rows would be read in: the screen's subsets, and after where its fields
    [InlineData("set ", "all | shown (3 rows) | high | manual | automatic | where | readings")]
    [InlineData("set where ", "id | altitude | backup")]
    [InlineData("set all ", "altitude | backup | where")]
    [InlineData("set where altitude>30 ", "and | altitude | backup")]
    [InlineData("set manual ", "altitude | backup | where")]
    [InlineData("set by hand ", "volume | unit | checked | note | wind speed | wind direction | drift | where")]
    public void With_a_table_left_out_what_could_follow_is_the_screens_tables(string line, string offered)
    {
        var preview = new Rig().Preview(line, new LineView(Stations.Sites));

        Assert.Equal(offered, string.Join(" | ", preview.Complete.Select(c => $"{c.Value}{(c.Detail is null ? "" : $" ({c.Detail})")}").Take(offered.Split(" | ").Length)));
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
    [InlineData("sett R1", "UnknownCommand", "sett", 0, 4, "set, reset, remove, undo, redo, find")]
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

    [Fact] // ADR-0016 §4, note 25 of step 2: a workspace's tables are told with their actions, each a title and the line that does it, the kernel's Reset this cell first
    public void A_workspaces_tables_are_told_with_their_actions_the_kernels_first()
    {
        var rig = new Rig();

        var tables = rig.Lines.Tables(rig.Id, new Caller("ana", RunOrigin.Pricer));

        Assert.Equal(
            ["weather.readings Readings: Reset this cell=reset, Mark checked=set selected checked on", "weather.stations Stations: Reset this cell=reset", "weather.settings Board settings: Reset this cell=reset"],
            tables.Select(t => $"{t.TableId} {t.Title}: {string.Join(", ", t.Actions.Select(a => $"{a.Title}={a.Line}"))}"));
        // Each line is one the preview reads with a screen behind it: the kernel's own on the cell the menu was opened on.
        var view = new LineView(Stations.Readings, null, Keys: ["R1", "R2"], Focus: new("checked", Key: "R2"));
        Assert.Equal(("entity.set", "R1, R2"), (rig.Preview(tables[0].Actions[1].Line, view).Command!.Id, rig.Preview(tables[0].Actions[1].Line, view).Inputs!["request"]!["rows"]!.GetValue<string>()));
        Assert.Equal(("entity.reset", "R2", "checked"), (rig.Preview("reset", view).Command!.Id, rig.Preview("reset", view).Inputs!["request"]!["rows"]!.GetValue<string>(),
            rig.Preview("reset", view).Inputs!["request"]!["field"]!.GetValue<string>()));
        // Another user's workspace is not described at all.
        Assert.Equal("NotOwner", Assert.Throws<WorkspaceRefused>(() => rig.Lines.Tables(rig.Id, new Caller("ben", RunOrigin.Pricer))).Code);
    }

    [Fact] // ADR-0016 §4, note 25 of step 2: an action whose line begins with no command's words is the registration's defect, and fails when the tables are read
    public void An_action_of_no_command_fails_when_the_tables_are_read()
    {
        var rig = new Rig(Stations.SitesTable with { Actions = [new("Raise", "lift selected altitude 10")] });

        var failure = Assert.Throws<InvalidOperationException>(() => rig.Lines.Tables(rig.Id, new Caller("ana", RunOrigin.Pricer)));

        Assert.Equal("weather.stations offers Raise as the line `lift selected altitude 10`, which begins with no command's words.", failure.Message);
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
    [InlineData("set stations ", "all | high | manual | where | N1 | S1 | X1")]
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
