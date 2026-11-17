using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Qube.Composition.Application.Workspaces;
using Qube.Samples.Desk;

namespace Qube.Composition.Tests;

/// <summary>
/// ADR-0019 §2, §5 and §8: the Desk at the command layer, as the line and the AI reach it. The results port is a small
/// set held in memory; the engine's own files are read in <see cref="DeskResultFilesTests"/>.
/// </summary>
public class DeskCommandTests(QubeFactory factory) : IClassFixture<QubeFactory>
{
    /// <summary>Result sets by reference, as a test holds them.</summary>
    sealed class HeldResults : IDeskResults
    {
        public Dictionary<string, DeskResultSet> Sets { get; } = [];

        public DeskResultSet Read(string reference) => Sets.TryGetValue(reference, out var set) ? set
            : throw new DeskResultsRefused(DeskResultsRefusal.NotFound, $"There is no result set '{reference}'.");
    }

    internal static DeskResultSet Tiny(string hash = "1111111111111111111111111111111111111111111111111111111111111111") => new("tiny", hash,
        new DeskManifest("alpha", "abc123", "synthetic/tiny", new DateOnly(2026, 11, 13), "S0000",
            new Dictionary<string, string> { ["C0001"] = "SYN-LE-0001" },
            new Dictionary<string, SensitivityPer> { ["zero_rate"] = SensitivityPer.Rate, ["fx_spot"] = SensitivityPer.Level })
        { HedgeCurves = new Dictionary<string, string> { ["JPY"] = "JPY-OIS" } },
        [new("C0001", "SYN-LE-0001"), new("C0002", null)],
        [
            new("S0000", "C0001", "CVA", -1200.5, 30), new("S0000", "C0001", "FVA", -300, 10), new("S0000", "C0001", "ColVA", 0, 0),
            new("S0000", "C0002", "CVA", -50, 5),
            new("S0001", "C0001", "CVA", -9999, 1),
        ],
        [
            new("S0000", "C0001", "CVA", "zero_rate", "JPY-OIS", "1826d", 2.5e6),
            new("S0000", "C0001", "CVA", "zero_rate", "JPY-OIS", "3652d", -1.0e6),
            new("S0000", "C0001", "CVA", "fx_spot", "USDJPY", "", -400),
            new("S0000", "C0001", "CVA", "fx_notional_delta", "USD", "JPY_amount", -60000),
            new("S0001", "C0001", "CVA", "zero_rate", "JPY-OIS", "1826d", 7),
        ],
        [new("S0000", "T1", "C0001", "C0001-MA1", 12.5, 0)])
    {
        Curves = new Dictionary<string, DeskCurve>
        {
            ["JPY-OIS"] = new("JPY-OIS", [new("365d", 365, 0.99), new("1826d", 1826, 0.94), new("3652d", 3652, 0.86), new("7305d", 7305, 0.70)]),
        },
    };

    (WebApplicationFactory<Program> Host, HttpClient Client) Desk(string? configured = "tiny", params DeskResultSet[] sets)
    {
        var held = new HeldResults();
        foreach (var set in sets.Length == 0 ? [Tiny()] : sets) held.Sets[set.Reference] = set;
        var host = factory.WithWebHostBuilder(b => b.UseSetting("Rates:Ticker", "false").UseSetting("Desk:ResultSet", configured ?? "")
            .ConfigureTestServices(s => s.AddSingleton<IDeskResults>(held)));
        return (host, host.CreateClient());
    }

    static Task<HttpResponseMessage> Command(HttpClient client, string capability, JsonObject request) =>
        client.PostAsJsonAsync($"/api/agent/commands/{capability}/1.0.0",
            new { inputs = new JsonObject { ["request"] = request }, idempotencyKey = Guid.NewGuid().ToString() });

    static async Task<JsonObject> Ok(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!;
    }

    static async Task<(HttpStatusCode Status, string Code, string Message)> Refused(HttpResponseMessage response)
    {
        var body = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        return (response.StatusCode, body["code"]!.GetValue<string>(), body.ToJsonString());
    }

    static async Task<JsonNode> NewWorkspace(HttpClient client, string? resultSet = null) =>
        (await Ok(await Command(client, "desk.workspace.create", resultSet is null ? [] : new JsonObject { ["resultSet"] = resultSet })))["outputs"]!["workspace"]!;

    static async Task<JsonNode> Read(HttpClient client, string workspaceId, string table, params string[] fields) =>
        (await Ok(await Command(client, "tables.read", new JsonObject
        {
            ["workspaceId"] = workspaceId, ["table"] = table, ["rows"] = "all", ["fields"] = new JsonArray([.. fields.Select(f => (JsonNode)f)]),
        })))["outputs"]!["answer"]!;

    [Fact] // ADR-0019 §2, §4: the workspace's acquired source is the result set, and its answer says which book it is
    public async Task A_workspace_is_made_on_the_configured_result_set_and_says_which_book_it_is()
    {
        var (host, client) = Desk();

        var workspace = await NewWorkspace(client);

        Assert.Equal(("tiny", "alpha", "abc123", "synthetic/tiny", "2026-11-13"),
            (workspace["resultSet"]!.GetValue<string>(), workspace["engine"]!.GetValue<string>(), workspace["engineCommit"]!.GetValue<string>(),
             workspace["inputSet"]!.GetValue<string>(), workspace["valuationDate"]!.GetValue<string>()));
        var state = host.Services.GetRequiredService<WorkspaceService>().Find(workspace["workspaceId"]!.GetValue<string>())!;
        Assert.Equal(DeskEditRules.App, state.App);
        Assert.Equal($"tiny@{Tiny().ContentHash}", state.Source.Version);
    }

    [Fact] // ADR-0019 §7, §9: the book is the base scenario's rows, with the engine's signs
    public async Task The_books_values_read_as_the_engine_wrote_them()
    {
        var (_, client) = Desk();
        var id = (await NewWorkspace(client))["workspaceId"]!.GetValue<string>();

        var values = await Read(client, id, "desk.values", "counterparty", "legalEntity", "metric", "value", "stdError");

        Assert.Equal(4, values["count"]!.GetValue<int>());
        Assert.Equal(["C0001/CVA", "C0001/FVA", "C0001/ColVA", "C0002/CVA"], values["rows"]!.AsArray().Select(r => r!["key"]!.GetValue<string>()));
        var cva = values["rows"]![0]!["cells"]!.AsArray().Select(c => c!.GetValue<string>()).ToArray();
        Assert.Equal(new[] { "C0001", "SYN-LE-0001", "CVA" }, cva[..3]);
        Assert.StartsWith("-", cva[3]);
    }

    [Fact] // ADR-0019 §7: a counterparty the mapping does not hold has no legal entity, and none is made up for it
    public async Task An_unmapped_counterparty_reads_with_no_legal_entity()
    {
        var (_, client) = Desk();
        var id = (await NewWorkspace(client))["workspaceId"]!.GetValue<string>();

        var values = await Read(client, id, "desk.values", "counterparty", "legalEntity");

        var unmapped = values["rows"]!.AsArray().Single(r => r!["key"]!.GetValue<string>() == "C0002/CVA")!["cells"]!.AsArray();
        Assert.Equal("C0002", unmapped[0]!.GetValue<string>());
        Assert.Equal("empty", unmapped[1]!.GetValue<string>());
    }

    [Fact] // ADR-0019 §7: a kind with no declared unit reads with none
    public async Task The_books_sensitivities_read_with_the_unit_the_result_set_declares()
    {
        var (_, client) = Desk();
        var id = (await NewWorkspace(client))["workspaceId"]!.GetValue<string>();

        var risk = await Read(client, id, "desk.risk", "kind", "input", "per");

        Assert.Equal(4, risk["count"]!.GetValue<int>());
        string Per(string kind) => risk["rows"]!.AsArray().First(r => r!["cells"]![0]!.GetValue<string>() == kind)!["cells"]![2]!.GetValue<string>();
        Assert.Equal("rate", Per("zero_rate"));
        Assert.Equal("level", Per("fx_spot"));
        Assert.Equal("empty", Per("fx_notional_delta"));
    }

    [Theory] // ADR-0019 §2: no edit ever changes an acquired row
    [InlineData("entity.set", "value=0")]
    [InlineData("entity.remove", null)]
    public async Task A_write_of_a_book_row_is_refused(string capability, string? set)
    {
        var (host, client) = Desk();
        var id = (await NewWorkspace(client))["workspaceId"]!.GetValue<string>();
        var before = host.Services.GetRequiredService<WorkspaceService>().Find(id)!.Revision;
        var request = new JsonObject { ["workspaceId"] = id, ["table"] = "desk.values", ["rows"] = "C0001/CVA" };
        if (set is not null) request["set"] = set;

        var response = await Command(client, capability, request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(before, host.Services.GetRequiredService<WorkspaceService>().Find(id)!.Revision);
    }

    [Fact] // ADR-0019 §4 and its Rejected: with no result set configured nothing is made, and no book stands in
    public async Task With_no_result_set_configured_the_command_is_refused_by_name()
    {
        var (host, client) = Desk(configured: null);
        var before = host.Services.GetRequiredService<WorkspaceService>().OwnedBy("akira").Count;

        var (status, code, message) = await Refused(await Command(client, "desk.workspace.create", []));

        Assert.Equal((HttpStatusCode.UnprocessableEntity, "NotConfigured"), (status, code));
        Assert.Contains("Desk:ResultSet", message);
        Assert.Equal(before, host.Services.GetRequiredService<WorkspaceService>().OwnedBy("akira").Count);
    }

    [Fact]
    public async Task A_result_set_that_does_not_exist_is_refused_by_name()
    {
        var (_, client) = Desk();

        var (status, code, message) = await Refused(await Command(client, "desk.workspace.create", new JsonObject { ["resultSet"] = "nothing" }));

        Assert.Equal((HttpStatusCode.UnprocessableEntity, "NotFound"), (status, code));
        Assert.Contains("nothing", message);
    }

    // ADR-0019 §2: a what-if hedge is an entity the workspace makes, in the one log.

    const string TenYearPayer = "jpy 10y 1bn pay 1.5%";

    static Task<HttpResponseMessage> NewHedge(HttpClient client, string workspaceId, string terms) =>
        Command(client, "entity.create", new JsonObject { ["product"] = DeskProducts.Irs, ["workspaceId"] = workspaceId, ["terms"] = terms });

    static async Task<string[]> Hedges(HttpClient client, string workspaceId) =>
        [.. (await Read(client, workspaceId, "desk.hedges", "currency"))["rows"]!.AsArray().Select(r => r!["key"]!.GetValue<string>())];

    [Fact]
    public async Task A_swap_made_from_its_short_terms_is_a_row_of_the_log_and_undo_takes_it_back()
    {
        var (host, client) = Desk();
        var id = (await NewWorkspace(client))["workspaceId"]!.GetValue<string>();

        var made = (await Ok(await NewHedge(client, id, TenYearPayer)))["outputs"]!["entity"]!["entity"]!.GetValue<string>();

        Assert.Equal("H1", made);
        var row = (await Read(client, id, "desk.hedges", "product", "end", "currency", "notional", "direction", "rate"))["rows"]!.AsArray().Single()!;
        Assert.Equal(new[] { "irs", "10y", "JPY", "1bn", "pay", "1.5%" }, row["cells"]!.AsArray().Select(c => c!.GetValue<string>()));
        var log = host.Services.GetRequiredService<WorkspaceService>().Find(id)!.Log;
        Assert.Contains(log, e => e.Kind == EditKind.Create && e.Target is { Scope: DeskHedges.Scope, Entity: "H1" });

        // The page's door: it names the revision it saw, where the agent's names the command.
        await Ok(await client.PostAsJsonAsync("/api/commands/workspace.undo/1.0.0", new
        {
            inputs = new JsonObject { ["request"] = new JsonObject { ["workspaceId"] = id, ["revision"] = log[^1].Seq } },
            idempotencyKey = Guid.NewGuid().ToString(),
        }));

        Assert.Empty(await Hedges(client, id));
    }

    [Fact]
    public async Task A_hedge_is_changed_and_removed_through_the_tables_verbs()
    {
        var (_, client) = Desk();
        var id = (await NewWorkspace(client))["workspaceId"]!.GetValue<string>();
        await Ok(await NewHedge(client, id, TenYearPayer));

        await Ok(await Command(client, "entity.set", new JsonObject { ["workspaceId"] = id, ["table"] = "desk.hedges", ["rows"] = "H1", ["set"] = "notional=2bn" }));
        var notional = (await Read(client, id, "desk.hedges", "notional"))["rows"]![0]!["cells"]![0]!.GetValue<string>();
        await Ok(await Command(client, "entity.remove", new JsonObject { ["workspaceId"] = id, ["table"] = "desk.hedges", ["rows"] = "H1" }));

        Assert.Equal("2bn", notional);
        Assert.Empty(await Hedges(client, id));
        // A name is never given twice: the next hedge is H2, though H1 is gone.
        Assert.Equal("H2", (await Ok(await NewHedge(client, id, TenYearPayer)))["outputs"]!["entity"]!["entity"]!.GetValue<string>());
    }

    [Fact] // ADR-0019 §7: a hedge whose risk the provider cannot answer is not made
    public async Task A_hedge_in_a_currency_whose_curve_the_result_set_does_not_hold_is_refused_by_name()
    {
        var (_, client) = Desk();
        var id = (await NewWorkspace(client))["workspaceId"]!.GetValue<string>();

        var response = await NewHedge(client, id, "usd 10y 1bn pay 1.5%");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("declares no curve for a hedge in USD", await response.Content.ReadAsStringAsync());
        Assert.Empty(await Hedges(client, id));
    }

    [Fact]
    public async Task A_change_that_leaves_no_swap_is_refused_and_the_hedge_stays_as_it_was()
    {
        var (_, client) = Desk();
        var id = (await NewWorkspace(client))["workspaceId"]!.GetValue<string>();
        await Ok(await NewHedge(client, id, TenYearPayer));

        var response = await Command(client, "entity.set", new JsonObject { ["workspaceId"] = id, ["table"] = "desk.hedges", ["rows"] = "H1", ["set"] = "currency=usd" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("JPY", (await Read(client, id, "desk.hedges", "currency"))["rows"]![0]!["cells"]![0]!.GetValue<string>());
    }
}
