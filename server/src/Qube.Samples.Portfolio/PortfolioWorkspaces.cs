using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Qube.Composition.Application;
using Qube.Composition.Application.Saved;
using Qube.Composition.Application.Workspaces;

namespace Qube.Samples.Portfolio;

/// <summary>What <c>scenario save</c> says back: the set as saved, how many scenarios it holds, and whether it replaced the user's own.</summary>
public sealed record SetSaved(string Set, string Owner, SavedVisibility Visibility, string? Desk, int Scenarios, bool Replaced);

/// <summary>A scenario set the user can see, and the name <c>scenario load</c> takes for it.</summary>
public sealed record ScenarioSetView(string Label, string Name, string Owner, SavedVisibility Visibility, string? Desk, DateTimeOffset SavedAt, int Scenarios);

/// <summary>A sort the grid asked for: a column of <see cref="EffectiveTrade"/> and its direction.</summary>
public sealed record RowSort(string Column, string Direction);

/// <summary>One page of effective rows, in the shape ExGrid's fetching source takes (ADR-0003 §4).</summary>
public sealed record RowPage(IReadOnlyList<EffectiveTrade> Rows, int Start, int TotalCount, string OrderToken, long Revision);

/// <summary>What the workspace panel shows beside the grid. <c>Overrides</c> counts the edits to the portfolio's data;
/// the selection of measures is the page's choice, shown on its own. <c>TradeColumns</c> are the selected modules'
/// (ADR-0010 §10), as are the shared tabs' <c>Inputs</c> they need, and <c>PendingCells</c> counts their cells on the new trades still waiting for a result, so the page
/// asks again while any is. <c>DisplayCurrency</c> is the workspace's setting, and <c>Simulated</c> names the modules whose
/// figures come from a simulated provider, which the page says (ADR-0010 §1).</summary>
public sealed record PortfolioWorkspaceView(
    string WorkspaceId,
    long Revision,
    JsonObject Context,
    string SourceVersion,
    Registration Registration,
    Agreement Agreement,
    IReadOnlyList<EffectiveCsa> Csas,
    EffectiveCredit Credit,
    int Trades,
    int NewTrades,
    int InBefore,
    int InAfter,
    int Overrides,
    IReadOnlyList<HeldEdit> Conflicts,
    IReadOnlyList<HeldEdit> Lost,
    IReadOnlyList<RunLink> Runs,
    IReadOnlyList<RegisteredMeasure> Measures,
    IReadOnlyList<string> SelectedMeasures,
    IReadOnlyList<RegisteredColumn> TradeColumns,
    IReadOnlyList<MeasureInput> Inputs,
    int PendingCells,
    string DisplayCurrency,
    IReadOnlyList<string> Currencies,
    IReadOnlyList<string> Simulated,
    IReadOnlyList<CaseView> Cases,
    int CaseCap,
    int Changes,
    IReadOnlyList<ScenarioView> Scenarios,
    IReadOnlyList<ShiftRow> Shifts,
    IReadOnlyList<ModuleSettings> Settings);

/// <summary>
/// One change as the Changes tab lists it (ADR-0010 §3's note of 2026-11-08): its entity and field, the case it is a
/// change of (none for a Common change), its title as a cell is named (<c>CSA-A threshold</c>), its value and Original's,
/// each also as written, whether it is <c>set</c>, held as a <c>conflict</c> or <c>lost</c>, and whether its value is
/// typed there, which a change of a cell is and one of a whole grid's B or A is not.
/// </summary>
public sealed record ChangeItem(string Entity, string Field, string? Case, string Title, JsonNode? Value, string Text, JsonNode? Original, string OriginalText,
    string State, bool Editable);

/// <summary>A case with its own changes, as the Changes tab lists it.</summary>
public sealed record CaseChanges(CaseView Case, IReadOnlyList<ChangeItem> Changes);

/// <summary>Every change of the workspace: the Common ones, then each case's, and how many there are in all.</summary>
public sealed record ChangesList(long Revision, IReadOnlyList<ChangeItem> Common, IReadOnlyList<CaseChanges> Cases, int Count);

/// <summary>An edit held back for the user: a conflict (with the source's new values) or a lost target. <c>Case</c> names
/// the case it is a change of, none for a Common edit (ADR-0010 §3).</summary>
public sealed record HeldEdit(string Scope, string Entity, string Field, JsonNode? Value, IReadOnlyDictionary<string, JsonNode?>? Source, bool Acknowledged,
    string? Case = null);

/// <summary>The portfolio's configuration: how many cases one Price prices, the Base among them (ADR-0010 §3's note).</summary>
public sealed record PortfolioOptions(int CaseCap = PortfolioCases.DefaultCap);

/// <summary>A Run this workspace submitted (its latest twenty, newest first).</summary>
public sealed record RunLink(string RunId, long Revision);

/// <summary>How many edits a counterparty change would drop, by module edit kind, for the user to confirm.</summary>
public sealed record ScopeDrop(string Scope, int Count, IReadOnlyDictionary<string, int> ByKind);

/// <summary>
/// The portfolio workspace application service (ADR-0010 §10), shared by the measures that price it: what the
/// endpoints call. It decides scopes and sources; the generic
/// kernel stores and checks the edits. The user is passed in; who may act is decided before this is called.
/// </summary>
public sealed class PortfolioWorkspaces(WorkspaceService workspaces, IDataStore store, RunService runs, MeasureRegister register, PortfolioOptions? options = null,
    SavedItems? saved = null)
{
    readonly int caseCap = (options ?? new PortfolioOptions()).CaseCap;
    readonly PortfolioChecks checks = new(register);

    /// <summary>A new workspace, on its owner's last selection of measures (ADR-0010 §10, DEFAULT).</summary>
    public WorkspaceState Create(string user, string registration, DateOnly valueDate, int existingCount)
    {
        var source = new PortfolioSourceVersion(registration, valueDate, existingCount);
        // The selection its owner set last is part of the workspace's making, so `undo` never takes it back (ADR-0016 §11, note 2).
        IReadOnlyList<WorkspaceChange.Set>? last = LastSelection(user) is { } selection ? [new(PortfolioEditRules.Target(source, Entity.Measures, Entity.MeasuresField), Chosen(selection))] : null;
        return workspaces.Create(user, PortfolioEditRules.App, Context(registration, valueDate, existingCount), source, with: last);
    }

    JsonArray Chosen(IReadOnlyList<string> measures) => new([.. register.Checked(measures).Select(id => JsonValue.Create(id))]);

    /// <summary>Sets the measures Price runs (`measures cva fva`). An unknown one, or none, is refused by name.</summary>
    public WorkspaceState SetMeasures(string workspaceId, long revision, string user, IReadOnlyList<string> measures)
    {
        var state = Owned(workspaceId, user);
        return workspaces.Set(workspaceId, revision, user, PortfolioEditRules.Target(Source(state), Entity.Measures, Entity.MeasuresField), Chosen(measures));
    }

    // The selection the owner set last, in any of their workspaces; none while they have only taken the default, or have
    // taken their last choice back to it: a reset is what `undo` writes where the choice replaced none (ADR-0016 §11).
    IReadOnlyList<string>? LastSelection(string user) =>
        workspaces.OwnedBy(user).Where(w => w.App == PortfolioEditRules.App)
            .SelectMany(w => w.Log.Where(e => e.Kind is EditKind.Set or EditKind.Reset && e.Target?.Entity == Entity.Measures).Select(e => (e.At, e.Seq, e.Value)))
            .OrderBy(e => e.At).ThenBy(e => e.Seq).LastOrDefault() is { Value: JsonArray chosen }
            ? [.. register.Ids.Where(id => chosen.Any(c => c!.GetValue<string>() == id))]
            : null;

    public PortfolioWorkspaceView View(string workspaceId, string user)
    {
        var raw = Owned(workspaceId, user);
        // The workspace shows the Base; the cases' changes are the Changes tab's (ADR-0010 §3's note of 2026-11-08).
        var state = PortfolioCases.Base(raw);
        var source = Source(state);
        var links = runs.List(new RunQuery(user, 0, 20, workspaceId)).Runs
            .Select(r => new RunLink(r.RunId, r.Relations.WorkspaceRevision ?? throw new InvalidOperationException($"Run {r.RunId} names a workspace but no revision.")))
            .ToList();
        return new(state.WorkspaceId, state.Revision, state.Context, source.Version, source.Registration, source.Agreement,
            register.WithMembership(state, PortfolioMaterialiser.Csas(state)), PortfolioMaterialiser.Credit(state), Rows(state).Count, Rows(state).Count(t => t.IsNew), Rows(state).Count(t => t.Before), Rows(state).Count(t => t.After), state.Overrides.Keys.Count(t => t.Scope != PortfolioEditRules.WorkspaceScope),
            // Every edit held back is the user's to resolve, a case's among them.
            [.. raw.Conflicts.Values.OrderBy(c => c.Override.Seq).Select(c => new HeldEdit(c.Override.Target.Scope, c.Override.Target.Entity, c.Override.Target.Field, c.Override.Value, c.Source, false, CaseIdOf(c.Override.Target)))],
            [.. raw.Lost.Values.OrderBy(l => l.Override.Seq).Select(l => new HeldEdit(l.Override.Target.Scope, l.Override.Target.Entity, l.Override.Target.Field, l.Override.Value, null, l.Acknowledged, CaseIdOf(l.Override.Target)))],
            links, register.Measures, PortfolioMaterialiser.SelectedMeasures(state, register),
            [.. register.TradeColumns.Where(c => register.Selected(state).Any(m => m.Id == c.Module))], register.Inputs(state),
            register.WithCells(state, [.. Rows(state).Where(t => t.IsNew)]).Sum(t => t.Cells?.Values.Count(c => c.State == TradeCell.Pending) ?? 0),
            PortfolioMaterialiser.DisplayCurrency(state), PortfolioMarket.Currencies, [.. register.Selected(state).Where(m => m.Simulated).Select(m => m.Id)],
            PortfolioCases.All(raw), caseCap, DataEdits(raw).Count(), PortfolioScenarios.All(raw), PortfolioScenarios.Rows(raw), register.Settings(state));
    }

    /// <summary>
    /// <c>scenario &lt;name&gt; &lt;shift&gt;; …</c> (ADR-0010 §4 and its note of 2026-11-09): the scenario whole, its shifts read as
    /// written, in one log entry per row; one of that name is replaced, and stays included or excluded as it was.
    /// </summary>
    public ScenarioAnswer DefineScenario(string workspaceId, long revision, string user, string name, string shifts, RunOrigin? origin = null)
    {
        var raw = Owned(workspaceId, user);
        var read = ShiftReader.ReadAll(shifts);
        var named = PortfolioScenarios.Checked(name);
        var (changes, replaced) = PortfolioScenarios.Define(raw, named, read, revision);
        return new ScenarioAnswer(workspaces.Batch(workspaceId, revision, user, changes, origin), [named], replaced ? [named] : []);
    }

    /// <summary>
    /// <c>scenario ladder …</c> and <c>scenario cross …</c> (ADR-0010 §4): a set of scenarios at once, each named from its
    /// shifts and replacing one of its name, in one batch; the answer names those it made and those it replaced.
    /// </summary>
    public ScenarioAnswer GenerateScenarios(string workspaceId, long revision, string user, string generator, string text, RunOrigin? origin = null)
    {
        var made = generator switch
        {
            "ladder" => ScenarioGenerators.Ladder(text, caseCap),
            "cross" => ScenarioGenerators.Cross(text, caseCap),
            _ => throw new WorkspaceRefused("InvalidGenerator", $"There is no generator {generator}; there are ladder and cross."),
        };
        IReadOnlyList<string> replaced = [];
        var state = Apply(workspaceId, revision, user, raw =>
        {
            var (changes, gone) = PortfolioScenarios.DefineAll(raw, made, revision);
            replaced = gone;
            return changes;
        }, origin);
        return new ScenarioAnswer(state, [.. made.Select(m => m.Name)], replaced);
    }

    /// <summary>
    /// The Scenarios grid's edits as one batch (ADR-0010 §4): a cell typed, a block pasted onto it, or cells cleared, each
    /// read as its column reads it; one cell that does not resolve refuses the edit whole, naming its row and column.
    /// </summary>
    public WorkspaceState EditShifts(string workspaceId, long revision, string user, IReadOnlyList<ShiftEdit> edits, RunOrigin? origin = null) =>
        Apply(workspaceId, revision, user, raw => PortfolioScenarios.Edit(raw, edits, revision), origin);

    /// <summary><c>scenario include|exclude &lt;name&gt;</c>: whether Price prices it, as its box says; its rows stay.</summary>
    public WorkspaceState IncludeScenario(string workspaceId, long revision, string user, string name, bool included, RunOrigin? origin = null) =>
        Apply(workspaceId, revision, user, raw => PortfolioScenarios.Include(raw, name, included), origin);

    /// <summary><c>scenario remove &lt;name&gt;</c>: its rows, in one batch.</summary>
    public WorkspaceState RemoveScenario(string workspaceId, long revision, string user, string name, RunOrigin? origin = null) =>
        Apply(workspaceId, revision, user, raw => PortfolioScenarios.Remove(raw, name), origin);

    /// <summary><c>scenarios clear</c>: every shift row.</summary>
    public WorkspaceState ClearScenarios(string workspaceId, long revision, string user, RunOrigin? origin = null) =>
        Apply(workspaceId, revision, user, PortfolioScenarios.Clear, origin);

    /// <summary><c>scenario duplicate &lt;name&gt; &lt;new name&gt;</c>: the scenario's rows under the new name, to be edited.</summary>
    public ScenarioAnswer DuplicateScenario(string workspaceId, long revision, string user, string name, string copy, RunOrigin? origin = null)
    {
        var state = Apply(workspaceId, revision, user, raw => PortfolioScenarios.Duplicate(raw, name, copy, revision), origin);
        return new ScenarioAnswer(state, [PortfolioScenarios.Checked(copy)], []);
    }

    /// <summary>
    /// <c>scenario save &lt;set&gt; [desk|all]</c> (ADR-0010 §4, C32): every scenario of the workspace, included or not, saved
    /// under the user's name for the set, for them alone unless shared with their desk or with all; saved again, it
    /// replaces their own set of that name. The workspace does not change.
    /// </summary>
    public SetSaved SaveScenarios(string workspaceId, long revision, string user, string set, string? share = null)
    {
        var raw = At(workspaceId, revision, user);
        var count = PortfolioScenarios.All(raw).Count;
        if (count == 0) throw new WorkspaceRefused("NothingToSave", "The workspace has no scenario to save. Define one first, such as scenario up jpy_ois +10bp.");
        var visibility = share?.Trim().ToLowerInvariant() switch
        {
            null or "" => SavedVisibility.Private,
            "desk" => SavedVisibility.Desk,
            "all" => SavedVisibility.All,
            var other => throw new WorkspaceRefused("InvalidShare", $"scenario save {set.Trim()} shares the set with desk or all, not {other}."),
        };
        var (item, replaced) = Saving(s => s.Save(PortfolioScenarios.SetKind, SetNoun, user, set, visibility, PortfolioScenarios.SetOf(raw)));
        return new SetSaved(item.Name, item.Owner, item.Visibility, item.Desk, count, replaced);
    }

    /// <summary>
    /// <c>scenario load &lt;set&gt;</c> (ADR-0010 §4): the set's scenarios added in one batch, replacing those of the same name
    /// and included as they were saved. A set named <c>&lt;set&gt;@&lt;owner&gt;</c> is that owner's; a name two sets the user
    /// can see share is refused, naming their owners.
    /// </summary>
    public ScenarioAnswer LoadScenarios(string workspaceId, long revision, string user, string set, RunOrigin? origin = null)
    {
        IReadOnlyList<string> made = [], replaced = [];
        var state = Apply(workspaceId, revision, user, raw =>
        {
            var item = Saving(s => s.Find(PortfolioScenarios.SetKind, SetNoun, user, set));
            var (changes, loaded, gone) = PortfolioScenarios.Load(raw, item.Content, revision);
            (made, replaced) = (loaded, gone);
            return changes;
        }, origin);
        return new ScenarioAnswer(state, made, replaced);
    }

    /// <summary>The scenario sets the user can see, for <c>scenario load</c> to offer: their own, their desk's and all's.</summary>
    public IReadOnlyList<ScenarioSetView> ScenarioSets(string user)
    {
        var visible = Saving(s => s.Visible(PortfolioScenarios.SetKind, user));
        return [.. visible.Select(i => new ScenarioSetView(SavedItems.Label(i, visible), i.Name, i.Owner, i.Visibility, i.Desk, i.SavedAt,
            i.Content["scenarios"]?.AsArray().Count ?? throw new InvalidOperationException($"The saved set {i.Name} holds no scenarios.")))];
    }

    const string SetNoun = "set";

    // A saved-item rule's refusal is the command's, by code; a server built without the saved items is a defect.
    T Saving<T>(Func<SavedItems, T> act)
    {
        var service = saved ?? throw new InvalidOperationException("The portfolio workspaces were built without saved items, so no set can be saved or loaded.");
        try
        {
            return act(service);
        }
        catch (SavedItemRefused refused)
        {
            throw new WorkspaceRefused(refused.Code, refused.Message);
        }
    }

    // A scenario command's changes as one batch, against the revision named; one that changes nothing leaves the
    // workspace as it stands.
    WorkspaceState Apply(string workspaceId, long revision, string user, Func<WorkspaceState, IReadOnlyList<WorkspaceChange>> change, RunOrigin? origin)
    {
        var raw = At(workspaceId, revision, user);
        var changes = change(raw);
        return changes.Count == 0 ? raw : workspaces.Batch(workspaceId, revision, user, changes, origin);
    }

    // The user's workspace at the revision a command was taken against; one that has moved since is refused (spine 6).
    WorkspaceState At(string workspaceId, long revision, string user)
    {
        var raw = Owned(workspaceId, user);
        return raw.Revision == revision ? raw : throw new WorkspaceRefused("RevisionMoved", $"The workspace is at revision {raw.Revision}, not {revision}.");
    }

    // The edits to the portfolio's data in any layer, as the Changes tab counts them: the selection, the display currency
    // and the scenarios are the page's, shown on their own; a module's settings are inputs and listed (ADR-0010 §8).
    IEnumerable<Override> DataEdits(WorkspaceState raw) =>
        raw.Overrides.Values.Concat(raw.Conflicts.Values.Select(c => c.Override)).Concat(raw.Lost.Values.Select(l => l.Override))
            .Where(o => o.Target.Scope != PortfolioEditRules.WorkspaceScope || register.SettingsOf(o.Target.Entity) is not null);

    /// <summary>
    /// The Changes tab (ADR-0010 §3's note of 2026-11-08): each Common change, then each case with its own, in the order
    /// they were made, with Original's value, the source's under the rules with no edit at all.
    /// </summary>
    public ChangesList Changes(string workspaceId, string user)
    {
        var raw = Owned(workspaceId, user);
        var original = raw with
        {
            Overrides = ImmutableDictionary<EditTarget, Override>.Empty, Conflicts = ImmutableDictionary<EditTarget, Conflict>.Empty,
            Lost = ImmutableDictionary<EditTarget, LostTarget>.Empty,
        };
        // A case of iterative addition is its membership, changed only whole (ADR-0010 §3's note of 2026-11-08).
        var whole = PortfolioCases.Made(raw).Where(c => PortfolioCases.KindOf(c) == PortfolioCases.Add).Select(c => c.Entity).ToHashSet();
        ChangeItem Item(Override o)
        {
            var t = o.Target;
            var state = raw.Conflicts.ContainsKey(t) ? "conflict" : raw.Lost.ContainsKey(t) ? "lost" : "set";
            if (PortfolioCells.Find(raw, register, t.Entity, t.Field) is { } cell)
            {
                var was = PortfolioCells.Current(original, register, cell);
                return new(t.Entity, t.Field, CaseIdOf(t), $"{cell.Row} {cell.Column}", o.Value?.DeepClone(), PortfolioCells.Write(cell, o.Value), was?.DeepClone(), PortfolioCells.Write(cell, was),
                    state, state == "set" && !(t.Layer is { } layer && whole.Contains(layer)));
            }
            // A whole grid's B or A (ADR-0010 §6): every row of the grid at once, over each row's own.
            if (Entity.Split(t.Entity).Kind == Entity.Grid)
                return new(t.Entity, t.Field, CaseIdOf(t), $"{(Entity.Split(t.Entity).Id == Entity.NewGrid ? "New" : "Existing")} {(t.Field == "before" ? "B" : "A")} every row",
                    o.Value?.DeepClone(), o.Value?.GetValue<bool>() == true ? "on" : "off", null, "each row's own", state, false);
            // A row this version no longer has: listed to be removed, never typed here.
            var source = raw.SourceOf(t).Read(t.Entity, t.Field)?.Value;
            return new(t.Entity, t.Field, CaseIdOf(t), $"{t.Entity} {t.Field}", o.Value?.DeepClone(), o.Value?.ToJsonString() ?? "empty", source?.DeepClone(),
                source?.ToJsonString() ?? "empty", state, false);
        }
        var edits = DataEdits(raw).OrderBy(o => o.Seq).ToList();
        var common = edits.Where(o => o.Target.Layer is null).Select(Item).ToList();
        var cases = PortfolioCases.All(raw).Skip(1)
            .Select(c => new CaseChanges(c, [.. edits.Where(o => o.Target.Layer == PortfolioCases.Of(c.Id)).Select(Item)])).ToList();
        return new ChangesList(raw.Revision, common, cases, edits.Count);
    }

    /// <summary>
    /// Sets a change's value as written (the Changes tab): read as its cell reads a value and checked as the
    /// grid checks it typed, on the reading it is a change of, the Base's or a case's, in that case's layer.
    /// </summary>
    public WorkspaceState SetText(string workspaceId, long revision, string user, string entity, string field, string text, string? caseId = null, RunOrigin? origin = null)
    {
        var raw = Owned(workspaceId, user);
        var layer = caseId is null ? null : Changeable(raw, caseId);
        var reading = PortfolioCases.Reading(raw, caseId);
        var cell = PortfolioCells.Of(reading, register, entity, field);
        var value = PortfolioCells.Read(cell, text);
        checks.Check(reading, entity, field, value);
        return workspaces.Set(workspaceId, revision, user, PortfolioEditRules.Target(Source(raw), entity, field, layer), value, origin);
    }

    /// <summary>Removes a case with every change it holds, in one log entry (<c>case remove</c>, ADR-0010 §3). A case is
    /// named by its title or its id, case ignored; one that names none, or several, is refused with the cases there are.</summary>
    public WorkspaceState RemoveCase(string workspaceId, long revision, string user, string name)
    {
        var raw = Owned(workspaceId, user);
        var cases = PortfolioCases.All(raw).Skip(1).ToList();
        var named = cases.Where(c => string.Equals(c.Id, name.Trim(), StringComparison.OrdinalIgnoreCase)).ToList() is { Count: 1 } byId ? byId
            : cases.Where(c => string.Equals(c.Title, name.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        if (named.Count == 0)
            throw new WorkspaceRefused("UnknownCase", cases.Count == 0 ? "The workspace has no case; the Base is the workspace itself and is never removed."
                : $"No case {name.Trim()}; the cases are {string.Join(", ", cases.Select(c => c.Title))}.");
        if (named.Count > 1)
            throw new WorkspaceRefused("AmbiguousCase", $"{name.Trim()} names {named.Count} cases, {string.Join(" and ", named.Select(c => c.Id))}; remove one by its id.");
        var entity = PortfolioCases.Of(named[0].Id);
        return workspaces.Remove(workspaceId, revision, user, entity, layer: entity);
    }

    /// <summary>Removes every case with its changes, as one command (<c>cases clear</c>, ADR-0010 §3).</summary>
    public WorkspaceState ClearCases(string workspaceId, long revision, string user)
    {
        var raw = Owned(workspaceId, user);
        var made = PortfolioCases.Made(raw);
        if (made.Count == 0) throw new WorkspaceRefused("NothingToChange", "The workspace has no case to remove.");
        return workspaces.Batch(workspaceId, revision, user, [.. made.Select(c => new WorkspaceChange.Remove(c.Entity, c.Entity))]);
    }

    // A case whose changes may be changed one by one: a case of iterative addition is its membership, made whole by
    // add trades and removed whole (ADR-0010 §3's note of 2026-11-08), so none of its changes is set or removed alone.
    static string Changeable(WorkspaceState raw, string caseId)
    {
        var layer = CaseOf(raw, caseId);
        var made = raw.Created[layer];
        return PortfolioCases.KindOf(made) != PortfolioCases.Add ? layer
            : throw new WorkspaceRefused("NotEditable",
                $"Case {PortfolioCases.Title(made)} is its trade alone: remove the case whole, or run add trades again to make the set anew.");
    }

    // The case an edit is a change of, as the wire names it: none for a Common edit.
    static string? CaseIdOf(EditTarget target) => target.Layer is { } layer ? Entity.Split(layer).Id : null;

    // The layer of a case the workspace holds, or why there is none.
    static string CaseOf(WorkspaceState raw, string caseId)
    {
        var layer = PortfolioCases.Of(caseId);
        return raw.Created.ContainsKey(layer) ? layer
            : throw new WorkspaceRefused("UnknownCase", $"No case {caseId} in this workspace; the Base's changes are named with no case.");
    }

    /// <summary>Sets the currency the figures are shown in (ADR-0010 §1): the edit of <c>settings:workspace</c>
    /// <c>displayCurrency</c>. One the market cannot convert to is refused.</summary>
    public WorkspaceState SetDisplayCurrency(string workspaceId, long revision, string user, string currency) =>
        Edit(workspaceId, revision, user, Entity.WorkspaceSettings, Entity.DisplayCurrency, currency);

    /// <summary>Refresh (ADR-0010 §1): acquires the same counterparty's trades for the same value date again; edits carry
    /// or are held as for any new version.</summary>
    public (WorkspaceState State, CarrySummary Summary) Refresh(string workspaceId, long revision, string user) =>
        ChangeValueDate(workspaceId, revision, user, Source(Owned(workspaceId, user)).ValueDate);

    /// <summary>The revision a workspace of the user's holds now, for a command that names none.</summary>
    public long Revision(string workspaceId, string user) => Owned(workspaceId, user).Revision;

    public IReadOnlyList<WorkspaceState> OwnedBy(string user) =>
        [.. workspaces.OwnedBy(user).Where(w => w.App == PortfolioEditRules.App)];

    /// <summary>
    /// A page of effective rows under a sort, of one trade grid (<c>new</c> or <c>existing</c>, ADR-0010 §6) or of all.
    /// Refused for an unknown column or grid, never sorted or filtered by a guess.
    /// </summary>
    public RowPage Page(string workspaceId, string user, int start, int count, IReadOnlyList<RowSort> sorts, string? grid = null)
    {
        if (start < 0 || count < 1) throw new WorkspaceRefused("InvalidRange", "A range starts at 0 or later and holds at least one row.");
        if (grid is not (null or Entity.NewGrid or Entity.ExistingGrid))
            throw new WorkspaceRefused("UnknownGrid", $"No trade grid {grid}; the grids are {Entity.NewGrid} and {Entity.ExistingGrid}.");
        var state = PortfolioCases.Base(Owned(workspaceId, user));
        var keys = TradeOrder.Keys(sorts.Select(sort => (sort.Column, sort.Direction switch
        {
            "ascending" => false,
            "descending" => true,
            _ => throw new WorkspaceRefused("InvalidSort", $"Sort direction {sort.Direction} is not ascending or descending."),
        })));
        var all = TradeOrder.Ordered(state, register, grid, keys);
        var token = TradeOrder.Token(state, register, grid, keys);
        // The modules' cells are read for the page alone, each time: a result such as PV01 arrives with no edit.
        var page = start >= all.Count ? [] : register.WithCells(state, [.. all.Skip(start).Take(count)]);
        return new(page, start, all.Count, token, state.Revision);
    }

    public WorkspaceState Edit(string workspaceId, long revision, string user, string entity, string field, JsonNode? value)
    {
        var state = PortfolioCases.Base(Owned(workspaceId, user));
        checks.Check(state, entity, field, value);
        return workspaces.Set(workspaceId, revision, user, PortfolioEditRules.Target(Source(state), entity, field), value);
    }

    /// <summary>
    /// <c>vary &lt;row&gt; &lt;column&gt; &lt;values&gt;</c> (ADR-0010 §3, §9): one case per value, each the Base with that one
    /// change, named from what it changed (<c>CSA-A threshold 5mm</c>). The cell is named as ADR-0010 §9's note names it.
    /// </summary>
    public VaryAnswer Vary(string workspaceId, long revision, string user, string cell, IReadOnlyList<string> values, RunOrigin? origin = null)
    {
        var text = cell.Trim();
        var at = text.IndexOf(' ');
        if (at <= 0)
            throw new WorkspaceRefused("UnknownCell", $"Name a cell as its row and its column, such as CSA-A threshold; {(text.Length == 0 ? "none was named" : $"{text} is one word")}.");
        var raw = Owned(workspaceId, user);
        return Vary(raw, revision, user, PortfolioCells.Resolve(PortfolioCases.Base(raw), register, text[..at], text[(at + 1)..]), values, origin);
    }

    /// <summary>As <see cref="Vary(string, long, string, string, IReadOnlyList{string}, RunOrigin?)"/>, on the cell of an entity's
    /// field: <em>Vary values…</em> on a cell of the input tabs or the trade grids (ADR-0010 §3).</summary>
    public VaryAnswer VaryField(string workspaceId, long revision, string user, string entity, string field, IReadOnlyList<string> values, RunOrigin? origin = null)
    {
        var raw = Owned(workspaceId, user);
        return Vary(raw, revision, user, PortfolioCells.Of(PortfolioCases.Base(raw), register, entity, field), values, origin);
    }

    // Every value is read and checked as the cell would check it typed, before any case is made: a list holding a value
    // that cannot be read makes none. A value the Base holds makes no case, nor one a case already holds; each is said.
    VaryAnswer Vary(WorkspaceState raw, long revision, string user, CellName cell, IReadOnlyList<string> values, RunOrigin? origin)
    {
        var written = values.Select(v => v.Trim()).ToList();
        if (written.Count == 0 || written.Any(v => v.Length == 0))
            throw new WorkspaceRefused("NothingToVary", $"vary takes one value or more for {cell.Row} {cell.Column}, separated by commas{(written.Count == 0 ? "" : ", none of them empty")}.");
        var baseline = PortfolioCases.Base(raw);
        var read = written.Select(v => (Text: v, Value: PortfolioCells.Read(cell, v))).ToList();
        foreach (var (_, value) in read) checks.Check(baseline, cell.Entity, cell.Field, value);
        var held = PortfolioCells.Current(baseline, register, cell);
        // A case another vary made already holds the one change.
        var existing = PortfolioCases.Made(raw).Where(c => PortfolioCases.KindOf(c) == PortfolioCases.Vary)
            .Select(c => (Title: PortfolioCases.Title(c), Changes: PortfolioCases.ChangesOf(raw, Entity.Split(c.Entity).Id)))
            .Where(c => c.Changes.Count == 1)
            .Select(c => (c.Title, Change: c.Changes[0]))
            .ToList();
        var made = new List<(string Title, JsonNode? Value)>();
        var skipped = new List<string>();
        foreach (var (text, value) in read)
        {
            if (PortfolioMaterialiser.Same(value, held)) { skipped.Add($"{text} is the workspace's value"); continue; }
            var same = existing.Where(e => e.Change.Target.Entity == cell.Entity && e.Change.Target.Field == cell.Field && PortfolioMaterialiser.Same(e.Change.Value, value))
                .Select(e => e.Title).Concat(made.Where(m => PortfolioMaterialiser.Same(m.Value, value)).Select(m => m.Title)).FirstOrDefault();
            if (same is not null) { skipped.Add($"{text} is case {same}, made already"); continue; }
            made.Add(($"{cell.Row} {cell.Column} {text}", value));
        }
        if (made.Count == 0) return new VaryAnswer(raw, [], skipped);
        var source = Source(raw);
        // A case is named by the log entry that made it, so no id is ever given twice.
        var changes = made.SelectMany((m, i) =>
        {
            var id = $"c{revision + 1 + 2 * i}";
            var layer = PortfolioCases.Of(id);
            return new WorkspaceChange[]
            {
                new WorkspaceChange.Create($"cp:{source.Registration.Key}", layer, new JsonObject { ["title"] = m.Title, ["kind"] = PortfolioCases.Vary }),
                new WorkspaceChange.Set(PortfolioEditRules.Target(source, cell.Entity, cell.Field, layer), m.Value),
            };
        }).ToList();
        return new VaryAnswer(workspaces.Batch(raw.WorkspaceId, revision, user, changes, origin), [.. made.Select(m => m.Title)], skipped);
    }

    /// <summary>
    /// <c>cds &lt;words&gt;</c>, <c>cds proxy &lt;sector&gt; &lt;region&gt; &lt;rating&gt;</c>, <c>cds none</c> (ADR-0010 §8's note
    /// of 2026-11-09): the counterparty's CDS, read on the server, set in one log entry. Only the keys that change are
    /// written; a key of the other type is kept, so choosing that type again finds it.
    /// </summary>
    public (WorkspaceState State, EffectiveCredit Credit) ChooseCds(string workspaceId, long revision, string user, string text, RunOrigin? origin = null)
    {
        var raw = Owned(workspaceId, user);
        var baseline = PortfolioCases.Base(raw);
        var source = Source(raw);
        var current = PortfolioMaterialiser.Credit(baseline).Choice;
        var chosen = CdsCatalog.Read(text, current);
        var entity = Entity.Of(Entity.Credit, source.Registration.Key);
        var changes = new (string Field, string? Before, string? After)[]
            {
                ("cdsType", current.Type, chosen.Type), ("cdsReference", current.Reference, chosen.Reference), ("proxySector", current.Sector, chosen.Sector),
                ("proxyRegion", current.Region, chosen.Region), ("proxyRating", current.Rating, chosen.Rating),
            }
            .Where(c => c.Before != c.After)
            .Select(c => (WorkspaceChange)new WorkspaceChange.Set(PortfolioEditRules.Target(source, entity, c.Field), c.After is null ? null : JsonValue.Create(c.After)))
            .ToList();
        if (changes.Count == 0) return (raw, PortfolioMaterialiser.Credit(baseline));
        var state = workspaces.Batch(workspaceId, revision, user, changes, origin);
        return (state, PortfolioMaterialiser.Credit(PortfolioCases.Base(state)));
    }

    /// <summary>What <c>cds</c> says back: the CDS the counterparty now has, or why it has none.</summary>
    public static string CdsSaid(Registration registration, EffectiveCredit credit) => credit.Cds is { } cds
        ? $"The CDS of {registration.Key} is {(cds.Type == CdsCatalog.Proxy ? "the proxy" : "the single name")} {cds.Reference}, recovery {cds.Recovery * 100m:0.##}%."
        : credit.Missing ?? throw new InvalidOperationException("A credit with no curve says why.");

    /// <summary>
    /// <c>add trades</c> (docs/03 §5, ADR-0010 §3's note of 2026-11-08): one case per New row with A ticked, at this
    /// moment, each holding that trade alone on top of the existing trades, never the others of the set: in its reading
    /// only, A is off on every other New row of the set. A candidate in Before is refused by name, never unticked, and
    /// nothing is made. A set made before is replaced in the same command; the <c>vary</c> cases stay.
    /// </summary>
    public AddAnswer AddTrades(string workspaceId, long revision, string user, RunOrigin? origin = null)
    {
        var raw = Owned(workspaceId, user);
        var candidates = PortfolioMaterialiser.Trades(PortfolioCases.Base(raw)).Where(t => t.IsNew && t.After).Select(t => (t.TradeId, t.Before)).ToList();
        if (candidates.Count == 0)
            throw new WorkspaceRefused("NothingToAdd", "add trades makes a case per New row with A ticked, and no New row has A ticked.");
        if (candidates.Where(c => c.Before).Select(c => c.TradeId).ToList() is { Count: > 0 } before)
        {
            var (are, them) = before.Count == 1 ? ("is", "it") : ("are", "them");
            throw new WorkspaceRefused("CandidateInBefore",
                $"{string.Join(", ", before)} {are} in Before, and a new trade is priced alone only out of Before. Clear B on {them}, or A to leave {them} out, then add trades again.");
        }
        var replaced = PortfolioCases.Made(raw).Where(c => PortfolioCases.KindOf(c) == PortfolioCases.Add).ToList();
        // The cap counts the Base (ADR-0010 §3's note): a set Price would refuse is not written n² edits deep first.
        var count = PortfolioCases.All(raw).Count - replaced.Count + candidates.Count;
        if (count > caseCap)
            throw new WorkspaceRefused("TooManyCases",
                $"add trades would make {candidates.Count} cases, and Price prices {caseCap} at most, the Base among them: {count - caseCap} over. Clear A on {count - caseCap} new trade(s) first.");
        var source = Source(raw);
        var ids = candidates.Select(c => c.TradeId).ToList();
        var changes = new List<WorkspaceChange>(replaced.Select(c => new WorkspaceChange.Remove(c.Entity, c.Entity)));
        foreach (var trade in ids)
        {
            // A case is named by the log entry that made it, so no id is ever given twice.
            var layer = PortfolioCases.Of($"c{revision + 1 + changes.Count}");
            changes.Add(new WorkspaceChange.Create($"cp:{source.Registration.Key}", layer,
                new JsonObject { ["title"] = $"add {trade}", ["kind"] = PortfolioCases.Add, ["trade"] = trade }));
            changes.AddRange(ids.Where(other => other != trade)
                .Select(other => new WorkspaceChange.Set(PortfolioEditRules.Target(source, Entity.Of(Entity.Trade, other), "after", layer), JsonValue.Create(false))));
        }
        return new AddAnswer(workspaces.Batch(workspaceId, revision, user, changes, origin), [.. ids.Select(t => $"add {t}")],
            [.. replaced.Select(PortfolioCases.Title)]);
    }

    /// <summary>
    /// Asks again for every failed result among the selected modules' cells on the workspace's trades, or on one trade
    /// (`pv01 retry`, and the failed cell's action, ADR-0010 §6). Answers how many were asked again.
    /// </summary>
    public int Retry(string workspaceId, string user, string? tradeId = null)
    {
        var state = PortfolioCases.Base(Owned(workspaceId, user));
        var trades = tradeId is null ? Rows(state) : [.. Rows(state).Where(t => t.TradeId == tradeId)];
        if (tradeId is not null && trades.Count == 0) throw new WorkspaceRefused("UnknownTarget", $"No trade {tradeId} in this workspace.");
        return register.Selected(state).Sum(m => m.Retry(state, trades));
    }

    public WorkspaceState Resolve(string workspaceId, long revision, string user, string entity, string field, string resolution, string? caseId = null)
    {
        var raw = Owned(workspaceId, user);
        var target = Held(raw, entity, field, caseId is null ? null : resolution == "reset" ? Changeable(raw, caseId) : CaseOf(raw, caseId));
        return resolution switch
        {
            "reset" => workspaces.Reset(workspaceId, revision, user, target),
            "keep" => workspaces.KeepOverride(workspaceId, revision, user, target),
            "takeSource" => workspaces.TakeSource(workspaceId, revision, user, target),
            "acknowledge" => workspaces.AcknowledgeLost(workspaceId, revision, user, target),
            _ => throw new WorkspaceRefused("UnknownResolution", $"{resolution} is not reset, keep, takeSource or acknowledge."),
        };
    }

    /// <summary>Moves the value date: a new version of the same counterparty's trades; edits carry or are held.</summary>
    public (WorkspaceState State, CarrySummary Summary) ChangeValueDate(string workspaceId, long revision, string user, DateOnly valueDate)
    {
        var source = Source(Owned(workspaceId, user));
        var count = source.Descriptor["existingCount"]!.GetValue<int>();
        return DataEdits(workspaces.Reacquire(workspaceId, revision, user, Context(source.Registration.Key, valueDate, count),
            new PortfolioSourceVersion(source.Registration.Key, valueDate, count)));
    }

    // The selection of measures and the settings are in the workspace's own scope, so they always carry; the summary
    // counts the edits to the portfolio's data, as the view does.
    static (WorkspaceState State, CarrySummary Summary) DataEdits((WorkspaceState State, CarrySummary Summary) moved) =>
        (moved.State, moved.Summary with { Carried = moved.Summary.Carried - moved.State.Overrides.Keys.Count(t => t.Scope == PortfolioEditRules.WorkspaceScope) });

    /// <summary>What leaving the current counterparty would drop, for the confirmation (ADR-0003 §6).</summary>
    public ScopeDrop PreviewCounterpartyChange(string workspaceId, string user)
    {
        var state = Owned(workspaceId, user);
        var scope = $"cp:{Source(state).Registration.Key}";
        var rules = new PortfolioEditRules();
        // A case goes with the counterparty it was made on, with every change it holds (ADR-0010 §3), and is counted.
        var held = state.InScope(scope).Keys.Concat(state.Created.Values.Where(c => c.Scope == scope).Select(c => new EditTarget(scope, c.Entity, "*"))).ToList();
        return new(scope, held.Count, held.GroupBy(rules.KindOf).ToDictionary(g => g.Key, g => g.Count()));
    }

    /// <summary>Changes the counterparty: the confirmed edits of the previous one are dropped, a new trade's own edits stay.</summary>
    public (WorkspaceState State, CarrySummary Summary) ChangeCounterparty(string workspaceId, long revision, string user, string registration, int confirmedCount)
    {
        var source = Source(Owned(workspaceId, user));
        if (source.Registration.Key == registration)
            throw new WorkspaceRefused("SameCounterparty", $"The workspace is already on {registration}.");
        var next = new PortfolioSourceVersion(registration, source.ValueDate, source.Descriptor["existingCount"]!.GetValue<int>());
        return DataEdits(workspaces.ChangeScope(workspaceId, revision, user, $"cp:{source.Registration.Key}", confirmedCount,
            Context(registration, source.ValueDate, source.Descriptor["existingCount"]!.GetValue<int>()), next));
    }

    /// <summary>Price: freezes the revision the user saw and submits one Run for it.</summary>
    public SubmissionResult Price(string workspaceId, long revision, string user, string idempotencyKey)
    {
        Owned(workspaceId, user);
        // A retry is answered before anything is frozen again: a PV01 may have answered since, or the user moved on,
        // and neither makes it another Price (XV13).
        return runs.Known(user, idempotencyKey, PortfolioPrice.Basis(workspaceId, revision))
            ?? runs.Submit(PortfolioPrice.Submission(workspaces, store, register, workspaceId, revision, idempotencyKey, user, caseCap: caseCap));
    }

    public void Delete(string workspaceId, string user) => workspaces.Delete(workspaceId, user);

    IReadOnlyList<EffectiveTrade> Rows(WorkspaceState state) => TradeOrder.Ordered(state, register, null, []);

    WorkspaceState Owned(string workspaceId, string user)
    {
        var state = workspaces.Find(workspaceId);
        if (state is null || state.App != PortfolioEditRules.App) throw new WorkspaceRefused("UnknownWorkspace", $"No XVA workspace {workspaceId}.");
        if (state.Owner != user) throw new WorkspaceRefused("NotOwner", "This workspace belongs to another user.");
        return state;
    }

    static PortfolioSourceVersion Source(WorkspaceState state) => (PortfolioSourceVersion)state.Source;

    static JsonObject Context(string registration, DateOnly valueDate, int existingCount) => new()
    {
        ["registration"] = registration,
        ["valueDate"] = valueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        ["portfolioSize"] = existingCount,
    };

    // A field holds a Common edit and one per case at once; the layer says which (ADR-0010 §3).
    static EditTarget Held(WorkspaceState state, string entity, string field, string? layer) =>
        state.Overrides.Keys.Concat(state.Conflicts.Keys).Concat(state.Lost.Keys).FirstOrDefault(t => t.Entity == entity && t.Field == field && t.Layer == layer)
        ?? throw new WorkspaceRefused("NotOverridden", $"{entity}.{field} has no edit{(layer is null ? "" : $" in {layer}")}.");

}

/// <summary>
/// The order the trade grids show their rows in (ADR-0010 §6): one grid's rows, the sort, and the token that says
/// whether two orders are the same. The page that draws a grid and the command that reads a position in it (ADR-0016
/// §2, note 17 of step 2) both take it from here, so a position names the trade the grid drew there.
/// </summary>
public static class TradeOrder
{
    public static readonly IReadOnlyList<string> SortableColumns =
        ["tradeId", "isNew", "product", "currency", "notional", "maturity", "fixedRate", "schedule", "lastFixing", "officialPv", "csa", "curve", "imR", "imP", "before", "after"];

    /// <summary>The sort as asked, checked: a column the grids cannot sort by is refused by name, never guessed.</summary>
    public static IReadOnlyList<(string Column, bool Descending)> Keys(IEnumerable<(string Column, bool Descending)> sorts)
    {
        var keys = sorts.ToList();
        return keys.FirstOrDefault(k => !SortableColumns.Contains(k.Column)) is { Column: { } unknown }
            ? throw new WorkspaceRefused("UnknownColumn", $"Rows cannot be sorted by {unknown}.") : keys;
    }

    // Kept per state while the state is alive: scrolling asks for many pages of one order, and a line read with a view
    // asks for the order again at every key typed. A state is of one revision, so what is kept is never out of date.
    static readonly ConditionalWeakTable<WorkspaceState, ConcurrentDictionary<(MeasureRegister Register, string Order), IReadOnlyList<EffectiveTrade>>> Orders = new();

    /// <summary>
    /// The trades of one grid, or of both, under a sort, with each measure's membership on them and no module's cell:
    /// the one order the page's reader and the trades' table both answer (ADR-0016 §2, note 17 of 2026-11-11).
    /// </summary>
    public static IReadOnlyList<EffectiveTrade> Ordered(WorkspaceState state, MeasureRegister register, string? grid, IReadOnlyList<(string Column, bool Descending)> keys)
    {
        var orders = Orders.GetOrCreateValue(state);
        IReadOnlyList<EffectiveTrade> Whole() => orders.GetOrAdd((register, ""), _ => register.WithMembership(state, PortfolioMaterialiser.Trades(state)));
        IReadOnlyList<EffectiveTrade> Grid() => grid is null ? Whole() : orders.GetOrAdd((register, grid), _ => Of(Whole(), grid));
        return keys.Count == 0 ? Grid() : orders.GetOrAdd((register, OrderOf(grid, keys)), _ => Sorted(Grid(), keys));
    }

    /// <summary>The rows of one trade grid (<c>new</c> or <c>existing</c>), in the order given; every row where none is named.</summary>
    public static IReadOnlyList<EffectiveTrade> Of(IReadOnlyList<EffectiveTrade> rows, string? grid) => grid switch
    {
        null => rows,
        Entity.NewGrid or Entity.ExistingGrid => [.. rows.Where(t => t.IsNew == (grid == Entity.NewGrid))],
        _ => throw new WorkspaceRefused("UnknownGrid", $"No trade grid {grid}; the grids are {Entity.NewGrid} and {Entity.ExistingGrid}."),
    };

    /// <summary>The rows under a sort, stable: rows the sort does not tell apart stay in the order given.</summary>
    public static IReadOnlyList<EffectiveTrade> Sorted(IReadOnlyList<EffectiveTrade> rows, IReadOnlyList<(string Column, bool Descending)> keys) => keys.Count == 0 ? rows
        : [.. rows.Order(Comparer<EffectiveTrade>.Create((a, b) =>
        {
            foreach (var (column, descending) in keys)
                if (Compare(Key(a, column), Key(b, column), descending) is var c && c != 0) return c;
            return 0;
        }))];

    // A sorted order's digest, kept per state as its rows are: every page of one order asks for its token.
    static readonly ConditionalWeakTable<WorkspaceState, ConcurrentDictionary<(MeasureRegister Register, string Order), string>> Digests = new();

    /// <summary>
    /// The token of a grid's order (ADR-0016 note 26 of step 2): it moves when the order does, and only then. Unsorted,
    /// the order is the source's and moves with a new version. Sorted, an edit may move a row: the token is the version
    /// and a digest of the keys in that order, so an edit that moves, adds and removes no row leaves it.
    /// </summary>
    public static string Token(WorkspaceState state, MeasureRegister register, string? grid, IReadOnlyList<(string Column, bool Descending)> keys) =>
        keys.Count == 0 ? state.Source.Version
        : Digests.GetOrCreateValue(state).GetOrAdd((register, OrderOf(grid, keys)), _ =>
            $"{state.Source.Version}/o{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', Ordered(state, register, grid, keys).Select(t => t.TradeId)))).AsSpan(0, 8))}");

    static string OrderOf(string? grid, IReadOnlyList<(string Column, bool Descending)> keys) => $"{grid}|{string.Join(",", keys.Select(k => $"{k.Column}:{k.Descending}"))}";

    static IComparable? Key(EffectiveTrade row, string column) => column switch
    {
        "tradeId" => row.TradeId,
        "isNew" => row.IsNew,
        "product" => row.Product,
        "currency" => row.Currency,
        "notional" => row.Notional,
        "maturity" => row.Maturity,
        "fixedRate" => row.FixedRate,
        "schedule" => row.Schedule,
        "lastFixing" => row.LastFixing,
        "officialPv" => row.OfficialPv,
        "csa" => row.Csa,
        "curve" => row.Curve,
        "imR" => row.ImR,
        "imP" => row.ImP,
        "before" => row.Before,
        "after" => row.After,
        _ => throw new InvalidOperationException(column),
    };

    // As ExGrid's reference semantics (its ADR-0023): a Blank sorts last in both directions, and text compares
    // OrdinalIgnoreCase.
    static int Compare(IComparable? a, IComparable? b, bool descending) => (a, b) switch
    {
        (null, null) => 0,
        (null, _) => 1,
        (_, null) => -1,
        (string x, string y) => (descending ? -1 : 1) * string.Compare(x, y, StringComparison.OrdinalIgnoreCase),
        _ => (descending ? -1 : 1) * a.CompareTo(b),
    };
}
