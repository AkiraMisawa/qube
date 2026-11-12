using System.Text.Json.Nodes;
using Qube.Composition.Application;
using Qube.Composition.Application.Saved;
using Qube.Composition.Application.Tables;
using Qube.Composition.Application.Workspaces;

namespace Qube.Composition.Tests;

/// <summary>
/// Saved sequences of edits over the station tables (ADR-0016 §8, notes 60 to 63): what a line's first words name,
/// what a sequence may hold, and how one runs. Every name and figure is synthetic.
/// </summary>
public sealed class SequenceTests
{
    [Fact] // ADR-0016 note 61: what a line's first words name is said whether or not the line reads
    public void A_lines_first_words_name_a_command_a_verb_of_the_screen_or_a_short_form()
    {
        var rig = new SequenceRig();

        // A command, by its id, though no station has a field zzz: the reading refuses the line, and the name stands.
        var set = rig.Lines.Names("set stations where zzz = 1 load 3", rig.Board);
        Assert.Equal(("set", EntitySet.Id, EntitySet.Id, false), (set!.Words, set.Id, set.Command?.CapabilityId, set.ShortForm));
        Assert.Equal("UnknownField", rig.Preview("set stations where zzz = 1 load 3").Refused?.Code);

        // A verb of the screen names no command, and a short form stands for a line.
        var find = rig.Lines.Names("find NORTH", rig.Board);
        Assert.Equal(("find", null, false), (find!.Words, find.Command, find.ShortForm));
        var tick = rig.Lines.Names("tick manual", rig.Board);
        Assert.Equal(("tick", "weather.tick", null, true), (tick!.Words, tick.Id, tick.Command, tick.ShortForm));

        // Whatever their case, and the longest words first, as a reading finds them; words that begin no command name nothing.
        Assert.Equal(WorkspaceTakeBack.Undo, rig.Lines.Names("UNDO", rig.Board)!.Id);
        Assert.Equal(SequenceRun.Id, rig.Lines.Names("sequence run morning", rig.Board)!.Id);
        Assert.Null(rig.Lines.Names("frobnicate NORTH", rig.Board));
        Assert.Null(rig.Lines.Names("  ", rig.Board));
    }

    [Fact] // ADR-0016 note 60: a sequence is its lines as written, in order, each with what its first words named
    public async Task A_sequence_keeps_its_lines_in_order_as_written_with_what_each_named()
    {
        var rig = new SequenceRig();
        var before = rig.Board.Revision;

        var (saved, _) = await rig.Save("morning", null, ["set stations N1 altitude 130", "  tick manual ", "survey"]);

        Assert.Equal("Saved 3 lines as the sequence morning, for you alone.", saved!["said"]!.GetValue<string>());
        Assert.Equal((rig.Id, before, "morning", "ana", "private", 3, false),
            (saved["workspaceId"]!.GetValue<string>(), saved["revision"]!.GetValue<long>(), saved["name"]!.GetValue<string>(), saved["owner"]!.GetValue<string>(),
                saved["share"]!.GetValue<string>(), saved["lines"]!.GetValue<int>(), saved["replaced"]!.GetValue<bool>()));
        Assert.Equal(["set stations N1 altitude 130 [entity.set]", "tick manual [weather.tick]", "survey [weather.survey]"], rig.Kept("morning"));
        // Saving edits no workspace, and is in no log.
        Assert.Equal(before, rig.Board.Revision);

        // Saved again under the name, whatever its case, it replaces its owner's own; shared with the desk, the desk is named.
        var (again, _) = await rig.Save("Morning", "desk", ["survey"]);
        Assert.Equal("Saved 1 line as the sequence Morning, for the desk north, replacing your own of that name.", again!["said"]!.GetValue<string>());
        Assert.Equal(("desk", "north", true), (again["share"]!.GetValue<string>(), again["desk"]!.GetValue<string>(), again["replaced"]!.GetValue<bool>()));
        Assert.Equal(["survey [weather.survey]"], rig.Kept("Morning"));
    }

    [Theory] // ADR-0016 note 61: what a sequence may not hold is refused with the line's number, and nothing is kept
    [InlineData("find N1", "NotInASequence: Line 1 (`find N1`): find is done by a page of the workbench, and a sequence has no screen.")]
    [InlineData("set selected checked on",
        "NoView: Line 1 (`set selected checked on`): selected means the rows selected on a screen, and no screen is behind this command: name the rows by their ids, all, a subset or where.")]
    [InlineData("set shown checked on",
        "NoView: Line 1 (`set shown checked on`): shown means the rows shown on a screen, and no screen is behind this command: name the rows by their ids, all, a subset or where.")]
    [InlineData("undo", "NotInASequence: Line 1 (`undo`): undo acts on whatever command is the log's latest when it runs.")]
    [InlineData("redo", "NotInASequence: Line 1 (`redo`): redo acts on whatever command is the log's latest when it runs.")]
    [InlineData("apply", "NotInASequence: Line 1 (`apply`): apply writes the draft of an open dialog, and a sequence has none.")]
    [InlineData("sequence run evening", "NotInASequence: Line 1 (`sequence run evening`): sequence run is a sequence's own verb, and a sequence holds no other.")]
    [InlineData("sequence save evening nudge", "NotInASequence: Line 1 (`sequence save evening nudge`): sequence save is a sequence's own verb, and a sequence holds no other.")]
    [InlineData("watch", "NotInASequence: Line 1 (`watch`): watch neither edits the workspace nor starts work on it.")]
    [InlineData("frobnicate N1", "NotACommand: Line 1 (`frobnicate N1`) begins with no command's words.")]
    [InlineData(" ", "EmptyLine: Line 1 is empty: a sequence's line is a command as it is typed.")]
    public async Task A_line_a_sequence_may_not_hold_refuses_the_save(string line, string refused)
    {
        var rig = new SequenceRig();

        Assert.Equal(refused, (await rig.Save("morning", null, [line])).Refused);
        // The same line after one that is fine is refused by its own number.
        Assert.Equal(refused.Replace("Line 1", "Line 2"), (await rig.Save("morning", null, ["set stations N1 altitude 130", line])).Refused);

        Assert.Empty(rig.Saved.Visible(SequenceRig.Kind, "ana"));
    }

    [Fact] // ADR-0016 note 61: a command that starts work is a sequence's last line or none (§8: it "may end by" one)
    public async Task A_command_that_starts_work_is_a_sequences_last_line()
    {
        var rig = new SequenceRig();

        Assert.Equal("ExecutesBeforeTheEnd: Line 1 (`survey`): survey starts work on the workspace as it stands, so it is a sequence's last line or none.",
            (await rig.Save("morning", null, ["survey", "set stations N1 altitude 130"])).Refused);
        Assert.Empty(rig.Saved.Visible(SequenceRig.Kind, "ana"));

        Assert.Null((await rig.Save("morning", null, ["set stations N1 altitude 130", "survey"])).Refused);
        // Alone, it is the last.
        Assert.Null((await rig.Save("noon", null, ["survey"])).Refused);
    }

    [Fact] // ADR-0016 note 61: a line that names a command and is refused as the workspace stands is kept, and said
    public async Task A_line_refused_as_the_workspace_stands_is_kept_and_said()
    {
        var rig = new SequenceRig();
        Assert.Equal("UnknownRow", rig.Preview("set stations N9 altitude 5").Refused?.Code);

        var (saved, _) = await rig.Save("later", null, ["set stations N1 altitude 130", "set stations N9 altitude 5", "set stations where zzz = 1 altitude 3"]);

        var why = rig.Preview("set stations N9 altitude 5").Refused!.Sentence;
        // Each such line is said with its own reason.
        var other = rig.Preview("set stations where zzz = 1 altitude 3").Refused!.Sentence;
        Assert.NotEqual(why, other);
        Assert.Equal($"Saved 3 lines as the sequence later, for you alone. Lines 2 and 3 are refused on the workspace as it stands (line 2: {why}; line 3: {other}); "
            + "each is read again when the sequence runs, on what the lines before it leave.", saved!["said"]!.GetValue<string>());
        Assert.Equal(3, rig.Kept("later").Length);

        var (one, _) = await rig.Save("one", null, ["set stations N9 altitude 5"]);
        Assert.Equal($"Saved 1 line as the sequence one, for you alone. Line 1 is refused on the workspace as it stands ({why}); "
            + "it is read again when the sequence runs, on what the lines before it leave.", one!["said"]!.GetValue<string>());
    }

    [Fact] // ADR-0016 note 60: one to fifty lines, a name of one word, and whom it is shared with
    public async Task A_save_that_cannot_be_made_is_refused_and_keeps_nothing()
    {
        var rig = new SequenceRig();
        string[] line = ["set stations N1 altitude 130"];

        Assert.StartsWith("ValidationFailed: ", (await rig.Save("morning", null, [])).Refused);
        Assert.Equal("TooManyLines: A sequence holds 50 lines at most, and this has 51.", (await rig.Save("morning", null, [.. Enumerable.Repeat(line[0], 51)])).Refused);
        Assert.Null((await rig.Save("fifty", null, [.. Enumerable.Repeat(line[0], 50)])).Refused);
        Assert.Equal("InvalidName: A sequence's name is one word without @, such as rates-up; a b is not.", (await rig.Save("a b", null, line)).Refused);
        Assert.StartsWith("ValidationFailed: ", (await rig.Save("morning", "team", line)).Refused);
        // Cy is of no desk.
        var board = rig.BoardOf(SequenceRig.Cy);
        Assert.Equal("NoDesk: cy belongs to no desk, so the sequence morning cannot be shared with one: keep it to yourself, or share it with all.",
            (await rig.Save("morning", "desk", line, SequenceRig.Cy, board)).Refused);
        // Another user's workspace is not one to save on.
        Assert.Equal($"NotOwner: Workspace {rig.Id} belongs to another user.", (await rig.Save("morning", null, line, SequenceRig.Ben)).Refused);

        Assert.Equal(["fifty"], rig.Saved.Visible(SequenceRig.Kind, "ana").Select(i => i.Name));
        Assert.Empty(rig.Saved.Visible(SequenceRig.Kind, "cy"));
    }

    static readonly string[] Morning = ["set stations N1 altitude 130", "tick manual", "survey"];

    [Fact] // ADR-0016 note 62: each line is read at the revision the line before left and invoked as its own command
    public async Task A_run_invokes_each_line_as_its_own_command_at_the_revision_the_line_before_left()
    {
        var rig = new SequenceRig();
        await rig.Save("morning", null, Morning);
        var began = rig.Board.Revision;

        var (ran, refused) = await rig.RunSequence("morning");

        Assert.Null(refused);
        var left = rig.Board.Revision;
        Assert.True(left > began);
        Assert.Equal((rig.Id, began, left, "morning", "ana", 3, 3),
            (ran!["workspaceId"]!.GetValue<string>(), ran["began"]!.GetValue<long>(), ran["revision"]!.GetValue<long>(), ran["sequence"]!.GetValue<string>(), ran["owner"]!.GetValue<string>(),
                ran["lines"]!.GetValue<int>(), ran["ran"]!.GetValue<int>()));
        Assert.Equal($"Ran morning: 3 lines, from revision {began} to {left}. Survey 1 was started at revision {left}.", ran["said"]!.GetValue<string>());
        Assert.Null(ran["stopped"]);
        Assert.Null(ran["stays"]);

        // Each line that ran, with the command it named and what that command answered, whole, as text.
        var steps = ran["steps"]!.AsArray();
        Assert.Equal(["1 set stations N1 altitude 130 [entity.set]", "2 tick manual [entity.set]", "3 survey [weather.survey]"],
            steps.Select(s => $"{s!["number"]} {s["line"]} [{s["command"]}]"));
        Assert.Equal([left, left], [steps[1]!["revision"]!.GetValue<long>(), steps[2]!["revision"]!.GetValue<long>()]);
        Assert.Equal(steps[2]!["said"]!.GetValue<string>(), JsonNode.Parse(steps[2]!["answer"]!.GetValue<string>())!["said"]!.GetValue<string>());
        Assert.Equal(1, JsonNode.Parse(steps[2]!["answer"]!.GetValue<string>())!["survey"]!.GetValue<int>());

        // What the lines wrote is written, and the work was started at the revision they left.
        Assert.Equal("130", rig.Written(Stations.Sites, "N1", "altitude"));
        Assert.Equal("on", rig.Written(Stations.Readings, "R1", "checked"));
        Assert.Equal([(left, RunOrigin.Workflow)], rig.Surveys.Started);
        // The log holds each edit as a command of its own, in its own words, from a saved definition (§8).
        Assert.Equal([("set stations N1 altitude 130", RunOrigin.Workflow), ("set manual checked on", RunOrigin.Workflow)], rig.Logged()[^2..]);
    }

    [Fact] // ADR-0016 note 62, §11: undo reaches each line of a run, the last first
    public async Task Undo_takes_a_runs_lines_back_one_at_a_time()
    {
        var rig = new SequenceRig();
        await rig.Save("morning", null, Morning);
        await rig.RunSequence("morning");

        await rig.Run("undo");
        Assert.Equal(("130", "off"), (rig.Written(Stations.Sites, "N1", "altitude"), rig.Written(Stations.Readings, "R1", "checked")));
        await rig.Run("undo");
        Assert.Equal(("120", "off"), (rig.Written(Stations.Sites, "N1", "altitude"), rig.Written(Stations.Readings, "R1", "checked")));
    }

    [Fact] // ADR-0016 note 62: a first line that is refused refuses the command with that line's code, and nothing runs
    public async Task A_first_line_that_is_refused_refuses_the_run()
    {
        var rig = new SequenceRig();
        await rig.Save("later", null, ["set stations N9 altitude 5", "set stations N1 altitude 130", "survey"]);
        var before = rig.Board.Revision;
        var why = rig.Preview("set stations N9 altitude 5").Refused!.Sentence;

        var (_, refused) = await rig.RunSequence("later");

        Assert.Equal($"UnknownRow: Line 1 of later (`set stations N9 altitude 5`) was refused, and nothing ran: {why}", refused);
        Assert.Equal(before, rig.Board.Revision);
        Assert.Empty(rig.Surveys.Started);
    }

    [Fact] // ADR-0016 note 62: a later refusal ends the run, with the lines before it written and where it began said
    public async Task A_later_line_that_is_refused_ends_the_run_and_says_where_it_began()
    {
        var rig = new SequenceRig();
        await rig.Save("later", null, ["set stations N1 altitude 130", "set stations N9 altitude 5", "survey"]);
        var began = rig.Board.Revision;
        var why = rig.Preview("set stations N9 altitude 5").Refused!.Sentence;

        var (ran, refused) = await rig.RunSequence("later");

        // Done, since a line ran: a refusal is a command that left nothing done.
        Assert.Null(refused);
        var left = rig.Board.Revision;
        Assert.Equal((began, left, 3, 1, 2, "UnknownRow", why, true),
            (ran!["began"]!.GetValue<long>(), ran["revision"]!.GetValue<long>(), ran["lines"]!.GetValue<int>(), ran["ran"]!.GetValue<int>(), ran["stopped"]!.GetValue<int>(),
                ran["stoppedCode"]!.GetValue<string>(), ran["stoppedWhy"]!.GetValue<string>(), ran["stays"]!.GetValue<bool>()));
        Assert.Equal($"Stopped at line 2 of later (`set stations N9 altitude 5`): {why} Line 1 ran, from revision {began} to {left}: undo takes it back.", ran["said"]!.GetValue<string>());
        Assert.Single(ran["steps"]!.AsArray());
        Assert.Equal("130", rig.Written(Stations.Sites, "N1", "altitude"));
        Assert.Empty(rig.Surveys.Started);

        // Two lines ran before the one that stopped it.
        await rig.Save("longer", null, ["nudge", "nudge", "set stations N9 altitude 5"]);
        var (longer, _) = await rig.RunSequence("longer");
        Assert.Equal($"Stopped at line 3 of longer (`set stations N9 altitude 5`): {why} Lines 1 and 2 ran, from revision {left} to {rig.Board.Revision}: "
            + "undo takes them back one at a time, the last first.", longer!["said"]!.GetValue<string>());
    }

    [Fact] // ADR-0016 note 62, §6: a line that changed nothing ran, and undo is promised only for what was written
    public async Task A_stopped_run_promises_undo_only_for_the_lines_that_wrote()
    {
        var rig = new SequenceRig();
        // N1 stands at 120: the first line holds the value already, and writes nothing.
        await rig.Save("held", null, ["set stations N1 altitude 120", "nudge", "nudge", "set stations N9 altitude 5"]);
        await rig.Save("none", null, ["set stations N1 altitude 120", "set stations N9 altitude 5"]);
        var why = rig.Preview("set stations N9 altitude 5").Refused!.Sentence;
        var began = rig.Board.Revision;

        var (none, _) = await rig.RunSequence("none");
        Assert.Equal($"Stopped at line 2 of none (`set stations N9 altitude 5`): {why} Line 1 ran and wrote nothing: the workspace is at revision {began} still.",
            none!["said"]!.GetValue<string>());
        Assert.Equal(began, rig.Board.Revision);

        var (held, _) = await rig.RunSequence("held");
        Assert.Equal($"Stopped at line 4 of held (`set stations N9 altitude 5`): {why} Lines 1 to 3 ran, from revision {began} to {rig.Board.Revision}: "
            + "2 of them wrote the workspace, and undo takes those back one at a time, the last first.", held!["said"]!.GetValue<string>());
    }

    [Fact] // ADR-0016 note 62: a workspace another hand moved between two lines stops the run
    public async Task A_workspace_moved_between_two_lines_stops_the_run()
    {
        var rig = new SequenceRig();
        await rig.Save("twice", null, ["nudge", "nudge"]);
        // Another hand writes the board once the first nudge is written, before the second line is read.
        rig.Nudges.After = () =>
        {
            rig.Nudges.After = null;
            rig.Workspaces.Set(rig.Id, rig.Board.Revision, "ana", Stations.Target("station:S1", "altitude"), 16m);
        };

        var (ran, _) = await rig.RunSequence("twice");

        Assert.Equal((1, 2, "RevisionMoved"), (ran!["ran"]!.GetValue<int>(), ran["stopped"]!.GetValue<int>(), ran["stoppedCode"]!.GetValue<string>()));
        Assert.Equal("121", rig.Written(Stations.Sites, "N1", "altitude"));
        // The answer says where the run's own line left the workspace, which is not where it stands.
        Assert.Equal(rig.Board.Revision - 1, ran["revision"]!.GetValue<long>());
    }

    [Fact] // ADR-0016 note 62: what was read is what runs, so a sequence saved again since is refused
    public async Task A_sequence_saved_again_since_it_was_read_is_refused()
    {
        var rig = new SequenceRig();
        await rig.Save("morning", null, ["nudge"]);
        var read = rig.Saved.Visible(SequenceRig.Kind, "ana").Single().SavedAt;
        rig.Clock.Advance(TimeSpan.FromMinutes(1));
        await rig.Save("morning", null, ["survey"]);
        var before = rig.Board.Revision;

        Assert.Equal("SequenceMoved: The sequence morning was saved again after this run was read: read it again.", (await rig.RunSequence("morning", saved: read)).Refused);
        Assert.Equal(before, rig.Board.Revision);
        Assert.Empty(rig.Surveys.Started);

        // Read as it is now, it runs; and a caller that read nothing of it names no moment.
        Assert.Null((await rig.RunSequence("morning", saved: read + TimeSpan.FromMinutes(1))).Refused);
        Assert.Null((await rig.RunSequence("morning")).Refused);
        Assert.Equal(2, rig.Surveys.Started.Count);
    }

    [Fact] // ADR-0016 note 62: a line whose first words name another command than when it was saved is not run blind
    public async Task A_line_whose_first_words_name_another_command_than_when_saved_is_refused()
    {
        var rig = new SequenceRig();
        rig.Keep("old", ("nudge", "weather.nudge"), ("tick manual", "weather.tock"));

        var (ran, _) = await rig.RunSequence("old");

        Assert.Equal((1, 2, "LineMoved", "Its first words named weather.tock when the sequence was saved and name weather.tick now: save the sequence again."),
            (ran!["ran"]!.GetValue<int>(), ran["stopped"]!.GetValue<int>(), ran["stoppedCode"]!.GetValue<string>(), ran["stoppedWhy"]!.GetValue<string>()));
        Assert.Equal("off", rig.Written(Stations.Readings, "R1", "checked"));

        // Words that name nothing any more, as the first line: the run is refused.
        rig.Keep("older", ("gauge all", "weather.gauge"));
        Assert.Equal("LineMoved: Line 1 of older (`gauge all`) was refused, and nothing ran: Its first words named weather.gauge when the sequence was saved and name no command now: "
            + "save the sequence again.", (await rig.RunSequence("older")).Refused);
    }

    [Fact] // ADR-0016 note 61: the rule of what a line may be is read again as each line is reached
    public async Task A_kept_line_a_sequence_may_not_hold_stops_the_run()
    {
        var rig = new SequenceRig();
        rig.Keep("back", ("nudge", "weather.nudge"), ("undo", WorkspaceTakeBack.Undo));
        rig.Keep("early", ("nudge", "weather.nudge"), ("survey", Survey.Id), ("nudge", "weather.nudge"));
        rig.Keep("seen", ("nudge", "weather.nudge"), ("find N1", "find"));

        var (back, _) = await rig.RunSequence("back");
        Assert.Equal(("NotInASequence", "undo acts on whatever command is the log's latest when it runs."), (back!["stoppedCode"]!.GetValue<string>(), back["stoppedWhy"]!.GetValue<string>()));
        Assert.Equal("121", rig.Written(Stations.Sites, "N1", "altitude"));

        var (early, _) = await rig.RunSequence("early");
        Assert.Equal(("ExecutesBeforeTheEnd", 2), (early!["stoppedCode"]!.GetValue<string>(), early["stopped"]!.GetValue<int>()));
        Assert.Empty(rig.Surveys.Started);

        var (seen, _) = await rig.RunSequence("seen");
        Assert.Equal(("NotInASequence", "find is done by a page of the workbench, and a sequence has no screen."), (seen!["stoppedCode"]!.GetValue<string>(), seen["stoppedWhy"]!.GetValue<string>()));
    }

    [Fact] // ADR-0016 note 62, ADR-0005: asked again with the same key, a run answers what it answered and runs nothing twice
    public async Task A_run_asked_again_with_the_same_key_runs_nothing_twice()
    {
        var rig = new SequenceRig();
        await rig.Save("morning", null, ["nudge", "survey"]);
        var began = rig.Board.Revision;

        var (first, _) = await rig.RunSequence("morning", revision: began, key: "one-run");
        var left = rig.Board.Revision;
        var (again, refused) = await rig.RunSequence("morning", revision: began, key: "one-run");

        Assert.Null(refused);
        Assert.Equal(first!.ToJsonString(), again!.ToJsonString());
        Assert.Equal((left, 1, "121"), (rig.Board.Revision, rig.Surveys.Started.Count, rig.Written(Stations.Sites, "N1", "altitude")));
        // Another key at the revision the first run read is a run that read too late.
        Assert.StartsWith("RevisionMoved: ", (await rig.RunSequence("morning", revision: began, key: "another")).Refused);
    }

    [Fact] // ADR-0016 note 60, C32: a shared sequence runs on the caller's own workspace, and a private one is not seen
    public async Task A_shared_sequence_runs_on_the_callers_own_workspace_and_a_private_one_is_not_seen()
    {
        var rig = new SequenceRig();
        await rig.Save("mine", null, ["nudge"]);
        await rig.Save("ours", "desk", ["nudge"]);
        var (bens, cys) = (rig.BoardOf(SequenceRig.Ben), rig.BoardOf(SequenceRig.Cy));
        var anas = rig.Board.Revision;

        var (ran, _) = await rig.RunSequence("ours", SequenceRig.Ben, bens);
        Assert.Equal(("ours", "ana", 1), (ran!["sequence"]!.GetValue<string>(), ran["owner"]!.GetValue<string>(), ran["ran"]!.GetValue<int>()));
        Assert.Equal(("121", "120", anas), (rig.Written(Stations.Sites, "N1", "altitude", bens), rig.Written(Stations.Sites, "N1", "altitude"), rig.Board.Revision));

        Assert.Equal("UnknownSaved: There is no sequence mine you can see: there are ours.", (await rig.RunSequence("mine", SequenceRig.Ben, bens)).Refused);
        Assert.Equal("UnknownSaved: There is no sequence ours you can see: none is saved for you, your desk or all.", (await rig.RunSequence("ours", SequenceRig.Cy, cys)).Refused);
        // A sequence one can see is still run on a workspace of one's own.
        Assert.Equal($"NotOwner: Workspace {rig.Id} belongs to another user.", (await rig.RunSequence("ours", SequenceRig.Ben)).Refused);

        // Two of a name are told apart by their owners.
        await rig.Save("ours", null, ["survey"], SequenceRig.Ben, bens);
        Assert.Equal("AmbiguousSaved: 2 sequences named ours can be seen, ana's and ben's: name one, as ours@ana or ours@ben.", (await rig.RunSequence("ours", SequenceRig.Ben, bens)).Refused);
        var (anas2, _) = await rig.RunSequence("ours@ana", SequenceRig.Ben, bens);
        Assert.Equal(("ours@ana", "122"), (anas2!["sequence"]!.GetValue<string>(), rig.Written(Stations.Sites, "N1", "altitude", bens)));
    }

    [Fact] // ADR-0016 note 62: a sequence the AI runs keeps the AI's origin for its lines
    public async Task A_sequence_the_AI_runs_keeps_the_AIs_origin_for_its_lines()
    {
        var rig = new SequenceRig();
        await rig.Save("morning", null, ["nudge", "survey"]);

        Assert.Null((await rig.RunSequence("morning", new Caller("ana", RunOrigin.Ai))).Refused);

        Assert.Equal(RunOrigin.Ai, rig.Logged()[^1].Origin);
        Assert.Equal(RunOrigin.Ai, rig.Surveys.Started.Single().Origin);
    }

    [Fact] // ADR-0016 note 62: a command that says nothing of where it left the workspace cannot be chained, and that is its defect
    public async Task A_command_that_answers_no_revision_is_a_defect_and_not_a_refusal()
    {
        var rig = new SequenceRig();
        await rig.Save("twice", null, ["nudge", "nudge"]);
        rig.Nudges.Silent = true;

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.RunSequence("twice"));

        Assert.Equal("weather.nudge answered without the revision it left the workspace at and what it did, so line 1 of twice (`nudge`) cannot be followed by another.", thrown.Message);
    }

    [Fact] // ADR-0016 note 62, ADR-0005: each line has a key of its own made from the run's, so a run asked again after it broke runs no line twice
    public async Task A_run_that_broke_and_is_asked_again_with_its_key_runs_no_line_twice()
    {
        var rig = new SequenceRig();
        await rig.Save("twice", null, ["nudge", "nudge"]);
        // The second nudge writes, and answers nothing of where it left the board: the run breaks there.
        var nudged = 0;
        rig.Nudges.After = () => rig.Nudges.Silent = ++nudged == 2;
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.RunSequence("twice", key: "one-run"));
        Assert.Equal("122", rig.Written(Stations.Sites, "N1", "altitude"));
        rig.Nudges.After = null;
        rig.Nudges.Silent = false;

        var (again, refused) = await rig.RunSequence("twice", key: "one-run");

        // Line 1's key was used for the board as it then stood: it is not run again on the board as it stands now.
        Assert.Null(again);
        Assert.StartsWith("IdempotencyConflict: Line 1 of twice (`nudge`) was refused, and nothing ran: ", refused);
        Assert.Equal("122", rig.Written(Stations.Sites, "N1", "altitude"));
    }

    [Fact] // ADR-0016 notes 33, 62 and 64: what a line's own command said waits for the user is said with the run's, and the run's notice stays
    public async Task A_line_whose_answer_waits_for_the_user_keeps_the_runs_notice()
    {
        var rig = new SequenceRig();
        await rig.Save("plain", null, ["nudge", "set stations S1 altitude 130"]);
        await rig.Save("morning", null, ["nudge", "set stations S1 altitude 131"]);
        await rig.Save("noon", null, ["set stations S1 altitude 140", "nudge"]);
        var began = rig.Board.Revision;

        // No line waits: the notice leaves by itself.
        var (plain, _) = await rig.RunSequence("plain");
        Assert.Equal((2, null), (plain!["ran"]!.GetValue<int>(), plain["stays"]));

        rig.Nudges.Waits = true;
        var (first, _) = await rig.RunSequence("morning");
        var (last, _) = await rig.RunSequence("noon");

        // A line before the last: its sentence is said after the run's, with its number.
        var set = first!["steps"]![1]!["said"]!.GetValue<string>();
        Assert.Equal((true, $"Ran morning: 2 lines, from revision {began + 2} to {began + 4}. {set} Line 1 (`nudge`): Raised N1 to 122."),
            (first["stays"]!.GetValue<bool>(), first["said"]!.GetValue<string>()));
        // The last line: its sentence is the run's already, said once.
        Assert.Equal((true, $"Ran noon: 2 lines, from revision {began + 4} to {began + 6}. Raised N1 to 123."), (last!["stays"]!.GetValue<bool>(), last["said"]!.GetValue<string>()));
    }

    [Fact] // ADR-0016 §11, note 62: a stopped run promises of undo what undo reaches: a line no undo takes back keeps it from the lines before
    public async Task A_stopped_run_says_what_undo_reaches_past_a_line_it_cannot_take_back()
    {
        var rig = new SequenceRig();
        await rig.Save("later", null, ["nudge", "resend", "nudge", "set stations N9 altitude 5"]);
        var why = rig.Preview("set stations N9 altitude 5").Refused!.Sentence;
        var began = rig.Board.Revision;

        var (later, _) = await rig.RunSequence("later");

        var left = rig.Board.Revision;
        Assert.Equal($"Stopped at line 4 of later (`set stations N9 altitude 5`): {why} Lines 1 to 3 ran, from revision {began} to {left}: "
            + "line 2 (`resend`) took another source version (sent-2), so undo neither takes it back nor reaches a line before it, and takes back line 3 alone.",
            later!["said"]!.GetValue<string>());
        // And so it is: undo takes line 3 back, and is refused at line 2.
        await rig.Run("undo");
        Assert.Equal(("121", "CannotUndo"), (rig.Written(Stations.Sites, "N1", "altitude"), rig.Preview("undo").Refused?.Code));

        // Where it is the last line that wrote, undo takes nothing back.
        var fresh = new SequenceRig();
        await fresh.Save("sooner", null, ["nudge", "resend", "set stations N9 altitude 5"]);
        var (sooner, _) = await fresh.RunSequence("sooner");
        Assert.EndsWith("line 2 (`resend`) took another source version (sent-2), so undo neither takes it back nor reaches a line before it.", sooner!["said"]!.GetValue<string>());

        // Of two such lines the later is the one undo stops at.
        await fresh.Save("twice", null, ["resend", "nudge", "resend", "nudge", "nudge", "set stations N9 altitude 5"]);
        var (twice, _) = await fresh.RunSequence("twice");
        Assert.EndsWith("line 3 (`resend`) took another source version (sent-4), so undo neither takes it back nor reaches a line before it, and takes back the 2 lines after it that wrote, "
            + "the last first.", twice!["said"]!.GetValue<string>());
    }

    [Fact] // ADR-0016 note 61: a short form is held to the rule by the line it stands for, at the save and when it runs
    public async Task A_short_form_is_held_to_the_rule_by_the_line_it_stands_for()
    {
        var rig = new SequenceRig();

        // `poll` stands for `survey`, which starts work on the board.
        Assert.Equal("ExecutesBeforeTheEnd: Line 1 (`poll`): poll starts work on the workspace as it stands, so it is a sequence's last line or none.",
            (await rig.Save("morning", null, ["poll", "nudge"])).Refused);
        Assert.Null((await rig.Save("morning", null, ["nudge", "poll"])).Refused);
        Assert.Equal(["nudge [weather.nudge]", "poll [weather.poll]"], rig.Kept("morning"));
        // The line offers no `then` after it.
        Assert.DoesNotContain("then", rig.Preview("sequence save noon poll ").Complete.Select(c => c.Value));

        var (ran, _) = await rig.RunSequence("morning");
        Assert.Equal((2, 1), (ran!["ran"]!.GetValue<int>(), rig.Surveys.Started.Count));
        // Kept before the rule held it, it stops the run where it stands.
        rig.Keep("older", ("poll", "weather.poll"), ("nudge", "weather.nudge"));
        Assert.StartsWith("ExecutesBeforeTheEnd: Line 1 of older (`poll`) was refused, and nothing ran: ", (await rig.RunSequence("older")).Refused);
    }

    [Fact] // ADR-0016 note 60: a kept sequence that does not hold its lines as a save writes them is the store's defect, and is not read as a shorter one
    public void A_kept_sequence_that_does_not_hold_its_lines_is_a_defect()
    {
        var none = new SequenceRig();
        none.Keep("empty");
        Assert.Equal("The saved sequence empty of ana does not hold its lines as a sequence's are kept.", Assert.Throws<InvalidOperationException>(() => none.Preview("sequence run empty")).Message);

        var odd = new SequenceRig();
        odd.Saved.Save(SequenceRig.Kind, "sequence", "ana", "odd", SavedVisibility.Private, new JsonObject { ["lines"] = new JsonArray(new JsonObject { ["line"] = "nudge" }) });
        Assert.Throws<InvalidOperationException>(() => odd.Preview("sequence run odd"));
    }

    [Fact] // ADR-0016 note 63: within a line a sequence may not hold nothing is offered, and the line is not read: a sequence's own verb typed ever deeper is refused as it stands
    public async Task A_line_a_sequence_may_not_hold_is_offered_nothing_and_is_not_read()
    {
        var rig = new SequenceRig();
        await rig.Save("morning", null, ["nudge"]);
        // `sequence run` offers the sequences one can see where it is typed as the line; as a line of a sequence, nothing.
        Assert.Equal(["morning"], rig.Preview("sequence run mor").Complete.Select(c => c.Value));
        Assert.Empty(rig.Preview("sequence save noon sequence run mor").Complete);

        var deep = rig.Preview("sequence save a " + string.Concat(Enumerable.Repeat("sequence save a ", 3000)) + "nudge");

        Assert.Equal("NotInASequence", deep.Refused?.Code);
        Assert.Empty(deep.Complete);
    }

    [Fact] // ADR-0016 note 63: `sequence save` reads its lines apart at the word then, and says each with its number
    public async Task The_line_sequence_save_reads_its_lines_apart_at_then()
    {
        var rig = new SequenceRig();
        const string typed = "sequence save morning set stations N1 altitude 130 then  tick manual THEN survey";

        var read = rig.Preview(typed);

        Assert.Null(read.Refused);
        Assert.Equal(SequenceSave.Id, read.Command!.Id);
        Assert.Equal("Saves 3 lines as the sequence morning, for you alone. 1. `set stations N1 altitude 130` 2. `tick manual` 3. `survey`", read.Would);
        Assert.Equal(["name morning", "lines set stations N1 altitude 130 then tick manual then survey"], read.Arguments!.Select(a => $"{a.Name} {a.Text}"));
        var request = read.Inputs!["request"]!.AsObject();
        Assert.Equal((rig.Id, "morning", false), (request["workspaceId"]!.GetValue<string>(), request["name"]!.GetValue<string>(), request.ContainsKey("share")));
        Assert.Equal(Morning, request["lines"]!.AsArray().Select(l => l!.GetValue<string>()));
        // A reading keeps nothing; Enter keeps what was read.
        Assert.Empty(rig.Saved.Visible(SequenceRig.Kind, "ana"));
        await rig.Run(typed);
        Assert.Equal(["set stations N1 altitude 130 [entity.set]", "tick manual [weather.tick]", "survey [weather.survey]"], rig.Kept("morning"));

        // Whom it is for stands after the name, and a save over one's own says so.
        var shared = rig.Preview("sequence save morning Desk survey");
        Assert.Equal("Saves 1 line as the sequence morning, for the desk north, replacing your own of that name. 1. `survey`", shared.Would);
        Assert.Equal(["name morning", "share desk", "lines survey"], shared.Arguments!.Select(a => $"{a.Name} {a.Text}"));
        Assert.Equal("desk", shared.Inputs!["request"]!["share"]!.GetValue<string>());
        Assert.Equal("Saves 1 line as the sequence noon, for every user. 1. `nudge`", rig.Read("sequence save noon all nudge"));
        // A word that only holds then is no end of a line.
        Assert.Equal("Saves 1 line as the sequence athens, for you alone. 1. `nudge`", rig.Read("sequence save athens nudge"));
    }

    [Fact] // ADR-0016 notes 61 and 63: the save's reading refuses what the save refuses, and says the lines refused as the workspace stands
    public void The_saves_reading_refuses_what_the_save_refuses_and_keeps_nothing()
    {
        var rig = new SequenceRig();
        var why = rig.Preview("set stations N9 altitude 5").Refused!.Sentence;

        Assert.Equal("InvalidName: A sequence needs a name, such as rates-up.", rig.Read("sequence save"));
        Assert.Equal("NoLines: The sequence morning needs its lines: each a command as it is typed, with the word then between two.", rig.Read("sequence save morning"));
        Assert.Equal("NoLines: The sequence morning needs its lines: each a command as it is typed, with the word then between two.", rig.Read("sequence save morning desk "));
        Assert.Equal("EmptyLine: Line 2 is empty: a sequence's line is a command as it is typed.", rig.Read("sequence save morning nudge then"));
        Assert.Equal("EmptyLine: Line 1 is empty: a sequence's line is a command as it is typed.", rig.Read("sequence save morning then nudge"));
        Assert.Equal("NotInASequence: Line 2 (`undo`): undo acts on whatever command is the log's latest when it runs.", rig.Read("sequence save morning nudge then undo"));
        Assert.Equal("ExecutesBeforeTheEnd: Line 1 (`survey`): survey starts work on the workspace as it stands, so it is a sequence's last line or none.",
            rig.Read("sequence save morning survey then nudge"));
        Assert.Equal("NotACommand: Line 1 (`frobnicate N1`) begins with no command's words.", rig.Read("sequence save morning frobnicate N1"));
        Assert.Equal("InvalidName: A sequence's name is one word without @, such as rates-up; a@b is not.", rig.Read("sequence save a@b nudge"));
        Assert.Equal("NoDesk: cy belongs to no desk, so the sequence morning cannot be shared with one: keep it to yourself, or share it with all.",
            rig.Read("sequence save morning desk nudge", SequenceRig.Cy, rig.BoardOf(SequenceRig.Cy)));

        // A line that names a command and is refused where the workspace stands is kept, and the reading says so before the lines.
        Assert.Equal($"Saves 2 lines as the sequence later, for you alone. Line 2 is refused on the workspace as it stands ({why}); "
            + "it is read again when the sequence runs, on what the lines before it leave. 1. `nudge` 2. `set stations N9 altitude 5`",
            rig.Read("sequence save later nudge then set stations N9 altitude 5"));

        Assert.Empty(rig.Saved.Visible(SequenceRig.Kind, "ana"));
        Assert.Empty(rig.Saved.Visible(SequenceRig.Kind, "cy"));
    }

    [Fact] // ADR-0016 note 63: `sequence run` says every line, and reads the first on the workspace as it stands
    public async Task The_line_sequence_run_says_every_line_and_reads_the_first()
    {
        var rig = new SequenceRig();
        await rig.Save("morning", null, Morning);
        await rig.Save("once", "desk", ["nudge"]);
        var first = rig.Preview("set stations N1 altitude 130").Would;
        var at = rig.Saved.Visible(SequenceRig.Kind, "ana").Single(i => i.Name == "morning").SavedAt;
        var stands = rig.Board.Revision;

        var read = rig.Preview("sequence run morning");

        Assert.Null(read.Refused);
        Assert.Equal(SequenceRun.Id, read.Command!.Id);
        Assert.Equal("Runs morning, of 3 lines, each as its own command, until one is refused: 1. `set stations N1 altitude 130` 2. `tick manual` 3. `survey`. "
            + $"Each line after the first is read as it is reached, on what the lines before it leave. Line 1, as the workspace stands: {first}", read.Would);
        Assert.Equal(["sequence morning"], read.Arguments!.Select(a => $"{a.Name} {a.Text}"));
        var request = read.Inputs!["request"]!;
        // The run is asked for by the sequence's owner and the moment it was saved: what was read is what runs.
        Assert.Equal((rig.Id, stands, "morning@ana", at),
            (request["workspaceId"]!.GetValue<string>(), request["revision"]!.GetValue<long>(), request["sequence"]!.GetValue<string>(), DateTimeOffset.Parse(request["saved"]!.GetValue<string>(),
                System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind)));
        // A reading runs nothing.
        Assert.Equal((stands, 0), (rig.Board.Revision, rig.Surveys.Started.Count));

        var ran = await rig.Run("sequence run morning");
        Assert.Equal((3, 1), (ran["ran"]!.GetValue<int>(), rig.Surveys.Started.Count));

        // One line; and another's sequence says whose it is.
        Assert.Equal("Runs once, of 1 line: 1. `nudge`. As the workspace stands: Raises N1 by one.", rig.Read("sequence run once"));
        Assert.Equal("Runs once (saved by ana), of 1 line: 1. `nudge`. As the workspace stands: Raises N1 by one.", rig.Read("sequence run once", SequenceRig.Ben, rig.BoardOf(SequenceRig.Ben)));
    }

    [Fact] // ADR-0016 note 63: a first line that is refused refuses the run's reading, as it would refuse the run
    public async Task A_run_whose_first_line_is_refused_refuses_its_reading()
    {
        var rig = new SequenceRig();
        Assert.Equal("SequenceNotNamed: sequence run takes a saved sequence's name, and none is saved for you, your desk or all: sequence save keeps one.", rig.Read("sequence run"));
        await rig.Save("later", null, ["set stations N9 altitude 5", "nudge"]);
        await rig.Save("morning", null, Morning);
        rig.Keep("older", ("gauge all", "weather.gauge"));
        var why = rig.Preview("set stations N9 altitude 5").Refused!.Sentence;

        Assert.Equal($"UnknownRow: Line 1 of later (`set stations N9 altitude 5`) is refused as the workspace stands, and nothing would run: {why}", rig.Read("sequence run later"));
        Assert.Equal("LineMoved: Line 1 of older (`gauge all`) is refused as the workspace stands, and nothing would run: "
            + "Its first words named weather.gauge when the sequence was saved and name no command now: save the sequence again.", rig.Read("sequence run older"));
        Assert.Equal("SequenceNotNamed: sequence run takes a saved sequence's name: later, morning and older.", rig.Read("sequence run "));
        Assert.Equal("UnknownSaved: There is no sequence noon you can see: there are later, morning, older.", rig.Read("sequence run noon"));
        var more = rig.Preview("sequence run morning now");
        Assert.Equal(("UnreadWords", "sequence run takes a sequence's name, and no word after it.", "now"), (more.Refused!.Code, more.Refused.Sentence, more.Refused.Word));
    }

    [Fact] // ADR-0016 note 63: the line offers whom to share with, the commands a sequence may hold where a line starts, and then
    public void The_save_line_offers_whom_it_is_for_the_commands_a_sequence_may_hold_and_then()
    {
        var rig = new SequenceRig();
        string[] Offered(string line) => [.. rig.Preview(line).Complete.Select(c => $"{c.Value}@{c.Start}")];

        // The name is the user's to make.
        Assert.Empty(Offered("sequence save "));
        Assert.Empty(Offered("sequence save mor"));

        // After the name: whom it is for, then what a first line may begin with.
        var after = rig.Preview("sequence save morning ").Complete;
        Assert.Equal(["desk: your desk sees it", "all: every user sees it"], after.Take(2).Select(c => $"{c.Value}: {c.Detail}"));
        Assert.All(after, c => Assert.Equal(22, c.Start));
        var begins = after.Skip(2).Select(c => c.Value).ToList();
        Assert.Superset(new HashSet<string> { "set", "reset", "remove", "new", "nudge", "survey", "tick" }, begins.ToHashSet());
        // What a sequence may not hold is not offered: a verb of the screen, undo, a dialog's apply, a sequence's own verbs, what neither edits nor starts work.
        Assert.Empty(begins.Intersect(["find", "sort", "open", "cancel", "undo", "redo", "apply", "sequence save", "sequence run", "watch"]));
        Assert.Equal(["desk@22"], Offered("sequence save morning de"));
        Assert.Equal(["survey@22"], Offered("sequence save morning su"));

        // After whom it is for, and after then, a line begins: the same commands, and no desk.
        Assert.Equal(begins, rig.Preview("sequence save morning desk ").Complete.Select(c => c.Value));
        Assert.Equal(begins.Select(c => $"{c}@33"), Offered("sequence save morning nudge then "));
        Assert.Equal(["survey@33"], Offered("sequence save morning nudge then su"));

        // Within a line, what the line's own command offers, placed in the whole line.
        var own = rig.Preview("set stations N1 ").Complete;
        Assert.NotEmpty(own);
        Assert.Equal(own.Select(c => $"{c.Value}@{c.Start + 22}"), Offered("sequence save morning set stations N1 "));

        // After a line that reads and may be followed, then; a command that starts work is the last line, and is followed by none.
        Assert.Equal(["then@28"], Offered("sequence save morning nudge "));
        Assert.Equal(["then@28"], Offered("sequence save morning nudge th"));
        Assert.Empty(Offered("sequence save morning survey "));
    }

    [Fact] // ADR-0016 note 63: `sequence run` offers the sequences the user can see, each with how many lines it has and whose it is
    public async Task The_run_line_offers_the_sequences_one_can_see()
    {
        var rig = new SequenceRig();
        await rig.Save("morning", null, Morning);
        await rig.Save("ours", "desk", ["nudge"]);
        await rig.Save("open", "all", ["nudge", "nudge"]);
        var bens = rig.BoardOf(SequenceRig.Ben);
        string[] Offered(string line, Caller? caller = null, string? board = null) => [.. rig.Preview(line, caller, board).Complete.Select(c => $"{c.Value}@{c.Start}: {c.Detail}")];

        Assert.Empty(Offered("sequence run"));
        Assert.Equal(["morning@13: 3 lines, for you alone, saved by ana", "open@13: 2 lines, for every user, saved by ana", "ours@13: 1 line, for the desk north, saved by ana"],
            Offered("sequence run "));
        Assert.Equal(["open@13: 2 lines, for every user, saved by ana", "ours@13: 1 line, for the desk north, saved by ana"], Offered("sequence run O"));
        Assert.Equal(["open@13: 2 lines, for every user, saved by ana", "ours@13: 1 line, for the desk north, saved by ana"], Offered("sequence run ", SequenceRig.Ben, bens));
        Assert.Equal(["open@13: 2 lines, for every user, saved by ana"], Offered("sequence run ", SequenceRig.Cy, rig.BoardOf(SequenceRig.Cy)));
    }

    [Fact] // ADR-0016 note 62, §11: undo says whose command it would take back, and one a sequence ran is the sequence's for the user
    public async Task Undo_says_that_a_saved_sequence_ran_the_command_it_takes_back()
    {
        var rig = new SequenceRig();
        await rig.Save("morning", null, ["set stations N1 altitude 130"]);
        await rig.RunSequence("morning");

        Assert.Equal("Takes back: set stations N1 altitude 130 (a saved sequence for you, 1 edit)", rig.Read("undo"));
    }
}
