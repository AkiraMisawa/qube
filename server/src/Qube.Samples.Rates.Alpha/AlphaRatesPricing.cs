using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Qube.Samples.Rates;

namespace Qube.Samples.Rates.Alpha;

/// <summary>One request to alpha's resident worker and its answer (alpha docs/spec/rates-pricer.md §7).</summary>
public interface IAlphaChannel
{
    Task<JsonObject> SendAsync(JsonObject request, CancellationToken cancellationToken);
}

/// <summary>
/// Prices Rates lines with alpha (docs/reports/alpha-for-rates.html). The worker keeps curves by market and lines by
/// content, so a market is sent once (ideally when it arrives, <see cref="PrepareMarketAsync"/>), a set of lines once
/// per content, and a tick sends two ids (docs/reports/rates-live-30-users.html §6). A worker that answers it does not
/// hold one is sent the same content again; nothing else ever answers for it.
/// </summary>
public sealed class AlphaRatesPricing(IAlphaChannel channel, AlphaCurveMap map, Func<string, IBusinessCalendar> calendar)
    : IRatesPricingProvider, IRatesMarketPreparation
{
    public const string Mode = "real";
    public string Name => "alpha-rates";
    public IReadOnlyList<PricedProduct> Products { get; } = [new(RatesProducts.Irs, RatesProducts.Version), new(RatesProducts.Ccs, RatesProducts.Version)];

    readonly Lock gate = new();
    readonly HashSet<string> markets = [];
    readonly HashSet<string> lineSets = [];
    // per market key, the delta names the market allows: zero deltas by curve node, par deltas by quote
    readonly Dictionary<string, HashSet<string>> named = [];

    static string MarketKey(MarketSnapshot market, DateOnly valuationDate) => $"{market.Id}@{valuationDate:yyyy-MM-dd}";

    public Task PrepareMarketAsync(MarketSnapshot market, CancellationToken cancellationToken) =>
        EnsureMarket(market, DateOnly.FromDateTime(market.AsOf.UtcDateTime), cancellationToken);

    async Task EnsureMarket(MarketSnapshot market, DateOnly valuationDate, CancellationToken cancellationToken)
    {
        var key = MarketKey(market, valuationDate);
        lock (gate) if (markets.Contains(key)) return;
        Check(await channel.SendAsync(MarketRequest(key, market, valuationDate), cancellationToken), "market");
        lock (gate)
        {
            markets.Add(key);
            named[key] = [
                .. market.Curves.Values.SelectMany(c => c.Nodes.Select(n => Measures.Zero(c.Name, n.Tenor))),
                .. market.Quotes.Values.SelectMany(q => q.Quotes.Select(x => Measures.Delta(q.Name, x.Tenor))),
            ];
        }
    }

    /// <summary>
    /// The market as delivered: curves (nodes in days from the valuation date, discount factors), the quotes and the
    /// Jacobians, and FX. The worker reads them and builds nothing (owner, 2026-11-17: curves and Jacobians arrive
    /// together every second).
    /// </summary>
    static JsonObject MarketRequest(string key, MarketSnapshot market, DateOnly valuationDate)
    {
        var curves = new JsonArray();
        foreach (var c in market.Curves.Values.OrderBy(c => c.Name, StringComparer.Ordinal))
            curves.Add(new JsonObject
            {
                ["name"] = c.Name, ["currency"] = c.Currency, ["index"] = c.Index, ["role"] = c.Role, ["composition"] = c.Composition,
                ["interpolation"] = c.Interpolation, ["indexMonths"] = c.IndexMonths, ["numerator"] = c.Numerator, ["denominator"] = c.Denominator,
                ["nodes"] = new JsonArray([.. c.Nodes.Select(n => (JsonNode)new JsonObject { ["tenor"] = n.Tenor, ["days"] = n.Days, ["df"] = n.DiscountFactor })]),
            });
        var quotes = new JsonObject();
        foreach (var q in market.Quotes.Values.OrderBy(q => q.Name, StringComparer.Ordinal))
            quotes[q.Name] = new JsonArray([.. q.Quotes.Select(x => (JsonNode)new JsonObject { ["tenor"] = x.Tenor, ["days"] = x.Days, ["rate"] = x.RatePercent / 100 })]);
        var jacobians = new JsonArray();
        foreach (var j in market.Jacobians)
            jacobians.Add(new JsonObject
            {
                ["quotes"] = j.QuoteSet, ["curve"] = j.Curve,
                ["rows"] = new JsonArray([.. j.Rows.Select(r => (JsonNode)new JsonArray([.. r.Select(v => (JsonNode)JsonValue.Create(v))]))]),
            });
        var fx = new JsonObject();
        foreach (var (ccy, rate) in market.Fx) fx[ccy] = rate;
        return new JsonObject
        {
            ["op"] = "market", ["market"] = key, ["valuationDate"] = valuationDate.ToString("yyyy-MM-dd"), ["origin"] = market.Origin,
            ["curves"] = curves, ["quotes"] = quotes, ["jacobians"] = jacobians, ["fx"] = fx,
        };
    }

    public async Task<RatesBatchResult> PriceBatchAsync(RatesBatchInput input, CancellationToken cancellationToken)
    {
        var refused = new List<RowResult>();
        var tables = new List<CashflowTable>();
        var lines = new Dictionary<string, PricingLine>();
        foreach (var line in input.Lines)
        {
            var (table, refusals) = CashflowTables.From(line, map, input.Market, input.ValuationDate, calendar);
            if (table is null) refused.Add(new RowResult(line.RowKey, new Dictionary<string, Measure>(), string.Join(" ", refusals)));
            else { tables.Add(table); lines[line.RowKey] = line; }
        }
        var rows = new List<RowResult>(refused);
        if (tables.Count > 0)
        {
            var linesJson = JsonSerializer.SerializeToNode(tables, Json)!.AsArray();
            var linesId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(linesJson.ToJsonString())))[..32];
            var key = MarketKey(input.Market, input.ValuationDate);
            JsonObject answer = new();
            for (var attempt = 0; attempt < 2; attempt++)
            {
                await EnsureMarket(input.Market, input.ValuationDate, cancellationToken);
                bool known;
                lock (gate) known = lineSets.Contains(linesId);
                if (!known)
                {
                    Check(await channel.SendAsync(new JsonObject { ["op"] = "lines", ["id"] = linesId, ["lines"] = linesJson.DeepClone() }, cancellationToken), "lines");
                    lock (gate) lineSets.Add(linesId);
                }
                answer = await channel.SendAsync(new JsonObject { ["op"] = "price", ["market"] = key, ["lines"] = linesId }, cancellationToken);
                if (answer["ok"]?.GetValue<bool>() == true) break;
                var error = (string?)answer["error"] ?? "";
                if (attempt == 0 && error is "unknown-lines" or "unknown-market")
                {
                    lock (gate) { markets.Remove(key); lineSets.Remove(linesId); }
                    continue;
                }
                throw new InvalidOperationException($"The engine refused the batch: {error}");
            }
            HashSet<string> allowed;
            lock (gate) allowed = named[key];
            foreach (var r in answer["rows"]!.AsArray())
                rows.Add(Row(r!.AsObject(), lines, allowed));
        }
        var order = input.Lines.Select((l, i) => (l.RowKey, i)).ToDictionary(x => x.RowKey, x => x.i);
        return new RatesBatchResult(Name, Mode, [.. rows.OrderBy(r => order[r.RowKey])]);
    }

    static RowResult Row(JsonObject r, Dictionary<string, PricingLine> lines, HashSet<string> allowed)
    {
        var id = (string)r["id"]!;
        if ((string?)r["refused"] is { } reason) return new RowResult(id, new Dictionary<string, Measure>(), reason);
        var line = lines[id];
        var reporting = line.Legs[0].Terms.Currency;
        var measures = new Dictionary<string, Measure> { [Measures.Pv] = new((double)r["pv"]!, reporting) };
        if (line.ParLeg is { } p && r["par"] is { } par)
            measures[line.Legs[p - 1].Terms.Floating ? Measures.FairSpread : Measures.ParRate] = new((double)par * 100, $"%/leg{p}");
        if (line.Risk)
        {
            // The engine names a delta by the market's own names: "{curve}:{node tenor}" or "{quote set}:{quote tenor}".
            void Deltas(string field, Func<string, string, string> name)
            {
                if (r[field] is not JsonObject deltas) return;
                foreach (var (k, v) in deltas)
                {
                    var cut = k.LastIndexOf(':');
                    var measure = cut < 0 ? k : name(k[..cut], k[(cut + 1)..]);
                    if (!allowed.Contains(measure))
                        throw new InvalidOperationException($"The engine answered a delta on {k}, which this market does not name.");
                    measures[measure] = new((double)v!, $"{reporting}/bp");
                }
            }
            Deltas("parDelta", Measures.Delta);
            Deltas("zero", Measures.Zero);
        }
        return new RowResult(id, measures, null);
    }

    static void Check(JsonObject answer, string op)
    {
        if (answer["ok"]?.GetValue<bool>() != true) throw new InvalidOperationException($"The engine refused '{op}': {(string?)answer["error"]}");
    }

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
}
