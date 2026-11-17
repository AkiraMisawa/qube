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
            new Dictionary<string, SensitivityPer> { ["zero_rate"] = SensitivityPer.Rate, ["fx_spot"] = SensitivityPer.Level }),
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
        [new("S0000", "T1", "C0001", "C0001-MA1", 12.5, 0)]);

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
}
