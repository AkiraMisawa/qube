using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Qube.Composition.Application;
using Qube.Composition.Application.Tables;
using Qube.Composition.Application.Workspaces;

namespace Qube.Samples.Portfolio;

/// <summary>
/// A case (ADR-0010 §3): the workspace plus its own changes, priced as one Before/After pair. <paramref name="Kind"/> is
/// <c>base</c> for the workspace itself, <c>vary</c> for a case a value made, <c>add</c> for an iteration of iterative
/// addition, which it names in <paramref name="Iteration"/>. <paramref name="Changes"/> counts its own edits. A case of
/// iterative addition says in <paramref name="Note"/> the common change to the existing trades it carries (docs/03 §5).
/// </summary>
public sealed record CaseView(string Id, string Title, string Kind, int Changes, Iteration? Iteration = null, string? Note = null);

/// <summary>
/// An iteration of <c>add each</c> (ADR-0016 §3 and its note 37 of 2026-11-11): the new trades its case adds together,
/// and the existing trades its case takes out of After. One trade alone and none taken out is the iteration of old.
/// </summary>
public sealed record Iteration(IReadOnlyList<string> Add, IReadOnlyList<string> Without)
{
    /// <summary>The iteration in a title's words: <c>DRAFT-1 + DRAFT-2 without E00003</c>. The line has no spelling of it (§3, Open).</summary>
    public string Said() => Without.Count == 0 ? string.Join(" + ", Add) : $"{string.Join(" + ", Add)} without {string.Join(" + ", Without)}";

    /// <summary>Whether two iterations add the same trades and take the same out, in whatever order each names them.</summary>
    public bool Same(Iteration other) => Add.ToHashSet().SetEquals(other.Add) && Without.ToHashSet().SetEquals(other.Without);

    /// <summary>The iteration as its two lists, <c>add</c> and <c>without</c>: as a case stores it, and as a Run freezes it.</summary>
    public JsonObject Lists() => new()
    {
        ["add"] = new JsonArray([.. Add.Select(id => JsonValue.Create(id))]),
        ["without"] = new JsonArray([.. Without.Select(id => JsonValue.Create(id))]),
    };

    /// <summary>The ids a list holds. Null where it is no list, which is its reader's to refuse or to say is none.</summary>
    public static IReadOnlyList<string>? Ids(JsonNode? list) => list is JsonArray ids ? [.. ids.Select(id => id!.GetValue<string>())] : null;
}

/// <summary>What <c>vary</c> did: the state it left, the cases it made by title, and each value it skipped with why.</summary>
public sealed record VaryAnswer(WorkspaceState State, IReadOnlyList<string> Made, IReadOnlyList<string> Skipped);

/// <summary>What <c>add each</c> did: the state it left, the cases it made by title, and those of the set it replaced.</summary>
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
            state.Overrides.Keys.Count(t => t.Layer == c.Entity), KindOf(c) == Add ? IterationOf(c) : null, KindOf(c) == Add ? common : null))];
    }

    /// <summary>A case's title, as it was made.</summary>
    public static string Title(CreatedEntity c) => c.Fields["title"]!.GetValue<string>();

    /// <summary>A case's kind: <c>vary</c> or <c>add</c>.</summary>
    public static string KindOf(CreatedEntity c) => c.Fields["kind"]!.GetValue<string>();

    /// <summary>
    /// The iteration a case of iterative addition was made of. A case stored before an iteration was a list names one
    /// <c>trade</c> (ADR-0016 note 37): the iteration of that trade alone, which is what it stored.
    /// </summary>
    public static Iteration IterationOf(CreatedEntity c)
    {
        IReadOnlyList<string> Ids(string list) => Iteration.Ids(c.Fields[list])
            ?? throw new InvalidOperationException($"The case {Title(c)} of add each does not say its trades: it holds no list {list}.");
        return c.Fields["trade"] is { } alone ? new([alone.GetValue<string>()], []) : new(Ids("add"), Ids("without"));
    }

    /// <summary>The iterations of iterative addition, one per case, in the order their cases were made.</summary>
    public static IReadOnlyList<Iteration> Iterations(WorkspaceState state) => [.. Made(state).Where(c => KindOf(c) == Add).Select(IterationOf)];

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
    /// Refuses, naming the trades, a set of iterative addition whose candidates, read together over its iterations
    /// (ADR-0016 note 37), are no longer the New rows with A ticked in the Base, or hold one in Before, or whose case
    /// takes out a trade the Base no longer has in After (docs/03 §5: never another set quietly). <c>add each</c> again
    /// makes it anew.
    /// </summary>
    public static void CheckAdditionSet(WorkspaceState raw)
    {
        var iterations = Iterations(raw);
        if (iterations.Count == 0) return;
        var trades = PortfolioMaterialiser.Trades(Base(raw));
        var rows = trades.Where(t => t.IsNew).ToList();
        var ticked = rows.Where(t => t.After).Select(t => t.TradeId).ToList();
        var set = iterations.SelectMany(i => i.Add).Distinct().ToList();
        static string Are(List<string> ids) => $"{string.Join(", ", ids)} {(ids.Count == 1 ? "is" : "are")}";
        var problems = new List<string>();
        if (ticked.Except(set).ToList() is { Count: > 0 } added) problems.Add($"{Are(added)} ticked and in no case");
        if (set.Except(ticked).ToList() is { Count: > 0 } gone) problems.Add($"{Are(gone)} in a case and no longer ticked");
        var before = rows.Where(t => t.Before && set.Contains(t.TradeId)).Select(t => t.TradeId).ToList();
        if (before.Count > 0) problems.Add($"{Are(before)} in Before");
        var inAfter = trades.Where(t => !t.IsNew && t.After).Select(t => t.TradeId).ToHashSet();
        if (iterations.SelectMany(i => i.Without).Distinct().Where(id => !inAfter.Contains(id)).ToList() is { Count: > 0 } kept)
            problems.Add($"{Are(kept)} taken out by a case and no longer in After");
        if (problems.Count > 0)
            throw new WorkspaceRefused("AdditionSetChanged",
                $"add each's cases add {string.Join(", ", set)}, and {string.Join("; ", problems)}. Run add each again to make the set anew{(before.Count > 0 ? ", with B clear on each" : "")}.");
    }

    /// <summary>The Base: Original plus the Common edits. The state itself where no edit is in a layer.</summary>
    public static WorkspaceState Base(WorkspaceState state) => Reading(state, null);

    /// <summary>
    /// A case's reading, or the Base's for <c>base</c> or null. A case's own edit wins over a Common one on the same field
    /// of the same entity, whatever scope each is kept in: a new trade's own content is kept in its own scope, a case's
    /// change in the counterparty's (<see cref="PortfolioEditRules.Target"/>).
    /// </summary>
    public static WorkspaceState Reading(WorkspaceState state, string? caseId)
    {
        var layer = caseId is null or BaseId ? null : Of(caseId);
        if (layer is null && !Layered(state)) return state;
        return Readings.GetOrCreateValue(state).GetOrAdd(layer ?? "", _ =>
        {
            // The fields the case holds an edit of, applied or held: on those the Common edit is not the case's value.
            var mine = layer is null ? [] : state.Overrides.Keys.Concat(state.Conflicts.Keys).Concat(state.Lost.Keys)
                .Where(k => k.Layer == layer).Select(k => (k.Entity, k.Field)).ToHashSet();
            ImmutableDictionary<EditTarget, T> Read<T>(ImmutableDictionary<EditTarget, T> items, Func<T, EditTarget, T> retarget) =>
                items.Where(i => i.Key.Layer is null && !mine.Contains((i.Key.Entity, i.Key.Field)))
                    .Concat(layer is null ? [] : items.Where(i => i.Key.Layer == layer)
                        .Select(i => KeyValuePair.Create(i.Key with { Layer = null }, retarget(i.Value, i.Key with { Layer = null }))))
                    .ToImmutableDictionary();
            // A case's edits come after every Common one, whenever made: where precedence goes by when an edit was made,
            // as a row's B or A over its grid's (ADR-0010 §6), the case's still wins, since a case is the Base plus its own.
            // The offset is fixed, so a reading's edits keep their order and the same reading the same numbers.
            var overrides = Read(state.Overrides, (o, t) => o with { Target = t, Seq = CaseSeq + o.Seq });
            // A Common edit held back on a field the case holds is not the case's, and the case's own held edits are.
            var conflicts = Read(state.Conflicts, (c, t) => c with { Override = c.Override with { Target = t } });
            var lost = Read(state.Lost, (l, t) => l with { Override = l.Override with { Target = t } });
            return state with { Overrides = overrides, Conflicts = conflicts, Lost = lost };
        });
    }

    // Above every sequence number a log will reach.
    const long CaseSeq = 1L << 48;

    /// <summary>
    /// Refuses a state whose edits are in layers where a reading is wanted: the materialisers read one edit per field,
    /// which only a reading (<see cref="Base"/>, <see cref="Reading"/>) gives. A call site that forgets is a defect, said
    /// at once rather than read as some layer's value.
    /// </summary>
    public static void Unlayered(WorkspaceState state)
    {
        if (Layered(state))
            throw new InvalidOperationException("A workspace with cases is read through PortfolioCases.Base or PortfolioCases.Reading, never as it is stored.");
    }

    // Asked by every reader of a state, and a state never changes: answered once for each.
    static bool Layered(WorkspaceState state) => (bool)LayeredRead.GetValue(state, s =>
        (object)(s.Overrides.Keys.Any(k => k.Layer is not null) || s.Conflicts.Keys.Any(k => k.Layer is not null) || s.Lost.Keys.Any(k => k.Layer is not null)));

    static readonly ConditionalWeakTable<WorkspaceState, object> LayeredRead = new();

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

    /// <summary>A case's own edits, each with its value: what makes it that case.</summary>
    public static IReadOnlyList<Override> ChangesOf(WorkspaceState state, string caseId) =>
        [.. state.Overrides.Values.Where(o => o.Target.Layer == Of(caseId)).OrderBy(o => o.Seq)];
}

/// <summary>
/// A cell named as ADR-0010 §9's note of 2026-11-08 names it: <c>&lt;row&gt; &lt;column&gt;</c>. The row is a CSA's id, the
/// netting set's id or a trade's id; the column a field's title or one of its short names, case ignored. A value is read
/// as the field's form reads it (<see cref="FieldForms"/>, ADR-0016 §1): an amount as ADR-0008 writes it (a bare number
/// is millions), a rate in <c>%</c> or <c>bp</c>.
/// </summary>
public sealed record CellName(string Entity, string Field, string Row, string Column, FieldSpec Spec);

public static class PortfolioCells
{
    sealed record Column(string Field, FieldSpec Spec, params string[] Names);

    // A cell is a field the registered tables say a command may set (ADR-0016 §1), named by that field's words and
    // read and written by its form.
    static IReadOnlyList<Column> Columns(IEnumerable<TableField> fields) => [.. fields.Where(f => f.Editable).Select(f => new Column(f.Name, f.Spec, [.. f.Words]))];

    static readonly IReadOnlyList<Column> CsaColumns = Columns(PortfolioFields.Csa), NettingColumns = Columns(PortfolioFields.Netting),
        // The counterparty's CDS choice, row `credit`, and the chosen curve's spreads, row `cds` (ADR-0010 §8's note of 2026-11-09).
        CreditColumns = Columns(PortfolioFields.Credit), SpreadColumns = Columns(PortfolioFields.Cds);

    /// <summary>The row that names the counterparty's CDS choice.</summary>
    public const string CreditRow = "credit";

    /// <summary>The row that names the chosen CDS curve's spreads.</summary>
    public const string CdsRow = "cds";

    /// <summary>
    /// The cell a row and a column name, as the state stands. A row or a column that names nothing, or more than one,
    /// is refused with what it could mean, never guessed.
    /// </summary>
    public static CellName Resolve(WorkspaceState state, MeasureRegister register, string row, string column)
    {
        var source = (PortfolioSourceVersion)state.Source;
        var credit = PortfolioMaterialiser.Credit(state);
        if (string.Equals(row.Trim(), CdsRow, StringComparison.OrdinalIgnoreCase) && credit.Cds is null)
            throw new WorkspaceRefused("UnknownCell", credit.Missing ?? throw new InvalidOperationException("A credit with no curve says why."));
        var rows = source.Agreement.Csas.Select(c => (Id: c.CsaId, Entity: Entity.Of(Entity.Csa, c.CsaId), Columns: CsaColumns))
            .Append((Id: source.Agreement.NettingSetId, Entity: Entity.Of(Entity.Netting, source.Agreement.NettingSetId), Columns: NettingColumns))
            .Append((Id: CreditRow, Entity: Entity.Of(Entity.Credit, source.Registration.Key), Columns: CreditColumns))
            .Concat(credit.Cds is { } cds ? [(Id: CdsRow, Entity: Entity.Of(Entity.Cds, cds.CurveId), Columns: SpreadColumns)] : [])
            .Concat(source.Existing.Concat(source.New).Select(t => (Id: t.TradeId, Entity: Entity.Of(Entity.Trade, t.TradeId), Columns: Trade(register))))
            // A module's settings, row its id (ADR-0010 §8): `set xva cost of capital 12%`.
            .Concat(register.Modules.Where(m => m.Settings.Count > 0).Select(m => (Id: m.Id, Entity: MeasureSettings.EntityOf(m.Id), Columns: Settings(m))))
            .Where(r => string.Equals(r.Id, row.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (rows.Count == 0)
            throw new WorkspaceRefused("UnknownCell", $"No row {row.Trim()}: a row is a CSA ({string.Join(", ", source.Agreement.Csas.Select(c => c.CsaId))}), "
                + $"the netting set ({source.Agreement.NettingSetId}), {CreditRow}, {CdsRow}, a trade's id{string.Concat(register.Modules.Where(m => m.Settings.Count > 0).Select(m => $", or {m.Id} for {m.Title}'s settings"))}.");
        if (rows.Count > 1) throw new WorkspaceRefused("AmbiguousCell", $"{row.Trim()} names {string.Join(" and ", rows.Select(r => r.Entity))}.");
        var (id, entity, columns) = rows[0];
        var name = Words(column);
        var found = columns.Where(c => c.Names.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase))).ToList();
        if (found.Count == 0)
            throw new WorkspaceRefused("UnknownCell", $"{id} has no column {column.Trim()}; its columns are {string.Join(", ", columns.Select(c => c.Names[0]))}.");
        if (found.Count > 1)
            throw new WorkspaceRefused("AmbiguousCell", $"{column.Trim()} could be {string.Join(" or ", found.Select(c => c.Names[0]))} of {id}.");
        return new CellName(entity, found[0].Field, id, found[0].Names[0], found[0].Spec);
    }

    /// <summary>The cell of an entity's field, named as <see cref="Resolve"/> names it; refused where it is no cell.</summary>
    public static CellName Of(WorkspaceState state, MeasureRegister register, string entity, string field)
    {
        var (kind, id) = Entity.Split(entity);
        var columns = ColumnsOf(entity, register)
            ?? throw new WorkspaceRefused("UnknownCell", $"{entity} is not a row a cell is named on: a row is a CSA, the netting set, the credit, the CDS, a trade or a module's settings.");
        var column = columns.FirstOrDefault(c => c.Field == field)
            ?? throw new WorkspaceRefused("UnknownCell", $"{id} has no column {field}; its columns are {string.Join(", ", columns.Select(c => c.Names[0]))}.");
        // A curve's spread is a cell of its own row whether or not it is the curve chosen now (the Changes tab sets it).
        if (kind == Entity.Cds)
            return CdsCatalog.Find(id) is not null ? new CellName(entity, field, RowOf(state, entity), column.Names[0], column.Spec)
                : throw new WorkspaceRefused("UnknownCell", $"{entity} is not a curve of the CDS catalogue.");
        var row = kind == Entity.Credit ? CreditRow : id;
        return Resolve(state, register, row, column.Names[0]) is var cell && cell.Entity == entity ? cell
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
            Entity.Credit => source.Registration.Key == id,
            Entity.Cds => CdsCatalog.Find(id) is not null,
            Entity.Settings => register.SettingsOf(entity) is not null,
            _ => false,
        };
        var column = exists ? ColumnsOf(entity, register)?.FirstOrDefault(c => c.Field == field) : null;
        return column is null ? null : new CellName(entity, field, RowOf(state, entity), column.Names[0], column.Spec);
    }

    // How a row is named: the credit's and the chosen curve's by their rows' names, a curve not chosen by its id.
    static string RowOf(WorkspaceState state, string entity)
    {
        var (kind, id) = Entity.Split(entity);
        return kind switch
        {
            Entity.Credit => CreditRow,
            // The Base's curve: a case changes spreads, never the CDS chosen (ADR-0010 §8's note of 2026-11-09).
            Entity.Cds => PortfolioMaterialiser.Credit(PortfolioCases.Base(state)).Cds?.CurveId == id ? CdsRow : id,
            _ => id,
        };
    }

    static IReadOnlyList<Column>? ColumnsOf(string entity, MeasureRegister register) => Entity.Split(entity).Kind switch
    {
        Entity.Csa => CsaColumns,
        Entity.Netting => NettingColumns,
        Entity.Credit => CreditColumns,
        Entity.Cds => SpreadColumns,
        Entity.Trade => Trade(register),
        Entity.Settings => register.SettingsOf(entity) is { } module ? Settings(module) : null,
        _ => null,
    };

    // A module's settings are named by their titles' words and their own names (ADR-0010 §8).
    static IReadOnlyList<Column> Settings(IMeasureModule module) => Columns(PortfolioFields.Settings(module));

    /// <summary>
    /// A value as the Changes tab and a case's title write it, in the form <see cref="Read"/> reads back to the same value
    /// (<see cref="FieldForms.Write"/>): an amount never bare, since a bare number is read as millions; empty for none.
    /// </summary>
    public static string Write(CellName cell, JsonNode? value) => FieldForms.Write(cell.Spec, value);

    // A trade's own cells, then each module's typed column (ADR-0010 §10).
    static IReadOnlyList<Column> Trade(MeasureRegister register) => Columns(PortfolioFields.Trade(register));

    static string Words(string text) => string.Join(' ', text.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>A value as written for a cell of the form, or why it cannot be read; never a guess at what was meant.</summary>
    public static JsonNode? Read(CellName cell, string text)
    {
        var read = FieldForms.Read(cell.Spec, text);
        if (read.Problem == FieldForms.Problem.None) return read.Value;
        var written = text.Trim();
        throw new WorkspaceRefused("InvalidValue", $"{cell.Row} {cell.Column} is {read.What}; {(written.Length == 0 ? "nothing was written" : $"{written} is not")}.");
    }

    /// <summary>The value a reading holds in the cell now, as an edit would write it.</summary>
    public static JsonNode? Current(WorkspaceState reading, MeasureRegister register, CellName cell)
    {
        var (kind, id) = Entity.Split(cell.Entity);
        switch (kind)
        {
            case Entity.Csa:
                return PortfolioFields.Value(PortfolioMaterialiser.Csas(reading).Single(x => x.CsaId == id), cell.Field);
            case Entity.Netting:
                return PortfolioMaterialiser.Credit(reading).CvaRecovery;
            case Entity.Credit:
                return PortfolioFields.Value(PortfolioMaterialiser.Credit(reading).Choice, cell.Field);
            case Entity.Cds:
            {
                var curve = CdsCatalog.Find(id) ?? throw new InvalidOperationException($"{cell.Entity} is not a curve of the catalogue.");
                return PortfolioMaterialiser.CurveOf(reading, curve).Spreads[cell.Field];
            }
            case Entity.Trade:
            {
                var t = PortfolioMaterialiser.Trades(reading).Single(x => x.TradeId == id);
                return register.ColumnOf(cell.Field) is null ? PortfolioFields.Value(t, cell.Field)
                    : register.WithCells(reading, [t])[0].Cells?.GetValueOrDefault(cell.Field)?.Value?.DeepClone();
            }
            case Entity.Settings:
            {
                var module = register.SettingsOf(cell.Entity) ?? throw new InvalidOperationException($"{cell.Entity} is no module's settings.");
                return MeasureSettings.Value(reading, module.Id, module.Settings.Single(s => s.Id == cell.Field))?.DeepClone();
            }
            default:
                throw new InvalidOperationException($"{cell.Entity} is not a cell's row.");
        }
    }
}
