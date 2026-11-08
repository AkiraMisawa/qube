using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Workbench.Composition.Tests;

// The pricer page's server over HTTP (ADR-0010): measures register into it, and Price prices the selected ones.
// Every name and figure is synthetic.
public class PricerApiTests(WorkbenchFactory factory) : IClassFixture<WorkbenchFactory>
{
    readonly HttpClient client = factory.CreateClient();

    async Task<JsonObject> Create(string registration = "ABC/LE-01", int size = 30)
    {
        var response = await client.PostAsJsonAsync("/api/xva/workspaces", new { registration, valueDate = "2026-10-26", portfolioSize = size });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!;
    }

    static string Id(JsonObject ws) => ws["workspaceId"]!.GetValue<string>();
    static long Revision(JsonObject ws) => ws["revision"]!.GetValue<long>();
    static string[] Strings(JsonNode? array) => [.. array!.AsArray().Select(n => n!.GetValue<string>())];

    async Task<HttpResponseMessage> Post(JsonObject ws, string path, object body) =>
        await client.PostAsJsonAsync($"/api/xva/workspaces/{Id(ws)}/{path}", body);

    async Task<JsonObject> Measures(JsonObject ws, params string[] measures)
    {
        var response = await Post(ws, "measures", new { revision = Revision(ws), measures });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!;
    }

    /// <summary>Prices the revision, waits for the Run to finish, and reads every output it retained.</summary>
    async Task<(JsonObject Run, IReadOnlyList<JsonObject> Rows)> Price(JsonObject ws)
    {
        var response = await Post(ws, "price", new { revision = Revision(ws), idempotencyKey = Guid.NewGuid().ToString() });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var runId = (await response.Content.ReadFromJsonAsync<JsonObject>())!["runId"]!.GetValue<string>();
        JsonObject run;
        do run = (await client.GetFromJsonAsync<JsonObject>($"/api/runs/{runId}"))!;
        while (run["status"]!.GetValue<string>() == "running");
        Assert.Equal("succeeded", run["status"]!.GetValue<string>());
        var rows = new List<JsonObject>();
        foreach (var (_, reference) in run["outputs"]!.AsObject())
        {
            var data = (await (await client.PostAsJsonAsync("/api/data/read", reference)).Content.ReadFromJsonAsync<JsonObject>())!;
            rows.AddRange(data["rows"]!.AsArray().Select(r => r!.AsObject()));
        }
        return (run, rows);
    }

    [Fact] // ADR-0010 §10, §11: the workspace lists the registered measures, XVA's CVA and FVA, and selects among them
    public async Task A_workspace_lists_the_registered_measures()
    {
        var ws = await Create();

        var measures = ws["measures"]!.AsArray().Select(m => m!.AsObject()).ToList();

        Assert.Equal(["cva", "fva"], measures.Select(m => m["id"]!.GetValue<string>()));
        Assert.All(measures, m => Assert.Equal("xva", m["module"]!.GetValue<string>()));
        Assert.Equal(["cva", "cvaCharge"], measures[0]["metrics"]!.AsArray().Select(m => m!["id"]!.GetValue<string>()));
        Assert.All(Strings(ws["selectedMeasures"]), m => Assert.Contains(m, measures.Select(r => r["id"]!.GetValue<string>())));
    }

    [Fact] // ADR-0010 §10: `measures cva` then Price gives a Run whose figures are CVA's alone
    public async Task Price_prices_only_the_selected_measures()
    {
        var ws = await Measures(await Create(), "cva");
        Assert.Equal(["cva"], Strings(ws["selectedMeasures"]));

        var (run, rows) = await Price(ws);

        // The Run says what it was asked for, so the page reads the measures from the Run, never from the screen.
        Assert.Equal("cva", run["input"]!["measures"]!.GetValue<string>());
        Assert.NotEmpty(rows);
        Assert.All(rows, r => Assert.Equal("cva", r["measure"]!.GetValue<string>()));
        Assert.Equal(["cva", "cvaCharge"], rows.Select(r => r["metric"]!.GetValue<string>()));
        Assert.All(rows, r => Assert.Equal(("priced", "priced"), (r["beforeStatus"]!.GetValue<string>(), r["afterStatus"]!.GetValue<string>())));
        Assert.All(rows, r => Assert.Equal(r["after"]!.GetValue<decimal>() - r["before"]!.GetValue<decimal>(), r["diff"]!.GetValue<decimal>()));
    }

    [Fact] // ADR-0010 §10, spine 1: a measure nobody registered is refused by name, never ignored
    public async Task An_unknown_measure_is_refused()
    {
        var ws = await Create();

        var response = await Post(ws, "measures", new { revision = Revision(ws), measures = new[] { "cva", "kva2" } });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var error = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal("UnknownMeasure", error["code"]!.GetValue<string>());
        Assert.Contains("kva2", error["message"]!.GetValue<string>());
    }

    [Fact] // ADR-0010 §10: a Price with no measure selected is refused rather than run for nothing
    public async Task No_measure_selected_is_refused()
    {
        var ws = await Create();

        var response = await Post(ws, "measures", new { revision = Revision(ws), measures = Array.Empty<string>() });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("NoMeasure", (await response.Content.ReadFromJsonAsync<JsonObject>())!["code"]!.GetValue<string>());
    }

    [Fact] // ADR-0010 §10 (DEFAULT): a new workspace takes its owner's last selection
    public async Task A_new_workspace_takes_the_owners_last_selection()
    {
        await Measures(await Create(), "fva");

        var next = await Create("ABC/LE-02");

        Assert.Equal(["fva"], Strings(next["selectedMeasures"]));
    }

    [Fact] // ADR-0010 §6: XVA's columns reach the New grid over HTTP, a Margin is an edit, and `pv01 retry` asks again
    public async Task Xvas_trade_columns_are_read_and_edited_over_http()
    {
        var ws = await Create();
        Assert.Equal(["xva.pv01", "xva.margin", "xva.pvAdj"], ws["tradeColumns"]!.AsArray().Select(c => c!["field"]!.GetValue<string>()));

        var edited = await Post(ws, "edits", new { revision = Revision(ws), entity = "trade:DRAFT-1", field = "xva.margin", value = 2 });
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        var rows = await (await Post(ws, "rows", new { start = 0, count = 10, sorts = Array.Empty<object>(), grid = "new" })).Content.ReadFromJsonAsync<JsonObject>();
        var cells = rows!["rows"]!.AsArray().Single(r => r!["tradeId"]!.GetValue<string>() == "DRAFT-1")!["cells"]!;
        Assert.Equal(2m, cells["xva.margin"]!["value"]!.GetValue<decimal>());
        Assert.False(cells["xva.pvAdj"]!["editable"]!.GetValue<bool>());

        var retried = await Post(ws, "retry", new { });
        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        Assert.Equal(0, (await retried.Content.ReadFromJsonAsync<JsonObject>())!["asked"]!.GetValue<int>());
    }
}
