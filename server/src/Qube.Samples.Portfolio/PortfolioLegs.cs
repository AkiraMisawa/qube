using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Qube.Composition.Application;
using Qube.Composition.Application.Workspaces;

namespace Qube.Samples.Portfolio;

// ADR-0016 note 49: a trade's PV legs and independent amounts, rows under the trade, and the trade's PV, a figure of
// its legs. No measure of the sample reads a leg: SA-CCR's replacement cost reads the official PV (ADR-0010 §6,
// ADR-0013), so a change here moves no figure Price answers.

/// <summary>
/// A PV leg as the workspace reads it: the source's with the user's edits, or one the workspace made
/// (<paramref name="Made"/>). A leg an overwritten PV made holds no notional.
/// </summary>
public sealed record EffectiveLeg(string LegId, string TradeId, string Currency, decimal Pv, decimal? Notional, bool Made);

/// <summary>An independent amount as the workspace reads it: the source's, while no edit has taken it out.</summary>
public sealed record EffectiveAmount(string AmountId, string TradeId, string Currency, string Start, string End, decimal Value);

public static class PortfolioLegs
{
    /// <summary>A trade's PV: the field of the trades that is a figure of its legs.</summary>
    public const string Pv = "pv";

    /// <summary>A trade's independent amount: the field of the trades that is a figure of its independent amounts.</summary>
    public const string IndependentAmount = "independentAmount";

    /// <summary>The fields of a leg an edit sets, as its cells are checked (<see cref="PortfolioChecks"/>).</summary>
    public static IReadOnlyList<string> EditableFields { get; } = ["currency", "pv", "notional"];

    // Every leg and amount of one reading, and those of each trade, read once: a state never changes.
    sealed class Standing(IReadOnlyList<EffectiveLeg> legs, IReadOnlyList<EffectiveAmount> amounts)
    {
        public IReadOnlyList<EffectiveLeg> Legs => legs;
        public IReadOnlyList<EffectiveAmount> Amounts => amounts;
        public ILookup<string, EffectiveLeg> LegsOf { get; } = legs.ToLookup(l => l.TradeId);
        public ILookup<string, EffectiveAmount> AmountsOf { get; } = amounts.ToLookup(a => a.TradeId);
    }

    static readonly ConditionalWeakTable<WorkspaceState, Standing> Read = new();
    // Most workspaces hold no edit of a leg: their legs are the source's, worked out once for the version.
    static readonly ConditionalWeakTable<PortfolioSourceVersion, Standing> Untouched = new();

    static Standing Of(WorkspaceState reading) => Read.GetValue(reading, ReadAll);

    static PortfolioSourceVersion Source(WorkspaceState state) => (PortfolioSourceVersion)state.Source;

    static bool Under(string entity) => entity.StartsWith($"{Entity.Leg}:", StringComparison.Ordinal) || entity.StartsWith($"{Entity.Amount}:", StringComparison.Ordinal);

    static Standing ReadAll(WorkspaceState reading)
    {
        PortfolioCases.Unlayered(reading);
        var source = Source(reading);
        var edits = reading.Overrides.Values.Where(o => Under(o.Target.Entity)).ToDictionary(o => (o.Target.Entity, o.Target.Field), o => o.Value);
        var made = Made(reading);
        if (edits.Count == 0 && made.Count == 0)
            return Untouched.GetValue(source, s => new Standing(
                [.. Trades(s).SelectMany(t => t.Legs).Select(l => new EffectiveLeg(l.LegId, l.TradeId, l.Currency, l.Pv, l.Notional, false))],
                [.. Trades(s).SelectMany(t => t.Amounts).Select(Amount)]));

        bool Out(string entity) => edits.TryGetValue((entity, Entity.TakenOut), out var taken) && Flag(taken, entity);
        EffectiveLeg Edited(string id, string trade, string currency, decimal pv, decimal? notional, bool isMade)
        {
            var entity = Entity.Of(Entity.Leg, id);
            return new(id, trade,
                edits.TryGetValue((entity, "currency"), out var c) ? Text(c, entity, "currency") : currency,
                edits.TryGetValue((entity, "pv"), out var p) ? Number(p, entity, "pv") : pv,
                edits.TryGetValue((entity, "notional"), out var n) ? n is null ? null : Number(n, entity, "notional") : notional, isMade);
        }
        return new(
            [
                .. Trades(source).SelectMany(t => t.Legs).Where(l => !Out(Entity.Of(Entity.Leg, l.LegId))).Select(l => Edited(l.LegId, l.TradeId, l.Currency, l.Pv, l.Notional, false)),
                .. made.Select(c => Edited(Entity.Split(c.Entity).Id, Text(c.Fields["trade"], c.Entity, "trade"), Text(c.Fields["currency"], c.Entity, "currency"),
                    Number(c.Fields["pv"], c.Entity, "pv"), c.Fields["notional"] is { } notional ? Number(notional, c.Entity, "notional") : null, true)),
            ],
            [.. Trades(source).SelectMany(t => t.Amounts).Where(a => !Out(Entity.Of(Entity.Amount, a.AmountId))).Select(Amount)]);
    }

    static IEnumerable<SourceTrade> Trades(PortfolioSourceVersion source) => source.Existing.Concat(source.New);

    // The trade as the source holds it; one it does not hold has no row to be under.
    static SourceTrade Held(WorkspaceState state, string tradeId) =>
        Source(state).Trade(tradeId) ?? throw new WorkspaceRefused("UnknownTarget", $"No trade {tradeId} in this workspace.");

    static EffectiveAmount Amount(SourceAmount a) => new(a.AmountId, a.TradeId, a.Currency, Day(a.Start), Day(a.End), a.Value);

    static string Day(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // A value a leg holds, read as its form says; one that is not of that form was written past the checks, and is
    // said, never read as something else.
    static string Text(JsonNode? value, string entity, string field) =>
        value is JsonValue v && v.TryGetValue<string>(out var text) ? text : throw new InvalidOperationException($"{entity} holds a {field} that is no text.");

    static decimal Number(JsonNode? value, string entity, string field) =>
        value is JsonValue v && v.TryGetValue<decimal>(out var number) ? number : throw new InvalidOperationException($"{entity} holds a {field} that is no number.");

    static bool Flag(JsonNode? value, string entity) =>
        value is JsonValue v && v.TryGetValue<bool>(out var flag) ? flag : throw new InvalidOperationException($"{entity} is taken out by a value that is neither true nor false.");

    /// <summary>A leg or an independent amount as the log and the Changes tab name it: what it is, and its key.</summary>
    public static string Named(string entity)
    {
        var (kind, id) = Entity.Split(entity);
        return $"{(kind == Entity.Leg ? "leg" : "independent amount")} {id}";
    }

    /// <summary>A leg the workspace made, as the Changes tab lists it: reset there, it is removed.</summary>
    public static ChangeItem MadeChange(CreatedEntity made) =>
        new(made.Entity, "*", null, Named(made.Entity), made.Fields.DeepClone(),
            $"made: {Number(made.Fields["pv"], made.Entity, "pv").ToString("0.############################", CultureInfo.InvariantCulture)} {Text(made.Fields["currency"], made.Entity, "currency")}",
            null, "none", "set", false);

    /// <summary>The legs the workspace made, in the order they were made.</summary>
    public static IReadOnlyList<CreatedEntity> Made(WorkspaceState state) =>
        [.. state.Created.Values.Where(c => c.Entity.StartsWith($"{Entity.Leg}:", StringComparison.Ordinal)).OrderBy(c => c.Seq)];

    /// <summary>Every leg of the reading: the source's that no edit took out, in the trades' order, then those the workspace made.</summary>
    public static IReadOnlyList<EffectiveLeg> Legs(WorkspaceState reading) => Of(reading).Legs;

    /// <summary>A trade's legs as the reading holds them.</summary>
    public static IEnumerable<EffectiveLeg> LegsOf(WorkspaceState reading, string tradeId) => Of(reading).LegsOf[tradeId];

    /// <summary>Every independent amount of the reading that no edit took out.</summary>
    public static IReadOnlyList<EffectiveAmount> Amounts(WorkspaceState reading) => Of(reading).Amounts;

    /// <summary>
    /// A trade's PV (note 49): its legs' PVs, each currency's into the display currency at the value date's synthetic
    /// rate, added up and held to the cent. Empty where the trade has no leg: no book holds a PV for it.
    /// </summary>
    public static decimal? PvOf(WorkspaceState reading, string tradeId) =>
        Of(reading).LegsOf[tradeId].ToList() is { Count: > 0 } legs ? InDisplay(reading, legs.Select(l => (l.Currency, l.Pv))) : null;

    /// <summary>
    /// A trade's PV as the source's legs alone give it, in the display currency: Original's (ADR-0010 §2), which a PV
    /// that differs from it is marked against.
    /// </summary>
    public static decimal? OriginalPvOf(WorkspaceState reading, string tradeId) =>
        Held(reading, tradeId).Legs is { Count: > 0 } legs ? InDisplay(reading, legs.Select(l => (l.Currency, l.Pv))) : null;

    /// <summary>A trade's independent amounts added up in the display currency, as its PV is; empty where it holds none.</summary>
    public static decimal? IndependentAmountOf(WorkspaceState reading, string tradeId) =>
        Of(reading).AmountsOf[tradeId].ToList() is { Count: > 0 } amounts ? InDisplay(reading, amounts.Select(a => (a.Currency, a.Value))) : null;

    static decimal InDisplay(WorkspaceState reading, IEnumerable<(string Currency, decimal Amount)> amounts)
    {
        var (display, date) = (PortfolioMaterialiser.DisplayCurrency(reading), Source(reading).ValueDate);
        // Each currency is added up before it is converted, so amounts in the display currency are never divided at all.
        return Math.Round(amounts.GroupBy(a => a.Currency, a => a.Amount).Sum(g => g.Key == display ? g.Sum()
            : g.Sum() * PortfolioMarket.UnitsPerGbp(display, date) / PortfolioMarket.UnitsPerGbp(g.Key, date)), 2);
    }

    // A PV as written (FieldForm.Money): its amount, and the currency written or, with none, the display currency
    // (owner, 2026-11-10).
    static (decimal Amount, string Currency) Written(WorkspaceState reading, JsonNode? value)
    {
        if (value is not JsonObject money || money["amount"] is not JsonValue held || !held.TryGetValue<decimal>(out var amount))
            throw new WorkspaceRefused("InvalidValue", "A PV is a number, alone or followed by its currency.");
        return (amount, money["unit"] is { } unit ? Text(unit, "A PV", "currency") : PortfolioMaterialiser.DisplayCurrency(reading));
    }

    /// <summary>
    /// A trade's PV overwritten (note 49, the owner's rule of 2026-11-10): the trade is left one leg of the amount, in
    /// the currency written or the display currency. Every leg it holds is taken out and one is made, numbered after
    /// its last; a trade that holds that one leg alone already changes nothing.
    /// </summary>
    public static IReadOnlyList<WorkspaceChange> SetPv(WorkspaceState reading, string tradeId, JsonNode? value)
    {
        var (amount, currency) = Written(reading, value);
        var legs = Of(reading).LegsOf[tradeId].ToList();
        if (legs is [var one] && one.Currency == currency && one.Pv == amount) return [];
        var entity = Entity.Of(Entity.Leg, $"{tradeId}/{Next(reading, tradeId)}");
        return
        [
            .. legs.Select(l => TakeOut(reading, l.LegId, l.Made)),
            new WorkspaceChange.Create(PortfolioEditRules.Target(Source(reading), entity, "*").Scope, entity, Fields(tradeId, currency, amount, null)),
        ];
    }

    /// <summary>What overwriting a trade's PV would do to its legs, as a preview says it: "2 legs replaced by 1 leg, USD".</summary>
    public static string SetPvSays(WorkspaceState reading, string tradeId, JsonNode? value)
    {
        var (_, currency) = Written(reading, value);
        var legs = Of(reading).LegsOf[tradeId].Count();
        return legs == 0 ? $"1 leg made, {currency}" : $"{legs} leg{(legs == 1 ? "" : "s")} replaced by 1 leg, {currency}";
    }

    /// <summary>A made leg's fields as its entity holds them.</summary>
    public static JsonObject Fields(string tradeId, string currency, decimal pv, decimal? notional) =>
        new() { ["trade"] = tradeId, ["currency"] = currency, ["pv"] = pv, ["notional"] = notional };

    /// <summary>The number a leg made under a trade now takes: one after the last of the source's and of those made, taken out or not.</summary>
    public static int Next(WorkspaceState reading, string tradeId)
    {
        var prefix = $"{tradeId}/";
        var held = Held(reading, tradeId).Legs.Select(l => l.LegId)
            .Concat(Made(reading).Select(c => Entity.Split(c.Entity).Id)).Where(id => id.StartsWith(prefix, StringComparison.Ordinal));
        return held.Select(id => int.Parse(id[prefix.Length..], CultureInfo.InvariantCulture)).DefaultIfEmpty(0).Max() + 1;
    }

    /// <summary>
    /// A leg or an independent amount taken out of its trade: one the workspace made is removed; one the source holds
    /// is marked by an edit of its own, so a reset or an undo brings it back.
    /// </summary>
    public static WorkspaceChange TakeOut(WorkspaceState reading, string id, bool made, string kind = Entity.Leg)
    {
        var entity = Entity.Of(kind, id);
        return made ? new WorkspaceChange.Remove(entity) : new WorkspaceChange.Set(PortfolioEditRules.Target(Source(reading), entity, Entity.TakenOut), true);
    }

    /// <summary>
    /// What brings a trade's legs back to the source's (<c>reset &lt;trade&gt; pv</c>, note 47): every edit of one of its
    /// legs is reset, one that took it out among them, and every leg the workspace made under it is removed.
    /// </summary>
    public static IReadOnlyList<WorkspaceChange> ResetPv(WorkspaceState state, string tradeId)
    {
        var prefix = $"{tradeId}/";
        return
        [
            .. Resets(state, Entity.Leg, Held(state, tradeId).Legs.Select(l => l.LegId), [.. EditableFields, Entity.TakenOut]),
            .. Made(state).Where(c => Entity.Split(c.Entity).Id.StartsWith(prefix, StringComparison.Ordinal)).Select(c => new WorkspaceChange.Remove(c.Entity)),
        ];
    }

    /// <summary>What brings back the independent amounts of a trade that an edit took out.</summary>
    public static IReadOnlyList<WorkspaceChange> ResetAmounts(WorkspaceState state, string tradeId) =>
        Resets(state, Entity.Amount, Held(state, tradeId).Amounts.Select(a => a.AmountId), [Entity.TakenOut]);

    // The edits rows of a kind hold on these fields, each to be removed; one held back as a conflict is the user's to decide.
    static IReadOnlyList<WorkspaceChange> Resets(WorkspaceState state, string kind, IEnumerable<string> ids, IReadOnlyList<string> fields) =>
        [.. ids.SelectMany(id => fields.Select(f => PortfolioEditRules.Target(Source(state), Entity.Of(kind, id), f)))
            .Where(t => state.Overrides.ContainsKey(t) || state.Lost.ContainsKey(t)).Select(t => new WorkspaceChange.Reset(t))];
}
