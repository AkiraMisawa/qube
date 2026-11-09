using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Workbench.Composition.Application;
using Workbench.Composition.Application.Workspaces;

namespace Workbench.Samples.Portfolio;

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
    int Changes);

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
public sealed class PortfolioWorkspaces(WorkspaceService workspaces, IDataStore store, RunService runs, MeasureRegister register, PortfolioOptions? options = null)
{
    readonly int caseCap = (options ?? new PortfolioOptions()).CaseCap;

    // Effective rows per immutable state: a page request at the same revision does not materialise again.
    readonly ConditionalWeakTable<WorkspaceState, IReadOnlyList<EffectiveTrade>> materialised = new();
    readonly ConditionalWeakTable<WorkspaceState, ConcurrentDictionary<string, IReadOnlyList<EffectiveTrade>>> sorted = new();
    readonly ConditionalWeakTable<WorkspaceState, ConcurrentDictionary<string, IReadOnlyList<EffectiveTrade>>> grouped = new();

    public static readonly IReadOnlyList<string> SortableColumns =
        ["tradeId", "isNew", "product", "currency", "notional", "maturity", "fixedRate", "schedule", "lastFixing", "officialPv", "csa", "curve", "imR", "imP", "before", "after"];

    /// <summary>A new workspace, on its owner's last selection of measures (ADR-0010 §10, DEFAULT).</summary>
    public WorkspaceState Create(string user, string registration, DateOnly valueDate, int existingCount)
    {
        var last = LastSelection(user);
        var created = workspaces.Create(user, PortfolioEditRules.App, Context(registration, valueDate, existingCount), new PortfolioSourceVersion(registration, valueDate, existingCount));
        return last is null ? created : SetMeasures(created.WorkspaceId, created.Revision, user, last);
    }

    /// <summary>Sets the measures Price runs (`measures cva fva`). An unknown one, or none, is refused by name.</summary>
    public WorkspaceState SetMeasures(string workspaceId, long revision, string user, IReadOnlyList<string> measures)
    {
        var state = Owned(workspaceId, user);
        var selection = register.Checked(measures);
        return workspaces.Set(workspaceId, revision, user, PortfolioEditRules.Target(Source(state), Entity.Measures, Entity.MeasuresField),
            new JsonArray([.. selection.Select(id => JsonValue.Create(id))]));
    }

    // The selection the owner set last, in any of their workspaces; none while they have only taken the default.
    IReadOnlyList<string>? LastSelection(string user) =>
        workspaces.OwnedBy(user).Where(w => w.App == PortfolioEditRules.App)
            .SelectMany(w => w.Log.Where(e => e.Kind == EditKind.Set && e.Target?.Entity == Entity.Measures).Select(e => (e.At, e.Seq, e.Value)))
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
            PortfolioCases.All(raw), caseCap, DataEdits(raw).Count());
    }

    // The edits to the portfolio's data in any layer, as the Changes tab counts them: the selection and the settings
    // are the page's, shown on their own.
    static IEnumerable<Override> DataEdits(WorkspaceState raw) =>
        raw.Overrides.Values.Concat(raw.Conflicts.Values.Select(c => c.Override)).Concat(raw.Lost.Values.Select(l => l.Override))
            .Where(o => o.Target.Scope != PortfolioEditRules.WorkspaceScope);

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
    /// Sets a change's value as written (the Changes tab, and <c>set</c>): read as its cell reads a value and checked as the
    /// grid checks it typed, on the reading it is a change of, the Base's or a case's, in that case's layer.
    /// </summary>
    public WorkspaceState SetText(string workspaceId, long revision, string user, string entity, string field, string text, string? caseId = null, RunOrigin? origin = null)
    {
        var raw = Owned(workspaceId, user);
        var layer = caseId is null ? null : Changeable(raw, caseId);
        var reading = PortfolioCases.Reading(raw, caseId);
        var cell = PortfolioCells.Of(reading, register, entity, field);
        var value = PortfolioCells.Read(cell, text);
        Check(reading, entity, field, value);
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
        var keys = sorts.Select(sort =>
        {
            if (!SortableColumns.Contains(sort.Column))
                throw new WorkspaceRefused("UnknownColumn", $"Rows cannot be sorted by {sort.Column}.");
            var descending = sort.Direction switch
            {
                "ascending" => false,
                "descending" => true,
                _ => throw new WorkspaceRefused("InvalidSort", $"Sort direction {sort.Direction} is not ascending or descending."),
            };
            return (sort.Column, Descending: descending);
        }).ToList();
        // A stable sort, kept per state and sort while the state is alive: scrolling asks for many pages of one order.
        var rows = grid is null ? Rows(state) : grouped.GetOrCreateValue(state).GetOrAdd(grid, g => [.. Rows(state).Where(t => t.IsNew == (g == Entity.NewGrid))]);
        var all = keys.Count == 0 ? rows : sorted.GetOrCreateValue(state).GetOrAdd($"{grid}|{string.Join(",", keys.Select(k => $"{k.Column}:{k.Descending}"))}", _ =>
            [.. rows.Order(Comparer<EffectiveTrade>.Create((a, b) =>
            {
                foreach (var (column, descending) in keys)
                    if (Compare(Key(a, column), Key(b, column), descending) is var c && c != 0) return c;
                return 0;
            }))]);
        // Unsorted, the order is the source's and moves only with a new version; sorted, any edit may move it.
        var token = sorts.Count == 0 ? state.Source.Version : $"{state.Source.Version}/r{state.Revision}";
        // The modules' cells are read for the page alone, each time: a result such as PV01 arrives with no edit.
        var page = start >= all.Count ? [] : register.WithCells(state, [.. all.Skip(start).Take(count)]);
        return new(page, start, all.Count, token, state.Revision);
    }

    public WorkspaceState Edit(string workspaceId, long revision, string user, string entity, string field, JsonNode? value)
    {
        var state = PortfolioCases.Base(Owned(workspaceId, user));
        Check(state, entity, field, value);
        return workspaces.Set(workspaceId, revision, user, PortfolioEditRules.Target(Source(state), entity, field), value);
    }

    // What a typed cell is checked for, on the reading it is typed into: whether it is editable, and its type.
    void Check(WorkspaceState reading, string entity, string field, JsonNode? value)
    {
        if (register.ColumnOf(field) is { } column) CheckCell(reading, entity, field, column, value);
        else
        {
            if (!Editable(entity, field))
                throw new WorkspaceRefused("NotEditable", $"{entity}.{field} is not editable in this workspace.");
            Validate(reading, entity, field, value);
        }
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
        foreach (var (_, value) in read) Check(baseline, cell.Entity, cell.Field, value);
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
    /// <c>set &lt;rows&gt; &lt;column&gt; &lt;value&gt;</c> (ADR-0010 §9): the edit a typed cell makes, in the Common layer, on
    /// each row named, without making a case. Each cell is named as <c>vary</c> names it, and the value read and checked
    /// on each as the grid checks it typed, before any is set: one refused sets none.
    /// </summary>
    public WorkspaceState SetCells(string workspaceId, long revision, string user, IReadOnlyList<string> rows, string column, string text, RunOrigin? origin = null)
    {
        if (rows.Count == 0 || rows.Any(r => r.Trim().Length == 0) || column.Trim().Length == 0 || text.Trim().Length == 0)
            throw new WorkspaceRefused("UnknownCell", "set names one row or more, separated by commas, then a column and a value, such as set CSA-A threshold 5mm.");
        var raw = Owned(workspaceId, user);
        var baseline = PortfolioCases.Base(raw);
        var source = Source(raw);
        var cells = rows.Select(row => PortfolioCells.Resolve(baseline, register, row, column)).ToList();
        if (cells.GroupBy(c => c.Entity).FirstOrDefault(g => g.Count() > 1) is { } twice)
            throw new WorkspaceRefused("UnknownCell", $"set names {twice.First().Row} twice; name each row once.");
        var changes = cells.Select(cell =>
        {
            var value = PortfolioCells.Read(cell, text.Trim());
            Check(baseline, cell.Entity, cell.Field, value);
            return (WorkspaceChange)new WorkspaceChange.Set(PortfolioEditRules.Target(source, cell.Entity, cell.Field), value);
        }).ToList();
        return workspaces.Batch(workspaceId, revision, user, changes, origin);
    }

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

    // A module's column is typed where its cell on the trade says it may be, as the page shows it: PV Adj is read-only
    // while Margin is entered (ADR-0010 §6), and refused then with the cell's own note.
    void CheckCell(WorkspaceState state, string entity, string field, (IMeasureModule Module, TradeColumn Column) owner, JsonNode? value)
    {
        var (kind, id) = Entity.Split(entity);
        var title = owner.Column.Title;
        var trade = kind == Entity.Trade ? Rows(state).FirstOrDefault(t => t.TradeId == id) : null;
        if (trade is null) throw new WorkspaceRefused("NotEditable", $"{title} is a column of a trade, and {entity} is not one in this workspace.");
        if (register.Selected(state).All(m => m != owner.Module))
            throw new WorkspaceRefused("NotEditable", $"{title} shows while one of {owner.Module.Id}'s measures is selected.");
        var cell = register.WithCells(state, [trade])[0].Cells?.GetValueOrDefault(field);
        if (cell is null) throw new WorkspaceRefused("NotEditable", $"{trade.TradeId} has no {title}.");
        if (!cell.Editable)
            throw new WorkspaceRefused("NotEditable", cell.Note is null ? $"{title} of {trade.TradeId} is read-only." : $"{title} of {trade.TradeId} is read-only: {cell.Note}");
        if (owner.Module.Check(owner.Column.Id, value) is { } problem)
            throw new WorkspaceRefused("InvalidValue", problem, [PortfolioEditRules.Target(Source(state), entity, field)]);
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

    IReadOnlyList<EffectiveTrade> Rows(WorkspaceState state) => materialised.GetValue(state, s => register.WithMembership(s, PortfolioMaterialiser.Trades(s)));

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

    static bool Editable(string entity, string field) => (Entity.Split(entity).Kind, field) switch
    {
        (Entity.Trade, var f) => PortfolioMaterialiser.EditableFields.Contains(f),
        (Entity.Csa, var f) => PortfolioMaterialiser.CsaFields.Contains(f),
        // The CVA recovery is the netting set's since ADR-0010 §8; one set on the counterparty before still applies.
        (Entity.Netting, "cvaRecovery") => true,
        // B or A for every row of a grid at once (ADR-0010 §6).
        (Entity.Grid, "before" or "after") => Entity.Split(entity).Id is Entity.NewGrid or Entity.ExistingGrid,
        // The workspace's display currency (ADR-0010 §1).
        (Entity.Settings, Entity.DisplayCurrency) => entity == Entity.WorkspaceSettings,
        _ => false,
    };

    // Type checks only: the meaning (eligibility, availability) is the materialiser's, and shows on the row.
    static void Validate(WorkspaceState state, string entity, string field, JsonNode? value)
    {
        string? problem = field switch
        {
            "notional" => value is JsonValue v && v.TryGetValue<decimal>(out var n) && n != 0 ? null : "A notional is a non-zero number.",
            "fixedRate" => value is null || value is JsonValue r && r.TryGetValue<decimal>(out _) ? null : "A fixed rate is a number or empty.",
            "cvaRecovery" => value is null || value is JsonValue c && c.TryGetValue<decimal>(out var rec) && rec is >= 0 and < 1 ? null : "A CVA recovery is empty or from 0 up to 1.",
            "before" or "after" => value is JsonValue b && b.TryGetValue<bool>(out _) ? null : "Before and After are true or false.",
            "schedule" => value is JsonValue s && s.TryGetValue<string>(out var t) && t is "3M" or "6M" ? null : "A schedule is 3M or 6M.",
            "csa" or "curve" or "fvaCurve" => value is JsonValue x && x.TryGetValue<string>(out var id) && id.Length > 0 ? null : $"A {field} is an identifier.",
            // Empty is no IM CSA of that direction (ADR-0012 §2).
            "imR" or "imP" => value is null || value is JsonValue im && im.TryGetValue<string>(out var imId) && imId.Length > 0 ? null : $"{(field == "imR" ? "IM-R" : "IM-P")} is an IM CSA's identifier, or empty for none.",
            "frequency" => value is JsonValue f && f.TryGetValue<string>(out var often) && PortfolioCatalog.Frequencies.Contains(often)
                ? null : $"A frequency is {string.Join(", ", PortfolioCatalog.Frequencies)}.",
            "exchange" or "oneWay" => value is JsonValue e && e.TryGetValue<bool>(out _) ? null : $"{field} is true or false.",
            "counterpartyThreshold" or "counterpartyMta" or "selfThreshold" or "selfMta" =>
                value is JsonValue a && a.TryGetValue<decimal>(out var amount) && amount >= 0 ? null : "A threshold or MTA is an amount of 0 or more.",
            "currency" or "counterpartyCurrency" or "selfCurrency" or Entity.DisplayCurrency => value is JsonValue c3 && c3.TryGetValue<string>(out var code) && PortfolioMarket.Currencies.Contains(code)
                ? null : $"A currency is {string.Join(", ", PortfolioMarket.Currencies)}.",
            _ => null,
        };
        if (problem is not null) throw new WorkspaceRefused("InvalidValue", problem, [PortfolioEditRules.Target(Source(state), entity, field)]);
    }

    // A field holds a Common edit and one per case at once; the layer says which (ADR-0010 §3).
    static EditTarget Held(WorkspaceState state, string entity, string field, string? layer) =>
        state.Overrides.Keys.Concat(state.Conflicts.Keys).Concat(state.Lost.Keys).FirstOrDefault(t => t.Entity == entity && t.Field == field && t.Layer == layer)
        ?? throw new WorkspaceRefused("NotOverridden", $"{entity}.{field} has no edit{(layer is null ? "" : $" in {layer}")}.");

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
