using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Qube.Composition.Application;
using Qube.Composition.Application.Products;
using Qube.Composition.Application.Workspaces;

namespace Qube.Samples.Desk;

/// <summary>
/// The what-if hedges of a Desk workspace (ADR-0019 §2): entities the user creates in the scope <c>hedges</c>, each a
/// row of the one edit log. A hedge is hypothetical wherever it is read; nothing here books a trade.
/// </summary>
public static partial class DeskHedges
{
    public const string Scope = "hedges";
    public const string Product = "product", Currency = "currency", Notional = "notional", Direction = "direction", End = "end", Rate = "rate";
    public const string Irs = "irs";
    const char Prefix = 'H';

    public static IEnumerable<CreatedEntity> In(WorkspaceState state) => state.Created.Values.Where(c => c.Scope == Scope).OrderBy(c => c.Seq);

    /// <summary>A hedge's fields as they stand: what it was made with, and the user's edits on them.</summary>
    public static IReadOnlyDictionary<string, JsonNode?> Fields(WorkspaceState state, string hedge)
    {
        var fields = state.Created[hedge].Fields.ToDictionary(p => p.Key, p => p.Value);
        foreach (var o in state.Overrides.Values.Where(o => o.Target.Scope == Scope && o.Target.Entity == hedge))
            fields[o.Target.Field] = o.Value;
        return fields;
    }

    /// <summary>The name the next hedge takes: <c>H</c> and one more than the highest the log ever made, so a name is never given twice.</summary>
    public static string NextName(WorkspaceState state)
    {
        var highest = 0L;
        foreach (var entry in state.Log)
            if (entry is { Kind: EditKind.Create, Target: { Scope: Scope, Entity: var made } } && made.Length > 1 && made[0] == Prefix
                && long.TryParse(made.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > highest)
                highest = n;
        return $"{Prefix}{highest + 1}";
    }

    [GeneratedRegex("^([1-9][0-9]*)(d|w|m|y)$")]
    private static partial Regex Period();

    /// <summary>A hedge's end as a date: a date as written, or a period counted from the valuation date.</summary>
    public static DateOnly? EndDate(string text, DateOnly valuation)
    {
        if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return date;
        if (Period().Match(text) is not { Success: true } period) return null;
        var n = int.Parse(period.Groups[1].ValueSpan, CultureInfo.InvariantCulture);
        return period.Groups[2].Value switch
        {
            "d" => valuation.AddDays(n),
            "w" => valuation.AddDays(7 * n),
            "m" => valuation.AddMonths(n),
            _ => valuation.AddYears(n),
        };
    }

    /// <summary>A hedge's fields read into the swap they describe, or every reason they describe none.</summary>
    public static (HedgeSwap? Swap, IReadOnlyList<string> Issues) Read(string hedge, IReadOnlyDictionary<string, JsonNode?> fields, DateOnly valuation)
    {
        var issues = new List<string>();
        string? Text(string name) => fields.GetValueOrDefault(name) is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;
        double? Number(string name)
        {
            if (fields.GetValueOrDefault(name) is not JsonValue v) return null;
            if (v.GetValueKind() == System.Text.Json.JsonValueKind.Number) return (double)FieldForms.Held(v);
            issues.Add($"{name} is not a number.");
            return null;
        }

        if (Text(Product) != Irs) issues.Add($"The product is {Text(Product) ?? "empty"}; a hedge is an {Irs}.");
        var currency = Text(Currency);
        if (currency is null) issues.Add("The currency is empty.");
        var notional = Number(Notional);
        if (notional is null) issues.Add("The notional is empty.");
        else if (notional <= 0) issues.Add("The notional is more than 0; the direction says which way the hedge is.");
        bool? pay = Text(Direction) switch { "pay" => true, "receive" => false, _ => null };
        if (pay is null) issues.Add("The direction is pay or receive (the fixed rate).");
        var end = Text(End) is { } text ? EndDate(text, valuation) : null;
        if (Text(End) is null) issues.Add("The end is empty.");
        else if (end is null) issues.Add($"'{Text(End)}' is neither a date (yyyy-MM-dd) nor a period.");
        var rate = Number(Rate);
        if (rate is null) issues.Add("The fixed rate is empty: this slice does not solve a par rate.");
        return issues.Count > 0 ? (null, issues) : (new HedgeSwap(hedge, currency!, notional!.Value, pay!.Value, end!.Value, rate!.Value), []);
    }

    /// <summary>
    /// A hedge and its risk as the workspace stands, or why it has none: its terms describe no swap, or the provider
    /// refuses it. A hedge without risk is shown as such and adds nothing; it is never given a figure (ADR-0019 §4).
    /// </summary>
    public static (HedgeSwap? Swap, HedgeRiskAnswer? Risk, IReadOnlyList<string> Issues) Risk(WorkspaceState state, string hedge, IHedgeRisk provider)
    {
        var book = DeskBookSource.Of(state).Set;
        var (swap, issues) = Read(hedge, Fields(state, hedge), book.Manifest.ValuationDate);
        if (swap is null) return (null, null, issues);
        try
        {
            return (swap, provider.Risk(swap, book), []);
        }
        catch (HedgeRiskRefused refused)
        {
            return (swap, null, [refused.Message]);
        }
    }
}

/// <summary>The Desk's product (ADR-0008 §7, ADR-0019 §7): one hypothetical fixed-float interest rate swap.</summary>
public sealed class DeskProducts : IProductCatalog
{
    public const string Irs = "desk.irs";
    public const string Version = "1.0.0";

    internal static readonly IReadOnlyDictionary<string, string> Directions = new Dictionary<string, string>
    {
        ["pay"] = "pay", ["p"] = "pay", ["payer"] = "pay", ["receive"] = "receive", ["rec"] = "receive", ["r"] = "receive", ["receiver"] = "receive",
    };

    /// <summary>The currencies a hedge may be typed in. Whether a result set holds a curve for one is asked of it, and refused by name where not.</summary>
    internal static readonly IReadOnlyDictionary<string, string> Currencies =
        new[] { "JPY", "USD", "EUR", "GBP", "AUD", "CHF", "CAD" }.ToDictionary(c => c.ToLowerInvariant(), c => c);

    static readonly ProductDescriptor Descriptor = new(Irs, Version, DeskEditRules.App, DeskHedges.Scope, "irs", "What-if interest rate swap",
    [
        new(DeskHedges.End, "End: a period from the valuation date, or a date", FieldForm.PeriodOrDate, Required: true, Unnamed: true, Examples: ["1y", "2y", "5y", "10y", "20y", "30y"]),
        new(DeskHedges.Currency, "Currency", FieldForm.Choice, Required: true, Unnamed: true, Choices: Currencies, Aliases: ["ccy"]),
        new(DeskHedges.Notional, "Notional", FieldForm.Amount, Required: true, Unnamed: true),
        new(DeskHedges.Direction, "Pay or receive the fixed rate", FieldForm.Choice, Required: true, Unnamed: true, Choices: Directions),
        new(DeskHedges.Rate, "Fixed rate", FieldForm.Percent, Required: true, Unnamed: true),
    ], Table: DeskTables.Hedges);

    public IEnumerable<ProductDescriptor> Products => [Descriptor];
}

/// <summary>
/// Making a what-if swap (ADR-0019 §2, §7): its terms must describe a swap whose risk the provider answers on the
/// workspace's book, or nothing is written and every reason is given.
/// </summary>
public sealed class DeskIrsRules(Func<WorkspaceService> workspaces, IHedgeRisk provider) : IProductRules
{
    public string ProductId => DeskProducts.Irs;

    // Nothing is supplied for a term left out: a hedge's terms are all the user's.
    public IReadOnlyDictionary<string, ProductDefault> Defaults(WorkspaceState workspace, IReadOnlyDictionary<string, JsonNode?> given) =>
        new Dictionary<string, ProductDefault>();

    JsonObject Checked(WorkspaceState state, string hedge, IReadOnlyDictionary<string, JsonNode?> terms)
    {
        if (state.App != DeskEditRules.App) throw new ProductTermsRefused([new("InvalidTerms", null, "A what-if swap is made in a Desk workspace.")]);
        var fields = new JsonObject { [DeskHedges.Product] = DeskHedges.Irs };
        foreach (var name in new[] { DeskHedges.End, DeskHedges.Currency, DeskHedges.Notional, DeskHedges.Direction, DeskHedges.Rate })
            fields[name] = terms.GetValueOrDefault(name)?.DeepClone();
        var book = DeskBookSource.Of(state).Set;
        var (swap, issues) = DeskHedges.Read(hedge, fields.ToDictionary(f => f.Key, f => f.Value), book.Manifest.ValuationDate);
        if (swap is null) throw new ProductTermsRefused([.. issues.Select(i => new ProductIssue("InvalidTerms", null, i))]);
        try
        {
            provider.Risk(swap, book);
        }
        catch (HedgeRiskRefused refused)
        {
            throw new ProductTermsRefused([new(refused.Code, null, refused.Message)]);
        }
        return fields;
    }

    public (WorkspaceState State, string Entity) Create(string workspaceId, long? revision, string user, IReadOnlyDictionary<string, JsonNode?> terms,
        IReadOnlyDictionary<string, string> sources, RunOrigin origin)
    {
        var state = workspaces().Find(workspaceId);
        if (state is null || state.App != DeskEditRules.App) throw new WorkspaceRefused("UnknownWorkspace", $"No Desk workspace {workspaceId}.");
        if (state.Owner != user) throw new WorkspaceRefused("NotOwner", "This workspace belongs to another user.");
        var hedge = DeskHedges.NextName(state);
        var fields = Checked(state, hedge, terms);
        return (workspaces().Create(workspaceId, revision ?? state.Revision, user, DeskHedges.Scope, hedge, fields, origin, sources), hedge);
    }

    public string WouldCreate(WorkspaceState workspace, IReadOnlyDictionary<string, JsonNode?> terms)
    {
        var hedge = DeskHedges.NextName(workspace);
        Checked(workspace, hedge, terms);
        return hedge;
    }
}
