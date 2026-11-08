using System.Collections.Concurrent;
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
    IReadOnlyList<string> Simulated);

/// <summary>An edit held back for the user: a conflict (with the source's new values) or a lost target.</summary>
public sealed record HeldEdit(string Scope, string Entity, string Field, JsonNode? Value, IReadOnlyDictionary<string, JsonNode?>? Source, bool Acknowledged);

/// <summary>A Run this workspace submitted (its latest twenty, newest first).</summary>
public sealed record RunLink(string RunId, long Revision);

/// <summary>How many edits a counterparty change would drop, by module edit kind, for the user to confirm.</summary>
public sealed record ScopeDrop(string Scope, int Count, IReadOnlyDictionary<string, int> ByKind);

/// <summary>
/// The portfolio workspace application service (ADR-0010 §10), shared by the measures that price it: what the
/// endpoints call. It decides scopes and sources; the generic
/// kernel stores and checks the edits. The user is passed in; who may act is decided before this is called.
/// </summary>
public sealed class PortfolioWorkspaces(WorkspaceService workspaces, IDataStore store, RunService runs, MeasureRegister register)
{
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
        var state = Owned(workspaceId, user);
        var source = Source(state);
        var links = runs.List(new RunQuery(user, 0, 20, workspaceId)).Runs
            .Select(r => new RunLink(r.RunId, r.Relations.WorkspaceRevision ?? throw new InvalidOperationException($"Run {r.RunId} names a workspace but no revision.")))
            .ToList();
        return new(state.WorkspaceId, state.Revision, state.Context, source.Version, source.Registration, source.Agreement,
            register.WithMembership(state, PortfolioMaterialiser.Csas(state)), PortfolioMaterialiser.Credit(state), Rows(state).Count, Rows(state).Count(t => t.IsNew), Rows(state).Count(t => t.Before), Rows(state).Count(t => t.After), state.Overrides.Keys.Count(t => t.Scope != PortfolioEditRules.WorkspaceScope),
            [.. state.Conflicts.Values.OrderBy(c => c.Override.Seq).Select(c => new HeldEdit(c.Override.Target.Scope, c.Override.Target.Entity, c.Override.Target.Field, c.Override.Value, c.Source, false))],
            [.. state.Lost.Values.OrderBy(l => l.Override.Seq).Select(l => new HeldEdit(l.Override.Target.Scope, l.Override.Target.Entity, l.Override.Target.Field, l.Override.Value, null, l.Acknowledged))],
            links, register.Measures, PortfolioMaterialiser.SelectedMeasures(state, register),
            [.. register.TradeColumns.Where(c => register.Selected(state).Any(m => m.Id == c.Module))], register.Inputs(state),
            register.WithCells(state, [.. Rows(state).Where(t => t.IsNew)]).Sum(t => t.Cells?.Values.Count(c => c.State == TradeCell.Pending) ?? 0),
            PortfolioMaterialiser.DisplayCurrency(state), PortfolioMarket.Currencies, [.. register.Selected(state).Where(m => m.Simulated).Select(m => m.Id)]);
    }

    /// <summary>Sets the currency the figures are shown in (ADR-0010 §1): the edit of <c>settings:workspace</c>
    /// <c>displayCurrency</c>. One the market cannot convert to is refused.</summary>
    public WorkspaceState SetDisplayCurrency(string workspaceId, long revision, string user, string currency) =>
        Edit(workspaceId, revision, user, Entity.WorkspaceSettings, Entity.DisplayCurrency, currency);

    /// <summary>Refresh (ADR-0010 §1): acquires the same counterparty's trades for the same value date again; edits carry
    /// or are held as for any new version.</summary>
    public (WorkspaceState State, CarrySummary Summary) Refresh(string workspaceId, long revision, string user) =>
        ChangeValueDate(workspaceId, revision, user, Source(Owned(workspaceId, user)).ValueDate);

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
        var state = Owned(workspaceId, user);
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
        var state = Owned(workspaceId, user);
        if (register.ColumnOf(field) is { } column) CheckCell(state, entity, field, column, value);
        else
        {
            if (!Editable(entity, field))
                throw new WorkspaceRefused("NotEditable", $"{entity}.{field} is not editable in this workspace.");
            Validate(state, entity, field, value);
        }
        return workspaces.Set(workspaceId, revision, user, PortfolioEditRules.Target(Source(state), entity, field), value);
    }

    /// <summary>
    /// Asks again for every failed result among the selected modules' cells on the workspace's trades, or on one trade
    /// (`pv01 retry`, and the failed cell's action, ADR-0010 §6). Answers how many were asked again.
    /// </summary>
    public int Retry(string workspaceId, string user, string? tradeId = null)
    {
        var state = Owned(workspaceId, user);
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

    public WorkspaceState Resolve(string workspaceId, long revision, string user, string entity, string field, string resolution)
    {
        var target = Held(Owned(workspaceId, user), entity, field);
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
        var held = state.InScope(scope);
        return new(scope, held.Count, held.Keys.GroupBy(rules.KindOf).ToDictionary(g => g.Key, g => g.Count()));
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
            ?? runs.Submit(PortfolioPrice.Submission(workspaces, store, register, workspaceId, revision, idempotencyKey, user));
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

    static EditTarget Held(WorkspaceState state, string entity, string field) =>
        state.Overrides.Keys.Concat(state.Conflicts.Keys).Concat(state.Lost.Keys).FirstOrDefault(t => t.Entity == entity && t.Field == field)
        ?? throw new WorkspaceRefused("NotOverridden", $"{entity}.{field} has no edit.");

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
