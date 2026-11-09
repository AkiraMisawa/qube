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

    [Fact] // ADR-0010 §3, §9: vary over HTTP makes its cases, by a cell's name or by its entity and field, and Price gives a row per case
    public async Task Vary_makes_cases_and_price_gives_a_row_per_case()
    {
        var ws = await Measures(await Create(), "cva");

        var named = await Post(ws, "vary", new { revision = Revision(ws), cell = "CSA-A threshold", values = new[] { "10mm", "0" } });
        Assert.Equal(HttpStatusCode.OK, named.StatusCode);
        var varied = (await named.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal(["CSA-A threshold 10mm"], Strings(varied["made"]));
        Assert.Equal(["0 is the workspace's value"], Strings(varied["skipped"]));
        ws = varied["workspace"]!.AsObject();
        var byField = await Post(ws, "vary", new { revision = Revision(ws), entity = "netting:NS-001", field = "cvaRecovery", values = new[] { "25%" } });
        Assert.Equal(HttpStatusCode.OK, byField.StatusCode);
        ws = (await byField.Content.ReadFromJsonAsync<JsonObject>())!["workspace"]!.AsObject();
        var cases = ws["cases"]!.AsArray().Select(c => (Id: c!["id"]!.GetValue<string>(), Title: c["title"]!.GetValue<string>())).ToList();
        Assert.Equal(["Base", "CSA-A threshold 10mm", "NS-001 recovery 25%"], cases.Select(c => c.Title));
        Assert.Equal(50, ws["caseCap"]!.GetValue<int>());

        var (_, rows) = await Price(ws);

        Assert.Equal(cases.Select(c => (c.Id, c.Title)), rows.Where(r => r["metric"]!.GetValue<string>() == "cva")
            .Select(r => (r["case"]!.GetValue<string>(), r["caseTitle"]!.GetValue<string>())));
        // A lower recovery is a larger loss: more negative CVA in the case.
        decimal After(string @case) => rows.Single(r => r["metric"]!.GetValue<string>() == "cva" && r["case"]!.GetValue<string>() == @case)["after"]!.GetValue<decimal>();
        Assert.True(After(cases[2].Id) < After("base"));
    }

    [Fact] // ADR-0010 §3's note of 2026-11-08: the Changes list, a case's change set, and cases removed, over HTTP
    public async Task The_changes_list_edits_and_removes_cases_over_http()
    {
        var ws = await Create();
        var varied = await Post(ws, "vary", new { revision = Revision(ws), cell = "CSA-A threshold", values = new[] { "5mm", "10mm" } });
        ws = (await varied.Content.ReadFromJsonAsync<JsonObject>())!["workspace"]!.AsObject();
        var first = ws["cases"]![1]!["id"]!.GetValue<string>();

        var set = await Post(ws, "changes", new { revision = Revision(ws), entity = "csa:CSA-A", field = "counterpartyThreshold", text = "7.5mm", @case = first });
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        ws = (await set.Content.ReadFromJsonAsync<JsonObject>())!;
        var changes = (await client.GetFromJsonAsync<JsonObject>($"/api/xva/workspaces/{Id(ws)}/changes"))!;
        Assert.Equal(2, changes["count"]!.GetValue<int>());
        Assert.Equal(["7.5mm", "10mm"], changes["cases"]!.AsArray().Select(c => c!["changes"]![0]!["text"]!.GetValue<string>()));
        Assert.Equal(first, changes["cases"]![0]!["changes"]![0]!["case"]!.GetValue<string>());

        var removed = await Post(ws, "cases/remove", new { revision = Revision(ws), name = "CSA-A threshold 10mm" });
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        ws = (await removed.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal(["Base", "CSA-A threshold 5mm"], ws["cases"]!.AsArray().Select(c => c!["title"]!.GetValue<string>()));
        var cleared = await Post(ws, "cases/clear", new { revision = Revision(ws) });
        Assert.Equal(["Base"], (await cleared.Content.ReadFromJsonAsync<JsonObject>())!["cases"]!.AsArray().Select(c => c!["title"]!.GetValue<string>()));
    }

    [Fact] // ADR-0010 §9's note of 2026-11-08: a cell vary cannot name is refused over HTTP with what it could mean
    public async Task Vary_refuses_a_cell_it_cannot_name()
    {
        var ws = await Create();

        var response = await Post(ws, "vary", new { revision = Revision(ws), cell = "CSA-A", values = new[] { "1mm" } });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var error = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal(("UnknownCell", "Name a cell as its row and its column, such as CSA-A threshold; CSA-A is one word."),
            (error["code"]!.GetValue<string>(), error["message"]!.GetValue<string>()));
    }

    [Fact] // ADR-0010 §10, §11, ADR-0013 §1, ADR-0012 §7's note: the workspace lists the registered measures, and the request that prices each
    public async Task A_workspace_lists_the_registered_measures()
    {
        var ws = await Create();

        var measures = ws["measures"]!.AsArray().Select(m => m!.AsObject()).ToList();

        Assert.Equal(["cva", "fva", "kva", "ccr.spotCapital", "simm.spotIm"], measures.Select(m => m["id"]!.GetValue<string>()));
        Assert.Equal(["xva", "xva", "xva", "ccr", "simm"], measures.Select(m => m["module"]!.GetValue<string>()));
        // Spot capital is SA-CCR's, and XVA's request returns it with KVA on (ADR-0013 §2); spot IM is SIMM's own
        // request in the PoC (ADR-0012 §4's note of 2026-11-08).
        Assert.Equal(["xva", "xva", "xva", "xva", "simm"], measures.Select(m => m["request"]!.GetValue<string>()));
        Assert.Equal(["cva", "cvaCharge", "pv"], measures[0]["metrics"]!.AsArray().Select(m => m!["id"]!.GetValue<string>()));
        Assert.All(Strings(ws["selectedMeasures"]), m => Assert.Contains(m, measures.Select(r => r["id"]!.GetValue<string>())));
    }

    [Fact] // ADR-0010 §7, §10, ADR-0012 §7: a measure registers its result tabs beside Metrics, as spot IM's SIMM and spot capital's CCR, drawn as a trace
    public async Task A_measure_lists_the_result_tabs_it_registers()
    {
        var ws = await Create();

        var tabs = ws["measures"]!.AsArray().ToDictionary(m => m!["id"]!.GetValue<string>(),
            m => m!["tabs"]!.AsArray().Select(t => (t!["id"]!.GetValue<string>(), t["title"]!.GetValue<string>(), t["view"]!.GetValue<string>())).ToList());

        Assert.Equal([("simm", "SIMM", "trace")], tabs["simm.spotIm"]);
        Assert.Equal([("ccr", "CCR", "trace")], tabs["ccr.spotCapital"]);
        // A measure without a tab of its own lists none, never a missing list.
        Assert.All(new[] { "cva", "fva", "kva" }, m => Assert.Empty(tabs[m]));
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
        Assert.Equal(["cva", "cvaCharge", "pv"], rows.Select(r => r["metric"]!.GetValue<string>()));
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

    [Fact] // ADR-0010 §7: Download and Copy quote are a Run's, over HTTP: a zip of the Run, and the quote as text and HTML
    public async Task A_run_downloads_as_a_zip_and_quotes_as_text_and_html()
    {
        // The fixture's workspaces share an owner, whose last selection a new workspace takes: CVA is selected here.
        var ws = await Measures(await Create(), "cva", "fva");
        var (run, _) = await Price(ws);
        var runId = run["runId"]!.GetValue<string>();

        var download = await client.GetAsync($"/api/runs/{runId}/archive");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("application/zip", download.Content.Headers.ContentType!.MediaType);
        Assert.Equal($"{runId}.zip", download.Content.Headers.ContentDisposition!.FileName);
        using var archive = new System.IO.Compression.ZipArchive(await download.Content.ReadAsStreamAsync());
        Assert.NotNull(archive.GetEntry("run.json"));
        Assert.NotNull(archive.GetEntry("manifest.json"));

        var quote = (await client.GetFromJsonAsync<JsonObject>($"/api/xva/runs/{runId}/quote"))!;
        Assert.StartsWith($"XVA quote (simulated) · {runId}", quote["text"]!.GetValue<string>());
        Assert.StartsWith("<p><b>XVA quote (simulated)", quote["html"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/xva/runs/run-none/quote")).StatusCode);
    }
}

