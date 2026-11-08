using System.Text.Json;
using System.Text.Json.Nodes;
using Workbench.Composition.Application.Workspaces;
using Workbench.Composition.Contracts;
using Workbench.Samples.Portfolio;
using Workbench.Samples.Rates;

namespace Workbench.Samples.Simm;

/// <summary>
/// SIMM's registration into the pricer (ADR-0012 §2, §3, §7 with their notes of 2026-11-08): spot IM, IM-R and IM-P,
/// its membership as a scope per direction and regime, its inputs (the trades' IM-R and IM-P, and the IM tab), and its
/// validation. Which IM CSAs a counterparty has and how a trade is classified are the portfolio source's; this module
/// says what they mean for spot IM. In the PoC its own request prices it (<see cref="SpotImPrice"/>).
/// </summary>
/// <param name="conventions">The confirmed conventions a trade in scope is resolved with for CRIF.</param>
/// <param name="marketOf">The market a value date's CRIF is generated on: a port, which the composition fills.</param>
/// <param name="tables">The SIMM parameter tables the request has loaded, which an IM CSA's SIMM version must name.</param>
public sealed class SimmMeasures(ConventionRegistry conventions, Func<DateOnly, MarketSnapshot> marketOf, IReadOnlyList<SimmParameters> tables) : IMeasureModule
{
    public const string Module = "simm";
    public const string SpotIm = "simm.spotIm";
    /// <summary>The input tab spot IM registers: the counterparty's IM CSAs (ADR-0012 §3).</summary>
    public const string ImTab = "im";

    public string Id => Module;

    public static IReadOnlyList<MeasureDefinition> Definitions { get; } =
        [new(SpotIm, "Spot IM", [new("imR", "IM-R", ImCsaTerms.Collect), new("imP", "IM-P", ImCsaTerms.Post)], "imR", DeclaresMembership: true, Traced: true)];

    public IReadOnlyList<MeasureDefinition> Measures => Definitions;

    /// <summary>
    /// The trades' IM-R and IM-P on both trade grids, and the IM tab with the counterparty's IM CSAs (ADR-0012 §3): shown
    /// while spot IM is selected, and kept while it is not.
    /// </summary>
    public IReadOnlyList<MeasureInput> Inputs { get; } = [new(TradesTab, "imR", SpotIm), new(TradesTab, "imP", SpotIm), new(ImTab, "imCsas", SpotIm)];

    /// <summary>The trade grids, as an input's tab: a field of every trade there (ADR-0010 §10's note of 2026-11-08).</summary>
    public const string TradesTab = "trades";

    public IReadOnlyList<IMembershipRule> Membership(WorkspaceState state) => [new Scopes((PortfolioSourceVersion)state.Source)];

    /// <summary>
    /// SIMM's part of the accepted input (ADR-0012 §4's note of 2026-11-08): the IM CSAs, the classification of every
    /// trade in Before or After, the value date's market pinned by its snapshot's ID with the portfolio's FX, and the
    /// conventions those trades resolve with.
    /// </summary>
    public JsonObject Input(WorkspaceState state)
    {
        var source = (PortfolioSourceVersion)state.Source;
        var priced = PortfolioMaterialiser.Trades(state).Where(t => t.Before || t.After).ToList();
        var classification = priced.ToDictionary(t => t.TradeId, t =>
        {
            var c = source.ImClassificationOf(t.TradeId) ?? throw new InvalidOperationException($"Trade {t.TradeId} is not the source's.");
            static IReadOnlyList<string> In(IReadOnlyList<RegimeStanding> standings) => [.. standings.Where(s => s.In).Select(s => s.Regime)];
            return new FrozenClassification(In(c.Collect), In(c.Post), c.Exemption);
        });
        var market = new FrozenMarket(marketOf(source.ValueDate).Id,
            PortfolioMarket.Currencies.ToDictionary(c => c, c => (double)PortfolioMarket.UnitsPerGbp(c, source.ValueDate)));
        var used = priced.Where(t => t.Product == "IRS").Select(t => t.Currency).Distinct().Order(StringComparer.Ordinal)
            .Select(c => PortfolioRatesLines.OvernightIndex.TryGetValue(c, out var index) ? conventions.Find(c, index) : null)
            // A currency with no confirmed convention freezes none: Price's validation refuses its trades by name.
            .OfType<ConventionProfile>().Select(p => new FrozenConvention(p.Currency, p.Index, p.Id, p.Version));
        return JsonSerializer.SerializeToNode(new SpotImInput(source.Agreement.ImCsas, classification, market, [.. used]), WireJson.Options)!.AsObject();
    }

    /// <summary>Never asked: Price validates spot IM over the trades it froze (the overload below), as spot capital does.</summary>
    public IReadOnlyList<MeasureRefusal> Validate(WorkspaceState state, IReadOnlyList<string> selected) =>
        throw new InvalidOperationException("Spot IM validates the trades Price froze.");

    /// <summary>
    /// Spot IM refused alone, by name (ADR-0012 §2, §4's note of 2026-11-08), where it cannot be computed over the trades
    /// Price froze: a trade naming an IM CSA that is not the counterparty's, a trade in scope that cannot be a Rates line,
    /// or IM CSAs naming a SIMM table that is not loaded. The other measures are priced.
    /// </summary>
    public IReadOnlyList<MeasureRefusal> Validate(WorkspaceState state, IReadOnlyList<string> selected, IReadOnlyList<EffectiveTrade> priced) =>
        selected.Contains(SpotIm) && Problem(state, priced) is { } problem ? [new MeasureRefusal(SpotIm, "SpotImRefused", problem)] : [];

    string? Problem(WorkspaceState state, IReadOnlyList<EffectiveTrade> priced)
    {
        var source = (PortfolioSourceVersion)state.Source;
        var agreement = source.Agreement;
        // Through its text, as the request reads the input the Run retained.
        IReadOnlyList<PricedImTrade> trades;
        try
        {
            trades = PricedImTrade.Read(JsonNode.Parse(JsonSerializer.Serialize(priced, WireJson.Options)));
        }
        catch (SimmInputException unreadable)
        {
            return string.Join(" ", unreadable.Problems);
        }
        foreach (var trade in trades)
            foreach (var direction in ImCsaTerms.Directions)
            {
                var held = direction == ImCsaTerms.Collect ? trade.ImR : trade.ImP;
                if (held is null) continue;
                var title = Title(direction);
                var csa = agreement.ImCsaOf(direction);
                if (csa is null) return $"{trade.TradeId}'s {title} names {held}, and {source.Registration.Key} has no {title} CSA.";
                if (held != csa.ImCsaId) return $"{trade.TradeId}'s {title} names {held}, which is not {source.Registration.Key}'s {title} CSA, {csa.ImCsaId}.";
            }
        foreach (var trade in trades.Where(t => t.InScope))
            if (PortfolioRatesLines.Resolve(trade, source.ValueDate, conventions).Problem is { } problem)
                return $"Spot IM cannot be computed: {problem}";
        return SpotImPrice.TableFor(agreement.ImCsas, tables).Problem;
    }

    public WorkflowStep Step(TypedDataRef acceptedInput) => new(Module, SpotImPrice.CapabilityId, SpotImPrice.CapabilityVersion, [],
        new Dictionary<string, Binding> { ["acceptedInput"] = new DataReferenceBinding(acceptedInput) }, FailurePolicy.Stop);

    /// <summary>
    /// A direction's IM CSA titled as the page shows it: IM-R is collected from the counterparty, IM-P posted to it.
    /// </summary>
    public static string Title(string direction) => direction == ImCsaTerms.Collect ? "IM-R" : "IM-P";

    /// <summary>A direction's letter in a membership's compact form (`R: JFSA, CFTC · P: JFSA`, ADR-0012 §2).</summary>
    static string Letter(string direction) => direction == ImCsaTerms.Collect ? "R" : "P";

    /// <summary>Why a counterparty without an IM CSA has no spot IM: said by its scopes and by its figures alike.</summary>
    public static string NoImCsa(string registration) => $"{registration} has no IM CSA: it exchanges no initial margin.";

    /// <summary>
    /// Spot IM's membership (ADR-0012 §2): a trade is in a scope, a direction and a regime, when its IM column of that
    /// direction holds the counterparty's IM CSA and the source classifies it under the regime. Out says why, scope by
    /// scope; a counterparty with no IM CSA has no scope at all, and says so.
    /// </summary>
    sealed class Scopes(PortfolioSourceVersion source) : IMembershipRule
    {
        public string Measure => SpotIm;
        public string Version => "simm-scopes/1";

        public Membership Of(EffectiveTrade trade)
        {
            var agreement = source.Agreement;
            if (agreement.ImCsas.Count == 0)
                return new(false, NoImCsa(source.Registration.Key), []);
            var classification = source.ImClassificationOf(trade.TradeId)
                ?? throw new InvalidOperationException($"Trade {trade.TradeId} is not the source's, so it has no IM classification.");
            var scopes = new List<MembershipScope>();
            var compact = new List<string>();
            foreach (var direction in ImCsaTerms.Directions)
            {
                if (agreement.ImCsaOf(direction) is not { } csa) continue;
                var held = direction == ImCsaTerms.Collect ? trade.ImR : trade.ImP;
                var title = Title(direction);
                var standings = classification.Exemption is { } exempt
                    ? csa.Regimes.Select(r => new RegimeStanding(r.Regime, false, exempt))
                    : classification.Of(direction);
                foreach (var standing in standings)
                {
                    var scope = $"{direction}/{standing.Regime}";
                    string? outBecause = classification.Exemption
                        ?? (held is null ? $"{title} is empty: no IM CSA holds the trade in this direction."
                            : held != csa.ImCsaId ? $"{title} names {held}, which is not {source.Registration.Key}'s {title} CSA, {csa.ImCsaId}."
                            : standing.In ? null : standing.Reason);
                    scopes.Add(outBecause is null
                        ? new(scope, $"{title} {standing.Regime}", true, $"{csa.ImCsaId} {(direction == ImCsaTerms.Collect ? "collects" : "posts")} under {standing.Regime}. {standing.Reason}")
                        : new(scope, $"{title} {standing.Regime}", false, outBecause));
                }
                var inScope = scopes.Where(s => s.In && s.Id.StartsWith($"{direction}/", StringComparison.Ordinal)).Select(s => s.Id[(direction.Length + 1)..]).ToList();
                compact.Add($"{Letter(direction)}: {(inScope.Count == 0 ? "none" : string.Join(", ", inScope))}");
            }
            return scopes.Any(s => s.In)
                ? new(true, string.Join(" · ", compact), scopes)
                : new(false, string.Join(" ", scopes.Select(s => s.Reason).Distinct()), scopes);
        }

        public Membership? Of(EffectiveCsa csa) => null;
    }
}
