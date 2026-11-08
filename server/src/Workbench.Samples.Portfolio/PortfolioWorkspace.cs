using System.Collections.Concurrent;
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
    // Each declared measure's membership (ADR-0010 §10), set by the register; none where no measure declares one.
    IReadOnlyDictionary<string, Membership>? Membership = null);

/// <summary>
/// A CSA of the agreement with its effective terms (ADR-0010 §8): the source's, or the user's where <c>Overridden</c>
/// names the field. <c>Membership</c> is each measure rule's standing on it, such as FVA's Imperfect (§5).
/// </summary>
public sealed record EffectiveCsa(
    string CsaId, string Type, string Currency, string Curve, Ownership CurveOwner, string FvaCurve, string Frequency, bool Exchange, bool OneWay,
    decimal CounterpartyThreshold, decimal CounterpartyMta, string CounterpartyCurrency,
    decimal SelfThreshold, decimal SelfMta, string SelfCurrency,
    IReadOnlyList<string> Overridden,
    IReadOnlyDictionary<string, Membership>? Membership = null);

/// <summary>The netting set's credit inputs. An empty CVA recovery stays empty; analytics uses the CDS recovery.</summary>
public sealed record EffectiveCredit(decimal? CvaRecovery, Ownership CvaRecoveryOwner, decimal CdsRecovery);

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
        (Entity.Trade, "csa" or "curve") => "xva.assignment",
        (Entity.Trade, _) => "xva.trade",
        (Entity.Csa, _) => "xva.csa",
        (Entity.Netting, _) => "portfolio.netting",
        (Entity.Credit, _) => "xva.credit",
        (Entity.Selection, _) => "portfolio.selection",
        _ => throw new ArgumentException($"No portfolio edit kind for {target.Entity}.{target.Field}."),
    };

    // A fixed rate means something else once its schedule changes, so the two are compared together.
    public IReadOnlyList<string> ConflictGroup(EditTarget target) =>
        target.Field is "fixedRate" or "schedule" ? ["fixedRate", "schedule"] : [target.Field];

    /// <summary>
    /// The scope of an edit, decided here and never taken from the client: a new trade's own content belongs to
    /// that trade and survives a counterparty change; everything else belongs to the counterparty registration.
    /// </summary>
    public static EditTarget Target(PortfolioSourceVersion source, string entity, string field)
    {
        var (kind, id) = Entity.Split(entity);
        // The selection of measures is the workspace's, whichever counterparty it is on.
        if (kind == Entity.Selection) return new EditTarget(WorkspaceScope, entity, field);
        var tradeContent = kind == Entity.Trade && field is not ("before" or "after" or "csa" or "curve");
        var isNew = kind == Entity.Trade && source.Trade(id)?.IsNew == true;
        return new EditTarget(tradeContent && isNew ? $"new:{id}" : $"cp:{source.Registration.Key}", entity, field);
    }
}

public static class PortfolioMaterialiser
{
    public static IReadOnlyList<string> EditableFields { get; } = ["notional", "fixedRate", "schedule", "csa", "curve", "before", "after"];

    /// <summary>The CSA terms the user may set (ADR-0010 §8). Its type is the source's.</summary>
    public static IReadOnlyList<string> CsaFields { get; } =
        ["currency", "curve", "fvaCurve", "frequency", "exchange", "oneWay",
         "counterpartyThreshold", "counterpartyMta", "counterpartyCurrency", "selfThreshold", "selfMta", "selfCurrency"];

    public static IReadOnlyList<EffectiveTrade> Trades(WorkspaceState state)
    {
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
        var untouched = Untouched.GetOrCreateValue(source).GetOrAdd(fingerprint, _ =>
            [.. source.Existing.Concat(source.New).Select(t => Trade(source, t, shared, Nothing))]);
        var touched = overrides.Keys.Select(k => k.Entity).Concat(held.Select(h => h.Key))
            .Where(e => Entity.Split(e).Kind == Entity.Trade).ToHashSet();
        if (touched.Count == 0) return untouched;
        var rows = (EffectiveTrade[])untouched.Clone();
        for (var i = 0; i < rows.Length; i++)
            if (touched.Contains(Entity.Of(Entity.Trade, rows[i].TradeId)))
                rows[i] = Trade(source, source.Trade(rows[i].TradeId)!, overrides, held);
        return rows;
    }

    static readonly ConditionalWeakTable<PortfolioSourceVersion, ConcurrentDictionary<string, EffectiveTrade[]>> Untouched = new();
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
            .Append(Entity.Measures)
            .ToHashSet();
        return entities.Contains;
    }

    /// <summary>The measures Price runs: the user's selection, else every registered measure (ADR-0010 §10).</summary>
    public static IReadOnlyList<string> SelectedMeasures(WorkspaceState state, MeasureRegister register) =>
        state.Overrides.Values.SingleOrDefault(o => o.Target.Entity == Entity.Measures && o.Target.Field == Entity.MeasuresField) is { Value: JsonArray chosen }
            ? [.. register.Ids.Where(id => chosen.Any(c => c!.GetValue<string>() == id))]
            : register.Ids;

    public static IReadOnlyList<EffectiveCsa> Csas(WorkspaceState state)
    {
        var source = (PortfolioSourceVersion)state.Source;
        return [.. source.Agreement.Csas.Select(c =>
        {
            var entity = Entity.Of(Entity.Csa, c.CsaId);
            var set = state.Overrides.Values.Where(o => o.Target.Entity == entity).ToDictionary(o => o.Target.Field, o => o.Value);
            T Term<T>(string field, T value) => set.TryGetValue(field, out var v) ? v!.GetValue<T>() : value;
            return new EffectiveCsa(c.CsaId, c.Type, Term("currency", c.Currency), Term("curve", c.Curve), set.ContainsKey("curve") ? Ownership.User : Ownership.Source,
                Term("fvaCurve", c.FvaCurve), Term("frequency", c.Frequency), Term("exchange", c.Exchange), Term("oneWay", c.OneWay),
                Term("counterpartyThreshold", c.CounterpartyThreshold), Term("counterpartyMta", c.CounterpartyMta), Term("counterpartyCurrency", c.CounterpartyCurrency),
                Term("selfThreshold", c.SelfThreshold), Term("selfMta", c.SelfMta), Term("selfCurrency", c.SelfCurrency),
                [.. CsaFields.Where(set.ContainsKey)]);
        })];
    }

    /// <summary>
    /// The netting set's CVA recovery (ADR-0010 §8), and the counterparty's CDS recovery. A recovery the user set on the
    /// counterparty before it moved to the netting set still applies until one is set on the netting set.
    /// </summary>
    public static EffectiveCredit Credit(WorkspaceState state)
    {
        var source = (PortfolioSourceVersion)state.Source;
        Override? Set(string entity) => state.Overrides.Values.SingleOrDefault(x => x.Target.Entity == entity && x.Target.Field == "cvaRecovery");
        var o = Set(Entity.Of(Entity.Netting, source.Agreement.NettingSetId)) ?? Set(Entity.Of(Entity.Credit, source.Registration.Key));
        return o is null
            ? new(source.CvaRecovery, Ownership.Source, source.CdsRecovery)
            : new(o.Value?.GetValue<decimal>(), Ownership.User, source.CdsRecovery);
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
            [.. EditableFields.Where(f => overrides.ContainsKey((entity, f)))]);
    }
}
