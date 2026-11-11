using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Qube.Composition.Tests;

// The pricer page's server over HTTP (ADR-0010): measures register into it, and Price prices the selected ones.
// Every name and figure is synthetic.
public class PricerApiTests(QubeFactory factory) : IClassFixture<QubeFactory>
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

    [Fact] // docs/03 §5, ADR-0010 §3's note of 2026-11-08: add trades over HTTP makes the set, and Price refuses a set the ticked rows left
    public async Task Add_trades_makes_the_set_and_price_refuses_one_the_ticked_rows_left()
    {
        var ws = await Measures(await Create(), "cva");

        var added = await Post(ws, "add-trades", new { revision = Revision(ws) });
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        var answer = (await added.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal(["add DRAFT-1", "add DRAFT-2", "add DRAFT-3"], Strings(answer["made"]));
        ws = answer["workspace"]!.AsObject();
        Assert.Equal([("add", "DRAFT-1"), ("add", "DRAFT-2"), ("add", "DRAFT-3")],
            ws["cases"]!.AsArray().Skip(1).Select(c => (c!["kind"]!.GetValue<string>(), c["trade"]!.GetValue<string>())));
        var (_, rows) = await Price(ws);
        Assert.Equal(4, rows.Count(r => r["metric"]!.GetValue<string>() == "cva"));
        var unticked = await Post(ws, "edits", new { revision = Revision(ws), entity = "trade:DRAFT-2", field = "after", value = false });
        ws = (await unticked.Content.ReadFromJsonAsync<JsonObject>())!;

        var refused = await Post(ws, "price", new { revision = Revision(ws), idempotencyKey = Guid.NewGuid().ToString() });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        var error = (await refused.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal(("AdditionSetChanged", "add trades prices DRAFT-1, DRAFT-2, DRAFT-3 alone, and DRAFT-2 is in a case and no longer ticked. Run add trades again to make the set anew."),
            (error["code"]!.GetValue<string>(), error["message"]!.GetValue<string>()));
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
        // A body with no values is refused as it arrives, before anything reads it.
        var none = await Post(ws, "vary", new { revision = Revision(ws), cell = "CSA-A threshold" });
        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
    }

    [Fact] // ADR-0010 §8's note of 2026-11-09: the CDS catalogue the Credit tab searches, and `cds` over HTTP, which a missing CDS refuses CVA by
    public async Task The_cds_is_listed_and_chosen_over_http()
    {
        var catalogue = (await client.GetFromJsonAsync<JsonObject>("/api/xva/cds"))!;
        Assert.Equal(["6m", "1y", "2y", "3y", "5y", "7y", "10y"], Strings(catalogue["tenors"]));
        Assert.Contains("ABCH | USD | SNRFOR | XR14 | ABC Holdings (synthetic)", catalogue["singleNames"]!.AsArray().Select(c => c!["reference"]!.GetValue<string>()));
        var ws = await Measures(await Create(), "cva", "fva");
        Assert.Equal("singleName", ws["credit"]!["choice"]!["type"]!.GetValue<string>());

        var none = await Post(ws, "cds", new { revision = Revision(ws), cds = "none" });

        Assert.Equal(HttpStatusCode.OK, none.StatusCode);
        var chosen = (await none.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.StartsWith("No CDS for ABC/LE-01", chosen["said"]!.GetValue<string>());
        ws = chosen["workspace"]!.AsObject();
        Assert.Null(ws["credit"]!["cds"]);
        var price = await Post(ws, "price", new { revision = Revision(ws), idempotencyKey = Guid.NewGuid().ToString() });
        Assert.Equal(HttpStatusCode.Accepted, price.StatusCode);
        var proxy = await Post(ws, "cds", new { revision = Revision(ws), cds = "proxy industrials asia bbb" });
        Assert.Equal(HttpStatusCode.OK, proxy.StatusCode);
        ws = (await proxy.Content.ReadFromJsonAsync<JsonObject>())!["workspace"]!.AsObject();
        Assert.Equal("PROXY.INDUSTRIALS.ASIA.BBB", ws["credit"]!["cds"]!["curveId"]!.GetValue<string>());
        var unknown = await Post(ws, "cds", new { revision = Revision(ws), cds = "nobody" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.StatusCode);
    }

    [Fact] // ADR-0010 §8, §10, ADR-0016 §3: a module's settings over HTTP: listed while a measure they serve is selected, set by `entity.set` through the page's door, and checked
    public async Task A_modules_settings_are_listed_and_set_over_http()
    {
        var ws = await Measures(await Create(), "kva");
        var xva = Assert.Single(ws["settings"]!.AsArray())!;
        Assert.Equal(["costOfCapital", "pvAdjustment"], xva["settings"]!.AsArray().Select(s => s!["id"]!.GetValue<string>()));
        Assert.Equal("rate", xva["settings"]![0]!["form"]!.GetValue<string>());

        var set = await client.PostAsJsonAsync("/api/commands/entity.set/1.0.0", new
        {
            inputs = new { request = new { workspaceId = Id(ws), revision = Revision(ws), rows = "xva", set = "cost of capital=12%" } },
            idempotencyKey = Guid.NewGuid().ToString(),
        });

        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        Assert.Equal("Set xva cost of capital to 12%.", (await set.Content.ReadFromJsonAsync<JsonObject>())!["outputs"]!["answer"]!["said"]!.GetValue<string>());
        ws = (await client.GetFromJsonAsync<JsonObject>($"/api/xva/workspaces/{Id(ws)}"))!;
        var coc = ws["settings"]![0]!["settings"]![0]!;
        Assert.Equal(("12%", "10%", true), (coc["text"]!.GetValue<string>(), coc["defaultText"]!.GetValue<string>(), coc["changed"]!.GetValue<bool>()));
        var refused = await Post(ws, "edits", new { revision = Revision(ws), entity = "settings:xva", field = "costOfCapital", value = 1.5 });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
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
        // CVA and FVA register one Risks tab between them, and with KVA one Profile tab (the owner's Risks and Profile of 2026-11-08).
        Assert.Equal([("risks", "Risks", "risks"), ("profile", "Profile", "profile")], tabs["cva"]);
        Assert.Equal([("risks", "Risks", "risks"), ("profile", "Profile", "profile")], tabs["fva"]);
        Assert.Equal([("profile", "Profile", "profile")], tabs["kva"]);
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

    [Fact] // ADR-0010 §4 and its note of 2026-11-09: a scenario over HTTP, a row per pair in its Run, a pair's trace, and the quote of a scenario
    public async Task A_scenario_is_defined_and_priced_with_a_row_per_pair_over_http()
    {
        var ws = await Measures(await Create(), "cva", "ccr.spotCapital");

        var defined = await Post(ws, "scenarios", new { revision = Revision(ws), name = "up", shifts = "usd_sofr +100bp; usdjpy -5%" });
        Assert.Equal(HttpStatusCode.OK, defined.StatusCode);
        var made = (await defined.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal(["up"], Strings(made["made"]));
        Assert.Empty(Strings(made["replaced"]));
        ws = made["workspace"]!.AsObject();
        Assert.Equal(("up", 2, true), (ws["scenarios"]![0]!["name"]!.GetValue<string>(), ws["scenarios"]![0]!["shifts"]!.GetValue<int>(), ws["scenarios"]![0]!["included"]!.GetValue<bool>()));
        Assert.Equal(["usd_sofr", "usdjpy"], ws["shifts"]!.AsArray().Select(r => r!["index"]!.GetValue<string>()));
        var refused = await Post(ws, "scenarios", new { revision = Revision(ws), name = "s2", shifts = "jpy_ois +10%" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Contains("an IR shift is absolute, in bp", (await refused.Content.ReadFromJsonAsync<JsonObject>())!["message"]!.GetValue<string>());

        var (run, rows) = await Price(ws);
        var runId = run["runId"]!.GetValue<string>();

        // The Run names its markets by position, each with its title.
        Assert.Equal([("base", "Base market"), ("s1", "up")], rows.Where(r => r["metric"]!.GetValue<string>() == "cva")
            .Select(r => (r["scenario"]!.GetValue<string>(), r["scenarioTitle"]!.GetValue<string>())));
        var trace = await client.GetAsync($"/api/runs/{runId}/cases/base/traces/ccr.spotCapital?scenario=s1");
        Assert.Equal(HttpStatusCode.OK, trace.StatusCode);
        var unpriced = await client.GetAsync($"/api/runs/{runId}/cases/base/traces/ccr.spotCapital?scenario=s2");
        Assert.Equal(HttpStatusCode.NotFound, unpriced.StatusCode);
        Assert.Contains("scenario s2", (await unpriced.Content.ReadFromJsonAsync<JsonObject>())!["message"]!.GetValue<string>());
        var quote = (await client.GetFromJsonAsync<JsonObject>($"/api/xva/runs/{runId}/quote?scenario=s1"))!;
        Assert.Contains("Scenario up", quote["text"]!.GetValue<string>());
        Assert.Equal("up", quote["scenario"]!.GetValue<string>());
        Assert.DoesNotContain("Scenario", (await client.GetFromJsonAsync<JsonObject>($"/api/xva/runs/{runId}/quote"))!["text"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/xva/runs/{runId}/quote?scenario=s9")).StatusCode);
    }

    [Fact] // ADR-0010 §4: the Scenarios tab over HTTP: a block pasted, a cell refused by row and column, include, duplicate, remove and clear
    public async Task The_scenarios_tab_edits_and_lists_over_http()
    {
        var ws = await Create();
        async Task<JsonObject> Ok(string path, object body)
        {
            var response = await Post(ws, path, body);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var answer = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
            return answer["workspace"]?.AsObject() ?? answer;
        }
        static object Row(int row, string scenario, string index, string size) => new { id = (string?)null, row, cells = new Dictionary<string, string?> { ["scenario"] = scenario, ["index"] = index, ["size"] = size } };

        ws = await Ok("shifts", new { revision = Revision(ws), rows = new[] { Row(1, "up", "jpy_ois", "+10bp"), Row(2, "up", "usdjpy", "-5%") } });
        Assert.Equal([("IR", "abs", "all"), ("FX", "rel", "spot")], ws["shifts"]!.AsArray().Select(r => (r!["category"]!.GetValue<string>(), r["type"]!.GetValue<string>(), r["factor"]!.GetValue<string>())));
        var refused = await Post(ws, "shifts", new { revision = Revision(ws), rows = new[] { Row(3, "up", "jpy_ojs", "+1bp") } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.StartsWith("Row 3, Index: jpy_ojs is no index", (await refused.Content.ReadFromJsonAsync<JsonObject>())!["message"]!.GetValue<string>());

        ws = await Ok("scenarios/duplicate", new { revision = Revision(ws), name = "up", copy = "up2" });
        ws = await Ok("scenarios/include", new { revision = Revision(ws), name = "up", included = false });
        Assert.Equal([("up", false), ("up2", true)], ws["scenarios"]!.AsArray().Select(s => (s!["name"]!.GetValue<string>(), s["included"]!.GetValue<bool>())));
        ws = await Ok("scenarios/remove", new { revision = Revision(ws), name = "up2" });
        Assert.Equal(["up"], ws["scenarios"]!.AsArray().Select(s => s!["name"]!.GetValue<string>()));
        ws = await Ok("scenarios/clear", new { revision = Revision(ws) });
        Assert.Empty(ws["scenarios"]!.AsArray());
    }

    [Fact] // ADR-0010 §4, C32: a set is saved, listed for the user and loaded into another workspace over HTTP
    public async Task A_scenario_set_is_saved_listed_and_loaded_over_http()
    {
        var ws = await Create();
        var defined = await Post(ws, "scenarios", new { revision = Revision(ws), name = "up", shifts = "jpy_ois +10bp" });
        ws = (await defined.Content.ReadFromJsonAsync<JsonObject>())!["workspace"]!.AsObject();

        var saved = await Post(ws, "scenarios/save", new { revision = Revision(ws), set = "http-set", share = "all" });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var said = (await saved.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal(("http-set", "all", 1), (said["set"]!.GetValue<string>(), said["visibility"]!.GetValue<string>(), said["scenarios"]!.GetValue<int>()));
        var desk = await Post(ws, "scenarios/save", new { revision = Revision(ws), set = "http-set", share = "desk" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, desk.StatusCode);

        var sets = (await client.GetFromJsonAsync<JsonArray>("/api/xva/scenario-sets"))!;
        Assert.Contains(sets, s => s!["label"]!.GetValue<string>() == "http-set" && s["owner"]!.GetValue<string>() == "akira");
        var other = await Create();
        var loaded = await Post(other, "scenarios/load", new { revision = Revision(other), set = "http-set" });
        Assert.Equal(HttpStatusCode.OK, loaded.StatusCode);
        var answer = (await loaded.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal(["up"], answer["made"]!.AsArray().Select(m => m!.GetValue<string>()));
        Assert.Equal(["up"], answer["workspace"]!["scenarios"]!.AsArray().Select(s => s!["name"]!.GetValue<string>()));
        var unknown = await Post(other, "scenarios/load", new { revision = Revision(answer["workspace"]!.AsObject()), set = "no-such-set" });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }
}
