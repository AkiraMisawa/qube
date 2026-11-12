using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Qube.Composition.Application;
using Qube.Composition.Application.Saved;
using Qube.Composition.Application.Tables;
using Qube.Composition.Application.Settings;
using Qube.Composition.Application.Workspaces;

namespace Qube.Samples.Portfolio;

/// <summary>What <c>scenario save</c> says back: the set as saved, how many scenarios it holds, and whether it replaced the user's own.</summary>
public sealed record SetSaved(string Set, string Owner, SavedVisibility Visibility, string? Desk, int Scenarios, bool Replaced);

/// <summary>A scenario set the user can see, and the name <c>scenario load</c> takes for it.</summary>
public sealed record ScenarioSetView(string Label, string Name, string Owner, SavedVisibility Visibility, string? Desk, DateTimeOffset SavedAt, int Scenarios);

/// <summary>A sort the grid asked for: a column of <see cref="EffectiveTrade"/> and its direction.</summary>
public sealed record RowSort(string Column, string Direction);

/// <summary>One page of effective rows, in the shape ExGrid's fetching source takes (ADR-0003 §4).</summary>
/// <param name="Of">How many rows the grid has with no conditions, where the page was read under some (ADR-0016 note 41 of step 4); none where it was not.</param>
public sealed record RowPage(IReadOnlyList<EffectiveTrade> Rows, int Start, int TotalCount, string OrderToken, long Revision, int? Of = null);

/// <summary>What the workspace panel shows beside the grid. <c>Overrides</c> counts the edits to the portfolio's data;
/// the selection of measures is the page's choice, shown on its own. <c>TradeColumns</c> are the selected modules'
/// (ADR-0010 §10), as are the shared tabs' <c>Inputs</c> they need, and <c>PendingCells</c> counts their cells on the new trades still waiting for a result, so the page
/// asks again while any is. <c>DisplayCurrency</c> is the workspace's setting, <c>DisplayCurrencyFrom</c> where it came
/// from (<c>settings</c>, <c>workspace</c> or <c>profile</c>, ADR-0017 §3), and <c>Simulated</c> names the modules whose
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
    string DisplayCurrencyFrom,
    IReadOnlyList<string> Currencies,
    IReadOnlyList<string> Simulated,
    IReadOnlyList<CaseView> Cases,
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

/// <summary>
/// What a command would write and say (ADR-0016 note 28 of 2026-11-11): planned on the workspace as it stands, with
/// nothing written. The line's reading tries the changes and says the plan; the command writes them as one batch,
/// named in the log by <paramref name="Said"/>, the words that would type it.
/// </summary>
public sealed record CommandPlan(IReadOnlyList<WorkspaceChange> Changes, string Said);

/// <summary><c>vary</c> as planned: the cases it would make, and the values it would skip, each with why.</summary>
public sealed record VaryPlan(CommandPlan Command, IReadOnlyList<string> Made, IReadOnlyList<string> Skipped);

/// <summary><c>add each</c> as planned: the cases it would make, and those of the set made before that it would replace.</summary>
public sealed record AddPlan(CommandPlan Command, IReadOnlyList<string> Made, IReadOnlyList<string> Replaced);

/// <summary>The cases a command would remove, each by its title with how many changes it holds.</summary>
public sealed record CasesPlan(CommandPlan Command, IReadOnlyList<(string Title, int Changes)> Removed);

/// <summary>
/// What Price would price (ADR-0016 note 33): the measures by their titles, the cases with the Base, the markets with
/// the base market, whether the Base holds a trade in Before and in After, and each measure a case's input already
/// refuses, which the Run would say of that case and price the rest.
/// </summary>
public sealed record PricePlan(IReadOnlyList<string> Measures, int Cases, int Markets, bool Before, bool After, IReadOnlyList<PriceRefusal> Refused);

/// <summary>A measure refused for a case before Price starts, with why.</summary>
public sealed record PriceRefusal(string Measure, string Case, string Message);

/// <summary>A scenario command as planned (ADR-0016 note 32).</summary>
/// <param name="Made">The scenarios it would make, by name.</param>
/// <param name="Replaced">Those of them that would replace one of the same name.</param>
/// <param name="Named">The scenarios of the workspace it names, each by the name the workspace holds: the one included, excluded, removed or copied, and every one for a clear.</param>
/// <param name="Shifts">For <c>scenario</c>, the shifts as read.</param>
public sealed record ScenarioPlan(CommandPlan Command, IReadOnlyList<string> Made, IReadOnlyList<string> Replaced, IReadOnlyList<string> Named, IReadOnlyList<Shift> Shifts);

/// <summary>A selection of measures as planned: the measures Price would price, in register order, by id and by title.</summary>
public sealed record MeasuresPlan(CommandPlan Command, IReadOnlyList<string> Ids, IReadOnlyList<string> Titles);

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
/// <param name="settings">The users' settings, whose display currency a new workspace starts in (ADR-0017 §3).</param>
public sealed class PortfolioWorkspaces(WorkspaceService workspaces, IDataStore store, RunService runs, MeasureRegister register, UserSettings settings,
    PortfolioOptions? options = null, SavedItems? saved = null)
{
    readonly int caseCap = (options ?? new PortfolioOptions()).CaseCap;
    readonly PortfolioChecks checks = new(register);

    // The pricer's tables as the kernel reads them, for a page read under a grid's conditions (ADR-0016 note 41 of step 4).
    readonly TableRegister tables = new([new PortfolioTables(register)]);

    /// <summary>
    /// A new workspace, on its owner's last selection of measures (ADR-0010 §10, DEFAULT) and in the display currency they
    /// set (ADR-0017 §3). A display currency they set that is no longer one the pricer offers refuses the making, by the
    /// setting's name, rather than starting in another.
    /// </summary>
    public WorkspaceState Create(string user, string registration, DateOnly valueDate, int existingCount)
    {
        var source = new PortfolioSourceVersion(registration, valueDate, existingCount);
        // Both are part of the workspace's making, so `undo` never takes them back (ADR-0016 §11, note 2), and the page
        // says the display currency came from the user's setting (ADR-0017 §3).
        var made = new List<WorkspaceChange.Set>();
        if (LastSelection(user) is { } selection) made.Add(new(PortfolioEditRules.Target(source, Entity.Measures, Entity.MeasuresField), Chosen(selection)));
        if (settings.Default(user, PortfolioSettings.DisplayCurrency) is { } currency)
            made.Add(new(PortfolioEditRules.Target(source, Entity.WorkspaceSettings, Entity.DisplayCurrency), currency));
        return workspaces.Create(user, PortfolioEditRules.App, Context(registration, valueDate, existingCount), source, with: made);
    }

    JsonArray Chosen(IReadOnlyList<string> measures) => new([.. register.Checked(measures).Select(id => JsonValue.Create(id))]);

    /// <summary>
    /// <c>measures cva fva</c> as planned (ADR-0016 note 28): the words name measures by their ids, or a module for all
    /// of its measures, whatever their case and once each. An unknown one, or none, is refused by name. A selection
    /// the workspace already holds plans no change.
    /// </summary>
    public MeasuresPlan PlanMeasures(WorkspaceState raw, string words)
    {
        var known = register.Ids.Concat(register.Modules.Select(m => m.Id)).ToList();
        var typed = words.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => known.FirstOrDefault(id => id.Equals(w, StringComparison.OrdinalIgnoreCase)) ?? w.ToLowerInvariant()).Distinct().ToList();
        var ids = register.Checked(typed);
        var titles = register.Measures.Where(m => ids.Contains(m.Id)).Select(m => m.Title).ToList();
        IReadOnlyList<WorkspaceChange> changes = PortfolioMaterialiser.SelectedMeasures(PortfolioCases.Base(raw), register).SequenceEqual(ids) ? []
            : [new WorkspaceChange.Set(PortfolioEditRules.Target(Source(raw), Entity.Measures, Entity.MeasuresField), Chosen(ids))];
        return new(new(changes, $"measures {string.Join(' ', ids)}"), ids, titles);
    }

    /// <summary>Selects the measures Price prices, as one command (<c>measures cva fva</c>); a selection already held writes nothing.</summary>
    public (WorkspaceState State, MeasuresPlan Plan) SelectMeasures(string workspaceId, long revision, string user, string words, RunOrigin? origin = null)
    {
        var raw = Owned(workspaceId, user);
        var plan = PlanMeasures(raw, words);
        return (Written(raw, revision, user, plan.Command, origin), plan);
    }

    /// <summary>
    /// The state a command's planned changes would leave, checked as the write checks them and written nowhere
    /// (ADR-0016 note 28): what a line's reading tries. A plan of no change leaves the state as it is.
    /// </summary>
    public WorkspaceState Trial(WorkspaceState raw, string user, CommandPlan plan, RunOrigin? origin = null) =>
        plan.Changes.Count == 0 ? raw : workspaces.After(raw, user, plan.Changes, origin);

    // The plan written as one command, named in the log by its words; a plan of no change writes nothing.
    WorkspaceState Written(WorkspaceState raw, long revision, string user, CommandPlan plan, RunOrigin? origin) =>
        plan.Changes.Count == 0 ? raw : workspaces.Batch(raw.WorkspaceId, revision, user, plan.Changes, origin, plan.Said);

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
            PortfolioMaterialiser.DisplayCurrency(state), PortfolioMaterialiser.DisplayCurrencyFrom(state), PortfolioMarket.Currencies, [.. register.Selected(state).Where(m => m.Simulated).Select(m => m.Id)],
            PortfolioCases.All(raw), DataEdits(raw).Count(), PortfolioScenarios.All(raw), PortfolioScenarios.Rows(raw), register.Settings(state));
    }

    /// <summary>
    /// <c>scenario &lt;name&gt; &lt;shift&gt;; …</c> as planned (ADR-0010 §4 and its note of 2026-11-09, ADR-0016 note 32): the
    /// scenario whole, its shifts read as written, in one log entry per row; one of that name is replaced, and stays
    /// included or excluded as it was.
    /// </summary>
    public ScenarioPlan PlanScenario(WorkspaceState raw, long revision, string name, string shifts)
    {
        var read = ShiftReader.ReadAll(shifts);
        var named = PortfolioScenarios.Checked(name);
        var (changes, replaced) = PortfolioScenarios.Define(raw, named, read, revision);
        return new(new(changes, $"scenario {named} {string.Join("; ", read.Select(s => s.Text))}"), [named], replaced ? [named] : [], [], read);
    }

    /// <summary>
    /// <c>scenario ladder …</c> and <c>scenario cross …</c> as planned (ADR-0010 §4, ADR-0016 note 32): a set of scenarios
    /// at once, each named from its shifts and replacing one of its name, in one batch; the plan names those it would
    /// make and those it would replace.
    /// </summary>
    public ScenarioPlan PlanGenerate(WorkspaceState raw, long revision, string generator, string text)
    {
        var made = generator switch
        {
            "ladder" => ScenarioGenerators.Ladder(text, caseCap),
            "cross" => ScenarioGenerators.Cross(text, caseCap),
            _ => throw new WorkspaceRefused("InvalidGenerator", $"There is no generator {generator}; there are ladder and cross."),
        };
        var (changes, replaced) = PortfolioScenarios.DefineAll(raw, made, revision);
        return new(new(changes, $"scenario {generator} {Words(text)}"), [.. made.Select(m => m.Name)], replaced, [], []);
    }

    /// <summary>
    /// The pairs of a case and a market Price would price as the workspace stands (ADR-0010 §4's note of 2026-11-09),
    /// and the most it prices: what a command that makes scenarios says of what it leaves.
    /// </summary>
    public (int Pairs, int Cap) Pairs(WorkspaceState raw) => (PortfolioCases.All(raw).Count * (1 + PortfolioScenarios.All(raw).Count(s => s.Included)), caseCap);

    /// <summary>
    /// The Scenarios grid's edits as one batch (ADR-0010 §4): a cell typed, a block pasted onto it, or cells cleared, each
    /// read as its column reads it; one cell that does not resolve refuses the edit whole, naming its row and column.
    /// </summary>
    public WorkspaceState EditShifts(string workspaceId, long revision, string user, IReadOnlyList<ShiftEdit> edits, RunOrigin? origin = null)
    {
        var raw = At(workspaceId, revision, user);
        var changes = PortfolioScenarios.Edit(raw, edits, revision);
        return changes.Count == 0 ? raw : workspaces.Batch(workspaceId, revision, user, changes, origin);
    }

    /// <summary>
    /// <c>scenario include|exclude &lt;name&gt;</c> as planned (ADR-0016 note 32): whether Price prices it, as its box says;
    /// its rows stay. The scenario by the name it holds; no change where it already stands so.
    /// </summary>
    public ScenarioPlan PlanInclude(WorkspaceState raw, string name, bool included)
    {
        var changes = PortfolioScenarios.Include(raw, name, included);
        var held = PortfolioScenarios.Named(raw, name);
        return new(new(changes, $"scenario {(included ? "include" : "exclude")} {held}"), [], [], [held], []);
    }

    /// <summary>
    /// <c>scenario remove &lt;name&gt;</c> as planned (ADR-0016 note 32): its rows, in one batch; the scenario by the name
    /// it holds, with how many shifts go with it.
    /// </summary>
    public ScenarioPlan PlanRemoveScenario(WorkspaceState raw, string name)
    {
        var changes = PortfolioScenarios.Remove(raw, name);
        var held = PortfolioScenarios.Named(raw, name);
        return new(new(changes, $"scenario remove {held}"), [], [], [held], []);
    }

    /// <summary><c>scenarios clear</c> as planned (ADR-0016 note 32): every shift row of every scenario the workspace holds; with none, refused.</summary>
    public ScenarioPlan PlanClearScenarios(WorkspaceState raw)
    {
        var changes = PortfolioScenarios.Clear(raw);
        if (changes.Count == 0) throw new WorkspaceRefused("NothingToChange", "The workspace has no scenario to remove.");
        return new(new(changes, "scenarios clear"), [], [], [.. PortfolioScenarios.All(raw).Select(s => s.Name)], []);
    }

    /// <summary>
    /// <c>scenario duplicate &lt;name&gt; &lt;new name&gt;</c> as planned (ADR-0016 note 32): the scenario's rows under the
    /// new name, to be edited; the copy by its name.
    /// </summary>
    public ScenarioPlan PlanDuplicate(WorkspaceState raw, long revision, string name, string copy)
    {
        var changes = PortfolioScenarios.Duplicate(raw, name, copy, revision);
        var (held, made) = (PortfolioScenarios.Named(raw, name), PortfolioScenarios.Checked(copy));
        return new(new(changes, $"scenario duplicate {held} {made}"), [made], [], [held], []);
    }

    /// <summary>
    /// <c>scenario save &lt;set&gt; [desk|all]</c> (ADR-0010 §4, C32): every scenario of the workspace, included or not, saved
    /// under the user's name for the set, for them alone unless shared with their desk or with all; saved again, it
    /// replaces their own set of that name. The workspace does not change.
    /// </summary>
    public SetSaved SaveScenarios(string workspaceId, long revision, string user, string set, string? share = null)
    {
        var raw = At(workspaceId, revision, user);
        var (count, visibility) = SetToSave(raw, set, share);
        var (item, replaced) = Saving(s => s.Save(PortfolioScenarios.SetKind, SetNoun, user, set, visibility, PortfolioScenarios.SetOf(raw)));
        return new SetSaved(item.Name, item.Owner, item.Visibility, item.Desk, count, replaced);
    }

    /// <summary><c>scenario save</c> as it would save (ADR-0016 note 32): checked as the save checks it, and nothing kept.</summary>
    public SetSaved PlanSave(WorkspaceState raw, string user, string set, string? share)
    {
        var (count, visibility) = SetToSave(raw, set, share);
        var (item, replaced) = Saving(s => s.Planned(PortfolioScenarios.SetKind, SetNoun, user, set, visibility, PortfolioScenarios.SetOf(raw)));
        return new SetSaved(item.Name, item.Owner, item.Visibility, item.Desk, count, replaced);
    }

    // How many scenarios a save keeps, and whom it is for; a workspace with none, or a share that is neither word, is refused.
    static (int Scenarios, SavedVisibility Visibility) SetToSave(WorkspaceState raw, string set, string? share)
    {
        var count = PortfolioScenarios.All(raw).Count;
        if (count == 0) throw new WorkspaceRefused("NothingToSave", "The workspace has no scenario to save. Define one first, such as scenario up jpy_ois +10bp.");
        return (count, share?.Trim().ToLowerInvariant() switch
        {
            null or "" => SavedVisibility.Private,
            "desk" => SavedVisibility.Desk,
            "all" => SavedVisibility.All,
            var other => throw new WorkspaceRefused("InvalidShare", $"scenario save {set.Trim()} shares the set with desk or all, not {other}."),
        });
    }

    /// <summary>
    /// <c>scenario load &lt;set&gt;</c> as planned (ADR-0010 §4, ADR-0016 note 32): the set's scenarios added in one batch,
    /// replacing those of the same name and included as they were saved. A set named <c>&lt;set&gt;@&lt;owner&gt;</c> is that
    /// owner's; a name two sets the user can see share is refused, naming their owners.
    /// </summary>
    public ScenarioPlan PlanLoad(WorkspaceState raw, long revision, string user, string set)
    {
        var item = Saving(s => s.Find(PortfolioScenarios.SetKind, SetNoun, user, set));
        var (changes, made, replaced) = PortfolioScenarios.Load(raw, item.Content, revision);
        return new(new(changes, $"scenario load {Words(set)}"), made, replaced, [], []);
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

    /// <summary>
    /// A scenario command as planned on the workspace at the revision named and written as one command, named in the log
    /// by its words (ADR-0016 note 32); one that changes nothing leaves the workspace as it stands. Answers the plan it wrote.
    /// </summary>
    public (WorkspaceState State, ScenarioPlan Plan) WriteScenarios(string workspaceId, long revision, string user, Func<WorkspaceState, ScenarioPlan> planned, RunOrigin? origin = null)
    {
        var raw = At(workspaceId, revision, user);
        var plan = planned(raw);
        return (Written(raw, revision, user, plan.Command, origin), plan);
    }

    // A line's words with one space between them, as the log names a command.
    static string Words(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

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
    public (WorkspaceState State, CasesPlan Plan) RemoveCase(string workspaceId, long revision, string user, string name, RunOrigin? origin = null)
    {
        var raw = Owned(workspaceId, user);
        var plan = PlanRemoveCase(raw, name);
        return (Written(raw, revision, user, plan.Command, origin), plan);
    }

    /// <summary><c>case remove</c> as planned (ADR-0016 note 28): the case the name means, with how many changes it holds.</summary>
    public CasesPlan PlanRemoveCase(WorkspaceState raw, string name)
    {
        var cases = PortfolioCases.All(raw).Skip(1).ToList();
        var named = cases.Where(c => string.Equals(c.Id, name.Trim(), StringComparison.OrdinalIgnoreCase)).ToList() is { Count: 1 } byId ? byId
            : cases.Where(c => string.Equals(c.Title, name.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        // A name that finds no case, or several, is the word the refusal names (ADR-0016 note 45); with no name the
        // refusal is of the whole line.
        if (named.Count == 0)
            throw cases.Count == 0 ? new WorkspaceRefused("UnknownCase", "The workspace has no case; the Base is the workspace itself and is never removed.", word: name.Trim().Length == 0 ? null : name.Trim())
                : name.Trim().Length == 0 ? new WorkspaceRefused("UnknownCase", $"case remove takes the case to remove, by its title; the cases are {string.Join(", ", cases.Select(c => c.Title))}.")
                : new WorkspaceRefused("UnknownCase", $"No case {name.Trim()}; the cases are {string.Join(", ", cases.Select(c => c.Title))}.", word: name.Trim(), candidates: [.. cases.Select(c => c.Title)]);
        if (named.Count > 1)
            throw new WorkspaceRefused("AmbiguousCase", $"{name.Trim()} names {named.Count} cases, {string.Join(" and ", named.Select(c => c.Id))}; remove one by its id.",
                word: name.Trim(), candidates: [.. named.Select(c => c.Id)]);
        var entity = PortfolioCases.Of(named[0].Id);
        return new(new([new WorkspaceChange.Remove(entity, entity)], $"case remove {named[0].Title}"), [(named[0].Title, PortfolioCases.ChangesOf(raw, named[0].Id).Count)]);
    }

    /// <summary>Removes every case with its changes, as one command (<c>cases clear</c>, ADR-0010 §3).</summary>
    public (WorkspaceState State, CasesPlan Plan) ClearCases(string workspaceId, long revision, string user, RunOrigin? origin = null)
    {
        var raw = Owned(workspaceId, user);
        var plan = PlanClearCases(raw);
        return (Written(raw, revision, user, plan.Command, origin), plan);
    }

    /// <summary><c>cases clear</c> as planned (ADR-0016 note 28): every case the workspace holds; with none, refused.</summary>
    public CasesPlan PlanClearCases(WorkspaceState raw)
    {
        var made = PortfolioCases.Made(raw);
        if (made.Count == 0) throw new WorkspaceRefused("NothingToChange", "The workspace has no case to remove.");
        return new(new([.. made.Select(c => new WorkspaceChange.Remove(c.Entity, c.Entity))], "cases clear"),
            [.. made.Select(c => (PortfolioCases.Title(c), PortfolioCases.ChangesOf(raw, Entity.Split(c.Entity).Id).Count))]);
    }

    // A case whose changes may be changed one by one: a case of iterative addition is its membership, made whole by
    // add each and removed whole (ADR-0010 §3's note of 2026-11-08), so none of its changes is set or removed alone.
    static string Changeable(WorkspaceState raw, string caseId)
    {
        var layer = CaseOf(raw, caseId);
        var made = raw.Created[layer];
        return PortfolioCases.KindOf(made) != PortfolioCases.Add ? layer
            : throw new WorkspaceRefused("NotEditable",
                $"Case {PortfolioCases.Title(made)} is its trades and no other change: remove the case whole, or run add each again to make the set anew.");
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

    /// <summary>
    /// <c>refresh</c> (ADR-0003 §6): the same counterparty's trades and CSAs read again on the value date the workspace
    /// holds; edits carry or are held. The log names it by its word, with the caller's origin.
    /// </summary>
    public (WorkspaceState State, CarrySummary Summary) Refresh(string workspaceId, long revision, string user, RunOrigin? origin = null) =>
        Reacquired(workspaceId, revision, user, Source(Owned(workspaceId, user)).ValueDate, origin, "refresh");

    /// <summary>The revision a workspace of the user's holds now, for a command that names none.</summary>
    public long Revision(string workspaceId, string user) => Owned(workspaceId, user).Revision;

    public IReadOnlyList<WorkspaceState> OwnedBy(string user) =>
        [.. workspaces.OwnedBy(user).Where(w => w.App == PortfolioEditRules.App)];

    /// <summary>
    /// A page of effective rows under a sort, of one trade grid (<c>new</c> or <c>existing</c>, ADR-0010 §6) or of all.
    /// Refused for an unknown column or grid, never sorted or filtered by a guess. Under a grid's conditions
    /// (<paramref name="where"/>, ADR-0016 note 41 of step 4) the trades' own table gives the order and the kernel
    /// narrows it, which is how a line reads <c>shown</c> and a position under the same conditions: the page and the
    /// line cannot disagree. Conditions that cannot be read are refused, never answered as every row.
    /// </summary>
    public RowPage Page(string workspaceId, string user, int start, int count, IReadOnlyList<RowSort> sorts, string? grid = null, string? where = null)
    {
        if (start < 0 || count < 1) throw new WorkspaceRefused("InvalidRange", "A range starts at 0 or later and holds at least one row.");
        if (grid is not (null or Entity.NewGrid or Entity.ExistingGrid))
            throw new WorkspaceRefused("UnknownGrid", $"No trade grid {grid}; the grids are {Entity.NewGrid} and {Entity.ExistingGrid}.");
        var owned = Owned(workspaceId, user);
        var state = PortfolioCases.Base(owned);
        var keys = TradeOrder.Keys(sorts.Select(sort => (sort.Column, sort.Direction switch
        {
            "ascending" => false,
            "descending" => true,
            _ => throw new WorkspaceRefused("InvalidSort", $"Sort direction {sort.Direction} is not ascending or descending."),
        })));
        var all = TradeOrder.Ordered(state, register, grid, keys);
        var token = TradeOrder.Token(state, register, grid, keys);
        int? of = null;
        if (where is not null)
        {
            var view = new LineView(PortfolioTables.Trades, grid, [.. keys.Select(k => new ViewSort(k.Column, k.Descending))], Where: where);
            var left = GridRows.Paged(view, tables.Of(owned), owned);
            var trades = all.ToDictionary(t => t.TradeId, StringComparer.Ordinal);
            (of, all, token) = (all.Count, [.. left.Rows.Select(r => trades[r.Key])], left.Token);
        }
        // The modules' cells are read for the page alone, each time: a result such as PV01 arrives with no edit.
        var page = start >= all.Count ? [] : register.WithCells(state, [.. all.Skip(start).Take(count)]);
        return new(page, start, all.Count, token, state.Revision, of);
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
        var raw = Owned(workspaceId, user);
        return Varied(raw, revision, user, PlanVary(raw, revision, cell, values), origin);
    }

    /// <summary>
    /// <c>vary</c> as planned (ADR-0016 note 28): the cases it would make at <paramref name="revision"/>, which names
    /// them, and the values it would skip. A cell or a value that cannot be read is refused, and plans nothing.
    /// </summary>
    public VaryPlan PlanVary(WorkspaceState raw, long revision, string cell, IReadOnlyList<string> values)
    {
        var text = cell.Trim();
        var at = text.IndexOf(' ');
        if (at <= 0)
            throw new WorkspaceRefused("UnknownCell", $"Name a cell as its row and its column, such as CSA-A threshold; {(text.Length == 0 ? "none was named" : $"{text} is one word")}.");
        return Plan(raw, revision, PortfolioCells.Resolve(PortfolioCases.Base(raw), register, text[..at], text[(at + 1)..]), values);
    }

    /// <summary>As <see cref="PlanVary"/>, on the cell of an entity's field, as a page names a cell.</summary>
    public VaryPlan PlanVaryField(WorkspaceState raw, long revision, string entity, string field, IReadOnlyList<string> values) =>
        Plan(raw, revision, PortfolioCells.Of(PortfolioCases.Base(raw), register, entity, field), values);

    VaryAnswer Varied(WorkspaceState raw, long revision, string user, VaryPlan plan, RunOrigin? origin) =>
        new(Written(raw, revision, user, plan.Command, origin), plan.Made, plan.Skipped);

    /// <summary>As <see cref="Vary(string, long, string, string, IReadOnlyList{string}, RunOrigin?)"/>, on the cell of an entity's
    /// field: <em>Vary values…</em> on a cell of the input tabs or the trade grids (ADR-0010 §3).</summary>
    public VaryAnswer VaryField(string workspaceId, long revision, string user, string entity, string field, IReadOnlyList<string> values, RunOrigin? origin = null)
    {
        var raw = Owned(workspaceId, user);
        return Varied(raw, revision, user, PlanVaryField(raw, revision, entity, field, values), origin);
    }

    // Every value is read and checked as the cell would check it typed, before any case is made: a list holding a value
    // that cannot be read makes none. A value the Base holds makes no case, nor one a case already holds; each is said.
    VaryPlan Plan(WorkspaceState raw, long revision, CellName cell, IReadOnlyList<string> values)
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
        // The command by the words that would type it, which the log and `undo` name it by.
        var said = $"vary {cell.Row} {cell.Column} {string.Join(", ", written)}";
        if (made.Count == 0) return new(new([], said), [], skipped);
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
        return new(new(changes, said), [.. made.Select(m => m.Title)], skipped);
    }

    /// <summary>
    /// <c>cds &lt;words&gt;</c>, <c>cds proxy &lt;sector&gt; &lt;region&gt; &lt;rating&gt;</c>, <c>cds none</c> (ADR-0010 §8's note
    /// of 2026-11-09): the counterparty's CDS, read on the server, set in one log entry. Only the keys that change are
    /// written; a key of the other type is kept, so choosing that type again finds it.
    /// </summary>
    public (WorkspaceState State, EffectiveCredit Credit) ChooseCds(string workspaceId, long revision, string user, string text, RunOrigin? origin = null)
    {
        var raw = Owned(workspaceId, user);
        var state = Written(raw, revision, user, PlanCds(raw, text), origin);
        return (state, PortfolioMaterialiser.Credit(PortfolioCases.Base(state)));
    }

    /// <summary><c>cds</c> as planned (ADR-0016 note 28): the keys of the choice that would change; a choice already held plans none.</summary>
    public CommandPlan PlanCds(WorkspaceState raw, string text)
    {
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
        return new(changes, $"cds {string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries))}");
    }

    /// <summary>The credit a state holds, as the page shows it: what a reading of <c>cds</c> says of the state its plan would leave.</summary>
    public static EffectiveCredit CreditOf(WorkspaceState state) => PortfolioMaterialiser.Credit(PortfolioCases.Base(state));

    /// <summary>What <c>cds</c> says back: the CDS the counterparty now has, or why it has none.</summary>
    public static string CdsSaid(Registration registration, EffectiveCredit credit) => credit.Cds is { } cds
        ? $"The CDS of {registration.Key} is {CdsNamed(cds)}."
        : WhyNoCds(credit);

    /// <summary>Why a counterparty has no CDS curve, in the credit's own sentence. A credit with neither a curve nor a reason is a defect.</summary>
    public static string WhyNoCds(EffectiveCredit credit) => credit.Missing ?? throw new InvalidOperationException("A credit with no curve says why.");

    /// <summary>A CDS curve in a sentence's words: whether it is a proxy or a single name, its reference and its recovery.</summary>
    public static string CdsNamed(EffectiveCds cds) =>
        $"{(cds.Type == CdsCatalog.Proxy ? "the proxy" : "the single name")} {cds.Reference}, recovery {cds.Recovery * 100m:0.##}%";

    /// <summary>
    /// <c>add each</c> (docs/03 §5, ADR-0010 §3's note of 2026-11-08, ADR-0016 §3 and its note 37): a case per iteration,
    /// each holding its candidates on top of the existing trades less those it takes out, never the others of the set:
    /// in its reading only, A is off on every other New row of the set and on the trades taken out. With no iteration
    /// given, one per New row with A ticked at this moment, that trade alone; given some, those, then one alone for
    /// each ticked New row none names. A candidate in Before is refused by name, never unticked, and nothing is made.
    /// A set made before is replaced in the same command, unless <paramref name="keep"/> says to make the iterations
    /// given beside it; the <c>vary</c> cases stay.
    /// </summary>
    public AddAnswer AddTrades(string workspaceId, long revision, string user, IReadOnlyList<Iteration>? iterations = null, bool keep = false, RunOrigin? origin = null)
    {
        var raw = Owned(workspaceId, user);
        var plan = PlanAdd(raw, revision, iterations, keep);
        return new(Written(raw, revision, user, plan.Command, origin), plan.Made, plan.Replaced);
    }

    /// <summary>
    /// <c>add each</c> as planned (ADR-0016 notes 28 and 37): the cases it would make at <paramref name="revision"/>,
    /// which names them, and those it would replace. With <paramref name="keep"/> (note 37, corrected in review) the
    /// cases of the set made before stay the cases they are and are not read again, so a stale one refuses nothing
    /// here and stays Price's to refuse; the iterations given are made beside them, and one made already is refused.
    /// </summary>
    public AddPlan PlanAdd(WorkspaceState raw, long revision, IReadOnlyList<Iteration>? iterations = null, bool keep = false)
    {
        var trades = PortfolioMaterialiser.Trades(PortfolioCases.Base(raw));
        var given = Given(trades, iterations ?? []);
        if (keep && given.Count == 0)
            throw new WorkspaceRefused("NothingGiven", "keep makes the iterations given beside the cases made, and none is given. Give one, or leave keep out for a case per New row with A ticked.");
        var candidates = trades.Where(t => t.IsNew && t.After).Select(t => (t.TradeId, t.Before)).ToList();
        if (candidates.Count == 0)
            throw new WorkspaceRefused("NothingToAdd", "add each makes a case per New row with A ticked, and no New row has A ticked.");
        var before = PortfolioCases.Made(raw).Where(c => PortfolioCases.KindOf(c) == PortfolioCases.Add).Select(c => (Case: c, Iteration: PortfolioCases.IterationOf(c))).ToList();
        var kept = keep ? before : [];
        var replaced = keep ? [] : before;
        foreach (var one in given)
            if (kept.FirstOrDefault(k => k.Iteration.Same(one)) is { Case: { } already })
                throw new WorkspaceRefused("AlreadyMade", $"The case {PortfolioCases.Title(already)} is made already.");
        var ids = candidates.Select(c => c.TradeId).ToList();
        // The set is every New row with A ticked (§3): one that no iteration names, given or kept, is an iteration alone.
        var named = given.Concat(kept.Select(k => k.Iteration)).SelectMany(i => i.Add).ToHashSet();
        IReadOnlyList<Iteration> make = [.. given, .. ids.Where(id => !named.Contains(id)).Select(id => new Iteration([id], []))];
        // What the command makes is checked: a candidate only a case kept adds is Price's to refuse.
        if (candidates.Where(c => c.Before && make.Any(i => i.Add.Contains(c.TradeId))).Select(c => c.TradeId).ToList() is { Count: > 0 } inBefore)
        {
            var (are, them) = inBefore.Count == 1 ? ("is", "it") : ("are", "them");
            throw new WorkspaceRefused("CandidateInBefore",
                $"{string.Join(", ", inBefore)} {are} in Before, and a new trade is priced alone only out of Before. Clear B on {them}, or A to leave {them} out, then add each again.");
        }
        // The cap counts the Base (ADR-0010 §3's note): a set Price would refuse is not written n² edits deep first.
        var count = PortfolioCases.All(raw).Count - replaced.Count + make.Count;
        if (count > caseCap)
        {
            var over = count - caseCap;
            throw new WorkspaceRefused("TooManyCases", $"add each would make {make.Count} case(s){(keep ? " beside those made" : "")}, and Price prices {caseCap} at most, the Base among them: {over} over. "
                + (keep ? $"Remove {over} case(s) first, or give fewer iterations."
                    : given.Count == 0 ? $"Clear A on {over} new trade(s) first." : "Give fewer iterations, or clear A on a new trade none of them names."));
        }
        var source = Source(raw);
        WorkspaceChange Off(string trade, string layer) =>
            new WorkspaceChange.Set(PortfolioEditRules.Target(source, Entity.Of(Entity.Trade, trade), "after", layer), JsonValue.Create(false));
        var changes = new List<WorkspaceChange>(replaced.Select(r => new WorkspaceChange.Remove(r.Case.Entity, r.Case.Entity)));
        // A case kept holds A off on the candidates the set had when it was made. One that is in the set now and is
        // not off in it yet is turned off there too, so each case kept still adds its own trades alone.
        foreach (var (made, iteration) in kept)
            changes.AddRange(ids.Where(other => !iteration.Add.Contains(other) && !HeldOff(raw, source, other, made.Entity)).Select(other => Off(other, made.Entity)));
        foreach (var iteration in make)
        {
            // A case is named by the log entry that made it, so no id is ever given twice.
            var layer = PortfolioCases.Of($"c{revision + 1 + changes.Count}");
            var fields = iteration.Lists();
            fields["title"] = $"add {iteration.Said()}";
            fields["kind"] = PortfolioCases.Add;
            changes.Add(new WorkspaceChange.Create($"cp:{source.Registration.Key}", layer, fields));
            changes.AddRange(ids.Where(other => !iteration.Add.Contains(other)).Concat(iteration.Without).Select(off => Off(off, layer)));
        }
        return new(new(changes, AddSaid(given, [.. before.Select(b => b.Iteration)], keep)), [.. make.Select(i => $"add {i.Said()}")], [.. replaced.Select(r => PortfolioCases.Title(r.Case))]);
    }

    // Whether a case's layer holds A off on a trade already.
    static bool HeldOff(WorkspaceState raw, PortfolioSourceVersion source, string trade, string layer) =>
        raw.Overrides.TryGetValue(PortfolioEditRules.Target(source, Entity.Of(Entity.Trade, trade), "after", layer), out var held)
        && held.Value is JsonValue value && value.GetValueKind() == System.Text.Json.JsonValueKind.False;

    // With a value there is no line that types the command (§3, Open), so its name in the log, which undo says back,
    // says the iterations given: those no case made before is, the first three and how many more, then how many are
    // as before, or that they were made beside the cases made.
    static string AddSaid(IReadOnlyList<Iteration> given, IReadOnlyList<Iteration> before, bool keep)
    {
        if (given.Count == 0) return "add each";
        var fresh = given.Where(g => !before.Any(g.Same)).ToList();
        var same = given.Count - fresh.Count;
        var lead = $"{string.Join("; ", fresh.Take(3).Select(i => i.Said()))}{(fresh.Count > 3 ? $" and {fresh.Count - 3} more" : "")}";
        if (keep) return before.Count == 0 ? $"add each: {lead}" : $"add each: {lead}, beside the cases made";
        return same == 0 ? $"add each: {lead}" : fresh.Count == 0 ? $"add each: {same} as before" : $"add each: {lead}, with {same} as before";
    }

    // The iterations given, each trade by the id the workspace holds it under, whatever the case of its letters
    // (ADR-0016 note 37). What an iteration cannot name is refused by the trade's name: nothing is ticked, cleared or
    // left out for the caller.
    static IReadOnlyList<Iteration> Given(IReadOnlyList<EffectiveTrade> trades, IReadOnlyList<Iteration> given)
    {
        if (given.Count == 0) return [];
        var held = trades.ToDictionary(t => t.TradeId, StringComparer.OrdinalIgnoreCase);
        List<EffectiveTrade> Held(IReadOnlyList<string> ids)
        {
            var named = ids.Select(id => held.TryGetValue(id, out var trade) ? trade : throw new WorkspaceRefused("UnknownTarget", $"No trade {id} in this workspace.")).ToList();
            return named.GroupBy(t => t.TradeId).FirstOrDefault(g => g.Count() > 1) is { } twice
                ? throw new WorkspaceRefused("GivenTwice", $"{twice.Key} is named twice in one iteration.") : named;
        }
        var read = new List<Iteration>();
        foreach (var iteration in given)
        {
            var (add, without) = (Held(iteration.Add), Held(iteration.Without));
            if (add.Count == 0)
                throw new WorkspaceRefused("NothingToAdd", without.Count == 1
                    ? $"An iteration adds one new trade at least. A case that only takes {without[0].TradeId} out is vary {without[0].TradeId} A off."
                    : "An iteration adds one new trade at least.");
            if (add.FirstOrDefault(t => !t.IsNew) is { } existing)
                throw new WorkspaceRefused("NotACandidate", $"{existing.TradeId} is an existing trade: an iteration adds New rows, and takes existing trades out.");
            if (add.FirstOrDefault(t => !t.After) is { } clear)
                throw new WorkspaceRefused("CandidateNotTicked", $"{clear.TradeId} has A clear, and add each prices New rows with A ticked. Tick A on it, or leave it out of the iteration.");
            if (without.FirstOrDefault(t => t.IsNew) is { } fresh)
                throw new WorkspaceRefused("NotExisting", $"{fresh.TradeId} is a new trade: an iteration takes existing trades out of After. Leave a new trade out of what it adds instead.");
            if (without.FirstOrDefault(t => !t.After) is { } outside)
                throw new WorkspaceRefused("NotInAfter", $"{outside.TradeId} is not in After in the Base, so no case takes it out.");
            var one = new Iteration([.. add.Select(t => t.TradeId)], [.. without.Select(t => t.TradeId)]);
            if (read.Any(one.Same)) throw new WorkspaceRefused("GivenTwice", $"The iteration {one.Said()} is given twice.");
            read.Add(one);
        }
        return read;
    }

    /// <summary>
    /// Asks again for every failed result among the selected modules' cells on the workspace's trades, or on one trade
    /// (`pv01 retry`, and the failed cell's action, ADR-0010 §6), of the workspace at the revision named; one that has
    /// moved since is refused. Answers how many were asked again.
    /// </summary>
    public int Retry(string workspaceId, long revision, string user, string? tradeId = null)
    {
        var state = PortfolioCases.Base(At(workspaceId, revision, user));
        return register.Selected(state).Sum(m => m.Retry(state, Asked(state, tradeId)));
    }

    /// <summary>
    /// The trades a retry would ask again for (ADR-0016 note 33): those of the workspace, or the one named, that show a
    /// failed result among the selected modules' cells, by their ids. Nothing is asked again; a cell read for the first
    /// time is asked for, as when the grid's rows are read.
    /// </summary>
    public IReadOnlyList<string> Failed(WorkspaceState raw, string? tradeId = null)
    {
        var state = PortfolioCases.Base(raw);
        return [.. register.WithCells(state, Asked(state, tradeId)).Where(t => t.Cells?.Values.Any(c => c.State == TradeCell.Failed) == true).Select(t => t.TradeId)];
    }

    // The trades a retry is asked for: every trade, or the one named, whatever the case of its letters; one the
    // workspace does not hold is refused.
    IReadOnlyList<EffectiveTrade> Asked(WorkspaceState state, string? tradeId)
    {
        if (tradeId is null) return Rows(state);
        IReadOnlyList<EffectiveTrade> named = [.. Rows(state).Where(t => string.Equals(t.TradeId, tradeId, StringComparison.OrdinalIgnoreCase))];
        return named.Count == 0 ? throw new WorkspaceRefused("UnknownTarget", $"No trade {tradeId} in this workspace.") : named;
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
    public (WorkspaceState State, CarrySummary Summary) ChangeValueDate(string workspaceId, long revision, string user, DateOnly valueDate) =>
        Reacquired(workspaceId, revision, user, valueDate, null, null);

    (WorkspaceState State, CarrySummary Summary) Reacquired(string workspaceId, long revision, string user, DateOnly valueDate, RunOrigin? origin, string? said)
    {
        var source = Source(Owned(workspaceId, user));
        var count = source.Descriptor["existingCount"]!.GetValue<int>();
        return DataEdits(workspaces.Reacquire(workspaceId, revision, user, Context(source.Registration.Key, valueDate, count),
            new PortfolioSourceVersion(source.Registration.Key, valueDate, count), origin, said));
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
    public SubmissionResult Price(string workspaceId, long revision, string user, string idempotencyKey, RunOrigin origin)
    {
        Owned(workspaceId, user);
        // A retry is answered before anything is frozen again: a PV01 may have answered since, or the user moved on,
        // and neither makes it another Price (XV13).
        return runs.Known(user, idempotencyKey, PortfolioPrice.Basis(workspaceId, revision, origin))
            ?? runs.Submit(PortfolioPrice.Submission(workspaces, store, register, workspaceId, revision, idempotencyKey, user, origin, caseCap: caseCap));
    }

    /// <summary>
    /// What Price would price as the workspace stands at a revision (ADR-0016 note 33): the input built as Price builds
    /// it and refused as Price refuses it, with nothing kept and no Run submitted. A reading of <c>price</c> says this.
    /// </summary>
    public PricePlan PlanPrice(string workspaceId, long revision, string user)
    {
        Owned(workspaceId, user);
        var freeze = workspaces.Freeze(workspaceId, revision, PortfolioCases.PricedEntities);
        var input = PortfolioInput.Build(freeze, register, caseCap);
        var rows = Rows(PortfolioCases.Base(freeze.State));
        var titles = register.Measures.ToDictionary(m => m.Id, m => m.Title);
        var cases = input["cases"]!.AsArray();
        return new(
            [.. input["selection"]!.AsArray().Select(id => titles[id!.GetValue<string>()])],
            cases.Count, input["scenarios"]!.AsArray().Count, rows.Any(t => t.Before), rows.Any(t => t.After),
            [.. cases.SelectMany(c => c!["measures"]!.AsArray().Where(m => m!["refused"] is not null)
                .Select(m => new PriceRefusal(titles[m!["id"]!.GetValue<string>()], c["title"]!.GetValue<string>(), m["refused"]!["message"]!.GetValue<string>())))]);
    }

    public void Delete(string workspaceId, string user) => workspaces.Delete(workspaceId, user);

    IReadOnlyList<EffectiveTrade> Rows(WorkspaceState state) => TradeOrder.Ordered(state, register, null, []);

    /// <summary>The user's own workspace of the pricer, as it stands; another's, or none of that id, is refused.</summary>
    public WorkspaceState Owned(string workspaceId, string user)
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
