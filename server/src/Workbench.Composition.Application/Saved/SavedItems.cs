using System.Text.Json.Nodes;

namespace Workbench.Composition.Application.Saved;

/// <summary>Who sees a saved item besides its owner (C32): no one, the owner's desk, or every user.</summary>
public enum SavedVisibility { Private, Desk, All }

/// <summary>
/// Something a user saved under a name to use again (C32): a scenario set today, a workflow or a view later. The kernel
/// keeps it and decides who sees it; what it holds is its kind's, and the store never reads it. <see cref="Desk"/> is the
/// owner's desk when it was saved, and only for an item shared with that desk.
/// </summary>
public sealed record SavedItem(string Kind, string Name, string Owner, SavedVisibility Visibility, string? Desk, DateTimeOffset SavedAt, JsonNode Content)
{
    /// <summary>The name as items are told apart: an owner has one item of a kind per name, its case ignored.</summary>
    public string Key => Name.ToLowerInvariant();
}

/// <summary>Where saved items are kept: one per kind, owner and <see cref="SavedItem.Key"/>.</summary>
public interface ISavedItemStore
{
    /// <summary>Every item of a kind, whoever owns it, in no particular order.</summary>
    IReadOnlyList<SavedItem> All(string kind);

    /// <summary>Keeps the item, in place of its owner's item of that kind and key.</summary>
    void Save(SavedItem item);
}

/// <summary>Saved items in memory, for tests and a server started without a database.</summary>
public sealed class InMemorySavedItemStore : ISavedItemStore
{
    readonly Lock gate = new();
    readonly Dictionary<(string Kind, string Owner, string Key), SavedItem> items = [];

    public IReadOnlyList<SavedItem> All(string kind)
    {
        lock (gate) return [.. items.Values.Where(i => i.Kind == kind)];
    }

    public void Save(SavedItem item)
    {
        lock (gate) items[(item.Kind, item.Owner, item.Key)] = item;
    }
}

/// <summary>Each user's desk, as configuration names it (C32, ADR-0010 §4); a user it does not name has none.</summary>
public sealed class Desks(IReadOnlyDictionary<string, string> byUser)
{
    // A user named with no desk is a configuration mistake, said at start, never read as no desk.
    readonly Dictionary<string, string> desks = new(byUser.Select(d => d.Value.Trim().Length > 0 ? KeyValuePair.Create(d.Key, d.Value.Trim())
        : throw new InvalidOperationException($"The desks name {d.Key} with no desk.")), StringComparer.OrdinalIgnoreCase);

    /// <summary>No user has a desk.</summary>
    public static Desks None { get; } = new(new Dictionary<string, string>());

    public string? Of(string user) => desks.GetValueOrDefault(user);
}

/// <summary>A save or a load refused, with its code and why, said to the user.</summary>
public sealed class SavedItemRefused(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// The rules of saved items (C32): an item is its owner's alone unless shared with the owner's desk or with every user;
/// saving again replaces the owner's own item of that name, and no one else's; a name two visible items share is
/// refused, naming their owners, and <c>&lt;name&gt;@&lt;owner&gt;</c> picks one. The item's kind names it to the user
/// (a set, a workflow), so a refusal reads in the command's words.
/// </summary>
public sealed class SavedItems(ISavedItemStore store, Desks desks, TimeProvider time)
{
    /// <summary>The longest name an item takes.</summary>
    public const int MaxName = 64;

    /// <summary>Saves the content under the user's name for it, shared as asked; says whether it replaced their own.</summary>
    public (SavedItem Item, bool Replaced) Save(string kind, string noun, string user, string name, SavedVisibility visibility, JsonNode content)
    {
        var named = Name(noun, name);
        var desk = desks.Of(user);
        if (visibility == SavedVisibility.Desk && desk is null)
            throw new SavedItemRefused("NoDesk", $"{user} belongs to no desk, so the {noun} {named} cannot be shared with one: keep it to yourself, or share it with all.");
        var item = new SavedItem(kind, named, user, visibility, visibility == SavedVisibility.Desk ? desk : null, time.GetUtcNow(), content.DeepClone());
        var replaced = store.All(kind).Any(i => i.Owner == user && i.Key == item.Key);
        store.Save(item);
        return (item, replaced);
    }

    /// <summary>The items of a kind the user can see: their own, their desk's, and those shared with all, by name and owner.</summary>
    public IReadOnlyList<SavedItem> Visible(string kind, string user)
    {
        var desk = desks.Of(user);
        return [.. store.All(kind)
            .Where(i => i.Owner == user || i.Visibility == SavedVisibility.All
                || (i.Visibility == SavedVisibility.Desk && desk is not null && string.Equals(i.Desk, desk, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(i => i.Key, StringComparer.Ordinal).ThenBy(i => i.Owner, StringComparer.Ordinal)];
    }

    /// <summary>
    /// The item the user names, as <c>&lt;name&gt;</c> or <c>&lt;name&gt;@&lt;owner&gt;</c>, among those they can see. None, or
    /// a name two of them share, is refused with what they can see.
    /// </summary>
    public SavedItem Find(string kind, string noun, string user, string named)
    {
        var text = named.Trim();
        var at = text.LastIndexOf('@');
        var (name, owner) = at < 0 ? (text, null) : (text[..at], text[(at + 1)..]);
        var visible = Visible(kind, user);
        var matching = visible.Where(i => i.Key == name.ToLowerInvariant() && (owner is null || string.Equals(i.Owner, owner, StringComparison.OrdinalIgnoreCase))).ToList();
        return matching.Count switch
        {
            1 => matching[0],
            0 => throw new SavedItemRefused("UnknownSaved", visible.Count == 0
                ? $"There is no {noun} {text} you can see: none is saved for you, your desk or all."
                : $"There is no {noun} {text} you can see: there are {string.Join(", ", visible.Select(i => Label(i, visible)))}."),
            _ => throw new SavedItemRefused("AmbiguousSaved",
                $"{matching.Count} {noun}s named {name} can be seen, {string.Join(" and ", matching.Select(i => $"{i.Owner}'s"))}: name one, as {string.Join(" or ", matching.Select(i => $"{i.Name}@{i.Owner}"))}."),
        };
    }

    /// <summary>How the user names an item among those they see: its name alone, or with its owner where another shares it.</summary>
    public static string Label(SavedItem item, IReadOnlyList<SavedItem> visible) =>
        visible.Count(i => i.Key == item.Key) > 1 ? $"{item.Name}@{item.Owner}" : item.Name;

    // A name is one word, so a command can say how it is shared after it, and has no @, which names an owner.
    static string Name(string noun, string name)
    {
        var named = name.Trim();
        return named.Length == 0 ? throw new SavedItemRefused("InvalidName", $"A {noun} needs a name, such as rates-up.")
            : named.Any(char.IsWhiteSpace) || named.Contains('@')
                ? throw new SavedItemRefused("InvalidName", $"A {noun}'s name is one word without @, such as rates-up; {named} is not.")
            : named.Length > MaxName ? throw new SavedItemRefused("InvalidName", $"A {noun}'s name is {MaxName} characters at most.")
            : named;
    }
}
