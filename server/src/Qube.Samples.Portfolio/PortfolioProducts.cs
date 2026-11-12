using System.Text.Json.Nodes;
using Qube.Composition.Application;
using Qube.Composition.Application.Products;
using Qube.Composition.Application.Workspaces;

namespace Qube.Samples.Portfolio;

// ADR-0016 note 48: a row a workspace makes is an entity of a described product (ADR-0008). The pricer describes one:
// a PV leg, made under a trade (note 49).

/// <summary>The products the pricer's workspace makes with <c>new</c>.</summary>
public sealed class PortfolioProducts : IProductCatalog
{
    /// <summary>A PV leg under a trade: <c>new leg E00003 eur 250,000</c>.</summary>
    public const string Leg = "xva.leg";

    /// <summary>The term that names the trade a leg is made under.</summary>
    public const string Trade = "trade";

    static readonly ProductDescriptor LegProduct = new(Leg, PortfolioContracts.Version, PortfolioEditRules.App, "trade", "leg", "PV leg",
    [
        new(Trade, "Trade", FieldForm.Id, Required: true),
        new("currency", "Currency", FieldForm.Choice, Required: true, Unnamed: true, Choices: PortfolioMarket.Currencies.ToDictionary(c => c.ToLowerInvariant(), c => c)),
        // A plain number as a grid cell takes it, never in millions; the notional is said by its name, since it is written as the PV is.
        new("pv", "PV", FieldForm.Number, Required: true, Unnamed: true),
        new("notional", "Notional", FieldForm.Number),
    ], Table: PortfolioTables.Legs, Under: Trade);

    public IEnumerable<ProductDescriptor> Products => [LegProduct];
}

/// <summary>
/// What a leg's description cannot say (ADR-0016 note 49): its trade is one of the workspace's, and its number follows
/// the trade's last. Nothing is defaulted: a leg's currency and PV are typed.
/// </summary>
public sealed class LegRules(Func<PortfolioWorkspaces> workspaces) : IProductRules
{
    public string ProductId => PortfolioProducts.Leg;

    public IReadOnlyDictionary<string, ProductDefault> Defaults(WorkspaceState workspace, IReadOnlyDictionary<string, JsonNode?> given) => new Dictionary<string, ProductDefault>();

    public string WouldCreate(WorkspaceState workspace, IReadOnlyDictionary<string, JsonNode?> terms) => PortfolioWorkspaces.LegToMake(workspace, Text(terms, PortfolioProducts.Trade));

    public (WorkspaceState State, string Entity) Create(string workspaceId, long? revision, string user, IReadOnlyDictionary<string, JsonNode?> terms,
        IReadOnlyDictionary<string, string> sources, RunOrigin origin) =>
        workspaces().NewLeg(workspaceId, revision, user, Text(terms, PortfolioProducts.Trade), Text(terms, "currency"), FieldForms.Held(Term(terms, "pv")), Notional(terms), origin);

    public (IReadOnlyList<WorkspaceChange> Changes, string Entity)? Planned(WorkspaceState workspace, IReadOnlyDictionary<string, JsonNode?> terms, IReadOnlyDictionary<string, string> sources)
    {
        var (made, id) = PortfolioWorkspaces.LegMade(workspace, Text(terms, PortfolioProducts.Trade), Text(terms, "currency"), FieldForms.Held(Term(terms, "pv")), Notional(terms));
        return ([made], id);
    }

    // A leg's notional is typed or it has none.
    static decimal? Notional(IReadOnlyDictionary<string, JsonNode?> terms) => terms.GetValueOrDefault("notional") is { } notional ? FieldForms.Held(notional) : null;

    // A term the description requires, which the reading filled before the rules were asked.
    static JsonNode Term(IReadOnlyDictionary<string, JsonNode?> terms, string name) =>
        terms.GetValueOrDefault(name) ?? throw new InvalidOperationException($"A leg's terms are complete and hold no {name}.");

    static string Text(IReadOnlyDictionary<string, JsonNode?> terms, string name) => Term(terms, name).GetValue<string>();
}
