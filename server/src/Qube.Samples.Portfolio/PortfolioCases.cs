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
/// An iteration of <c>add each</c> (ADR-0016 §3, note 37, and note 90 of 2026-11-14): the new trades its case adds
/// together, and the existing trades its case takes out of After. <paramref name="Package"/> names the package the
/// trades' Package cells make it of; none for a new trade priced alone, the iteration of old, and for a case made
/// before a package was a cell.
/// </summary>
public sealed record Iteration(IReadOnlyList<string> Add, IReadOnlyList<string> Without, string? Package = null)
{
    /// <summary>
    /// The case's title (note 90): <c>add DRAFT-3</c> for a trade alone, <c>P1: add DRAFT-1 + DRAFT-2 without E00003</c>
    /// for a package, and <c>P2: take out E00007</c> for one that only takes trades out. A title holds no comma, so a
    /// list of them is told apart by commas; a case made before a package was a cell says so as it was made, with no
    /// name: <c>add DRAFT-1 without E00003</c>.
    /// </summary>
    public string Title()
    {
        static string Joined(IReadOnlyList<string> ids) => string.Join(" + ", ids);
        var said = Add.Count == 0 ? $"take out {Joined(Without)}"
            : Without.Count == 0 ? $"add {Joined(Add)}" : $"add {Joined(Add)} without {Joined(Without)}";
        return Package is null ? said : $"{Package}: {said}";
    }

    /// <summary>Whether two iterations are of one package, add the same trades and take the same out, in whatever order each names them.</summary>
    public bool Same(Iteration other) => string.Equals(Package, other.Package, StringComparison.Ordinal)
        && Add.ToHashSet().SetEquals(other.Add) && Without.ToHashSet().SetEquals(other.Without);

    /// <summary>The iteration as its package and its two lists, <c>add</c> and <c>without</c>: as a case stores it, and as a Run freezes it.</summary>
    public JsonObject Lists() => new()
    {
        ["package"] = Package,
        ["add"] = new JsonArray([.. Add.Select(id => JsonValue.Create(id))]),
        ["without"] = new JsonArray([.. Without.Select(id => JsonValue.Create(id))]),
    };

    /// <summary>The ids a list holds. Null where it is no list, which is its reader's to refuse or to say is none.</summary>
    public static IReadOnlyList<string>? Ids(JsonNode? list) => list is JsonArray ids ? [.. ids.Select(id => id!.GetValue<string>())] : null;
}

/// <summary>What <c>vary</c> did: the state it left, the cases it made by title, and each value it skipped with why.</summary>
public sealed record VaryAnswer(WorkspaceState State, IReadOnlyList<string> Made, IReadOnlyList<string> Skipped);

/// <summary>What <c>add each</c> did: the state it left, the cases it made by title, those of the set it replaced, and
/// what the package it was given wrote, where it was given one (ADR-0016 note 90).</summary>
public sealed record AddAnswer(WorkspaceState State, IReadOnlyList<string> Made, IReadOnlyList<string> Replaced, string? Wrote = null);

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
        var changed = made.Any(c => KindOf(c) == Add) ? ChangedInBase(Base(state)) : [];
        CaseView View(CreatedEntity c)
        {
            var iteration = KindOf(c) == Add ? IterationOf(c) : null;
            return new(Entity.Split(c.Entity).Id, Title(c), KindOf(c), state.Overrides.Keys.Count(t => t.Layer == c.Entity),
                iteration, iteration is null ? null : CommonChange(changed, iteration.Without));
        }
        return [new(BaseId, BaseTitle, "base", 0), .. made.Select(View)];
    }

    /// <summary>A case's title, as it was made.</summary>
    public static string Title(CreatedEntity c) => c.Fields["title"]!.GetValue<string>();

    /// <summary>A case's kind: <c>vary</c> or <c>add</c>.</summary>
    public static string KindOf(CreatedEntity c) => c.Fields["kind"]!.GetValue<string>();

    /// <summary>
    /// The iteration a case of iterative addition was made of. A case stored before an iteration was a list names one
    /// <c>trade</c> (ADR-0016 note 37): the iteration of that trade alone, which is what it stored. One stored before a
    /// package was a cell holds no <c>package</c> (note 90): it was of none, which is what it stored.
    /// </summary>
    public static Iteration IterationOf(CreatedEntity c)
    {
        IReadOnlyList<string> Ids(string list) => Iteration.Ids(c.Fields[list])
            ?? throw new InvalidOperationException($"The case {Title(c)} of add each does not say its trades: it holds no list {list}.");
        return c.Fields["trade"] is { } alone ? new([alone.GetValue<string>()], [])
            : new(Ids("add"), Ids("without"), c.Fields["package"]?.GetValue<string>());
    }

    /// <summary>The iterations of iterative addition, one per case, in the order their cases were made.</summary>
    public static IReadOnlyList<Iteration> Iterations(WorkspaceState state) => [.. Made(state).Where(c => KindOf(c) == Add).Select(IterationOf)];

    // The existing trades whose After the Base holds otherwise than their Before: the common change of docs/03 §5.
    static IReadOnlyList<EffectiveTrade> ChangedInBase(WorkspaceState baseline) =>
        [.. PortfolioMaterialiser.Trades(baseline).Where(t => !t.IsNew && t.Before != t.After)];

    /// <summary>
    /// The common change a case of iterative addition carries (docs/03 §5): the existing trades the Base moves, but for
    /// those the case takes out of After, which it does not carry (an existing trade in After alone may be in a
    /// package, ADR-0016 note 91). Null where it carries none, so no case says a change it does not carry.
    /// </summary>
    static string? CommonChange(IReadOnlyList<EffectiveTrade> changedInBase, IReadOnlyList<string> takenOut)
    {
        var moved = changedInBase.Where(t => !takenOut.Contains(t.TradeId)).ToList();
        if (moved.Count == 0) return null;
        static string Named(List<EffectiveTrade> trades) =>
            trades.Count <= 3 ? $"existing {string.Join(", ", trades.Select(t => t.TradeId))}" : $"{trades.Count} existing trades";
        var parts = new List<string>();
        if (moved.Where(t => t.Before).ToList() is { Count: > 0 } outOf) parts.Add($"{Named(outOf)} out of After");
        if (moved.Where(t => t.After).ToList() is { Count: > 0 } into) parts.Add($"{Named(into)} in After only");
        return $"with {string.Join(" and ", parts)}";
    }

    /// <summary>
    /// Refuses a set of iterative addition that is no longer what the trades' Package cells and A say (docs/03 §5: never
    /// another set quietly; ADR-0016 notes 37 and 90), naming the cases that differ; and packages written that no case
    /// of add each prices, which would otherwise go unpriced in silence. What the cells say that no set could be made
    /// of is refused as <c>add each</c> refuses it. <c>add each</c> again makes the set anew.
    /// </summary>
    public static void CheckAdditionSet(WorkspaceState raw)
    {
        var made = Iterations(raw);
        var trades = PortfolioMaterialiser.Trades(Base(raw));
        var written = PortfolioPackages.Named(trades);
        if (made.Count == 0)
        {
            if (written.Count == 0) return;
            var names = written.Select(p => p.Name).ToList();
            throw new WorkspaceRefused("PackagesNotMade", $"The Package column names {TableWords.Some(names)}, and no case of add each prices {(names.Count == 1 ? "it" : "them")}. "
                + "Run add each to make the cases, or clear the column with reset all package to price without them.");
        }
        var wanted = PortfolioPackages.AdditionSetOrNone(trades);
        var missing = wanted.Where(w => !made.Any(w.Same)).Select(w => w.Title()).ToList();
        var stale = made.Where(m => !wanted.Any(m.Same)).Select(m => m.Title()).ToList();
        if (missing.Count == 0 && stale.Count == 0) return;
        // Titles are told apart by commas, as add each's answer lists them: `and` would read as one case's trades.
        static string Titles(List<string> titles) => string.Join(", ", titles.Take(3)) + (titles.Count > 3 ? $" and {titles.Count - 3} more" : "");
        const string differs = "add each's cases are not what the Package column and A say";
        // Where the cells now make no case, add each again would refuse: the way out is to remove the cases, by the
        // command that removes one as the line or the AI says it, or to write what a case is made of.
        if (wanted.Count == 0)
            throw new WorkspaceRefused("AdditionSetChanged",
                $"{differs}, which make no case now. Out of date: {Titles(stale)}. "
                + "Remove them with case remove or in Changes, or tick A on a new trade or write a package and run add each again.");
        var problems = new List<string>();
        if (missing.Count > 0) problems.Add($"Missing: {Titles(missing)}.");
        if (stale.Count > 0) problems.Add($"Out of date: {Titles(stale)}.");
        throw new WorkspaceRefused("AdditionSetChanged", $"{differs}. {string.Join(" ", problems)} Run add each again to make the set anew.");
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
    // A column a case of vary takes values of, which every cell is but a trade's package (ADR-0016 note 90): that one is
    // named, for vary to say why it makes no case of it, and never offered.
    sealed record Column(string Field, FieldSpec Spec, params string[] Names)
    {
        public bool Varies => Field != PortfolioPackages.Field;
    }

    // A column that follows others (ADR-0016 note 66) is no cell: a case varies the columns it follows. It is kept by
    // its field and its names, so that whoever names it, a line or a page, is told which those are.
    sealed record Follower(string Field, IReadOnlyList<string> Names, IReadOnlyList<string> Followed)
    {
        public WorkspaceRefused Refusal(string? word = null) => new("NotEditable", TableWords.NoCommandSets(Names[0], Followed), word: word, candidates: Followed);
    }

    // The columns of a kind of row, from the one list of its table's fields: the cells, and the ones that follow others.
    sealed record ColumnSet(IReadOnlyList<Column> Cells, IReadOnlyList<Follower> Followers);

    // A cell is a field the registered tables say a command may set (ADR-0016 §1), named by that field's words and
    // read and written by its form. A figure of the rows under a row is no cell: it holds no value of its own for a
    // case to vary, and is written as those rows (ADR-0016 note 47).
    static ColumnSet ColumnsIn(IReadOnlyList<TableField> fields) => new(
        [.. fields.Where(f => f.Editable && f.Figure is null).Select(f => new Column(f.Name, f.Spec, [.. f.Words]))],
        [.. fields.Where(f => f.From is not null).Select(f => new Follower(f.Name, [.. f.Words], TableWords.WordsFollowed(f.From!, fields)))]);

    static readonly ColumnSet CsaColumns = ColumnsIn(PortfolioFields.Csa), NettingColumns = ColumnsIn(PortfolioFields.Netting),
        // The counterparty's CDS choice, row `credit`, and the chosen curve's spreads, row `cds` (ADR-0010 §8's note of 2026-11-09).
        CreditColumns = ColumnsIn(PortfolioFields.Credit), SpreadColumns = ColumnsIn(PortfolioFields.Cds);

    /// <summary>The row that names the counterparty's CDS choice.</summary>
    public const string CreditRow = "credit";

    /// <summary>The row that names the chosen CDS curve's spreads.</summary>
    public const string CdsRow = "cds";

    // A row a cell is named on: its id as a line says it, its entity, its columns, what it is in a word or two, and
    // whether it is a trade, of which there may be ten thousand.
    sealed record Row(string Id, string Entity, ColumnSet Columns, string What, bool IsTrade = false);

    // The rows a cell is named on, as the state stands: each CSA, the netting set, the credit, the chosen curve where
    // there is one, each trade by its id, and a module's settings by the module's id (ADR-0010 §8: `set xva cost of
    // capital 12%`).
    static IReadOnlyList<Row> Rows(WorkspaceState state, MeasureRegister register)
    {
        var source = PortfolioSourceVersion.Of(state);
        var trade = Trade(register);
        return
        [
            .. source.Agreement.Csas.Select(c => new Row(c.CsaId, Entity.Of(Entity.Csa, c.CsaId), CsaColumns, "a CSA")),
            new(source.Agreement.NettingSetId, Entity.Of(Entity.Netting, source.Agreement.NettingSetId), NettingColumns, "the netting set"),
            new(CreditRow, Entity.Of(Entity.Credit, source.Registration.Key), CreditColumns, "the counterparty's CDS choice"),
            .. PortfolioMaterialiser.Credit(state).Cds is { } cds ? new[] { new Row(CdsRow, Entity.Of(Entity.Cds, cds.CurveId), SpreadColumns, "the chosen curve's spreads") } : [],
            .. source.Existing.Concat(source.New).Select(t => new Row(t.TradeId, Entity.Of(Entity.Trade, t.TradeId), trade, "a trade", IsTrade: true)),
            .. register.Modules.Where(m => m.Settings.Count > 0).Select(m => new Row(m.Id, MeasureSettings.EntityOf(m.Id), Settings(m), $"{m.Title}'s settings")),
        ];
    }

    // Whether a word as typed says a name: whatever its case, and whatever spaces stand around it.
    static bool Says(string typed, string name) => string.Equals(typed.Trim(), name, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The cell a row and a column name, as the state stands. A row or a column that names nothing, or more than one,
    /// is refused with what it could mean, never guessed: the refusal names the word and what it could have been
    /// (ADR-0016 note 45), where a line can say it. A trade's id is a row too, and no candidate: there may be ten
    /// thousand.
    /// </summary>
    public static CellName Resolve(WorkspaceState state, MeasureRegister register, string row, string column)
    {
        var source = PortfolioSourceVersion.Of(state);
        var credit = PortfolioMaterialiser.Credit(state);
        if (Says(row, CdsRow) && credit.Cds is null)
            throw new WorkspaceRefused("UnknownCell", credit.Missing ?? throw new InvalidOperationException("A credit with no curve says why."), word: row.Trim());
        var all = Rows(state, register);
        var rows = all.Where(r => Says(row, r.Id)).ToList();
        if (rows.Count == 0)
            throw new WorkspaceRefused("UnknownCell", $"No row {row.Trim()}: a row is a CSA ({string.Join(", ", source.Agreement.Csas.Select(c => c.CsaId))}), "
                + $"the netting set ({source.Agreement.NettingSetId}), {CreditRow},{(credit.Cds is null ? "" : $" {CdsRow},")} a trade's id{string.Concat(register.Modules.Where(m => m.Settings.Count > 0).Select(m => $", or {m.Id} for {m.Title}'s settings"))}.",
                word: row.Trim(), candidates: [.. all.Where(r => !r.IsTrade).Select(r => r.Id)]);
        if (rows.Count > 1) throw new WorkspaceRefused("AmbiguousCell", $"{row.Trim()} names {string.Join(" and ", rows.Select(r => r.Entity))}.", word: row.Trim());
        var (id, entity, columns) = (rows[0].Id, rows[0].Entity, rows[0].Columns.Cells);
        var name = Words(column);
        bool Named(IReadOnlyList<string> names) => names.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        var found = columns.Where(c => Named(c.Names)).ToList();
        // A column that follows others is no cell: the line is told which to name in its place, as `set` is (ADR-0016 note 66).
        if (found.Count == 0 && rows[0].Columns.Followers.FirstOrDefault(f => Named(f.Names)) is { } follower) throw follower.Refusal(column.Trim());
        if (found.Count == 0)
            throw new WorkspaceRefused("UnknownCell", $"{id} has no column {column.Trim()}; its columns are {string.Join(", ", columns.Where(c => c.Varies).Select(c => c.Names[0]))}.",
                word: column.Trim(), candidates: [.. columns.Where(c => c.Varies).Select(c => c.Names[0])]);
        if (found.Count > 1)
            throw new WorkspaceRefused("AmbiguousCell", $"{column.Trim()} could be {string.Join(" or ", found.Select(c => c.Names[0]))} of {id}.",
                word: column.Trim(), candidates: [.. found.Select(c => c.Names[0])]);
        return new CellName(entity, found[0].Field, id, found[0].Names[0], found[0].Spec);
    }

    /// <summary>
    /// What could follow in a line that names a cell and then its values, as far as <paramref name="text"/> is typed
    /// (<c>vary</c>, ADR-0016 note 45): the rows a cell is named on; after a row, its columns; after a column, what its
    /// form has to choose from, and again after each comma. A row or a column the state has not offers nothing: the
    /// reading says which.
    /// </summary>
    public static LineCompletions Offered(WorkspaceState state, MeasureRegister register, string text)
    {
        // No space after the command's own words: they are still being typed, and nothing follows yet.
        if (text.Length == 0) return LineCompletions.None;
        var rows = Rows(state, register);
        var comma = text.IndexOf(',');
        return comma < 0 ? BeforeAComma(rows, text) : AfterAComma(rows, text, comma);
    }

    // The row a word says, and the column of a row that words say by any of its names, where they say one.
    static Row? RowSaid(IReadOnlyList<Row> rows, string word) => rows.Where(r => Says(word, r.Id)).ToList() is [var one] ? one : null;

    static Column? ColumnSaid(Row row, IEnumerable<string> words) =>
        row.Columns.Cells.Where(c => c.Names.Any(n => Says(string.Join(' ', words), n))).ToList() is [var one] ? one : null;

    // With no comma yet: the row, then its column, then the first value.
    static LineCompletions BeforeAComma(IReadOnlyList<Row> rows, string text)
    {
        var words = LineWords.Of(text);
        // The word being typed is the last, or an empty one after a space; the words before it are done.
        var open = !char.IsWhiteSpace(text[^1]);
        var (typing, at) = open ? words[^1] : ("", text.Length);
        var done = open ? [.. words.SkipLast(1)] : words;
        if (done.Count == 0) return RowsStarted(rows, typing, at);
        if (RowSaid(rows, done[0].Word) is not { } row) return LineCompletions.None;
        var said = done.Skip(1).Select(w => w.Word).ToList();
        // The words name one column: its value is awaited.
        if (said.Count > 0 && ColumnSaid(row, said) is { } column) return Values(column.Spec, typing, at, []);
        // No word yet, or words that start a column's name: the columns they could be, each by the word that names it
        // and written from the column's first word.
        var start = string.Join(' ', said.Append(typing));
        return new([.. row.Columns.Cells.Where(c => c.Varies && c.Names.Any(n => n.StartsWith(start, StringComparison.OrdinalIgnoreCase)))
            .Select(c => new LineCompletion(c.Names[0], done.Count > 1 ? done[1].Start : at))]);
    }

    // Rows by their ids as far as typed, an id that starts or ends with it, as `set` offers rows (note 15): those that
    // are no trade first, a few at a time with how many more, each with what it is.
    static LineCompletions RowsStarted(IReadOnlyList<Row> rows, string typing, int at)
    {
        var found = rows.Where(r => r.Id.StartsWith(typing, StringComparison.OrdinalIgnoreCase) || r.Id.EndsWith(typing, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.IsTrade).ToList();
        return new([.. found.Take(LineCompletions.RowsListed).Select(r => new LineCompletion(r.Id, at, r.What))], Math.Max(0, found.Count - LineCompletions.RowsListed));
    }

    // After a comma a value is typed. As the line is read, the first value is the last word before the first comma,
    // and the cell the words before it.
    static LineCompletions AfterAComma(IReadOnlyList<Row> rows, string text, int comma)
    {
        var cell = LineWords.Of(text[..comma]);
        if (cell.Count < 3 || RowSaid(rows, cell[0].Word) is not { } row || ColumnSaid(row, cell.Skip(1).SkipLast(1).Select(w => w.Word)) is not { } column)
            return LineCompletions.None;
        var after = text.LastIndexOf(',') + 1;
        var at = text.Length - text[after..].TrimStart().Length;
        return Values(column.Spec, text[at..], at, text[cell[^1].Start..after].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
    }

    // What a column's form has to choose from, as far as a value is typed (`FieldForms.ChoicesWritten`), without the
    // values listed already, however each was written: `yes` is `on`.
    static LineCompletions Values(FieldSpec spec, string typing, int at, IReadOnlyList<string> listed)
    {
        var taken = listed.Select(l => FieldForms.Read(spec, l) is { Problem: FieldForms.Problem.None } read ? FieldForms.Write(spec, read.Value) : l)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new([.. FieldForms.ChoicesWritten(spec).Where(v => !taken.Contains(v) && v.StartsWith(typing, StringComparison.OrdinalIgnoreCase)).Select(v => new LineCompletion(v, at))]);
    }

    /// <summary>The cell of an entity's field, named as <see cref="Resolve"/> names it; refused where it is no cell.</summary>
    public static CellName Of(WorkspaceState state, MeasureRegister register, string entity, string field)
    {
        var (kind, id) = Entity.Split(entity);
        var set = ColumnsOf(entity, register)
            ?? throw new WorkspaceRefused("UnknownCell", $"{entity} is not a row a cell is named on: a row is a CSA, the netting set, the credit, the CDS, a trade or a module's settings.");
        // A field that follows others is no cell by its name either (ADR-0016 note 66): the page and the AI are told as a line is.
        if (set.Followers.FirstOrDefault(f => f.Field == field) is { } follower) throw follower.Refusal();
        var columns = set.Cells;
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
        var source = PortfolioSourceVersion.Of(state);
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
        var column = exists ? ColumnsOf(entity, register)?.Cells.FirstOrDefault(c => c.Field == field) : null;
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

    static ColumnSet? ColumnsOf(string entity, MeasureRegister register) => Entity.Split(entity).Kind switch
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
    static ColumnSet Settings(IMeasureModule module) => ColumnsIn(PortfolioFields.Settings(module));

    /// <summary>
    /// A value as the Changes tab and a case's title write it, in the form <see cref="Read"/> reads back to the same value
    /// (<see cref="FieldForms.Write"/>): an amount never bare, since a bare number is read as millions; empty for none.
    /// </summary>
    public static string Write(CellName cell, JsonNode? value) => FieldForms.Write(cell.Spec, value);

    // A trade's own cells, then each module's typed column (ADR-0010 §10).
    static ColumnSet Trade(MeasureRegister register) => ColumnsIn(PortfolioFields.Trade(register));

    static string Words(string text) => string.Join(' ', text.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>A value as written for a cell of the form, or why it cannot be read; never a guess at what was meant.</summary>
    public static JsonNode? Read(CellName cell, string text)
    {
        var read = FieldForms.Read(cell.Spec, text);
        if (read.Problem == FieldForms.Problem.None) return read.Value;
        var written = text.Trim();
        // The value is the word at fault, with what the cell's form has to choose from (ADR-0016 note 45).
        throw new WorkspaceRefused("InvalidValue", $"{cell.Row} {cell.Column} is {read.What}; {(written.Length == 0 ? "nothing was written" : $"{written} is not")}.",
            word: written.Length == 0 ? null : written, candidates: FieldForms.ChoicesWritten(cell.Spec));
    }

    /// <summary>The value a reading holds in the cell now, as an edit would write it.</summary>
    public static JsonNode? Current(WorkspaceState reading, MeasureRegister register, CellName cell)
    {
        var (kind, id) = Entity.Split(cell.Entity);
        switch (kind)
        {
            case Entity.Csa:
                return PortfolioFields.Value(reading, PortfolioMaterialiser.Csas(reading).Single(x => x.CsaId == id), cell.Field);
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
                return register.ColumnOf(cell.Field) is null ? PortfolioFields.Value(reading, t, cell.Field)
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
