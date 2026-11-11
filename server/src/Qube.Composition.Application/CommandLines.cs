using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Qube.Composition.Application.Tables;
using Qube.Composition.Application.Workspaces;

namespace Qube.Composition.Application;

// ADR-0016 §5 and its notes 11 and 15 of 2026-11-11: the server owns the reading of a line. A command that says how it
// is typed (§4) reads the words after its own, and the preview answers that reading with nothing executed.

/// <summary>
/// A line after its command's words, as typed, for a workspace as it stands and the caller who types it, with what the
/// screen behind the line showed as it opened, where a screen is behind it (ADR-0016 §9, note 16 of step 2).
/// </summary>
public sealed record LineContext(WorkspaceState State, Caller Caller, string Text, LineView? View = null);

/// <summary>Where an argument came from (ADR-0016 §5): the line's own words, or the screen behind the line.</summary>
public enum ArgumentSource
{
    Typed,
    Context,
}

/// <summary>One argument of a command as a line was read: the name its request holds it by, its words, and where it came from.</summary>
public sealed record LineArgument(string Name, string Text, ArgumentSource From = ArgumentSource.Typed);

/// <summary>The rows a line means: their table, a title in its words (<c>readings, 2 rows</c>), how many, and the first to look at.</summary>
public sealed record LineRows(string Table, string Title, int Count, IReadOnlyList<string> First);

/// <summary>
/// A line as its command read it: the inputs to invoke the command with, each argument as read, the rows it means where
/// it means rows, and what running it would change, in one sentence.
/// </summary>
/// <param name="At">The moment the reading speaks of, where it speaks of one (when the command an undo would take back ran): the sentence holds no time, and a client writes this in its user's zone.</param>
public sealed record LineRead(JsonObject Inputs, IReadOnlyList<LineArgument> Arguments, LineRows? Rows, string Would, DateTimeOffset? At = null);

/// <summary>
/// What could stand at the end of a line: the text, the place in the line it is written from (it replaces what is typed
/// from there on), and what it is, where that helps (a table's word beside a row).
/// </summary>
public sealed record LineCompletion(string Value, int Start, string? Detail = null);

/// <summary>What could follow, the first of them, and how many more there are than are listed.</summary>
public sealed record LineCompletions(IReadOnlyList<LineCompletion> Items, int More = 0)
{
    public static LineCompletions None { get; } = new([]);
}

/// <summary>
/// A command capability that reads its own line (ADR-0016 §4, §5): one whose descriptor says how it is typed. Reading
/// writes nothing. A line it cannot read is refused as the command itself would refuse it, by name, with the word at fault.
/// </summary>
public interface ILineCommand
{
    /// <summary>Reads the words after the command's own into what invoking it would be given and would do.</summary>
    LineRead Read(LineContext line);

    /// <summary>What could follow the line's last word, or complete it where it is half typed; places count from the start of <see cref="LineContext.Text"/>.</summary>
    LineCompletions Complete(LineContext line);
}

public sealed record LineCommand(string Id, string Version);

/// <summary>Why a line cannot be read (ADR-0016 §6): a stable code, a sentence, the word at fault with its place in the line where it stands there, and what it could have been.</summary>
public sealed record LineRefusal(string Code, string Sentence, string? Word, int? Start, int? End, IReadOnlyList<string> Candidates);

/// <summary>
/// A line as the server read it at a revision (ADR-0016 §5): the command, and either its reading or why it cannot be
/// read; and what could follow the last word. The inputs name the revision read, so that running them acts on what
/// was shown or is refused.
/// </summary>
/// <param name="View">What the screen behind the line showed, in one sentence, where the preview was asked with a view (note 19 of step 2).</param>
public sealed record LinePreview(
    long Revision,
    LineCommand? Command,
    JsonObject? Inputs,
    IReadOnlyList<LineArgument>? Arguments,
    LineRows? Rows,
    string? Would,
    DateTimeOffset? At,
    LineRefusal? Refused,
    IReadOnlyList<LineCompletion> Complete,
    int More,
    string? View = null);

/// <summary>The words of a line as typed: what stands between its spaces, each with where it starts.</summary>
public static partial class LineWords
{
    public static IReadOnlyList<(string Word, int Start)> Of(string text) => [.. Pattern().Matches(text).Select(m => (m.Value, m.Index))];

    [GeneratedRegex(@"\S+")]
    private static partial Regex Pattern();
}

/// <summary>
/// Reads a line as typed for a workspace (ADR-0016 §5) and executes nothing: finds the command its first words name
/// among those that say how they are typed, and has it read the rest. It names no command and no table of its own:
/// the commands are the registry's, and what a line's words mean is each command's to say.
/// </summary>
public sealed class CommandLines
{
    readonly WorkspaceService workspaces;
    readonly TableRegister tables;
    readonly IReadOnlyList<(string[] Words, string Said, ICapabilityHandler Handler)> typed;

    /// <summary>Fails at start where two commands answer to the same words, or one says how it is typed and does not read its line.</summary>
    public CommandLines(CompositionRegistry registry, WorkspaceService workspaces, TableRegister tables)
    {
        this.workspaces = workspaces;
        this.tables = tables;
        var named = new List<(string[] Words, string Said, ICapabilityHandler Handler)>();
        foreach (var descriptor in registry.CapabilityDescriptors)
        {
            if (descriptor.Command is not { } form) continue;
            var handler = registry.FindCapability(descriptor.CapabilityId, descriptor.Version)!;
            if (handler is not ILineCommand)
                throw new InvalidOperationException($"{descriptor.CapabilityId} says how it is typed and does not read its line.");
            named.AddRange(form.Words.Select(w => (TableWords.Of(w).Split(' '), TableWords.Of(w), handler)));
        }
        if (named.GroupBy(n => n.Said).FirstOrDefault(g => g.Count() > 1) is { } shared)
            throw new InvalidOperationException($"{TableWords.And(shared.Select(n => n.Handler.Descriptor.CapabilityId))} answer to the same words, {shared.Key}.");
        // The longest words first: `add each` is read before `add`.
        typed = [.. named.OrderByDescending(n => n.Words.Length)];
    }

    /// <summary>
    /// The line as read for the caller's own workspace as it stands, or at the revision the caller read, which a
    /// workspace that moved refuses (<see cref="WorkspaceRefused"/>, as an unknown workspace and another user's are).
    /// With the view of the screen behind the line, the line may say its rows by it, and the answer says the view
    /// (notes 16 to 19 of step 2); a view that names what the workspace has not is refused whole (<c>InvalidView</c>).
    /// </summary>
    public LinePreview Preview(string line, string workspaceId, long? revision, Caller caller, LineView? view = null)
    {
        var state = WorkspaceCommands.Owned(workspaces, workspaceId, caller, revision);
        var shown = view is null ? null : Views.Say(view, tables.Of(state), state);
        var words = LineWords.Of(line);
        var said = typed.FirstOrDefault(t => t.Words.Length <= words.Count && t.Words.Zip(words).All(p => p.First.Equals(p.Second.Word, StringComparison.OrdinalIgnoreCase)));
        var all = typed.Select(t => t.Said).ToList();
        if (said.Handler is null)
        {
            // A first word half typed is completed; one that is no command is refused with the commands there are.
            var first = words.Count > 0 ? words[0] : (Word: "", Start: line.Length);
            var starting = words.Count <= 1 && !EndsInSpace(line) ? all.Where(w => w.StartsWith(first.Word, StringComparison.OrdinalIgnoreCase)).ToList() : [];
            var refusal = new LineRefusal("UnknownCommand", first.Word.Length == 0 ? $"Type a command: {TableWords.List(all)}." : $"No command {first.Word}: the commands are {TableWords.List(all)}.",
                first.Word.Length == 0 ? null : first.Word, first.Word.Length == 0 ? null : first.Start, first.Word.Length == 0 ? null : first.Start + first.Word.Length, all);
            return new(state.Revision, null, null, null, null, null, null, refusal, [.. starting.Select(w => new LineCompletion(w, first.Start))], 0, shown);
        }

        var descriptor = said.Handler.Descriptor;
        var command = new LineCommand(descriptor.CapabilityId, descriptor.Version);
        var last = words[said.Words.Length - 1];
        var from = last.Start + last.Word.Length;
        var reader = (ILineCommand)said.Handler;
        var context = new LineContext(state, caller, line[from..], view);
        var completions = reader.Complete(context);
        IReadOnlyList<LineCompletion> complete = [.. completions.Items.Select(c => c with { Start = c.Start + from })];
        // A command that takes no argument takes no word after its own.
        if (descriptor.Command!.Arguments.Count == 0 && words.Count > said.Words.Length)
            return Refused("UnreadWords", $"{said.Said} takes no words after it.", words[said.Words.Length].Word, []);
        try
        {
            var read = reader.Read(context);
            return new(state.Revision, command, read.Inputs, read.Arguments, read.Rows, read.Would, read.At, null, complete, completions.More, shown);
        }
        catch (TableRefused refused)
        {
            return Refused(refused.Code, refused.Message, refused.Word, refused.Candidates);
        }
        catch (WorkspaceRefused refused)
        {
            return Refused(refused.Code, refused.Message, null, []);
        }

        LinePreview Refused(string code, string sentence, string? word, IReadOnlyList<string> candidates)
        {
            var place = word is null ? null : Place(line, from, word);
            return new(state.Revision, command, null, null, null, null, null, new(code, sentence, word, place?.Start, place?.End, candidates), complete, completions.More, shown);
        }
    }

    // Where the word stands in the line after the command's own words, as a word of its own: bounded by the line's
    // ends, a space, a comma or an operator's sign. A word that stands there twice has no place: the reader names the
    // word and not which of the two, and the wrong one marked would send the user to a word that is right.
    static (int Start, int End)? Place(string line, int from, string word)
    {
        (int Start, int End)? found = null;
        for (var at = line.IndexOf(word, from, StringComparison.OrdinalIgnoreCase); at >= 0; at = line.IndexOf(word, at + 1, StringComparison.OrdinalIgnoreCase))
        {
            var end = at + word.Length;
            if (!((at == 0 || Bounds(line[at - 1]) || Bounds(word[0])) && (end == line.Length || Bounds(line[end]) || Bounds(word[^1])))) continue;
            if (found is not null) return null;
            found = (at, end);
        }
        return found;
    }

    static bool Bounds(char c) => char.IsWhiteSpace(c) || c is ',' or '=' or '<' or '>' or '!' or '~';

    static bool EndsInSpace(string line) => line.Length > 0 && char.IsWhiteSpace(line[^1]);
}
