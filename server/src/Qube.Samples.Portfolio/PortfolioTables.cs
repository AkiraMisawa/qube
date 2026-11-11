using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Qube.Composition.Application;
using Qube.Composition.Application.Tables;
using Qube.Composition.Application.Workspaces;

namespace Qube.Samples.Portfolio;

// ADR-0016 §1: the pricer registers its tables as data. The kernel's verbs address `table row field` through these and
// know no trade, CSA or currency; what a write is, and whether a value may be written, stays here.

/// <summary>
/// The fields of the pricer's tables, said once (ADR-0016 §1): a command, <c>vary</c> and the Changes tab name a cell by
/// these words and read and write its value by these forms. A field's first word is the one a sentence uses.
/// </summary>
public static partial class PortfolioFields
{
    /// <summary>The field that names the row of a table of one row.</summary>
    public const string Row = "id";

    /// <summary>The display currencies, as a field and the user's setting read them (ADR-0017 §6.1).</summary>
    internal static readonly IReadOnlyDictionary<string, string> Currencies = Choice(PortfolioMarket.Currencies);

    static readonly IReadOnlyDictionary<string, string> Frequencies = Choice(PortfolioCatalog.Frequencies), Schedules = Choice(["3M", "6M"]);

    // A choice read by its own words, case ignored.
    static IReadOnlyDictionary<string, string> Choice(IEnumerable<string> values) => values.ToDictionary(v => v.ToLowerInvariant(), v => v);

    static TableField OneRow => new(Row, "Row", [Row], FieldForm.Id);

    const string Counterparty = "Counterparty", Self = "Self";

    /// <summary>A CSA's terms (ADR-0010 §8): each party's threshold and minimum transfer amount in that party's currency.</summary>
    public static IReadOnlyList<TableField> Csa { get; } =
    [
        new("csaId", "CSA", ["csa id", "id"], FieldForm.Id),
        new("type", "Type", ["type"], FieldForm.Id),
        new("counterpartyThreshold", "Threshold", ["threshold", "counterparty threshold"], FieldForm.Amount, Editable: true, UnitField: "counterpartyCurrency", Group: Counterparty),
        new("counterpartyMta", "MTA", ["mta", "counterparty mta"], FieldForm.Amount, Editable: true, UnitField: "counterpartyCurrency", Group: Counterparty),
        new("counterpartyCurrency", "Currency", ["counterparty currency"], FieldForm.Choice, Editable: true, Choices: Currencies, Group: Counterparty),
        new("selfThreshold", "Threshold", ["self threshold"], FieldForm.Amount, Editable: true, UnitField: "selfCurrency", Group: Self),
        new("selfMta", "MTA", ["self mta"], FieldForm.Amount, Editable: true, UnitField: "selfCurrency", Group: Self),
        new("selfCurrency", "Currency", ["self currency"], FieldForm.Choice, Editable: true, Choices: Currencies, Group: Self),
        new("currency", "Currency", ["currency", "ccy"], FieldForm.Choice, Editable: true, Choices: Currencies),
        new("curve", "Discount curve", ["curve", "discount curve", "discount"], FieldForm.Id, Editable: true),
        new("fvaCurve", "FVA discount curve", ["fva curve", "fva discount curve", "fva discount"], FieldForm.Id, Editable: true),
        new("frequency", "Frequency", ["frequency"], FieldForm.Choice, Editable: true, Choices: Frequencies),
        new("exchange", "Exchange", ["exchange"], FieldForm.Flag, Editable: true),
        new("oneWay", "One-way", ["one-way", "one way", "oneway"], FieldForm.Flag, Editable: true),
    ];

    /// <summary>The netting set: its CVA recovery, which may be empty (XV15).</summary>
    public static IReadOnlyList<TableField> Netting { get; } =
    [
        new("nettingSetId", "Netting set", ["netting set", "id"], FieldForm.Id),
        new("cvaRecovery", "CVA recovery", ["recovery", "cva recovery"], FieldForm.Rate, Editable: true, Optional: true),
    ];

    /// <summary>The counterparty's CDS choice (ADR-0010 §8's note of 2026-11-09): each key is the catalogue's, or empty.</summary>
    public static IReadOnlyList<TableField> Credit { get; } =
    [
        OneRow,
        new("cdsType", "CDS type", ["cds type", "type"], FieldForm.Id, Editable: true, Optional: true),
        new("cdsReference", "CDS reference", ["cds reference", "reference"], FieldForm.Id, Editable: true, Optional: true),
        new("proxySector", "Proxy sector", ["proxy sector", "sector"], FieldForm.Id, Editable: true, Optional: true),
        new("proxyRegion", "Proxy region", ["proxy region", "region"], FieldForm.Id, Editable: true, Optional: true),
        new("proxyRating", "Proxy rating", ["proxy rating", "rating"], FieldForm.Id, Editable: true, Optional: true),
    ];

    /// <summary>The chosen CDS curve's spreads, a field a tenor.</summary>
    public static IReadOnlyList<TableField> Cds { get; } = [OneRow, .. CdsCatalog.Tenors.Select(t => new TableField(t, t, [t], FieldForm.Spread, Editable: true))];

    /// <summary>The workspace's own settings (ADR-0010 §1): the currency its figures are shown in.</summary>
    public static IReadOnlyList<TableField> Workspace { get; } =
    [
        OneRow,
        new(Entity.DisplayCurrency, "Display currency", ["currency", "ccy", "display currency"], FieldForm.Choice, Editable: true, Choices: Currencies),
    ];

    /// <summary>A module's settings (ADR-0010 §8), named by their titles' words and their own names.</summary>
    public static IReadOnlyList<TableField> Settings(IMeasureModule module) =>
        [OneRow, .. module.Settings.Select(s => new TableField(s.Id, s.Title, MeasureSettings.Names(s), s.Form, Editable: true, Optional: MeasureSettings.Spec(s).Optional))];

    /// <summary>
    /// A trade's fields: the source's terms, which a condition reads; what the user sets; and each module's typed column
    /// (ADR-0010 §10), named by its id or its title, such as XVA's Margin. A module's result column, such as PV01, is not a
    /// field: it changes with no edit, so a condition on it would depend on when it was asked. A module's amount is in
    /// the trade's currency and is typed as its grid cell is, a plain number (-3000, -3k), never in millions.
    /// </summary>
    public static IReadOnlyList<TableField> Trade(MeasureRegister register) =>
    [
        new("tradeId", "Trade", ["trade id", "id"], FieldForm.Id),
        new("product", "Product", ["product"], FieldForm.Id),
        new("currency", "Currency", ["ccy", "currency"], FieldForm.Choice, Choices: Currencies),
        new("notional", "Notional", ["notional"], FieldForm.SignedAmount, Editable: true, UnitField: "currency"),
        new("maturity", "Maturity", ["maturity"], FieldForm.Date),
        new("fixedRate", "Fixed rate", ["rate", "fixed rate"], FieldForm.Rate, Editable: true, Optional: true),
        new("schedule", "Schedule", ["schedule"], FieldForm.Choice, Editable: true, Choices: Schedules),
        new("lastFixing", "Last fixing", ["last fixing"], FieldForm.Rate, Optional: true),
        new("officialPv", "Official PV", ["official pv"], FieldForm.SignedAmount, Optional: true, UnitField: "currency"),
        new("csa", "CSA", ["csa"], FieldForm.Id, Editable: true, Table: PortfolioTables.Csas),
        new("curve", "Discount curve", ["curve", "discount curve"], FieldForm.Id, Editable: true),
        new("imR", "IM-R", ["im-r", "imr"], FieldForm.Id, Editable: true, Optional: true),
        new("imP", "IM-P", ["im-p", "imp"], FieldForm.Id, Editable: true, Optional: true),
        new("before", "Before", ["B", "before"], FieldForm.Flag, Editable: true),
        new("after", "After", ["A", "after"], FieldForm.Flag, Editable: true),
        .. register.TradeColumns.Where(c => c.Editable).Select(c => new TableField(c.Field, c.Title,
            [.. new[] { Words(c.Field[(c.Field.IndexOf('.') + 1)..]), Words(c.Title), Words(Unit().Replace(c.Title, "")) }.Distinct()], FieldForm.Number, Editable: true,
            UnitField: c.Type == "amount" ? "currency" : null)),
    ];

    /// <summary>The value a trade holds in one of its own fields, as an edit would write it. A module's column is its cell's, not the trade's.</summary>
    public static JsonNode? Value(EffectiveTrade t, string field) => field switch
    {
        "tradeId" => t.TradeId, "product" => t.Product, "currency" => t.Currency, "notional" => t.Notional, "maturity" => t.Maturity,
        "fixedRate" => t.FixedRate, "schedule" => t.Schedule, "lastFixing" => t.LastFixing, "officialPv" => t.OfficialPv, "csa" => t.Csa, "curve" => t.Curve,
        "imR" => t.ImR, "imP" => t.ImP, "before" => t.Before, "after" => t.After,
        _ => throw new InvalidOperationException($"A trade has no {field}."),
    };

    /// <summary>The value a CSA holds in a field.</summary>
    public static JsonNode? Value(EffectiveCsa c, string field) => field switch
    {
        "csaId" => c.CsaId, "type" => c.Type,
        "counterpartyThreshold" => c.CounterpartyThreshold, "counterpartyMta" => c.CounterpartyMta, "counterpartyCurrency" => c.CounterpartyCurrency,
        "selfThreshold" => c.SelfThreshold, "selfMta" => c.SelfMta, "selfCurrency" => c.SelfCurrency,
        "currency" => c.Currency, "curve" => c.Curve, "fvaCurve" => c.FvaCurve, "frequency" => c.Frequency,
        "exchange" => c.Exchange, "oneWay" => c.OneWay,
        _ => throw new InvalidOperationException($"A CSA has no {field}."),
    };

    /// <summary>The value the counterparty's CDS choice holds in a key.</summary>
    public static JsonNode? Value(CdsChoice choice, string field) => field switch
    {
        "cdsType" => choice.Type, "cdsReference" => choice.Reference, "proxySector" => choice.Sector, "proxyRegion" => choice.Region, "proxyRating" => choice.Rating,
        _ => throw new InvalidOperationException($"The credit has no {field}."),
    };

    static string Words(string text) => string.Join(' ', text.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));

    [GeneratedRegex("\\s*\\([^)]*\\)")]
    private static partial Regex Unit();
}

/// <summary>
/// The pricer's tables (ADR-0016 §1): the trades, with the grids <c>new</c> and <c>existing</c> as subsets and B and A as
/// whole-grid edits (ADR-0010 §6); the CSAs; and, each a table of one row, the netting set, the counterparty's credit,
/// the chosen CDS curve, the workspace's own settings and each module's settings. A row is read on the Base, and a
/// command's edit is the Common one a typed cell makes, checked as that cell is (<see cref="PortfolioChecks"/>).
/// </summary>
public sealed class PortfolioTables(MeasureRegister register) : ITableCatalog
{
    public const string Trades = "xva.trades";
    public const string Csas = "xva.csas";
    public const string Netting = "xva.netting";
    public const string Credit = "xva.credit";
    public const string Cds = "xva.cds";
    public const string Workspace = "xva.workspace";

    /// <summary>The table of a module's settings, such as <c>xva.settings.xva</c>.</summary>
    public static string SettingsOf(string module) => $"xva.settings.{module}";

    public IEnumerable<IWorkspaceTable> Tables { get; } = Build(register, new PortfolioChecks(register));

    static IReadOnlyList<IWorkspaceTable> Build(MeasureRegister register, PortfolioChecks checks) =>
    [
        new TradeTable(register, checks),
        // `csa` alone is a trade's field, which a line with trades selected sets (ADR-0016 note 18 of step 2): the table is `csas`.
        new Table(checks, Described(Csas, "CSAs", ["csas"], "csaId", PortfolioFields.Csa), reading =>
            [.. PortfolioMaterialiser.Csas(reading).Select(c => new Row(reading, Entity.Of(Entity.Csa, c.CsaId), c.CsaId, null, f => PortfolioFields.Value(c, f)))],
            row => $"{row.Key} is a CSA of the agreement, which the source holds: a command changes its terms and never removes it."),
        OneRow(checks, Described(Netting, "Netting set", ["netting"], "nettingSetId", PortfolioFields.Netting, one: true), reading =>
        {
            var id = Source(reading).Agreement.NettingSetId;
            var recovery = PortfolioMaterialiser.Credit(reading).CvaRecovery;
            return new Row(reading, Entity.Of(Entity.Netting, id), id, null, f => f == "nettingSetId" ? (JsonNode?)id : recovery);
        }),
        OneRow(checks, Described(Credit, "Credit", [PortfolioCells.CreditRow], PortfolioFields.Row, PortfolioFields.Credit, one: true), reading =>
        {
            var choice = PortfolioMaterialiser.Credit(reading).Choice;
            return new Row(reading, Entity.Of(Entity.Credit, Source(reading).Registration.Key), PortfolioCells.CreditRow, null,
                f => f == PortfolioFields.Row ? PortfolioCells.CreditRow : PortfolioFields.Value(choice, f));
        }),
        // The curve the Base chose: no row while it has none, and the credit says why (ADR-0010 §8's note of 2026-11-09).
        OneRow(checks, Described(Cds, "CDS", [PortfolioCells.CdsRow], PortfolioFields.Row, PortfolioFields.Cds, one: true),
            reading => PortfolioMaterialiser.Credit(reading).Cds is not { } cds ? null
                : new Row(reading, Entity.Of(Entity.Cds, cds.CurveId), PortfolioCells.CdsRow, null, f => f == PortfolioFields.Row ? PortfolioCells.CdsRow : cds.Spreads[f]),
            reading => PortfolioMaterialiser.Credit(reading).Missing),
        OneRow(checks, Described(Workspace, "Workspace", [Entity.WorkspaceId], PortfolioFields.Row, PortfolioFields.Workspace, one: true), reading =>
        {
            var currency = PortfolioMaterialiser.DisplayCurrency(reading);
            return new Row(reading, Entity.WorkspaceSettings, Entity.WorkspaceId, null, f => f == PortfolioFields.Row ? Entity.WorkspaceId : currency);
        }),
        // A module's settings, the table and its row named by the module's id (ADR-0010 §8): `set xva cost of capital 12%`.
        .. register.Modules.Where(m => m.Settings.Count > 0).Select(m =>
            OneRow(checks, Described(SettingsOf(m.Id), $"{m.Title} settings", [m.Id], PortfolioFields.Row, PortfolioFields.Settings(m), one: true), reading =>
                new Row(reading, MeasureSettings.EntityOf(m.Id), m.Id, null,
                    f => f == PortfolioFields.Row ? m.Id : MeasureSettings.Value(reading, m.Id, m.Settings.Single(s => s.Id == f))?.DeepClone()))),
    ];

    static TableDescriptor Described(string id, string title, IReadOnlyList<string> words, string key, IReadOnlyList<TableField> fields, bool one = false, bool main = false,
        IReadOnlyList<TableSubset>? subsets = null, IReadOnlyList<TableAction>? actions = null) =>
        new(id, PortfolioContracts.Version, PortfolioEditRules.App, title, words, key, fields, subsets ?? [], one, main, actions);

    // What a trade grid's Context Menu offers on the rows selected in it (ADR-0016 §4, note 25 of step 2): each side's
    // membership, out and in, by the line that does it.
    static readonly IReadOnlyList<TableAction> TradeActions =
    [
        new("Take out of After", "set selected after off"), new("Put into After", "set selected after on"),
        new("Take out of Before", "set selected before off"), new("Put into Before", "set selected before on"),
    ];

    static Table OneRow(PortfolioChecks checks, TableDescriptor descriptor, Func<WorkspaceState, Row?> row, Func<WorkspaceState, string?>? whyEmpty = null) =>
        new(checks, descriptor, reading => row(reading) is { } one ? [one] : [],
            _ => $"{descriptor.Words[0]} is always one row: a command changes its values and never removes it.", whyEmpty);

    static PortfolioSourceVersion Source(WorkspaceState state) => (PortfolioSourceVersion)state.Source;

    /// <summary>A row as a command reads it: the Base it was read on, the entity its edits are on (<c>trade:ID</c>), and its values by field.</summary>
    class Row(WorkspaceState reading, string entity, string key, string? subset, Func<string, JsonNode?> read) : ITableRow
    {
        public WorkspaceState Reading => reading;
        public string EntityId => entity;
        public string Key => key;
        public string? Subset => subset;
        public JsonNode? Value(string field) => read(field);
    }

    /// <summary>A table whose rows are entities of the workspace and whose fields are edited one by one, each checked as its cell is.</summary>
    class Table(PortfolioChecks checks, TableDescriptor descriptor, Func<WorkspaceState, IReadOnlyList<ITableRow>> rows, Func<Row, string> notRemovable,
        Func<WorkspaceState, string?>? whyEmpty = null) : IWorkspaceTable
    {
        protected PortfolioChecks Checks => checks;

        /// <summary>The table with every field it can have; a workspace may have fewer (<see cref="Describe"/>).</summary>
        protected TableDescriptor Whole => descriptor;
        public string TableId => descriptor.TableId;
        public string App => descriptor.App;
        public virtual TableDescriptor Describe(WorkspaceState state) => descriptor;
        public IReadOnlyList<ITableRow> Rows(WorkspaceState state) => rows(PortfolioCases.Base(state));
        public string? WhyEmpty(WorkspaceState state) => whyEmpty?.Invoke(PortfolioCases.Base(state));
        public virtual DateOnly? AsOf(WorkspaceState state) => null;

        /// <summary>No reader pages the table: the page holds its every row, and names rows by their keys.</summary>
        public virtual OrderedRows? InOrder(WorkspaceState state, string? subset, IReadOnlyList<ViewSort> sorts) => null;

        public IReadOnlyList<WorkspaceChange> Set(WorkspaceState state, ITableRow row, TableField field, JsonNode? value)
        {
            var at = (Row)row;
            Check(at, field, value);
            return [new WorkspaceChange.Set(PortfolioEditRules.Target(Source(state), at.EntityId, field.Name), value)];
        }

        protected virtual void Check(Row row, TableField field, JsonNode? value) => checks.Check(row.Reading, row.EntityId, field.Name, value);

        public virtual IReadOnlyList<WorkspaceChange>? SetAll(WorkspaceState state, TableSubset subset, TableField field, JsonNode? value) => null;

        public IReadOnlyList<WorkspaceChange> Reset(WorkspaceState state, ITableRow row, TableField? field) =>
            Resets(state, ((Row)row).EntityId, field is null ? Describe(state).Fields.Where(f => f.Editable).Select(f => f.Name) : [field.Name]);

        public virtual IReadOnlyList<WorkspaceChange> ResetAll(WorkspaceState state, TableSubset subset, TableField? field) => [];

        public IReadOnlyList<WorkspaceChange> Remove(WorkspaceState state, ITableRow row) => throw new WorkspaceRefused("NotRemovable", notRemovable((Row)row));

        // The Common edits an entity holds on these fields, each to be removed; an edit held back as a conflict is the user's to decide.
        protected static IReadOnlyList<WorkspaceChange> Resets(WorkspaceState state, string entity, IEnumerable<string> fields) =>
            [.. fields.Select(f => PortfolioEditRules.Target(Source(state), entity, f)).Where(t => state.Overrides.ContainsKey(t) || state.Lost.ContainsKey(t))
                .Select(t => new WorkspaceChange.Reset(t))];
    }

    /// <summary>
    /// The trades. Its rows share what is read once for all of them, the modules' cells among them, so a command over
    /// every row stays linear in the rows (ADR-0016 §2).
    /// </summary>
    sealed class TradeTable(MeasureRegister register, PortfolioChecks checks) : Table(checks,
        Described(Trades, "Trades", ["trades", "trade"], "tradeId", PortfolioFields.Trade(register), main: true,
            subsets: [new(Entity.ExistingGrid, "Existing", [Entity.ExistingGrid]), new(Entity.NewGrid, "New", [Entity.NewGrid])], actions: TradeActions),
        reading => Read(register, reading),
        row => $"{row.Key} is a trade the workspace was given, not one it made: take it out of After with set {row.Key} A off.")
    {
        static readonly IReadOnlyList<string> Sides = ["before", "after"];

        // The table as a selection of modules has it, described once for each selection met.
        readonly ConcurrentDictionary<string, TableDescriptor> described = new();

        // A module's column is a field while one of its measures is selected, as the grids show it then (ADR-0010 §10):
        // one that does not show is no field, and a command that names it is told the fields there are.
        public override TableDescriptor Describe(WorkspaceState state)
        {
            var selected = register.Selected(PortfolioCases.Base(state)).Select(m => m.Id).ToHashSet();
            return described.GetOrAdd(string.Join(',', selected.Order(StringComparer.Ordinal)), _ =>
            {
                var hidden = register.TradeColumns.Where(c => !selected.Contains(c.Module)).Select(c => c.Field).ToHashSet();
                return Whole with { Fields = [.. Whole.Fields.Where(f => !hidden.Contains(f.Name))] };
            });
        }

        // What every row of one reading shares: the modules selected and the trades with their cells, read when first asked.
        sealed class Shared(MeasureRegister register, WorkspaceState reading, IReadOnlyList<EffectiveTrade> trades)
        {
            public Lazy<IReadOnlyList<IMeasureModule>> Selected { get; } = new(() => register.Selected(reading));
            public Lazy<IReadOnlyList<EffectiveTrade>> WithCells { get; } = new(() => register.WithCells(reading, trades));
        }

        sealed class TradeRow(Shared shared, int index, WorkspaceState reading, EffectiveTrade trade, Func<string, JsonNode?> read)
            : Row(reading, Entity.Of(Entity.Trade, trade.TradeId), trade.TradeId, trade.IsNew ? Entity.NewGrid : Entity.ExistingGrid, read)
        {
            public IReadOnlyList<IMeasureModule> Selected => shared.Selected.Value;
            public EffectiveTrade WithCells => shared.WithCells.Value[index];
        }

        static IReadOnlyList<ITableRow> Read(MeasureRegister register, WorkspaceState reading)
        {
            var trades = PortfolioMaterialiser.Trades(reading);
            var shared = new Shared(register, reading, trades);
            var columns = register.TradeColumns.Select(c => c.Field).ToHashSet();
            return [.. trades.Select((t, i) => new TradeRow(shared, i, reading, t,
                f => columns.Contains(f) ? shared.WithCells.Value[i].Cells?.GetValueOrDefault(f)?.Value?.DeepClone() : PortfolioFields.Value(t, f)))];
        }

        // The order a trade grid is paged in (ADR-0016 §2, note 17 of step 2): the page's own order, which it keeps for
        // the state, and the page's token. The rows are read each time: a module's cell arrives with no edit.
        public override OrderedRows? InOrder(WorkspaceState state, string? subset, IReadOnlyList<ViewSort> sorts)
        {
            var reading = PortfolioCases.Base(state);
            var keys = TradeOrder.Keys(sorts.Select(s => (s.Column, s.Descending)));
            var rows = Read(register, reading).ToDictionary(r => r.Key, StringComparer.Ordinal);
            return new([.. TradeOrder.Ordered(reading, register, subset, keys).Select(t => rows[t.TradeId])], TradeOrder.Token(reading, register, subset, keys));
        }

        public override DateOnly? AsOf(WorkspaceState state) => Source(state).ValueDate;

        // A module's column is checked on the trade's cell, which the rows read once for all of them.
        protected override void Check(Row row, TableField field, JsonNode? value)
        {
            if (register.ColumnOf(field.Name) is null) base.Check(row, field, value);
            else if (row is TradeRow trade) Checks.Check(row.Reading, row.EntityId, field.Name, value, trade.WithCells, trade.Selected);
            else throw new InvalidOperationException($"{row.Key} is not a row of the trades.");
        }

        // B or A for every row of a grid is one edit on the grid (ADR-0010 §6), which wins over each row's earlier one.
        public override IReadOnlyList<WorkspaceChange>? SetAll(WorkspaceState state, TableSubset subset, TableField field, JsonNode? value)
        {
            if (!Sides.Contains(field.Name)) return null;
            var grid = Entity.Of(Entity.Grid, subset.Name);
            Checks.Check(PortfolioCases.Base(state), grid, field.Name, value);
            return [new WorkspaceChange.Set(PortfolioEditRules.Target(Source(state), grid, field.Name), value)];
        }

        public override IReadOnlyList<WorkspaceChange> ResetAll(WorkspaceState state, TableSubset subset, TableField? field) =>
            Resets(state, Entity.Of(Entity.Grid, subset.Name), field is null ? Sides : Sides.Where(s => s == field.Name));
    }
}
