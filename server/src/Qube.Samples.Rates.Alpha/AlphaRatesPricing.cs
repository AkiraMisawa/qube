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

    /// <summary>The measure name of a zero-rate delta (par delta keeps <see cref="Measures.Delta"/>, the page's default).</summary>
    public static string ZeroDelta(string curve, Tenor pillar) => $"zero:{curve}:{pillar}";

    readonly Lock gate = new();
    readonly HashSet<string> markets = [];
    readonly HashSet<string> lineSets = [];
    // engine curve name -> days -> (snapshot curve key, tenor), per market key
    readonly Dictionary<string, Dictionary<string, Dictionary<int, (string Key, Tenor Tenor)>>> nodes = [];

    static string MarketKey(MarketSnapshot market, DateOnly valuationDate) => $"{market.Id}@{valuationDate:yyyy-MM-dd}";

    public Task PrepareMarketAsync(MarketSnapshot market, CancellationToken cancellationToken) =>
        EnsureMarket(market, DateOnly.FromDateTime(market.AsOf.UtcDateTime), cancellationToken);

    async Task EnsureMarket(MarketSnapshot market, DateOnly valuationDate, CancellationToken cancellationToken)
    {
        var key = MarketKey(market, valuationDate);
        lock (gate) if (markets.Contains(key)) return;
        var (request, curveNodes) = MarketRequest(key, market, valuationDate);
        Check(await channel.SendAsync(request, cancellationToken), "market");
        lock (gate)
        {
            markets.Add(key);
            nodes[key] = curveNodes;
        }
    }

    (JsonObject, Dictionary<string, Dictionary<int, (string, Tenor)>>) MarketRequest(string key, MarketSnapshot market, DateOnly valuationDate)
    {
        var curves = new JsonArray();
        var quotes = new JsonObject();
        var curveNodes = new Dictionary<string, Dictionary<int, (string, Tenor)>>();
        foreach (var c in map.Curves)
        {
            if (!market.Curves.TryGetValue(c.Quotes, out var q)) continue;  // a curve without quotes is absent; lines on it are refused by the engine
            curves.Add(new JsonObject
            {
                ["name"] = c.Name, ["currency"] = c.Currency, ["role"] = c.Role, ["indexMonths"] = c.IndexMonths,
                ["base"] = c.Base, ["interpolation"] = c.Interpolation,
            });
            var points = new JsonArray();
            var byDays = new Dictionary<int, (string, Tenor)>();
            foreach (var (tenor, level) in q.Pillars.OrderBy(p => p.Tenor.AddTo(valuationDate)))
            {
                var days = tenor.AddTo(valuationDate).DayNumber - valuationDate.DayNumber;
                points.Add(new JsonObject { ["days"] = days, ["rate"] = level / 100 });
                byDays[days] = (c.Quotes, tenor);
            }
            quotes[c.Name] = points;
            curveNodes[c.Name] = byDays;
        }
        var fx = new JsonObject();
        foreach (var (ccy, rate) in market.Fx) fx[ccy] = rate;
        return (new JsonObject
        {
            ["op"] = "market", ["market"] = key, ["valuationDate"] = valuationDate.ToString("yyyy-MM-dd"), ["origin"] = market.Origin,
            ["curves"] = curves, ["quotes"] = quotes, ["fx"] = fx,
        }, curveNodes);
    }

    public async Task<RatesBatchResult> PriceBatchAsync(RatesBatchInput input, CancellationToken cancellationToken)
    {
        var refused = new List<RowResult>();
        var tables = new List<CashflowTable>();
        var lines = new Dictionary<string, PricingLine>();
        foreach (var line in input.Lines)
        {
            var (table, refusals) = CashflowTables.From(line, map, input.ValuationDate, calendar);
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
            Dictionary<string, Dictionary<int, (string Key, Tenor Tenor)>> curveNodes;
            lock (gate) curveNodes = nodes[key];
            foreach (var r in answer["rows"]!.AsArray())
                rows.Add(Row(r!.AsObject(), lines, curveNodes));
        }
        var order = input.Lines.Select((l, i) => (l.RowKey, i)).ToDictionary(x => x.RowKey, x => x.i);
        return new RatesBatchResult(Name, Mode, [.. rows.OrderBy(r => order[r.RowKey])]);
    }

    static RowResult Row(JsonObject r, Dictionary<string, PricingLine> lines, Dictionary<string, Dictionary<int, (string Key, Tenor Tenor)>> curveNodes)
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
            void Deltas(string field, Func<string, Tenor, string> name)
            {
                if (r[field] is not JsonObject deltas) return;
                foreach (var (k, v) in deltas)
                {
                    var cut = k.LastIndexOf(':');
                    var (curve, days) = (k[..cut], int.Parse(k[(cut + 1)..]));
                    if (!curveNodes.TryGetValue(curve, out var byDays) || !byDays.TryGetValue(days, out var node))
                        throw new InvalidOperationException($"The engine answered a delta on {k}, which no quote of this market names.");
                    measures[name(node.Key, node.Tenor)] = new((double)v!, $"{reporting}/bp");
                }
            }
            Deltas("parDelta", Measures.Delta);
            Deltas("zero", ZeroDelta);
        }
        return new RowResult(id, measures, null);
    }

    static void Check(JsonObject answer, string op)
    {
        if (answer["ok"]?.GetValue<bool>() != true) throw new InvalidOperationException($"The engine refused '{op}': {(string?)answer["error"]}");
    }

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
}
