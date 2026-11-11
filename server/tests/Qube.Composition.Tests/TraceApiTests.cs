using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Qube.Composition.Application.Traces;

namespace Qube.Composition.Tests;

// Reading spot capital's trace over HTTP and through trace.read (ADR-0011 §5, §7, ADR-0013 §2): a Price with KVA
// retains the trace, and the page and the AI read it a term at a time. Every name and figure is synthetic.
public class TraceApiTests(QubeFactory factory) : IClassFixture<QubeFactory>
{
    readonly HttpClient client = factory.CreateClient();

    const string Measure = "ccr.spotCapital";

    async Task<JsonObject> Workspace(bool before = true)
    {
        var created = await client.PostAsJsonAsync("/api/xva/workspaces", new { registration = "ABC/LE-01", valueDate = "2026-10-26", portfolioSize = 12 });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var ws = (await created.Content.ReadFromJsonAsync<JsonObject>())!;
        // The measures are chosen as the page chooses them, by the command `measures` (ADR-0016 note 28).
        var request = new JsonObject { ["workspaceId"] = Id(ws), ["revision"] = Revision(ws), ["measures"] = $"kva {Measure}" };
        var chosen = await client.PostAsJsonAsync("/api/commands/xva.measures/1.0.0", new { inputs = new JsonObject { ["request"] = request }, idempotencyKey = Guid.NewGuid().ToString() });
        Assert.True(chosen.IsSuccessStatusCode, await chosen.Content.ReadAsStringAsync());
        ws = (await client.GetFromJsonAsync<JsonObject>($"/api/xva/workspaces/{Id(ws)}"))!;
        return before ? ws : await Post(ws, "edits", new { revision = Revision(ws), entity = "grid:existing", field = "before", value = false });
    }

    static string Id(JsonObject ws) => ws["workspaceId"]!.GetValue<string>();
    static long Revision(JsonObject ws) => ws["revision"]!.GetValue<long>();

    async Task<JsonObject> Post(JsonObject ws, string path, object body)
    {
        var response = await client.PostAsJsonAsync($"/api/xva/workspaces/{Id(ws)}/{path}", body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!;
    }

    /// <summary>Prices the workspace and waits for its Run to finish.</summary>
    async Task<string> Price(JsonObject ws)
    {
        var response = await client.PostAsJsonAsync($"/api/xva/workspaces/{Id(ws)}/price", new { revision = Revision(ws), idempotencyKey = Guid.NewGuid().ToString() });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var runId = (await response.Content.ReadFromJsonAsync<JsonObject>())!["runId"]!.GetValue<string>();
        JsonObject run;
        do run = (await client.GetFromJsonAsync<JsonObject>($"/api/runs/{runId}"))!;
        while (run["status"]!.GetValue<string>() == "running");
        Assert.Equal("succeeded", run["status"]!.GetValue<string>());
        return runId;
    }

    async Task<(HttpStatusCode Status, JsonObject Body)> Get(string url)
    {
        var response = await client.GetAsync(url);
        return (response.StatusCode, (await response.Content.ReadFromJsonAsync<JsonObject>())!);
    }

    async Task<JsonObject> Term(string runId, string? path = null, string query = "")
    {
        var (status, body) = await Get($"/api/runs/{runId}/cases/base/traces/{Measure}?scenario=base{(path is null ? "" : "&path=" + Uri.EscapeDataString(path))}{query}");
        Assert.True(status == HttpStatusCode.OK, $"{status}: {body}");
        return body;
    }

    static string[] Ids(JsonNode? terms) => [.. terms!.AsArray().Select(t => t!["id"]!.GetValue<string>())];

    [Fact] // ADR-0011 §5, ADR-0013 §2: a Price with KVA retains spot capital's trace, read a term at a time on both sides
    public async Task Spot_capitals_trace_is_read_a_term_at_a_time_on_both_sides()
    {
        var runId = await Price(await Workspace());

        var root = await Term(runId);

        var trace = root["trace"]!;
        Assert.Equal((runId, "base", "base", Measure), (trace["runId"]!.GetValue<string>(), trace["case"]!.GetValue<string>(),
            trace["scenario"]!.GetValue<string>(), trace["measure"]!.GetValue<string>()));
        Assert.Equal(["before", "after"], trace["sides"]!.AsArray().Select(s => s!.GetValue<string>()));
        Assert.Equal(("sa-ccr", "2023-01-01", "trade"), (trace["method"]!["id"]!.GetValue<string>(), trace["method"]!["version"]!.GetValue<string>(), trace["depth"]!.GetValue<string>()));
        // The provider says it simulated, on every side, and what the figure is of.
        Assert.All(trace["origins"]!.AsArray(), o => Assert.Equal("simulated", o!["provenance"]!["executionMode"]!.GetValue<string>()));
        Assert.All(trace["origins"]!.AsArray(), o => Assert.Equal("ABC/LE-01", o!["subject"]!["id"]!.GetValue<string>()));
        Assert.Equal(["capital"], Ids(trace["roots"]));

        // With no path, the first root opens: capital on both sides, its Diff, its child and the parameters in its scope.
        var term = root["term"]!;
        Assert.Equal("capital", term["term"]!["id"]!.GetValue<string>());
        var before = term["term"]!["before"]!["value"]!.GetValue<double>();
        var after = term["term"]!["after"]!["value"]!.GetValue<double>();
        Assert.Equal(after - before, term["term"]!["diff"]!["value"]!.GetValue<double>(), 6);
        Assert.Equal("matches", term["term"]!["after"]!["check"]!["status"]!.GetValue<string>());
        Assert.Equal(["rwa"], Ids(term["children"]));
        Assert.Equal("rwa", term["term"]!["slots"]!["rwa"]!.GetValue<string>());
        Assert.Contains("kappa", term["afterParameters"]!.AsArray().Select(p => p!["name"]!.GetValue<string>()));
        Assert.Equal(["capital"], Ids(term["path"]));
    }

    [Fact] // ADR-0011 §5, §6: a term opens by its path, its children ordered by the change, and a leaf set pages and sorts on the server
    public async Task A_term_opens_by_path_and_its_leaf_rows_page_and_sort()
    {
        var runId = await Price(await Workspace());

        var ead = (await Term(runId, "ead"))["term"]!;
        Assert.Equal(["capital", "rwa", "ead"], Ids(ead["path"]));
        var sets = Ids(ead["children"]);
        Assert.NotEmpty(sets);
        var bySize = (await Term(runId, "ead", "&order=size"))["term"]!["children"]!.AsArray()
            .Select(c => Math.Abs(c!["diff"]!["value"]?.GetValue<double>() ?? -1)).ToList();
        Assert.Equal(bySize.OrderByDescending(d => d), bySize);

        // Walk down the first netting set to a term with leaf rows, as the view does.
        var at = sets[0];
        JsonNode open;
        while (true)
        {
            open = (await Term(runId, at))["term"]!;
            var side = open["term"]!["after"] ?? open["term"]!["before"];
            if (side!["leaves"] is not null) break;
            at = Ids(open["children"]).First();
        }
        var (status, page) = await Get($"/api/runs/{runId}/cases/base/traces/{Measure}/leaves?path={Uri.EscapeDataString(at)}&limit=1");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(at, page["term"]!.GetValue<string>());
        Assert.Single(page["leaves"]!["rows"]!.AsArray());
        var total = page["leaves"]!["total"]!.GetValue<int>();
        Assert.True(total >= 1);

        var (_, sorted) = await Get($"/api/runs/{runId}/cases/base/traces/{Measure}/leaves?path={Uri.EscapeDataString(at)}&sort=tradeId&descending=true");
        var trades = sorted["leaves"]!["rows"]!.AsArray().Select(r => (r!["after"] ?? r["before"])!["tradeId"]!.GetValue<string>()).ToList();
        Assert.Equal(trades.OrderByDescending(t => t, StringComparer.Ordinal), trades);
    }

    [Fact] // ADR-0011 §4 (CONFIRMED): a Price without Before is read on After alone, with no Before and no Diff
    public async Task A_price_without_before_is_read_on_after_alone()
    {
        var runId = await Price(await Workspace(before: false));

        var root = await Term(runId);

        Assert.Equal(["after"], root["trace"]!["sides"]!.AsArray().Select(s => s!.GetValue<string>()));
        Assert.Null(root["term"]!["term"]!["before"]);
        Assert.Null(root["term"]!["term"]!["diff"]);
        Assert.Equal("value", root["term"]!["term"]!["after"]!["state"]!.GetValue<string>());
        Assert.Null(root["term"]!["beforeParameters"]);
    }

    [Fact] // ADR-0011 §5, spine 1: a trace, a term or a Run that is not there is refused by name, never answered empty
    public async Task What_is_not_there_is_refused_by_name()
    {
        var runId = await Price(await Workspace());

        var (status, error) = await Get($"/api/runs/{runId}/cases/base/traces/cva");
        Assert.Equal((HttpStatusCode.NotFound, "UnknownTrace"), (status, error["code"]!.GetValue<string>()));
        Assert.Contains("ccr.spotCapital (base, base, before)", error["message"]!.GetValue<string>());

        (status, error) = await Get($"/api/runs/{runId}/cases/base/traces/{Measure}?path=NS-999");
        Assert.Equal((HttpStatusCode.NotFound, "UnknownTerm"), (status, error["code"]!.GetValue<string>()));

        (status, error) = await Get($"/api/runs/run-none/cases/base/traces/{Measure}");
        Assert.Equal((HttpStatusCode.NotFound, "UnknownRun"), (status, error["code"]!.GetValue<string>()));

        (status, error) = await Get($"/api/runs/{runId}/cases/base/traces/{Measure}?limit=0");
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "InvalidRange"), (status, error["code"]!.GetValue<string>()));

        // Only the Run's owner reads it until roles arrive with sign-in (C30, C31).
        var traces = factory.Services.GetRequiredService<TraceService>();
        var refused = Assert.Throws<TraceRefused>(() => traces.Read(new(runId, "base", "base", Measure), "someone-else", null));
        Assert.Equal("NotOwner", refused.Code);
    }

    [Fact] // ADR-0011 §7: trace.read is a command the AI calls, and answers the term the endpoint answers
    public async Task Trace_read_answers_what_the_endpoint_answers()
    {
        var runId = await Price(await Workspace());

        var response = await client.PostAsJsonAsync("/api/agent/commands/trace.read/1.0", new
        {
            inputs = new { request = new { runId, measure = Measure, path = "rwa" } },
            idempotencyKey = Guid.NewGuid().ToString(),
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var read = (await response.Content.ReadFromJsonAsync<JsonObject>())!["outputs"]!["term"]!;
        var term = (await Term(runId, "rwa"))["term"]!;
        Assert.Equal(term["term"]!.ToJsonString(), read["term"]!["term"]!.ToJsonString());
        Assert.Equal(Ids(term["children"]), Ids(read["term"]!["children"]));

        // It reads for a caller, so it is never a workflow step.
        var catalog = (await client.GetFromJsonAsync<JsonObject>("/api/catalog"))!;
        var effects = catalog["capabilities"]!.AsArray().Single(c => c!["capabilityId"]!.GetValue<string>() == "trace.read")!["effects"]!;
        Assert.Equal(["readRun"], effects.AsArray().Select(e => e!.GetValue<string>()));
    }

    [Fact] // ADR-0011 §5, §6: the catalogue of kinds is read once per method version, with each kind's formula
    public async Task The_catalogue_gives_each_kinds_formula()
    {
        var (status, catalog) = await Get("/api/traces/catalogs/sa-ccr/2023-01-01");

        Assert.Equal(HttpStatusCode.OK, status);
        var capital = catalog["kinds"]!.AsArray().Single(k => k!["kind"]!.GetValue<string>() == "saccr.capital")!;
        Assert.Contains(@"\slot{rwa}", capital["tex"]!.GetValue<string>());
        Assert.Equal("product", capital["expression"]!["op"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/traces/catalogs/sa-ccr/1999-01-01")).StatusCode);
    }
}
