using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Workbench.Composition.Application;
using Workbench.Composition.Application.Workspaces;
using Workbench.Composition.Contracts;
using Workbench.Samples.Portfolio;

namespace Workbench.Samples.Ccr;

/// <summary>
/// SA-CCR's registration into the pricer (ADR-0013 §6): spot CCR capital, its membership (every derivative of the
/// counterparty's netting set; one that cannot be decomposed is out, with why), its part of the accepted input (the
/// counterparty's reference data, the CSAs' margining, the collateral, the FX and the settings), its column on the trade
/// grids (what SA-CCR decided each trade is), and its validation. XVA's request prices it: its KVA option returns spot
/// capital (ADR-0013 §2), so this module has no step of its own.
/// </summary>
/// <param name="kappa">κ, configured (ADR-0013 §1: 8% DEFAULT, OPEN).</param>
public sealed class CcrMeasures(CapitalRatio? kappa = null) : IMeasureModule
{
    public const string Module = "ccr";
    public const string SpotCapital = "ccr.spotCapital";
    /// <summary>The module whose request prices spot capital: XVA's, with KVA on (ADR-0013 §2).</summary>
    public const string Request = "xva";
    public const string Class = "class";

    public static readonly CapitalRatio DefaultKappa = new(0.08, "ccr.capitalRatio", "DEFAULT 8%, ADR-0013 OPEN");
    public static readonly SaCcrSettings SaCcrSettings = new("basel cre52 · 2023-01-01");
    public static readonly RiskWeightSettings RiskWeightSettings = new(ExternalRatingsAllowed: true, Version: "basel cre20 · 2023-01-01");
    /// <summary>
    /// The bank's domestic currency, which picks an FX trade's foreign leg (CRE52.35). It is the bank's, never the
    /// currency the page shows, so a trade's adjusted notional does not move with the display (DEFAULT, ADR-0013 §7).
    /// </summary>
    public const string DomesticCurrency = "JPY";

    readonly CapitalRatio kappa = kappa ?? DefaultKappa;

    public string Id => Module;
    public string PricedBy => Request;

    public static IReadOnlyList<MeasureDefinition> Definitions { get; } =
        [new(SpotCapital, "Spot CCR capital", [new("capital", "Capital"), new("rwa", "RWA"), new("ead", "EAD")], "capital", DeclaresMembership: true, Traced: true)];

    public IReadOnlyList<MeasureDefinition> Measures => Definitions;

    public IReadOnlyList<TradeColumn> TradeColumns { get; } = [new(Class, "SA-CCR", "text", false)];

    /// <summary>What SA-CCR decides each trade is (ADR-0013 §6), read-only; or, where it cannot decompose it, why.</summary>
    public IReadOnlyList<IReadOnlyDictionary<string, TradeCell>?> Cells(WorkspaceState state, IReadOnlyList<EffectiveTrade> trades)
    {
        var market = MarketOf(state);
        return [.. trades.Select(t =>
        {
            TradeCell cell;
            try
            {
                cell = new TradeCell(SpotCapitalPricing.Classification(SpotCapitalPricing.Exposures(TradeOf(t), market)), TradeCell.Shown, null, false);
            }
            catch (SaCcrRefused refused)
            {
                cell = new TradeCell(null, TradeCell.Failed, refused.Message, false);
            }
            return (IReadOnlyDictionary<string, TradeCell>?)new Dictionary<string, TradeCell> { [Class] = cell };
        })];
    }

    public string? Check(string column, JsonNode? value) => $"{column} is what SA-CCR decided; it is not typed.";

    public IReadOnlyList<IMembershipRule> Membership(WorkspaceState state) =>
        [new Decomposable(MarketOf(state), ((PortfolioSourceVersion)state.Source).Agreement.NettingSetId)];

    public JsonObject Input(WorkspaceState state) => InputOf(state, kappa);

    /// <summary>
    /// SA-CCR's part of the accepted input: the counterparty's reference data, the netting set, each CSA's margining
    /// with the user's edits to its terms, the collateral, the FX of the value date and the settings. What the reference
    /// data does not know stays empty, and validation refuses on it by name.
    /// </summary>
    public static JsonObject InputOf(WorkspaceState state, CapitalRatio kappa)
    {
        var source = (PortfolioSourceVersion)state.Source;
        var registration = source.Registration;
        var profile = PortfolioCredit.ProfileOf(registration.Key);
        var market = MarketOf(state);
        static JsonArray Ratings(IEnumerable<SourceRating> ratings) => [.. ratings.Select(r => new JsonObject { ["agency"] = r.Agency, ["grade"] = r.Grade })];
        return new JsonObject
        {
            ["reportingCurrency"] = market.ReportingCurrency,
            ["fx"] = new JsonObject(market.ToReporting.Select(p => KeyValuePair.Create(p.Key, (JsonNode?)p.Value))),
            ["counterparty"] = new JsonObject
            {
                ["id"] = registration.Key,
                ["code"] = registration.Code,
                ["legalEntity"] = registration.LegalEntity,
                ["name"] = registration.Name,
                ["type"] = profile?.InstitutionType,
                ["country"] = profile?.Country,
                ["ratings"] = Ratings(profile?.Ratings ?? []),
                ["sovereignRatings"] = Ratings(profile?.SovereignRatings ?? []),
                ["supervisedAsBank"] = profile?.SupervisedAsBank,
                ["scraGrade"] = profile?.ScraGrade,
                ["dueDiligenceBucketsUp"] = profile?.DueDiligenceBucketsUp,
                ["sme"] = profile?.Sme,
                ["investmentGrade"] = profile?.InvestmentGrade,
            },
            ["nettingSet"] = new JsonObject
            {
                ["id"] = source.Agreement.NettingSetId,
                ["agreement"] = source.Agreement.MaId,
                ["enforceable"] = source.Agreement.Enforceable,
            },
            ["csas"] = new JsonArray([.. PortfolioMaterialiser.Csas(state).Select(c =>
            {
                var terms = source.CsaOf(c.CsaId) ?? throw new InvalidOperationException($"CSA {c.CsaId} is not the source's.");
                return new JsonObject
                {
                    ["csaId"] = c.CsaId, ["currency"] = c.Currency, ["frequency"] = c.Frequency, ["exchange"] = c.Exchange, ["oneWay"] = c.OneWay,
                    // SA-CCR's threshold and MTA are the counterparty's: what it may owe before it posts (CRE52.18).
                    ["threshold"] = c.CounterpartyThreshold, ["mta"] = c.CounterpartyMta, ["thresholdCurrency"] = c.CounterpartyCurrency,
                    ["disputes"] = terms.Disputes, ["illiquidCollateral"] = terms.IlliquidCollateral,
                };
            })]),
            ["collateral"] = new JsonArray([.. source.Agreement.Csas.SelectMany(c => PortfolioCredit.CollateralOf(c.CsaId)).Select(h => new JsonObject
            {
                ["id"] = h.Id, ["csaId"] = h.CsaId, ["direction"] = h.Direction, ["purpose"] = h.Purpose, ["type"] = h.Type,
                ["currency"] = h.Currency, ["amount"] = h.Amount, ["segregated"] = h.Segregated, ["issuerGrade"] = h.IssuerGrade, ["residualYears"] = h.ResidualYears,
            })]),
            ["settings"] = new JsonObject
            {
                ["kappa"] = new JsonObject { ["value"] = kappa.Value, ["key"] = kappa.Key, ["version"] = kappa.Version },
                ["saCcr"] = new JsonObject { ["version"] = SaCcrSettings.Version, ["irOffsetAcrossBuckets"] = SaCcrSettings.IrOffsetAcrossBuckets },
                ["riskWeight"] = new JsonObject { ["externalRatingsAllowed"] = RiskWeightSettings.ExternalRatingsAllowed, ["version"] = RiskWeightSettings.Version },
                ["domesticCurrency"] = DomesticCurrency,
            },
        };
    }

    public IReadOnlyList<MeasureRefusal> Validate(WorkspaceState state, IReadOnlyList<string> selected) =>
        throw new InvalidOperationException("Spot capital validates the trades Price froze.");

    /// <summary>
    /// Spot capital is refused by name where it cannot be computed as the workspace stands (ADR-0013 §6): a counterparty
    /// without an exposure class, a CSA whose margining SA-CCR cannot read, a trade without the fields its decomposition
    /// needs. It blocks spot capital alone; CVA and FVA stand.
    /// </summary>
    public IReadOnlyList<MeasureRefusal> Validate(WorkspaceState state, IReadOnlyList<string> selected, IReadOnlyList<EffectiveTrade> priced) =>
        selected.Contains(SpotCapital) && Problem(state, priced, kappa) is { } problem
            ? [new MeasureRefusal(SpotCapital, "SpotCapitalRefused", problem)]
            : [];

    /// <summary>
    /// Why spot capital cannot be computed over the trades Price froze, or null when it can, read exactly as the
    /// provider will read the accepted input. KVA, which reads spot capital's profile, is refused on it too.
    /// </summary>
    public static string? Problem(WorkspaceState state, IReadOnlyList<EffectiveTrade> priced, CapitalRatio? kappa = null)
    {
        var source = (PortfolioSourceVersion)state.Source;
        if (PortfolioCredit.ProfileOf(source.Registration.Key) is null)
            return $"{source.Registration.Key} has no reference data, so its exposure class cannot be decided.";
        // Through its text, as the provider reads the input the Run retained.
        var input = JsonNode.Parse(new JsonObject
        {
            ["context"] = new JsonObject { ["valueDate"] = source.ValueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) },
            ["trades"] = JsonSerializer.SerializeToNode(priced, WireJson.Options),
            ["modules"] = new JsonObject { [Module] = InputOf(state, kappa ?? DefaultKappa) },
        }.ToJsonString())!.AsObject();
        try
        {
            var portfolio = CapitalInput.Read(input, Module);
            return SpotCapitalPricing.Problem(portfolio, SpotCapitalPricing.Before) ?? SpotCapitalPricing.Problem(portfolio, SpotCapitalPricing.After);
        }
        catch (Exception e) when (e is SaCcrRefused or RiskWeightRefused)
        {
            return e.Message;
        }
    }

    public WorkflowStep Step(TypedDataRef acceptedInput) =>
        throw new InvalidOperationException($"Spot capital is priced by {Request}'s request, whose KVA option returns it (ADR-0013 §2); {Module} has no step of its own.");

    /// <summary>
    /// The value date, the reporting currency, the synthetic FX of the day and the bank's domestic currency, as the
    /// decomposition reads them. Spot capital is computed in the workspace's display currency (ADR-0010 §1), so its
    /// figures, as XVA's, and its trace are in the currency the page shows; SA-CCR converts every amount at the same
    /// rates, and the domestic currency that picks an FX trade's foreign leg is the bank's, so the figure is the one
    /// computed in any other currency, converted.
    /// </summary>
    public static CapitalMarket MarketOf(WorkspaceState state)
    {
        var date = ((PortfolioSourceVersion)state.Source).ValueDate;
        var currency = PortfolioMaterialiser.DisplayCurrency(state);
        var reporting = PortfolioMarket.UnitsPerGbp(currency, date);
        return new CapitalMarket(date, currency,
            PortfolioMarket.Currencies.ToDictionary(c => c, c => (double)(reporting / PortfolioMarket.UnitsPerGbp(c, date))), DomesticCurrency);
    }

    /// <summary>A trade of the workspace as spot capital reads it.</summary>
    public static CapitalTrade TradeOf(EffectiveTrade t) => new(
        t.TradeId, t.IsNew, t.Product, t.Currency, (double)t.Notional, DateOnly.ParseExact(t.Maturity, "yyyy-MM-dd", CultureInfo.InvariantCulture), t.Csa,
        t.Before, t.After, (double?)t.OfficialPv,
        t.Start is { } start ? DateOnly.ParseExact(start, "yyyy-MM-dd", CultureInfo.InvariantCulture) : null,
        t.Direction, t.CounterCurrency, (double?)t.CounterNotional);

    /// <summary>Spot capital's membership: every derivative SA-CCR can decompose; one it cannot is out, with why.</summary>
    sealed class Decomposable(CapitalMarket market, string nettingSet) : IMembershipRule
    {
        public string Measure => SpotCapital;
        public string Version => "sa-ccr-decomposition/1";

        public Membership Of(EffectiveTrade trade)
        {
            try
            {
                SpotCapitalPricing.Exposures(TradeOf(trade), market);
                return new(true, $"A derivative of {nettingSet}");
            }
            catch (SaCcrRefused refused)
            {
                return new(false, refused.Message);
            }
        }

        public Membership? Of(EffectiveCsa csa) => null;
    }
}

/// <summary>Spot capital's answer to one request: each side, its result rows and its traces.</summary>
public sealed record SpotCapitalAnswer(SideCapital Before, SideCapital After, CapitalPortfolio? Portfolio, IReadOnlyList<JsonObject> Rows, TraceSet Traces);

/// <summary>
/// Spot capital over a portfolio accepted input, as any request that prices it answers (ADR-0013 §2): XVA's KVA option
/// and the capability of its own. The rows are written only where the input selected spot capital; a measure refused at
/// Price is reported with its reason and not computed; a side that could not be computed is <c>unavailable</c> with why.
/// </summary>
public static class SpotCapitalResults
{
    public const string Case = "base";
    public const string CaseTitle = "Workspace";
    public const string Scenario = "base";

    public static SpotCapitalAnswer Answer(JsonObject acceptedInput, string requestId, DatasetProvenance provenance)
    {
        var selected = acceptedInput["measures"]?.AsArray().Select(m => m!.AsObject()).SingleOrDefault(m => m["id"]?.GetValue<string>() == CcrMeasures.SpotCapital);
        var refusedAtPrice = selected?["refused"]?["message"]?.GetValue<string>();
        CapitalPortfolio? portfolio = null;
        SideCapital before, after;
        try
        {
            portfolio = CapitalInput.Read(acceptedInput, CcrMeasures.Module);
            (before, after) = (SpotCapitalPricing.Price(portfolio, SpotCapitalPricing.Before, requestId, provenance),
                SpotCapitalPricing.Price(portfolio, SpotCapitalPricing.After, requestId, provenance));
        }
        catch (Exception e) when (e is SaCcrRefused or RiskWeightRefused)
        {
            // The part spot capital reads could not be read: every side is unavailable, with why.
            var trades = acceptedInput["trades"]?.AsArray() ?? [];
            int Count(string side) => trades.Count(t => t?[side]?.GetValue<bool>() == true);
            (before, after) = (new SideCapital(SpotCapitalPricing.Before, Count("before"), Refused: e.Message), new SideCapital(SpotCapitalPricing.After, Count("after"), Refused: e.Message));
        }

        var rows = new List<JsonObject>();
        var traces = new List<TraceEntry>();
        var currency = portfolio?.ReportingCurrency ?? acceptedInput["displayCurrency"]?.GetValue<string>()
            ?? throw new SaCcrRefused("The accepted input names no display currency.");
        if (selected is not null)
        {
            foreach (var metric in CcrMeasures.Definitions.Single().Metrics)
            {
                if (refusedAtPrice is not null)
                {
                    var refused = new MeasureResult.Side(MeasureResult.Refused, null, null, refusedAtPrice);
                    rows.Add(MeasureResult.Row(Case, CaseTitle, CcrMeasures.SpotCapital, metric, currency, refused, refused));
                    continue;
                }
                rows.Add(MeasureResult.Row(Case, CaseTitle, CcrMeasures.SpotCapital, metric, currency, Figure(before, metric.Id), Figure(after, metric.Id)));
            }
            if (refusedAtPrice is null)
                foreach (var side in new[] { before, after })
                    if (side.Trace is { } trace) traces.Add(new TraceEntry(Case, Scenario, side.Side, CcrMeasures.SpotCapital, trace));
        }
        return new SpotCapitalAnswer(before, after, portfolio, rows, new TraceSet(traces));
    }

    // Rounded as the other measures' figures are; the trace keeps every figure as computed.
    static MeasureResult.Side Figure(SideCapital side, string metric) =>
        side.Trades == 0 ? MeasureResult.Side.NotPricedWith(0)
        : side.Capital is not { } capital ? new MeasureResult.Side(MeasureResult.Unavailable, null, side.Trades, side.Refused)
        : MeasureResult.Side.Of(Math.Round((decimal)(metric switch
        {
            "capital" => capital.Capital,
            "rwa" => capital.Rwa,
            "ead" => capital.Exposure.Ead,
            _ => throw new InvalidOperationException($"Spot capital has no metric {metric}."),
        }), 2), side.Trades);
}

/// <summary>
/// Spot capital as a capability of its own (ADR-0013 §2, C12, C27), for the command line, the AI and workflows: the
/// reference implementation over a portfolio accepted input, with its traces. Simulated, as every figure says.
/// </summary>
public sealed class SpotCapitalPrice : ICapabilityHandler
{
    public const string CapabilityId = "ccr.spotCapital";
    public const string CapabilityVersion = "1.0.0";

    public CapabilityDescriptor Descriptor { get; } = new(CapabilityId, CapabilityVersion, "Spot CCR capital, SA-CCR and SA risk weight (simulated)",
        [new("acceptedInput", PortfolioContracts.AcceptedInput, PortfolioContracts.Version, Cardinality.One)],
        [new("result", PortfolioContracts.MeasureResult, PortfolioContracts.Version, Cardinality.One),
         new("traces", TraceSet.ContractId, TraceSet.ContractVersion, Cardinality.One)],
        ExecutionKind.RequestResponse, [CapabilityEffect.Compute]);

    public Task<IReadOnlyDictionary<string, JsonNode?>> InvokeAsync(CapabilityInvocation invocation, CancellationToken cancellationToken)
    {
        var reference = invocation.Inputs["acceptedInput"].Deserialize<TypedDataRef>(WireJson.Options)
            ?? throw new CapabilityFailure("InvalidInput", "The accepted input is required.");
        var input = invocation.Data.ReadRequired(reference).AsObject();
        var provenance = new DatasetProvenance(ExecutionMode.Simulated, DataOrigin.Synthetic, invocation.InvocationId);
        var answer = SpotCapitalResults.Answer(input, invocation.InvocationId, provenance);
        return Task.FromResult<IReadOnlyDictionary<string, JsonNode?>>(new Dictionary<string, JsonNode?>
        {
            ["result"] = Datasets.Create("Spot CCR capital (simulated)", MeasureResult.Columns, answer.Rows, provenance),
            ["traces"] = JsonSerializer.SerializeToNode(answer.Traces, WireJson.Options),
        });
    }
}

/// <summary>Registers SA-CCR's capability and its trace catalogue (ADR-0011 §2).</summary>
public sealed class CcrModule : IWorkbenchModule
{
    public void Register(CompositionRegistryBuilder builder)
    {
        builder.AddCapability(new SpotCapitalPrice());
        builder.AddTraceCatalog(SpotCapitalTrace.Catalog);
    }
}
