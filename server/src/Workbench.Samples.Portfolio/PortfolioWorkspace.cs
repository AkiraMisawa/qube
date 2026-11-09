using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Workbench.Composition.Application.Workspaces;

namespace Workbench.Samples.Portfolio;

/// <summary>Who decided a field's value (docs/04): the source, an automatic rule, or the user.</summary>
public enum Ownership { Source, Automatic, User }

/// <summary>A trade as the workspace shows it and Price would submit it: source, rules and overrides applied.</summary>
public sealed record EffectiveTrade(
    string TradeId,
    bool IsNew,
    string Product,
    string Currency,
    decimal Notional,
    string Maturity,
    decimal? FixedRate,
    string? Schedule,
    decimal? LastFixing,
    decimal? OfficialPv,
    string Csa,
    Ownership CsaOwner,
    string Curve,
    Ownership CurveOwner,
    bool Before,
    bool After,
    IReadOnlyList<string> Issues,
    IReadOnlyList<string> Held,
    IReadOnlyList<string> Overridden,
    // The source's terms SA-CCR's decomposition reads (ADR-0013 §3), read-only: the start, the direction, and an FX
    // forward's other leg. Null where the source gives none.
    string? Start = null,
    string? Direction = null,
    string? CounterCurrency = null,
    decimal? CounterNotional = null,
    // Each declared measure's membership (ADR-0010 §10), set by the register; none where no measure declares one.
    IReadOnlyDictionary<string, Membership>? Membership = null,
    // The fields whose value differs from Original, with Original's value (ADR-0010 §2); none where nothing differs.
    IReadOnlyDictionary<string, JsonNode?>? Original = null,
    // The cells of the selected modules' trade columns, by field (ADR-0010 §10), such as XVA's PV01; none where no
    // module adds a column to the trade.
    IReadOnlyDictionary<string, TradeCell>? Cells = null,
    // The IM CSAs the trade is held under (ADR-0012 §2), assigned as its CSA is: the source's, the counterparty's for a
    // new trade, or the user's. Empty is none: the trade is out of that direction's scopes.
    string? ImR = null,
    Ownership ImROwner = Ownership.Source,
    string? ImP = null,
    Ownership ImPOwner = Ownership.Source);

/// <summary>
/// A CSA of the agreement with its effective terms (ADR-0010 §8): the source's, or the user's where <c>Overridden</c>
/// names the field. <c>Membership</c> is each measure rule's standing on it, such as FVA's Imperfect (§5).
/// </summary>
public sealed record EffectiveCsa(
    string CsaId, string Type, string Currency, string Curve, Ownership CurveOwner, string FvaCurve, string Frequency, bool Exchange, bool OneWay,
    decimal CounterpartyThreshold, decimal CounterpartyMta, string CounterpartyCurrency,
    decimal SelfThreshold, decimal SelfMta, string SelfCurrency,
    IReadOnlyList<string> Overridden,
    IReadOnlyDictionary<string, Membership>? Membership = null,
    IReadOnlyDictionary<string, JsonNode?>? Original = null);

/// <summary>
/// The counterparty's credit inputs: the netting set's CVA recovery, and the CDS chosen with its curve, or why there is
/// none (ADR-0010 §8's note of 2026-11-09). An empty CVA recovery stays empty; analytics uses the curve's recovery (XV15).
/// <c>Original</c> holds each field that differs from the source's, the CVA recovery's and the choice's, with the source's value.
/// </summary>
public sealed record EffectiveCredit(decimal? CvaRecovery, Ownership CvaRecoveryOwner, CdsChoice Choice, EffectiveCds? Cds, string? Missing,
    IReadOnlyDictionary<string, JsonNode?>? Original = null)
{
    /// <summary>The CDS recovery an empty CVA recovery takes: the chosen curve's; none without a curve.</summary>
    public decimal? CdsRecovery => Cds?.Recovery;
}

/// <summary>The portfolio's edit kinds, scopes and conflict groups (ADR-0003 §7), registered with the kernel.</summary>
public sealed class PortfolioEditRules : IEditRules
{
    /// <summary>The stored app id. It was XVA's before the portfolio module was cut out of it (ADR-0010 §10), and
    /// is kept so that a workspace saved before still opens.</summary>
    public const string App = "xva";

    /// <summary>The scope of what belongs to the workspace itself rather than to a counterparty or a new trade.</summary>
    public const string WorkspaceScope = "ws";

    public string KindOf(EditTarget target) => (Entity.Split(target.Entity).Kind, target.Field) switch
    {
        (Entity.Trade or Entity.Grid, "before" or "after") => "xva.membership",
        (Entity.Trade, "csa" or "curve" or "imR" or "imP") => "xva.assignment",
        (Entity.Trade, _) => "xva.trade",
        (Entity.Csa, _) => "xva.csa",
        (Entity.Netting, _) => "portfolio.netting",
        (Entity.Credit or Entity.Cds, _) => "xva.credit",
        (Entity.Selection, _) => "portfolio.selection",
        (Entity.Settings, _) => "portfolio.settings",
        (Entity.Case, _) => "portfolio.case",
        (PortfolioScenarios.ShiftKind or PortfolioScenarios.ScenariosKind, _) => "portfolio.scenario",
        _ => throw new ArgumentException($"No portfolio edit kind for {target.Entity}.{target.Field}."),
    };

    // A fixed rate means something else once its schedule changes, so the two are compared together.
    public IReadOnlyList<string> ConflictGroup(EditTarget target) =>
        target.Field is "fixedRate" or "schedule" ? ["fixedRate", "schedule"] : [target.Field];

    /// <summary>
    /// The scope of an edit, decided here and never taken from the client: a new trade's own content belongs to
    /// that trade and survives a counterparty change; everything else belongs to the counterparty registration.
    /// </summary>
    public static EditTarget Target(PortfolioSourceVersion source, string entity, string field, string? layer = null)
    {
        var (kind, id) = Entity.Split(entity);
        // A case's change is the counterparty's, as the case is (ADR-0010 §3's note of 2026-11-08): a counterparty change
        // drops the case with every change it holds, a new trade's included.
        if (layer is not null) return new EditTarget($"cp:{source.Registration.Key}", entity, field, layer);
        // The selection of measures, the settings and the scenarios are the workspace's, whichever counterparty it is on:
        // market data is no counterparty's (ADR-0010 §4's note of 2026-11-09).
        if (kind is Entity.Selection or Entity.Settings or PortfolioScenarios.ShiftKind or PortfolioScenarios.ScenariosKind) return new EditTarget(WorkspaceScope, entity, field);
        var tradeContent = kind == Entity.Trade && field is not ("before" or "after" or "csa" or "curve" or "imR" or "imP");
        var isNew = kind == Entity.Trade && source.Trade(id)?.IsNew == true;
        return new EditTarget(tradeContent && isNew ? $"new:{id}" : $"cp:{source.Registration.Key}", entity, field);
    }
}

public static class PortfolioMaterialiser
{
    public static IReadOnlyList<string> EditableFields { get; } = ["notional", "fixedRate", "schedule", "csa", "curve", "imR", "imP", "before", "after"];

    /// <summary>The CSA terms the user may set (ADR-0010 §8). Its type is the source's.</summary>
    public static IReadOnlyList<string> CsaFields { get; } =
        ["currency", "curve", "fvaCurve", "frequency", "exchange", "oneWay",
         "counterpartyThreshold", "counterpartyMta", "counterpartyCurrency", "selfThreshold", "selfMta", "selfCurrency"];

    public static IReadOnlyList<EffectiveTrade> Trades(WorkspaceState state)
    {
        PortfolioCases.Unlayered(state);
        var source = (PortfolioSourceVersion)state.Source;
        var overrides = state.Overrides.Values.ToDictionary(o => (o.Target.Entity, o.Target.Field));
        var held = state.Conflicts.Keys.Select(t => (t, "conflict")).Concat(state.Lost.Keys.Select(t => (t, "lost")))
            .ToLookup(x => x.t.Entity, x => $"{x.t.Field}: {x.Item2}");
        // Most rows carry no edit of their own: they are the source under the rules, the CSA terms and B or A set for a
        // whole grid, which are computed once per source version and set of those edits. Only the rows the user
        // touched, or that hold an edit back, are worked out again.
        var shared = overrides.Where(o => Entity.Split(o.Key.Entity).Kind is Entity.Csa or Entity.Grid)
            .OrderBy(o => o.Key.Entity, StringComparer.Ordinal).ThenBy(o => o.Key.Field, StringComparer.Ordinal)
            .ToDictionary(o => o.Key, o => o.Value);
        var fingerprint = string.Join("|", shared.Select(o => $"{o.Key.Entity}.{o.Key.Field}={o.Value.Value?.ToJsonString()}#{o.Value.Seq}"));
        // Original (ADR-0010 §2): the source under the rules with no edit at all, in the same order as every row list here.
        var original = Originals.GetValue(source, s => [.. s.Existing.Concat(s.New).Select(t => Trade(s, t, NoEdits, Nothing))]);
        var untouched = Untouched.GetOrCreateValue(source).GetOrAdd(fingerprint, _ =>
            [.. source.Existing.Concat(source.New).Select((t, i) => Marked(Trade(source, t, shared, Nothing), original[i]))]);
        var touched = overrides.Keys.Select(k => k.Entity).Concat(held.Select(h => h.Key))
            .Where(e => Entity.Split(e).Kind == Entity.Trade).ToHashSet();
        if (touched.Count == 0) return untouched;
        var rows = (EffectiveTrade[])untouched.Clone();
        for (var i = 0; i < rows.Length; i++)
            if (touched.Contains(Entity.Of(Entity.Trade, rows[i].TradeId)))
                rows[i] = Marked(Trade(source, source.Trade(rows[i].TradeId)!, overrides, held), original[i]);
        return rows;
    }

    /// <summary>The trade fields Original is compared on. B and A are shown by their boxes and never marked (ADR-0010 §2).</summary>
    public static IReadOnlyList<string> ComparedFields { get; } = ["notional", "fixedRate", "schedule", "csa", "curve", "imR", "imP"];

    // The test is the value, not the edit: a value edited back to Original is not marked, and one a rule moved is.
    static EffectiveTrade Marked(EffectiveTrade row, EffectiveTrade original)
    {
        static JsonNode? Of(EffectiveTrade t, string field) => field switch
        {
            "notional" => t.Notional,
            "fixedRate" => t.FixedRate,
            "schedule" => t.Schedule,
            "csa" => t.Csa,
            "curve" => t.Curve,
            "imR" => t.ImR,
            "imP" => t.ImP,
            _ => throw new ArgumentException(field),
        };
        var differs = ComparedFields.Where(f => !Same(Of(row, f), Of(original, f))).ToDictionary(f => f, f => Of(original, f));
        return differs.Count == 0 ? row : row with { Original = differs };
    }

    /// <summary>Two values are the same when they say the same thing: numbers by value (1.0 is 1), the rest as JSON.</summary>
    public static bool Same(JsonNode? a, JsonNode? b) =>
        a is JsonValue x && b is JsonValue y && x.TryGetValue<decimal>(out var m) && y.TryGetValue<decimal>(out var n) ? m == n : JsonNode.DeepEquals(a, b);

    static readonly ConditionalWeakTable<PortfolioSourceVersion, ConcurrentDictionary<string, EffectiveTrade[]>> Untouched = new();
    static readonly ConditionalWeakTable<PortfolioSourceVersion, EffectiveTrade[]> Originals = new();
    static readonly IReadOnlyDictionary<(string, string), Override> NoEdits = new Dictionary<(string, string), Override>();
    static readonly ILookup<string, string> Nothing = Array.Empty<(string, string)>().ToLookup(x => x.Item1, x => x.Item2);

    /// <summary>The entities Price would read: trades in Before or After, the CSAs they use, the netting set's and
    /// the counterparty's credit inputs and the selection of measures.</summary>
    public static Func<string, bool> PricedEntities(WorkspaceState state)
    {
        var source = (PortfolioSourceVersion)state.Source;
        var trades = Trades(state).Where(t => t.Before || t.After).ToList();
        var entities = trades.Select(t => Entity.Of(Entity.Trade, t.TradeId))
            .Concat(trades.Select(t => Entity.Of(Entity.Csa, t.Csa)))
            .Append(Entity.Of(Entity.Netting, source.Agreement.NettingSetId))
            .Append(Entity.Of(Entity.Grid, Entity.NewGrid))
            .Append(Entity.Of(Entity.Grid, Entity.ExistingGrid))
            .Append(Entity.Of(Entity.Credit, source.Registration.Key))
            .Concat(Credit(state).Cds is { } cds ? [Entity.Of(Entity.Cds, cds.CurveId)] : Array.Empty<string>())
            .Append(Entity.Measures)
            .Append(Entity.WorkspaceSettings)
            .ToHashSet();
        // A module's settings are read by its request whenever one of its measures is priced (ADR-0010 §8).
        return e => entities.Contains(e) || Entity.Split(e).Kind == Entity.Settings;
    }

    /// <summary>The measures Price runs: the user's selection, else every registered measure (ADR-0010 §10).</summary>
    public static IReadOnlyList<string> SelectedMeasures(WorkspaceState state, MeasureRegister register) =>
        state.Overrides.Values.SingleOrDefault(o => o.Target.Entity == Entity.Measures && o.Target.Field == Entity.MeasuresField) is { Value: JsonArray chosen }
            ? [.. register.Ids.Where(id => chosen.Any(c => c!.GetValue<string>() == id))]
            : register.Ids;

    /// <summary>The currency the figures are shown in (ADR-0010 §1): the user's choice, else the profile's.</summary>
    public static string DisplayCurrency(WorkspaceState state) =>
        state.Overrides.Values.SingleOrDefault(o => o.Target.Entity == Entity.WorkspaceSettings && o.Target.Field == Entity.DisplayCurrency) is { Value: { } chosen }
            ? chosen.GetValue<string>()
            : PortfolioCatalog.Profile.DisplayCurrency;

    public static IReadOnlyList<EffectiveCsa> Csas(WorkspaceState state)
    {
        PortfolioCases.Unlayered(state);
        var source = (PortfolioSourceVersion)state.Source;
        return [.. source.Agreement.Csas.Select(c =>
        {
            var entity = Entity.Of(Entity.Csa, c.CsaId);
            var set = state.Overrides.Values.Where(o => o.Target.Entity == entity).ToDictionary(o => o.Target.Field, o => o.Value);
            T Term<T>(string field, T value) => set.TryGetValue(field, out var v) ? v!.GetValue<T>() : value;
            var effective = new EffectiveCsa(c.CsaId, c.Type, Term("currency", c.Currency), Term("curve", c.Curve), set.ContainsKey("curve") ? Ownership.User : Ownership.Source,
                Term("fvaCurve", c.FvaCurve), Term("frequency", c.Frequency), Term("exchange", c.Exchange), Term("oneWay", c.OneWay),
                Term("counterpartyThreshold", c.CounterpartyThreshold), Term("counterpartyMta", c.CounterpartyMta), Term("counterpartyCurrency", c.CounterpartyCurrency),
                Term("selfThreshold", c.SelfThreshold), Term("selfMta", c.SelfMta), Term("selfCurrency", c.SelfCurrency),
                [.. CsaFields.Where(set.ContainsKey)]);
            // Original is the source's term; a term set back to it is not marked (ADR-0010 §2).
            var differs = CsaFields.Where(f => set.ContainsKey(f) && !Same(set[f], source.Read(entity, f)!.Value))
                .ToDictionary(f => f, f => source.Read(entity, f)!.Value);
            return differs.Count == 0 ? effective : effective with { Original = differs };
        })];
    }

    /// <summary>
    /// The netting set's CVA recovery (ADR-0010 §8), and the counterparty's CDS: the source's choice or the user's, and the
    /// curve it names with the user's spreads on that curve (§8's note of 2026-11-09). A recovery the user set on the
    /// counterparty before it moved to the netting set still applies until one is set on the netting set.
    /// </summary>
    public static EffectiveCredit Credit(WorkspaceState state)
    {
        PortfolioCases.Unlayered(state);
        var source = (PortfolioSourceVersion)state.Source;
        var credit = Entity.Of(Entity.Credit, source.Registration.Key);
        Override? Set(string entity, string field) => state.Overrides.Values.SingleOrDefault(x => x.Target.Entity == entity && x.Target.Field == field);
        var original = new Dictionary<string, JsonNode?>();
        var o = Set(Entity.Of(Entity.Netting, source.Agreement.NettingSetId), "cvaRecovery") ?? Set(credit, "cvaRecovery");
        var recovery = o is null ? source.CvaRecovery : o.Value?.GetValue<decimal>();
        if (o is not null && recovery != source.CvaRecovery) original["cvaRecovery"] = source.CvaRecovery;
        // Each key of the choice is the user's where set, else the source's; one set back to the source's is not marked.
        string? ChoiceKey(string field, string? sourced)
        {
            if (Set(credit, field) is not { } edit) return sourced;
            var chosen = edit.Value?.GetValue<string>();
            if (chosen != sourced) original[field] = sourced;
            return chosen;
        }
        var given = source.Cds;
        var choice = new CdsChoice(ChoiceKey("cdsType", given.Type), ChoiceKey("cdsReference", given.Reference), ChoiceKey("proxySector", given.Sector),
            ChoiceKey("proxyRegion", given.Region), ChoiceKey("proxyRating", given.Rating));
        var (curve, missing) = CdsCatalog.Resolve(choice, source.Registration.Key);
        return new(recovery, o is null ? Ownership.Source : Ownership.User, choice, curve is null ? null : CurveOf(state, curve), missing,
            original.Count == 0 ? null : original);
    }

    /// <summary>A curve with the user's spreads, each edit kept on the curve it was made on (ADR-0010 §8's note of 2026-11-09).</summary>
    public static EffectiveCds CurveOf(WorkspaceState state, CdsCurve curve)
    {
        var entity = Entity.Of(Entity.Cds, curve.Id);
        var set = state.Overrides.Values.Where(x => x.Target.Entity == entity && curve.Spreads.ContainsKey(x.Target.Field))
            .ToDictionary(x => x.Target.Field, x => x.Value?.GetValue<decimal>()
                ?? throw new InvalidOperationException($"The spread {curve.Id} {x.Target.Field} was set to nothing, which no edit may set."));
        var spreads = CdsCatalog.Tenors.ToDictionary(t => t, t => set.TryGetValue(t, out var typed) ? typed : curve.Spreads[t]);
        var differs = CdsCatalog.Tenors.Where(t => spreads[t] != curve.Spreads[t]).ToDictionary(t => t, t => (JsonNode?)curve.Spreads[t]);
        return new(curve.Id, curve.Type, curve.Ticker, curve.Currency, curve.Seniority, curve.DocClause, curve.Name, curve.Reference, curve.Recovery, spreads,
            [.. CdsCatalog.Tenors.Where(set.ContainsKey)], curve.Sector, curve.Region, curve.Rating, differs.Count == 0 ? null : differs);
    }

    static EffectiveTrade Trade(PortfolioSourceVersion source, SourceTrade t, IReadOnlyDictionary<(string, string), Override> overrides, ILookup<string, string> held)
    {
        var entity = Entity.Of(Entity.Trade, t.TradeId);
        bool Has(string field, out JsonNode? value)
        {
            var found = overrides.TryGetValue((entity, field), out var o);
            value = o?.Value;
            return found;
        }
        // B or A: the latest of the row's own edit and its grid's (ADR-0010 §6). A grid's edit sets every row; a row's
        // own edit made after it wins over it.
        bool Side(string field, bool original)
        {
            var own = overrides.GetValueOrDefault((entity, field));
            var grid = overrides.GetValueOrDefault((Entity.Of(Entity.Grid, t.IsNew ? Entity.NewGrid : Entity.ExistingGrid), field));
            var latest = own is null ? grid : grid is null || own.Seq > grid.Seq ? own : grid;
            return latest is null ? original : latest.Value!.GetValue<bool>();
        }
        var agreement = source.Agreement;
        var issues = new List<string>();

        string csa;
        Ownership csaOwner;
        if (Has("csa", out var csaValue)) (csa, csaOwner) = (csaValue!.GetValue<string>(), Ownership.User);
        else if (t.IsNew) (csa, csaOwner) = (agreement.MainCsaId ?? agreement.NoCsa, Ownership.Automatic);
        else (csa, csaOwner) = (t.Csa ?? throw new InvalidOperationException($"Existing trade {t.TradeId} has no CSA."), Ownership.Source);

        var eligible = csa == agreement.NoCsa || agreement.Csas.Any(c => c.CsaId == csa);
        if (!eligible) issues.Add($"CSA {csa} is not eligible under {agreement.MaId}.");

        string curve;
        Ownership curveOwner;
        if (Has("curve", out var curveValue)) (curve, curveOwner) = (curveValue!.GetValue<string>(), Ownership.User);
        else if (csa == agreement.NoCsa) (curve, curveOwner) = (PortfolioCatalog.Profile.UncollateralisedCurve, Ownership.Automatic);
        else if (source.CsaOf(csa) is { } c)
            (curve, curveOwner) = (overrides.TryGetValue((Entity.Of(Entity.Csa, c.CsaId), "curve"), out var csaCurve) ? csaCurve.Value!.GetValue<string>() : c.Curve, Ownership.Automatic);
        else (curve, curveOwner) = ("", Ownership.Automatic);
        if (!PortfolioCatalog.Curves.Contains(curve)) issues.Add(curve == "" ? "No discount curve: the CSA is not eligible." : $"Curve {curve} is not available.");

        // The IM CSAs, as the CSA (ADR-0012 §2): the user's, else a new trade's counterparty's, else the source's. Whether
        // one is the counterparty's is spot IM's to say, so it is no issue of the trade's and blocks no other measure.
        (string? Id, Ownership Owner) Im(string field, string direction, string? held) =>
            Has(field, out var set) ? (set?.GetValue<string>(), Ownership.User)
            : t.IsNew ? (agreement.ImCsaOf(direction)?.ImCsaId, Ownership.Automatic)
            : (held, Ownership.Source);
        var (imR, imROwner) = Im("imR", ImCsaTerms.Collect, t.ImR);
        var (imP, imPOwner) = Im("imP", ImCsaTerms.Post, t.ImP);

        return new(
            t.TradeId, t.IsNew, t.Product, t.Currency,
            Has("notional", out var n) ? n!.GetValue<decimal>() : t.Notional,
            t.Maturity.ToString("yyyy-MM-dd"),
            Has("fixedRate", out var r) ? r?.GetValue<decimal>() : t.FixedRate,
            Has("schedule", out var s) ? s?.GetValue<string>() : t.Schedule,
            t.LastFixing,
            t.OfficialPv,
            csa, csaOwner, curve, curveOwner,
            Side("before", !t.IsNew),
            Side("after", true),
            issues,
            [.. held[entity]],
            [.. EditableFields.Where(f => overrides.ContainsKey((entity, f)))],
            t.Start?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), t.Direction, t.CounterCurrency, t.CounterNotional,
            ImR: imR, ImROwner: imROwner, ImP: imP, ImPOwner: imPOwner);
    }
}
