using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Qube.Composition.Application.Saved;
using Qube.Composition.Application.Tables;
using Qube.Composition.Application.Workspaces;
using Qube.Composition.Contracts;
using static Qube.Composition.Application.ContractShapes;

namespace Qube.Composition.Application;

// ADR-0016 §8 and its notes 60 to 63 of 2026-11-12: a saved sequence of edits. It is lines as a person types them,
// kept under a name as a scenario set is (C32), read again by the server's reader each time it runs and invoked
// through the command gateway, each as its own command. Nothing here knows a table, a command or an application.

/// <summary>A line of a saved sequence (note 60): the command as typed, and what its first words named when it was saved.</summary>
public sealed record SequenceLine(string Line, string Names);

/// <summary>A saved sequence as a user sees it: the name they say it by, whose it is and for whom, when it was saved, and its lines in order.</summary>
/// <param name="Label">Its name alone, or with its owner where another the user can see shares the name.</param>
public sealed record SavedSequence(string Label, string Name, string Owner, SavedVisibility Visibility, string? Desk, DateTimeOffset SavedAt, IReadOnlyList<SequenceLine> Lines);

/// <summary>A line a save keeps though it is refused as the workspace stands (note 61): its number, and why.</summary>
public sealed record UnreadLine(int Number, string Why);

/// <summary>
/// What a save keeps, or would keep: the sequence, whether it replaces its owner's own, the lines that are refused as
/// the workspace stands, and the revision that workspace stood at, which a save does not move.
/// </summary>
public sealed record SequenceSaved(SavedSequence Sequence, bool Replaced, IReadOnlyList<UnreadLine> Unread, long Revision);

/// <summary>A line of a run that ran (note 62): its number, the command it named, what that command said, the revision it left, and its whole answer.</summary>
/// <param name="Wrote">Whether it moved the workspace: a command that changed nothing is done, and is none that <c>undo</c> takes back.</param>
public sealed record SequenceStep(int Number, string Line, string Command, string Said, long Revision, bool Wrote, JsonObject Answer);

/// <summary>The line a run stopped at, and why: the refusal's code and its sentence.</summary>
public sealed record SequenceStop(int Number, string Line, string Code, string Why);

/// <summary>
/// A run (note 62): the sequence, the revision the workspace was at when it began and the one the run's last line left
/// it at, each line that ran in order, and the line it stopped at where one was refused.
/// </summary>
public sealed record SequenceRan(string WorkspaceId, SavedSequence Sequence, long Began, long Left, IReadOnlyList<SequenceStep> Steps, SequenceStop? Stopped);

/// <summary>
/// The rules of saved sequences of edits (ADR-0016 §8, notes 60 to 62): what a line of one may be, how one is kept,
/// and how one runs. The lines are read by <see cref="CommandLines"/> and run by <see cref="CommandService"/>, each
/// asked for when first needed, as the workspaces are: they are built from the registry the sequence's own commands
/// are part of.
/// </summary>
public sealed class Sequences(Func<CommandLines> lines, Func<CommandService> commands, Func<WorkspaceService> workspaces, SavedItems saved)
{
    /// <summary>The most lines a sequence holds (note 60): it runs in one call, so the cap is on what is executed.</summary>
    public const int MostLines = 50;

    const string Noun = "sequence";

    /// <summary>The kind a sequence is kept under: one per application, since a command is of an application's workspaces (note 27).</summary>
    public static string Kind(string app) => $"sequence:{app}";

    /// <summary>
    /// Keeps the lines under the caller's name for them, on the caller's own workspace's application, shared as asked
    /// (note 60). Refused, and nothing kept, where a line may not be a sequence's (note 61).
    /// </summary>
    public SequenceSaved Save(string workspaceId, Caller caller, string name, string? share, IReadOnlyList<string> typed) =>
        Saving(WorkspaceCommands.Owned(workspaces(), workspaceId, caller), caller, name, share, typed, keep: true);

    /// <summary>The save as it would be made: checked as <see cref="Save"/> checks it, and nothing kept.</summary>
    public SequenceSaved Plan(WorkspaceState state, Caller caller, string name, string? share, IReadOnlyList<string> typed) => Saving(state, caller, name, share, typed, keep: false);

    SequenceSaved Saving(WorkspaceState state, Caller caller, string name, string? share, IReadOnlyList<string> typed, bool keep)
    {
        var visibility = share?.Trim().ToLowerInvariant() switch
        {
            null or "" => SavedVisibility.Private,
            "desk" => SavedVisibility.Desk,
            "all" => SavedVisibility.All,
            var other => throw new WorkspaceRefused("InvalidShare", $"A sequence is shared with desk or all, not {other}."),
        };
        var kind = Kind(state.App);
        // The name and whom it is for are checked before the lines, as the line is typed: a save of no line yet says so.
        var (planned, replaced) = Kept(() => saved.Planned(kind, Noun, caller.UserId, name, visibility, Content([])));
        if (typed.Count == 0)
            throw new WorkspaceRefused("NoLines", $"The sequence {planned.Name} needs its lines: each a command as it is typed, with the word {SequenceSave.Then} between two.");
        if (typed.Count > MostLines) throw new WorkspaceRefused("TooManyLines", $"A sequence holds {MostLines} lines at most, and this has {typed.Count}.");

        var kept = new List<SequenceLine>();
        var unread = new List<UnreadLine>();
        foreach (var (line, number) in typed.Select((l, i) => (l.Trim(), i + 1)))
        {
            if (line.Length == 0) throw new WorkspaceRefused("EmptyLine", $"Line {number} is empty: a sequence's line is a command as it is typed.");
            var names = lines().Names(line, state) ?? throw new WorkspaceRefused("NotACommand", $"{Numbered(number, line)} begins with no command's words.");
            WorkspaceRefused Refused((string Code, string Why) barred) => new(barred.Code, $"{Numbered(number, line)}: {barred.Why}");
            if (Barred(names, number, typed.Count) is { } named) throw Refused(named);
            var read = lines().Read(line, state, caller);
            // What reads a screen can never be read by a sequence, whatever the workspace holds by then (§9).
            if (read.Refused is { Code: "NoView" } screen) throw Refused((screen.Code, screen.Sentence));
            // Any other refusal is of the workspace as it stands: what the line names may be made by a line before it.
            if (read.Refused is { } refused) unread.Add(new(number, refused.Sentence));
            else if (BarredAsRead(names, read, number, typed.Count, state) is { } meant) throw Refused(meant);
            kept.Add(new(line, names.Id));
        }
        var item = keep ? Kept(() => saved.Save(kind, Noun, caller.UserId, name, visibility, Content(kept))).Item : planned;
        return new(new(item.Name, item.Name, item.Owner, item.Visibility, item.Desk, item.SavedAt, kept), replaced, unread, state.Revision);
    }

    /// <summary>The sequences of the workspace's application the user can see (note 60): their own, their desk's and all's, by name and owner.</summary>
    public IReadOnlyList<SavedSequence> Visible(WorkspaceState state, string user)
    {
        var visible = saved.Visible(Kind(state.App), user);
        return [.. visible.Select(i => Seen(i, SavedItems.Label(i, visible)))];
    }

    /// <summary>The sequence the user names, as <c>&lt;name&gt;</c> or <c>&lt;name&gt;@&lt;owner&gt;</c>, among those they can see; none, or a name two share, is refused.</summary>
    public SavedSequence Find(WorkspaceState state, string user, string named)
    {
        var kind = Kind(state.App);
        var item = Kept(() => saved.Find(kind, Noun, user, named));
        return Seen(item, SavedItems.Label(item, saved.Visible(kind, user)));
    }

    static SavedSequence Seen(SavedItem item, string label)
    {
        InvalidOperationException Unread() => new($"The saved sequence {item.Name} of {item.Owner} does not hold its lines as a sequence's are kept.");
        IReadOnlyList<SequenceLine> kept =
            [.. (item.Content["lines"] as JsonArray ?? throw Unread()).Select(l => new SequenceLine(l?["line"]?.GetValue<string>() ?? throw Unread(), l["names"]?.GetValue<string>() ?? throw Unread()))];
        // A save keeps one line at least (note 60).
        return kept.Count == 0 ? throw Unread() : new(label, item.Name, item.Owner, item.Visibility, item.Desk, item.SavedAt, kept);
    }

    /// <summary>
    /// A run as it would begin (note 63): the sequence the user names, and its first line read on the workspace as it
    /// stands, for the caller the run's lines would run for. Refused where that line is, as the run would be.
    /// </summary>
    public (SavedSequence Sequence, LinePreview First) Begins(WorkspaceState state, Caller caller, string named)
    {
        var sequence = Find(state, caller.UserId, named);
        var (read, stopped) = Reads(sequence, 1, state, () => lines().Read(sequence.Lines[0].Line, state, Runner(caller)));
        if (stopped is not null)
            throw new WorkspaceRefused(stopped.Code, $"Line 1 of {sequence.Label} (`{stopped.Line}`) is refused as the workspace stands, and nothing would run: {stopped.Why}");
        return (sequence, read ?? throw new InvalidOperationException("A line of a sequence is either read or stops the run."));
    }

    /// <summary>
    /// What could follow a line of a sequence as it is typed (note 63), places counted from the line's start: where it
    /// begins, the commands a sequence may hold; within it, what its own command offers; and nothing for a line a
    /// sequence may not hold.
    /// </summary>
    public LineCompletions Completes(WorkspaceState state, Caller caller, string line)
    {
        var read = lines().Read(line, state, caller);
        if (lines().Names(line, state) is { } names) return Barred(names, 1, 1) is null ? new(read.Complete, read.More) : LineCompletions.None;
        // No command is named yet: the line's first word is being typed, and what is offered is commands' words.
        return new([.. read.Complete.Where(c => lines().Names(c.Value, state) is { } offered && Barred(offered, 1, 1) is null)]);
    }

    /// <summary>
    /// Whether a line could stand in a sequence with another after it, as the workspace stands (note 63): it reads, and
    /// what it names may be a line that is not the last. What the line offers the word <c>then</c> after.
    /// </summary>
    public bool Follows(WorkspaceState state, Caller caller, string line)
    {
        if (lines().Names(line, state) is not { } names || Barred(names, 1, 2) is not null) return false;
        var read = lines().Read(line, state, caller);
        return read.Refused is null && BarredAsRead(names, read, 1, 2, state) is null;
    }

    // §8: the lines run with origin workflow. The AI's keep its own, so that the log does not say that no AI wrote
    // what the AI ran (note 62).
    static Caller Runner(Caller caller) => caller.Origin == RunOrigin.Ai ? caller : caller with { Origin = RunOrigin.Workflow };

    /// <summary>
    /// Runs a sequence on the caller's own workspace (note 62). Each line is read by the server's reader at the revision
    /// the line before it left, the first at the revision the caller read, and the command it names is invoked through
    /// the gateway as its own command. A first line that is refused refuses the run; a later one ends it, with the
    /// lines before it written. <paramref name="read"/> is the moment the sequence was saved as the caller read it: one
    /// saved again since is refused. <paramref name="key"/> is the key the run was asked with, from which each line's
    /// own is made.
    /// </summary>
    public async Task<SequenceRan> RunAsync(string workspaceId, long? revision, Caller caller, string named, DateTimeOffset? read, string key, CancellationToken cancellationToken)
    {
        var state = WorkspaceCommands.Owned(workspaces(), workspaceId, caller, revision);
        var sequence = Find(state, caller.UserId, named);
        if (read is { } then && then != sequence.SavedAt)
            throw new WorkspaceRefused("SequenceMoved", $"The sequence {sequence.Label} was saved again after this run was read: read it again.");
        var runner = Runner(caller);
        var steps = new List<SequenceStep>();
        var stands = state.Revision;
        for (var number = 1; number <= sequence.Lines.Count; number++)
        {
            var (ran, stopped) = await Step(sequence, number, state, stands, runner, $"{key}:{number}", cancellationToken);
            if (stopped is not null)
                return steps.Count > 0 ? new(workspaceId, sequence, state.Revision, stands, steps, stopped)
                    : throw new WorkspaceRefused(stopped.Code, $"Line {number} of {sequence.Label} (`{stopped.Line}`) was refused, and nothing ran: {stopped.Why}");
            steps.Add(ran ?? throw new InvalidOperationException("A line of a sequence either ran or stopped the run."));
            stands = ran.Revision;
        }
        return new(workspaceId, sequence, state.Revision, stands, steps, null);
    }

    // A line of a run before it is invoked (notes 61 and 62): what its first words name still, the rule on that, its
    // reading as `reading` reads it, and the rule on the line a short form stands for. Either the reading, or why the
    // run stops at the line.
    (LinePreview? Read, SequenceStop? Stopped) Reads(SavedSequence sequence, int number, WorkspaceState state, Func<LinePreview> reading)
    {
        var line = sequence.Lines[number - 1];
        (LinePreview?, SequenceStop?) Stop(string code, string why) => (null, new(number, line.Line, code, why));
        var names = lines().Names(line.Line, state);
        // The vocabulary changed under the line: it is not read as whatever its words mean now.
        if (names is null || names.Id != line.Names)
            return Stop("LineMoved", $"Its first words named {line.Names} when the sequence was saved and name {names?.Id ?? "no command"} now: save the sequence again.");
        if (Barred(names, number, sequence.Lines.Count) is { } named) return Stop(named.Code, named.Why);
        LinePreview read;
        try
        {
            read = reading();
        }
        catch (WorkspaceRefused refused)
        {
            return Stop(refused.Code, refused.Message);
        }
        if (read.Refused is { } unread) return Stop(unread.Code, unread.Sentence);
        return BarredAsRead(names, read, number, sequence.Lines.Count, state) is { } meant ? Stop(meant.Code, meant.Why) : (read, null);
    }

    // One line of a run: read at the revision the line before left, and the command it names invoked. Either the line
    // as it ran, or why the run stops at it.
    async Task<(SequenceStep? Ran, SequenceStop? Stopped)> Step(SavedSequence sequence, int number, WorkspaceState state, long stands, Caller runner, string key, CancellationToken cancellationToken)
    {
        var line = sequence.Lines[number - 1];
        // A workspace another hand moved since the line before is refused by this reading, as any line read too late is.
        var (read, stopped) = Reads(sequence, number, state, () => lines().Preview(line.Line, state.WorkspaceId, stands, runner));
        if (stopped is not null) return (null, stopped);
        if (read is null) throw new InvalidOperationException("A line of a sequence is either read or stops the run.");
        var command = read.Command ?? throw new InvalidOperationException($"`{line.Line}` was read as a command and names none.");
        var inputs = read.Inputs ?? throw new InvalidOperationException($"`{line.Line}` was read as {command.Id} with no inputs said.");
        switch (await commands().InvokeAsync(command.Id, command.Version, inputs, runner, key, cancellationToken))
        {
            case CommandResult.Refused refused:
                return (null, new(number, line.Line, refused.Code, string.Join(" ", refused.Issues.Select(i => i.Message))));
            case CommandResult.Done done:
                // What every command of a workspace answers (note 36): where it left it, and what it did. One that
                // answers neither cannot be chained, which is its defect and no refusal.
                var port = done.Outputs.ContainsKey("answer") ? "answer" : done.Outputs.Count == 1 ? done.Outputs.Keys.Single() : null;
                if (port is null || done.Outputs[port] is not JsonObject told || told["revision"]?.GetValueKind() != JsonValueKind.Number || told["said"]?.GetValueKind() != JsonValueKind.String)
                    throw new InvalidOperationException(
                        $"{done.CapabilityId} answered without the revision it left the workspace at and what it did, so line {number} of {sequence.Label} (`{line.Line}`) cannot be followed by another.");
                var left = long.Parse(told["revision"]!.ToJsonString(), CultureInfo.InvariantCulture);
                return (new(number, line.Line, done.CapabilityId, told["said"]!.GetValue<string>(), left, left != stands, told), null);
            default:
                throw new InvalidOperationException("Unhandled command result.");
        }
    }

    // What bars a line from a sequence by what its first words name (note 61), or nothing: a command of the workspace's
    // application that edits it or, last, starts work on it may be one's line. A short form is held to the rule by
    // the line it stands for, once that is read.
    static (string Code, string Why)? Barred(LineNames names, int number, int count) =>
        names.ShortForm ? null : names.Command is { } command ? Barred(command, names.Words, number, count) : OfTheScreen(names);

    // What bars a line that read: a reading that asks a page for something names no command to run, and a short
    // form's line is held to the rule its own words could not be.
    (string Code, string Why)? BarredAsRead(LineNames names, LinePreview read, int number, int count, WorkspaceState state)
    {
        if (read.Command is null) return OfTheScreen(names);
        if (!names.ShortForm) return null;
        var stands = read.Expands ?? throw new InvalidOperationException($"The short form {names.Words} was read and says no line it stands for.");
        var meant = lines().Names(stands, state)?.Command ?? throw new InvalidOperationException($"`{stands}`, which {names.Words} stands for, was read as a command and names none.");
        return Barred(meant, names.Words, number, count);
    }

    static (string Code, string Why) OfTheScreen(LineNames names) => ("NotInASequence", $"{names.Words} is done by a page of the workbench, and a sequence has no screen.");

    static (string Code, string Why)? Barred(CapabilityDescriptor command, string words, int number, int count)
    {
        var why = command.CapabilityId switch
        {
            WorkspaceTakeBack.Undo or WorkspaceTakeBack.Redo => "acts on whatever command is the log's latest when it runs",
            DraftApply.Id => "writes the draft of an open dialog, and a sequence has none",
            SequenceSave.Id or SequenceRun.Id => "is a sequence's own verb, and a sequence holds no other",
            _ when !command.Effects.Any(e => e is CapabilityEffect.EditWorkspace or CapabilityEffect.Execute) => "neither edits the workspace nor starts work on it",
            _ => null,
        };
        if (why is not null) return ("NotInASequence", $"{words} {why}.");
        // §8: a sequence "may end by submitting a workflow at the revision it left".
        return command.Effects.Contains(CapabilityEffect.Execute) && number < count
            ? ("ExecutesBeforeTheEnd", $"{words} starts work on the workspace as it stands, so it is a sequence's last line or none.")
            : null;
    }

    static string Numbered(int number, string line) => $"Line {number} (`{line}`)";

    static JsonObject Content(IReadOnlyList<SequenceLine> kept) =>
        new() { ["lines"] = new JsonArray([.. kept.Select(l => (JsonNode)new JsonObject { ["line"] = l.Line, ["names"] = l.Names })]) };

    // A saved-item rule's refusal is the command's, by its code.
    static T Kept<T>(Func<T> act)
    {
        try
        {
            return act();
        }
        catch (SavedItemRefused refused)
        {
            throw new WorkspaceRefused(refused.Code, refused.Message);
        }
    }

    /// <summary>Whom a sequence is for, in words: its owner alone, the desk it names, or every user.</summary>
    public static string For(SavedSequence sequence) => sequence.Visibility switch
    {
        SavedVisibility.All => "every user",
        SavedVisibility.Desk => $"the desk {sequence.Desk ?? throw new InvalidOperationException($"The sequence {sequence.Name} is shared with a desk it does not name.")}",
        SavedVisibility.Private => "you alone",
        _ => throw new InvalidOperationException($"No visibility {sequence.Visibility}."),
    };

    /// <summary>A sequence's lines as a reading lists them (note 63): each as written, with its number.</summary>
    public static string Listed(IReadOnlyList<SequenceLine> lines) => string.Join(" ", lines.Select((l, i) => $"{i + 1}. `{l.Line}`"));

    /// <summary>A saved sequence in words: how many lines, under which name, for whom, whether it replaces the user's own, and the lines refused where it stands.</summary>
    public static string SavedAs(SequenceSaved saved)
    {
        var sequence = saved.Sequence;
        var said = $"{TableWords.Counted(sequence.Lines.Count, "line")} as the sequence {sequence.Name}, for {For(sequence)}{(saved.Replaced ? ", replacing your own of that name" : "")}.";
        return saved.Unread switch
        {
            [] => said,
            [var one] => $"{said} Line {one.Number} is refused on the workspace as it stands ({one.Why}); it is read again when the sequence runs, on what the lines before it leave.",
            [var first, ..] => $"{said} Lines {TableWords.And(saved.Unread.Select(u => $"{u.Number}"))} are refused on the workspace as it stands (line {first.Number}: {first.Why}); "
                + "each is read again when the sequence runs, on what the lines before it leave.",
        };
    }
}

/// <summary>
/// <c>sequence.save</c>, typed <c>sequence save &lt;name&gt; [desk|all] &lt;line&gt; then &lt;line&gt; …</c> (ADR-0016 notes 60, 61
/// and 63): command lines kept under a name, for the caller alone unless shared with their desk or with all. It changes
/// no workspace and is in no log: the workspace says which application the sequence is of, and its lines are checked
/// on it. A request holds the lines as a list; the line reads them apart at the word <c>then</c>.
/// </summary>
public sealed class SequenceSave(Sequences sequences) : ICapabilityHandler, ILineCommand
{
    public const string Id = "sequence.save";
    public const string Request = "sequence.save-request";
    public const string Answer = "sequence.saved";
    public const string Version = "1.0.0";

    /// <summary>The word that stands alone between two lines of a sequence as it is typed (note 63).</summary>
    public const string Then = "then";

    public CapabilityDescriptor Descriptor { get; } = new(Id, Version, "Save command lines under a name as a sequence, to run again on any workspace of the application",
        [new("request", Request, Version, Cardinality.One)], [new("answer", Answer, Version, Cardinality.One)],
        ExecutionKind.RequestResponse, [CapabilityEffect.SaveItem],
        new(["sequence save"], [new("name", CommandArgumentKind.Text, Required: true), new("share", CommandArgumentKind.Text, Required: false), new("lines", CommandArgumentKind.Text, Required: true)]));

    static readonly (string Word, string Detail)[] Shares = [("desk", "your desk sees it"), ("all", "every user sees it")];

    // The words after the command's own before its lines (note 63): the name, whom the sequence is shared with where
    // the word after the name says so, and the place the lines begin at.
    static (string Name, string? Share, int From) Head(string text, IReadOnlyList<(string Word, int Start)> words)
    {
        if (words.Count == 0) return ("", null, text.Length);
        var shared = words.Count > 1 && Shares.Any(s => s.Word.Equals(words[1].Word, StringComparison.OrdinalIgnoreCase));
        var last = words[shared ? 1 : 0];
        return (words[0].Word, shared ? words[1].Word.ToLowerInvariant() : null, last.Start + last.Word.Length);
    }

    static bool IsThen(string word) => word.Equals(Then, StringComparison.OrdinalIgnoreCase);

    // The lines as typed: what stands between two of the word then, standing alone. None where nothing is typed yet.
    static IReadOnlyList<string> Lines(string typed)
    {
        var words = LineWords.Of(typed);
        if (words.Count == 0) return [];
        var (lines, start) = (new List<string>(), 0);
        foreach (var (word, at) in words.Where(w => IsThen(w.Word)))
        {
            lines.Add(typed[start..at].Trim());
            start = at + word.Length;
        }
        lines.Add(typed[start..].Trim());
        return lines;
    }

    /// <summary>
    /// The save as it would be made (note 63): each line with its number, whom the sequence is for, whether it replaces
    /// the user's own, and the lines that are refused as the workspace stands. Checked as the save checks it, and
    /// nothing is kept; no line of it is run.
    /// </summary>
    public LineRead Read(LineContext line)
    {
        var (name, share, from) = Head(line.Text, LineWords.Of(line.Text));
        var saved = sequences.Plan(line.State, line.Caller, name, share, Lines(line.Text[from..]));
        IReadOnlyList<string> lines = [.. saved.Sequence.Lines.Select(l => l.Line)];
        var request = new JsonObject { ["workspaceId"] = line.State.WorkspaceId, ["name"] = name };
        List<LineArgument> arguments = [new("name", name)];
        if (share is not null)
        {
            request["share"] = share;
            arguments.Add(new("share", share));
        }
        request["lines"] = new JsonArray([.. lines.Select(l => (JsonNode)l)]);
        arguments.Add(new("lines", string.Join($" {Then} ", lines)));
        return new(new JsonObject { ["request"] = request }, arguments, null, $"Saves {Sequences.SavedAs(saved)} {Sequences.Listed(saved.Sequence.Lines)}");
    }

    /// <summary>
    /// After the name, whom the sequence may be shared with; where a line begins, the commands a sequence may hold;
    /// within a line, what that line's own command offers; and after a line that reads and may be followed, <c>then</c>.
    /// </summary>
    public LineCompletions Complete(LineContext line)
    {
        var text = line.Text;
        var (done, typing, start) = LineWords.Typing(text);
        // The name is the user's to make.
        if (done.Count == 0) return LineCompletions.None;
        IReadOnlyList<(string Word, int Start)> words = [.. LineWords.Of(text).Take(done.Count)];
        var from = Head(text, words).From;
        var items = new List<LineCompletion>();
        if (done.Count == 1) items.AddRange(Shares.Where(s => s.Word.StartsWith(typing, StringComparison.OrdinalIgnoreCase)).Select(s => new LineCompletion(s.Word, start, s.Detail)));
        // The line being typed stands after the last then, or after the name and whom the sequence is for.
        var thens = words.Where(w => w.Start >= from && IsThen(w.Word)).ToList();
        var after = thens.Count == 0 ? from : thens[^1].Start + Then.Length;
        var at = text.Length - text[after..].TrimStart().Length;
        var own = sequences.Completes(line.State, line.Caller, text[at..]);
        items.AddRange(own.Items.Select(c => c with { Start = c.Start + at }));
        // The word that begins another line, after one that reads and may be followed.
        if (at < start && Then.StartsWith(typing, StringComparison.OrdinalIgnoreCase) && sequences.Follows(line.State, line.Caller, text[at..start].TrimEnd()))
            items.Add(new(Then, start, "another line follows"));
        return new(items, own.More);
    }

    public Task<IReadOnlyDictionary<string, JsonNode?>> InvokeAsync(CapabilityInvocation invocation, CancellationToken cancellationToken) => TableContracts.Answered(invocation, (request, caller) =>
    {
        var workspaceId = CommandRequests.Text(request, "workspaceId");
        var saved = sequences.Save(workspaceId, caller, CommandRequests.Text(request, "name"), CommandRequests.Optional(request, "share"),
            [.. (request["lines"] as JsonArray ?? throw new CapabilityFailure("InvalidInput", "The request has no lines.")).Select(l => l!.GetValue<string>())]);
        var sequence = saved.Sequence;
        var answer = new JsonObject
        {
            ["workspaceId"] = workspaceId,
            // Saving moves no workspace: the revision is the one it stands at, as every command of a workspace says where it left it.
            ["revision"] = saved.Revision,
            ["said"] = $"Saved {Sequences.SavedAs(saved)}",
            ["name"] = sequence.Name,
            ["owner"] = sequence.Owner,
            ["share"] = SequenceContracts.Share(sequence.Visibility),
            ["lines"] = sequence.Lines.Count,
            ["replaced"] = saved.Replaced,
        };
        if (sequence.Desk is { } desk) answer["desk"] = desk;
        return answer;
    });
}

/// <summary>
/// <c>sequence.run</c>, typed <c>sequence run &lt;name&gt;</c> (ADR-0016 notes 62 and 63): a saved sequence run on the
/// caller's own workspace, each line read again and invoked as its own command, until one is refused. The answer says
/// the revision the run began at and the one it left, and each line that ran with what its command answered; a run that
/// stopped says the line, why, and that it waits for the user (<c>stays</c>).
/// </summary>
public sealed class SequenceRun(Sequences sequences) : ICapabilityHandler, ILineCommand
{
    public const string Id = "sequence.run";
    public const string Request = "sequence.run-request";
    public const string Answer = "sequence.ran";
    public const string Version = "1.0.0";

    public CapabilityDescriptor Descriptor { get; } = new(Id, Version, "Run a saved sequence on a workspace: each of its lines read again and run as its own command, until one is refused",
        [new("request", Request, Version, Cardinality.One)], [new("answer", Answer, Version, Cardinality.One)],
        ExecutionKind.RequestResponse, [CapabilityEffect.EditWorkspace, CapabilityEffect.Execute],
        new(["sequence run"], [new("sequence", CommandArgumentKind.Text, Required: true)]));

    /// <summary>
    /// The run as it would begin (note 63): every line as written, and the first read on the workspace as it stands,
    /// with what it would do. A first line that is refused refuses the reading. The request names the sequence by its
    /// owner and the moment it was saved, so that Enter runs what was read or is refused. No line after the first is
    /// read: one preview cannot say what a line would read after another has written (§9).
    /// </summary>
    public LineRead Read(LineContext line)
    {
        var words = LineWords.Of(line.Text);
        if (words.Count > 1) throw new TableRefused("UnreadWords", "sequence run takes a sequence's name, and no word after it.", words[1].Word);
        if (words.Count == 0)
        {
            IReadOnlyList<string> seen = [.. sequences.Visible(line.State, line.Caller.UserId).Select(s => s.Label)];
            throw new WorkspaceRefused("SequenceNotNamed", seen.Count == 0
                ? "sequence run takes a saved sequence's name, and none is saved for you, your desk or all: sequence save keeps one."
                : $"sequence run takes a saved sequence's name: {TableWords.Some(seen)}.");
        }
        var (sequence, first) = sequences.Begins(line.State, line.Caller, words[0].Word);
        var request = new JsonObject
        {
            ["workspaceId"] = line.State.WorkspaceId,
            ["revision"] = line.State.Revision,
            ["sequence"] = $"{sequence.Name}@{sequence.Owner}",
            ["saved"] = sequence.SavedAt.ToString("O", CultureInfo.InvariantCulture),
        };
        var does = first.Would ?? throw new InvalidOperationException($"`{sequence.Lines[0].Line}` was read and says nothing of what it would do.");
        // Whose it is, where its name does not say so and it is another's.
        var name = sequence.Owner == line.Caller.UserId || sequence.Label != sequence.Name ? sequence.Label : $"{sequence.Label} (saved by {sequence.Owner})";
        var listed = Sequences.Listed(sequence.Lines);
        // What the first line would do is its command's own sentence, and stands last: it ends as that command ends it.
        var would = sequence.Lines.Count == 1 ? $"Runs {name}, of 1 line: {listed}. As the workspace stands: {does}"
            : $"Runs {name}, of {sequence.Lines.Count} lines, each as its own command, until one is refused: {listed}. "
                + $"Each line after the first is read as it is reached, on what the lines before it leave. Line 1, as the workspace stands: {does}";
        return new(new JsonObject { ["request"] = request }, [new("sequence", words[0].Word)], null, would);
    }

    /// <summary>The sequences the user can see whose names begin with what is typed, each with how many lines it has, whom it is for and whose it is.</summary>
    public LineCompletions Complete(LineContext line)
    {
        if (line.Text.Length == 0) return LineCompletions.None;
        var from = line.Text.Length - line.Text.TrimStart().Length;
        return new([.. sequences.Visible(line.State, line.Caller.UserId).Where(s => s.Label.StartsWith(line.Text[from..], StringComparison.OrdinalIgnoreCase))
            .Select(s => new LineCompletion(s.Label, from, $"{TableWords.Counted(s.Lines.Count, "line")}, for {Sequences.For(s)}, saved by {s.Owner}"))]);
    }

    public async Task<IReadOnlyDictionary<string, JsonNode?>> InvokeAsync(CapabilityInvocation invocation, CancellationToken cancellationToken)
    {
        var caller = invocation.Caller ?? throw new CapabilityFailure("NoCaller", "A command is invoked for a caller, through the command gateway.");
        var request = invocation.Inputs["request"] ?? throw new CapabilityFailure("InvalidInput", "The command takes a request.");
        DateTimeOffset? read = CommandRequests.Optional(request, "saved") is not { } moment ? null
            : DateTimeOffset.TryParse(moment, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) ? at
            : throw new CapabilityFailure("InvalidInput", $"saved is the moment the sequence was saved, as its reading says it; {moment} is none.");
        try
        {
            var ran = await sequences.RunAsync(CommandRequests.Text(request, "workspaceId"), request["revision"]?.GetValue<long>(), caller, CommandRequests.Text(request, "sequence"), read,
                CommandRequests.Key(invocation, "sequence run"), cancellationToken);
            return new Dictionary<string, JsonNode?> { ["answer"] = Answered(ran) };
        }
        catch (WorkspaceRefused refused)
        {
            throw new CapabilityFailure(refused.Code, refused.Message);
        }
    }

    static JsonObject Answered(SequenceRan ran)
    {
        var answer = new JsonObject
        {
            ["workspaceId"] = ran.WorkspaceId,
            ["revision"] = ran.Left,
            ["began"] = ran.Began,
            ["said"] = Said(ran),
            ["sequence"] = ran.Sequence.Label,
            ["owner"] = ran.Sequence.Owner,
            ["lines"] = ran.Sequence.Lines.Count,
            ["ran"] = ran.Steps.Count,
            ["steps"] = new JsonArray([.. ran.Steps.Select(s => (JsonNode)new JsonObject
            {
                ["number"] = s.Number, ["line"] = s.Line, ["command"] = s.Command, ["said"] = s.Said, ["revision"] = s.Revision,
                // Text, since a contract holds no object in an object (note 12): the command's own answer, whole.
                ["answer"] = s.Answer.ToJsonString(),
            })]),
        };
        if (ran.Stopped is { } stopped)
        {
            answer["stopped"] = stopped.Number;
            answer["stoppedCode"] = stopped.Code;
            answer["stoppedWhy"] = stopped.Why;
            // What it left half done waits for the user (note 33): the notice does not leave by itself.
            answer["stays"] = true;
        }
        return answer;
    }

    // What a run did, in words: every line with what the last said, or the line it stopped at, why, and what of the
    // lines before it undo takes back. A line that changed nothing ran, and is none an undo reaches (§6, §11).
    static string Said(SequenceRan ran)
    {
        var (name, steps) = (ran.Sequence.Label, ran.Steps);
        if (ran.Stopped is not { } stopped)
            return $"Ran {name}: {TableWords.Counted(steps.Count, "line")}, from revision {ran.Began} to {ran.Left}. {steps[^1].Said}";
        var before = steps.Count switch { 1 => "Line 1", 2 => "Lines 1 and 2", var n => $"Lines 1 to {n}" };
        var wrote = steps.Count(s => s.Wrote);
        var left = wrote == 0 ? $"{before} ran and wrote nothing: the workspace is at revision {ran.Began} still."
            : $"{before} ran, from revision {ran.Began} to {ran.Left}: " + (wrote, steps.Count) switch
            {
                (1, 1) => "undo takes it back.",
                var (w, n) when w == n => "undo takes them back one at a time, the last first.",
                (1, _) => "1 of them wrote the workspace, and undo takes that one back.",
                _ => $"{wrote} of them wrote the workspace, and undo takes those back one at a time, the last first.",
            };
        return $"Stopped at line {stopped.Number} of {name} (`{stopped.Line}`): {stopped.Why} {left}";
    }
}

static class SequenceContracts
{
    /// <summary>Whom a sequence is for, as an answer and a request say it.</summary>
    public static string Share(SavedVisibility visibility) => visibility switch
    {
        SavedVisibility.Private => "private",
        SavedVisibility.Desk => "desk",
        SavedVisibility.All => "all",
        _ => throw new InvalidOperationException($"No visibility {visibility}."),
    };
}

/// <summary>The kernel's commands over saved sequences of edits, and their contracts. Knows no application.</summary>
public sealed class SequencesModule(Sequences sequences) : IQubeModule
{
    public void Register(CompositionRegistryBuilder builder)
    {
        builder.AddCapability(new SequenceSave(sequences));
        builder.AddCapability(new SequenceRun(sequences));
        builder.AddContract(new ContractDefinition(SequenceSave.Request, SequenceSave.Version, "Save command lines as a sequence", Record(new JsonObject
        {
            ["workspaceId"] = Given("A workspace of the caller's: the sequence is of its application, and the lines are checked on it"),
            ["name"] = Given("The sequence's name: one word without @"),
            ["share"] = new JsonObject
            {
                ["type"] = "string", ["enum"] = new JsonArray("desk", "all"), ["title"] = "Whom it is shared with: the caller's desk or every user. Left out, the caller alone",
            },
            ["lines"] = new JsonObject
            {
                ["type"] = "array", ["minItems"] = 1,
                ["title"] = $"The lines in order, {Sequences.MostLines} at most: each a command that edits the workspace, as a person types it, with its rows by id, all, a subset or where; "
                    + "the last may start work, as price does",
                ["items"] = new JsonObject { ["type"] = "string", ["minLength"] = 1 },
            },
        }, "workspaceId", "name", "lines")));
        builder.AddContract(new ContractDefinition(SequenceSave.Answer, SequenceSave.Version, "Sequence saved", Record(new JsonObject
        {
            ["workspaceId"] = Text("Workspace"),
            ["revision"] = Whole("Revision the workspace stands at: saving moves none"),
            ["said"] = Text("What was done, in words, with the lines that are refused as the workspace stands"),
            ["name"] = Text("The sequence's name"),
            ["owner"] = Text("Whose it is"),
            ["share"] = Text("Whom it is for: private, desk or all"),
            ["desk"] = Text("The desk it is shared with"),
            ["lines"] = Whole("How many lines it holds"),
            ["replaced"] = Flag("Whether it replaced its owner's own of that name"),
        }, "workspaceId", "revision", "said", "name", "owner", "share", "lines", "replaced")));
        builder.AddContract(new ContractDefinition(SequenceRun.Request, SequenceRun.Version, "Run a saved sequence", Record(new JsonObject
        {
            ["workspaceId"] = Given("The workspace to run it on: one of the caller's, of the application the sequence was saved for"),
            ["revision"] = Whole("Revision the workspace was read at; refused if it has moved since. Left out, the one it holds"),
            ["sequence"] = Given("The sequence, by its name, or as <name>@<owner> where two that the caller can see share a name"),
            ["saved"] = Text("The moment the sequence was saved, as its reading says it: a sequence saved again since is refused. Left out, the sequence as it is"),
        }, "workspaceId", "sequence")));
        builder.AddContract(new ContractDefinition(SequenceRun.Answer, SequenceRun.Version, "Sequence run", Record(new JsonObject
        {
            ["workspaceId"] = Text("Workspace"),
            ["revision"] = Whole("Revision the run's last line left the workspace at"),
            ["began"] = Whole("Revision the workspace was at when the run began"),
            ["said"] = Text("What was done, in words: every line, or the line the run stopped at and why"),
            ["sequence"] = Text("The sequence, as the caller names it"),
            ["owner"] = Text("Whose it is"),
            ["lines"] = Whole("How many lines it holds"),
            ["ran"] = Whole("How many of them ran"),
            ["steps"] = Records("Each line that ran, in order", new JsonObject
            {
                ["number"] = Whole("The line's number in the sequence"),
                ["line"] = Text("The line as it was saved"),
                ["command"] = Text("The command it named, by its capability id"),
                ["said"] = Text("What that command did, in words"),
                ["revision"] = Whole("Revision that command left the workspace at"),
                ["answer"] = Text("That command's own answer, whole, as JSON: the id to name to take it back, a Run it started"),
            }, "number", "line", "command", "said", "revision", "answer"),
            ["stopped"] = Whole("The number of the line the run stopped at; absent where every line ran"),
            ["stoppedCode"] = Text("That line's refusal, by its code"),
            ["stoppedWhy"] = Text("And in words"),
            ["stays"] = Flag("True where the run stopped: the lines before it are written, and what to do with them is the user's"),
        }, "workspaceId", "revision", "began", "said", "sequence", "owner", "lines", "ran", "steps")));
    }
}
