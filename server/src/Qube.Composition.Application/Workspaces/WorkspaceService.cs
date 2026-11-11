using System.Collections.Immutable;
using System.Text.Json.Nodes;

namespace Qube.Composition.Application.Workspaces;

/// <summary>
/// A workspace as it stands: its context, the source version its overrides are read against, the user's
/// overrides, the ones held back as conflicts or lost targets, and the log that explains how it got here.
/// The revision is the last log entry's sequence number.
/// </summary>
public sealed record WorkspaceState(
    string WorkspaceId,
    string Owner,
    string App,
    JsonObject Context,
    ISourceVersion Source,
    ImmutableDictionary<EditTarget, Override> Overrides,
    ImmutableDictionary<EditTarget, Conflict> Conflicts,
    ImmutableDictionary<EditTarget, LostTarget> Lost,
    ImmutableList<EditEntry> Log)
{
    public long Revision => Log.Count == 0 ? 0 : Log[^1].Seq;

    /// <summary>The entities the user created in this workspace (ADR-0004), by entity.</summary>
    public ImmutableDictionary<string, CreatedEntity> Created { get; init; } = ImmutableDictionary<string, CreatedEntity>.Empty;

    /// <summary>The module's derived sources as of this revision, by derivation scope (ADR-0004). Never stored: derived again on load.</summary>
    public ImmutableDictionary<string, ISourceVersion> Derived { get; init; } = ImmutableDictionary<string, ISourceVersion>.Empty;

    /// <summary>The source an edit on this target is read against: its derivation scope's, its created entity's, or the acquired one.</summary>
    public ISourceVersion SourceOf(EditTarget target) =>
        Derived.TryGetValue(target.Scope, out var derived) ? derived
        : Created.TryGetValue(target.Entity, out var created) ? new CreatedSource(created)
        : Source;

    /// <summary>What the derivation run by the last command did to the edits (ADR-0004); null when no derived scope moved. Not stored.</summary>
    public CarrySummary? Rederived { get; init; }

    /// <summary>True when the target is read from the acquired source, which a re-acquisition replaces.</summary>
    public bool IsAcquired(EditTarget target) => !Derived.ContainsKey(target.Scope) && !Created.ContainsKey(target.Entity);

    /// <summary>
    /// True where the edit was written by the workspace's making: a value it was made with, such as a user's default
    /// (ADR-0017 §3), which is the workspace's from its start and which <c>undo</c> never takes back.
    /// </summary>
    public bool IsMade(Override edit)
    {
        var entry = edit.Seq >= 1 && edit.Seq <= Log.Count ? Log[(int)edit.Seq - 1] : null;
        if (entry?.Seq != edit.Seq) throw new InvalidOperationException($"Workspace {WorkspaceId}'s log holds no entry {edit.Seq} where its edits say.");
        return entry.Command?.Id == CommandStamp.IdAt(1);
    }

    /// <summary>How many edits of each module kind a scope holds, for the confirmation before dropping it.</summary>
    public IReadOnlyDictionary<EditTarget, Override> InScope(string scope) =>
        Overrides.Values.Concat(Conflicts.Values.Select(c => c.Override)).Concat(Lost.Values.Select(l => l.Override))
            .Where(o => o.Target.Scope == scope)
            .ToDictionary(o => o.Target);
}

/// <summary>What re-acquiring did to the overrides (ADR-0003 §7): said to the user after every re-acquisition.</summary>
public sealed record CarrySummary(int Carried, int Conflicted, int Lost);

/// <summary>
/// A frozen revision: what Price submits. The Run keeps this copy; the workspace may later be deleted.
/// <see cref="State"/> is the immutable state at that revision, from which the module builds its accepted input.
/// </summary>
public sealed record WorkspaceFreeze(
    string WorkspaceId,
    long Revision,
    JsonObject Context,
    string SourceVersion,
    IReadOnlyList<Override> Overrides,
    WorkspaceState State);

/// <summary>A command refused. Nothing changed.</summary>
/// <param name="word">
/// The word of a typed line the refusal is about, where it is about one (ADR-0016 note 45 of step 4): a reading marks
/// it where it stands, as it marks a table's (<c>TableRefused</c>).
/// </param>
/// <param name="candidates">What that word could have been, where the refusal can say.</param>
public sealed class WorkspaceRefused(string code, string message, IReadOnlyList<EditTarget>? targets = null, string? word = null, IReadOnlyList<string>? candidates = null)
    : Exception(message)
{
    public string Code { get; } = code;
    public IReadOnlyList<EditTarget> Targets { get; } = targets ?? [];
    public string? Word { get; } = word;
    public IReadOnlyList<string> Candidates { get; } = candidates ?? [];

    /// <summary>
    /// The refusal with the edits it waits on said in its sentence, where it names any: a line and a notice show a
    /// sentence alone, so the targets would otherwise reach nobody (ADR-0016 note 33).
    /// </summary>
    public WorkspaceRefused Named() =>
        Targets.Count == 0 ? this : new(Code, $"{Message.TrimEnd('.')}: {string.Join(", ", Targets.Select(t => $"{t.Entity} {t.Field}"))}.", Targets, Word, Candidates);
}

/// <summary>
/// The workspace commands. Every command names the revision it was taken against and is refused if the
/// workspace has moved since, so an outcome never depends on timing. Each accepted command is written to the
/// store before it is answered (ADR-0003 §6); states are cached in memory once loaded.
/// </summary>
public sealed class WorkspaceService(IReadOnlyDictionary<string, IEditRules> rules, TimeProvider clock, IWorkspaceStore? store = null,
    IEnumerable<IWorkspaceObserver>? observers = null)
{
    readonly IWorkspaceStore store = store ?? new InMemoryWorkspaceStore();
    readonly ILookup<string, IWorkspaceObserver> observers = (observers ?? []).ToLookup(o => o.App);
    readonly Dictionary<string, WorkspaceState> workspaces = [];
    readonly Lock gate = new();

    /// <summary>
    /// Makes a workspace. <paramref name="with"/> are values it is made with, such as what its owner chose last: each is
    /// an edit in the log like any other, and part of the making's own command, so `undo` finds nothing of them to take
    /// back (ADR-0016 §11, note 2 of 2026-11-11).
    /// </summary>
    public WorkspaceState Create(string owner, string app, JsonObject context, ISourceVersion source, RunOrigin? origin = null,
        IReadOnlyList<WorkspaceChange.Set>? with = null)
    {
        RulesOf(app);
        var id = $"ws-{Guid.CreateVersion7():N}";
        var made = new CommandStamp(CommandStamp.IdAt(1));
        var log = ImmutableList<EditEntry>.Empty
            .Add(new EditEntry(1, clock.GetUtcNow(), owner, EditKind.Context, null, null, context.DeepClone(), null, origin, made))
            .Add(new EditEntry(2, clock.GetUtcNow(), owner, EditKind.Acquire, null, null, Acquired(source), null, origin, made));
        var state = new WorkspaceState(id, owner, app, (JsonObject)context.DeepClone(), source,
            ImmutableDictionary<EditTarget, Override>.Empty, ImmutableDictionary<EditTarget, Conflict>.Empty,
            ImmutableDictionary<EditTarget, LostTarget>.Empty, log);
        if (with is { Count: > 0 })
            state = Stamped(state, Rederive(with.Aggregate(state, (current, set) => SetOn(current, owner, set.Target, set.Value, origin))), made);
        lock (gate)
        {
            store.Insert(state);
            workspaces[id] = state;
        }
        return state;
    }

    public WorkspaceState? Find(string workspaceId)
    {
        lock (gate) return Cached(workspaceId);
    }

    public IReadOnlyList<WorkspaceState> OwnedBy(string owner)
    {
        lock (gate) return [.. store.OwnedBy(owner).Select(id => Cached(id) ?? throw new InvalidOperationException($"Workspace {id} is listed but cannot be loaded."))];
    }

    /// <summary>Deletes the workspace. Runs keep their own frozen copies; the store keeps the log that explains them.</summary>
    public void Delete(string workspaceId, string user)
    {
        lock (gate)
        {
            var state = Get(workspaceId);
            if (state.Owner != user) throw new WorkspaceRefused("NotOwner", "Only the workspace's owner deletes it.");
            store.Delete(workspaceId, clock.GetUtcNow());
            workspaces.Remove(workspaceId);
        }
    }

    /// <summary>Overrides a field. The field must exist in the current version and not be held as a conflict.</summary>
    public WorkspaceState Set(string workspaceId, long expectedRevision, string user, EditTarget target, JsonNode? value, RunOrigin? origin = null) =>
        Apply(workspaceId, expectedRevision, state => SetOn(state, user, target, value, origin));

    /// <summary>Removes an override: the field follows its source or its automatic rule again.</summary>
    public WorkspaceState Reset(string workspaceId, long expectedRevision, string user, EditTarget target) =>
        Apply(workspaceId, expectedRevision, state => ResetOn(state, user, target, null));

    /// <summary>
    /// Applies several changes as one command: each is checked against the state the ones before it left, and either
    /// every one is written, each its own log entry, or none is. A module uses it where one gesture is several edits,
    /// such as making a case and setting its change (ADR-0010 §3), so no reading ever sees half of it. The batch is
    /// one command in the log (ADR-0016 §11), named by <paramref name="said"/> where its caller has its words.
    /// </summary>
    public WorkspaceState Batch(string workspaceId, long expectedRevision, string user, IReadOnlyList<WorkspaceChange> changes, RunOrigin? origin = null,
        string? said = null)
    {
        if (changes.Count == 0) throw new WorkspaceRefused("NothingToChange", "A batch holds at least one change.");
        return Apply(workspaceId, expectedRevision, state => After(state, user, changes, origin), said);
    }

    /// <summary>
    /// The state the changes would leave on a state, as a batch folds them, and written nowhere: what a command reads
    /// to check its later changes against its earlier ones before it writes them all (ADR-0016 §3). The module's
    /// derivation is not run here; it runs once, when the batch is written.
    /// </summary>
    public WorkspaceState After(WorkspaceState state, string user, IReadOnlyList<WorkspaceChange> changes, RunOrigin? origin = null) =>
        changes.Aggregate(state, (current, change) => change switch
        {
            WorkspaceChange.Set set => SetOn(current, user, set.Target, set.Value, origin),
            WorkspaceChange.Reset reset => ResetOn(current, user, reset.Target, origin),
            WorkspaceChange.Create create => CreateOn(current, user, create.Scope, create.Entity, create.Fields, origin, null),
            WorkspaceChange.Remove remove => RemoveOn(current, user, remove.Entity, origin, remove.Layer),
            WorkspaceChange.Context context => ReacquireOn(current, user, context.Value, current.Source, origin).Item1,
            _ => throw new InvalidOperationException($"No batch change {change.GetType().Name}."),
        });

    WorkspaceState SetOn(WorkspaceState state, string user, EditTarget target, JsonNode? value, RunOrigin? origin)
    {
        if (state.Conflicts.ContainsKey(target))
            throw new WorkspaceRefused("ConflictUnresolved", "Keep or take the source value before editing this field again.", [target]);
        var source = state.SourceOf(target);
        if (source.Read(target.Entity, target.Field) is null)
            throw new WorkspaceRefused("UnknownTarget", $"{target.Entity} has no field {target.Field} in this version.", [target]);
        var seq = state.Revision + 1;
        var item = new Override(target, value?.DeepClone(), Carry.BaseOf(target, source, RulesOf(state.App)), source.Version, seq);
        return state with
        {
            Overrides = state.Overrides.SetItem(target, item),
            Lost = state.Lost.Remove(target),
            Log = state.Log.Add(Entry(state, seq, user, EditKind.Set, target, value, origin: origin, replaced: Found(state, target))),
        };
    }

    /// <summary>What a set or a reset finds on its target (ADR-0016 §11); nothing where the field follows its source.</summary>
    static Replaced? Found(WorkspaceState state, EditTarget target)
    {
        // An edit a derivation holds back is still the edit: put back, the derivation decides on it again.
        var item = state.Overrides.GetValueOrDefault(target) ?? state.Conflicts.GetValueOrDefault(target)?.Override;
        var lost = state.Lost.GetValueOrDefault(target);
        return item is null && lost is null ? null : new Replaced(item, lost);
    }

    WorkspaceState ResetOn(WorkspaceState state, string user, EditTarget target, RunOrigin? origin)
    {
        if (!state.Overrides.ContainsKey(target) && !state.Lost.ContainsKey(target))
            throw new WorkspaceRefused("NotOverridden", "There is no override on this field.", [target]);
        var seq = state.Revision + 1;
        return state with
        {
            Overrides = state.Overrides.Remove(target),
            Lost = state.Lost.Remove(target),
            Log = state.Log.Add(Entry(state, seq, user, EditKind.Reset, target, null, origin: origin, replaced: Found(state, target))),
        };
    }

    /// <summary>Resolves a conflict by keeping the user's value; its base moves to the new version.</summary>
    public WorkspaceState KeepOverride(string workspaceId, long expectedRevision, string user, EditTarget target) =>
        Apply(workspaceId, expectedRevision, state =>
        {
            var conflict = ConflictOn(state, target);
            var seq = state.Revision + 1;
            var kept = conflict.Override with { Base = conflict.Source, BaseVersion = conflict.SourceVersion, Seq = seq };
            return state with
            {
                Overrides = state.Overrides.SetItem(target, kept),
                Conflicts = state.Conflicts.Remove(target),
                Log = state.Log.Add(Entry(state, seq, user, EditKind.KeepOverride, target, kept.Value)),
            };
        });

    /// <summary>Resolves a conflict by dropping the user's value for the new source value.</summary>
    public WorkspaceState TakeSource(string workspaceId, long expectedRevision, string user, EditTarget target) =>
        Apply(workspaceId, expectedRevision, state =>
        {
            ConflictOn(state, target);
            var seq = state.Revision + 1;
            return state with
            {
                Conflicts = state.Conflicts.Remove(target),
                Log = state.Log.Add(Entry(state, seq, user, EditKind.TakeSource, target, null)),
            };
        });

    /// <summary>Records that the user has seen an override that lost its target. It stays out of pricing.</summary>
    public WorkspaceState AcknowledgeLost(string workspaceId, long expectedRevision, string user, EditTarget target) =>
        Apply(workspaceId, expectedRevision, state =>
        {
            if (!state.Lost.TryGetValue(target, out var lost))
                throw new WorkspaceRefused("NotLost", "This override has not lost its target.", [target]);
            var seq = state.Revision + 1;
            return state with
            {
                Lost = state.Lost.SetItem(target, lost with { Acknowledged = true }),
                Log = state.Log.Add(Entry(state, seq, user, EditKind.AcknowledgeLost, target, null)),
            };
        });

    /// <summary>
    /// Creates an entity the workspace holds (ADR-0004), with the module's initial field values as its source
    /// values. The module names the entity; one that exists already is refused.
    /// </summary>
    public WorkspaceState Create(string workspaceId, long expectedRevision, string user, string scope, string entity, JsonObject fields,
        RunOrigin? origin = null, IReadOnlyDictionary<string, string>? sources = null) =>
        Apply(workspaceId, expectedRevision, state => CreateOn(state, user, scope, entity, fields, origin, sources));

    WorkspaceState CreateOn(WorkspaceState state, string user, string scope, string entity, JsonObject fields, RunOrigin? origin, IReadOnlyDictionary<string, string>? sources)
    {
        if (state.Created.ContainsKey(entity))
            throw new WorkspaceRefused("EntityExists", $"{entity} exists already.");
        var seq = state.Revision + 1;
        var target = new EditTarget(scope, entity, "*");
        var given = sources is { Count: > 0 } ? sources.ToDictionary() : null;
        // The log says which fields were not typed and where they came from, as the entity does (ADR-0008 §4).
        var note = given is null ? null : "defaults: " + string.Join(", ", given.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key} from {p.Value}"));
        return state with
        {
            Created = state.Created.Add(entity, new CreatedEntity(scope, entity, (JsonObject)fields.DeepClone(), seq, given)),
            Log = state.Log.Add(Entry(state, seq, user, EditKind.Create, target, fields, note, origin)),
        };
    }

    /// <summary>
    /// Removes a created entity. Its own overrides and conflicts end with it, counted in the log entry's note: the
    /// user removed them, so they are not lost targets to acknowledge. What was derived from it is checked when the
    /// derivation runs. Where the entity owns a layer, as a case owns its changes (ADR-0010 Consequences), the caller
    /// names it and every edit in it ends in the same entry.
    /// </summary>
    public WorkspaceState Remove(string workspaceId, long expectedRevision, string user, string entity, RunOrigin? origin = null, string? layer = null) =>
        Apply(workspaceId, expectedRevision, state => RemoveOn(state, user, entity, origin, layer));

    WorkspaceState RemoveOn(WorkspaceState state, string user, string entity, RunOrigin? origin, string? layer)
    {
        if (!state.Created.TryGetValue(entity, out var created))
            throw new WorkspaceRefused("UnknownEntity", $"{entity} was not created in this workspace.");
        var seq = state.Revision + 1;
        var gone = state.Overrides.Keys.Concat(state.Conflicts.Keys).Concat(state.Lost.Keys)
            .Where(t => (t.Entity == entity && !state.Derived.ContainsKey(t.Scope)) || (layer is not null && t.Layer == layer)).Distinct().ToList();
        return state with
        {
            Created = state.Created.Remove(entity),
            Overrides = state.Overrides.RemoveRange(gone),
            Conflicts = state.Conflicts.RemoveRange(gone),
            Lost = state.Lost.RemoveRange(gone),
            Log = state.Log.Add(Entry(state, seq, user, EditKind.Remove, new EditTarget(created.Scope, entity, "*"), null, $"{gone.Count} edits", origin,
                new Replaced(Created: created,
                    Overrides: [.. gone.Select(state.Overrides.GetValueOrDefault).OfType<Override>()],
                    Conflicts: [.. gone.Select(state.Conflicts.GetValueOrDefault).OfType<Conflict>()],
                    LostEdits: [.. gone.Select(state.Lost.GetValueOrDefault).OfType<LostTarget>()],
                    Layer: layer))),
        };
    }

    /// <summary>
    /// Adopts a new context and source version within the same scopes (a value date change, a refresh), and
    /// checks every override against it: carried, conflicted, or lost. <paramref name="said"/> names it in the log where
    /// a command asked for it (<c>refresh</c>), as a batch's does.
    /// </summary>
    public (WorkspaceState State, CarrySummary Summary) Reacquire(string workspaceId, long expectedRevision, string user, JsonObject context, ISourceVersion source,
        RunOrigin? origin = null, string? said = null)
    {
        CarrySummary summary = new(0, 0, 0);
        var state = Apply(workspaceId, expectedRevision, state =>
        {
            (var next, summary) = ReacquireOn(state, user, context, source, origin);
            return next;
        }, said);
        return (state, summary);
    }

    /// <summary>
    /// Leaves a scope (a counterparty change): drops every edit in it. The caller passes the count the user
    /// confirmed; a scope that holds another count by now is refused, so nothing is dropped unseen.
    /// </summary>
    public WorkspaceState DropScope(string workspaceId, long expectedRevision, string user, string scope, int confirmedCount) =>
        Apply(workspaceId, expectedRevision, state => DropOn(state, user, scope, confirmedCount));

    /// <summary>
    /// Moves to another scope in one step (ADR-0003 §6): drops the confirmed edits of the scope being left,
    /// then re-acquires for the new context, checking what remains (a new trade's own edits) as any refresh
    /// does. Either both happen or neither.
    /// </summary>
    public (WorkspaceState State, CarrySummary Summary) ChangeScope(string workspaceId, long expectedRevision, string user,
        string leaving, int confirmedCount, JsonObject context, ISourceVersion source)
    {
        CarrySummary summary = new(0, 0, 0);
        var state = Apply(workspaceId, expectedRevision, state =>
        {
            (var next, summary) = ReacquireOn(DropOn(state, user, leaving, confirmedCount), user, context, source);
            return next;
        });
        return (state, summary);
    }

    (WorkspaceState, CarrySummary) ReacquireOn(WorkspaceState state, string user, JsonObject context, ISourceVersion source, RunOrigin? origin = null)
    {
        // Only what the acquired source holds is checked against the new one; created and derived entities are not
        // in it (ADR-0004), and a derivation that reads the new context runs after the command.
        var (carried, summary) = CarryWhere(state, state.IsAcquired, source);
        var seq = state.Revision + 1;
        var next = carried with
        {
            Context = (JsonObject)context.DeepClone(),
            Source = source,
            Log = state.Log
                .Add(Entry(state, seq, user, EditKind.Context, null, context, origin: origin,
                    replaced: new Replaced(Context: (JsonObject)state.Context.DeepClone(), SourceVersion: state.Source.Version)))
                .Add(Entry(state, seq + 1, user, EditKind.Acquire, null, Acquired(source), origin: origin)),
        };
        return (next, summary);
    }

    /// <summary>Checks the overrides, conflicts and lost edits the filter selects against a source; the rest stay as they are.</summary>
    (WorkspaceState, CarrySummary) CarryWhere(WorkspaceState state, Func<EditTarget, bool> selected, ISourceVersion source)
    {
        var rules = RulesOf(state.App);
        var all = state.Overrides.Values
            .Concat(state.Conflicts.Values.Select(c => c.Override))
            .Concat(state.Lost.Values.Select(l => l.Override))
            .Where(o => selected(o.Target))
            .ToList();
        var overrides = state.Overrides.ToBuilder();
        var conflicts = state.Conflicts.ToBuilder();
        var lost = state.Lost.ToBuilder();
        var (c1, c2, c3) = (0, 0, 0);
        foreach (var item in all)
        {
            overrides.Remove(item.Target);
            conflicts.Remove(item.Target);
            lost.Remove(item.Target);
            switch (Carry.Check(item, source, rules))
            {
                case CarryOutcome.Carried c: overrides[item.Target] = c.Override; c1++; break;
                case CarryOutcome.Conflicted c: conflicts[item.Target] = c.Conflict; c2++; break;
                case CarryOutcome.Lost l: lost[item.Target] = l.Target; c3++; break;
            }
        }
        return (state with { Overrides = overrides.ToImmutable(), Conflicts = conflicts.ToImmutable(), Lost = lost.ToImmutable() }, new(c1, c2, c3));
    }

    /// <summary>
    /// Runs the module's derivation (ADR-0004) and checks the edits of every derivation scope whose version changed
    /// against its new source; a scope that is gone leaves its edits without a target.
    /// </summary>
    WorkspaceState Rederive(WorkspaceState state)
    {
        var derived = RulesOf(state.App).Derive(state).ToImmutableDictionary();
        if (derived.Count == 0 && state.Derived.Count == 0) return state with { Rederived = null };
        var next = state with { Derived = derived, Rederived = null };
        CarrySummary? total = null;
        foreach (var scope in state.Derived.Keys.Union(derived.Keys).Order(StringComparer.Ordinal))
        {
            var before = state.Derived.GetValueOrDefault(scope);
            var after = derived.GetValueOrDefault(scope);
            if (before is not null && after is not null && before.Version == after.Version) continue;
            var source = after ?? new EmptySource($"gone:{state.Revision}");
            (next, var summary) = CarryWhere(next, t => t.Scope == scope, source);
            total = new((total?.Carried ?? 0) + summary.Carried, (total?.Conflicted ?? 0) + summary.Conflicted, (total?.Lost ?? 0) + summary.Lost);
            // The log says why edits in the scope became conflicts or lost: the scope moved to this version.
            next = next with
            {
                Log = next.Log.Add(new EditEntry(next.Revision + 1, clock.GetUtcNow(), "derivation", EditKind.Derive, null, null,
                    new JsonObject { ["scope"] = scope, ["version"] = source.Version },
                    $"{summary.Carried} carried, {summary.Conflicted} conflicted, {summary.Lost} lost")),
            };
        }
        return next with { Rederived = total };
    }

    WorkspaceState DropOn(WorkspaceState state, string user, string scope, int confirmedCount)
    {
        // Entities the user created in the scope go with it, and count as edits the user confirmed (ADR-0004).
        var dropped = state.InScope(scope);
        var created = state.Created.Values.Where(c => c.Scope == scope).Select(c => c.Entity).ToList();
        if (dropped.Count + created.Count != confirmedCount)
            throw new WorkspaceRefused("ConfirmationStale", $"The scope holds {dropped.Count + created.Count} edits; {confirmedCount} were confirmed.");
        var seq = state.Revision + 1;
        return state with
        {
            Created = state.Created.RemoveRange(created),
            Overrides = state.Overrides.RemoveRange(dropped.Keys),
            Conflicts = state.Conflicts.RemoveRange(dropped.Keys),
            Lost = state.Lost.RemoveRange(dropped.Keys),
            Log = state.Log.Add(Entry(state, seq, user, EditKind.DropScope, null, JsonValue.Create(scope), $"{dropped.Count + created.Count} edits")),
        };
    }

    /// <summary>
    /// Freezes the revision for Price. Refused while an entity it would price has an unresolved conflict or an
    /// unacknowledged lost target; entities not priced do not block it.
    /// </summary>
    public WorkspaceFreeze Freeze(string workspaceId, long expectedRevision, Func<string, bool> priced) =>
        Freeze(workspaceId, expectedRevision, _ => priced);

    /// <summary>As <see cref="Freeze(string, long, Func{string, bool})"/>, with the priced entities decided from the state being frozen.</summary>
    public WorkspaceFreeze Freeze(string workspaceId, long expectedRevision, Func<WorkspaceState, Func<string, bool>> pricedIn)
    {
        lock (gate)
        {
            var state = Get(workspaceId);
            if (state.Revision != expectedRevision)
                throw new WorkspaceRefused("RevisionMoved", $"The workspace is at revision {state.Revision}, not {expectedRevision}.");
            var priced = pricedIn(state);
            var blocking = state.Conflicts.Keys
                .Concat(state.Lost.Where(l => !l.Value.Acknowledged).Select(l => l.Key))
                .Where(t => priced(t.Entity))
                .ToList();
            if (blocking.Count > 0)
                throw new WorkspaceRefused("UnresolvedEdits", "Resolve the conflicts and lost edits on the trades being priced.", blocking);
            return new WorkspaceFreeze(state.WorkspaceId, state.Revision, (JsonObject)state.Context.DeepClone(), state.Source.Version,
                state.Overrides.Values.OrderBy(o => o.Seq).ToList(), state);
        }
    }

    WorkspaceState Apply(string workspaceId, long expectedRevision, Func<WorkspaceState, WorkspaceState> change, string? said = null) =>
        Apply(workspaceId, expectedRevision, state => (change(state), new CommandStamp(CommandStamp.IdAt(state.Revision + 1), said)));

    WorkspaceState Apply(string workspaceId, long expectedRevision, Func<WorkspaceState, (WorkspaceState State, CommandStamp Stamp)> change)
    {
        WorkspaceState next;
        lock (gate)
        {
            var state = Get(workspaceId);
            if (state.Revision != expectedRevision)
                throw new WorkspaceRefused("RevisionMoved", $"The workspace is at revision {state.Revision}, not {expectedRevision}.");
            var (changed, stamp) = change(state);
            next = Stamped(state, Rederive(changed), stamp);
            store.Commit(state, next, clock.GetUtcNow());
            workspaces[workspaceId] = next;
        }
        // Outside the lock (ADR-0016 note 35): an observer's work holds up no other workspace's command.
        foreach (var observer in observers[next.App]) observer.Accepted(next);
        return next;
    }

    /// <summary>
    /// Takes back the workspace's latest command not yet taken back (ADR-0016 §11): one all-or-none command of its own
    /// that puts back what each of that command's entries replaced, as it stood. Refused by name where the latest
    /// command took another source version, left a scope or decided a held edit, where it is another user's, and
    /// where <paramref name="command"/> names a command that is not the latest; it never reaches past one to an older.
    /// </summary>
    public (WorkspaceState State, TakenBack TookBack) Undo(string workspaceId, long expectedRevision, string user, string? command = null, RunOrigin? origin = null) =>
        TakeBack(workspaceId, expectedRevision, user, command, origin, undo: true);

    /// <summary>Puts back what the latest undo took back, while no other command has been run since (ADR-0016 §11).</summary>
    public (WorkspaceState State, TakenBack PutBack) Redo(string workspaceId, long expectedRevision, string user, string? command = null, RunOrigin? origin = null) =>
        TakeBack(workspaceId, expectedRevision, user, command, origin, undo: false);

    (WorkspaceState, TakenBack) TakeBack(string workspaceId, long expectedRevision, string user, string? named, RunOrigin? origin, bool undo)
    {
        TakenBack? tookBack = null;
        var next = Apply(workspaceId, expectedRevision, state =>
        {
            var stacks = CommandHistory.Read(state.Log);
            var latest = Latest(state, undo ? stacks.Undo : stacks.Redo, user, named, undo);
            var told = tookBack = Told(state, latest);
            var reverted = latest.Entries.Reverse().Aggregate(state, (current, entry) => Revert(current, user, entry, origin));
            var id = CommandStamp.IdAt(state.Revision + 1);
            return (reverted, undo ? new CommandStamp(id, told.Said, Undoes: told.Command) : new CommandStamp(id, told.Said, Redoes: told.Command));
        });
        return (next, tookBack ?? throw new InvalidOperationException("A command taken back says which."));
    }

    /// <summary>
    /// The command an undo or a redo by this user would act on as the workspace stands (ADR-0016 §5), or the refusal it
    /// would meet. Read from the log, and nothing written.
    /// </summary>
    public TakenBack WouldTakeBack(WorkspaceState state, string user, bool undo)
    {
        var stacks = CommandHistory.Read(state.Log);
        return Told(state, Latest(state, undo ? stacks.Undo : stacks.Redo, user, null, undo));
    }

    TakenBack Told(WorkspaceState state, LoggedCommand latest)
    {
        var stamp = latest.Stamp ?? throw new InvalidOperationException("A command that is taken back has an id.");
        return new(stamp.Id, stamp.Said ?? Named(state, latest), latest.First.User, latest.First.Origin, latest.First.At, latest.Edits.Count());
    }

    /// <summary>The command an undo or a redo acts on, or why there is none it may act on.</summary>
    LoggedCommand Latest(WorkspaceState state, IReadOnlyList<LoggedCommand> stack, string user, string? named, bool undo)
    {
        if (!undo && stack.Count == 0)
            throw new WorkspaceRefused("NothingToRedo", "There is nothing to put back: no command was taken back since the last one ran.");
        if (stack.Count == 0 || stack[^1].Entries[0].Seq == 1)
            throw new WorkspaceRefused("NothingToUndo", "There is nothing to take back: the workspace is as it was made.");
        var latest = stack[^1];
        if (latest.Stamp is null)
            throw new WorkspaceRefused("CannotUndo", "The latest change was recorded before commands could be taken back, and nothing before it is reached.");
        if (named is not null && named != latest.Stamp.Id)
            throw new WorkspaceRefused("NotLatestCommand", $"{named} is not the latest command; {latest.Stamp.Id} is ({latest.Stamp.Said ?? Named(state, latest)}).");
        if (latest.First.User != user)
            throw new WorkspaceRefused("NotYourCommand", $"The latest command is {latest.First.User}'s ({latest.Stamp.Said ?? Named(state, latest)}); only they take it back.");
        if (latest.Barrier is { } why)
            throw new WorkspaceRefused("CannotUndo", $"The latest command {why}, so it cannot be taken back, and nothing before it is reached.");
        return latest;
    }

    /// <summary>A command with no words is named by its edits' titles, as the module's rules give them (note 3).</summary>
    string Named(WorkspaceState state, LoggedCommand command)
    {
        var rules = RulesOf(state.App);
        var edits = command.Edits.Select(e => e.Kind switch
        {
            EditKind.Set => $"set {rules.TitleOf(state, e.Target!)}",
            EditKind.Reset => $"reset {rules.TitleOf(state, e.Target!)}",
            EditKind.Create => $"made {rules.TitleOf(state, e.Target!)}",
            EditKind.Remove => $"removed {rules.TitleOf(state, e.Target!)}",
            EditKind.KeepOverride => $"kept {rules.TitleOf(state, e.Target!)}",
            EditKind.TakeSource => $"took the source's {rules.TitleOf(state, e.Target!)}",
            EditKind.AcknowledgeLost => $"saw the lost edit of {rules.TitleOf(state, e.Target!)}",
            EditKind.DropScope => "left a scope with its edits",
            EditKind.Context => "changed the context",
            _ => throw new InvalidOperationException($"No name for an entry of kind {e.Kind}."),
        }).ToList();
        const int shown = 3;
        return edits.Count <= shown ? string.Join(", ", edits) : $"{string.Join(", ", edits.Take(shown))} and {edits.Count - shown} more";
    }

    /// <summary>
    /// Writes the inverse of one entry: what it replaced is put back as it stood, and the entry that says so records
    /// what it replaced in turn, so the same act takes an undo back (ADR-0016 §11's note 1).
    /// </summary>
    WorkspaceState Revert(WorkspaceState state, string user, EditEntry entry, RunOrigin? origin)
    {
        var seq = state.Revision + 1;
        switch (entry.Kind)
        {
            case EditKind.Set or EditKind.Reset:
            {
                var target = entry.Target ?? throw new InvalidOperationException($"Entry {entry.Seq} has no target.");
                var back = entry.Replaced;
                return state with
                {
                    Overrides = back?.Override is { } item ? state.Overrides.SetItem(target, item) : state.Overrides.Remove(target),
                    // A derivation run by the command may have held its edit back; the derivation run after this decides again.
                    Conflicts = state.Conflicts.Remove(target),
                    Lost = back?.Lost is { } lost ? state.Lost.SetItem(target, lost) : state.Lost.Remove(target),
                    Log = state.Log.Add(Entry(state, seq, user, back?.Override is null ? EditKind.Reset : EditKind.Set, target, back?.Override?.Value,
                        origin: origin, replaced: Found(state, target))),
                };
            }
            case EditKind.Create:
                // An entity an undo put back goes again with the layer that came back with it.
                return RemoveOn(state, user, (entry.Target ?? throw new InvalidOperationException($"Entry {entry.Seq} has no target.")).Entity, origin, entry.Replaced?.Layer);
            case EditKind.Remove:
            {
                var back = entry.Replaced ?? throw new InvalidOperationException($"Entry {entry.Seq} removed an entity and does not say which.");
                var created = back.Created ?? throw new InvalidOperationException($"Entry {entry.Seq} removed an entity and does not say which.");
                if (state.Created.ContainsKey(created.Entity)) throw new WorkspaceRefused("EntityExists", $"{created.Entity} exists already.");
                var edits = (back.Overrides?.Count ?? 0) + (back.Conflicts?.Count ?? 0) + (back.LostEdits?.Count ?? 0);
                return state with
                {
                    Created = state.Created.Add(created.Entity, created),
                    Overrides = state.Overrides.SetItems((back.Overrides ?? []).Select(o => KeyValuePair.Create(o.Target, o))),
                    Conflicts = state.Conflicts.SetItems((back.Conflicts ?? []).Select(c => KeyValuePair.Create(c.Override.Target, c))),
                    Lost = state.Lost.SetItems((back.LostEdits ?? []).Select(l => KeyValuePair.Create(l.Override.Target, l))),
                    Log = state.Log.Add(Entry(state, seq, user, EditKind.Create, new EditTarget(created.Scope, created.Entity, "*"), created.Fields,
                        $"put back with {edits} edits", origin, back.Layer is null ? null : new Replaced(Layer: back.Layer))),
                };
            }
            case EditKind.Context:
            {
                var before = entry.Replaced?.Context ?? throw new InvalidOperationException($"Entry {entry.Seq} changed the context and does not say from what.");
                return ReacquireOn(state, user, before, state.Source, origin).Item1;
            }
            // The source came with the context, and the derivation runs again after the command, as after any.
            case EditKind.Acquire or EditKind.Derive:
                return state;
            default:
                throw new InvalidOperationException($"No inverse of {entry.Kind}; such a command is refused before it is taken back.");
        }
    }

    /// <summary>One accepted call is one command (ADR-0016 §11): every entry it wrote, the derivation's included, carries its stamp.</summary>
    static WorkspaceState Stamped(WorkspaceState before, WorkspaceState after, CommandStamp stamp)
    {
        var log = after.Log.ToBuilder();
        for (var i = before.Log.Count; i < log.Count; i++) log[i] = log[i] with { Command = stamp };
        return after with { Log = log.ToImmutable() };
    }

    WorkspaceState? Cached(string workspaceId)
    {
        if (workspaces.TryGetValue(workspaceId, out var state)) return state;
        if (store.Load(workspaceId) is not { } stored) return null;
        // Derived sources are a function of the stored state, so deriving again restores them as they were.
        var loaded = stored with { Derived = RulesOf(stored.App).Derive(stored).ToImmutableDictionary() };
        workspaces[workspaceId] = loaded;
        return loaded;
    }

    WorkspaceState Get(string workspaceId) =>
        Cached(workspaceId) ?? throw new WorkspaceRefused("UnknownWorkspace", $"No workspace {workspaceId}.");

    static JsonObject Acquired(ISourceVersion source) =>
        new() { ["version"] = source.Version, ["descriptor"] = source.Descriptor.DeepClone() };

    IEditRules RulesOf(string app) =>
        rules.GetValueOrDefault(app) ?? throw new InvalidOperationException($"No module registered edit rules for {app}.");

    static Conflict ConflictOn(WorkspaceState state, EditTarget target) =>
        state.Conflicts.GetValueOrDefault(target) ?? throw new WorkspaceRefused("NoConflict", "This field has no conflict.", [target]);

    EditEntry Entry(WorkspaceState state, long seq, string user, EditKind kind, EditTarget? target, JsonNode? value, string? note = null,
        RunOrigin? origin = null, Replaced? replaced = null) =>
        new(seq, clock.GetUtcNow(), user, kind, target is null ? null : RulesOf(state.App).KindOf(target), target, value?.DeepClone(), note, origin,
            Replaced: replaced);
}
