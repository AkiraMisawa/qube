using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Qube.Composition.Application.Tables;
using Qube.Composition.Application.Workspaces;
using Qube.Composition.Contracts;

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

/// <summary>
/// What a view verb asks of the screen behind a line (ADR-0016 §9, note 21 of step 2): the cell the page puts the
/// keyboard on. Its row by key, and by its position in the view's order where the server pages the grid; its column
/// as the view names its columns, none where the grid's Focus had none to keep; and the sentence to say once the
/// keyboard is there.
/// </summary>
public sealed record LinePlace(string Table, string? Subset, string Key, int? Position, string? Column, string Said);

/// <summary>A line as a view verb read it: each argument as read, the rows it means, what Enter would do, and the place it asks the page for.</summary>
public sealed record ViewRead(IReadOnlyList<LineArgument> Arguments, LineRows? Rows, string Would, LinePlace Place);

/// <summary>
/// A verb of the screen (ADR-0016 §3, note 21 of step 2): it reads its line as a command does, with the view the line
/// was given, and answers what the page is to do. It is no capability: nothing invokes it, it writes no workspace, and
/// no log holds it. A line it cannot read is refused by name, with the word at fault (<see cref="TableRefused"/>).
/// </summary>
public interface IViewVerb
{
    ViewVerbDescriptor Descriptor { get; }

    /// <summary>Reads the words after the verb's own into what the page is asked to do.</summary>
    ViewRead Read(LineContext line);

    /// <summary>What could follow the line's last word, or complete it where it is half typed; places count from the start of <see cref="LineContext.Text"/>.</summary>
    LineCompletions Complete(LineContext line);
}

/// <summary>
/// Words an application's users type for a line of a kernel verb (ADR-0016 §3, note 29 of step 3): <c>tick manual</c>
/// for <c>set manual checked on</c>. It has no behaviour of its own: it answers the line it stands for, and the server
/// reads that line. What it cannot expand is refused by name, with the word at fault (<see cref="TableRefused"/>).
/// </summary>
public interface IShortForm
{
    ShortFormDescriptor Descriptor { get; }

    /// <summary>The line of a command that the words after the form's own stand for, given the view behind the line.</summary>
    string Expand(LineContext line);

    /// <summary>What could follow the line's last word, or complete it where it is half typed; places count from the start of <see cref="LineContext.Text"/>.</summary>
    LineCompletions Complete(LineContext line);
}

/// <summary>A module's short forms, for the applications it serves.</summary>
public interface IShortFormCatalog
{
    IEnumerable<IShortForm> ShortForms { get; }
}

/// <summary>Why a line cannot be read (ADR-0016 §6): a stable code, a sentence, the word at fault with its place in the line where it stands there, and what it could have been.</summary>
public sealed record LineRefusal(string Code, string Sentence, string? Word, int? Start, int? End, IReadOnlyList<string> Candidates);

/// <summary>
/// A line as the server read it at a revision (ADR-0016 §5): the command, and either its reading or why it cannot be
/// read; and what could follow the last word. The inputs name the revision read, so that running them acts on what
/// was shown or is refused.
/// </summary>
/// <param name="View">What the screen behind the line showed, in one sentence, where the preview was asked with a view (note 19 of step 2).</param>
/// <param name="ViewMoved">True where that view's positions are of an order that has moved since: its rows can no longer be said.</param>
/// <param name="Place">Where a view verb asks the page to put the keyboard (note 21 of step 2): the reading then names no command, and Enter invokes none.</param>
/// <param name="Expands">The line a short form stands for (note 29 of step 3): the reading is that line's, and the command and inputs are its own.</param>
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
    string? View = null,
    bool ViewMoved = false,
    LinePlace? Place = null,
    string? Expands = null);

/// <summary>The words of a line as typed: what stands between its spaces, each with where it starts.</summary>
public static partial class LineWords
{
    public static IReadOnlyList<(string Word, int Start)> Of(string text) => [.. Pattern().Matches(text).Select(m => (m.Value, m.Index))];

    /// <summary>
    /// The word being typed at the end of <paramref name="text"/>, with where it starts, and the words done before it:
    /// the last word, or an empty one after a space. What a command that completes word by word asks first.
    /// </summary>
    public static (IReadOnlyList<string> Done, string Word, int Start) Typing(string text)
    {
        var words = Of(text);
        return text.Length == 0 || char.IsWhiteSpace(text[^1])
            ? ([.. words.Select(w => w.Word)], "", text.Length)
            : ([.. words.SkipLast(1).Select(w => w.Word)], words[^1].Word, words[^1].Start);
    }

    [GeneratedRegex(@"\S+")]
    private static partial Regex Pattern();
}

/// <summary>
/// Reads a line as typed for a workspace (ADR-0016 §5) and executes nothing: finds what its first words name, among the
/// commands that say how they are typed, the verbs of the screen and the short forms, and has it read the rest. The
/// commands are the registry's, and it names none; the verbs of the screen are the kernel's own, and it holds them
/// (note 21 of step 2); the short forms are their modules' (note 29 of step 3), and one is read as the line it stands
/// for. What a line's words mean is each command's, each verb's and each short form's to say.
/// </summary>
public sealed class CommandLines
{
    // A line as what its first words name read it: a command's reading has the inputs to invoke it with, and a verb
    // of the screen's has the place it asks the page for, and nothing to invoke.
    sealed record Reading(JsonObject? Inputs, IReadOnlyList<LineArgument> Arguments, LineRows? Rows, string Would, DateTimeOffset? At, LinePlace? Place);

    // What a line's first words name, a command capability that reads its line, a verb of the screen or a short form:
    // how it is typed, the command Enter invokes where there is one, and how it completes and reads the rest of the
    // line. A short form reads nothing: it answers the line it stands for.
    sealed record Typed(string[] Words, string Said, string Id, CommandForm Form, LineCommand? Command, Func<LineContext, LineCompletions> Complete, Func<LineContext, Reading>? Read, Func<LineContext, string>? Expand = null);

    readonly WorkspaceService workspaces;
    readonly TableRegister tables;
    readonly IReadOnlyList<Typed> typed;

    /// <summary>The verbs of the screen (ADR-0016 §3, note 21 of step 2), for the catalog to list apart from the capabilities.</summary>
    public IReadOnlyList<ViewVerbDescriptor> ViewVerbs { get; }

    /// <summary>The short forms (ADR-0016 §3, note 29 of step 3), for the catalog to list apart from the capabilities.</summary>
    public IReadOnlyList<ShortFormDescriptor> ShortForms { get; }

    /// <summary>Fails at start where two commands answer to the same words, or one says how it is typed and does not read its line.</summary>
    public CommandLines(CompositionRegistry registry, WorkspaceService workspaces, TableRegister tables, IEnumerable<IShortFormCatalog> shortForms)
    {
        this.workspaces = workspaces;
        this.tables = tables;
        IShortForm[] forms = [.. shortForms.SelectMany(c => c.ShortForms)];
        ShortForms = [.. forms.Select(f => f.Descriptor)];
        IViewVerb[] verbs = [new FindVerb(tables)];
        ViewVerbs = [.. verbs.Select(v => v.Descriptor)];
        var named = new List<Typed>();
        foreach (var descriptor in registry.CapabilityDescriptors)
        {
            if (descriptor.Command is not { } form) continue;
            if (registry.FindCapability(descriptor.CapabilityId, descriptor.Version) is not ILineCommand reader)
                throw new InvalidOperationException($"{descriptor.CapabilityId} says how it is typed and does not read its line.");
            var command = new LineCommand(descriptor.CapabilityId, descriptor.Version);
            Reading Read(LineContext line)
            {
                var read = reader.Read(line);
                return new(read.Inputs, read.Arguments, read.Rows, read.Would, read.At, null);
            }
            named.AddRange(form.Words.Select(w => new Typed(TableWords.Of(w).Split(' '), TableWords.Of(w), descriptor.CapabilityId, form, command, reader.Complete, Read)));
        }
        foreach (var verb in verbs)
        {
            // A verb of the screen names no command: its reading answers a place, and Enter invokes nothing (note 21 of step 2).
            Reading Read(LineContext line)
            {
                var asked = verb.Read(line);
                return new(null, asked.Arguments, asked.Rows, asked.Would, null, asked.Place);
            }
            named.AddRange(verb.Descriptor.Command.Words.Select(w => new Typed(TableWords.Of(w).Split(' '), TableWords.Of(w), verb.Descriptor.VerbId, verb.Descriptor.Command, null, verb.Complete, Read)));
        }
        named.AddRange(forms.SelectMany(form => form.Descriptor.Command.Words.Select(w =>
            new Typed(TableWords.Of(w).Split(' '), TableWords.Of(w), form.Descriptor.FormId, form.Descriptor.Command, null, form.Complete, null, form.Expand))));
        // Two applications may each have a command of the same words (note 27 of step 3): a workspace is one
        // application's. Within one, and beside a command of every application's, words are one command's.
        if (named.GroupBy(n => n.Said).FirstOrDefault(g => g.Count() > 1 && (g.Any(n => n.Form.App is null) || g.GroupBy(n => n.Form.App).Any(a => a.Count() > 1))) is { } shared)
            throw new InvalidOperationException($"{TableWords.And(shared.Select(n => n.Id))} answer to the same words, {shared.Key}.");
        // The longest words first: `add each` is read before `add`.
        typed = [.. named.OrderByDescending(n => n.Words.Length)];
    }

    // What a line of the workspace may name (note 27 of step 3): the commands of its application, and those of every one.
    IEnumerable<Typed> Of(WorkspaceState state) => typed.Where(t => t.Form.App is null || t.Form.App == state.App);

    /// <summary>
    /// The line as read for the caller's own workspace as it stands, or at the revision the caller read, which a
    /// workspace that moved refuses (<see cref="WorkspaceRefused"/>, as an unknown workspace and another user's are).
    /// With the view of the screen behind the line, the line may say its rows by it, and the answer says the view
    /// (notes 16 to 19 of step 2); a view that names what the workspace has not is refused whole (<c>InvalidView</c>).
    /// </summary>
    public LinePreview Preview(string line, string workspaceId, long? revision, Caller caller, LineView? view = null)
    {
        var state = WorkspaceCommands.Owned(workspaces, workspaceId, caller, revision);
        var viewSaid = view is null ? null : Views.Say(view, tables.Of(state), state);
        var preview = Read(line, state, caller, view, Of(state).ToList());
        return viewSaid is null ? preview : preview with { View = viewSaid.Sentence, ViewMoved = viewSaid.Moved };
    }

    // The line as read among `named`: what a line of the workspace may name, or, for the line a short form stands for,
    // the commands and the screen's verbs alone.
    LinePreview Read(string line, WorkspaceState state, Caller caller, LineView? view, IReadOnlyList<Typed> named)
    {
        var words = LineWords.Of(line);
        var said = Named(named, words);
        var all = named.Select(t => t.Said).ToList();
        if (said is null)
        {
            // A first word half typed is completed; one that is no command is refused with the commands there are.
            var first = words.Count > 0 ? words[0] : (Word: "", Start: line.Length);
            var starting = words.Count <= 1 && !EndsInSpace(line) ? all.Where(w => w.StartsWith(first.Word, StringComparison.OrdinalIgnoreCase)).ToList() : [];
            var refusal = new LineRefusal("UnknownCommand", first.Word.Length == 0 ? $"Type a command: {TableWords.List(all)}." : $"No command {first.Word}: the commands are {TableWords.List(all)}.",
                first.Word.Length == 0 ? null : first.Word, first.Word.Length == 0 ? null : first.Start, first.Word.Length == 0 ? null : first.Start + first.Word.Length, all);
            return new(state.Revision, null, null, null, null, null, null, refusal, [.. starting.Select(w => new LineCompletion(w, first.Start))], 0);
        }

        var last = words[said.Words.Length - 1];
        var from = last.Start + last.Word.Length;
        var context = new LineContext(state, caller, line[from..], view);
        var completions = said.Complete(context);
        IReadOnlyList<LineCompletion> complete = [.. completions.Items.Select(c => c with { Start = c.Start + from })];
        // A command that takes no argument takes no word after its own.
        if (said.Form.Arguments.Count == 0 && words.Count > said.Words.Length)
            return Refused("UnreadWords", $"{said.Said} takes no words after it.", words[said.Words.Length].Word, []);
        try
        {
            if (said.Expand is { } expand)
            {
                // A short form (note 29 of step 3): the reading is the line's it stands for, with what the form itself
                // completes; a word that line is refused for is marked where it stands in the line as typed.
                var stands = expand(context);
                IReadOnlyList<Typed> commands = [.. named.Where(n => n.Expand is null)];
                if (Named(commands, LineWords.Of(stands)) is null)
                    throw new InvalidOperationException($"The short form {said.Said} expands to `{stands}`, which begins with no command's words.");
                var meant = Read(stands, state, caller, view, commands);
                var marked = meant.Refused?.Word is { } word ? Place(line, from, word) : null;
                return meant with { Expands = stands, Complete = complete, More = completions.More, Refused = meant.Refused is null ? null : meant.Refused with { Start = marked?.Start, End = marked?.End } };
            }
            var read = (said.Read ?? throw new InvalidOperationException($"{said.Id} neither reads its line nor stands for another."))(context);
            return new(state.Revision, said.Command, read.Inputs, read.Arguments, read.Rows, read.Would, read.At, null, complete, completions.More, Place: read.Place);
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
            return new(state.Revision, said.Command, null, null, null, null, null, new(code, sentence, word, place?.Start, place?.End, candidates), complete, completions.More);
        }
    }

    /// <summary>
    /// The caller's own workspace's tables as a screen is told of them (ADR-0016 §4, note 25 of step 2): each with the
    /// actions a grid bound to it offers, a title and the line that does it. The kernel's own comes first, on every
    /// table: the cell the menu was opened on follows its source again. A registered action whose line begins with no
    /// command's words is the registration's defect, and fails here; so does a host that registers tables and not the
    /// command the kernel's own action runs.
    /// </summary>
    public IReadOnlyList<TableOnScreen> Tables(string workspaceId, Caller caller)
    {
        var state = WorkspaceCommands.Owned(workspaces, workspaceId, caller, null);
        var reset = typed.FirstOrDefault(t => t.Id == EntityReset.Id)
            ?? throw new InvalidOperationException($"Every table offers Reset this cell, and this host registers no {EntityReset.Id} to run it.");
        IReadOnlyList<TableAction> own = [new("Reset this cell", reset.Said)];
        return [.. tables.Of(state).Select(t => t.Descriptor).Select(table =>
        {
            foreach (var action in table.Actions ?? [])
                if (Named(Of(state), LineWords.Of(action.Line)) is null)
                    throw new InvalidOperationException($"{table.TableId} offers {action.Title} as the line `{action.Line}`, which begins with no command's words.");
            return new TableOnScreen(table.TableId, table.Title, [.. own, .. table.Actions ?? []]);
        })];
    }

    /// <summary>
    /// The keys of the rows a view's selection names, in the caller's own workspace as it stands (ADR-0016 note 37 of
    /// step 3): what a page reads to make a command's typed value from a grid's selection, where the server pages the
    /// grid and the page holds positions. Read as the preview reads <c>selected</c> (note 17): keys as they are,
    /// positions through the order they were written in, refused where that order has moved or a row is gone
    /// (<c>ViewMoved</c>). A view with nothing selected names no row. Nothing is written and nothing runs.
    /// </summary>
    public ViewSelection Selection(string workspaceId, Caller caller, LineView view)
    {
        var state = WorkspaceCommands.Owned(workspaces, workspaceId, caller, null);
        var bound = tables.Of(state);
        // The view is checked whole, as where a line's reading starts (Preview), whether or not it selects a row: one
        // that names what the workspace has not is the page's fault, and never read as "nothing selected".
        Views.Say(view, bound, state);
        var table = Views.TableOf(view, bound);
        if (view.Selected == 0) return new(table.Descriptor.TableId, []);
        var index = new Lazy<Selectors.RowIndex>(() => new(table, table.Table.Rows(state)));
        try
        {
            return new(table.Descriptor.TableId, [.. Views.Selected(view, table, state, key => index.Value.Row(key)).Select(r => r.Key)]);
        }
        catch (TableRefused refused)
        {
            // No line was read, so there is no word to mark: the refusal is the workspace's, by the same code and sentence.
            throw new WorkspaceRefused(refused.Code, refused.Message);
        }
    }

    // What a line's first words name: the command or the verb of the screen whose words they are, the longest first.
    static Typed? Named(IEnumerable<Typed> named, IReadOnlyList<(string Word, int Start)> words) =>
        named.FirstOrDefault(t => t.Words.Length <= words.Count && t.Words.Zip(words).All(p => p.First.Equals(p.Second.Word, StringComparison.OrdinalIgnoreCase)));

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
