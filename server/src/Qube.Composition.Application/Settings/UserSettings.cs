using System.Text.Json;
using System.Text.Json.Nodes;
using Qube.Composition.Application.Tables;

namespace Qube.Composition.Application.Settings;

/// <summary>
/// What a setting is (ADR-0017 §1): a preference changes how things look or are found, never a figure; a default
/// becomes an input value of something the user makes, and is read only when it is made (§3).
/// </summary>
public enum SettingKind { Preference, Default }

/// <summary>
/// A setting as the module it belongs to registers it (ADR-0017 §2), as a table's field is registered: its key, its
/// title and the words a line names it by, its kind, and how its value is written. A typed setting has a
/// <see cref="FieldSpec"/>: it is typed as a field is and read by the one reader of values. A structured one (a map of
/// widths) is written by the page as JSON, and its module says why a value is refused (<see cref="Check"/>). A key that
/// ends in <c>.*</c> is a family, whose members are kept by their own keys (<c>grid.widths.rates/lines</c>) and named
/// by the family's words and the rest of the key (<c>widths rates/lines</c>).
/// </summary>
/// <param name="Registered">The value when the user has set none; none where there is none (a default that leaves the field empty).</param>
/// <param name="Check">For a structured setting: why a value is refused, or null where it is taken.</param>
public sealed record SettingDefinition(
    string Key,
    string Title,
    IReadOnlyList<string> Words,
    SettingKind Kind,
    FieldSpec? Spec,
    JsonNode? Registered,
    Func<JsonNode, string?>? Check = null)
{
    /// <summary>A family of settings, one per member key under its prefix.</summary>
    public bool IsFamily => Key.EndsWith(".*", StringComparison.Ordinal);

    /// <summary>What a member's key starts with.</summary>
    public string Prefix => IsFamily ? Key[..^1] : Key;

    /// <summary>The word a line names it by first.</summary>
    public string Word => Words.Count > 0 ? Words[0] : Key;
}

/// <summary>A module's settings (ADR-0017 §2), for the kernel to keep.</summary>
public interface ISettingCatalog
{
    IEnumerable<SettingDefinition> Settings { get; }
}

/// <summary>A value a user set, as it is kept: by its key, with when it was set.</summary>
public sealed record StoredSetting(string Key, JsonNode Value, DateTimeOffset At);

/// <summary>Where users' settings are kept (ADR-0017 §2): one value per user and key, replaced by a write.</summary>
public interface IUserSettingStore
{
    /// <summary>Every value the user set, in no particular order.</summary>
    IReadOnlyList<StoredSetting> All(string user);

    /// <summary>Keeps the value in place of the user's value of that key.</summary>
    void Put(string user, StoredSetting setting);

    /// <summary>Removes the user's value of that key; says whether there was one.</summary>
    bool Remove(string user, string key);
}

/// <summary>Settings in memory, for tests and a server started without a database.</summary>
public sealed class InMemoryUserSettingStore : IUserSettingStore
{
    readonly Lock gate = new();
    readonly Dictionary<(string User, string Key), StoredSetting> settings = [];

    public IReadOnlyList<StoredSetting> All(string user)
    {
        lock (gate) return [.. settings.Where(s => s.Key.User == user).Select(s => s.Value)];
    }

    public void Put(string user, StoredSetting setting)
    {
        lock (gate) settings[(user, setting.Key)] = setting with { Value = setting.Value.DeepClone() };
    }

    public bool Remove(string user, string key)
    {
        lock (gate) return settings.Remove((user, key));
    }
}

/// <summary>
/// A setting as one user has it: its registration, its own key (a family member's), the name a line types it by, the
/// value that applies, whether the user set it, and, where the value they set is no longer one its form takes, why
/// (<see cref="Unreadable"/>): the value that applies is then the registered one, and <see cref="Stored"/> is what was set.
/// </summary>
public sealed record SettingRead(SettingDefinition Definition, string Key, string Name, JsonNode? Value, bool Set, JsonNode? Stored, string? Unreadable, DateTimeOffset? At);

/// <summary>A setting refused, with its code and why, said to the user.</summary>
public sealed class SettingRefused(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// A user's settings (ADR-0017 §2): what the modules register, kept per user in the store. A value is checked against
/// its form when it is set, and again when it is read, since a registration may have changed since: one no longer taken
/// is said unreadable, never replaced. A user reads and writes their own; nothing here takes a user from a request.
/// </summary>
public sealed class UserSettings
{
    readonly IUserSettingStore store;
    readonly TimeProvider time;

    /// <summary>Every registration, in the order the catalogs give them.</summary>
    public IReadOnlyList<SettingDefinition> Definitions { get; }

    /// <summary>Fails at start where two registrations share a key or a word, or one is neither typed nor checked.</summary>
    public UserSettings(IEnumerable<ISettingCatalog> catalogs, IUserSettingStore store, TimeProvider time)
    {
        this.store = store;
        this.time = time;
        Definitions = [.. catalogs.SelectMany(c => c.Settings)];
        foreach (var definition in Definitions)
        {
            if ((definition.Spec is null) == (definition.Check is null))
                throw new InvalidOperationException($"Setting {definition.Key} is typed (a field's form) or structured (a check), and one of the two.");
            if (definition.IsFamily && definition.Spec is not null)
                throw new InvalidOperationException($"Setting {definition.Key} is a family, which the page writes: it is structured.");
        }
        if (Definitions.GroupBy(d => d.Key, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1) is { } key)
            throw new InvalidOperationException($"Two settings are registered as {key.Key}.");
        if (Definitions.SelectMany(d => d.Words.Select(w => (Word: TableWords.Of(w), d.Key))).GroupBy(w => w.Word).FirstOrDefault(g => g.Count() > 1) is { } word)
            throw new InvalidOperationException($"Settings {TableWords.And(word.Select(w => w.Key))} are both named {word.Key}.");
    }

    /// <summary>
    /// Every setting of the user: each registered one that is not a family, and each family member they set, in the
    /// order of registration.
    /// </summary>
    public IReadOnlyList<SettingRead> Read(string user)
    {
        var stored = store.All(user).ToDictionary(s => s.Key, StringComparer.Ordinal);
        var read = new List<SettingRead>();
        foreach (var definition in Definitions)
        {
            if (!definition.IsFamily)
            {
                read.Add(Reading(definition, definition.Key, stored.GetValueOrDefault(definition.Key)));
                continue;
            }
            foreach (var member in stored.Values.Where(s => s.Key.StartsWith(definition.Prefix, StringComparison.Ordinal) && s.Key.Length > definition.Prefix.Length)
                         .OrderBy(s => s.Key, StringComparer.Ordinal))
                read.Add(Reading(definition, member.Key, member));
        }
        return read;
    }

    /// <summary>The user's setting by its key or its name (<c>theme</c>, <c>widths rates/lines</c>); one not registered is refused with the names there are.</summary>
    public SettingRead One(string user, string name)
    {
        var (definition, key) = Find(name);
        return Reading(definition, key, store.All(user).FirstOrDefault(s => s.Key == key));
    }

    /// <summary>Sets a setting from its value as typed, read as its form reads it (JSON for a structured one); says what it replaced.</summary>
    public (SettingRead Replaced, SettingRead Now) Set(string user, string name, string text)
    {
        var (definition, key) = Find(name);
        return Put(user, definition, key, Value(definition, text));
    }

    /// <summary>Sets a setting by its key to a value as the page holds it; says what it replaced.</summary>
    public (SettingRead Replaced, SettingRead Now) SetValue(string user, string key, JsonNode value)
    {
        var (definition, own) = Find(key);
        if (definition.Spec is { } spec) return Put(user, definition, own, Value(definition, FieldForms.Write(spec, value)));
        return Put(user, definition, own, Checked(definition, value));
    }

    /// <summary>Removes the user's value, so the registered one applies; says what it replaced.</summary>
    public (SettingRead Replaced, SettingRead Now) Reset(string user, string name)
    {
        var (definition, key) = Find(name);
        var replaced = Reading(definition, key, store.All(user).FirstOrDefault(s => s.Key == key));
        store.Remove(user, key);
        return (replaced, Reading(definition, key, null));
    }

    /// <summary>
    /// A default as a making reads it (ADR-0017 §3): the value the user set, or none where they set none, for the
    /// making to take its own (a profile's) and say so. A value no longer taken refuses the making, by the setting's
    /// name, rather than starting from another value.
    /// </summary>
    public JsonNode? Default(string user, string key)
    {
        var read = One(user, key);
        if (read.Unreadable is { } why)
            throw new SettingRefused("UnreadableSetting",
                $"Your {read.Definition.Title.ToLowerInvariant()} setting, {Stored(read)}, is no longer {why}: set it again, or type settings reset {read.Name}.");
        return read.Set ? read.Value?.DeepClone() : null;
    }

    /// <summary>A setting's value as a line types it: as its form writes it, JSON for a structured one, <c>none</c> for no value.</summary>
    public string Text(SettingRead read) => Text(read.Definition, read.Value);

    /// <summary>What the user set, as a line would type it, where it is unreadable.</summary>
    public static string Stored(SettingRead read) => read.Stored is null ? "none" : Text(read.Definition, read.Stored, lenient: true);

    /// <summary>What a setting takes, in words that follow "is".</summary>
    public static string Takes(SettingDefinition definition) => definition.Spec is { } spec ? FieldForms.Takes(spec) : "a value its page writes, as JSON";

    static string Text(SettingDefinition definition, JsonNode? value, bool lenient = false)
    {
        if (value is null) return "none";
        if (definition.Spec is not { } spec) return value.ToJsonString();
        try
        {
            return FieldForms.Write(spec, value);
        }
        catch (Exception e) when (lenient && e is InvalidOperationException or FormatException or JsonException)
        {
            // A value its form no longer writes is shown as it was kept, so the user sees what is wrong with it.
            return value.ToJsonString();
        }
    }

    (SettingRead Replaced, SettingRead Now) Put(string user, SettingDefinition definition, string key, JsonNode value)
    {
        var replaced = Reading(definition, key, store.All(user).FirstOrDefault(s => s.Key == key));
        var kept = new StoredSetting(key, value, time.GetUtcNow());
        store.Put(user, kept);
        return (replaced, Reading(definition, key, kept));
    }

    SettingRead Reading(SettingDefinition definition, string key, StoredSetting? stored)
    {
        var name = Name(definition, key);
        if (stored is null) return new(definition, key, name, definition.Registered?.DeepClone(), false, null, null, null);
        var why = Refusal(definition, stored.Value);
        return why is null
            ? new(definition, key, name, stored.Value.DeepClone(), true, stored.Value.DeepClone(), null, stored.At)
            : new(definition, key, name, definition.Registered?.DeepClone(), true, stored.Value.DeepClone(), why, stored.At);
    }

    // Why a value is not one the setting takes, in words that follow "is": null where it is taken.
    static string? Refusal(SettingDefinition definition, JsonNode value)
    {
        if (definition.Check is { } check) return check(value);
        var spec = definition.Spec!;
        string written;
        try
        {
            written = FieldForms.Write(spec, value);
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException or JsonException)
        {
            return FieldForms.Takes(spec);
        }
        var read = FieldForms.Read(spec, written);
        return read.Problem == FieldForms.Problem.None && FieldForms.Same(read.Value, value) ? null : read.What ?? FieldForms.Takes(spec);
    }

    static JsonNode Value(SettingDefinition definition, string text)
    {
        if (definition.Spec is { } spec)
        {
            var read = FieldForms.Read(spec, text);
            if (read.Problem != FieldForms.Problem.None || read.Value is null)
                throw new SettingRefused("InvalidValue", $"{definition.Title} is {read.What ?? FieldForms.Takes(spec)}; {Quoted(text)} is not.");
            return read.Value;
        }
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            throw new SettingRefused("InvalidValue", $"{definition.Title} is written by its page as JSON; {Quoted(text)} is not.");
        }
        return Checked(definition, parsed ?? throw new SettingRefused("InvalidValue", $"{definition.Title} takes a value; null is none. To clear it, reset it."));
    }

    static JsonNode Checked(SettingDefinition definition, JsonNode value) =>
        Refusal(definition, value) is { } why ? throw new SettingRefused("InvalidValue", $"{definition.Title} is {why}; {Quoted(value.ToJsonString())} is not.") : value.DeepClone();

    static string Quoted(string text) => text.Trim().Length == 0 ? "nothing" : text.Trim();

    // A setting by its key, or by its words, a family member by the family's words and the rest of its key.
    (SettingDefinition Definition, string Key) Find(string name)
    {
        var said = name.Trim();
        foreach (var definition in Definitions)
        {
            if (!definition.IsFamily)
            {
                if (string.Equals(said, definition.Key, StringComparison.OrdinalIgnoreCase) || definition.Words.Any(w => TableWords.Of(w) == TableWords.Of(said)))
                    return (definition, definition.Key);
                continue;
            }
            if (said.StartsWith(definition.Prefix, StringComparison.OrdinalIgnoreCase) && said.Length > definition.Prefix.Length)
                return (definition, definition.Prefix + said[definition.Prefix.Length..]);
            // The family's words, then the member's name: what follows as many words of the line as the family's has.
            var tokens = said.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            foreach (var word in definition.Words.Select(TableWords.Of))
            {
                var count = word.Split(' ').Length;
                if (tokens.Length > count && TableWords.Of(string.Join(' ', tokens.Take(count))) == word)
                    return (definition, definition.Prefix + string.Join(' ', tokens.Skip(count)));
            }
        }
        var names = Definitions.Select(d => d.IsFamily ? $"{d.Word} <name>" : d.Word);
        throw new SettingRefused("UnknownSetting", said.Length == 0 ? $"Name a setting: {TableWords.List(names)}." : $"No setting {said}: the settings are {TableWords.List(names)}.");
    }

    static string Name(SettingDefinition definition, string key) => definition.IsFamily ? $"{definition.Word} {key[definition.Prefix.Length..]}" : definition.Word;
}
