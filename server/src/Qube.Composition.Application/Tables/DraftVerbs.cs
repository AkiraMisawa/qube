using Qube.Composition.Contracts;

namespace Qube.Composition.Application.Tables;

// ADR-0016 §10 and its notes 55 and 56 of 2026-11-12: the verbs of the screen that open a row's dialog and end its
// draft. The server reads their lines, as it reads every verb's; the page opens the dialog and holds the draft.

/// <summary>
/// <c>open &lt;row&gt;</c> (note 56): the one row whose dialog the page opens. The row is said as any command's rows
/// are, and with no word it is the row the keyboard is on.
/// </summary>
sealed class OpenVerb(TableRegister register) : IViewVerb
{
    const string Word = "open";

    public ViewVerbDescriptor Descriptor { get; } = new("open", "Open a row's dialog", new([Word], [new("row", CommandArgumentKind.Rows, Required: false)]));

    public ViewRead Read(LineContext line)
    {
        var tables = register.Of(line.State);
        var said = SelectorReader.Words(line.Text);
        var (selector, _, _) = TableService.RowsSaid(tables, null, said, line.View);
        // With no word and a screen behind the line, the row the keyboard is on, or the one row selected.
        var resolved = selector.SaysNoRows && selector.Table is null && line.View is { } view ? Selectors.LeftOut(view, tables, line.State) : Selectors.Resolve(selector, tables, line.State, line.View);
        if (resolved.Rows is not [var row])
            throw resolved.Rows.Count == 0 ? TableService.NoRowMatches(resolved, Word, $"{said} names no row of {resolved.Table.Word}: there is none to {Word}.")
                : new TableRefused("SeveralRows", $"{Word} opens the dialog of one row, and {(said.Length == 0 ? "the screen has" : $"{said} names")} {resolved.Rows.Count}: {TableWords.Some([.. resolved.Rows.Select(r => r.Key)])}.");
        return new(said.Length == 0 ? [] : [new("row", said)], TableService.Titled(resolved.Table, resolved.Table.Word, resolved.Rows), $"Opens {row.Key}.",
            new LineOpen(resolved.Table.Descriptor.TableId, row.Key, $"Opened {row.Key}."));
    }

    public LineCompletions Complete(LineContext line) => TableCompletions.Of(line.Text, register.Of(line.State), line.State, afterRows: null, line.View);
}

/// <summary><c>cancel</c> (note 55): the page discards the open draft. Nothing was written, and nothing is.</summary>
sealed class CancelVerb : IViewVerb
{
    public ViewVerbDescriptor Descriptor { get; } = new("cancel", "Discard the open draft", new(["cancel"], []));

    public ViewRead Read(LineContext line)
    {
        var draft = line.View?.Draft ?? throw new TableRefused("NoDraft", "cancel discards an open draft, and none is open.");
        var would = draft.Lines.Count == 0 ? $"Closes the draft of {draft.Key}." : $"Discards the draft of {draft.Key} and its {TableWords.Counted(draft.Lines.Count, "line")}.";
        return new([], null, would, new LineCancel($"Discarded the draft of {draft.Key}."));
    }

    public LineCompletions Complete(LineContext line) => LineCompletions.None;

    public bool WithDraft => true;
}
