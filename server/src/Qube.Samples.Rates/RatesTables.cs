using System.Text.Json.Nodes;
using Qube.Composition.Application;
using Qube.Composition.Application.Tables;
using Qube.Composition.Application.Workspaces;

namespace Qube.Samples.Rates;

// ADR-0016 §1 and its note 34 of 2026-11-11: Rates registers its lines and its workspace's own settings as tables, so
// the kernel's verbs edit them as they edit the pricer's. A field's name is the line's own field (the grid's column);
// what a write is, and where a term does not apply to a line, stays here.

/// <summary>
/// Rates' tables (ADR-0016 §1, note 34): the lines, a row a line, with each leg's fields typed by a word that ends in
/// the leg's number (<c>notional1</c>) and an IRS's terms as fields of their own (<c>notional</c>, <c>rate</c>); and the
/// workspace's own settings, one row, which holds the defaults a new line takes. A write is checked for its form, as a
/// typed cell is: whether the terms then make a line is the resolver's, and shows on the row.
/// </summary>
public sealed class RatesTables(ConventionRegistry conventions) : ITableCatalog
{
    public const string Lines = "rates.lines";
    public const string Workspace = "rates.workspace";

    public IEnumerable<IWorkspaceTable> Tables { get; } = [new LineTable(conventions), new WorkspaceTable(conventions)];

    // A choice read by the words a grid cell takes for it, and by its own values, case ignored.
    static IReadOnlyDictionary<string, string> Choice(params (string Word, string Value)[] words)
    {
        var choices = words.ToDictionary(w => w.Word, w => w.Value);
        foreach (var value in words.Select(w => w.Value).Distinct()) choices[value.ToLowerInvariant()] = value;
        return choices;
    }

    static readonly IReadOnlyDictionary<string, string> Products = Choice(("irs", "irs"), ("ccs", "ccs"));

    static readonly IReadOnlyDictionary<string, string> QuoteKinds = Choice(("fixed", nameof(QuoteKind.FixedRatePercent)), ("spread", nameof(QuoteKind.FloatSpreadPercent)),
        ("strike", nameof(QuoteKind.StrikePercent)), ("fxpts", nameof(QuoteKind.FxForwardPoints)), ("atm", nameof(QuoteKind.AtmOffsetBp)));

    static readonly IReadOnlyDictionary<string, string> DayCounts = Choice(("act/360", nameof(DayCountBasis.Act360)), ("act/365f", nameof(DayCountBasis.Act365Fixed)),
        ("act365f", nameof(DayCountBasis.Act365Fixed)));

    static readonly IReadOnlyDictionary<string, string> Adjustments = Choice(("mf", nameof(BusinessDayAdjustment.ModifiedFollowing)), ("f", nameof(BusinessDayAdjustment.Following)),
        ("p", nameof(BusinessDayAdjustment.Preceding)), ("u", nameof(BusinessDayAdjustment.Unadjusted)));

    /// <summary>
    /// The lines. A term an IRS has once (its currency, index and notional, its fixed rate, its floating spread) is a
    /// field that writes the legs' own fields; on a line it does not apply to it is refused by the line's name, with
    /// the fields that do.
    /// </summary>
    sealed class LineTable(ConventionRegistry conventions) : IWorkspaceTable
    {
        const string Key = "line", Currency = "currency", Index = "index", Notional = "notional", Rate = "rate", Spread = "spread";

        static string LegSpread(int n) => $"{Spread}{n}";

        readonly TableDescriptor descriptor = new(Lines, RatesContracts.Version, RatesEditRules.App, "Lines", ["lines", "line"], Key, Fields(conventions), [], Main: true,
            Actions: [new("Remove line", "remove selected")]);

        static IReadOnlyList<TableField> Fields(ConventionRegistry conventions)
        {
            var (currencies, indices) = (RatesProducts.Currencies(conventions), RatesProducts.Indices(conventions));
            IEnumerable<TableField> Leg(int n)
            {
                string Name(string field) => LineFields.Leg(n, field);
                var group = $"Leg {n}";
                return
                [
                    new(Name("currency"), "Currency", [$"currency{n}", $"ccy{n}"], FieldForm.Choice, Editable: true, Choices: currencies, Group: group),
                    new(Name("index"), "Index", [$"index{n}"], FieldForm.Choice, Editable: true, Choices: indices, Group: group),
                    new(Name("notional"), "Notional", [$"notional{n}"], FieldForm.Amount, Editable: true, UnitField: Name("currency"), Group: group),
                    new(Name("quoteKind"), "Quote kind", [$"kind{n}"], FieldForm.Choice, Editable: true, Optional: true, Choices: QuoteKinds, Group: group),
                    new(Name("quote"), "Quote", [$"quote{n}"], FieldForm.Number, Editable: true, Optional: true, Group: group),
                    new(LegSpread(n), "Spread", [LegSpread(n)], FieldForm.Percent, Editable: true, Group: group),
                    new(Name("paymentFrequency"), "Payment frequency", [$"frequency{n}", $"freq{n}"], FieldForm.Period, Editable: true, Group: group),
                    new(Name("resetFrequency"), "Reset frequency", [$"reset{n}"], FieldForm.Period, Editable: true, Optional: true, Group: group),
                    new(Name("floating"), "Floating", [$"float{n}"], FieldForm.Flag, Editable: true, Group: group),
                    new(Name("dayCount"), "Day count", [$"daycount{n}"], FieldForm.Choice, Editable: true, Optional: true, Choices: DayCounts, Group: group),
                    new(Name("adjustment"), "Business day adjustment", [$"bdc{n}"], FieldForm.Choice, Editable: true, Optional: true, Choices: Adjustments, Group: group),
                    new(Name("dailyAverage"), "Daily average", [$"average{n}"], FieldForm.Flag, Editable: true, Optional: true, Group: group),
                    new(Name("observationShift"), "Observation shift", [$"shift{n}"], FieldForm.Days, Editable: true, Optional: true, Group: group),
                    new(Name("lookback"), "Lookback", [$"lookback{n}"], FieldForm.Days, Editable: true, Optional: true, Group: group),
                    new(Name("paymentDelay"), "Payment delay", [$"delay{n}"], FieldForm.Days, Editable: true, Optional: true, Group: group),
                    new(Name("inArrears"), "In arrears", [$"arrears{n}"], FieldForm.Flag, Editable: true, Optional: true, Group: group),
                    new(Name("gearing"), "Gearing", [$"gearing{n}"], FieldForm.Number, Editable: true, Optional: true, Group: group),
                    new(Name("currentReset"), "Current reset", [$"fixing{n}"], FieldForm.Number, Editable: true, Optional: true, Group: group),
                ];
            }
            return
            [
                new(Key, "Line", [Key, "id"], FieldForm.Id),
                new(LineFields.Product, "Product", [LineFields.Product], FieldForm.Choice, Choices: Products),
                new(LineFields.Start, "Start", [LineFields.Start], FieldForm.PeriodOrDate, Editable: true, Optional: true),
                new(LineFields.End, "End", [LineFields.End], FieldForm.PeriodOrDate, Editable: true),
                new(LineFields.Direction, "Direction", [LineFields.Direction], FieldForm.Choice, Editable: true, Choices: RatesProducts.Directions),
                new(Currency, "Currency", [Currency, "ccy"], FieldForm.Choice, Editable: true, Choices: currencies),
                new(Index, "Index", [Index], FieldForm.Choice, Editable: true, Choices: indices),
                new(Notional, "Notional", [Notional], FieldForm.Amount, Editable: true, UnitField: Currency),
                new(Rate, "Fixed rate", [Rate], FieldForm.Percent, Editable: true),
                new(Spread, "Floating spread", [Spread], FieldForm.Percent, Editable: true),
                new(LineFields.Collateral, "Collateral", [LineFields.Collateral], FieldForm.Id, Editable: true, Optional: true),
                new(LineFields.MandatoryBreak, "Mandatory break", ["break", "mandatory break"], FieldForm.Date, Editable: true, Optional: true),
                new(LineFields.Risk, "Risk", [LineFields.Risk], FieldForm.Flag, Editable: true),
                .. Leg(1), .. Leg(2),
            ];
        }

        public string TableId => Lines;
        public string App => RatesEditRules.App;
        public TableDescriptor Describe(WorkspaceState state) => descriptor;
        public DateOnly? AsOf(WorkspaceState state) => RatesEditRules.ValuationDate(state);

        public IReadOnlyList<ITableRow> Rows(WorkspaceState state) =>
            [.. state.Created.Values.Where(c => c.Scope == RatesEditRules.LinesScope).OrderBy(c => c.Seq).Select(c => new Row(c.Entity, RatesEditRules.Fields(state, c.Entity)))];

        /// <summary>A line as a command reads it: its own fields as they stand, and each term an IRS has once where the line has it.</summary>
        sealed class Row(string line, IReadOnlyDictionary<string, JsonNode?> fields) : ITableRow
        {
            public string Key => line;
            public string? Subset => null;

            public JsonNode? Held(string field) => fields.TryGetValue(field, out var value) ? value : throw new InvalidOperationException($"Line {line} has no field {field}.");

            public bool IsIrs => Held(LineFields.Product)?.GetValue<string>() switch
            {
                "irs" => true,
                "ccs" => false,
                var other => throw new InvalidOperationException($"Line {line} names product {other ?? "(none)"}, which Rates does not describe."),
            };

            public bool Floating(int leg) => Held(LineFields.Leg(leg, "floating")) is JsonValue v && v.TryGetValue<bool>(out var floating) ? floating
                : throw new InvalidOperationException($"Line {line} does not say whether leg {leg} floats.");

            public JsonNode? Value(string field) => Read(field)?.DeepClone();

            JsonNode? Read(string field) => field switch
            {
                LineTable.Key => line,
                // What an IRS has once is what both its legs hold; a line whose legs differ holds no one value, and says so by its own issue.
                Currency or Index or Notional => IsIrs && FieldForms.Same(Held(LineFields.Leg(1, field)), Held(LineFields.Leg(2, field))) ? Held(LineFields.Leg(1, field)) : null,
                Rate => Quoted(1, QuoteKind.FixedRatePercent),
                Spread => IsIrs ? Quoted(2, QuoteKind.FloatSpreadPercent) : null,
                _ when field == LegSpread(1) => Quoted(1, QuoteKind.FloatSpreadPercent),
                _ when field == LegSpread(2) => Quoted(2, QuoteKind.FloatSpreadPercent),
                _ => Held(field),
            };

            // A leg's quote where it is quoted in the kind asked: a fixed rate is no spread, and a leg at par has neither.
            JsonNode? Quoted(int leg, QuoteKind kind) =>
                string.Equals(Held(LineFields.Leg(leg, "quoteKind"))?.GetValue<string>(), kind.ToString(), StringComparison.OrdinalIgnoreCase) ? Held(LineFields.Leg(leg, "quote")) : null;
        }

        // The line's own fields a field of the table writes, with the value each takes: itself, or for a term the legs'
        // fields that hold it. A term the line does not have is refused by the line's name, with the fields that do.
        static IReadOnlyList<(string Field, JsonNode? Value)> Under(Row row, string field, JsonNode? value)
        {
            IReadOnlyList<(string, JsonNode?)> Quote(int leg, QuoteKind kind) =>
                [(LineFields.Leg(leg, "quoteKind"), kind.ToString()), (LineFields.Leg(leg, "quote"), value)];
            WorkspaceRefused OnEachLeg(string what) => new("NotApplicable",
                $"{row.Key} is a cross-currency swap, which has {what} on each leg: set {field}1 or {field}2.");
            IReadOnlyList<(string, JsonNode?)> SpreadOf(int leg) => row.Floating(leg) ? Quote(leg, QuoteKind.FloatSpreadPercent)
                : throw new WorkspaceRefused("NotApplicable", $"Leg {leg} of {row.Key} is fixed and takes no spread: set {(leg == 1 ? Rate : "kind2 and quote2")}.");
            return field switch
            {
                Currency or Notional => row.IsIrs ? [(LineFields.Leg(1, field), value), (LineFields.Leg(2, field), value)] : throw OnEachLeg($"a {field}"),
                Index => row.IsIrs ? [(LineFields.Leg(1, field), value), (LineFields.Leg(2, field), value)] : throw OnEachLeg($"an {field}"),
                Rate => !row.Floating(1) ? Quote(1, QuoteKind.FixedRatePercent)
                    : throw new WorkspaceRefused("NotApplicable", $"Leg 1 of {row.Key} is floating and takes no fixed rate: set {LegSpread(1)}."),
                Spread => row.IsIrs ? SpreadOf(2) : throw OnEachLeg($"a {field}"),
                _ when field == LegSpread(1) => SpreadOf(1),
                _ when field == LegSpread(2) => SpreadOf(2),
                _ => [(field, value)],
            };
        }

        static EditTarget Target(Row row, string field) => new(RatesEditRules.LinesScope, row.Key, field);

        // A term writes only the legs' fields that do not hold it yet: an IRS whose one leg has the notional takes it on the other.
        public IReadOnlyList<WorkspaceChange> Set(WorkspaceState state, ITableRow row, TableField field, JsonNode? value)
        {
            var line = (Row)row;
            return [.. Under(line, field.Name, value).Where(w => !FieldForms.Same(line.Held(w.Field), w.Value))
                .Select(w => new WorkspaceChange.Set(Target(line, w.Field), w.Value?.DeepClone()))];
        }

        // The line's own edits of the field, or of every field where none is named, each to be removed: the line then
        // holds what it was made with. A term's are the edits of the legs' fields that hold it.
        public IReadOnlyList<WorkspaceChange> Reset(WorkspaceState state, ITableRow row, TableField? field)
        {
            var line = (Row)row;
            var named = field is null ? null : Under(line, field.Name, null).Select(w => Target(line, w.Field)).ToHashSet();
            return [.. state.Overrides.Keys.Concat(state.Lost.Keys).Where(t => t.Scope == RatesEditRules.LinesScope && t.Entity == line.Key && (named is null || named.Contains(t)))
                .Distinct().OrderBy(t => t.Field, StringComparer.Ordinal).Select(t => new WorkspaceChange.Reset(t))];
        }

        public IReadOnlyList<WorkspaceChange> Remove(WorkspaceState state, ITableRow row) => [new WorkspaceChange.Remove(row.Key)];
    }

    /// <summary>
    /// The workspace's own settings, one row (note 34): the defaults a new line takes (ADR-0008 §4b), each named by words of
    /// its own (<c>default currency</c>), so that <c>currency</c> alone stays a line's. They live in the workspace's context,
    /// and a write is the whole context after it, which <c>undo</c> takes back.
    /// </summary>
    sealed class WorkspaceTable(ConventionRegistry conventions) : IWorkspaceTable
    {
        const string Key = "id", Row = "workspace", DefaultCurrency = "defaultCurrency", DefaultNotional = "defaultNotional", DefaultDirection = "defaultDirection";

        readonly TableDescriptor descriptor = new(Workspace, RatesContracts.Version, RatesEditRules.App, "Workspace", [Row], Key,
        [
            new(Key, "Row", [Key], FieldForm.Id),
            new(DefaultCurrency, "Default currency", ["default currency", "default ccy"], FieldForm.Choice, Editable: true, Optional: true, Choices: RatesProducts.Currencies(conventions)),
            new(DefaultNotional, "Default notional", ["default notional"], FieldForm.Amount, Editable: true, Optional: true, UnitField: DefaultCurrency),
            new(DefaultDirection, "Default direction", ["default direction"], FieldForm.Choice, Editable: true, Optional: true, Choices: RatesProducts.Directions),
        ], [], SingleRow: true);

        public string TableId => Workspace;
        public string App => RatesEditRules.App;
        public TableDescriptor Describe(WorkspaceState state) => descriptor;

        public IReadOnlyList<ITableRow> Rows(WorkspaceState state) => [new Settings(LineDefaults.Of(state))];

        sealed class Settings(LineDefaults defaults) : ITableRow
        {
            public string Key => Row;
            public string? Subset => null;

            public JsonNode? Value(string field) => field switch
            {
                WorkspaceTable.Key => Row,
                DefaultCurrency => defaults.Currency,
                DefaultNotional => defaults.Notional,
                DefaultDirection => defaults.Direction,
                _ => throw new InvalidOperationException($"The workspace has no {field}."),
            };
        }

        // The defaults with one of them changed; an empty value is no default.
        static LineDefaults With(LineDefaults defaults, string field, JsonNode? value) => field switch
        {
            DefaultCurrency => defaults with { Currency = value?.GetValue<string>() },
            DefaultNotional => defaults with
            {
                Notional = value is null ? null : RatesMaterialiser.Number(value) is > 0 and var notional ? notional
                    : throw new WorkspaceRefused("InvalidValue", "A default notional is more than 0."),
            },
            DefaultDirection => defaults with { Direction = value?.GetValue<string>() },
            _ => throw new InvalidOperationException($"The workspace has no {field} a command sets."),
        };

        // The whole context with these defaults: everything else it holds stays as it is.
        static WorkspaceChange Context(WorkspaceState state, LineDefaults defaults)
        {
            var context = (JsonObject)state.Context.DeepClone();
            if (defaults.ToJson() is { Count: > 0 } held) context["defaults"] = held;
            else context.Remove("defaults");
            return new WorkspaceChange.Context(context);
        }

        public IReadOnlyList<WorkspaceChange> Set(WorkspaceState state, ITableRow row, TableField field, JsonNode? value) =>
            [Context(state, With(LineDefaults.Of(state), field.Name, value))];

        // A default has nothing it follows: reset removes it, or every one where none is named.
        public IReadOnlyList<WorkspaceChange> Reset(WorkspaceState state, ITableRow row, TableField? field)
        {
            var held = LineDefaults.Of(state);
            var next = field is null ? new LineDefaults(null, null, null) : field.Name == Key ? held : With(held, field.Name, null);
            return next == held ? [] : [Context(state, next)];
        }

        public IReadOnlyList<WorkspaceChange> Remove(WorkspaceState state, ITableRow row) =>
            throw new WorkspaceRefused("NotRemovable", $"{Row} is always one row: a command changes its values and never removes it.");
    }
}

/// <summary>
/// Keeps a workspace's live subscription on the revision its last accepted command left (ADR-0016 note 35), whoever
/// wrote it: a cell of the page, a kernel verb, <c>undo</c>, or the AI. Nothing is built where nothing ticks.
/// </summary>
public sealed class RatesLiveObserver(RatesLiveService live, ConventionRegistry conventions, IRatesPricingProvider provider) : IWorkspaceObserver
{
    public string App => RatesEditRules.App;

    public void Accepted(WorkspaceState state)
    {
        if (live.OnWorkspace(state.WorkspaceId).Count == 0) return;
        live.WorkspaceChanged(state.WorkspaceId, RatesAcceptedInput.Of(state, conventions, provider));
    }
}
