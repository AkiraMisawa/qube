using System.Text.Json.Nodes;
using Qube.Samples.Rates;
using Qube.Samples.Rates.Alpha;

namespace Qube.Composition.Tests;

// The alpha provider over a channel the test answers (alpha docs/spec/rates-pricer.md §7;
// docs/reports/rates-live-30-users.html §6: the market and the lines are sent once, a tick sends ids).
public class AlphaRatesPricingTests
{
    static readonly IBusinessCalendar Calendar = new WeekendsOnlyCalendar();
    static readonly DateOnly ValuationDate = new(2026, 11, 17);

    sealed class ScriptedChannel(Func<JsonObject, JsonObject> answer) : IAlphaChannel
    {
        public List<JsonObject> Sent { get; } = [];
        public Task<JsonObject> SendAsync(JsonObject request, CancellationToken cancellationToken)
        {
            lock (Sent) Sent.Add((JsonObject)request.DeepClone());
            return Task.FromResult(answer(request));
        }
        public string[] Ops() { lock (Sent) return [.. Sent.Select(r => (string)r["op"]!)]; }
    }

    static readonly AlphaCurveMap Map = new(
        new Dictionary<string, string> { ["JPY/TONA"] = "JPY/TONA" },
        new Dictionary<string, string> { ["JPY@JPY"] = "JPY/TONA" });

    static MarketSnapshot Market(long n) => SyntheticMarket.Build($"m:{n}", n, new DateTimeOffset(2026, 11, 17, 9, 0, 0, TimeSpan.Zero), "synthetic", 1, "t",
        [new SyntheticCurve("JPY", "TONA", [(Tenor.Parse("1y"), 0.5), (Tenor.Parse("10y"), 1.0)])],
        new Dictionary<string, double> { ["USD"] = 1, ["JPY"] = 150 });

    static ConventionProfile Profile(string index, bool overnight) =>
        new($"t.{index}", "1", "JPY", index, DayCountBasis.Act360, Calendar.Id, BusinessDayAdjustment.ModifiedFollowing, 2, 0, StubRule.ShortFront, false, overnight);

    static PricingLine Line(string key = "L1", string? collateral = "JPY")
    {
        EffectiveLeg Leg(string index, bool floating, double rate) => new(new LegTerms("JPY", index, 1e9, null, Tenor.Parse("12m"), null, floating),
            Profile(index, floating), [new("p0", ValuationDate, ValuationDate.AddYears(1), ValuationDate.AddYears(1), 1e9, rate, 1.0, [])], []);
        return new(key, 1, Direction.Pay, [Leg("FIXED", false, 1.0), Leg("TONA", true, 0)], 1, true, collateral);
    }

    static JsonObject Ok(JsonObject request) => (string)request["op"]! switch
    {
        "price" => new JsonObject
        {
            ["ok"] = true,
            ["rows"] = new JsonArray(new JsonObject
            {
                ["id"] = "L1", ["pv"] = 1234.5, ["par"] = 0.0081,
                ["zero"] = new JsonObject { ["JPY/TONA:10y"] = -10.0 },
                ["parDelta"] = new JsonObject { ["JPY/TONA:10y"] = -11.0 },
            }),
        },
        _ => new JsonObject { ["ok"] = true },
    };

    static AlphaRatesPricing Provider(IAlphaChannel channel) => new(channel, Map, _ => Calendar);

    static RatesBatchInput Batch(long market, params PricingLine[] lines) => new(1, ValuationDate, Market(market), lines);

    [Fact]
    public async Task A_market_is_prepared_once_and_lines_are_sent_once_then_a_tick_sends_ids()
    {
        var channel = new ScriptedChannel(Ok);
        var provider = Provider(channel);

        await provider.PrepareMarketAsync(Market(1), CancellationToken.None);
        await provider.PrepareMarketAsync(Market(1), CancellationToken.None);
        await provider.PriceBatchAsync(Batch(1, Line()), CancellationToken.None);
        await provider.PriceBatchAsync(Batch(2, Line()), CancellationToken.None);

        Assert.Equal(["market", "lines", "price", "market", "price"], channel.Ops());
        // The market goes as delivered (owner, 2026-11-17): curves with their nodes, quotes and Jacobians; nothing to build.
        var curve = channel.Sent[0]["curves"]!.AsArray().Single()!;
        Assert.Equal(("JPY/TONA", 365, 3650), ((string)curve["name"]!, (int)curve["nodes"]![0]!["days"]!, (int)curve["nodes"]![1]!["days"]!));
        Assert.Equal(Math.Exp(-0.005), (double)curve["nodes"]![0]!["df"]!, 15);
        Assert.Equal(2, channel.Sent[0]["quotes"]!["JPY/TONA"]!.AsArray().Count);
        Assert.Equal(("JPY/TONA", "JPY/TONA", 2), ((string)channel.Sent[0]["jacobians"]![0]!["quotes"]!, (string)channel.Sent[0]["jacobians"]![0]!["curve"]!,
            channel.Sent[0]["jacobians"]![0]!["rows"]!.AsArray().Count));
        Assert.IsType<JsonValue>(channel.Sent[2]["lines"], exactMatch: false);   // an id, not the tables
        Assert.Equal(channel.Sent[1]["key"]!.ToString(), channel.Sent[4]["lines"]!.ToString());   // "id" is the channel's own
    }

    [Fact] // a restarted worker forgets: the provider sends the same content again, never another value
    public async Task Lines_and_markets_the_worker_does_not_hold_are_sent_again_and_the_price_retried()
    {
        var forgotten = true;
        var channel = new ScriptedChannel(r =>
        {
            if ((string)r["op"]! == "price" && forgotten) { forgotten = false; return new JsonObject { ["ok"] = false, ["error"] = "unknown-lines" }; }
            return Ok(r);
        });
        var provider = Provider(channel);

        var result = await provider.PriceBatchAsync(Batch(1, Line()), CancellationToken.None);

        Assert.Equal(["market", "lines", "price", "market", "lines", "price"], channel.Ops());
        Assert.Null(result.Rows.Single().Failure);
    }

    [Fact]
    public async Task Results_become_measures_with_par_and_zero_delta_by_quote_tenor()
    {
        var provider = Provider(new ScriptedChannel(Ok));
        var row = (await provider.PriceBatchAsync(Batch(1, Line()), CancellationToken.None)).Rows.Single();

        Assert.Equal(new Measure(1234.5, "JPY"), row.Measures[Measures.Pv]);
        Assert.Equal(("%/leg1", 0.81), (row.Measures[Measures.ParRate].Unit, Math.Round(row.Measures[Measures.ParRate].Value, 12)));
        Assert.Equal(new Measure(-11.0, "JPY/bp"), row.Measures[Measures.Delta("JPY/TONA", "10y")]);
        Assert.Equal(new Measure(-10.0, "JPY/bp"), row.Measures[Measures.Zero("JPY/TONA", "10y")]);
        Assert.Equal(("alpha-rates", "real"), (provider.Name, AlphaRatesPricing.Mode));
    }

    [Fact] // CLAUDE.md: a line the engine cannot take is refused on its row, and the others are still priced
    public async Task A_line_refused_by_the_mapping_or_by_the_engine_fails_on_its_row_only()
    {
        var channel = new ScriptedChannel(r => (string)r["op"]! == "price"
            ? new JsonObject { ["ok"] = true, ["rows"] = new JsonArray(new JsonObject { ["id"] = "L1", ["refused"] = "no fixing for JPY-RFR on 2026-11-16" }) }
            : Ok(r));
        var result = await Provider(channel).PriceBatchAsync(Batch(1, Line("L1"), Line("L2", collateral: "CHF")), CancellationToken.None);

        Assert.Equal("no fixing for JPY-RFR on 2026-11-16", result.Rows.Single(r => r.RowKey == "L1").Failure);
        Assert.Contains("collateralised in CHF", result.Rows.Single(r => r.RowKey == "L2").Failure);
        Assert.Single(channel.Sent.Single(s => (string)s["op"]! == "lines")["lines"]!.AsArray());
    }

    [Fact] // docs/07 §9.7: an unreachable engine is a failure, never a switch to another provider
    public async Task A_channel_failure_is_not_answered_with_values()
    {
        var provider = Provider(new ScriptedChannel(_ => throw new IOException("worker exited")));
        await Assert.ThrowsAsync<IOException>(() => provider.PriceBatchAsync(Batch(1, Line()), CancellationToken.None));
    }

    [Fact]
    public void The_risk_dataset_carries_zero_deltas_beside_par_deltas_so_a_run_keeps_both_the_page_switches_between()
    {
        var line = Line();
        var result = new RatesBatchResult("alpha-rates", AlphaRatesPricing.Mode, [new(line.RowKey, new Dictionary<string, Measure>
        {
            [Measures.Pv] = new(1, "JPY"),
            ["delta:JPY/TONA:10y"] = new(-2, "JPY/bp"),
            ["zero:JPY/TONA:10y"] = new(-3, "JPY/bp"),
        }, null)]);

        var (_, risk) = RatesResults.Datasets(result, [line], [], new MarketUpdate(1, "m:1", DateTimeOffset.UnixEpoch, "synthetic"), "inv");

        var measures = risk["rows"]!.AsArray().Select(r => (string)r!["measure"]!).ToList();
        Assert.Equal(["delta:JPY/TONA:10y", "zero:JPY/TONA:10y"], measures);
    }
}
