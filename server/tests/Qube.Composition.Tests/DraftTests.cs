using Qube.Composition.Application;
using Qube.Composition.Application.Products;
using Qube.Composition.Application.Tables;
using Qube.Composition.Application.Workspaces;

namespace Qube.Composition.Tests;

// ADR-0016 §10 and its notes 54 and 55 of 2026-11-12: a draft is a row, the revision it was opened at and its lines,
// read in order by the server, which stores nothing. The registration is a station with its sensors under it, and has
// no financial word. N1 stands at 120 with no backup and two sensors; S1 at 15, backed up by N1, with one.
public class DraftTests
{
    /// <summary>The form of a station's draft opened on the board as it stands, with the lines given.</summary>
    static DraftForm Form(SensorRig rig, string station, params string[] lines) =>
        rig.Lines.Draft(rig.Id, SensorRig.Ana, new(Stations.Sites, station, rig.Board.Revision, lines));

    // A field of the form as its name, its value as written and, where the draft changed it, a mark.
    static string[] Fields(DraftForm form, params string[] names) =>
        [.. form.Fields.Where(f => names.Contains(f.Field.Name)).Select(f => $"{f.Field.Name} {f.Written}{(f.Changed ? " *" : "")}")];

    [Fact] // ADR-0016 note 54: the form of a draft with no line is the row as the workspace has it, with the rows under it
    public void The_form_of_a_row_with_no_line_is_the_row_as_the_workspace_has_it()
    {
        var rig = new SensorRig();

        var form = Form(rig, "S1");

        Assert.Equal(["id S1", "altitude 15", "backup N1"], Fields(form, "id", "altitude", "backup"));
        // What a page draws each input from: the word a line says the field by, whether it is written, its form and its choices.
        Assert.Equal(["id id False", "altitude number True", "backup id True"],
            form.Fields.Select(f => f.Field).Where(f => f.Name is "id" or "altitude" or "backup").Select(f => $"{f.Word} {f.Form} {f.Editable}"));
        Assert.Equal("stations", form.Word);
        var sensors = Assert.Single(form.Under);
        Assert.Equal((Stations.Sensors, "Sensors", "sensors"), (sensors.Table, sensors.Title, sensors.Word));
        Assert.Equal(["S1/1"], sensors.Rows.Select(r => r.Key));
        Assert.All(sensors.Rows, r => Assert.Null(r.Mark));
        Assert.Empty(form.Lines);
        Assert.Equal(rig.Board.Revision, form.Revision);
    }

    // A line of the form as the line and what it said, or its refusal's code.
    static string[] Lines(DraftForm form) => [.. form.Lines.Select(l => $"{l.Line} => {l.Said ?? l.Refused!.Code}")];

    [Fact] // ADR-0016 note 54: each line is read on what the lines before it left, the form marks what the draft changed, and nothing is written
    public void Each_line_is_read_on_what_the_lines_before_it_left()
    {
        var rig = new SensorRig();
        var revision = rig.Board.Revision;

        // The third line sets a sensor the second makes: it reads only on what the second left.
        var form = Form(rig, "S1", "set stations S1 altitude 20", "new sensor S1 wind 2 m3", "set sensors S1/2 kind gust");

        Assert.Equal(["altitude 20 *", "backup N1"], Fields(form, "altitude", "backup"));
        Assert.Equal(["set stations S1 altitude 20 => Set S1 altitude to 20.", "new sensor S1 wind 2 m3 => Made S1/2: Sensor.", "set sensors S1/2 kind gust => Set S1/2 kind to gust."],
            Lines(form));
        Assert.Equal(["S1/1", "S1/2"], Assert.Single(form.Under).Rows.Select(r => r.Key));
        Assert.Equal(revision, rig.Board.Revision);
        Assert.Empty(rig.Board.Overrides);
    }

    [Fact] // ADR-0016 note 54: a line that no longer reads is answered with its refusal, the form is that of the lines before it, and the lines after it are not read
    public void A_refused_line_ends_the_reading_and_the_form_is_that_of_the_lines_before_it()
    {
        var rig = new SensorRig();

        var form = Form(rig, "S1", "set stations S1 altitude 20", "set stations S1 altitude high", "set stations S1 backup S1");

        Assert.Equal(["set stations S1 altitude 20 => Set S1 altitude to 20.", "set stations S1 altitude high => InvalidValue"], Lines(form));
        Assert.Equal("high", form.Lines[1].Refused!.Word);
        Assert.Equal(["altitude 20 *", "backup N1"], Fields(form, "altitude", "backup"));
    }

    [Fact] // ADR-0016 note 54: the rows under the row are as the draft leaves them, and one it made, changed or removed is marked
    public void A_row_the_draft_made_changed_or_removed_under_its_row_is_marked()
    {
        var rig = new SensorRig();

        var form = Form(rig, "N1", "new sensor N1 wind 2 m3", "set sensors N1/2 kind gust", "remove sensors N1/1", "new sensor N1 rain 1 m3", "remove sensors N1/4");

        // The removed row stays where it stood, as it was; a row made and removed within the draft was never the workspace's.
        var sensors = Assert.Single(form.Under);
        Assert.Equal(["N1/1 rain removed", "N1/2 gust changed", "N1/3 wind made"], sensors.Rows.Select(r => $"{r.Key} {r.Written[2]} {r.Mark}"));
        Assert.Equal(["Sensor", "Station", "Kind", "Load", "Unit"], sensors.Fields.Select(f => f.Title));
    }

    [Fact] // ADR-0016 note 54: a draft carries its revision, and one the workspace has left is refused, saying the revision it is at and the commands that moved it
    public async Task A_draft_the_workspace_has_left_is_refused_saying_what_moved_it()
    {
        var rig = new SensorRig();
        var draft = new LineDraft(Stations.Sites, "S1", rig.Board.Revision, ["set stations S1 altitude 20"]);
        await rig.Run("set stations N1 altitude 130");
        await rig.Run("set stations S1 backup none");

        var refused = Assert.Throws<WorkspaceRefused>(() => rig.Lines.Draft(rig.Id, SensorRig.Ana, draft));

        Assert.Equal(("RevisionMoved", $"The draft of S1 was opened at revision {draft.Revision} and the workspace is at {rig.Board.Revision}, after set stations N1 altitude 130 and set stations S1 backup none."),
            (refused.Code, refused.Message));
    }

    [Fact] // ADR-0016 note 54: Original is the row as the source alone gives it, whatever the workspace and the draft changed
    public async Task Original_is_the_row_as_the_source_alone_gives_it()
    {
        var rig = new SensorRig();
        await rig.Run("set stations S1 altitude 18");
        await rig.Run("new sensor S1 wind 2 m3");
        await rig.Run("remove sensors S1/1");

        var form = Form(rig, "S1", "set stations S1 altitude 20");

        Assert.Equal(["altitude 20 *"], Fields(form, "altitude"));
        Assert.Equal(["S1/2"], Assert.Single(form.Under).Rows.Select(r => r.Key));
        var original = form.Original!;
        Assert.Equal(["S1", "15", "N1"], form.Fields.Zip(original.Written).Where(f => f.First.Field.Name is "id" or "altitude" or "backup").Select(f => f.Second));
        Assert.Equal(["S1/1 rain "], Assert.Single(original.Under).Rows.Select(r => $"{r.Key} {r.Written[2]} {r.Mark}"));
    }

    [Fact] // ADR-0016 note 54: a row the workspace made has no Original
    public async Task A_row_the_workspace_made_has_no_Original()
    {
        var rig = new SensorRig();
        await rig.Run("new sensor S1 wind 2 m3");

        var form = rig.Lines.Draft(rig.Id, SensorRig.Ana, new(Stations.Sensors, "S1/2", rig.Board.Revision, []));

        Assert.Equal("wind", form.Fields.Single(f => f.Field.Name == "kind").Written);
        Assert.Null(form.Original);
    }

    [Fact] // ADR-0016 note 57: a table under the row says each product that makes a row there, with the terms it takes, the row above left out
    public void A_table_under_the_row_says_the_products_that_make_its_rows()
    {
        var sensors = Assert.Single(Form(new SensorRig(), "S1").Under);
        var undescribed = Assert.Single(Form(new SensorRig(described: false), "S1").Under);

        Assert.Equal(["sensor (Sensor): kind* choice rain|wind|all, load* number, unit* choice M3|GAL"],
            sensors.Makes.Select(m => $"{m.Word} ({m.Title}): {string.Join(", ", m.Takes.Select(x => $"{x.Name}{(x.Required ? "*" : "")} {x.Form}{(x.Choices is { } c ? $" {string.Join('|', c)}" : "")}"))}"));
        // With no product described, a row under the row is made by nothing a dialog could offer.
        Assert.Empty(undescribed.Makes);
    }

    [Fact] // ADR-0016 note 54: the form carries its table's templates, each with the word a line says it by and the terms it takes
    public void The_form_carries_the_templates_of_its_table()
    {
        var rig = new SensorRig(readings: Stations.ReadingsWithTemplates);

        var form = rig.Lines.Draft(rig.Id, SensorRig.Ana, new(Stations.Readings, "R1", rig.Board.Revision, ["set readings R1 as gauged gal"]));

        Assert.Equal(["gauged (Gauged by hand): unit* choice M3|GAL, drift rate", "calm (Calm): "],
            form.Templates.Select(t => $"{t.Word} ({t.Title}): {string.Join(", ", t.Takes.Select(x => $"{x.Name}{(x.Required ? "*" : "")} {x.Form}{(x.Choices is { } c ? $" {string.Join('|', c)}" : "")}"))}"));
        Assert.Equal(["unit GAL *"], Fields(form, "unit"));
        Assert.Empty(Form(new SensorRig(), "S1").Templates);
    }

    [Theory] // ADR-0016 note 54: a draft holds the lines that edit a row, and no other command's
    [InlineData("undo", "NotOfADraft", "undo is no line of a draft: a draft holds set, reset, new and remove.")]
    [InlineData("measure S1", "UnknownCommand", "`measure S1` begins with no command.")]
    public void A_line_of_another_command_is_refused_as_no_line_of_a_draft(string line, string code, string sentence)
    {
        var refused = Assert.Single(Form(new SensorRig(), "S1", line).Lines).Refused!;

        Assert.Equal((code, sentence), (refused.Code, refused.Sentence));
    }

    [Fact] // ADR-0016 note 56: open <row> answers the page the one row whose dialog to open, and invokes nothing; no row and several rows are refused
    public void Open_answers_the_one_row_whose_dialog_the_page_opens()
    {
        var rig = new SensorRig();

        var station = rig.Preview("open S1");
        var sensor = rig.Preview("open sensors n1/2");

        Assert.Equal(new LineOpen(Stations.Sites, "S1", "Opened S1."), station.Asks);
        Assert.Equal((null, "Opens S1.", rig.Board.Revision), (station.Command, station.Would, station.Revision));
        Assert.Equal(new LineOpen(Stations.Sensors, "N1/2", "Opened N1/2."), sensor.Asks);
        Assert.Equal(("UnknownRow", "S9"), (rig.Preview("open S9").Refused!.Code, rig.Preview("open S9").Refused!.Word));
        var several = rig.Preview("open stations all").Refused!;
        Assert.Equal("SeveralRows", several.Code);
        Assert.Equal("open opens the dialog of one row, and stations all names 3: N1, S1 and X1.", several.Sentence);
    }

    // The dialog of a station's draft as the screen behind a line: the draft's table, its row selected, and its lines.
    static LineView Dialog(SensorRig rig, string station, params string[] lines) => new(Stations.Sites, Draft: new(Stations.Sites, station, rig.Board.Revision, lines));

    [Fact] // ADR-0016 note 55: with a draft open, set names no row and means the draft's; the line answers the whole line the draft gains and what it would change, and has nothing to invoke
    public void With_a_draft_open_set_is_read_for_the_drafts_row_and_answers_the_line_the_draft_gains()
    {
        var rig = new SensorRig();
        var revision = rig.Board.Revision;

        var read = rig.Preview("set altitude 20", Dialog(rig, "S1"));
        // On what the draft's lines left: the second reading starts from 20.
        var again = rig.Preview("set altitude 25", Dialog(rig, "S1", "set stations S1 altitude 20"));

        Assert.Equal(new LineDrafted("set stations S1 altitude 20", "Added to the draft of S1: set stations S1 altitude 20."), read.Asks);
        Assert.Equal((null, null, null, "altitude: 15 to 20", revision), (read.Command, read.Inputs, read.Refused, read.Would, read.Revision));
        Assert.Equal("altitude: 20 to 25", again.Would);
        Assert.Equal(("Editing S1: nothing pending.", "Editing S1: 1 line pending."), (read.View, again.View));
        Assert.Equal(revision, rig.Board.Revision);
    }

    [Fact] // ADR-0016 note 55: new and remove are of the draft's row: a row is made under it without naming it, and a row under it is said by how its id ends
    public void With_a_draft_open_new_and_remove_are_of_the_rows_under_the_drafts_row()
    {
        var rig = new SensorRig();

        var made = rig.Preview("new sensor wind 2 m3", Dialog(rig, "S1"));
        var line = Assert.IsType<LineDrafted>(made.Asks).Line;
        // N1 has a sensor 2 as well: in the draft of S1 the sensors are S1's alone.
        var removed = rig.Preview("remove sensors 2", Dialog(rig, "S1", line));
        var set = rig.Preview("set sensors 1 kind gust", Dialog(rig, "S1"));

        Assert.Equal(("new sensor station=S1 wind 2 m3", "Makes S1/2: Sensor."), (line, made.Would));
        Assert.Equal("remove sensors S1/2", Assert.IsType<LineDrafted>(removed.Asks).Line);
        Assert.Equal("set sensors S1/1 kind gust", Assert.IsType<LineDrafted>(set.Asks).Line);
        // Each line the draft gains reads again as a whole line, with no draft behind it.
        Assert.Equal([$"{line} => Made S1/2: Sensor.", "remove sensors S1/2 => Removed S1/2."], Lines(Form(rig, "S1", line, "remove sensors S1/2")));
    }

    [Fact] // ADR-0016 note 55: in a draft the rows offered of a table under the draft's are the rows under its row alone, as the line then reads them
    public void In_a_draft_the_rows_offered_under_it_are_its_own()
    {
        var rig = new SensorRig();

        string[] outside = [.. rig.Preview("remove sensors ").Complete!.Select(c => c.Value).Where(v => v.Contains('/'))];
        string[] inside = [.. rig.Preview("remove sensors ", Dialog(rig, "S1")).Complete!.Select(c => c.Value).Where(v => v.Contains('/'))];

        Assert.Equal(["N1/1", "N1/2", "S1/1"], outside);
        Assert.Equal(["S1/1"], inside);
    }

    [Theory] // ADR-0016 note 55: a draft is of one row: a line of another row, one that changes nothing, and every command that is not the draft's are refused, and the draft gains nothing
    [InlineData("set stations N1 altitude 1", "NotTheDraftsRow", "The draft is of S1, and this line is of N1: apply or cancel the draft first.")]
    [InlineData("new sensor N1 wind 2 m3", "NotTheDraftsRow", "The draft is of S1, and this line is of N1: apply or cancel the draft first.")]
    [InlineData("set sensors N1/1 kind gust", "UnknownRow", "No row N1/1 in sensors: its rows are S1/1.")]
    [InlineData("set altitude 15", "NothingToAdd", "Nothing would change: S1 already holds altitude 15. The draft of S1 gains nothing.")]
    [InlineData("undo", "DraftOpen", "undo is not read while the draft of S1 is open: apply writes it, and cancel discards it.")]
    [InlineData("open N1", "DraftOpen", "open is not read while the draft of S1 is open: apply writes it, and cancel discards it.")]
    // A verb of a grid has no grid in a dialog: it is refused as every command that is not the draft's.
    [InlineData("sort by altitude", "DraftOpen", "sort is not read while the draft of S1 is open: apply writes it, and cancel discards it.")]
    [InlineData("copy", "DraftOpen", "copy is not read while the draft of S1 is open: apply writes it, and cancel discards it.")]
    public void With_a_draft_open_a_line_that_is_not_of_its_row_is_refused(string line, string code, string sentence)
    {
        var rig = new SensorRig();

        var read = rig.Preview(line, Dialog(rig, "S1"));

        Assert.Equal((code, sentence, null), (read.Refused?.Code, read.Refused?.Sentence, read.Asks));
    }

    [Fact] // ADR-0016 note 55: a draft does not take its own row away
    public async Task A_draft_does_not_remove_its_own_row()
    {
        var rig = new SensorRig();
        await rig.Run("new sensor S1 wind 2 m3");

        var read = rig.Preview("remove sensors S1/2", new LineView(Stations.Sensors, Draft: new(Stations.Sensors, "S1/2", rig.Board.Revision, [])));

        Assert.Equal(("RemovesTheDraftsRow", "The draft is of S1/2, which this line would take away: cancel the draft, and remove the row outside it."), (read.Refused?.Code, read.Refused?.Sentence));
    }

    [Fact] // ADR-0016 note 55: apply writes every line of the draft as one command at the revision it was opened at, and one undo takes it back whole
    public async Task Apply_writes_the_drafts_lines_as_one_command_that_one_undo_takes_back()
    {
        var rig = new SensorRig();
        var dialog = Dialog(rig, "S1", "set stations S1 altitude 20", "new sensor station=S1 wind 2 m3", "set sensors S1/2 kind gust");
        var commands = rig.Board.Log.Select(e => e.Command?.Id).Distinct().Count();

        var read = rig.Preview("apply", dialog);
        var told = await rig.Run("apply", dialog);

        Assert.Equal(("draft.apply", "Writes the 3 lines of the draft of S1 as one command.", null), (read.Command?.Id, read.Would, read.Asks));
        Assert.Equal(("Applied 3 lines to S1.", Stations.Sites, "S1", 3), (told["said"]!.GetValue<string>(), told["table"]!.GetValue<string>(), told["key"]!.GetValue<string>(), told["lines"]!.GetValue<int>()));
        Assert.Equal(commands + 1, rig.Board.Log.Select(e => e.Command?.Id).Distinct().Count());
        Assert.Equal("apply S1 (3 lines)", rig.Board.Log[^1].Command!.Said);
        Assert.Equal(["altitude 20"], Fields(Form(rig, "S1"), "altitude"));
        Assert.Equal(["S1/1 rain", "S1/2 gust"], Sensors(Form(rig, "S1")));

        await rig.Run("undo");
        Assert.Equal(["altitude 15"], Fields(Form(rig, "S1"), "altitude"));
        Assert.Equal(["S1/1 rain"], Sensors(Form(rig, "S1")));

        static string[] Sensors(DraftForm form) => [.. Assert.Single(form.Under).Rows.Select(r => $"{r.Key} {r.Written[2]}")];
    }

    [Fact] // ADR-0016 note 55: apply is refused where the workspace moved, where the draft has no line, and where no draft is open; nothing is written
    public async Task Apply_is_refused_where_the_workspace_moved_or_there_is_nothing_to_apply()
    {
        var rig = new SensorRig();
        var dialog = Dialog(rig, "S1", "set stations S1 altitude 20");
        var applied = rig.Preview("apply", dialog);
        await rig.Run("set stations N1 altitude 130");
        var revision = rig.Board.Revision;

        var moved = await rig.Commands.InvokeAsync(applied.Command!.Id, applied.Command.Version, applied.Inputs!, SensorRig.Ana, Guid.NewGuid().ToString(), default);

        Assert.Equal("RevisionMoved", Assert.Single(Assert.IsType<CommandResult.Refused>(moved).Issues).Code);
        Assert.Equal(("NothingToApply", "The draft of S1 has no line to apply: cancel closes it."), (rig.Preview("apply", Dialog(rig, "S1")).Refused?.Code, rig.Preview("apply", Dialog(rig, "S1")).Refused?.Sentence));
        Assert.Equal(("NoDraft", "apply writes the lines of an open draft, and none is open: open a row first."), (rig.Preview("apply").Refused?.Code, rig.Preview("apply").Refused?.Sentence));
        Assert.Equal(revision, rig.Board.Revision);
    }

    [Fact] // ADR-0016 note 55: cancel is a verb of the screen: the page discards the draft, and nothing is invoked
    public void Cancel_asks_the_page_to_discard_the_draft()
    {
        var rig = new SensorRig();

        var read = rig.Preview("cancel", Dialog(rig, "S1", "set stations S1 altitude 20"));

        Assert.Equal((new LineCancel("Discarded the draft of S1."), null, "Discards the draft of S1 and its 1 line."), (read.Asks, read.Command, read.Would));
        Assert.Equal("Closes the draft of S1.", rig.Preview("cancel", Dialog(rig, "S1")).Would);
        Assert.Equal(("NoDraft", "cancel discards an open draft, and none is open."), (rig.Preview("cancel").Refused?.Code, rig.Preview("cancel").Refused?.Sentence));
    }
}
