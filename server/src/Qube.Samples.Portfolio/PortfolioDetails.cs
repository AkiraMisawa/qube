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

    /// <summary>
    /// A CSA's fields beyond the grid (ADR-0016 note 51): its general terms, which are the source's, and what each side
    /// posts, which a command sets. A side's field is said with its side: the word alone names both, and is refused.
    /// </summary>
    public static IReadOnlyList<TableField> CsaFields { get; } =
    [
        new("counterparty", "Counterparty", ["counterparty"], FieldForm.Id, Group: FieldGroups.General),
        new("nettingSet", "Netting set", ["netting set"], FieldForm.Id, Group: FieldGroups.General),
        new("agreementName", "Agreement name", ["agreement name", "agreement"], FieldForm.Id, Group: FieldGroups.General),
        new("enforceable", "Enforceable", ["enforceable"], FieldForm.Flag, Group: FieldGroups.General),
        new("main", "Main", ["main"], FieldForm.Flag, Group: FieldGroups.General),
        new("userDefined", "User-defined", ["user-defined", "user defined"], FieldForm.Id, Optional: true, Group: FieldGroups.General),
        .. Side("counterparty", FieldGroups.Counterparty, ["counterparty", "cpty"]),
        .. Side("self", FieldGroups.Self, ["self"]),
    ];

    // One side's fields, each named as the side's (`self posts`, `cpty posts`) and, last, by the word the sides share.
    static IEnumerable<TableField> Side(string side, string group, IReadOnlyList<string> said)
    {
        IReadOnlyList<string> Words(params string[] words) => [.. words.SelectMany(w => said.Select(s => $"{s} {w}")), .. words];
        return
        [
            new($"{side}Posts", "Posts", Words("posts"), FieldForm.Flag, Editable: true, Group: group),
            new($"{side}VmCap", "VM cap", Words("vm cap"), FieldForm.Amount, Editable: true, Optional: true, UnitField: $"{side}VmCapCurrency", Group: group),
            new($"{side}VmCapCurrency", "VM cap currency", Words("vm cap currency", "vm cap ccy"), FieldForm.Choice, Editable: true, Choices: FieldChoices.Currencies, Group: group),
            new($"{side}IndependentAmount", "Independent amount", Words("independent amount"), FieldForm.Amount, Editable: true, UnitField: $"{side}Currency", Group: group),
            new($"{side}Rounding", "Rounding", Words("rounding"), FieldForm.Amount, Editable: true, UnitField: $"{side}Currency", Group: group),
            new($"{side}DeliverRounding", "Delivery rounding", Words("delivery rounding"), FieldForm.Choice, Editable: true, Choices: Roundings, Group: group),
            new($"{side}ReturnRounding", "Return rounding", Words("return rounding"), FieldForm.Choice, Editable: true, Choices: Roundings, Group: group),
        ];
    }

    /// <summary>
    /// A trade's fields beyond the grids (ADR-0016 note 51): its identity and its agreement, which are the source's, and
    /// its break and its model, which a command sets.
    /// </summary>
    public static IReadOnlyList<TableField> TradeFields { get; } =
    [
        new("requestedId", "Requested id", ["requested id"], FieldForm.Id, Group: FieldGroups.Identity),
        new("tradeType", "Trade type", ["trade type"], FieldForm.Id, Group: FieldGroups.Identity),
        new("sourceProductType", "Source product type", ["source product type", "source product"], FieldForm.Id, Group: FieldGroups.Identity),
        new("simmProductClass", "SIMM product class", ["simm product class", "product class"], FieldForm.Id, Group: FieldGroups.Identity),
        new("userDefined", "User-defined", ["user-defined", "user defined"], FieldForm.Id, Optional: true, Group: FieldGroups.Identity),
        new("masterAgreement", "Master agreement", ["master agreement", "ma"], FieldForm.Id, Group: FieldGroups.Agreement),
        new("nettingSet", "Netting set", ["netting set"], FieldForm.Id, Group: FieldGroups.Agreement),
        new("breakDate", "Break date", ["break date", "break"], FieldForm.Date, Editable: true, Optional: true, Group: FieldGroups.Dates),
        new("breakMandatory", "Mandatory break", ["mandatory break", "break mandatory"], FieldForm.Flag, Editable: true, Group: FieldGroups.Dates),
        new("modelId", "Model id", ["model id", "model"], FieldForm.Id, Editable: true, Group: FieldGroups.Model),
        new("pricerId", "Pricer id", ["pricer id", "pricer"], FieldForm.Id, Editable: true, Group: FieldGroups.Model),
        new("interpolationId", "Interpolation id", ["interpolation id", "interpolation"], FieldForm.Id, Editable: true, Group: FieldGroups.Model),
        new("domesticCurrency", "Domestic currency", ["domestic currency", "domestic ccy"], FieldForm.Choice, Editable: true, Choices: FieldChoices.Currencies, Group: FieldGroups.Model),
        new("irModel", "IR model", ["ir model"], FieldForm.Choice, Editable: true, Choices: IrModels, Group: FieldGroups.Model),
        new("irBasis", "IR basis", ["ir basis"], FieldForm.Choice, Editable: true, Choices: IrBases, Group: FieldGroups.Model),
        new("equityModel", "Equity model", ["equity model"], FieldForm.Choice, Editable: true, Optional: true, Choices: EquityModels, Group: FieldGroups.Model),
        new("creditModel", "Credit model", ["credit model"], FieldForm.Choice, Editable: true, Optional: true, Choices: CreditModels, Group: FieldGroups.Model),
        new("inflationModel", "Inflation model", ["inflation model"], FieldForm.Choice, Editable: true, Optional: true, Choices: InflationModels, Group: FieldGroups.Model),
        new("unmodelledMethod", "Unmodelled method", ["unmodelled method", "unmodelled"], FieldForm.Choice, Editable: true, Choices: Unmodelled, Group: FieldGroups.Model),
    ];

    static readonly IReadOnlyDictionary<string, TableField> OfCsa = CsaFields.ToDictionary(f => f.Name), OfTrade = TradeFields.ToDictionary(f => f.Name);

    /// <summary>The field beyond the grids an entity of the kind has under the name; none where it has no such field.</summary>
    public static TableField? Find(string kind, string field) => kind switch
    {
        Entity.Csa => OfCsa.TryGetValue(field, out var csa) ? csa : null,
        Entity.Trade => OfTrade.TryGetValue(field, out var trade) ? trade : null,
        _ => null,
    };

    /// <summary>
    /// What the source holds in a field beyond the grids, which is an edit's Original and what a carry compares; null
    /// where the kind has no such field or this version has not the entity.
    /// </summary>
    public static SourceValue? Source(PortfolioSourceVersion source, string entity, string field)
    {
        var (kind, id) = Entity.Split(entity);
        if (Find(kind, field) is null) return null;
        return kind == Entity.Csa
            ? source.CsaOf(id) is { } csa ? new(Held(source, csa, field)) : null
            : source.Trade(id) is { } trade ? new(Held(source, trade, field)) : null;
    }

    static JsonNode? Held(PortfolioSourceVersion source, Csa csa, string field) => field switch
    {
        "counterparty" => source.Registration.Key,
        "nettingSet" => source.Agreement.NettingSetId,
        "agreementName" => csa.AgreementName,
        "enforceable" => source.Agreement.Enforceable,
        "main" => source.Agreement.MainCsaId == csa.CsaId,
        "userDefined" => csa.UserDefined,
        _ when field.StartsWith("counterparty", StringComparison.Ordinal) => Held(csa.Counterparty, field["counterparty".Length..]),
        _ when field.StartsWith("self", StringComparison.Ordinal) => Held(csa.Self, field["self".Length..]),
        _ => throw new InvalidOperationException($"A CSA has no {field}."),
    };

    static JsonNode? Held(CsaSide side, string term) => term switch
    {
        "Posts" => side.Posts, "VmCap" => side.VmCap, "VmCapCurrency" => side.VmCapCurrency, "IndependentAmount" => side.IndependentAmount,
        "Rounding" => side.Rounding, "DeliverRounding" => side.DeliverRounding, "ReturnRounding" => side.ReturnRounding,
        _ => throw new InvalidOperationException($"A CSA's side has no {term}."),
    };

    // The synthetic rule: a trade's identity follows its id and its product, its agreement is the counterparty's, it
    // has no break, and its model is its product's usual one. A new trade is no different: it is priced under the
    // agreement it would join.
    static JsonNode? Held(PortfolioSourceVersion source, SourceTrade trade, string field)
    {
        var (type, sourceType, model, pricer) = trade.Product switch
        {
            "IRS" => ("Swap", "IR-SWAP-FIXFLT", "M-IR-01", "P-SWAP-01"),
            "FXForward" => ("Forward", "FX-FWD", "M-FX-01", "P-FWD-01"),
            _ => throw new InvalidOperationException($"The synthetic source has no detail for a {trade.Product}."),
        };
        return field switch
        {
            "requestedId" => $"REQ-{trade.TradeId}",
            "tradeType" => type,
            "sourceProductType" => sourceType,
            "simmProductClass" => "RatesFX",
            "userDefined" => null,
            "masterAgreement" => source.Agreement.MaId,
            "nettingSet" => source.Agreement.NettingSetId,
            "breakDate" => null,
            "breakMandatory" => false,
            "modelId" => model,
            "pricerId" => pricer,
            "interpolationId" => "I-LIN-01",
            "domesticCurrency" => trade.Currency,
            "irModel" => "HW1F",
            "irBasis" => "deterministic",
            "equityModel" or "creditModel" or "inflationModel" => null,
            "unmodelledMethod" => "mtm-only",
            _ => throw new InvalidOperationException($"A trade has no {field}."),
        };
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
        var fields = Entity.Split(entity).Kind switch
        {
            Entity.Csa => CsaFields,
            Entity.Trade => TradeFields,
            _ => throw new InvalidOperationException($"{entity} has no fields beyond the grids."),
        };
        return new(fields.Select(f => KeyValuePair.Create(f.Name, Value(reading, entity, f.Name))));
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

    static readonly IReadOnlyList<string> Writes =
        ["currency", "counterpartyCurrency", "selfCurrency", "exchange", "oneWay", "counterpartyPosts", "selfPosts", "counterpartyThreshold", "counterpartyMta", "selfThreshold", "selfMta"];

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
        return new Dictionary<string, JsonNode?>
        {
            ["currency"] = currency, ["counterpartyCurrency"] = currency, ["selfCurrency"] = currency,
            ["exchange"] = counterparty || self, ["oneWay"] = counterparty != self,
            ["counterpartyPosts"] = counterparty, ["selfPosts"] = self,
            ["counterpartyThreshold"] = 0m, ["counterpartyMta"] = 0m, ["selfThreshold"] = 0m, ["selfMta"] = 0m,
        };
    }
}
