using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
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
    /// <summary>How many rows a line lists at a time, with how many more there are (ADR-0016 note 15): a table may have ten thousand.</summary>
    public const int RowsListed = 20;

    public static LineCompletions None { get; } = new([]);
}

/// <summary>What a line would write (ADR-0016 note 54): its changes in order, what it says once they are written, and what it would change, each in one sentence.</summary>
/// <param name="Line">The whole line, its rows named by their keys in their table: what a draft holds, and reads again as any line is read.</param>
/// <param name="Table">The table of the rows the line is of.</param>
/// <param name="Rows">The rows it is of, by key: those it writes or removes, and for a row it makes, the row it makes it under.</param>
public sealed record LineChanges(IReadOnlyList<WorkspaceChange> Changes, string Said, string Would, string Line, string? Table, IReadOnlyList<string> Rows);

/// <summary>
/// A command whose line may be a line of a draft (ADR-0016 note 54): it says the changes the words after its own would
/// write on a workspace as it stands, and writes none. A line it cannot read is refused as the command refuses it.
/// </summary>
public interface IDraftLine
{
    LineChanges Changes(LineContext line);
}

/// <summary>A command that ends an open draft by writing it (ADR-0016 note 55): read while a draft is open, as no other command is.</summary>
public interface IDraftCommand : ILineCommand;

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

    /// <summary>Whether it is read while a draft is open (ADR-0016 note 55): a verb that changes nothing of the draft is, and one that opens another is not.</summary>
    bool WithDraft => true;
}

public sealed record LineCommand(string Id, string Version);

/// <summary>
/// What a verb of the screen asks of the page behind a line (ADR-0016 §9, notes 21, 39 and 42): one thing, said by
/// its kind, with the sentence to say once it is done.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(LinePlace), "place")]
[JsonDerivedType(typeof(LineSort), "sort")]
[JsonDerivedType(typeof(LineFilter), "filter")]
[JsonDerivedType(typeof(LineSelect), "select")]
[JsonDerivedType(typeof(LineCopy), "copy")]
[JsonDerivedType(typeof(LineExport), "export")]
[JsonDerivedType(typeof(LineOpen), "open")]
[JsonDerivedType(typeof(LineDrafted), "draft")]
[JsonDerivedType(typeof(LineCancel), "cancel")]
public abstract record ViewAsk(string Said);

/// <summary>What is done with the grid the view was read of (notes 21 and 39): each names that grid, by its table and subset.</summary>
public abstract record GridAsk(string Table, string? Subset, string Said) : ViewAsk(Said);

/// <summary>
/// The cell the page puts the keyboard on (note 21 of step 2). Its row by key, and by its position in the view's order
/// where the server pages the grid; its column as the view names its columns, none where the grid's Focus had none to
/// keep.
/// </summary>
public sealed record LinePlace(string Table, string? Subset, string Key, int? Position, string? Column, string Said) : GridAsk(Table, Subset, Said);

/// <summary>The sort the page gives its grid (note 40 of step 4): its columns as the view names them, the major key first; none takes the sort off.</summary>
public sealed record LineSort(string Table, string? Subset, IReadOnlyList<ViewSort> Sorts, string Said) : GridAsk(Table, Subset, Said);

/// <summary>
/// The filter the page gives its grid (note 41 of step 4): the conditions as typed, which the view then carries
/// (<see cref="LineView.Where"/>) and the server reads again at each reading; none takes the filter off.
/// </summary>
public sealed record LineFilter(string Table, string? Subset, string? Where, string Said) : GridAsk(Table, Subset, Said);

/// <summary>
/// The rows the page places its grid's Selection on (note 43 of step 4): one run of <paramref name="Count"/> rows.
/// Where the server pages the grid, the run by the position of its first row in the view's order; where the page
/// holds every row, the rows by their keys, and the page says whether its grid shows them as one run. The Focus goes
/// to the first of them, in <paramref name="Column"/>, as the view names its columns; none where the grid's Focus had
/// none to keep.
/// </summary>
public sealed record LineSelect(string Table, string? Subset, IReadOnlyList<string>? Keys, int? Position, int Count, string? Column, string Said) : GridAsk(Table, Subset, Said);

/// <summary>Rows' cells for the clipboard (note 44 of step 4): the page asks for the line's content at Enter and writes it there.</summary>
public sealed record LineCopy(string Said) : ViewAsk(Said);

/// <summary>Rows' cells for a file (note 44 of step 4): the page asks for the line's content at Enter and saves it under <paramref name="FileName"/>.</summary>
public sealed record LineExport(string FileName, string Said) : ViewAsk(Said);

/// <summary>The row whose dialog the page opens (ADR-0016 note 56), as a draft of that row at the revision the line was read at.</summary>
public sealed record LineOpen(string Table, string Key, string Said) : ViewAsk(Said);

/// <summary>
/// The line an open draft gains (ADR-0016 note 55): the page adds it to the draft it holds and reads the draft's form
/// again. Nothing is invoked, and nothing written until the draft is applied.
/// </summary>
public sealed record LineDrafted(string Line, string Said) : ViewAsk(Said);

/// <summary>The page discards the draft it holds and closes its dialog (ADR-0016 note 55): nothing was written, and nothing is.</summary>
public sealed record LineCancel(string Said) : ViewAsk(Said);

/// <summary>A line as a view verb read it: each argument as read, the rows it means where it means rows, what Enter would do, and what it asks the page for.</summary>
public sealed record ViewRead(IReadOnlyList<LineArgument> Arguments, LineRows? Rows, string Would, ViewAsk Asks);

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

    /// <summary>Whether it is read while a draft is open (ADR-0016 note 55): a verb that changes nothing of the draft is, and one that opens another is not.</summary>
    bool WithDraft => true;
}

/// <summary>
/// A verb of the screen whose line has content (ADR-0016 note 44 of step 4): text that leaves the workbench, for the
/// clipboard or a file. Its reading says what the content would be; the content itself is written when the page asks
/// for it at Enter, from the same line.
/// </summary>
public interface IContentVerb : IViewVerb
{
    /// <summary>The content of the words after the verb's own, read as <see cref="IViewVerb.Read"/> reads them and refused as it refuses.</summary>
    string Content(LineContext line);
}

/// <summary>
/// The content of a line at a revision (ADR-0016 note 44 of step 4): the text, or why the line has none. A line that
/// cannot be read is an answer, as a preview's is.
/// </summary>
public sealed record LineContent(long Revision, string? Text, LineRefusal? Refused);

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

    /// <summary>Whether it is read while a draft is open (ADR-0016 note 55): a verb that changes nothing of the draft is, and one that opens another is not.</summary>
    bool WithDraft => true;
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
/// <param name="Asks">What a view verb asks of the page (notes 21, 39 and 42): the reading then names no command, and Enter invokes none.</param>
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
    ViewAsk? Asks = null,
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
    // of the screen's has what it asks the page for, and nothing to invoke.
    sealed record Reading(JsonObject? Inputs, IReadOnlyList<LineArgument> Arguments, LineRows? Rows, string Would, DateTimeOffset? At, ViewAsk? Asks);

    // What a line's first words name, a command capability that reads its line, a verb of the screen or a short form:
    // how it is typed, the command Enter invokes where there is one, and how it completes and reads the rest of the
    // line. A short form reads nothing: it answers the line it stands for.
    sealed record Typed(string[] Words, string Said, string Id, CommandForm Form, LineCommand? Command, Func<LineContext, LineCompletions> Complete, Func<LineContext, Reading>? Read, Func<LineContext, string>? Expand = null,
        Func<LineContext, string>? Content = null, Func<LineContext, LineChanges>? Changes = null, bool WithDraft = false)
    {
        /// <summary>Whether it is read while a draft is open (note 55): a line the draft may hold, or what says it is read then.</summary>
        public bool InDraft => Changes is not null || WithDraft;
    }

    readonly WorkspaceService workspaces;
    readonly TableRegister tables;
    readonly IReadOnlyList<Typed> typed;

    /// <summary>The verbs of the screen (ADR-0016 §3, note 21 of step 2), for the catalog to list apart from the capabilities.</summary>
    public IReadOnlyList<ViewVerbDescriptor> ViewVerbs { get; }

    /// <summary>The short forms (ADR-0016 §3, note 29 of step 3), for the catalog to list apart from the capabilities.</summary>
    public IReadOnlyList<ShortFormDescriptor> ShortForms { get; }

    /// <summary>The most cells a line takes out of the workbench where a host sets no other (ADR-0016 note 44 of step 4).</summary>
    public const int MostCells = 1_000_000;

    /// <summary>Fails at start where two commands answer to the same words, or one says how it is typed and does not read its line.</summary>
    /// <param name="mostCells">The most cells <c>copy</c> and <c>export</c> take out: a line of more is refused, never cut.</param>
    public CommandLines(CompositionRegistry registry, WorkspaceService workspaces, TableRegister tables, IEnumerable<IShortFormCatalog> shortForms, int mostCells = MostCells)
    {
        this.workspaces = workspaces;
        this.tables = tables;
        IShortForm[] forms = [.. shortForms.SelectMany(c => c.ShortForms)];
        ShortForms = [.. forms.Select(f => f.Descriptor)];
        IViewVerb[] verbs = [new FindVerb(tables), new SortVerb(tables), new FilterVerb(tables), new SelectVerb(tables), new CopyVerb(tables, mostCells), new ExportVerb(tables, mostCells),
            new OpenVerb(tables), new CancelVerb()];
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
            // A command whose line a draft may hold says its changes too (note 54).
            Func<LineContext, LineChanges>? changes = reader is IDraftLine drafted ? drafted.Changes : null;
            named.AddRange(form.Words.Select(w => new Typed(TableWords.Of(w).Split(' '), TableWords.Of(w), descriptor.CapabilityId, form, command, reader.Complete, Read, Changes: changes,
                WithDraft: reader is IDraftCommand)));
        }
        foreach (var verb in verbs)
        {
            // A verb of the screen names no command: its reading answers what it asks the page for, and Enter invokes
            // nothing (notes 21 and 39).
            Reading Read(LineContext line)
            {
                var asked = verb.Read(line);
                return new(null, asked.Arguments, asked.Rows, asked.Would, null, asked.Asks);
            }
            named.AddRange(verb.Descriptor.Command.Words.Select(w => new Typed(TableWords.Of(w).Split(' '), TableWords.Of(w), verb.Descriptor.VerbId, verb.Descriptor.Command, null, verb.Complete, Read,
                Content: verb is IContentVerb content ? content.Content : null, WithDraft: verb.WithDraft)));
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
        if (view?.Draft is { } draft) return Drafted(line, workspaceId, caller, draft);
        var state = WorkspaceCommands.Owned(workspaces, workspaceId, caller, revision);
        var viewSaid = view is null ? null : Views.Say(view, tables.Of(state), state);
        var preview = Read(line, state, caller, view, Of(state).ToList());
        return viewSaid is null ? preview : preview with { View = viewSaid.Sentence, ViewMoved = viewSaid.Moved };
    }

    /// <summary>
    /// The form of a draft (ADR-0016 §10, note 54): its row as its lines leave it, each line read in order on what the
    /// lines before it left, and nothing written.
    /// </summary>
    public DraftForm Draft(string workspaceId, Caller caller, LineDraft draft)
    {
        var opened = Opened(workspaceId, caller, draft);
        var (left, read) = Folded(opened, caller, draft, Of(opened).ToList());
        return DraftForms.Of(draft, tables.Of(left), opened, left, workspaces.Unedited(opened), [.. read.Select(r => r.Line)]);
    }

    // The draft's lines read in order, each on what the ones before it left, and the workspace as they leave it. A
    // line that no longer reads ends the reading: the lines after it were accepted on what it left.
    (WorkspaceState Left, IReadOnlyList<(DraftLine Line, LineChanges? Changes)> Read) Folded(WorkspaceState opened, Caller caller, LineDraft draft, IReadOnlyList<Typed> named)
    {
        var (left, read) = (opened, new List<(DraftLine, LineChanges?)>());
        foreach (var line in draft.Lines)
        {
            var before = left;
            // Each line is one the draft could have gained, as it was when it was typed: of the draft's row, and no other.
            LineChanges OfTheDraft()
            {
                var changes = Changes(line, before, caller, named);
                Gained(draft, changes, before, caller);
                return changes;
            }
            if (Refusal(OfTheDraft, out var changes) is { } refused)
            {
                read.Add((new(line, null, refused), null));
                break;
            }
            left = workspaces.After(left, caller.UserId, changes!.Changes, caller.Origin);
            read.Add((new(line, changes.Said, null), changes));
        }
        return (left, read);
    }

    /// <summary>
    /// Writes a draft (ADR-0016 note 55): its lines are read again, in order, at the revision it was opened at, and
    /// every change they make is written as one command, which one undo takes back whole. Refused, and nothing written,
    /// where the workspace has left that revision, where a line no longer reads, and where there is nothing to write.
    /// </summary>
    public (WorkspaceState State, string Command, string Said) Apply(string workspaceId, Caller caller, LineDraft draft)
    {
        var opened = Opened(workspaceId, caller, draft);
        var (_, read) = Folded(opened, caller, draft, Of(opened).ToList());
        if (read.Count > 0 && read[^1].Line.Refused is { } unread)
            throw new WorkspaceRefused("DraftUnread", $"Line {read.Count} of the draft of {draft.Key} no longer reads: {unread.Sentence}");
        IReadOnlyList<WorkspaceChange> changes = [.. read.SelectMany(r => r.Changes!.Changes)];
        if (changes.Count == 0) throw new WorkspaceRefused("NothingToApply", $"The draft of {draft.Key} has no line to apply: cancel closes it.");
        var lines = TableWords.Counted(read.Count, "line");
        var next = workspaces.Batch(workspaceId, draft.Revision, caller.UserId, changes, caller.Origin, $"apply {draft.Key} ({lines})");
        return (next, next.Log[^1].Command?.Id ?? throw new InvalidOperationException("An accepted command has an id."), $"Applied {lines} to {draft.Key}.");
    }

    // A line typed while a draft is open (note 55): read on the workspace as the draft's lines leave it, with the
    // draft's dialog as the screen behind it, which is the draft's table with its row selected.
    LinePreview Drafted(string line, string workspaceId, Caller caller, LineDraft draft)
    {
        var opened = Opened(workspaceId, caller, draft);
        var named = Of(opened).ToList();
        var (left, read) = Folded(opened, caller, draft, named);
        var editing = $"Editing {draft.Key}: {(draft.Lines.Count == 0 ? "nothing" : TableWords.Counted(draft.Lines.Count, "line"))} pending.";
        if (read.Count > 0 && read[^1].Line.Refused is { } unread)
            return new(draft.Revision, null, null, null, null, null, null,
                new("DraftUnread", $"Line {read.Count} of the draft of {draft.Key} no longer reads: {unread.Sentence}", null, null, null, []), [], 0, View: editing);
        var screen = new LineView(draft.Table, Keys: [draft.Key], Draft: draft);
        return Read(line, left, caller, screen, named) with { Revision = draft.Revision, View = editing };
    }

    // The line a draft gains of what a line typed in it would write. A draft is of one row (note 55): a line that is of
    // another row, that would take the draft's own row away, or that changes nothing is refused, and adds nothing.
    LineDrafted Gained(LineDraft draft, LineChanges written, WorkspaceState left, Caller caller)
    {
        var bound = tables.Of(left);
        var table = written.Table is null ? null : bound.SingleOrDefault(t => t.Descriptor.TableId == written.Table);
        var own = table?.Descriptor.TableId == draft.Table && written.Rows.Count > 0 && written.Rows.All(key => key == draft.Key);
        var under = table?.Descriptor.Under == draft.Table && written.Rows.Count > 0
            && table.Table.Rows(left).Where(r => written.Rows.Contains(r.Key)).All(r => r.Under == draft.Key);
        if (!own && !under)
            throw new TableRefused("NotTheDraftsRow", $"The draft is of {draft.Key}, and this line is of {(written.Rows.Count == 0 ? "no row of it" : TableWords.Some(written.Rows))}: apply or cancel the draft first.");
        if (written.Changes.Count == 0) throw new TableRefused("NothingToAdd", $"{written.Would} The draft of {draft.Key} gains nothing.");
        if (own && tables.Of(left).Single(t => t.Descriptor.TableId == draft.Table).Table.Rows(workspaces.After(left, caller.UserId, written.Changes, caller.Origin)).All(r => r.Key != draft.Key))
            throw new TableRefused("RemovesTheDraftsRow", $"The draft is of {draft.Key}, which this line would take away: cancel the draft, and remove the row outside it.");
        return new(written.Line, $"Added to the draft of {draft.Key}: {written.Line}.");
    }

    // The caller's workspace at the revision the draft was opened at. One it has left is refused with what moved it
    // (note 54): the draft's lines were accepted on other rows than the workspace now has.
    WorkspaceState Opened(string workspaceId, Caller caller, LineDraft draft)
    {
        var state = WorkspaceCommands.Owned(workspaces, workspaceId, caller);
        if (state.Revision == draft.Revision) return state;
        throw new WorkspaceRefused("RevisionMoved",
            $"The draft of {draft.Key} was opened at revision {draft.Revision} and the workspace is at {state.Revision}, after {TableWords.Some(workspaces.Since(state, draft.Revision))}.");
    }

    // What a whole line would write on a state: its command's changes, where the command's line may be a draft's.
    static LineChanges Changes(string line, WorkspaceState state, Caller caller, IReadOnlyList<Typed> named)
    {
        var words = LineWords.Of(line);
        var said = Named(named, words) ?? throw new TableRefused("UnknownCommand", $"`{line}` begins with no command.");
        if (said.Changes is not { } changes)
            throw new TableRefused("NotOfADraft", $"{said.Said} is no line of a draft: a draft holds set, reset, new and remove.", said.Said);
        var last = words[said.Words.Length - 1];
        return changes(new LineContext(state, caller, line[(last.Start + last.Word.Length)..]));
    }

    // What a reading answers, or why it was refused, a table's refusal and a workspace's alike.
    static LineRefusal? Refusal<T>(Func<T> read, out T? answer) where T : class
    {
        answer = null;
        try
        {
            answer = read();
            return null;
        }
        catch (TableRefused refused)
        {
            return new(refused.Code, refused.Message, refused.Word, null, null, refused.Candidates);
        }
        catch (WorkspaceRefused refused)
        {
            return new(refused.Code, refused.Message, refused.Word, null, null, refused.Candidates);
        }
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
            // While a draft is open the line acts on it (note 55): no command but the draft's is read, and a line the
            // draft may hold is answered as the line it gains.
            if (view?.Draft is { } open && !said.InDraft)
                throw new TableRefused("DraftOpen", $"{said.Said} is not read while the draft of {open.Key} is open: apply writes it, and cancel discards it.", said.Said);
            var read = (said.Read ?? throw new InvalidOperationException($"{said.Id} neither reads its line nor stands for another."))(context);
            if (view?.Draft is { } draft && said.Changes is { } changes)
                return new(state.Revision, null, null, read.Arguments, read.Rows, read.Would, null, null, complete, completions.More, Asks: Gained(draft, changes(context), state, caller));
            return new(state.Revision, said.Command, read.Inputs, read.Arguments, read.Rows, read.Would, read.At, null, complete, completions.More, Asks: read.Asks);
        }
        catch (TableRefused refused)
        {
            return Refused(refused.Code, refused.Message, refused.Word, refused.Candidates);
        }
        catch (WorkspaceRefused refused)
        {
            // A domain verb's refusal names its word where it is about one (note 45 of step 4), as a table's does.
            return Refused(refused.Code, refused.Message, refused.Word, refused.Candidates);
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

    /// <summary>
    /// The rows a view's grid shows in the caller's own workspace as it stands (ADR-0016 note 41 of step 4): what a
    /// page that holds every row reads, at each revision it shows, to show the rows its grid's conditions leave. The
    /// conditions are read as a line's reading of <c>shown</c> reads them, so the two cannot disagree; ones that
    /// cannot be read are refused (<c>FilterUnread</c>), never answered as every row. Nothing is written.
    /// </summary>
    public ViewShown Shown(string workspaceId, Caller caller, LineView view)
    {
        var state = WorkspaceCommands.Owned(workspaces, workspaceId, caller, null);
        var table = Views.TableOf(view, tables.Of(state));
        var all = table.Table.Rows(state);
        var index = new Lazy<Selectors.RowIndex>(() => new(table, all));
        try
        {
            return new(table.Descriptor.TableId, state.Revision, [.. Views.Shown(view, table, all, key => index.Value.Row(key), state).Select(r => r.Key)], Views.Unfiltered(view, table, all).Count);
        }
        catch (TableRefused refused)
        {
            // No line was read, so there is no word to mark: the refusal is the workspace's, by the same code and sentence.
            throw new WorkspaceRefused(refused.Code, refused.Message);
        }
    }

    /// <summary>
    /// The content of a line of <c>copy</c> or <c>export</c> (ADR-0016 note 44 of step 4), for the caller's own
    /// workspace at the revision a reading named: the same line, read again by the same verb, so the two cannot
    /// disagree. A workspace that has moved, an unknown one and another user's are refused as a preview refuses them
    /// (<see cref="WorkspaceRefused"/>); a line that cannot be read, or that takes nothing out, is answered as that.
    /// Nothing is written.
    /// </summary>
    public LineContent Content(string line, string workspaceId, long? revision, Caller caller, LineView? view = null)
    {
        var state = WorkspaceCommands.Owned(workspaces, workspaceId, caller, revision);
        // The view is checked whole, as where a line's reading starts (Preview).
        if (view is not null) Views.Say(view, tables.Of(state), state);
        IReadOnlyList<Typed> named = [.. Of(state)];
        LineContent Refused(string code, string sentence, string? word, IReadOnlyList<string> candidates) => new(state.Revision, null, new(code, sentence, word, null, null, candidates));
        try
        {
            if (Meant(line, state, caller, view, named) is not ({ Content: { } content }, var context))
            {
                IReadOnlyList<string> takers = [.. named.Where(n => n.Content is not null).Select(n => n.Said)];
                return Refused("NoContent", $"`{line.Trim()}` takes nothing out of the workbench: {TableWords.And(takers)} {(takers.Count == 1 ? "does" : "do")}.", null, []);
            }
            return new(state.Revision, content(context), null);
        }
        catch (TableRefused refused)
        {
            return Refused(refused.Code, refused.Message, refused.Word, refused.Candidates);
        }
        catch (WorkspaceRefused refused)
        {
            // A domain verb's refusal names its word where it is about one (note 45 of step 4), as a table's does.
            return Refused(refused.Code, refused.Message, refused.Word, refused.Candidates);
        }
    }

    // What a line names, with the words after it: a short form (note 29 of step 3) is followed to the line it stands
    // for, as a reading follows it. None where the line begins with no command's words.
    static (Typed Said, LineContext Context)? Meant(string line, WorkspaceState state, Caller caller, LineView? view, IReadOnlyList<Typed> named)
    {
        var words = LineWords.Of(line);
        if (Named(named, words) is not { } said) return null;
        var last = words[said.Words.Length - 1];
        var context = new LineContext(state, caller, line[(last.Start + last.Word.Length)..], view);
        return said.Expand is { } expand ? Meant(expand(context), state, caller, view, [.. named.Where(n => n.Expand is null)]) : (said, context);
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
