namespace Qube.Composition.Application.Workspaces;

/// <summary>
/// One command as the log holds it (ADR-0016 §11): the entries one accepted call wrote, in order. An entry written
/// before commands were recorded is a command of its own with no stamp.
/// </summary>
public sealed record LoggedCommand(CommandStamp? Stamp, IReadOnlyList<EditEntry> Entries)
{
    /// <summary>What the command changed: its entries but the derivation's, and one for a change of context with its source.</summary>
    public IEnumerable<EditEntry> Edits => Entries.Where(e => e.Kind is not (EditKind.Derive or EditKind.Acquire));

    /// <summary>Whose command it is: the derivation writes under a name of its own.</summary>
    public EditEntry First => Edits.FirstOrDefault() ?? Entries[0];
}

/// <summary>
/// What can be taken back and what can be put back, read from the log alone (ADR-0016 §11 and its note 2 of 2026-11-11):
/// a command joins what can be taken back and ends what can be put back, an undo moves the latest to what can be put
/// back, and a redo moves it back.
/// </summary>
public static class CommandHistory
{
    public sealed record Stacks(IReadOnlyList<LoggedCommand> Undo, IReadOnlyList<LoggedCommand> Redo);

    public static Stacks Read(IReadOnlyList<EditEntry> log)
    {
        var undo = new List<LoggedCommand>();
        var redo = new List<LoggedCommand>();
        foreach (var command in Commands(log))
        {
            if (command.Stamp?.Undoes is { } undone)
            {
                Take(undo, undone, command);
                redo.Add(command);
            }
            else if (command.Stamp?.Redoes is { } redone)
            {
                Take(redo, redone, command);
                undo.Add(command);
            }
            else
            {
                undo.Add(command);
                redo.Clear();
            }
        }
        return new Stacks(undo, redo);
    }

    // The log is the service's own: an undo or a redo that names anything but the latest was never written.
    static void Take(List<LoggedCommand> from, string id, LoggedCommand by)
    {
        if (from.Count == 0 || from[^1].Stamp?.Id != id)
            throw new InvalidOperationException($"The log's command {by.Stamp?.Id} takes back {id}, which is not the latest command before it.");
        from.RemoveAt(from.Count - 1);
    }

    static IEnumerable<LoggedCommand> Commands(IReadOnlyList<EditEntry> log)
    {
        var entries = new List<EditEntry>();
        foreach (var entry in log)
        {
            if (entries.Count > 0 && (entry.Command is null || entry.Command.Id != entries[0].Command?.Id))
            {
                yield return new LoggedCommand(entries[0].Command, [.. entries]);
                entries.Clear();
            }
            entries.Add(entry);
        }
        if (entries.Count > 0) yield return new LoggedCommand(entries[0].Command, [.. entries]);
    }
}
