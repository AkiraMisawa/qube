using System.Text.Json;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Qube.Samples.Rates;

namespace Qube.Composition.Tests;

// The Rates line pricer over HTTP (docs/07, docs/10): commands carry their revision; Price names its snapshot. A line
// added, made or priced, the defaults and the live subscription's start and stop go through the command gateway, as
// the page sends them (ADR-0016 note 36); a typed cell is the page's own edit.
public class RatesApiTests : IClassFixture<QubeFactory>
{
    readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> host;
    readonly HttpClient client;

    public RatesApiTests(QubeFactory factory)
    {
        host = factory.WithWebHostBuilder(b => b.UseSetting("Rates:Ticker", "false"));
        client = host.CreateClient();
    }

    async Task<JsonObject> Ok(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!;
    }

    // A command through the page's door of the gateway, as the page's own door sends it.
    Task<HttpResponseMessage> Command(string capability, JsonObject request, HttpClient? by = null, string port = "request") =>
        (by ?? client).PostAsJsonAsync($"/api/commands/{capability}/1.0.0", new { inputs = new JsonObject { [port] = request }, idempotencyKey = Guid.NewGuid().ToString() });

    // What a command answered: its one output.
    async Task<JsonObject> Done(HttpResponseMessage response) => Assert.Single((await Ok(response))["outputs"]!.AsObject()).Value!.AsObject();

    Task<HttpResponseMessage> New(string id, long revision, string product, string terms, HttpClient? by = null) =>
        Command("entity.create", new JsonObject { ["workspaceId"] = id, ["revision"] = revision, ["product"] = product, ["terms"] = terms }, by);

    async Task<JsonObject> View(string id, HttpClient? by = null) => await Ok(await (by ?? client).GetAsync($"/api/rates/workspaces/{id}"));

    async Task<(string Id, JsonObject View, string Line)> PricedLine()
    {
        var ws = await Ok(await client.PostAsJsonAsync("/api/rates/workspaces", new { valuationDate = "2026-10-27" }));
        var id = ws["workspaceId"]!.GetValue<string>();
        var added = await Done(await Command("rates.line.add", new JsonObject { ["workspaceId"] = id, ["revision"] = ws["revision"]!.GetValue<long>(), ["product"] = "irs" }));
        var line = added["line"]!.GetValue<string>();
        var view = await View(id);
        foreach (var (field, value) in new (string, object?)[]
        {
            ("end", "5y"), ("leg1.currency", "JPY"), ("leg1.index", "TONA"), ("leg1.notional", 1_000_000_000), ("leg1.paymentFrequency", "12m"),
            ("leg2.currency", "JPY"), ("leg2.index", "TONA"), ("leg2.notional", 1_000_000_000), ("leg2.paymentFrequency", "12m"),
            ("leg2.quoteKind", "FloatSpreadPercent"), ("leg2.quote", 0),
        })
            view = await Ok(await client.PostAsJsonAsync($"/api/rates/workspaces/{id}/lines/{line}/edits",
                new { revision = view["revision"]!.GetValue<long>(), field, value }));
        return (id, view, line);
    }

    [Fact] // docs/07 §3: a line typed through the API prices once against the snapshot the request names
    public async Task A_line_is_priced_against_the_named_snapshot()
    {
        var (id, view, line) = await PricedLine();
        var snapshot = await host.Services.GetRequiredService<MarketFeed>().AdvanceAsync();
        Assert.True(view["lines"]!.AsArray().Single(l => l!["line"]!.GetValue<string>() == line)!["priceable"]!.GetValue<bool>());

        var answer = await Done(await Command("rates.workspace.price",
            new JsonObject { ["workspaceId"] = id, ["revision"] = view["revision"]!.GetValue<long>(), ["marketSnapshotId"] = snapshot.Id }));

        var run = await Ok(await client.GetAsync($"/api/runs/{answer["runId"]!.GetValue<string>()}"));
        Assert.Equal(snapshot.Id, answer["marketSnapshotId"]!.GetValue<string>());
        Assert.Equal(snapshot.Id, run["input"]!["marketSnapshotId"]!.GetValue<string>());
    }

    [Fact] // Spine 6: an edit against a moved revision is refused, whatever the timing
    public async Task An_edit_against_a_moved_revision_is_refused()
    {
        var (id, view, line) = await PricedLine();

        var response = await client.PostAsJsonAsync($"/api/rates/workspaces/{id}/lines/{line}/edits",
            new { revision = view["revision"]!.GetValue<long>() - 1, field = "end", value = "7y" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact] // docs/07 §6: frames reach the client as server-sent events, each naming its revision and snapshot
    public async Task Live_frames_stream_as_server_sent_events()
    {
        var (id, view, _) = await PricedLine();
        var started = await Done(await Command("rates.live.start", new JsonObject { ["workspaceId"] = id }, port: "workspace"));
        var subscription = started["subscriptionId"]!.GetValue<string>();
        // The frame waits in the subscription's channel until the stream reads it, so the order cannot race.
        var snapshot = await host.Services.GetRequiredService<MarketFeed>().AdvanceAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var stream = await client.GetStreamAsync($"/api/rates/live/{subscription}/frames", timeout.Token);
        using var reader = new StreamReader(stream);
        string? data = null;
        while (data is null && await reader.ReadLineAsync(timeout.Token) is { } text)
            if (text.StartsWith("data: ", StringComparison.Ordinal)) data = text["data: ".Length..];

        var frame = JsonNode.Parse(data!)!;
        Assert.Equal(view["revision"]!.GetValue<long>(), frame["inputRevision"]!.GetValue<long>());
        Assert.Equal(snapshot.Id, frame["market"]!["snapshotId"]!.GetValue<string>());
        Assert.Equal("simulated", frame["calculationMode"]!.GetValue<string>());
        var stopped = await Done(await Command("rates.live.stop", new JsonObject { ["workspaceId"] = id }, port: "workspace"));
        Assert.Equal((subscription, false), (stopped["subscriptionId"]!.GetValue<string>(), (await View(id))["live"]!["running"]!.GetValue<bool>()));
    }

    async Task<(string Id, long Revision)> Workspace()
    {
        var ws = await Ok(await client.PostAsJsonAsync("/api/rates/workspaces", new { valuationDate = "2026-10-27" }));
        return (ws["workspaceId"]!.GetValue<string>(), ws["revision"]!.GetValue<long>());
    }

    // The line a command made, as the workspace then shows it.
    async Task<JsonObject> LineOf(JsonObject made, HttpClient? by = null) =>
        (await View(made["workspaceId"]!.GetValue<string>(), by))["lines"]!.AsArray().Single(l => l!["line"]!.GetValue<string>() == made["entity"]!.GetValue<string>())!.AsObject();

    [Fact] // ADR-0008 §1, §3: `new irs` reads its words by form in any order; ATM by C47; the profile's template is recorded as such
    public async Task A_new_irs_is_read_from_short_words_and_records_its_defaults()
    {
        var (id, revision) = await Workspace();

        var added = await Done(await New(id, revision, "rates.irs", "jpy 10y 3bn rec"));

        var line = await LineOf(added);
        Assert.True(line["priceable"]!.GetValue<bool>(), line.ToJsonString());
        Assert.Equal(3e9, line["fields"]!["leg2.notional"]!.GetValue<double>());
        Assert.Equal("TONA", line["fields"]!["leg2.index"]!.GetValue<string>());
        var defaulted = line["defaulted"]!.AsObject();
        Assert.StartsWith("profile:proposed.jpy.tona@", defaulted["leg1.index"]!.GetValue<string>());
        Assert.StartsWith("profile:proposed.jpy.tona@", defaulted["leg1.paymentFrequency"]!.GetValue<string>());
        Assert.Equal("C47", defaulted["leg2.quote"]!.GetValue<string>());
        Assert.False(defaulted.ContainsKey("leg1.notional"));
    }

    [Fact] // ADR-0008 §1, §4c: a term no default supplies is refused by name, and nothing is written
    public async Task Missing_terms_are_refused_by_name()
    {
        var (id, revision) = await Workspace();

        var response = await New(id, revision, "rates.irs", "10y");

        Assert.Equal((HttpStatusCode)422, response.StatusCode);
        var paths = (await response.Content.ReadFromJsonAsync<JsonObject>())!["issues"]!.AsArray().Select(i => i!["path"]!.GetValue<string>()).ToList();
        // The frequencies come from the currency's profile, so with no currency they are missing too.
        Assert.Equal(new[] { "currency", "index", "notional", "direction", "fixedFrequency", "floatFrequency" }.Select(term => $"/inputs/request/terms/{term}"), paths);
        var view = await View(id);
        Assert.Equal(revision, view["revision"]!.GetValue<long>());
        Assert.Empty(view["lines"]!.AsArray());
    }

    [Fact] // ADR-0008 §4b: the workspace's own defaults fill what the user left out, and say they did
    public async Task The_workspace_defaults_fill_a_new_irs()
    {
        var (id, revision) = await Workspace();
        // The defaults are fields of the workspace's own table, set by the kernel's `set` as the line reads it.
        var read = await Ok(await client.PostAsJsonAsync("/api/commands/preview",
            new { line = "set default currency=usd default notional=500 default direction=pay", workspaceId = id, revision }));
        var set = await client.PostAsJsonAsync($"/api/commands/{read["command"]!["id"]!.GetValue<string>()}/{read["command"]!["version"]!.GetValue<string>()}",
            new { inputs = read["inputs"], idempotencyKey = Guid.NewGuid().ToString() });
        var view = await View(id);
        Assert.True(set.IsSuccessStatusCode, await set.Content.ReadAsStringAsync());
        Assert.Equal(("USD", 500e6), (view["defaults"]!["currency"]!.GetValue<string>(), view["defaults"]!["notional"]!.GetValue<double>()));

        var added = await Done(await New(id, view["revision"]!.GetValue<long>(), "rates.irs", "5y"));

        var line = await LineOf(added);
        Assert.True(line["priceable"]!.GetValue<bool>(), line.ToJsonString());
        Assert.Equal("SOFR", line["fields"]!["leg1.index"]!.GetValue<string>());
        Assert.Equal("workspace", line["defaulted"]!["leg1.notional"]!.GetValue<string>());
        Assert.Equal("workspace", line["defaulted"]!["direction"]!.GetValue<string>());
    }

    [Fact] // ADR-0008 §7: a CCS with no basis spread reads, but the economic check still refuses it; nothing defaults one
    public async Task A_new_ccs_without_a_spread_is_refused_by_its_economic_check()
    {
        var (id, revision) = await Workspace();

        var response = await New(id, revision, "rates.ccs", "5y usd jpy 100 15bn pay");

        Assert.Equal((HttpStatusCode)422, response.StatusCode);
        Assert.Contains("par", (await response.Content.ReadFromJsonAsync<JsonObject>())!["message"]!.GetValue<string>());
    }

    [Fact] // ADR-0008 §2: a CCS's currencies and notionals are sequences, leg 1 then leg 2
    public async Task A_new_ccs_takes_its_legs_in_order()
    {
        var (id, revision) = await Workspace();

        var added = await Done(await New(id, revision, "rates.ccs", "5y usd jpy 100 15bn pay spread2=-30bp"));

        var fields = (await LineOf(added))["fields"]!;
        Assert.Equal(("USD", "JPY"), (fields["leg1.currency"]!.GetValue<string>(), fields["leg2.currency"]!.GetValue<string>()));
        Assert.Equal((100e6, 15e9), (fields["leg1.notional"]!.GetValue<double>(), fields["leg2.notional"]!.GetValue<double>()));
        Assert.Equal(("SOFR", "TONA"), (fields["leg1.index"]!.GetValue<string>(), fields["leg2.index"]!.GetValue<string>()));
    }

    [Fact] // ADR-0008 §2 (owner, 2026-11-04): a full amount typed where millions are read is refused, not made a quadrillion
    public async Task A_full_amount_is_refused()
    {
        var (id, revision) = await Workspace();

        var response = await New(id, revision, "rates.irs", "jpy 10y 3000000000 pay");

        Assert.Equal((HttpStatusCode)422, response.StatusCode);
        Assert.Contains((await response.Content.ReadFromJsonAsync<JsonObject>())!["issues"]!.AsArray(),
            i => i!["path"]!.GetValue<string>() == "/inputs/request/terms/notional");
    }

    /// <summary>The fake's figures, but it declares that it prices IRS only (ADR-0008 §8).</summary>
    sealed class IrsOnly : IRatesPricingProvider
    {
        readonly FakeRatesPricing fake = new();
        public string Name => "irs-only";
        public IReadOnlyList<PricedProduct> Products { get; } = [new(RatesProducts.Irs, RatesProducts.Version)];
        public Task<RatesBatchResult> PriceBatchAsync(RatesBatchInput input, CancellationToken cancellationToken) =>
            input.Lines.Any(l => l.Legs.Select(g => g.Terms.Currency).Distinct().Count() > 1)
                ? throw new InvalidOperationException("A CCS line reached a provider that does not price it.")
                : fake.PriceBatchAsync(input, cancellationToken);
    }

    [Fact] // ADR-0008 §8: a line whose product no provider prices is listed as not priced with that reason, never sent elsewhere
    public async Task A_product_no_provider_prices_is_not_priced_and_says_why()
    {
        using var irsOnly = host.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IRatesPricingProvider, IrsOnly>()));
        var http = irsOnly.CreateClient();
        var ws = await Ok(await http.PostAsJsonAsync("/api/rates/workspaces", new { valuationDate = "2026-10-27" }));
        var id = ws["workspaceId"]!.GetValue<string>();
        var irs = await Done(await New(id, ws["revision"]!.GetValue<long>(), "rates.irs", "jpy 10y 3bn rec", http));
        var ccs = await Done(await New(id, irs["revision"]!.GetValue<long>(), "rates.ccs", "5y usd jpy 100 15bn pay spread2=-30bp", http));
        var snapshot = await irsOnly.Services.GetRequiredService<MarketFeed>().AdvanceAsync();

        var priced = await Done(await Command("rates.workspace.price",
            new JsonObject { ["workspaceId"] = id, ["revision"] = ccs["revision"]!.GetValue<long>(), ["marketSnapshotId"] = snapshot.Id }, http));

        var run = await Ok(await http.GetAsync($"/api/runs/{priced["runId"]!.GetValue<string>()}"));
        var reference = run["workflow"]!["steps"]![0]!["bindings"]!["acceptedInput"]!["reference"].Deserialize<Qube.Composition.Contracts.TypedDataRef>(Qube.Composition.Contracts.WireJson.Options)!;
        var accepted = Assert.IsType<Qube.Composition.Application.DataLookup.Found>(irsOnly.Services.GetRequiredService<Qube.Composition.Application.IDataStore>().Read(reference));
        var notPriced = accepted.Content["notPriced"]!.AsArray();
        var left = Assert.Single(notPriced)!;
        Assert.Equal(ccs["entity"]!.GetValue<string>(), left["line"]!.GetValue<string>());
        Assert.Contains("No pricing provider prices rates.ccs 1.0.0; irs-only prices rates.irs 1.0.0.", left["reasons"]!.AsArray().Select(r => r!.GetValue<string>()));
    }
}
