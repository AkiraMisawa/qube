using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Qube.Composition.Application;
using Qube.Composition.Application.Products;
using Qube.Composition.Application.Tables;
using Qube.Composition.Application.Workspaces;

namespace Qube.Samples.Portfolio;

// ADR-0016 note 51: the fields of a trade and of a CSA beyond the grids. They are fields of the row, which a
// condition, `set`, `reset` and `tables.read` reach, and no column of the page's rows. Every value is synthetic.

/// <summary>The groups the pricer's fields are drawn under (ADR-0016 note 51): a CSA's, then a trade's.</summary>
static class FieldGroups
{
    public const string General = "General", Terms = "Terms", Counterparty = "Counterparty", Self = "Self";
    public const string Identity = "Identity", Agreement = "Agreement", Dates = "Dates", Model = "Model", Valuation = "Valuation";
}

/// <summary>What a choice of the pricer's fields has to choose from: each value read by its own word, case ignored.</summary>
static class FieldChoices
{
    public static IReadOnlyDictionary<string, string> Of(IEnumerable<string> values) => values.ToDictionary(v => v.ToLowerInvariant(), v => v);

    /// <summary>The display currencies, as a field and the user's setting read them (ADR-0017 §6.1).</summary>
    public static readonly IReadOnlyDictionary<string, string> Currencies = Of(PortfolioMarket.Currencies);
}

/// <summary>What a trade's model fields have to choose from in the sample (ADR-0016 note 51): textbook names, and no desk's.</summary>
public static class TradeModel
{
    public static readonly IReadOnlyList<string> IrModels = ["HW1F", "HW2F", "LMM"];
    public static readonly IReadOnlyList<string> IrBases = ["deterministic", "stochastic"];
    public static readonly IReadOnlyList<string> EquityModels = ["black-scholes", "local-vol", "heston"];
    public static readonly IReadOnlyList<string> CreditModels = ["hazard-rate", "cir"];
    public static readonly IReadOnlyList<string> InflationModels = ["jarrow-yildirim"];
    public static readonly IReadOnlyList<string> UnmodelledMethods = ["mtm-only", "addon", "excluded"];
}

/// <summary>
/// The values of the fields beyond the grids (ADR-0016 note 51): what the synthetic source holds in each, what a reading
/// holds (the user's where set, else the source's), whether a value is one the field's form holds, and what Price
/// freezes of them (note 53).
/// </summary>
public static class PortfolioDetails
{
    static readonly IReadOnlyDictionary<string, string> Roundings = FieldChoices.Of(CsaSide.Roundings), IrModels = FieldChoices.Of(TradeModel.IrModels),
        IrBases = FieldChoices.Of(TradeModel.IrBases), EquityModels = FieldChoices.Of(TradeModel.EquityModels), CreditModels = FieldChoices.Of(TradeModel.CreditModels),
        InflationModels = FieldChoices.Of(TradeModel.InflationModels), Unmodelled = FieldChoices.Of(TradeModel.UnmodelledMethods);

    // A field beyond the grids with what the source holds in it for a row of its kind: declared together, so no field
    // is without its value and no value without its field.
    sealed record Detail<T>(TableField Field, Func<PortfolioSourceVersion, T, JsonNode?> Held);

    // One term of a CSA's side, which each side has: its name after the side's, the words it is said by after the
    // side's, what a side holds in it, and the term of the side that holds its unit.
    sealed record SideTerm(string Name, string Title, IReadOnlyList<string> Words, FieldForm Form, Func<CsaSide, JsonNode?> Held,
        bool Optional = false, string? Unit = null, IReadOnlyDictionary<string, string>? Choices = null);

    static readonly IReadOnlyList<SideTerm> SideTerms =
    [
        new("Posts", "Posts", ["posts"], FieldForm.Flag, s => s.Posts),
        new("VmCap", "VM cap", ["vm cap"], FieldForm.Amount, s => s.VmCap, Optional: true, Unit: "VmCapCurrency"),
        new("VmCapCurrency", "VM cap currency", ["vm cap currency", "vm cap ccy"], FieldForm.Choice, s => s.VmCapCurrency, Choices: FieldChoices.Currencies),
        new("IndependentAmount", "Independent amount", ["independent amount"], FieldForm.Amount, s => s.IndependentAmount, Unit: "Currency"),
        new("Rounding", "Rounding", ["rounding"], FieldForm.Amount, s => s.Rounding, Unit: "Currency"),
        new("DeliverRounding", "Delivery rounding", ["delivery rounding"], FieldForm.Choice, s => s.DeliverRounding, Choices: Roundings),
        new("ReturnRounding", "Return rounding", ["return rounding"], FieldForm.Choice, s => s.ReturnRounding, Choices: Roundings),
    ];

    // One side's fields, each named as the side's (`self posts`, `cpty posts`) and, last, by the word the sides share:
    // that word alone names both sides' field, and is refused with the two it could be.
    static IEnumerable<Detail<Csa>> Side(string side, string group, IReadOnlyList<string> said, Func<Csa, CsaSide> of) =>
        SideTerms.Select(term => new Detail<Csa>(
            new($"{side}{term.Name}", term.Title, [.. term.Words.SelectMany(w => said.Select(s => $"{s} {w}")), .. term.Words], term.Form, Editable: true,
                Optional: term.Optional, UnitField: term.Unit is { } unit ? $"{side}{unit}" : null, Choices: term.Choices, Group: group),
            (_, csa) => term.Held(of(csa))));

    // A CSA's: its general terms, which are the source's, and what each side posts, which a command sets.
    static readonly IReadOnlyList<Detail<Csa>> OfCsa =
    [
        new(new("counterparty", "Counterparty", ["counterparty"], FieldForm.Id, Group: FieldGroups.General), (source, _) => source.Registration.Key),
        new(new("nettingSet", "Netting set", ["netting set"], FieldForm.Id, Group: FieldGroups.General), (source, _) => source.Agreement.NettingSetId),
        new(new("agreementName", "Agreement name", ["agreement name", "agreement"], FieldForm.Id, Group: FieldGroups.General), (_, csa) => csa.AgreementName),
        new(new("enforceable", "Enforceable", ["enforceable"], FieldForm.Flag, Group: FieldGroups.General), (source, _) => source.Agreement.Enforceable),
        new(new("main", "Main", ["main"], FieldForm.Flag, Group: FieldGroups.General), (source, csa) => source.Agreement.MainCsaId == csa.CsaId),
        new(new("userDefined", "User-defined", ["user-defined", "user defined"], FieldForm.Id, Optional: true, Group: FieldGroups.General), (_, csa) => csa.UserDefined),
        .. Side("counterparty", FieldGroups.Counterparty, ["counterparty", "cpty"], csa => csa.Counterparty),
        .. Side("self", FieldGroups.Self, ["self"], csa => csa.Self),
    ];

    // What the synthetic source holds of a trade by its product: its type, the source's name for the product, its
    // model and its pricer. A product it has no detail for is refused, never given another's.
    static (string Type, string SourceType, string Model, string Pricer) Usual(SourceTrade trade) => trade.Product switch
    {
        "IRS" => ("Swap", "IR-SWAP-FIXFLT", "M-IR-01", "P-SWAP-01"),
        "FXForward" => ("Forward", "FX-FWD", "M-FX-01", "P-FWD-01"),
        _ => throw new InvalidOperationException($"The synthetic source has no detail for a {trade.Product}."),
    };

    // A trade's: its identity and its agreement, which are the source's, and its break and its model, which a command
    // sets. The synthetic rule: its identity follows its id and its product, its agreement is the counterparty's, it
    // has no break, and its model is its product's usual one. A new trade is no different: it is priced under the
    // agreement it would join.
    static readonly IReadOnlyList<Detail<SourceTrade>> OfTrade =
    [
        new(new("requestedId", "Requested id", ["requested id"], FieldForm.Id, Group: FieldGroups.Identity), (_, trade) => $"REQ-{trade.TradeId}"),
        new(new("tradeType", "Trade type", ["trade type"], FieldForm.Id, Group: FieldGroups.Identity), (_, trade) => Usual(trade).Type),
        new(new("sourceProductType", "Source product type", ["source product type", "source product"], FieldForm.Id, Group: FieldGroups.Identity), (_, trade) => Usual(trade).SourceType),
        new(new("simmProductClass", "SIMM product class", ["simm product class", "product class"], FieldForm.Id, Group: FieldGroups.Identity), (_, _) => "RatesFX"),
        new(new("userDefined", "User-defined", ["user-defined", "user defined"], FieldForm.Id, Optional: true, Group: FieldGroups.Identity), (_, _) => null),
        new(new("masterAgreement", "Master agreement", ["master agreement", "ma"], FieldForm.Id, Group: FieldGroups.Agreement), (source, _) => source.Agreement.MaId),
        new(new("nettingSet", "Netting set", ["netting set"], FieldForm.Id, Group: FieldGroups.Agreement), (source, _) => source.Agreement.NettingSetId),
        new(new("breakDate", "Break date", ["break date", "break"], FieldForm.Date, Editable: true, Optional: true, Group: FieldGroups.Dates), (_, _) => null),
        new(new("breakMandatory", "Mandatory break", ["mandatory break", "break mandatory"], FieldForm.Flag, Editable: true, Group: FieldGroups.Dates), (_, _) => false),
        new(new("modelId", "Model id", ["model id", "model"], FieldForm.Id, Editable: true, Group: FieldGroups.Model), (_, trade) => Usual(trade).Model),
        new(new("pricerId", "Pricer id", ["pricer id", "pricer"], FieldForm.Id, Editable: true, Group: FieldGroups.Model), (_, trade) => Usual(trade).Pricer),
        new(new("interpolationId", "Interpolation id", ["interpolation id", "interpolation"], FieldForm.Id, Editable: true, Group: FieldGroups.Model), (_, _) => "I-LIN-01"),
        new(new("domesticCurrency", "Domestic currency", ["domestic currency", "domestic ccy"], FieldForm.Choice, Editable: true, Choices: FieldChoices.Currencies, Group: FieldGroups.Model),
            (_, trade) => trade.Currency),
        new(new("irModel", "IR model", ["ir model"], FieldForm.Choice, Editable: true, Choices: IrModels, Group: FieldGroups.Model), (_, _) => "HW1F"),
        new(new("irBasis", "IR basis", ["ir basis"], FieldForm.Choice, Editable: true, Choices: IrBases, Group: FieldGroups.Model), (_, _) => "deterministic"),
        new(new("equityModel", "Equity model", ["equity model"], FieldForm.Choice, Editable: true, Optional: true, Choices: EquityModels, Group: FieldGroups.Model), (_, _) => null),
        new(new("creditModel", "Credit model", ["credit model"], FieldForm.Choice, Editable: true, Optional: true, Choices: CreditModels, Group: FieldGroups.Model), (_, _) => null),
        new(new("inflationModel", "Inflation model", ["inflation model"], FieldForm.Choice, Editable: true, Optional: true, Choices: InflationModels, Group: FieldGroups.Model), (_, _) => null),
        new(new("unmodelledMethod", "Unmodelled method", ["unmodelled method", "unmodelled"], FieldForm.Choice, Editable: true, Choices: Unmodelled, Group: FieldGroups.Model),
            (_, _) => "mtm-only"),
    ];

    // A kind of entity's fields beyond the grids, by name, and what the source holds in one for the entity of an id:
    // nothing where this version has not the entity.
    sealed record Kind(IReadOnlyList<TableField> Fields, IReadOnlyDictionary<string, TableField> Named, Func<PortfolioSourceVersion, string, string, SourceValue?> Held);

    static Kind Of<T>(IReadOnlyList<Detail<T>> details, Func<PortfolioSourceVersion, string, T?> row) where T : class
    {
        var held = details.ToDictionary(d => d.Field.Name, d => d.Held);
        return new([.. details.Select(d => d.Field)], details.ToDictionary(d => d.Field.Name, d => d.Field),
            (source, id, field) => row(source, id) is { } found ? new(held[field](source, found)) : null);
    }

    static readonly IReadOnlyDictionary<string, Kind> Kinds = new Dictionary<string, Kind>
    {
        [Entity.Csa] = Of(OfCsa, (source, id) => source.CsaOf(id)),
        [Entity.Trade] = Of(OfTrade, (source, id) => source.Trade(id)),
    };

    /// <summary>
    /// A CSA's fields beyond the grid (ADR-0016 note 51): its general terms, which are the source's, and what each side
    /// posts, which a command sets. A side's field is said with its side: the word alone names both, and is refused.
    /// </summary>
    public static IReadOnlyList<TableField> CsaFields { get; } = Kinds[Entity.Csa].Fields;

    /// <summary>
    /// A trade's fields beyond the grids (ADR-0016 note 51): its identity and its agreement, which are the source's, and
    /// its break and its model, which a command sets.
    /// </summary>
    public static IReadOnlyList<TableField> TradeFields { get; } = Kinds[Entity.Trade].Fields;

    /// <summary>The field beyond the grids an entity of the kind has under the name; none where it has no such field.</summary>
    public static TableField? Find(string kind, string field) => Kinds.TryGetValue(kind, out var of) && of.Named.TryGetValue(field, out var found) ? found : null;

    /// <summary>
    /// What the source holds in a field beyond the grids, which is an edit's Original and what a carry compares; null
    /// where the kind has no such field or this version has not the entity.
    /// </summary>
    public static SourceValue? Source(PortfolioSourceVersion source, string entity, string field)
    {
        var (kind, id) = Entity.Split(entity);
        return Find(kind, field) is null ? null : Kinds[kind].Held(source, id, field);
    }

    // The edits a reading holds of fields beyond the grids, read once: a state never changes, and most hold none.
    static readonly ConditionalWeakTable<WorkspaceState, IReadOnlyDictionary<(string Entity, string Field), JsonNode?>> EditsRead = new();

    static IReadOnlyDictionary<(string Entity, string Field), JsonNode?> Edits(WorkspaceState reading) => EditsRead.GetValue(reading, r =>
    {
        PortfolioCases.Unlayered(r);
        return r.Overrides.Values.Where(o => Find(Entity.Split(o.Target.Entity).Kind, o.Target.Field) is not null)
            .ToDictionary(o => (o.Target.Entity, o.Target.Field), o => o.Value);
    });

    /// <summary>The value a reading holds in a field beyond the grids: the user's where one is set, else the source's.</summary>
    public static JsonNode? Value(WorkspaceState reading, string entity, string field)
    {
        if (Edits(reading).TryGetValue((entity, field), out var set)) return set?.DeepClone();
        var held = Source((PortfolioSourceVersion)reading.Source, entity, field) ?? throw new InvalidOperationException($"{entity} has no {field} in this workspace.");
        return held.Value;
    }

    /// <summary>
    /// Every field beyond the grids of a trade or a CSA as a reading holds it, by name (ADR-0016 note 53): what Price
    /// freezes. An empty one is said as empty, never left out.
    /// </summary>
    public static JsonObject Frozen(WorkspaceState reading, string entity)
    {
        if (!Kinds.TryGetValue(Entity.Split(entity).Kind, out var kind)) throw new InvalidOperationException($"{entity} has no fields beyond the grids.");
        return new(kind.Fields.Select(f => KeyValuePair.Create(f.Name, Value(reading, entity, f.Name))));
    }

    /// <summary>
    /// Why a field beyond the grids cannot hold a value, by its form (ADR-0016 note 51): a flag, one of a choice's
    /// values, an amount of 0 or more, a date, an identifier of one word; empty where the field may be. Null where it can.
    /// </summary>
    public static string? Problem(TableField field, JsonNode? value)
    {
        var (what, orEmpty) = (field.Words[0], field.Optional ? ", or empty" : "");
        if (value is null && field.Optional) return null;
        var text = value is JsonValue held && held.TryGetValue<string>(out var said) ? said : null;
        return field.Form switch
        {
            FieldForm.Flag => value is JsonValue flag && flag.TryGetValue<bool>(out _) ? null : $"{what} is true or false.",
            FieldForm.Choice when field.Choices is { } choices => text is not null && choices.Values.Contains(text) ? null : $"{what} is one of {string.Join(", ", choices.Values)}{orEmpty}.",
            FieldForm.Amount => value is JsonValue amount && amount.TryGetValue<decimal>(out var n) && n >= 0 ? null : $"{what} is an amount of 0 or more{orEmpty}.",
            FieldForm.Date => text is not null && DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
                ? null : $"{what} is a date, yyyy-mm-dd{orEmpty}.",
            FieldForm.Id => text is { Length: > 0 } && !text.Any(char.IsWhiteSpace) ? null : $"{what} is an identifier of one word{orEmpty}.",
            _ => throw new InvalidOperationException($"{field.Name} is a {field.Form}, which no check of the fields beyond the grids reads."),
        };
    }
}

/// <summary>
/// The CSA's four templates (ADR-0016 note 52): who posts. Each takes a currency and writes the CSA's currency and each
/// side's, whether collateral is exchanged and one-way, who posts, and both sides' threshold and MTA as 0. Curves,
/// frequency, VM caps, independent amounts and rounding stay as they are: no curve is guessed for a currency.
/// </summary>
public static class CsaTemplates
{
    static readonly IReadOnlyList<ProductField> Takes =
        [new("currency", "Currency", FieldForm.Choice, Required: true, Unnamed: true, Choices: FieldChoices.Currencies, Aliases: ["ccy"])];

    // Each cell a template writes, in the order written, and its value for the currency given and for who posts: the
    // counterparty, and we. Collateral is exchanged where either posts, and one-way where exactly one does.
    static readonly IReadOnlyList<(string Field, Func<string, bool, bool, JsonNode?> Value)> Cells =
    [
        ("currency", (currency, _, _) => currency),
        ("counterpartyCurrency", (currency, _, _) => currency),
        ("selfCurrency", (currency, _, _) => currency),
        ("exchange", (_, counterparty, self) => counterparty || self),
        ("oneWay", (_, counterparty, self) => counterparty != self),
        ("counterpartyPosts", (_, counterparty, _) => counterparty),
        ("selfPosts", (_, _, self) => self),
        ("counterpartyThreshold", (_, _, _) => 0m),
        ("counterpartyMta", (_, _, _) => 0m),
        ("selfThreshold", (_, _, _) => 0m),
        ("selfMta", (_, _, _) => 0m),
    ];

    static readonly IReadOnlyList<string> Writes = [.. Cells.Select(c => c.Field)];

    // Each template with who posts under it: the counterparty, and we.
    static readonly IReadOnlyList<(TableTemplate Template, bool Counterparty, bool Self)> Posting =
    [
        (new("2way", "Two-way", ["2way", "two-way"], Takes, Writes), true, true),
        (new("1way-self", "One-way, we post", ["1way-self", "one-way self"], Takes, Writes), false, true),
        (new("1way-cpty", "One-way, the counterparty posts", ["1way-cpty", "one-way cpty", "one-way counterparty"], Takes, Writes), true, false),
        (new("none", "No collateral", ["none", "no collateral"], Takes, Writes), false, false),
    ];

    public static IReadOnlyList<TableTemplate> All { get; } = [.. Posting.Select(p => p.Template)];

    /// <summary>The value each field a template writes takes for the currency given.</summary>
    public static IReadOnlyDictionary<string, JsonNode?> Values(TableTemplate template, IReadOnlyDictionary<string, JsonNode?> terms)
    {
        var (_, counterparty, self) = Posting.Single(p => p.Template.Name == template.Name);
        var currency = terms["currency"] is JsonValue held && held.TryGetValue<string>(out var code) ? code
            : throw new InvalidOperationException($"The template {template.Name} was given no currency.");
        return Cells.ToDictionary(c => c.Field, c => c.Value(currency, counterparty, self));
    }
}
