using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Workbench.Composition.Application;
using Workbench.Composition.Contracts;
using Workbench.Samples.Portfolio;
using Workbench.Samples.Rates;

namespace Workbench.Samples.Simm;

/// <summary>How the source classified one priced trade for IM, as Price froze it (ADR-0012 §2): exempt, or the regimes it counts under.</summary>
public sealed record FrozenClassification(IReadOnlyList<string> Collect, IReadOnlyList<string> Post, string? Exemption = null);

/// <summary>
/// The value date's market as Price pinned it (ADR-0012 §4's note of 2026-11-08): the Rates snapshot whose curves CRIF
/// is generated on, by its ID, and the portfolio's FX of the value date, units of each currency per GBP, which every
/// measure on the page converts at.
/// </summary>
public sealed record FrozenMarket(string Snapshot, IReadOnlyDictionary<string, double> Fx);

/// <summary>A convention profile a trade in scope is resolved with, as it stood at Price.</summary>
public sealed record FrozenConvention(string Currency, string Index, string Id, string Version);

/// <summary>
/// SIMM's part of the portfolio's accepted input (ADR-0012 §4's note of 2026-11-08): the counterparty's IM CSAs, each
/// priced trade's classification, the pinned market and the conventions. The trades themselves, with their IM-R, IM-P
/// and scopes, are the accepted input's own.
/// </summary>
public sealed record SpotImInput(IReadOnlyList<ImCsaTerms> ImCsas, IReadOnlyDictionary<string, FrozenClassification> Classification, FrozenMarket Market,
    IReadOnlyList<FrozenConvention> Conventions);

/// <summary>
/// A trade of the accepted input as spot IM reads it: the terms its Rates line takes, its sides, its IM-R and IM-P, and
/// its spot IM scopes as Price froze them. A field it needs that the input does not hold is refused by name.
/// </summary>
public sealed record PricedImTrade(string TradeId, bool Before, bool After, string Product, string Currency, decimal Notional, string Maturity,
    decimal? FixedRate, string? Schedule, string? Start, string? Direction, string? ImR, string? ImP, IReadOnlyList<MembershipScope> Scopes)
{
    public bool InScope => Scopes.Any(s => s.In);

    /// <summary>Whether a scope of the direction (<c>collect</c>, <c>post</c>) holds the trade.</summary>
    public bool In(string direction) => Scopes.Any(s => s.In && s.Id.StartsWith($"{direction}/", StringComparison.Ordinal));

    /// <summary>
    /// The accepted input's trades, read as the request reads them; validation reads the trades Price froze through the
    /// same text, so that what it refuses and what is priced are one reading (as spot capital's are).
    /// </summary>
    public static IReadOnlyList<PricedImTrade> Read(JsonNode? trades)
    {
        if (trades is not JsonArray array) throw new SimmInputException(["The accepted input has no trades."]);
        return [.. array.Select((node, i) => node is JsonObject trade ? Of(trade, i) : throw new SimmInputException([$"Trade {i + 1} of the accepted input is not an object."]))];
    }

    static PricedImTrade Of(JsonObject t, int i)
    {
        var at = t["tradeId"] is JsonValue key && key.TryGetValue<string>(out var named) ? named : $"Trade {i + 1}";
        SimmInputException Missing(string field) => new([$"{at} of the accepted input has no {field}."]);
        string Text(string field) => t[field] is JsonValue v && v.TryGetValue<string>(out var text) ? text : throw Missing(field);
        string? Optional(string field) => t[field] is null ? null : Text(field);
        decimal? Number(string field) => t[field] is null ? null : t[field] is JsonValue v && v.TryGetValue<decimal>(out var n) ? n : throw Missing(field);
        bool Flag(string field) => t[field] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : throw Missing(field);
        // The trade's spot IM standing is Price's, never worked out again here (ADR-0012 §2).
        var membership = t["membership"]?[SimmMeasures.SpotIm] as JsonObject ?? throw Missing("spot IM membership");
        // Every spot IM membership writes its scopes, none included; a membership without them is not one Price froze.
        var scopes = (membership["scopes"] as JsonArray ?? throw Missing("spot IM scopes"))
            .Select(s => s.Deserialize<MembershipScope>(WireJson.Options) ?? throw Missing("scope")).ToList();
        return new(Text("tradeId"), Flag("before"), Flag("after"), Text("product"), Text("currency"), Number("notional") ?? throw Missing("notional"),
            Text("maturity"), Number("fixedRate"), Optional("schedule"), Optional("start"), Optional("direction"), Optional("imR"), Optional("imP"), scopes);
    }
}

/// <summary>
/// A portfolio IRS as a Rates line (ADR-0012 §4's note of 2026-11-08): fixed against the currency's overnight index,
/// from its start date to its maturity date, its schedule both legs' payment frequency and the floating leg's reset, and
/// <c>payFixed</c> paying leg 1. A trade that cannot be one says why; nothing is guessed for it.
/// </summary>
public static class PortfolioRatesLines
{
    /// <summary>Each currency's overnight index, which both legs name (DEFAULT, 2026-11-08).</summary>
    public static readonly IReadOnlyDictionary<string, string> OvernightIndex = new Dictionary<string, string>
    {
        ["JPY"] = "TONA", ["USD"] = "SOFR", ["EUR"] = "ESTR", ["GBP"] = "SONIA",
    };

    public static (RatesLine? Line, string? Problem) LineOf(PricedImTrade trade)
    {
        var id = trade.TradeId;
        if (trade.Product != "IRS") return (null, $"{id} is a {trade.Product}; CRIF is generated for an IRS alone here.");
        if (!OvernightIndex.TryGetValue(trade.Currency, out var index)) return (null, $"{id} is in {trade.Currency}, which has no overnight index here.");
        if (trade.FixedRate is not { } rate) return (null, $"{id} has no fixed rate.");
        if (trade.Schedule is not { } schedule) return (null, $"{id} has no schedule.");
        if (trade.Notional <= 0) return (null, $"{id}'s notional is not positive.");
        if (trade.Start is not { } start) return (null, $"{id} has no start date.");
        var direction = trade.Direction switch
        {
            "payFixed" => Direction.Pay,
            "receiveFixed" => Direction.Receive,
            _ => (Direction?)null,
        };
        if (direction is null) return (null, $"{id} neither pays nor receives fixed.");
        var frequency = Tenor.Parse(schedule.ToLowerInvariant());
        var notional = (double)trade.Notional;
        return (new RatesLine(id, RatesProduct.Irs, DateOrTenor.On(Date(start)), DateOrTenor.On(Date(trade.Maturity)), direction.Value,
            new LegTerms(trade.Currency, index, notional, new LegQuote(QuoteKind.FixedRatePercent, (double)rate * 100), frequency, null, false),
            new LegTerms(trade.Currency, index, notional, new LegQuote(QuoteKind.FloatSpreadPercent, 0), frequency, frequency, true),
            null, null, false), null);
    }

    /// <summary>The trade as CRIF generation prices it, resolved at the value date with the confirmed conventions, or why it cannot be.</summary>
    public static (PricingLine? Line, string? Problem) Resolve(PricedImTrade trade, DateOnly valueDate, ConventionRegistry conventions)
    {
        var (line, problem) = LineOf(trade);
        if (line is null) return (null, problem);
        var resolution = LineResolver.Resolve(line, valueDate, conventions);
        return resolution.Priceable
            ? (new PricingLine(trade.TradeId, 0, line.Direction, RatesMaterialiser.Unedited(resolution), resolution.ParLeg, false), null)
            : (null, $"{trade.TradeId}: {string.Join(" ", resolution.Issues)}");
    }

    static DateOnly Date(string text) => DateOnly.ParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture);
}

/// <summary>
/// <c>simm.spotIm</c> (ADR-0012 §4's note of 2026-11-08): SIMM's own request over the portfolio's accepted input, which
/// production does not have; there the XVA request's MVA option returns spot SIMM. Each side is priced on its own. Its
/// trades in a spot IM scope become Rates lines, CRIF is generated from them at the value date, and the reference engine
/// runs under the IM CSAs as Price froze them, each holding the side's trades that name it. IM-R and IM-P are then
/// converted from USD to the display currency. Simulated, as the Rates provider and the DEMO table are.
/// </summary>
public sealed class SpotImPrice(IRatesPricingProvider provider, Func<string, MarketSnapshot?> resolveMarket, ConventionRegistry conventions,
    IReadOnlyList<SimmParameters> tables) : ICapabilityHandler
{
    public const string CapabilityId = "simm.spotIm";
    public const string CapabilityVersion = "1.0.0";

    public CapabilityDescriptor Descriptor { get; } = new(CapabilityId, CapabilityVersion, "Spot IM, IM-R and IM-P from CRIF (simulated)",
        [new("acceptedInput", PortfolioContracts.AcceptedInput, PortfolioContracts.Version, Cardinality.One)],
        [new("result", PortfolioContracts.MeasureResult, PortfolioContracts.Version, Cardinality.One),
         new("traces", TraceSet.ContractId, TraceSet.ContractVersion, Cardinality.One),
         new("crifBefore", SimmContracts.Crif, SimmContracts.Version, Cardinality.One),
         new("crifAfter", SimmContracts.Crif, SimmContracts.Version, Cardinality.One)],
        ExecutionKind.RequestResponse, [CapabilityEffect.Compute]);

    const string Before = "before";
    const string After = "after";

    public async Task<IReadOnlyDictionary<string, JsonNode?>> InvokeAsync(CapabilityInvocation invocation, CancellationToken cancellationToken)
    {
        var reference = invocation.Inputs["acceptedInput"].Deserialize<TypedDataRef>(WireJson.Options)
            ?? throw new CapabilityFailure("InvalidInput", "The accepted input is required.");
        var provenance = new DatasetProvenance(ExecutionMode.Simulated, DataOrigin.Synthetic, invocation.InvocationId);
        // One request prices every case (ADR-0010 §3's note of 2026-11-08); the CRIF outputs are the Base's, and each
        // case's CRIF lines are in its trace.
        var rows = new List<JsonObject>();
        var traces = new List<TraceEntry>();
        var labels = new List<string>();
        (CrifDocument Before, CrifDocument After, string Registration)? crifs = null;
        foreach (var @case in PortfolioInput.Cases(invocation.Data.ReadRequired(reference)))
        {
            var priced = await PriceCase(@case, invocation.InvocationId, provenance, cancellationToken);
            rows.AddRange(priced.Rows);
            traces.AddRange(priced.Traces);
            labels.Add(priced.Label);
            crifs ??= (priced.Crifs[Before], priced.Crifs[After], priced.Registration);
        }
        var baseCrifs = crifs ?? throw new CapabilityFailure("InvalidInput", "The accepted input holds no cases.");
        // What the figures were computed with, said once where every case computed with the same.
        var label = string.Join("; ", labels.Distinct());
        return new Dictionary<string, JsonNode?>
        {
            ["result"] = Datasets.Create($"Spot IM ({label})", MeasureResult.Columns, rows, provenance),
            ["traces"] = JsonSerializer.SerializeToNode(new TraceSet(traces), WireJson.Options),
            ["crifBefore"] = SimmDatasets.Crif(baseCrifs.Before, $"CRIF Before {baseCrifs.Registration} ({labels[0]})", provenance),
            ["crifAfter"] = SimmDatasets.Crif(baseCrifs.After, $"CRIF After {baseCrifs.Registration} ({labels[0]})", provenance),
        };
    }

    /// <summary>One case's rows and traces, its CRIF on each side, and what its figures were computed with.</summary>
    async Task<(IReadOnlyList<JsonObject> Rows, IReadOnlyList<TraceEntry> Traces, IReadOnlyDictionary<string, CrifDocument> Crifs, string Label, string Registration)> PriceCase(
        PricedCase @case, string requestId, DatasetProvenance provenance, CancellationToken cancellationToken)
    {
        var input = @case.Input;
        string Required(JsonNode? node, string what) => node?.GetValue<string>() ?? throw new CapabilityFailure("InvalidInput", $"The accepted input names no {what}.");
        var valueDate = DateOnly.ParseExact(Required(input["context"]?["valueDate"], "value date"), "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var registration = Required(input["registration"], "registration");
        var currency = Required(input["displayCurrency"], "display currency");
        var pinned = input["modules"]?[SimmMeasures.Module].Deserialize<SpotImInput>(WireJson.Options)
            ?? throw new CapabilityFailure("InvalidInput", "The accepted input has no part of SIMM's.");
        // Under a scenario every amount converts at its FX (ADR-0010 §4's note of 2026-11-09), the conversion said included.
        var scenario = @case.Market;
        var part = scenario.IsBase ? pinned : pinned with
        {
            Market = pinned.Market with { Fx = pinned.Market.Fx.ToDictionary(p => p.Key, p => p.Value * (double)(scenario.FxFactor("GBP") / scenario.FxFactor(p.Key))) },
        };
        IReadOnlyList<PricedImTrade> trades;
        try
        {
            trades = PricedImTrade.Read(input["trades"]);
        }
        catch (SimmInputException unreadable)
        {
            throw new CapabilityFailure("InvalidInput", unreadable.Message);
        }
        var measures = input["measures"] as JsonArray ?? throw new CapabilityFailure("InvalidInput", "The accepted input names no measures.");
        var selected = measures.Select(m => m as JsonObject ?? throw new CapabilityFailure("InvalidInput", "A measure of the accepted input is not an object."))
            .SingleOrDefault(m => m["id"]?.GetValue<string>() == SimmMeasures.SpotIm);
        var refusedAtPrice = selected?["refused"]?["message"]?.GetValue<string>();

        var definition = SimmMeasures.Definitions.Single();
        var rows = new List<JsonObject>();
        var traces = new List<TraceEntry>();
        var crifs = new Dictionary<string, CrifDocument> { [Before] = new(registration, valueDate, []), [After] = new(registration, valueDate, []) };
        SimmParameters? table = null;
        if (selected is not null && refusedAtPrice is not null)
        {
            var refused = new MeasureResult.Side(MeasureResult.Refused, null, null, refusedAtPrice);
            rows.AddRange(definition.Metrics.Select(m => MeasureResult.Row(@case, SimmMeasures.SpotIm, m, currency, refused, refused)));
        }
        else if (selected is not null)
        {
            var (found, unusable) = TableFor(part.ImCsas, tables);
            if (unusable is not null) throw new CapabilityFailure("UnknownParameterTable", unusable);
            table = found;
            var market = Market(part, valueDate, scenario, @case.Scenario.Title);
            var sides = new Dictionary<string, IReadOnlyDictionary<string, MeasureResult.Side>>();
            foreach (var side in new[] { Before, After })
            {
                var (figures, crif, trace) = await PriceSide(side, trades, part, registration, valueDate, market, table, currency, requestId, provenance, cancellationToken);
                sides[side] = figures;
                if (crif is not null) crifs[side] = crif;
                if (trace is not null) traces.Add(new TraceEntry(@case.Id, @case.Scenario.Id, side, SimmMeasures.SpotIm, trace));
            }
            var conversion = Conversion(part.Market, currency, valueDate);
            rows.AddRange(definition.Metrics.Select(m => MeasureResult.Row(@case, SimmMeasures.SpotIm, m, currency, sides[Before][m.Id], sides[After][m.Id],
                table?.IsDemo == true ? DemoBasis : null, conversion)));
        }

        // What the figures were computed with: no table where no IM CSA named one, so nothing was computed.
        var label = table is null ? "simulated" : table.IsDemo ? $"{DemoBasis}, simulated" : $"SIMM {table.Version}, simulated";
        return (rows, traces, crifs, label, registration);
    }

    /// <summary>
    /// The rate the figures are shown at, said once (ADR-0012 §7): SIMM computes in USD, and a display currency other
    /// than USD takes the pinned FX of the value date. Null where the display currency is USD.
    /// </summary>
    public static string? Conversion(FrozenMarket market, string currency, DateOnly valueDate)
    {
        if (currency == CrifGenerator.CalculationCurrency) return null;
        var rate = RateOf(market, currency);
        return $"Computed in {CrifGenerator.CalculationCurrency}; shown in {currency} at {rate.ToString("G8", CultureInfo.InvariantCulture)} {currency} per "
            + $"{CrifGenerator.CalculationCurrency}, the pinned FX of {valueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.";
    }

    /// <summary>Units of the display currency per USD at the pinned FX, which holds each currency per one base unit.</summary>
    static double RateOf(FrozenMarket market, string currency) =>
        market.Fx.TryGetValue(currency, out var display) && market.Fx.TryGetValue(CrifGenerator.CalculationCurrency, out var usd)
            ? display / usd
            : throw new CapabilityFailure("MissingFx", $"The pinned FX has no {(market.Fx.ContainsKey(currency) ? CrifGenerator.CalculationCurrency : currency)} rate.");

    /// <summary>Said with the figures of the DEMO table (ADR-0012 §7's note of 2026-11-08), which is no calibration of record.</summary>
    public const string DemoBasis = "SIMM PARAMETERS DEMO";

    /// <summary>
    /// The parameter table the IM CSAs name, or why there is none to compute with: they name a table not loaded, or more
    /// than one. IM CSAs that name none (a counterparty without them) need none, and get none. Price's validation and the
    /// request read the IM CSAs through this one rule.
    /// </summary>
    public static (SimmParameters? Table, string? Problem) TableFor(IReadOnlyList<ImCsaTerms> csas, IReadOnlyList<SimmParameters> tables)
    {
        var versions = csas.Select(c => c.SimmVersion).Distinct().ToList();
        if (versions.Count == 0) return (null, null);
        if (versions.Count > 1) return (null, $"The IM CSAs name SIMM versions {string.Join(" and ", versions)}; one Price computes with one.");
        return tables.FirstOrDefault(t => t.Version == versions[0]) is { } table
            ? (table, null)
            : (null, $"No SIMM parameter table {versions[0]} is loaded; loaded: {(tables.Count == 0 ? "none" : string.Join(", ", tables.Select(t => t.Version)))}.");
    }

    /// <summary>
    /// The pinned market: the snapshot's curves, with the portfolio's FX, so one set of rates converts every amount. Under a
    /// scenario (ADR-0010 §4's note of 2026-11-09) each discount curve's shift moves the snapshot's curve of its currency at
    /// every pillar by its key-rate weight there, and the FX is the scenario's, as the part already holds it; CRIF is then
    /// generated again on it. A funding curve's shift moves nothing spot IM reads.
    /// </summary>
    MarketSnapshot Market(SpotImInput part, DateOnly valueDate, ScenarioMarket scenario, string title)
    {
        foreach (var frozen in part.Conventions)
        {
            var now = conventions.Find(frozen.Currency, frozen.Index);
            if (now is null || now.Id != frozen.Id || now.Version != frozen.Version)
                throw new CapabilityFailure("ConventionsMoved",
                    $"Price resolved {frozen.Currency} {frozen.Index} with {frozen.Id} {frozen.Version}, and the conventions now hold {(now is null ? "none" : $"{now.Id} {now.Version}")}.");
        }
        var snapshot = resolveMarket(part.Market.Snapshot)
            ?? throw new CapabilityFailure("UnknownMarket", $"The market snapshot {part.Market.Snapshot} does not resolve.");
        var pinned = snapshot with { Id = $"{snapshot.Id} with the portfolio's FX of {valueDate:yyyy-MM-dd}", Fx = part.Market.Fx };
        if (scenario.IsBase) return pinned;
        var curves = pinned.Curves.ToDictionary(p => p.Key, p =>
        {
            var discount = ScenarioCatalog.Indices.SingleOrDefault(i => i.Kind == ScenarioCatalog.Discount && i.Currency == p.Value.Currency
                && PortfolioRatesLines.OvernightIndex.GetValueOrDefault(p.Value.Currency) == p.Value.Index);
            return discount is null || !scenario.Moves(discount.Id) ? p.Value : p.Value with
            {
                Pillars = [.. p.Value.Pillars.Select(x => (x.Tenor, x.LevelPercent + (double)scenario.RateShiftBp(discount.Id, SyntheticMarket.YearsOf(x.Tenor)) / 100))],
            };
        });
        return pinned with { Id = $"{pinned.Id} under {title}", Curves = curves };
    }

    async Task<(IReadOnlyDictionary<string, MeasureResult.Side> Figures, CrifDocument? Crif, CalculationTrace? Trace)> PriceSide(string side,
        IReadOnlyList<PricedImTrade> trades, SpotImInput part, string registration, DateOnly valueDate, MarketSnapshot market, SimmParameters? table,
        string currency, string requestId, DatasetProvenance provenance, CancellationToken cancellationToken)
    {
        var title = side == Before ? "Before" : "After";
        var onSide = trades.Where(t => side == Before ? t.Before : t.After).ToList();
        IReadOnlyDictionary<string, MeasureResult.Side> All(Func<string, MeasureResult.Side> figure) =>
            SimmMeasures.Definitions.Single().Metrics.ToDictionary(m => m.Id, m => figure(m.Term!));
        int Counted(string direction) => onSide.Count(t => t.In(direction));

        // A side with no trade is not priced (ADR-0010 §3); one whose trades are all out of scope has no IM, which is not 0.
        if (onSide.Count == 0) return (All(_ => MeasureResult.Side.NotPricedWith(0)), null, null);
        // No table is found only where no IM CSA names one: the counterparty exchanges no IM.
        if (table is null)
            return (All(_ => new MeasureResult.Side(MeasureResult.NotPriced, null, 0, SimmMeasures.NoImCsa(registration))), null, null);
        var inScope = onSide.Where(t => t.InScope).ToList();
        // A side none of whose trades is in a scope has no IM in either direction, and nothing was computed: no CRIF, no
        // trace (a trace is a priced side's).
        if (inScope.Count == 0)
            return (All(direction => new MeasureResult.Side(MeasureResult.NotPriced, null, 0,
                $"No trade in {title} is in an {SimmMeasures.Title(direction)} scope.")), null, null);

        var simmTrades = new List<SimmTrade>();
        foreach (var trade in inScope)
        {
            var (line, problem) = PortfolioRatesLines.Resolve(trade, valueDate, conventions);
            var classification = part.Classification.GetValueOrDefault(trade.TradeId);
            if (line is null || classification is null)
                return (All(_ => new MeasureResult.Side(MeasureResult.Unavailable, null, null,
                    $"Spot IM {title} is not shown without {trade.TradeId}: {problem ?? "Price froze no IM classification of it."}")), null, null);
            simmTrades.Add(new(trade.TradeId, line, null, classification.Collect, classification.Post, FormattableString.Invariant($"{trade.Product} {trade.Currency} {trade.Notional:0}")));
        }

        CrifResult crif;
        SimmResult result;
        try
        {
            crif = await new CrifGenerator(provider).GenerateAsync(simmTrades, valueDate, market, cancellationToken);
            // No IM is shown without a trade it should hold (ADR-0012 §4's note): the side is unavailable, naming it.
            if (crif.Excluded.Count > 0)
            {
                var first = crif.Excluded[0];
                var more = crif.Excluded.Count > 1 ? $" ({crif.Excluded.Count - 1} more trade(s) too)" : "";
                return (All(_ => new MeasureResult.Side(MeasureResult.Unavailable, null, null,
                    $"Spot IM {title} is not shown without {first.TradeId}, which gave no CRIF line: {first.Reason}{more}")), null, null);
            }
            var csas = part.ImCsas.Select(c => new ImCsa(c.ImCsaId, c.Direction == ImCsaTerms.Collect ? ImDirection.Collect : ImDirection.Post,
                [.. c.Regimes.Select(r => r.Regime)],
                [.. inScope.Where(t => (c.Direction == ImCsaTerms.Collect ? t.ImR : t.ImP) == c.ImCsaId).Select(t => t.TradeId)])).ToList();
            result = SimmEngine.Calculate(crif.Lines, csas, table);
        }
        catch (SimmInputException refused)
        {
            return (All(_ => new MeasureResult.Side(MeasureResult.Unavailable, null, null, $"Spot IM {title} could not be computed: {refused.Message}")), null, null);
        }

        // SIMM is calculated in USD and shown in the display currency at the pinned FX (ADR-0012 §7), rounded as the
        // other measures' figures are; the trace keeps every figure as computed, in USD.
        var rate = RateOf(part.Market, currency);
        var figures = All(direction =>
        {
            var csa = part.ImCsas.SingleOrDefault(c => c.Direction == direction);
            if (csa is null)
                return new MeasureResult.Side(MeasureResult.NotPriced, null, 0, $"{registration} has no {SimmMeasures.Title(direction)} CSA.");
            var im = result.Direction(direction == ImCsaTerms.Collect ? ImDirection.Collect : ImDirection.Post);
            return im.Im is { } value
                ? new MeasureResult.Side(MeasureResult.Priced, Math.Round((decimal)(value * rate), 2), Counted(direction), null, im.GoverningRegime)
                : new MeasureResult.Side(MeasureResult.NotPriced, null, Counted(direction), $"No trade in {title} is in an {SimmMeasures.Title(direction)} scope.");
        });
        var trace = result.Trace with
        {
            RequestId = requestId, ValuationDate = valueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Subject = new("registration", registration), Provenance = provenance,
        };
        return (figures, new CrifDocument(registration, valueDate, crif.Lines), trace);
    }
}
