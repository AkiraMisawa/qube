using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Qube.Composition.Application.Workspaces;
using Qube.Samples.PositionKeeper;

namespace Qube.Composition.Tests;

/// <summary>
/// ADR-0019 §2, §5 and §8: the Position Keeper at the command layer, as the line and the AI reach it. The results port is a small
/// set held in memory; the engine's own files are read in <see cref="PkResultFilesTests"/>.
/// </summary>
public class PkCommandTests(QubeFactory factory) : IClassFixture<QubeFactory>
{
    /// <summary>Result sets by reference, as a test holds them.</summary>
    sealed class HeldResults : IPkResults
    {
        public Dictionary<string, PkResultSet> Sets { get; } = [];

        public PkResultSet Read(string reference) => Sets.TryGetValue(reference, out var set) ? set
            : throw new PkResultsRefused(PkResultsRefusal.NotFound, $"There is no result set '{reference}'.");
    }

    internal static PkResultSet Tiny(string hash = "1111111111111111111111111111111111111111111111111111111111111111") => new("tiny", hash,
        new PkManifest("alpha", "abc123", "synthetic/tiny", new DateOnly(2026, 11, 13), "S0000",
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
        Curves = new Dictionary<string, PkCurve>
        {
            ["JPY-OIS"] = new("JPY-OIS", [new("365d", 365, 0.99), new("1826d", 1826, 0.94), new("3652d", 3652, 0.86), new("7305d", 7305, 0.70)]),
        },
        Levels = new Dictionary<(string, string, string), double>
        {
            [("fx_spot", "USDJPY", "")] = 150,
            [("zero_rate", "JPY-OIS", "365d")] = 0.005, [("zero_rate", "JPY-OIS", "1826d")] = 0.01,
            [("zero_rate", "JPY-OIS", "3652d")] = 0.02, [("zero_rate", "JPY-OIS", "7305d")] = 0.025,
        },
    };

    (WebApplicationFactory<Program> Host, HttpClient Client) Keeper(string? configured = "tiny", IHedgeRisk? engine = null, PkResultSet? set = null)
    {
        var held = new HeldResults();
        held.Sets["tiny"] = set ?? Tiny();
        var host = factory.WithWebHostBuilder(b => b.UseSetting("Rates:Ticker", "false").UseSetting("PositionKeeper:ResultSet", configured ?? "")
            .ConfigureTestServices(s =>
            {
                s.AddSingleton<IPkResults>(held);
                if (engine is not null) s.AddSingleton(engine);
            }));
        return (host, host.CreateClient());
    }

    /// <summary>
    /// The engine port as a test holds it (ADR-0019 §8): every hedge has the same known risk, per unit of its notional
    /// in billions, so that Net and the prediction are checked against numbers worked by hand.
    /// </summary>
    sealed class FakeEngine(Qube.Composition.Contracts.ExecutionMode mode) : IHedgeRisk
    {
        public HedgeRiskAnswer Risk(HedgeSwap hedge, PkResultSet book) => hedge.Currency == "JPY"
            ? new("fake-engine", mode, 0,
                [new("zero_rate", "JPY-OIS", "1826d", -2.0e6 * hedge.Notional / 1e9), new("zero_rate", "JPY-OIS", "7305d", 5.0e5 * hedge.Notional / 1e9)])
            : throw new HedgeRiskRefused("NoCurve", $"The fake engine has no curve for {hedge.Currency}.");
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
        (await Ok(await Command(client, "pk.workspace.create", resultSet is null ? [] : new JsonObject { ["resultSet"] = resultSet })))["outputs"]!["workspace"]!;

    static async Task<JsonNode> Read(HttpClient client, string workspaceId, string table, params string[] fields) =>
        (await Ok(await Command(client, "tables.read", new JsonObject
        {
            ["workspaceId"] = workspaceId, ["table"] = table, ["rows"] = "all", ["fields"] = new JsonArray([.. fields.Select(f => (JsonNode)f)]),
        })))["outputs"]!["answer"]!;

    [Fact] // ADR-0019 §2, §4: the workspace's acquired source is the result set, and its answer says which book it is
    public async Task A_workspace_is_made_on_the_configured_result_set_and_says_which_book_it_is()
    {
        var (host, client) = Keeper();

        var workspace = await NewWorkspace(client);

        Assert.Equal(("tiny", "alpha", "abc123", "synthetic/tiny", "2026-11-13"),
            (workspace["resultSet"]!.GetValue<string>(), workspace["engine"]!.GetValue<string>(), workspace["engineCommit"]!.GetValue<string>(),
             workspace["inputSet"]!.GetValue<string>(), workspace["valuationDate"]!.GetValue<string>()));
        var state = host.Services.GetRequiredService<WorkspaceService>().Find(workspace["workspaceId"]!.GetValue<string>())!;
        Assert.Equal(PkEditRules.App, state.App);
        Assert.Equal($"tiny@{Tiny().ContentHash}", state.Source.Version);
    }

    [Fact] // ADR-0019 §7, §9: the book is the base scenario's rows, with the engine's signs
    public async Task The_books_values_read_as_the_engine_wrote_them()
    {
        var (_, client) = Keeper();
        var id = (await NewWorkspace(client))["workspaceId"]!.GetValue<string>();

        var values = await Read(client, id, "pk.values", "counterparty", "legalEntity", "metric", "value", "stdError");

        Assert.Equal(4, values["count"]!.GetValue<int>());
        Assert.Equal(["C0001/CVA", "C0001/FVA", "C0001/ColVA", "C0002/CVA"], values["rows"]!.AsArray().Select(r => r!["key"]!.GetValue<string>()));
        var cva = values["rows"]![0]!["cells"]!.AsArray().Select(c => c!.GetValue<string>()).ToArray();
        Assert.Equal(new[] { "C0001", "SYN-LE-0001", "CVA" }, cva[..3]);
        Assert.StartsWith("-", cva[3]);
    }

    [Fact] // ADR-0019 §7: a counterparty the mapping does not hold has no legal entity, and none is made up for it
    public async Task An_unmapped_counterparty_reads_with_no_legal_entity()
    {
        var (_, client) = Keeper();
        var id = (await NewWorkspace(client))["workspaceId"]!.GetValue<string>();

        var values = await Read(client, id, "pk.values", "counterparty", "legalEntity");

        var unmapped = values["rows"]!.AsArray().Single(r => r!["key"]!.GetValue<string>() == "C0002/CVA")!["cells"]!.AsArray();
        Assert.Equal("C0002", unmapped[0]!.GetValue<string>());
        Assert.Equal("empty", unmapped[1]!.GetValue<string>());
    }

    [Fact] // ADR-0019 §7: a kind with no declared unit reads with none
    public async Task The_books_sensitivities_read_with_the_unit_the_result_set_declares()
    {
        var (_, client) = Keeper();
        var id = (await NewWorkspace(client))["workspaceId"]!.GetValue<string>();

        var risk = await Read(client, id, "pk.risk", "kind", "input", "per");

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
        var (host, client) = Keeper();
        var id = (await NewWorkspace(client))["workspaceId"]!.GetValue<string>();
        var before = host.Services.GetRequiredService<WorkspaceService>().Find(id)!.Revision;
        var request = new JsonObject { ["workspaceId"] = id, ["table"] = "pk.values", ["rows"] = "C0001/CVA" };
        if (set is not null) request["set"] = set;

        var response = await Command(client, capability, request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(before, host.Services.GetRequiredService<WorkspaceService>().Find(id)!.Revision);
    }

    [Fact] // ADR-0019 §4 and its Rejected: with no result set configured nothing is made, and no book stands in
    public async Task With_no_result_set_configured_the_command_is_refused_by_name()
    {
        var (host, client) = Keeper(configured: null);
        var before = host.Services.GetRequiredService<WorkspaceService>().OwnedBy("akira").Count;

        var (status, code, message) = await Refused(await Command(client, "pk.workspace.create", []));

        Assert.Equal((HttpStatusCode.UnprocessableEntity, "NotConfigured"), (status, code));
        Assert.Contains("PositionKeeper:ResultSet", message);
        Assert.Equal(before, host.Services.GetRequiredService<WorkspaceService>().OwnedBy("akira").Count);
    }

    [Fact]
    public async Task A_result_set_that_does_not_exist_is_refused_by_name()
    {
        var (_, client) = Keeper();

        var (status, code, message) = await Refused(await Command(client, "pk.workspace.create", new JsonObject { ["resultSet"] = "nothing" }));

        Assert.Equal((HttpStatusCode.UnprocessableEntity, "NotFound"), (status, code));
        Assert.Contains("nothing", message);
    }

    // ADR-0019 §2: a what-if hedge is an entity the workspace makes, in the one log.

    const string TenYearPayer = "jpy 10y 1bn pay 1.5%";

    static Task<HttpResponseMessage> NewHedge(HttpClient client, string workspaceId, string terms) =>
        Command(client, "entity.create", new JsonObject { ["product"] = PkProducts.Irs, ["workspaceId"] = workspaceId, ["terms"] = terms });

    static async Task<string[]> Hedges(HttpClient client, string workspaceId) =>
        [.. (await Read(client, workspaceId, "pk.hedges", "currency"))["rows"]!.AsArray().Select(r => r!["key"]!.GetValue<string>())];

    [Fact]
    public async Task A_swap_made_from_its_short_terms_is_a_row_of_the_log_and_undo_takes_it_back()
    {
        var (host, client) = Keeper();
        var id = (await NewWorkspace(client))["workspaceId"]!.GetValue<string>();

        var made = (await Ok(await NewHedge(client, id, TenYearPayer)))["outputs"]!["entity"]!["entity"]!.GetValue<string>();

        Assert.Equal("H1", made);
        var row = (await Read(client, id, "pk.hedges", "product", "end", "currency", "notional", "direction", "rate"))["rows"]!.AsArray().Single()!;
        Assert.Equal(new[] { "irs", "10y", "JPY", "1bn", "pay", "1.5%" }, row["cells"]!.AsArray().Select(c => c!.GetValue<string>()));
        var log = host.Services.GetRequiredService<WorkspaceService>().Find(id)!.Log;
        Assert.Contains(log, e => e.Kind == EditKind.Create && e.Target is { Scope: PkHedges.Scope, Entity: "H1" });

        // The page's door: it names the revision it saw, where the agent's names the command.
        await Ok(await client.PostAsJsonAsync("/api/commands/workspace.undo/1.0.0", new
        {
            inputs = new JsonObject { ["request"] = new JsonObject { ["workspaceId"] = id, ["revision"] = log[^1].Seq } },
            idempotencyKey = Guid.NewGuid().ToString(),
        }));

        Assert.Empty(await Hedges(client, id));
    }

    [Fact] // ADR-0019 §4: a workspace loaded again on a result set that reads differently, or is gone, is refused by name, as a workspace's refusal
    public void A_workspace_is_not_loaded_on_a_result_set_that_changed_or_is_gone()
    {
        var held = new HeldResults();
        held.Sets["tiny"] = Tiny();
        var made = new PkBookSource(Tiny()).Descriptor;
        var resolve = PkBookSource.Resolver(() => held);

        Assert.IsType<PkBookSource>(resolve(made));
        held.Sets["tiny"] = Tiny(hash: "2222222222222222222222222222222222222222222222222222222222222222");
        var changed = Assert.Throws<WorkspaceRefused>(() => resolve(made));
        held.Sets.Clear();
        var gone = Assert.Throws<WorkspaceRefused>(() => resolve(made));

        Assert.Equal(("Changed", "NotFound"), (changed.Code, gone.Code));
        Assert.Contains("has changed since the workspace was made on it", changed.Message);
    }

    [Fact] // ADR-0019 §3: every move held has its line; one for an input the book has no risk on predicts nothing, and says so as a line
    public async Task A_move_held_for_a_curve_the_position_has_no_risk_on_predicts_zero_as_a_line()
    {
        var tiny = Tiny();
        var set = tiny with
        {
            Manifest = tiny.Manifest with { HedgeCurves = new Dictionary<string, string> { ["JPY"] = "JPY-OIS", ["USD"] = "USD-OIS" } },
            Curves = new Dictionary<string, PkCurve>(tiny.Curves) { ["USD-OIS"] = new("USD-OIS", [new("365d", 365, 0.96), new("3652d", 3652, 0.66)]) },
        };
        var (_, client) = Keeper(set: set);
        var id = (await NewWorkspace(client))["workspaceId"]!.GetValue<string>();

        await Ok(await Command(client, "entity.set", new JsonObject { ["workspaceId"] = id, ["rows"] = "move zero_rate/USD-OIS", ["set"] = "move 10" }));
        var line = Assert.Single((await Position(client, id))["pnl"]!.AsArray())!;

        Assert.Equal(("zero_rate", "USD-OIS", 10d, 0d, 0d), (line["kind"]!.GetValue<string>(), line["input"]!.GetValue<string>(), line["move"]!.GetValue<double>(), line["book"]!.GetValue<double>(), line["net"]!.GetValue<double>()));
        Assert.Null(line["whatIf"]);
        Assert.Equal(["FIRST ORDER"], line["marks"]!.AsArray().Select(m => m!.GetValue<string>()));
    }

    [Fact]
    public async Task A_hedge_is_changed_and_removed_through_the_tables_verbs()
    {
        var (_, client) = Keeper();
        var id = (await NewWorkspace(client))["workspaceId"]!.GetValue<string>();
        var before = (await Position(client, id))["risk"]!.ToJsonString();
        await Ok(await NewHedge(client, id, TenYearPayer));
        var hedged = (await Position(client, id))["risk"]!.ToJsonString();

        await Ok(await Command(client, "entity.set", new JsonObject { ["workspaceId"] = id, ["table"] = "pk.hedges", ["rows"] = "H1", ["set"] = "notional=2bn" }));
        var notional = (await Read(client, id, "pk.hedges", "notional"))["rows"]![0]!["cells"]![0]!.GetValue<string>();
        await Ok(await Command(client, "entity.remove", new JsonObject { ["workspaceId"] = id, ["table"] = "pk.hedges", ["rows"] = "H1" }));

        Assert.Equal("2bn", notional);
        Assert.Empty(await Hedges(client, id));
        // A removed hedge leaves the figures as they were before it (ADR-0019 §2): every line, mark and Net.
        Assert.NotEqual(before, hedged);
        Assert.Equal(before, (await Position(client, id))["risk"]!.ToJsonString());
        // A name is never given twice: the next hedge is H2, though H1 is gone.
        Assert.Equal("H2", (await Ok(await NewHedge(client, id, TenYearPayer)))["outputs"]!["entity"]!["entity"]!.GetValue<string>());
    }

    [Fact] // ADR-0019 §7: a hedge whose risk the provider cannot answer is not made
    public async Task A_hedge_in_a_currency_whose_curve_the_result_set_does_not_hold_is_refused_by_name()
    {
        var (_, client) = Keeper();
        var id = (await NewWorkspace(client))["workspaceId"]!.GetValue<string>();

        var response = await NewHedge(client, id, "usd 10y 1bn pay 1.5%");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("declares no curve for a hedge in USD", await response.Content.ReadAsStringAsync());
        Assert.Empty(await Hedges(client, id));
    }

    [Fact]
    public async Task A_change_that_leaves_no_swap_is_refused_and_the_hedge_stays_as_it_was()
    {
        var (_, client) = Keeper();
        var id = (await NewWorkspace(client))["workspaceId"]!.GetValue<string>();
        await Ok(await NewHedge(client, id, TenYearPayer));

        var response = await Command(client, "entity.set", new JsonObject { ["workspaceId"] = id, ["table"] = "pk.hedges", ["rows"] = "H1", ["set"] = "currency=usd" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("JPY", (await Read(client, id, "pk.hedges", "currency"))["rows"]![0]!["cells"]![0]!.GetValue<string>());
    }

    // ADR-0019 §3, §7: Book, What-if and Net, and the first-order prediction, read with their marks.

    static async Task<JsonNode> Position(HttpClient client, string workspaceId, JsonObject? more = null)
    {
        var request = new JsonObject { ["workspaceId"] = workspaceId };
        foreach (var (name, value) in more ?? []) request[name] = value?.DeepClone();
        return (await Ok(await Command(client, "pk.position.read", request)))["outputs"]!["answer"]!;
    }

    static Task<HttpResponseMessage> Move(HttpClient client, string workspaceId, string row, string value) =>
        Command(client, "entity.set", new JsonObject { ["workspaceId"] = workspaceId, ["table"] = "pk.moves", ["rows"] = row, ["set"] = $"move={value}" });

    static JsonNode Line(JsonNode position, string list, string input, string? key = null) =>
        position[list]!.AsArray().Single(r => r!["input"]!.GetValue<string>() == input && (key is null || r["key"]!.GetValue<string>() == key))!;

    static string[] MarksOf(JsonNode figure) => [.. figure["marks"]!.AsArray().Select(m => m!.GetValue<string>())];

    [Fact]
    public async Task The_book_alone_reads_with_no_mark_and_its_totals_have_no_standard_error()
    {
        var (_, client) = Keeper(engine: new FakeEngine(Qube.Composition.Contracts.ExecutionMode.Simulated));
        var id = (await NewWorkspace(client))["workspaceId"]!.GetValue<string>();

        var position = await Position(client, id);

        Assert.Equal(("tiny", "abc123", "2026-11-13"), (position["resultSet"]!.GetValue<string>(), position["engineCommit"]!.GetValue<string>(), position["valuationDate"]!.GetValue<string>()));
        // CVA: -1200.5 and -50 over the two counterparties; a cost is negative and is not turned over.
        Assert.Equal(-1250.5, position["totals"]!.AsArray().Single(t => t!["metric"]!.GetValue<string>() == "CVA")!["value"]!.GetValue<double>());
        Assert.Null(position["totals"]![0]!["stdError"]);
        var node = Line(position, "risk", "JPY-OIS", "1826d");
        Assert.Equal((2.5e6, 2.5e6), (node["book"]!.GetValue<double>(), node["net"]!.GetValue<double>()));
        Assert.Null(node["whatIf"]);
        Assert.All(position["risk"]!.AsArray(), r => Assert.Empty(MarksOf(r!)));
        Assert.Empty(position["pnl"]!.AsArray());
        Assert.Null(position["pnlTotalNet"]);
    }

    [Fact]
    public async Task Net_is_book_plus_what_if_on_matching_keys_and_a_key_only_one_side_holds_is_kept()
    {
        var (_, client) = Keeper(engine: new FakeEngine(Qube.Composition.Contracts.ExecutionMode.Simulated));
        var id = (await NewWorkspace(client))["workspaceId"]!.GetValue<string>();
        await Ok(await NewHedge(client, id, TenYearPayer));

        var position = await Position(client, id, new JsonObject { ["kind"] = "zero_rate" });

        (double Book, double? WhatIf, double Net) Of(string key)
        {
            var line = Line(position, "risk", "JPY-OIS", key);
            return (line["book"]!.GetValue<double>(), line["whatIf"]?.GetValue<double>(), line["net"]!.GetValue<double>());
        }
        Assert.Equal((2.5e6, -2.0e6, 0.5e6), Of("1826d"));    // both sides
        Assert.Equal((-1.0e6, null, -1.0e6), Of("3652d"));    // the book alone
        Assert.Equal((0, 5.0e5, 5.0e5), Of("7305d"));         // the hedge alone
        Assert.Equal(["1826d", "3652d", "7305d"], position["risk"]!.AsArray().Select(r => r!["key"]!.GetValue<string>()));
    }

    [Theory] // ADR-0019 §2, §4: the marks are the server's, on every figure they apply to and on no other
    [InlineData(Qube.Composition.Contracts.ExecutionMode.Simulated, "WHAT-IF|SIMULATED")]
    [InlineData(Qube.Composition.Contracts.ExecutionMode.Actual, "WHAT-IF")]
    public async Task A_figure_with_a_hedge_in_it_is_marked_and_a_book_only_figure_is_not(Qube.Composition.Contracts.ExecutionMode mode, string marks)
    {
        var (_, client) = Keeper(engine: new FakeEngine(mode));
        var id = (await NewWorkspace(client))["workspaceId"]!.GetValue<string>();
        await Ok(await NewHedge(client, id, TenYearPayer));
        await Ok(await Move(client, id, "zero_rate/JPY-OIS", "10"));
        await Ok(await Move(client, id, "fx_spot/USDJPY", "2"));

        var position = await Position(client, id);

        Assert.Equal(marks, string.Join("|", MarksOf(Line(position, "risk", "JPY-OIS", "1826d"))));
        Assert.Equal(marks, string.Join("|", MarksOf(Line(position, "risk", "JPY-OIS", "7305d"))));
        Assert.Empty(MarksOf(Line(position, "risk", "JPY-OIS", "3652d")));
        Assert.Empty(MarksOf(Line(position, "risk", "USDJPY")));
        Assert.Equal(marks, string.Join("|", MarksOf(position["hedges"]![0]!)));
        Assert.Equal(marks + "|FIRST ORDER", string.Join("|", MarksOf(Line(position, "pnl", "JPY-OIS"))));
        Assert.Equal("FIRST ORDER", string.Join("|", MarksOf(Line(position, "pnl", "USDJPY"))));
        Assert.Equal(marks + "|FIRST ORDER", string.Join("|", position["pnlTotalMarks"]!.AsArray().Select(m => m!.GetValue<string>())));
    }

    [Fact] // sensitivity times move: 10bp is 0.001 of a rate, and a quote's move is itself
    public async Task The_predicted_profit_and_loss_is_sensitivity_times_move()
    {
        var (_, client) = Keeper(engine: new FakeEngine(Qube.Composition.Contracts.ExecutionMode.Simulated));
        var id = (await NewWorkspace(client))["workspaceId"]!.GetValue<string>();
        await Ok(await NewHedge(client, id, TenYearPayer));
        await Ok(await Move(client, id, "zero_rate/JPY-OIS", "10"));
        await Ok(await Move(client, id, "fx_spot/USDJPY", "2"));

        var position = await Position(client, id);

        var rates = Line(position, "pnl", "JPY-OIS");
        // Book (2.5e6 - 1.0e6) * 0.001 = 1500; What-if (-2.0e6 + 0.5e6) * 0.001 = -1500; Net 0.
        Assert.Equal((10, "bp"), (rates["move"]!.GetValue<double>(), rates["typedIn"]!.GetValue<string>()));
        Assert.Equal(1500, rates["book"]!.GetValue<double>(), 9);
        Assert.Equal(-1500, rates["whatIf"]!.GetValue<double>(), 9);
        Assert.Equal(0, rates["net"]!.GetValue<double>(), 9);
        var fx = Line(position, "pnl", "USDJPY");
        // Book -400 per unit of the quote, moved by 2: -800. No hedge has a row on it.
        Assert.Equal((-800, -800), (fx["book"]!.GetValue<double>(), fx["net"]!.GetValue<double>()));
        Assert.Null(fx["whatIf"]);
        Assert.Equal(700, position["pnlTotalBook"]!.GetValue<double>(), 9);
        Assert.Equal(-1500, position["pnlTotalWhatIf"]!.GetValue<double>(), 9);
        Assert.Equal(-800, position["pnlTotalNet"]!.GetValue<double>(), 9);
    }

    [Fact] // ADR-0019 §7: a move in % is of the input's level, key by key: sensitivity times level times the percentage
    public async Task A_relative_move_is_sensitivity_times_the_level_times_the_percentage()
    {
        var (_, client) = Keeper(engine: new FakeEngine(Qube.Composition.Contracts.ExecutionMode.Simulated));
        var id = (await NewWorkspace(client))["workspaceId"]!.GetValue<string>();
        await Ok(await NewHedge(client, id, TenYearPayer));
        await Ok(await Move(client, id, "fx_spot/USDJPY", "1%"));
        await Ok(await Move(client, id, "zero_rate/JPY-OIS", "10 %"));

        var position = await Position(client, id);

        var fx = Line(position, "pnl", "USDJPY");
        // -400 per unit of the quote, the quote at 150, up 1%: -400 * 150 * 0.01 = -600.
        Assert.Equal((1, "%"), (fx["move"]!.GetValue<double>(), fx["typedIn"]!.GetValue<string>()));
        Assert.Equal(-600, fx["book"]!.GetValue<double>(), 9);
        var rates = Line(position, "pnl", "JPY-OIS");
        // Book (2.5e6 * 0.01 - 1.0e6 * 0.02) * 0.1 = 500; What-if (-2.0e6 * 0.01 + 0.5e6 * 0.025) * 0.1 = -750; Net -250.
        Assert.Equal(500, rates["book"]!.GetValue<double>(), 9);
        Assert.Equal(-750, rates["whatIf"]!.GetValue<double>(), 9);
        Assert.Equal(-250, rates["net"]!.GetValue<double>(), 9);
        var moves = await Read(client, id, "pk.moves", "move");
        Assert.Equal("1 %", moves["rows"]!.AsArray().Single(r => r!["key"]!.GetValue<string>() == "fx_spot/USDJPY")!["cells"]![0]!.GetValue<string>());
    }

    [Fact] // a bare number is absolute, in the kind's own unit; bp said is the same for a rate, and is not a quote's unit
    public async Task An_absolute_move_may_say_bp_for_a_rate_and_not_for_a_quote()
    {
        var (_, client) = Keeper();
        var id = (await NewWorkspace(client))["workspaceId"]!.GetValue<string>();

        await Ok(await Move(client, id, "zero_rate/JPY-OIS", "10bp"));
        var (status, code, message) = await Refused(await Move(client, id, "fx_spot/USDJPY", "10bp"));

        var rates = Line(await Position(client, id), "pnl", "JPY-OIS");
        Assert.Equal((10, "bp"), (rates["move"]!.GetValue<double>(), rates["typedIn"]!.GetValue<string>()));
        Assert.Equal(1500, rates["book"]!.GetValue<double>(), 9);
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "WrongUnit"), (status, code));
        Assert.Contains("fx_spot/USDJPY", message);
    }

    [Fact] // no level is assumed: a relative move of an input the result set gives no level of is refused, and names the key
    public async Task A_relative_move_of_an_input_without_its_levels_is_refused_by_name()
    {
        var tiny = Tiny();
        var set = tiny with { Levels = tiny.Levels.Where(l => l.Key != ("zero_rate", "JPY-OIS", "3652d")).ToDictionary(l => l.Key, l => l.Value) };
        var (_, client) = Keeper(set: set);
        var id = (await NewWorkspace(client))["workspaceId"]!.GetValue<string>();

        var (status, code, message) = await Refused(await Move(client, id, "zero_rate/JPY-OIS", "5%"));

        Assert.Equal((HttpStatusCode.UnprocessableEntity, "NoLevel"), (status, code));
        Assert.Contains("3652d", message);
        Assert.Empty((await Position(client, id))["pnl"]!.AsArray());
        await Ok(await Move(client, id, "zero_rate/JPY-OIS", "5"));
    }

    [Fact] // a trader reads a rate's risk per basis point and a tenor by its name; neither is guessed where the result set does not give it
    public async Task Risk_reads_per_step_with_the_tenor_named_where_the_key_is_one()
    {
        var (_, client) = Keeper();
        var id = (await NewWorkspace(client))["workspaceId"]!.GetValue<string>();

        var position = await Position(client, id);

        var fiveYears = Line(position, "risk", "JPY-OIS", "1826d");
        // 2026-11-13 plus five years is 1826 days on; 2.5e6 per 1.0 of the rate is 250 per basis point.
        Assert.Equal(("5Y", "1bp"), (fiveYears["tenor"]!.GetValue<string>(), fiveYears["step"]!.GetValue<string>()));
        Assert.Equal(250, fiveYears["bookStep"]!.GetValue<double>(), 9);
        Assert.Equal(250, fiveYears["netStep"]!.GetValue<double>(), 9);
        Assert.Null(fiveYears["whatIfStep"]);
        // Ten years on is 3653 days: 3652d is no tenor's day, and is given no name.
        Assert.Null(Line(position, "risk", "JPY-OIS", "3652d")["tenor"]);
        var fx = Line(position, "risk", "USDJPY");
        Assert.Equal(("quote", -400), (fx["step"]!.GetValue<string>(), fx["bookStep"]!.GetValue<double>()));
        var undeclared = Line(position, "risk", "USD");
        Assert.Null(undeclared["step"]);
        Assert.Null(undeclared["bookStep"]);
    }

    [Fact] // the summary a trader starts from: each input moved by one step at every key, and each counterparty's adjustments summed
    public async Task The_position_sums_each_input_over_its_keys_and_each_counterparty_over_its_metrics()
    {
        var (_, client) = Keeper(engine: new FakeEngine(Qube.Composition.Contracts.ExecutionMode.Simulated));
        var id = (await NewWorkspace(client))["workspaceId"]!.GetValue<string>();
        await Ok(await NewHedge(client, id, TenYearPayer));

        var position = await Position(client, id);

        var rates = Line(position, "parallel", "JPY-OIS");
        // Book (2.5e6 - 1.0e6) * 1e-4 = 150; What-if (-2.0e6 + 0.5e6) * 1e-4 = -150; Net 0.
        Assert.Equal("1bp", rates["step"]!.GetValue<string>());
        Assert.Equal(150, rates["book"]!.GetValue<double>(), 9);
        Assert.Equal(-150, rates["whatIf"]!.GetValue<double>(), 9);
        Assert.Equal(0, rates["net"]!.GetValue<double>(), 9);
        Assert.Equal(["WHAT-IF", "SIMULATED"], MarksOf(rates));
        // A kind with no declared unit has no step, and no line.
        Assert.DoesNotContain(position["parallel"]!.AsArray(), l => l!["kind"]!.GetValue<string>() == "fx_notional_delta");
        var parties = position["byCounterparty"]!.AsArray();
        // C0001: -1200.5 - 300 + 0.
        Assert.Equal(-1500.5, parties.Single(c => c!["counterparty"]!.GetValue<string>() == "C0001")!["total"]!.GetValue<double>(), 9);
        var unmapped = parties.Single(c => c!["counterparty"]!.GetValue<string>() == "C0002")!;
        Assert.Equal(-50, unmapped["total"]!.GetValue<double>());
        Assert.Null(unmapped["legalEntity"]);
    }

    [Fact] // the currency the figures are in is the result set's to say; unsaid, none is shown
    public async Task The_currency_of_the_figures_is_given_only_where_the_result_set_declares_it()
    {
        var (_, plain) = Keeper();
        var (_, declared) = Keeper(set: Tiny() with { Manifest = Tiny().Manifest with { Currency = "JPY" } });

        var without = await Position(plain, (await NewWorkspace(plain))["workspaceId"]!.GetValue<string>());
        var with = await Position(declared, (await NewWorkspace(declared))["workspaceId"]!.GetValue<string>());

        Assert.Null(without["currency"]);
        Assert.Equal("JPY", with["currency"]!.GetValue<string>());
    }

    [Fact] // ADR-0019 §7: nothing assumes a unit
    public async Task A_move_of_a_kind_without_a_declared_unit_is_refused_by_name()
    {
        var (_, client) = Keeper();
        var id = (await NewWorkspace(client))["workspaceId"]!.GetValue<string>();

        var (status, code, message) = await Refused(await Move(client, id, "fx_notional_delta/USD", "1"));

        Assert.Equal((HttpStatusCode.UnprocessableEntity, "NoUnit"), (status, code));
        Assert.Contains("fx_notional_delta", message);
        Assert.Empty((await Position(client, id))["pnl"]!.AsArray());
    }

    [Fact]
    public async Task A_move_is_in_the_log_and_emptied_it_predicts_nothing()
    {
        var (host, client) = Keeper();
        var id = (await NewWorkspace(client))["workspaceId"]!.GetValue<string>();

        await Ok(await Move(client, id, "zero_rate/JPY-OIS", "-25"));
        var held = (await Position(client, id))["pnl"]!.AsArray().Count;
        await Ok(await Move(client, id, "zero_rate/JPY-OIS", "empty"));

        Assert.Equal(1, held);
        Assert.Empty((await Position(client, id))["pnl"]!.AsArray());
        Assert.Contains(host.Services.GetRequiredService<WorkspaceService>().Find(id)!.Log, e => e.Kind == EditKind.Set && e.Target?.Scope == PkMoves.Scope);
    }

    [Fact]
    public async Task The_position_of_some_counterparties_reads_their_book_alone()
    {
        var (_, client) = Keeper();
        var id = (await NewWorkspace(client))["workspaceId"]!.GetValue<string>();

        var position = await Position(client, id, new JsonObject { ["counterparties"] = new JsonArray("C0002") });
        var unknown = await Refused(await Command(client, "pk.position.read", new JsonObject { ["workspaceId"] = id, ["counterparties"] = new JsonArray("C9999") }));

        Assert.Equal(-50, position["totals"]!.AsArray().Single()!["value"]!.GetValue<double>());
        Assert.Empty(position["risk"]!.AsArray());
        Assert.Equal("UnknownCounterparty", unknown.Code);
    }

    [Fact] // ADR-0019 §3: reading, making a hedge and holding a move make no Run
    public async Task No_command_of_the_slice_makes_a_run()
    {
        var (host, client) = Keeper();
        Task<int> Runs() => Task.FromResult(host.Services.GetRequiredService<Qube.Composition.Application.IRunStore>().List(new(null, 0, 1)).Total);
        var before = await Runs();
        var id = (await NewWorkspace(client))["workspaceId"]!.GetValue<string>();
        await Ok(await NewHedge(client, id, TenYearPayer));
        await Ok(await Move(client, id, "zero_rate/JPY-OIS", "10"));
        await Position(client, id);

        Assert.Equal(before, await Runs());
    }
}
