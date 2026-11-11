using System.Text.Json.Nodes;

namespace Qube.Composition.Application.Workspaces;

// The generic workspace (ADR-0003 §6, §7). The kernel stores what the user changed and checks whether it
// still means what they meant; what an entity, a field or a scope is belongs to the domain module.

/// <summary>
/// What an edit changes: one field of one entity, within a scope, in a layer. All four are opaque to the kernel; the
/// module that registered the edit kind gives them meaning (for XVA a scope is a counterparty registration, and a
/// layer a case, ADR-0010 Consequences). The layer is part of the target, so one field holds an edit in no layer and
/// one in each layer at once, each carried and checked on its own; how layers compose is the module's.
/// </summary>
public sealed record EditTarget(string Scope, string Entity, string Field, string? Layer = null);

/// <summary>
/// One change of a batch (<see cref="WorkspaceService.Batch"/>), made as the command of the same name makes it: an
/// override, a reset, a created entity, or one removed with the layer it owns.
/// </summary>
public abstract record WorkspaceChange
{
    public sealed record Set(EditTarget Target, JsonNode? Value) : WorkspaceChange;
    public sealed record Reset(EditTarget Target) : WorkspaceChange;
    public sealed record Create(string Scope, string Entity, JsonObject Fields) : WorkspaceChange;
    public sealed record Remove(string Entity, string? Layer = null) : WorkspaceChange;
}

/// <summary>A source value as read: present (possibly empty), never confused with absent.</summary>
public sealed record SourceValue(JsonNode? Value);

/// <summary>
/// One acquired version of everything a workspace's overrides may point at. <see cref="Read"/> returns
/// null when the entity or the field does not exist in this version.
/// </summary>
public interface ISourceVersion
{
    string Version { get; }

    /// <summary>
    /// What acquires exactly this version again: stored with the workspace, so the workspace can be restored
    /// after a restart. The module that resolves it must answer the same <see cref="Version"/>, or refuse.
    /// </summary>
    JsonObject Descriptor { get; }

    SourceValue? Read(string entity, string field);
}

/// <summary>Restores a module's source version from its descriptor. Registered per workspace app.</summary>
public delegate ISourceVersion SourceResolver(JsonObject descriptor);

/// <summary>
/// A user's override: its value, and the base it overrode, the source values of the field's conflict group
/// in the version current when it was made.
/// </summary>
public sealed record Override(
    EditTarget Target,
    JsonNode? Value,
    IReadOnlyDictionary<string, JsonNode?> Base,
    string BaseVersion,
    long Seq);

/// <summary>An override whose base changed under it. It is not applied until the user decides.</summary>
public sealed record Conflict(Override Override, IReadOnlyDictionary<string, JsonNode?> Source, string SourceVersion);

/// <summary>An override whose entity or field is gone from the current version. It is never moved elsewhere.</summary>
public sealed record LostTarget(Override Override, string SourceVersion, bool Acknowledged);

public enum EditKind
{
    Context,
    Acquire,
    Set,
    Reset,
    KeepOverride,
    TakeSource,
    AcknowledgeLost,
    DropScope,
    Create,
    Remove,
    Derive,
}

/// <summary>
/// The command an entry belongs to (ADR-0016 §11): one accepted call to the workspace service, whichever door it came
/// through. <c>Id</c> is <c>c</c> and the first sequence number the call wrote, unique in its workspace. <c>Said</c>
/// is the command in words where its caller gave them (<c>set where ccy=USD after off</c>). An undo names the command
/// it takes back in <c>Undoes</c>, and a redo the undo it takes back in <c>Redoes</c>; their <c>Said</c> is the name
/// of the command they act on, so a command put back and taken back again is still named.
/// </summary>
public sealed record CommandStamp(string Id, string? Said = null, string? Undoes = null, string? Redoes = null);

/// <summary>
/// What an undo took back, or a redo put back (ADR-0016 §11): the command's id, its words or its edits' titles, whose
/// it was and from where, when it ran, and how many edits it held. The sentence holds no time: a client writes
/// <c>At</c> in its user's zone.
/// </summary>
public sealed record TakenBack(string Command, string Said, string User, RunOrigin? Origin, DateTimeOffset At, int Edits);

/// <summary>
/// What an entry found where it wrote, as it stood (ADR-0016 §11): enough, with the entry, to write its inverse from
/// the log alone. A set or a reset records the override and the lost edit on its target; a remove the entity and the
/// edits that ended with it; a change of context the context and the source version before it.
/// </summary>
public sealed record Replaced(
    Override? Override = null,
    LostTarget? Lost = null,
    CreatedEntity? Created = null,
    IReadOnlyList<Override>? Overrides = null,
    IReadOnlyList<Conflict>? Conflicts = null,
    IReadOnlyList<LostTarget>? LostEdits = null,
    JsonObject? Context = null,
    string? SourceVersion = null);

/// <summary>
/// One entry of the append-only edit log. An undo is a new entry, never a removal. <c>DomainKind</c> names
/// the module's edit kind (for example <c>xva.assignment</c>), so one log holds every domain's edits. <c>Origin</c>
/// says where the edit came from (the page, the AI acting for the user; ADR-0005); null where the caller did not
/// state it, and on entries written before origins were recorded. <c>Command</c> and <c>Replaced</c> say which
/// command wrote the entry and what it replaced (ADR-0016 §11); an entry written before they were recorded has
/// neither, and is not taken back.
/// </summary>
public sealed record EditEntry(
    long Seq,
    DateTimeOffset At,
    string User,
    EditKind Kind,
    string? DomainKind,
    EditTarget? Target,
    JsonNode? Value,
    string? Note,
    RunOrigin? Origin = null,
    CommandStamp? Command = null,
    Replaced? Replaced = null);

/// <summary>What a module declares about its edits: which fields are compared together.</summary>
public interface IEditRules
{
    /// <summary>The module's name for this kind of edit, recorded on its log entries.</summary>
    string KindOf(EditTarget target);

    /// <summary>The fields compared as one with this target's field. Must include the field itself.</summary>
    IReadOnlyList<string> ConflictGroup(EditTarget target);

    /// <summary>
    /// The sources this module derives from the workspace's own values (ADR-0004), by derivation scope. A pure
    /// function of the state, run again after every accepted command and after a load; an override whose
    /// target scope is a derivation scope is read against that scope's source. Most modules derive nothing.
    /// </summary>
    IReadOnlyDictionary<string, ISourceVersion> Derive(WorkspaceState state) => NoDerivations;

    /// <summary>
    /// How an edit's target is named to the user, as the screen names it (ADR-0016 §11's note 3 of 2026-11-11): a row
    /// and its column, never an entity's key where the module has a title. A whole entity's field is <c>*</c>.
    /// </summary>
    string TitleOf(WorkspaceState state, EditTarget target) => target.Field == "*" ? target.Entity : $"{target.Entity} {target.Field}";

    static readonly IReadOnlyDictionary<string, ISourceVersion> NoDerivations = new Dictionary<string, ISourceVersion>();
}

/// <summary>
/// An entity the workspace itself holds, because the user created it (ADR-0004). Its initial field values act
/// as its source values: an edit on it is an override with that base, as on an acquired entity.
/// </summary>
/// <param name="Sources">
/// Where each field the user did not type came from (ADR-0008 §4): a product's template, the workspace's defaults, a
/// confirmed default. Absent for an entity created before ADR-0008, or whose every field was given.
/// </param>
public sealed record CreatedEntity(string Scope, string Entity, JsonObject Fields, long Seq, IReadOnlyDictionary<string, string>? Sources = null);

/// <summary>A created entity read as a source version, so the carry check and overrides treat it as any other.</summary>
public sealed class CreatedSource(CreatedEntity created) : ISourceVersion
{
    public string Version { get; } = $"created:{created.Seq}";
    public JsonObject Descriptor => new() { ["created"] = created.Entity };

    public SourceValue? Read(string entity, string field) =>
        entity == created.Entity && created.Fields.TryGetPropertyValue(field, out var value) ? new SourceValue(value?.DeepClone()) : null;
}

/// <summary>A derivation scope that no longer exists: every override in it has lost its target.</summary>
public sealed class EmptySource(string version) : ISourceVersion
{
    public string Version { get; } = version;
    public JsonObject Descriptor => [];
    public SourceValue? Read(string entity, string field) => null;
}

/// <summary>Each field is its own group: the default when a module declares nothing.</summary>
public sealed class FieldByField(string kind) : IEditRules
{
    public string KindOf(EditTarget target) => kind;

    public IReadOnlyList<string> ConflictGroup(EditTarget target) => [target.Field];
}

/// <summary>Where each override stands against a new source version.</summary>
public abstract record CarryOutcome
{
    public sealed record Carried(Override Override) : CarryOutcome;
    public sealed record Conflicted(Conflict Conflict) : CarryOutcome;
    public sealed record Lost(LostTarget Target) : CarryOutcome;
}

public static class Carry
{
    /// <summary>
    /// Checks an override against a new version (ADR-0003 §7): its target gone, its base unchanged, or its
    /// base changed. A conflict group field missing from the new version counts as changed.
    /// </summary>
    public static CarryOutcome Check(Override item, ISourceVersion next, IEditRules rules)
    {
        if (next.Read(item.Target.Entity, item.Target.Field) is null)
            return new CarryOutcome.Lost(new LostTarget(item, next.Version, false));
        var now = BaseOf(item.Target, next, rules);
        var same = now.Count == item.Base.Count
            && item.Base.All(b => now.TryGetValue(b.Key, out var v) && JsonNode.DeepEquals(v, b.Value));
        return same
            ? new CarryOutcome.Carried(item with { BaseVersion = next.Version })
            : new CarryOutcome.Conflicted(new Conflict(item, now, next.Version));
    }

    /// <summary>The source values of a target's conflict group in a version; absent fields are left out.</summary>
    public static IReadOnlyDictionary<string, JsonNode?> BaseOf(EditTarget target, ISourceVersion source, IEditRules rules)
    {
        var group = rules.ConflictGroup(target);
        if (!group.Contains(target.Field))
            throw new InvalidOperationException($"The conflict group of {target.Field} does not contain the field itself.");
        var values = new Dictionary<string, JsonNode?>();
        foreach (var field in group)
            if (source.Read(target.Entity, field) is { } value)
                values[field] = value.Value?.DeepClone();
        return values;
    }
}
