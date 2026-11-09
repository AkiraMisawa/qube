using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Workbench.Composition.Application.Workspaces;

namespace Workbench.Samples.Portfolio;

/// <summary>
/// A case (ADR-0010 §3): the workspace plus its own changes, priced as one Before/After pair. <paramref name="Kind"/> is
/// <c>base</c> for the workspace itself, <c>vary</c> for a case a value made, <c>add</c> for one trade of iterative
/// addition, which names it in <paramref name="Trade"/>. <paramref name="Changes"/> counts its own edits. A case of
/// iterative addition says in <paramref name="Note"/> the common change to the existing trades it carries (docs/03 §5).
/// </summary>
public sealed record CaseView(string Id, string Title, string Kind, int Changes, string? Trade = null, string? Note = null);

/// <summary>What <c>vary</c> did: the state it left, the cases it made by title, and each value it skipped with why.</summary>
public sealed record VaryAnswer(WorkspaceState State, IReadOnlyList<string> Made, IReadOnlyList<string> Skipped);

/// <summary>What <c>add trades</c> did: the state it left, the cases it made by title, and those of the set it replaced.</summary>
public sealed record AddAnswer(WorkspaceState State, IReadOnlyList<string> Made, IReadOnlyList<string> Replaced);

/// <summary>
/// The cases of a portfolio workspace and how its layers compose (ADR-0010 §3 and its note of 2026-11-08). A case is a
/// created entity <c>case:&lt;id&gt;</c> in the counterparty's scope; its changes are edits in the layer of the same name.
/// The kernel gives a layer no meaning: here the Base reads Original plus the Common edits, and a case the Base plus its
/// own layer, its value winning on a field both hold. Every reading the page has is computed from such a reading.
/// </summary>
public static class PortfolioCases
{
    public const string BaseId = "base";
    public const string BaseTitle = "Base";
    public const string Vary = "vary";
    public const string Add = "add";

    /// <summary>How many cases one Price prices, the Base among them, unless configured (ADR-0010 §3's note, DEFAULT).</summary>
    public const int DefaultCap = 50;

    /// <summary>The layer, and the entity, of a case.</summary>
    public static string Of(string caseId) => Entity.Of(Entity.Case, caseId);

    /// <summary>The cases made, in the order they were made; the Base is not among them.</summary>
    public static IReadOnlyList<CreatedEntity> Made(WorkspaceState state) =>
        [.. state.Created.Values.Where(c => Entity.Split(c.Entity).Kind == Entity.Case).OrderBy(c => c.Seq)];

    /// <summary>Every case Price prices, the Base first (ADR-0010 §3's note of 2026-11-08).</summary>
    public static IReadOnlyList<CaseView> All(WorkspaceState state)
    {
        var made = Made(state);
        // Every case of iterative addition says the common change it carries (docs/03 §5), read from the Base now.
        var common = made.Any(c => KindOf(c) == Add) ? CommonChange(Base(state)) : null;
        return [new(BaseId, BaseTitle, "base", 0), .. made.Select(c => new CaseView(Entity.Split(c.Entity).Id, Title(c), KindOf(c),
            state.Overrides.Keys.Count(t => t.Layer == c.Entity), c.Fields["trade"]?.GetValue<string>(), KindOf(c) == Add ? common : null))];
    }

    static string Title(CreatedEntity c) => c.Fields["title"]!.GetValue<string>();

    /// <summary>A case's kind: <c>vary</c> or <c>add</c>.</summary>
    public static string KindOf(CreatedEntity c) => c.Fields["kind"]!.GetValue<string>();

    /// <summary>The trades iterative addition prices alone, one per case, in the order their cases were made.</summary>
    public static IReadOnlyList<string> AdditionSet(WorkspaceState state) =>
        [.. Made(state).Where(c => KindOf(c) == Add).Select(c => c.Fields["trade"]!.GetValue<string>())];

    /// <summary>
    /// The common change every case of iterative addition carries (docs/03 §5): the existing trades whose After the Base
    /// holds otherwise than their Before. Null where it holds none, so no case says a change it does not carry.
    /// </summary>
    public static string? CommonChange(WorkspaceState baseline)
    {
        var moved = PortfolioMaterialiser.Trades(baseline).Where(t => !t.IsNew && t.Before != t.After).ToList();
        if (moved.Count == 0) return null;
        static string Named(List<EffectiveTrade> trades) =>
            trades.Count <= 3 ? $"existing {string.Join(", ", trades.Select(t => t.TradeId))}" : $"{trades.Count} existing trades";
        var parts = new List<string>();
        if (moved.Where(t => t.Before).ToList() is { Count: > 0 } outOf) parts.Add($"{Named(outOf)} out of After");
        if (moved.Where(t => t.After).ToList() is { Count: > 0 } into) parts.Add($"{Named(into)} in After only");
        return $"with {string.Join(" and ", parts)}";
    }

    /// <summary>
    /// Refuses, naming the trades, a set of iterative addition that no longer matches the New rows with A ticked in the
    /// Base, or that holds one in Before (docs/03 §5: never another set quietly). <c>add trades</c> again makes it anew.
    /// </summary>
    public static void CheckAdditionSet(WorkspaceState raw)
    {
        var set = AdditionSet(raw);
        if (set.Count == 0) return;
        var rows = PortfolioMaterialiser.Trades(Base(raw)).Where(t => t.IsNew).ToList();
        var ticked = rows.Where(t => t.After).Select(t => t.TradeId).ToList();
        static string Are(List<string> ids) => $"{string.Join(", ", ids)} {(ids.Count == 1 ? "is" : "are")}";
        var problems = new List<string>();
        if (ticked.Except(set).ToList() is { Count: > 0 } added) problems.Add($"{Are(added)} ticked and in no case");
        if (set.Except(ticked).ToList() is { Count: > 0 } gone) problems.Add($"{Are(gone)} in a case and no longer ticked");
        var before = rows.Where(t => t.Before && set.Contains(t.TradeId)).Select(t => t.TradeId).ToList();
        if (before.Count > 0) problems.Add($"{Are(before)} in Before");
        if (problems.Count > 0)
            throw new WorkspaceRefused("AdditionSetChanged",
                $"add trades prices {string.Join(", ", set)} alone, and {string.Join("; ", problems)}. Run add trades again to make the set anew{(before.Count > 0 ? ", with B clear on each" : "")}.");
    }

    /// <summary>The Base: Original plus the Common edits. The state itself where no edit is in a layer.</summary>
    public static WorkspaceState Base(WorkspaceState state) => Reading(state, null);

    /// <summary>A case's reading, or the Base's for <c>base</c> or null.</summary>
    public static WorkspaceState Reading(WorkspaceState state, string? caseId)
    {
        var layer = caseId is null or BaseId ? null : Of(caseId);
        if (layer is null && !Layered(state)) return state;
        return Readings.GetOrCreateValue(state).GetOrAdd(layer ?? "", _ =>
        {
            ImmutableDictionary<EditTarget, T> Read<T>(ImmutableDictionary<EditTarget, T> items, Func<T, EditTarget, T> retarget)
            {
                var common = items.Where(i => i.Key.Layer is null);
                if (layer is null) return common.ToImmutableDictionary();
                // The case's own edit wins over the Common one on its field.
                var own = items.Where(i => i.Key.Layer == layer).Select(i => KeyValuePair.Create(i.Key with { Layer = null }, retarget(i.Value, i.Key with { Layer = null }))).ToList();
                var mine = own.Select(o => o.Key).ToHashSet();
                return common.Where(c => !mine.Contains(c.Key)).Concat(own).ToImmutableDictionary();
            }
            // A case's edits come after every Common one, whenever made: where precedence goes by when an edit was made,
            // as a row's B or A over its grid's (ADR-0010 §6), the case's still wins, since a case is the Base plus its own.
            var overrides = Read(state.Overrides, (o, t) => o with { Target = t, Seq = state.Revision + o.Seq });
            var conflicts = Read(state.Conflicts, (c, t) => c with { Override = c.Override with { Target = t } });
            var lost = Read(state.Lost, (l, t) => l with { Override = l.Override with { Target = t } });
            // A field the case sets is the case's: a Common edit held back on it does not hold the case back.
            if (layer is not null)
            {
                var own = overrides.Keys.Where(k => state.Overrides.ContainsKey(k with { Layer = layer })).ToHashSet();
                conflicts = conflicts.RemoveRange(own);
                lost = lost.RemoveRange(own);
            }
            return state with { Overrides = overrides, Conflicts = conflicts, Lost = lost };
        });
    }

    static bool Layered(WorkspaceState state) =>
        state.Overrides.Keys.Any(k => k.Layer is not null) || state.Conflicts.Keys.Any(k => k.Layer is not null) || state.Lost.Keys.Any(k => k.Layer is not null);

    static readonly ConditionalWeakTable<WorkspaceState, ConcurrentDictionary<string, WorkspaceState>> Readings = new();

    /// <summary>
    /// The entities Price reads in any case it prices: an edit held back on one of them, in the Common layer or a case's,
    /// blocks Price as it does in a one-case workspace (ADR-0003 §6).
    /// </summary>
    public static Func<string, bool> PricedEntities(WorkspaceState state)
    {
        var each = All(state).Select(c => PortfolioMaterialiser.PricedEntities(Reading(state, c.Id))).ToList();
        return entity => each.Any(priced => priced(entity));
    }

    /// <summary>Whether two values an edit writes are the same value: numbers by value, whatever their written form.</summary>
    public static bool Same(JsonNode? a, JsonNode? b) => (a, b) switch
    {
        (null, null) => true,
        (null, _) or (_, null) => false,
        (JsonValue x, JsonValue y) when x.GetValueKind() == System.Text.Json.JsonValueKind.Number && y.GetValueKind() == System.Text.Json.JsonValueKind.Number =>
            x.GetValue<decimal>() == y.GetValue<decimal>(),
        _ => JsonNode.DeepEquals(a, b),
    };

    /// <summary>A case's own edits, each with its value: what makes it that case.</summary>
    public static IReadOnlyList<Override> ChangesOf(WorkspaceState state, string caseId) =>
        [.. state.Overrides.Values.Where(o => o.Target.Layer == Of(caseId)).OrderBy(o => o.Seq)];
}

/// <summary>
/// A cell named as ADR-0010 §9's note of 2026-11-08 names it: <c>&lt;row&gt; &lt;column&gt;</c>. The row is a CSA's id, the
/// netting set's id or a trade's id; the column a field's title or one of its short names, case ignored. A value is read
/// as the field takes it: an amount as ADR-0008 writes it (a bare number is millions), a rate in <c>%</c> or <c>bp</c>.
/// </summary>
public sealed record CellName(string Entity, string Field, string Row, string Column, CellName.Form Kind)
{
    public enum Form { Amount, SignedAmount, Rate, Number, Flag, Id, OptionalId, Currency, Frequency, Schedule }
}

public static partial class PortfolioCells
{
    sealed record Column(string Field, CellName.Form Form, params string[] Names);

    static readonly IReadOnlyList<Column> CsaColumns =
    [
        new("counterpartyThreshold", CellName.Form.Amount, "threshold", "counterparty threshold"),
        new("counterpartyMta", CellName.Form.Amount, "mta", "counterparty mta"),
        new("counterpartyCurrency", CellName.Form.Currency, "counterparty currency"),
        new("selfThreshold", CellName.Form.Amount, "self threshold"),
        new("selfMta", CellName.Form.Amount, "self mta"),
        new("selfCurrency", CellName.Form.Currency, "self currency"),
        new("currency", CellName.Form.Currency, "currency", "ccy"),
        new("curve", CellName.Form.Id, "curve", "discount curve", "discount"),
        new("fvaCurve", CellName.Form.Id, "fva curve", "fva discount curve", "fva discount"),
        new("frequency", CellName.Form.Frequency, "frequency"),
        new("exchange", CellName.Form.Flag, "exchange"),
        new("oneWay", CellName.Form.Flag, "one-way", "one way", "oneway"),
    ];

    static readonly IReadOnlyList<Column> NettingColumns = [new("cvaRecovery", CellName.Form.Rate, "recovery", "cva recovery")];

    static readonly IReadOnlyList<Column> TradeColumns =
    [
        new("notional", CellName.Form.SignedAmount, "notional"),
        new("fixedRate", CellName.Form.Rate, "rate", "fixed rate"),
        new("schedule", CellName.Form.Schedule, "schedule"),
        new("csa", CellName.Form.Id, "csa"),
        new("curve", CellName.Form.Id, "curve", "discount curve"),
        new("imR", CellName.Form.OptionalId, "im-r", "imr"),
        new("imP", CellName.Form.OptionalId, "im-p", "imp"),
        new("before", CellName.Form.Flag, "B", "before"),
        new("after", CellName.Form.Flag, "A", "after"),
    ];

    /// <summary>
    /// The cell a row and a column name, as the state stands. A row or a column that names nothing, or more than one,
    /// is refused with what it could mean, never guessed.
    /// </summary>
    public static CellName Resolve(WorkspaceState state, MeasureRegister register, string row, string column)
    {
        var source = (PortfolioSourceVersion)state.Source;
        var rows = source.Agreement.Csas.Select(c => (Id: c.CsaId, Entity: Entity.Of(Entity.Csa, c.CsaId), Columns: CsaColumns))
            .Append((Id: source.Agreement.NettingSetId, Entity: Entity.Of(Entity.Netting, source.Agreement.NettingSetId), Columns: NettingColumns))
            .Concat(source.Existing.Concat(source.New).Select(t => (Id: t.TradeId, Entity: Entity.Of(Entity.Trade, t.TradeId), Columns: Trade(register))))
            .Where(r => string.Equals(r.Id, row.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (rows.Count == 0)
            throw new WorkspaceRefused("UnknownCell", $"No row {row.Trim()}: a row is a CSA ({string.Join(", ", source.Agreement.Csas.Select(c => c.CsaId))}), "
                + $"the netting set ({source.Agreement.NettingSetId}) or a trade's id.");
        if (rows.Count > 1) throw new WorkspaceRefused("AmbiguousCell", $"{row.Trim()} names {string.Join(" and ", rows.Select(r => r.Entity))}.");
        var (id, entity, columns) = rows[0];
        var name = Words(column);
        var found = columns.Where(c => c.Names.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase))).ToList();
        if (found.Count == 0)
            throw new WorkspaceRefused("UnknownCell", $"{id} has no column {column.Trim()}; its columns are {string.Join(", ", columns.Select(c => c.Names[0]))}.");
        if (found.Count > 1)
            throw new WorkspaceRefused("AmbiguousCell", $"{column.Trim()} could be {string.Join(" or ", found.Select(c => c.Names[0]))} of {id}.");
        return new CellName(entity, found[0].Field, id, found[0].Names[0], found[0].Form);
    }

    /// <summary>The cell of an entity's field, named as <see cref="Resolve"/> names it; refused where it is no cell.</summary>
    public static CellName Of(WorkspaceState state, MeasureRegister register, string entity, string field)
    {
        var (kind, id) = Entity.Split(entity);
        var columns = ColumnsOf(kind, register)
            ?? throw new WorkspaceRefused("UnknownCell", $"{entity} is not a row a cell is named on: a row is a CSA, the netting set or a trade.");
        var column = columns.FirstOrDefault(c => c.Field == field)
            ?? throw new WorkspaceRefused("UnknownCell", $"{id} has no column {field}; its columns are {string.Join(", ", columns.Select(c => c.Names[0]))}.");
        return Resolve(state, register, id, column.Names[0]) is var cell && cell.Entity == entity ? cell
            : throw new WorkspaceRefused("UnknownCell", $"{entity} is not a row of this workspace.");
    }

    /// <summary>The cell of an entity's field where it is one a cell names, such as a CSA's threshold; none for an edit of
    /// a whole grid's B or A, or of a row this version no longer has.</summary>
    public static CellName? Find(WorkspaceState state, MeasureRegister register, string entity, string field)
    {
        var (kind, id) = Entity.Split(entity);
        var source = (PortfolioSourceVersion)state.Source;
        var exists = kind switch
        {
            Entity.Csa => source.Agreement.Csas.Any(c => c.CsaId == id),
            Entity.Netting => source.Agreement.NettingSetId == id,
            Entity.Trade => source.Trade(id) is not null,
            _ => false,
        };
        var column = exists ? ColumnsOf(kind, register)?.FirstOrDefault(c => c.Field == field) : null;
        return column is null ? null : new CellName(entity, field, id, column.Names[0], column.Form);
    }

    static IReadOnlyList<Column>? ColumnsOf(string kind, MeasureRegister register) => kind switch
    {
        Entity.Csa => CsaColumns,
        Entity.Netting => NettingColumns,
        Entity.Trade => Trade(register),
        _ => null,
    };

    /// <summary>A value as the Changes tab and a case's title write it, in the form <see cref="Read"/> reads back: an amount
    /// as ADR-0008 writes it (5mm, 250k, 1.5bn), a rate in %, a flag on or off, empty for none.</summary>
    public static string Write(CellName cell, JsonNode? value)
    {
        if (value is null) return "empty";
        switch (cell.Kind)
        {
            case CellName.Form.Amount or CellName.Form.SignedAmount:
            {
                var n = value.GetValue<decimal>();
                foreach (var (scale, unit) in new[] { (1_000_000_000m, "bn"), (1_000_000m, "mm"), (1_000m, "k") })
                    if (Math.Abs(n) >= scale && n % (scale / 1_000m) == 0) return $"{(n / scale).ToString("0.###", CultureInfo.InvariantCulture)}{unit}";
                return n == 0 ? "0" : n.ToString("#,0.##", CultureInfo.InvariantCulture);
            }
            case CellName.Form.Rate:
                return $"{(value.GetValue<decimal>() * 100m).ToString("0.####", CultureInfo.InvariantCulture)}%";
            case CellName.Form.Number:
                return value.GetValue<decimal>().ToString("0.##########", CultureInfo.InvariantCulture);
            case CellName.Form.Flag:
                return value.GetValue<bool>() ? "on" : "off";
            default:
                return value.GetValue<string>();
        }
    }

    // A module's typed trade column (ADR-0010 §10) is named by its id or its title, such as XVA's Margin.
    static IReadOnlyList<Column> Trade(MeasureRegister register) =>
        [.. TradeColumns, .. register.TradeColumns.Where(c => c.Editable).Select(c => new Column(c.Field, CellName.Form.Number,
            [.. new[] { Words(c.Field[(c.Field.IndexOf('.') + 1)..]), Words(c.Title), Words(Unit().Replace(c.Title, "")) }.Distinct()]))];

    static string Words(string text) => string.Join(' ', text.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>A value as written for a cell of the form, or why it cannot be read; never a guess at what was meant.</summary>
    public static JsonNode? Read(CellName cell, string text)
    {
        var t = text.Trim();
        string Refuse(string what) => throw new WorkspaceRefused("InvalidValue", $"{cell.Row} {cell.Column} is {what}; {t} is not.");
        switch (cell.Kind)
        {
            case CellName.Form.Amount or CellName.Form.SignedAmount:
            {
                var m = AmountPattern().Match(t);
                if (!m.Success) return Refuse("an amount: 5mm, 250k, 1bn, or a bare number of millions");
                var n = decimal.Parse(m.Groups[1].Value.Replace(",", "", StringComparison.Ordinal), NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
                if (cell.Kind == CellName.Form.Amount && n < 0) return Refuse("an amount of 0 or more");
                var scale = m.Groups[2].Value.ToLowerInvariant();
                // As ADR-0008: a bare number is millions, and a bare one of a million or more was typed in full.
                if (scale.Length == 0 && Math.Abs(n) >= 1_000_000m) return Refuse("in millions; write a full amount with its scale, such as 1bn");
                return n * scale switch { "k" => 1_000m, "mm" => 1_000_000m, "bn" => 1_000_000_000m, _ => 1_000_000m };
            }
            case CellName.Form.Rate:
            {
                if (t.Equals("empty", StringComparison.OrdinalIgnoreCase) || t.Equals("none", StringComparison.OrdinalIgnoreCase)) return null;
                var m = RatePattern().Match(t);
                if (!m.Success) return Refuse("a rate with its unit, 40% or 25bp, or empty");
                var n = decimal.Parse(m.Groups[1].Value, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
                return m.Groups[2].Value.Equals("bp", StringComparison.OrdinalIgnoreCase) ? n / 10_000m : n / 100m;
            }
            case CellName.Form.Number:
            {
                var m = NumberPattern().Match(t);
                if (!m.Success) return Refuse("a number, with k, mm or bn for its scale, or bp");
                var n = decimal.Parse(m.Groups[1].Value.Replace(",", "", StringComparison.Ordinal), NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
                return n * m.Groups[2].Value.ToLowerInvariant() switch { "k" => 1_000m, "mm" => 1_000_000m, "bn" => 1_000_000_000m, _ => 1m };
            }
            case CellName.Form.Flag:
                return t.ToLowerInvariant() switch
                {
                    "on" or "yes" or "true" or "y" => true,
                    "off" or "no" or "false" or "n" => false,
                    _ => Refuse("on or off"),
                };
            case CellName.Form.Currency:
                return PortfolioMarket.Currencies.FirstOrDefault(c => c.Equals(t, StringComparison.OrdinalIgnoreCase)) is { } code ? code
                    : Refuse($"a currency, {string.Join(", ", PortfolioMarket.Currencies)}");
            case CellName.Form.Frequency:
                return PortfolioCatalog.Frequencies.FirstOrDefault(f => f.Equals(t, StringComparison.OrdinalIgnoreCase)) is { } often ? often
                    : Refuse($"a frequency, {string.Join(", ", PortfolioCatalog.Frequencies)}");
            case CellName.Form.Schedule:
                return t.ToUpperInvariant() is "3M" or "6M" ? t.ToUpperInvariant() : Refuse("3M or 6M");
            case CellName.Form.OptionalId:
                return t.Equals("empty", StringComparison.OrdinalIgnoreCase) || t.Equals("none", StringComparison.OrdinalIgnoreCase) ? null : t;
            case CellName.Form.Id:
                return t.Length > 0 ? t : Refuse("an identifier");
            default:
                throw new InvalidOperationException($"No reading of {cell.Kind}.");
        }
    }

    /// <summary>The value a reading holds in the cell now, as an edit would write it.</summary>
    public static JsonNode? Current(WorkspaceState reading, MeasureRegister register, CellName cell)
    {
        var (kind, id) = Entity.Split(cell.Entity);
        switch (kind)
        {
            case Entity.Csa:
            {
                var c = PortfolioMaterialiser.Csas(reading).Single(x => x.CsaId == id);
                return cell.Field switch
                {
                    "counterpartyThreshold" => c.CounterpartyThreshold, "counterpartyMta" => c.CounterpartyMta, "counterpartyCurrency" => c.CounterpartyCurrency,
                    "selfThreshold" => c.SelfThreshold, "selfMta" => c.SelfMta, "selfCurrency" => c.SelfCurrency,
                    "currency" => c.Currency, "curve" => c.Curve, "fvaCurve" => c.FvaCurve, "frequency" => c.Frequency,
                    "exchange" => c.Exchange, "oneWay" => c.OneWay,
                    _ => throw new InvalidOperationException($"A CSA has no {cell.Field}."),
                };
            }
            case Entity.Netting:
                return PortfolioMaterialiser.Credit(reading).CvaRecovery;
            case Entity.Trade:
            {
                var t = PortfolioMaterialiser.Trades(reading).Single(x => x.TradeId == id);
                if (register.ColumnOf(cell.Field) is not null)
                    return register.WithCells(reading, [t])[0].Cells?.GetValueOrDefault(cell.Field)?.Value?.DeepClone();
                return cell.Field switch
                {
                    "notional" => t.Notional, "fixedRate" => t.FixedRate, "schedule" => t.Schedule, "csa" => t.Csa, "curve" => t.Curve,
                    "imR" => t.ImR, "imP" => t.ImP, "before" => t.Before, "after" => t.After,
                    _ => throw new InvalidOperationException($"A trade has no {cell.Field}."),
                };
            }
            default:
                throw new InvalidOperationException($"{cell.Entity} is not a cell's row.");
        }
    }

    [GeneratedRegex("^(-?[0-9][0-9,]*(?:\\.[0-9]+)?)(k|mm|bn)?$", RegexOptions.IgnoreCase)]
    private static partial Regex AmountPattern();

    [GeneratedRegex("^(-?[0-9]+(?:\\.[0-9]+)?)(%|bp)$", RegexOptions.IgnoreCase)]
    private static partial Regex RatePattern();

    [GeneratedRegex("^(-?[0-9][0-9,]*(?:\\.[0-9]+)?)(k|mm|bn|bp)?$", RegexOptions.IgnoreCase)]
    private static partial Regex NumberPattern();

    [GeneratedRegex("\\s*\\([^)]*\\)")]
    private static partial Regex Unit();
}
