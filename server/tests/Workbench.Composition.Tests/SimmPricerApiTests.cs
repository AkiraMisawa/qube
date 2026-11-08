using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Workbench.Composition.Contracts;

namespace Workbench.Composition.Tests;

/// <summary>
/// Spot IM on the pricer page over HTTP (ADR-0012 §2, §3, §7 with their notes of 2026-11-08): the measure, its inputs
/// and the IM CSAs in the workspace view, an IM-R edit, a Price, and the trace opened at the direction a Metrics cell
/// names. A class of its own, so the owner's last selection, which a new workspace takes, is this class's alone. Every
/// name and figure is synthetic.
/// </summary>
public class SimmPricerApiTests(WorkbenchFactory factory) : IClassFixture<WorkbenchFactory>
{
    readonly HttpClient client = factory.CreateClient();

    static string Text(JsonNode? node) => node!.GetValue<string>();
    static long Revision(JsonObject ws) => ws["revision"]!.GetValue<long>();

    async Task<JsonObject> Ok(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!;
    }

    Task<HttpResponseMessage> Post(JsonObject ws, string path, object body) =>
        client.PostAsJsonAsync($"/api/xva/workspaces/{Text(ws["workspaceId"])}/{path}", body);

    async Task<JsonObject> Read(JsonNode reference) => await Ok(await client.PostAsJsonAsync("/api/data/read", reference));

    [Fact] // ADR-0012 §2, §3, §7's notes: spot IM is selected, edited, priced and traced to its direction over HTTP
    public async Task Spot_im_is_priced_over_http_and_its_trace_opens_at_collect()
    {
        var ws = await Ok(await client.PostAsJsonAsync("/api/xva/workspaces", new { registration = "ABC/LE-01", valueDate = "2026-10-26", portfolioSize = 30 }));
        ws = await Ok(await Post(ws, "measures", new { revision = Revision(ws), measures = new[] { "simm.spotIm" } }));

        // The measure with its metrics and the cell each opens, its inputs, and the IM CSAs the IM tab shows.
        var measure = ws["measures"]!.AsArray().Single(m => Text(m!["id"]) == "simm.spotIm")!;
        Assert.Equal([("imR", "collect"), ("imP", "post")], measure["metrics"]!.AsArray().Select(m => (Text(m!["id"]), Text(m["term"]))));
        Assert.Equal([("trades", "imR"), ("trades", "imP"), ("im", "imCsas")], ws["inputs"]!.AsArray().Select(i => (Text(i!["tab"]), Text(i["field"]))));
        var csas = ws["agreement"]!["imCsas"]!.AsArray();
        Assert.Equal([("IMR-ABC-01", "collect", "USD"), ("IMP-ABC-01", "post", "USD")],
            csas.Select(c => (Text(c!["imCsaId"]), Text(c["direction"]), Text(c["calculationCurrency"]))));
        Assert.Equal(["JFSA", "CFTC"], csas[0]!["regimes"]!.AsArray().Select(r => Text(r!["regime"])));

        // An existing trade in IM-R's scope taken out of it by emptying its IM-R: an assignment edit, marked against Original.
        var rows = (await Ok(await Post(ws, "rows", new { start = 0, count = 100, sorts = Array.Empty<object>() })))["rows"]!.AsArray();
        var trade = rows.First(t => !t!["isNew"]!.GetValue<bool>() && t["membership"]!["simm.spotIm"]!["in"]!.GetValue<bool>()
            && t["membership"]!["simm.spotIm"]!["scopes"]!.AsArray().Any(s => s!["id"]!.GetValue<string>().StartsWith("collect/", StringComparison.Ordinal) && s["in"]!.GetValue<bool>()))!;
        var tradeId = Text(trade["tradeId"]);
        ws = await Ok(await Post(ws, "edits", new { revision = Revision(ws), entity = $"trade:{tradeId}", field = "imR", value = (string?)null }));
        rows = (await Ok(await Post(ws, "rows", new { start = 0, count = 100, sorts = Array.Empty<object>() })))["rows"]!.AsArray();
        var edited = rows.Single(t => Text(t!["tradeId"]) == tradeId)!;
        Assert.Null(edited["imR"]);
        Assert.Equal("user", Text(edited["imROwner"]));
        Assert.All(edited["membership"]!["simm.spotIm"]!["scopes"]!.AsArray().Where(s => Text(s!["id"]).StartsWith("collect/", StringComparison.Ordinal)),
            s => Assert.False(s!["in"]!.GetValue<bool>()));

        // Price: spot IM alone, IM-R and IM-P on both sides, each named with its governing regime and labelled DEMO.
        var accepted = await Ok(await Post(ws, "price", new { revision = Revision(ws), idempotencyKey = Guid.NewGuid().ToString() }));
        var runId = Text(accepted["runId"]);
        JsonObject run;
        do run = (await client.GetFromJsonAsync<JsonObject>($"/api/runs/{runId}"))!;
        while (Text(run["status"]) == "running");
        Assert.Equal("succeeded", Text(run["status"]));
        var result = (await Read(run["outputs"]!["simm"]!))["rows"]!.AsArray().Select(r => r!.AsObject()).ToList();
        Assert.Equal(["imR", "imP"], result.Select(r => Text(r["metric"])));
        Assert.All(result, r =>
        {
            Assert.Equal(("priced", "priced", "SIMM PARAMETERS DEMO"), (Text(r["beforeStatus"]), Text(r["afterStatus"]), Text(r["basis"])));
            Assert.NotNull(r["beforeNote"]);
            Assert.NotNull(r["afterNote"]);
        });

        // The trace opens at collect, the term IM-R's cell names, on both sides; the figure is its USD amount at the frozen FX.
        var reference = run["workflow"]!["steps"]![0]!["bindings"]!["acceptedInput"]!["reference"].Deserialize<TypedDataRef>(WireJson.Options)!;
        var input = await Read(JsonSerializer.SerializeToNode(reference, WireJson.Options)!);
        var fx = input["modules"]!["simm"]!["market"]!["fx"]!;
        var currency = Text(input["displayCurrency"]);
        var rate = fx[currency]!.GetValue<double>() / fx["USD"]!.GetValue<double>();
        var opened = await Ok(await client.GetAsync($"/api/runs/{runId}/cases/base/traces/simm.spotIm?path=collect"));
        Assert.Equal(["before", "after"], opened["trace"]!["sides"]!.AsArray().Select(Text));
        var collect = opened["term"]!["term"]!;
        Assert.Equal("collect", Text(collect["id"]));
        var imR = result.Single(r => Text(r["metric"]) == "imR");
        foreach (var side in new[] { "before", "after" })
        {
            Assert.Equal("matches", Text(collect[side]!["check"]!["status"]));
            Assert.Equal(Math.Round((decimal)(collect[side]!["value"]!.GetValue<double>() * rate), 2), imR[side]!.GetValue<decimal>());
        }
    }
}
