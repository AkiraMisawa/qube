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
        [.. form.Fields.Where(f => names.Contains(f.Name)).Select(f => $"{f.Name} {f.Written}{(f.Changed ? " *" : "")}")];

    [Fact] // ADR-0016 note 54: the form of a draft with no line is the row as the workspace has it, with the rows under it
    public void The_form_of_a_row_with_no_line_is_the_row_as_the_workspace_has_it()
    {
        var rig = new SensorRig();

        var form = Form(rig, "S1");

        Assert.Equal(["id S1", "altitude 15", "backup N1"], Fields(form, "id", "altitude", "backup"));
        Assert.Equal([false, true, true], form.Fields.Where(f => f.Name is "id" or "altitude" or "backup").Select(f => f.Editable));
        var sensors = Assert.Single(form.Under);
        Assert.Equal((Stations.Sensors, "Sensors"), (sensors.Table, sensors.Title));
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
        Assert.Equal(["Sensor", "Station", "Kind", "Load", "Unit"], sensors.Fields);
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

    [Theory] // ADR-0016 note 54: a draft holds the lines that edit a row, and no other command's
    [InlineData("undo", "NotOfADraft", "undo is no line of a draft: a draft holds set, reset, new and remove.")]
    [InlineData("measure S1", "UnknownCommand", "`measure S1` begins with no command.")]
    public void A_line_of_another_command_is_refused_as_no_line_of_a_draft(string line, string code, string sentence)
    {
        var refused = Assert.Single(Form(new SensorRig(), "S1", line).Lines).Refused!;

        Assert.Equal((code, sentence), (refused.Code, refused.Sentence));
    }
}
