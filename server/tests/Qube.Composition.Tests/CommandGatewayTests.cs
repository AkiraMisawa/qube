using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;

namespace Qube.Composition.Tests;

// ADR-0016 §5 and §11: the page's own door to the command gateway, and undo and redo as two kernel commands behind it.
// The workspace is the pricer's, so that what was taken back is named as its screen names it. Every name and figure
// is synthetic.
public class CommandGatewayTests(QubeFactory factory) : IClassFixture<QubeFactory>
{
    readonly HttpClient client = factory.CreateClient();

    async Task<JsonObject> Create()
    {
        var response = await client.PostAsJsonAsync("/api/xva/workspaces", new { registration = "ABC/LE-01", valueDate = "2026-10-26", portfolioSize = 10 });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!;
    }

    Task<HttpResponseMessage> Command(string capability, JsonObject request, HttpClient? by = null, string door = "/api/commands") =>
        (by ?? client).PostAsJsonAsync($"{door}/{capability}/1.0.0", new { inputs = new JsonObject { ["request"] = request }, idempotencyKey = Guid.NewGuid().ToString() });

    static async Task<JsonObject> Ok(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!["outputs"]!["answer"]!.AsObject();
    }

    // What a screen is asked for, whatever order its members are written in.
    static void Same(JsonObject expected, JsonNode? asked) => Assert.True(JsonNode.DeepEquals(expected, asked), $"Expected {expected.ToJsonString()} and was answered {asked?.ToJsonString()}.");

    static async Task<(HttpStatusCode Status, string Code, string Message)> Refused(HttpResponseMessage response)
    {
        var error = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        return (response.StatusCode, error["code"]!.GetValue<string>(), error["message"]!.GetValue<string>());
    }

    async Task<JsonObject> Edit(string id, long revision, string entity, string field, JsonNode? value)
    {
        var response = await client.PostAsJsonAsync($"/api/xva/workspaces/{id}/edits", new { revision, entity, field, value });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!;
    }

    static decimal Threshold(JsonObject view) =>
        view["csas"]!.AsArray().Single(c => c!["csaId"]!.GetValue<string>() == "CSA-A")!["counterpartyThreshold"]!.GetValue<decimal>();

    [Fact] // ADR-0016 §5, §11: the page takes back its own edit through the gateway, as the page, and is told what was taken back
    public async Task The_page_takes_back_its_last_edit_through_the_gateway_and_puts_it_back()
    {
        var ws = await Create();
        var id = ws["workspaceId"]!.GetValue<string>();
        var original = Threshold(ws);
        var edited = await Edit(id, ws["revision"]!.GetValue<long>(), "csa:CSA-A", "counterpartyThreshold", 5_000_000m);
        var notLatest = await Refused(await Command("workspace.undo", new JsonObject { ["workspaceId"] = id, ["command"] = "c999" }));

        var undone = await Ok(await Command("workspace.undo", new JsonObject { ["workspaceId"] = id, ["revision"] = edited["revision"]!.GetValue<long>() }));
        var afterUndo = (await client.GetFromJsonAsync<JsonObject>($"/api/xva/workspaces/{id}"))!;
        // The result names itself, so its caller can take this very command back by name and no other.
        var redone = await Ok(await Command("workspace.redo", new JsonObject { ["workspaceId"] = id, ["command"] = undone["command"]!.GetValue<string>() }));
        var afterRedo = (await client.GetFromJsonAsync<JsonObject>($"/api/xva/workspaces/{id}"))!;

        Assert.Equal((original, 5_000_000m), (Threshold(afterUndo), Threshold(afterRedo)));
        Assert.Equal("Took back: set CSA-A threshold. redo puts it back.", undone["said"]!.GetValue<string>());
        Assert.Equal("Put back: set CSA-A threshold.", redone["said"]!.GetValue<string>());
        Assert.Equal((afterUndo["revision"]!.GetValue<long>(), "akira", 1), (undone["revision"]!.GetValue<long>(), undone["by"]!.GetValue<string>(), undone["edits"]!.GetValue<int>()));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "NotLatestCommand", $"c999 is not the latest command; {undone["took"]!.GetValue<string>()} is (set CSA-A threshold)."), notLatest);
        // The redo acted on the undo, which the page's door recorded as the page's.
        Assert.Equal((undone["command"]!.GetValue<string>(), "pricer"), (redone["took"]!.GetValue<string>(), redone["origin"]!.GetValue<string>()));
    }

    [Fact] // ADR-0016 §11: a request says what it means, by the revision its caller read or by the command's id, and the AI's names the command
    public async Task A_request_names_the_revision_or_the_command_and_the_agents_names_the_command()
    {
        var ws = await Create();
        var id = ws["workspaceId"]!.GetValue<string>();
        var edited = await Edit(id, ws["revision"]!.GetValue<long>(), "csa:CSA-A", "counterpartyThreshold", 5_000_000m);
        var revision = edited["revision"]!.GetValue<long>();

        var neither = await Refused(await Command("workspace.undo", new JsonObject { ["workspaceId"] = id }));
        var agentUnnamed = await Refused(await Command("workspace.undo", new JsonObject { ["workspaceId"] = id, ["revision"] = revision }, door: "/api/agent/commands"));
        var after = (await client.GetFromJsonAsync<JsonObject>($"/api/xva/workspaces/{id}"))!;

        Assert.Equal((HttpStatusCode.UnprocessableEntity, "CommandNotNamed"), (neither.Status, neither.Code));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "CommandNotNamed"), (agentUnnamed.Status, agentUnnamed.Code));
        Assert.Equal((revision, 5_000_000m), (after["revision"]!.GetValue<long>(), Threshold(after)));
    }

    [Fact] // ADR-0016 §11, note 3: a command with no words is named by its edits' titles, as the screen names them, never an entity's key
    public async Task What_was_taken_back_is_named_as_the_screen_names_it()
    {
        var ws = await Create();
        var id = ws["workspaceId"]!.GetValue<string>();
        var revision = ws["revision"]!.GetValue<long>();
        async Task<string> EditAndUndo(string entity, string field, JsonNode? value)
        {
            var edited = await Edit(id, revision, entity, field, value);
            var undone = await Ok(await Command("workspace.undo", new JsonObject { ["workspaceId"] = id, ["revision"] = edited["revision"]!.GetValue<long>() }));
            revision = undone["revision"]!.GetValue<long>();
            return undone["said"]!.GetValue<string>();
        }

        Assert.Equal("Took back: set DRAFT-1 csa. redo puts it back.", await EditAndUndo("trade:DRAFT-1", "csa", "CSA-B"));
        Assert.Equal("Took back: set Before on the existing trades. redo puts it back.", await EditAndUndo("grid:existing", "before", false));
        Assert.Equal("Took back: set the display currency. redo puts it back.", await EditAndUndo("settings:workspace", "displayCurrency", "USD"));
    }

    [Fact] // ADR-0016 §5, note 4: the page's door answers as the agent's does: 404, 403, 409, and 422 for a refusal
    public async Task The_door_answers_an_unknown_command_a_moved_revision_another_users_workspace_and_a_refusal()
    {
        var ws = await Create();
        var id = ws["workspaceId"]!.GetValue<string>();
        var revision = ws["revision"]!.GetValue<long>();

        var nothing = await Refused(await Command("workspace.undo", new JsonObject { ["workspaceId"] = id, ["revision"] = revision }));
        var moved = await Refused(await Command("workspace.undo", new JsonObject { ["workspaceId"] = id, ["revision"] = revision - 1 }));
        var unknown = await Refused(await Command("workspace.nothing", new JsonObject { ["workspaceId"] = id }));
        var missing = await Refused(await Command("workspace.undo", new JsonObject { ["workspaceId"] = "ws-none", ["revision"] = revision }));
        using var other = factory.WithWebHostBuilder(b => b.UseSetting("Qube:DemoUser", "ben"));
        var notOwner = await Refused(await Command("workspace.undo", new JsonObject { ["workspaceId"] = id, ["revision"] = revision }, other.CreateClient()));

        Assert.Equal((HttpStatusCode.UnprocessableEntity, "NothingToUndo", "There is nothing to take back: the workspace is as it was made."), nothing);
        Assert.Equal((HttpStatusCode.Conflict, "RevisionMoved"), (moved.Status, moved.Code));
        Assert.Equal((HttpStatusCode.NotFound, "UnknownCapability"), (unknown.Status, unknown.Code));
        Assert.Equal((HttpStatusCode.NotFound, "UnknownWorkspace"), (missing.Status, missing.Code));
        Assert.Equal((HttpStatusCode.Forbidden, "NotOwner"), (notOwner.Status, notOwner.Code));
    }

    [Fact] // ADR-0016 §4: the catalog serves how each typed command is typed, and nothing more for a capability no line names
    public async Task The_catalog_says_how_the_typed_commands_are_typed()
    {
        var catalog = (await client.GetFromJsonAsync<JsonObject>("/api/catalog"))!;
        var capabilities = catalog["capabilities"]!.AsArray();
        var typed = capabilities.Where(c => c!["command"] is not null).ToDictionary(c => c!["capabilityId"]!.GetValue<string>(), c => c!["command"]!.ToJsonString());

        Assert.Equal(["draft.apply", "entity.create", "entity.remove", "entity.reset", "entity.set", "rates.frame.capture", "rates.line.add", "rates.live.start", "rates.live.stop",
            "rates.workspace.price", "sequence.run", "sequence.save", "workspace.redo", "workspace.undo", "xva.case.remove", "xva.cases.clear", "xva.cds", "xva.measures",
            "xva.pv01.retry", "xva.refresh", "xva.scenario", "xva.scenario.cross", "xva.scenario.duplicate", "xva.scenario.exclude", "xva.scenario.include",
            "xva.scenario.ladder", "xva.scenario.load", "xva.scenario.remove", "xva.scenario.save", "xva.scenarios.clear", "xva.trades.add", "xva.trades.load", "xva.vary", "xva.workspace.price"],
            typed.Keys.Order(StringComparer.Ordinal));
        // The catalog lists a module's commands in the order it registers them, which the line keeps (note 33): Price first.
        Assert.Equal("xva.workspace.price", capabilities.Select(c => c!["capabilityId"]!.GetValue<string>()).First(id => id.StartsWith("xva.", StringComparison.Ordinal) && typed.ContainsKey(id)));
        // A command that starts work says so by its effect (note 33), and one argument that may be left out says so.
        Assert.Equal("""["execute"]""", capabilities.Single(c => c!["capabilityId"]!.GetValue<string>() == "xva.workspace.price")!["effects"]!.ToJsonString());
        Assert.Equal("""{"words":["scenario save"],"arguments":[{"name":"set","kind":"text","required":true},{"name":"share","kind":"text","required":false}],"app":"xva"}""", typed["xva.scenario.save"]);
        // A command of one application says so (ADR-0016 note 27 of step 3); the kernel's say none, and act on every workspace.
        Assert.Equal("""{"words":["vary"],"arguments":[{"name":"cell","kind":"text","required":true},{"name":"values","kind":"text","required":true}],"app":"xva"}""", typed["xva.vary"]);
        Assert.Equal("""{"words":["add each"],"arguments":[],"app":"xva"}""", typed["xva.trades.add"]);
        Assert.Equal("""{"words":["load trades"],"arguments":[{"name":"trades","kind":"text","required":true}],"app":"xva"}""", typed["xva.trades.load"]);
        // The short forms are listed apart (note 29): what runs is the kernel command each stands for.
        Assert.Equal(["xva.before", "xva.after", "xva.currency"], catalog["shortForms"]!.AsArray().Select(f => f!["formId"]!.GetValue<string>()));
        Assert.Equal("""{"formId":"xva.currency","title":"Show the figures in a currency, such as USD","command":{"words":["currency"],"arguments":[{"name":"currency","kind":"text","required":true}],"app":"xva"}}""",
            catalog["shortForms"]![2]!.ToJsonString());
        Assert.Equal("""{"words":["set"],"arguments":[{"name":"rows","kind":"rows","required":false},{"name":"set","kind":"assignments","required":true}]}""", typed["entity.set"]);
        Assert.Equal("""{"words":["reset"],"arguments":[{"name":"rows","kind":"rows","required":false},{"name":"field","kind":"field","required":false}]}""", typed["entity.reset"]);
        Assert.Equal("""{"words":["remove"],"arguments":[{"name":"rows","kind":"rows","required":true}]}""", typed["entity.remove"]);
        Assert.Equal("""{"words":["undo"],"arguments":[]}""", typed["workspace.undo"]);
        // A saved sequence's two verbs are every application's (ADR-0016 note 63 of step 6).
        Assert.Equal("""{"words":["sequence save"],"arguments":[{"name":"name","kind":"text","required":true},{"name":"share","kind":"text","required":false},{"name":"lines","kind":"text","required":true}]}""",
            typed["sequence.save"]);
        Assert.Equal("""{"words":["sequence run"],"arguments":[{"name":"sequence","kind":"text","required":true}]}""", typed["sequence.run"]);
        // Rates' verbs are typed on a Rates workspace (note 36): two applications each have a `price`; `new` is the kernel's, for every one.
        Assert.Equal("""{"words":["price"],"arguments":[],"app":"rates"}""", typed["rates.workspace.price"]);
        Assert.Equal("""{"words":["add line"],"arguments":[{"name":"product","kind":"text","required":true}],"app":"rates"}""", typed["rates.line.add"]);
        Assert.Equal("""{"words":["new"],"arguments":[{"name":"product","kind":"text","required":true},{"name":"terms","kind":"text","required":false}]}""", typed["entity.create"]);
        Assert.Equal(["rates.workspace.price", "rates.live.start", "rates.live.stop", "rates.frame.capture", "rates.line.add"],
            capabilities.Select(c => c!["capabilityId"]!.GetValue<string>()).Where(id => id.StartsWith("rates.", StringComparison.Ordinal) && typed.ContainsKey(id)));
        Assert.True(capabilities.Count > typed.Count, "The catalog serves capabilities no line names, as before.");
    }

    async Task<(HttpStatusCode Status, JsonObject Body)> Preview(string line, string id, long? revision = null, HttpClient? by = null, JsonObject? view = null)
    {
        var request = new JsonObject { ["line"] = line, ["workspaceId"] = id };
        if (revision is { } read) request["revision"] = read;
        if (view is not null) request["view"] = view;
        var response = await (by ?? client).PostAsJsonAsync("/api/commands/preview", request);
        return (response.StatusCode, (await response.Content.ReadFromJsonAsync<JsonObject>())!);
    }

    [Fact] // ADR-0016 note 36: `new` is the kernel's and is read on every workspace, with the products its own application describes: Rates' swap is none of the pricer's
    public async Task New_on_a_workspace_names_the_products_its_own_application_describes()
    {
        var ws = await Create();

        var (status, read) = await Preview("new irs 5y", ws["workspaceId"]!.GetValue<string>());

        Assert.Equal((HttpStatusCode.OK, "UnknownProduct", "irs is no product new makes here: leg."),
            (status, read["refused"]!["code"]!.GetValue<string>(), read["refused"]!["sentence"]!.GetValue<string>()));
    }

    [Fact] // ADR-0016 §5, note 11: the preview reads a line for a workspace and writes nothing; what it answers is what Enter invokes
    public async Task The_preview_answers_the_command_its_inputs_its_rows_and_what_would_change_and_writes_nothing()
    {
        var ws = await Create();
        var id = ws["workspaceId"]!.GetValue<string>();
        var revision = ws["revision"]!.GetValue<long>();

        var (status, preview) = await Preview("set CSA-A threshold 5mm", id);
        var unchanged = (await client.GetFromJsonAsync<JsonObject>($"/api/xva/workspaces/{id}"))!;

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(revision, preview["revision"]!.GetValue<long>());
        Assert.Equal("""{"id":"entity.set","version":"1.0.0"}""", preview["command"]!.ToJsonString());
        Assert.Equal(new JsonObject { ["request"] = new JsonObject { ["workspaceId"] = id, ["revision"] = revision, ["rows"] = "CSA-A", ["set"] = "threshold 5mm" } }.ToJsonString(),
            preview["inputs"]!.ToJsonString());
        Assert.Equal("""[{"name":"rows","text":"CSA-A","from":"typed"},{"name":"set","text":"threshold 5mm","from":"typed"}]""", preview["arguments"]!.ToJsonString());
        // No screen was said to be behind the line, and the answer says none.
        Assert.Null(preview["view"]);
        Assert.Equal("""{"table":"xva.csas","title":"csas, 1 row","count":1,"first":["CSA-A"]}""", preview["rows"]!.ToJsonString());
        Assert.Matches("^threshold: .+ to 5mm$", preview["would"]!.GetValue<string>());
        Assert.Null(preview["refused"]);
        Assert.Equal("[]", preview["complete"]!.ToJsonString());
        Assert.Equal((revision, Threshold(ws)), (unchanged["revision"]!.GetValue<long>(), Threshold(unchanged)));

        // Enter: the command the preview named, with the inputs it answered, through the page's door.
        var ran = await client.PostAsJsonAsync($"/api/commands/{preview["command"]!["id"]!.GetValue<string>()}/{preview["command"]!["version"]!.GetValue<string>()}",
            new { inputs = preview["inputs"], idempotencyKey = Guid.NewGuid().ToString() });
        Assert.Equal("Set CSA-A threshold to 5mm.", (await Ok(ran))["said"]!.GetValue<string>());
        Assert.Equal(5_000_000m, Threshold((await client.GetFromJsonAsync<JsonObject>($"/api/xva/workspaces/{id}"))!));
    }

    [Fact] // ADR-0016 §2, §9, notes 16 to 19: the preview takes the view of the screen behind the line, answers it in a sentence, and what it answers runs as rows by key
    public async Task The_preview_takes_a_view_says_it_and_answers_the_rows_of_the_screen_by_key()
    {
        var ws = await Create();
        var id = ws["workspaceId"]!.GetValue<string>();
        JsonObject View() => new() { ["table"] = "xva.csas", ["keys"] = new JsonArray("CSA-A"), ["focus"] = new JsonObject { ["field"] = "counterpartyThreshold", ["key"] = "CSA-A" } };

        var (_, opened) = await Preview("", id, view: View());
        var (_, preview) = await Preview("set selected threshold 5mm", id, view: View());
        var (invalid, body) = await Preview("set selected threshold 5mm", id, view: new JsonObject { ["table"] = "xva.tides" });
        // A page that filters the rows it holds names those it shows, and `shown` is those.
        var filter = View();
        filter["shown"] = new JsonArray("CSA-A");
        var (_, left) = await Preview("set shown threshold 5mm", id, view: filter);

        Assert.Equal(("csas, 1 selected, CSA-A · threshold", "UnknownCommand"), (opened["view"]!.GetValue<string>(), opened["refused"]!["code"]!.GetValue<string>()));
        Assert.Equal("csas, 1 selected, CSA-A · threshold", preview["view"]!.GetValue<string>());
        Assert.Equal(("csas", "CSA-A", "threshold 5mm"), (preview["inputs"]!["request"]!["table"]!.GetValue<string>(), preview["inputs"]!["request"]!["rows"]!.GetValue<string>(),
            preview["inputs"]!["request"]!["set"]!.GetValue<string>()));
        Assert.Equal("""{"name":"rows","text":"selected","from":"typed"}""", preview["arguments"]![0]!.ToJsonString());
        Assert.Equal(("csas, filtered to 1, 1 selected, CSA-A · threshold", false, "CSA-A"),
            (left["view"]!.GetValue<string>(), left["viewMoved"]!.GetValue<bool>(), left["inputs"]!["request"]!["rows"]!.GetValue<string>()));
        // A view of what the workspace has not is a page's fault, and is refused whole.
        Assert.Equal((HttpStatusCode.BadRequest, "InvalidView"), (invalid, body["code"]!.GetValue<string>()));

        var ran = await client.PostAsJsonAsync($"/api/commands/{preview["command"]!["id"]!.GetValue<string>()}/{preview["command"]!["version"]!.GetValue<string>()}",
            new { inputs = preview["inputs"], idempotencyKey = Guid.NewGuid().ToString() });
        Assert.Equal("Set CSA-A threshold to 5mm.", (await Ok(ran))["said"]!.GetValue<string>());
    }

    [Fact] // ADR-0016 §4, note 25 of step 2: a workspace's tables are served with their actions, the pricer's trades with After and Before in and out, and another user's are not
    public async Task A_workspaces_tables_are_served_with_their_actions()
    {
        var id = (await Create())["workspaceId"]!.GetValue<string>();
        using var other = factory.WithWebHostBuilder(b => b.UseSetting("Qube:DemoUser", "ben"));

        var tables = (await client.GetFromJsonAsync<JsonArray>($"/api/workspaces/{id}/tables"))!;
        var notOwner = await other.CreateClient().GetAsync($"/api/workspaces/{id}/tables");
        var unknown = await client.GetAsync("/api/workspaces/W-none/tables");

        string Actions(string table) => string.Join(", ", tables.Single(t => t!["tableId"]!.GetValue<string>() == table)!["actions"]!.AsArray().Select(a => $"{a!["title"]!.GetValue<string>()}={a["line"]!.GetValue<string>()}"));
        Assert.Equal("Reset this cell=reset, Take out of After=set selected after off, Put into After=set selected after on, Take out of Before=set selected before off, Put into Before=set selected before on",
            Actions("xva.trades"));
        Assert.Equal("Reset this cell=reset", Actions("xva.csas"));
        Assert.Equal("Trades", tables.Single(t => t!["tableId"]!.GetValue<string>() == "xva.trades")!["title"]!.GetValue<string>());
        Assert.Equal((HttpStatusCode.Forbidden, HttpStatusCode.NotFound), (notOwner.StatusCode, unknown.StatusCode));
    }

    [Fact] // ADR-0016 §3, §9, note 21: the catalog lists find among the verbs of the screen and among no capabilities, and the preview answers the place of a row as the grid's reader orders it
    public async Task The_catalog_lists_find_apart_from_the_capabilities_and_the_preview_answers_its_place()
    {
        var catalog = (await client.GetFromJsonAsync<JsonObject>("/api/catalog"))!;
        var ws = await Create();
        var id = ws["workspaceId"]!.GetValue<string>();
        var sorts = new JsonArray(new JsonObject { ["column"] = "notional", ["direction"] = "descending" });
        var page = (await (await client.PostAsJsonAsync($"/api/xva/workspaces/{id}/rows", new { start = 0, count = 200, sorts, grid = "existing" })).Content.ReadFromJsonAsync<JsonObject>())!;
        var keys = page["rows"]!.AsArray().Select(r => r!["tradeId"]!.GetValue<string>()).ToList();
        var view = new JsonObject
        {
            ["table"] = "xva.trades", ["subset"] = "existing", ["sorts"] = new JsonArray(new JsonObject { ["column"] = "notional", ["descending"] = true }),
            ["order"] = page["orderToken"]!.GetValue<string>(), ["focus"] = new JsonObject { ["field"] = "pv", ["position"] = 0 },
        };

        // The end of an id that one row of the grid has, and a field by its word.
        var (_, found) = await Preview($"find {keys[^1][^6..]} csa", id, view: view.DeepClone().AsObject());
        var (_, row) = await Preview($"find {keys[1]}", id, view: view.DeepClone().AsObject());

        Assert.Equal("""{"verbId":"find","title":"Put the keyboard on a row of the grid on screen, or on one of its cells","command":{"words":["find"],"arguments":[{"name":"row","kind":"rows","required":true},{"name":"field","kind":"field","required":false}]}}""",
            catalog["viewVerbs"]![0]!.ToJsonString());
        Assert.DoesNotContain(catalog["capabilities"]!.AsArray(), c => c!["command"]?["words"]!.AsArray().Any(w => w!.GetValue<string>() == "find") == true);
        Same(new JsonObject
        {
            ["kind"] = "place", ["table"] = "xva.trades", ["subset"] = "existing", ["key"] = keys[^1], ["position"] = keys.Count - 1, ["column"] = "csa",
            ["said"] = $"The keyboard is on {keys[^1]} · csa in trades (existing).",
        }, found["asks"]);
        Assert.Null(found["command"]);
        Assert.Null(found["inputs"]);
        Assert.Equal($"The keyboard goes to {keys[^1]} · csa.", found["would"]!.GetValue<string>());
        // With no field the Focus keeps its column, which the view names as the grid does, a field of the table or not.
        Assert.Equal((keys[1], 1, "pv"), (row["asks"]!["key"]!.GetValue<string>(), row["asks"]!["position"]!.GetValue<int>(), row["asks"]!["column"]!.GetValue<string>()));
    }

    [Fact] // ADR-0016 note 37: a page asks which rows a paged grid's selection names; an order that has moved is a conflict, a table the workspace has not a bad request
    public async Task A_page_reads_the_keys_a_paged_grids_selection_names()
    {
        var ws = await Create();
        var id = ws["workspaceId"]!.GetValue<string>();
        var sorts = new JsonArray(new JsonObject { ["column"] = "notional", ["direction"] = "descending" });
        var page = (await (await client.PostAsJsonAsync($"/api/xva/workspaces/{id}/rows", new { start = 0, count = 200, sorts, grid = "existing" })).Content.ReadFromJsonAsync<JsonObject>())!;
        var keys = page["rows"]!.AsArray().Select(r => r!["tradeId"]!.GetValue<string>()).ToList();
        JsonObject View(string order) => new()
        {
            ["table"] = "xva.trades", ["subset"] = "existing", ["sorts"] = new JsonArray(new JsonObject { ["column"] = "notional", ["descending"] = true }),
            ["order"] = order, ["ranges"] = new JsonArray(new JsonObject { ["start"] = 1, ["count"] = 2 }, new JsonObject { ["start"] = 5, ["count"] = 1 }),
        };
        async Task<(HttpStatusCode Status, JsonObject Body)> Selection(JsonObject view, HttpClient? by = null)
        {
            var response = await (by ?? client).PostAsJsonAsync($"/api/workspaces/{id}/selection", new JsonObject { ["view"] = view });
            return (response.StatusCode, (await response.Content.ReadFromJsonAsync<JsonObject>())!);
        }

        var (status, named) = await Selection(View(page["orderToken"]!.GetValue<string>()));
        var (moved, why) = await Selection(View("an earlier order"));
        var (invalid, _) = await Selection(new JsonObject { ["table"] = "xva.tides", ["keys"] = new JsonArray("T1") });
        // Positions in a table no reader pages are the page's fault, as the preview answers them: no retry reads them.
        var (unordered, fault) = await Selection(new JsonObject { ["table"] = "xva.csas", ["ranges"] = new JsonArray(new JsonObject { ["start"] = 0, ["count"] = 1 }) });

        Assert.Equal((HttpStatusCode.OK, new JsonObject { ["table"] = "xva.trades", ["keys"] = new JsonArray(keys[1], keys[2], keys[5]) }.ToJsonString()), (status, named.ToJsonString()));
        Assert.Equal((HttpStatusCode.Conflict, "ViewMoved", true), (moved, why["code"]!.GetValue<string>(), why["retryable"]!.GetValue<bool>()));
        Assert.Equal(HttpStatusCode.BadRequest, invalid);
        Assert.Equal((HttpStatusCode.BadRequest, "InvalidView", false), (unordered, fault["code"]!.GetValue<string>(), fault["retryable"]?.GetValue<bool>() ?? false));
    }

    [Fact] // ADR-0016 §9, note 23: on the CSA grid a table left out is the CSAs, a subset of the trades alone is still the trades, and with no screen the trades as before
    public async Task On_the_csa_grid_rows_said_with_no_table_are_csas_and_the_request_names_them()
    {
        var ws = await Create();
        var id = ws["workspaceId"]!.GetValue<string>();
        JsonObject Csas() => new() { ["table"] = "xva.csas" };
        static (string? Table, string? Rows, string Title) Asked(JsonObject preview) =>
            (preview["inputs"]!["request"]!["table"]?.GetValue<string>(), preview["inputs"]!["request"]!["rows"]?.GetValue<string>(), preview["rows"]!["title"]!.GetValue<string>());

        var (_, where) = await Preview("set where ccy=USD threshold 0", id, view: Csas());
        var (_, existing) = await Preview("set existing a off", id, view: Csas());
        var (_, none) = await Preview("set where ccy=USD a off", id);
        var (_, fields) = await Preview("set where ", id, view: Csas());

        Assert.Equal(("csas", "where ccy=USD", "csas, 1 row"), Asked(where));
        Assert.Equal(["CSA-B"], where["rows"]!["first"]!.AsArray().Select(k => k!.GetValue<string>()));
        Assert.Equal((null, "existing", "xva.trades"), (Asked(existing).Table, Asked(existing).Rows, existing["rows"]!["table"]!.GetValue<string>()));
        Assert.Equal((null, "xva.trades"), (Asked(none).Table, none["rows"]!["table"]!.GetValue<string>()));
        // After where, the fields offered are the CSAs': a trade's notional is none of them.
        var offered = fields["complete"]!.AsArray().Select(c => c!["value"]!.GetValue<string>()).ToList();
        Assert.Contains("threshold", offered);
        Assert.DoesNotContain("notional", offered);

        var ran = await client.PostAsJsonAsync($"/api/commands/{where["command"]!["id"]!.GetValue<string>()}/{where["command"]!["version"]!.GetValue<string>()}",
            new { inputs = where["inputs"], idempotencyKey = Guid.NewGuid().ToString() });
        Assert.Equal("Set CSA-B threshold to 0.", (await Ok(ran))["said"]!.GetValue<string>());
    }

    [Fact] // ADR-0016 §5, §6: a line that cannot be read is an answer, with the word and its place; a workspace that cannot be read is a status
    public async Task The_preview_answers_a_line_it_cannot_read_and_what_could_follow_and_refuses_a_workspace_by_status()
    {
        var ws = await Create();
        var id = ws["workspaceId"]!.GetValue<string>();
        var revision = ws["revision"]!.GetValue<long>();
        await Edit(id, revision, "csa:CSA-A", "counterpartyThreshold", 5_000_000m);

        var (unread, refused) = await Preview("set CSA-A colour red", id, revision + 1);
        var (_, half) = await Preview("set CSA-A thr", id);
        var (_, undo) = await Preview("undo", id);
        var (moved, movedBody) = await Preview("undo", id, revision);
        var (missing, missingBody) = await Preview("undo", "ws-none");
        using var other = factory.WithWebHostBuilder(b => b.UseSetting("Qube:DemoUser", "ben"));
        var (notOwner, notOwnerBody) = await Preview("undo", id, by: other.CreateClient());

        Assert.Equal(HttpStatusCode.OK, unread);
        Assert.Equal(("UnknownField", "colour", 10, 16), (refused["refused"]!["code"]!.GetValue<string>(), refused["refused"]!["word"]!.GetValue<string>(),
            refused["refused"]!["start"]!.GetValue<int>(), refused["refused"]!["end"]!.GetValue<int>()));
        Assert.StartsWith("csas has no field colour; its fields are ", refused["refused"]!["sentence"]!.GetValue<string>());
        Assert.Contains("threshold", refused["refused"]!["candidates"]!.AsArray().Select(c => c!.GetValue<string>()));
        Assert.Equal("entity.set", refused["command"]!["id"]!.GetValue<string>());
        Assert.Null(refused["inputs"]);
        Assert.Equal("""[{"value":"threshold","start":10}]""", half["complete"]!.ToJsonString());
        // What undo would take back is the page's own edit, named as the screen names it, and when it was made.
        Assert.Equal("Takes back: set CSA-A threshold (you, 1 edit)", undo["would"]!.GetValue<string>());
        Assert.True(DateTimeOffset.TryParse(undo["at"]!.GetValue<string>(), out _));
        Assert.Equal((HttpStatusCode.Conflict, "RevisionMoved"), (moved, movedBody["code"]!.GetValue<string>()));
        Assert.Equal((HttpStatusCode.NotFound, "UnknownWorkspace"), (missing, missingBody["code"]!.GetValue<string>()));
        Assert.Equal((HttpStatusCode.Forbidden, "NotOwner"), (notOwner, notOwnerBody["code"]!.GetValue<string>()));
    }

    [Fact] // ADR-0016 §11, note 2; ADR-0010 §10: the selection a new workspace takes from its owner's last is its making's
    public async Task The_selection_a_new_workspace_takes_is_not_a_command_to_take_back_and_one_taken_back_is_not_taken()
    {
        var ws = await Create();
        var id = ws["workspaceId"]!.GetValue<string>();
        var others = Selected(ws).SequenceEqual(["fva"]) ? new[] { "cva" } : ["fva"];
        var chosen = await Ok(await Command("xva.measures", new JsonObject { ["workspaceId"] = id, ["revision"] = ws["revision"]!.GetValue<long>(), ["measures"] = string.Join(' ', others) }));
        var chosenAt = chosen["revision"]!.GetValue<long>();

        var taking = await Create();
        var nothing = await Refused(await Command("workspace.undo", new JsonObject { ["workspaceId"] = taking["workspaceId"]!.GetValue<string>(), ["revision"] = taking["revision"]!.GetValue<long>() }));
        // Taken back in the first workspace, the selection is no longer the owner's last: the next takes what stands there.
        var undone = await Ok(await Command("workspace.undo", new JsonObject { ["workspaceId"] = id, ["revision"] = chosenAt }));
        var after = await Create();

        Assert.Equal(others, Selected(taking));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "NothingToUndo"), (nothing.Status, nothing.Code));
        Assert.Equal($"Took back: measures {others[0]}. redo puts it back.", undone["said"]!.GetValue<string>());
        Assert.Equal(Selected(ws), Selected(after));
    }

    static string[] Selected(JsonObject view) => [.. view["selectedMeasures"]!.AsArray().Select(m => m!.GetValue<string>())];

    [Fact] // ADR-0016 notes 39 to 41: sort and filter are verbs of the screen; the pricer's page reads a trade grid under conditions as a line reads `shown` under them
    public async Task The_pricers_page_reads_a_trade_grid_under_conditions_as_a_line_reads_shown()
    {
        var catalog = (await client.GetFromJsonAsync<JsonObject>("/api/catalog"))!;
        var ws = await Create();
        var id = ws["workspaceId"]!.GetValue<string>();
        async Task<(HttpStatusCode Status, JsonObject Body)> Page(string? where)
        {
            var sorts = new JsonArray(new JsonObject { ["column"] = "notional", ["direction"] = "descending" });
            var response = await client.PostAsJsonAsync($"/api/xva/workspaces/{id}/rows", new { start = 0, count = 200, sorts, grid = "existing", where });
            return (response.StatusCode, (await response.Content.ReadFromJsonAsync<JsonObject>())!);
        }
        static List<string> Keys(JsonObject page) => [.. page["rows"]!.AsArray().Select(r => r!["tradeId"]!.GetValue<string>())];

        var (_, whole) = await Page(null);
        // A currency some rows have and others have not, read from the page itself: the fixture's rows are not spelled here.
        var currency = whole["rows"]!.AsArray().Select(r => r!["currency"]!.GetValue<string>()).GroupBy(c => c).OrderBy(g => g.Count()).First().Key;
        List<string> expected = [.. whole["rows"]!.AsArray().Where(r => r!["currency"]!.GetValue<string>() == currency).Select(r => r!["tradeId"]!.GetValue<string>())];
        var where = $"ccy={currency.ToLowerInvariant()}";
        var (status, left) = await Page(where);
        var (unread, why) = await Page("colour=red");
        JsonObject View(bool filtered) => new()
        {
            ["table"] = "xva.trades", ["subset"] = "existing", ["sorts"] = new JsonArray(new JsonObject { ["column"] = "notional", ["descending"] = true }),
            ["order"] = (filtered ? left : whole)["orderToken"]!.GetValue<string>(), ["where"] = filtered ? where : null,
        };

        var (_, filter) = await Preview($"filter where {where}", id, view: View(false));
        var (_, sort) = await Preview("sort by maturity, notional desc", id, view: View(false));
        var (_, shown) = await Preview("set shown a off", id, view: View(true));
        var (_, found) = await Preview($"find {expected[^1]}", id, view: View(true));

        Assert.InRange(expected.Count, 1, Keys(whole).Count - 1);
        Assert.Equal((HttpStatusCode.OK, string.Join(", ", expected), expected.Count, Keys(whole).Count),
            (status, string.Join(", ", Keys(left)), left["totalCount"]!.GetValue<int>(), left["of"]!.GetValue<int>()));
        Assert.Null(whole["of"]);
        Assert.NotEqual(whole["orderToken"]!.GetValue<string>(), left["orderToken"]!.GetValue<string>());
        // Conditions the server cannot read are refused, never answered as every row.
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "FilterUnread"), (unread, why["code"]!.GetValue<string>()));
        Same(new JsonObject
        {
            ["kind"] = "filter", ["table"] = "xva.trades", ["subset"] = "existing", ["where"] = where,
            ["said"] = $"Showing {expected.Count} of {Keys(whole).Count} rows of trades (existing) where ccy = {currency}.",
        }, filter["asks"]);
        Assert.Equal((null, null), (filter["command"], filter["inputs"]));
        Same(new JsonObject
        {
            ["kind"] = "sort", ["table"] = "xva.trades", ["subset"] = "existing",
            ["sorts"] = new JsonArray(new JsonObject { ["column"] = "maturity", ["descending"] = false }, new JsonObject { ["column"] = "notional", ["descending"] = true }),
            ["said"] = "Sorted trades (existing) by maturity, then notional, descending.",
        }, sort["asks"]);
        // The line reads `shown`, and a position, among the rows the page was answered.
        Assert.Equal(string.Join(", ", expected), shown["inputs"]!["request"]!["rows"]!.GetValue<string>());
        Assert.Equal($"trades (existing), filtered to {expected.Count}, nothing selected", shown["view"]!.GetValue<string>());
        Assert.Equal(expected.Count - 1, found["asks"]!["position"]!.GetValue<int>());
        Assert.Equal(["find", "sort", "filter", "select", "copy", "export", "open", "cancel"], catalog["viewVerbs"]!.AsArray().Select(v => v!["verbId"]!.GetValue<string>()));
        Assert.DoesNotContain(catalog["capabilities"]!.AsArray(), c => c!["command"]?["words"]!.AsArray().Any(w => w!.GetValue<string>() is "sort" or "filter") == true);
    }

    [Fact] // ADR-0016 note 41: a page that holds every row asks which rows its grid's conditions leave, at the revision it shows; conditions that cannot be read are refused
    public async Task A_page_reads_the_rows_a_held_grids_conditions_leave()
    {
        var ws = await Create();
        var id = ws["workspaceId"]!.GetValue<string>();
        async Task<(HttpStatusCode Status, JsonObject Body)> Shown(JsonObject view, HttpClient? by = null, string? workspace = null)
        {
            var response = await (by ?? client).PostAsJsonAsync($"/api/workspaces/{workspace ?? id}/shown", new JsonObject { ["view"] = view });
            return (response.StatusCode, (await response.Content.ReadFromJsonAsync<JsonObject>())!);
        }
        using var other = factory.WithWebHostBuilder(b => b.UseSetting("Qube:DemoUser", "ben"));

        var (status, usd) = await Shown(new JsonObject { ["table"] = "xva.csas", ["where"] = "ccy=USD" });
        var (_, every) = await Shown(new JsonObject { ["table"] = "xva.csas" });
        var (unread, why) = await Shown(new JsonObject { ["table"] = "xva.csas", ["where"] = "colour=red" });
        var (invalid, _) = await Shown(new JsonObject { ["table"] = "xva.tides", ["where"] = "ccy=USD" });
        var (notOwner, _) = await Shown(new JsonObject { ["table"] = "xva.csas" }, other.CreateClient());
        var (unknown, _) = await Shown(new JsonObject { ["table"] = "xva.csas" }, workspace: "ws-none");

        var csas = ws["csas"]!.AsArray().Count;
        Assert.Equal((HttpStatusCode.OK, new JsonObject { ["table"] = "xva.csas", ["revision"] = ws["revision"]!.GetValue<long>(), ["keys"] = new JsonArray("CSA-B"), ["of"] = csas }.ToJsonString()),
            (status, usd.ToJsonString()));
        Assert.Equal(csas, every["keys"]!.AsArray().Count);
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "FilterUnread", false), (unread, why["code"]!.GetValue<string>(), why["retryable"]!.GetValue<bool>()));
        Assert.Equal((HttpStatusCode.BadRequest, HttpStatusCode.Forbidden, HttpStatusCode.NotFound), (invalid, notOwner, unknown));
    }

    [Fact] // ADR-0016 notes 42 to 44: over the pricer's trades the preview answers a run to select by its position, and the content endpoint the cells of a line of copy and of export, at the revision read
    public async Task The_preview_answers_a_run_to_select_and_the_content_endpoint_the_cells_of_copy_and_export()
    {
        var ws = await Create();
        var id = ws["workspaceId"]!.GetValue<string>();
        var sorts = new JsonArray(new JsonObject { ["column"] = "notional", ["direction"] = "descending" });
        var page = (await (await client.PostAsJsonAsync($"/api/xva/workspaces/{id}/rows", new { start = 0, count = 200, sorts, grid = "existing" })).Content.ReadFromJsonAsync<JsonObject>())!;
        var rows = page["rows"]!.AsArray();
        var keys = rows.Select(r => r!["tradeId"]!.GetValue<string>()).ToList();
        JsonObject View() => new()
        {
            ["table"] = "xva.trades", ["subset"] = "existing", ["sorts"] = new JsonArray(new JsonObject { ["column"] = "notional", ["descending"] = true }),
            ["order"] = page["orderToken"]!.GetValue<string>(), ["ranges"] = new JsonArray(new JsonObject { ["start"] = 1, ["count"] = 2 }),
        };
        async Task<(HttpStatusCode Status, JsonObject Body)> Content(string line, long? revision = null, JsonObject? view = null, HttpClient? by = null)
        {
            var request = new JsonObject { ["line"] = line, ["workspaceId"] = id };
            if (revision is { } read) request["revision"] = read;
            if (view is not null) request["view"] = view;
            var response = await (by ?? client).PostAsJsonAsync("/api/commands/content", request);
            return (response.StatusCode, (await response.Content.ReadFromJsonAsync<JsonObject>())!);
        }
        using var other = factory.WithWebHostBuilder(b => b.UseSetting("Qube:DemoUser", "ben"));

        var (_, select) = await Preview($"select {keys[2]}, {keys[1]}", id, view: View());
        var (_, apart) = await Preview($"select {keys[0]}, {keys[2]}", id, view: View());
        var (_, copy) = await Preview("copy trade id, notional", id, view: View());
        var (copied, cells) = await Content("copy trade id, notional", copy["revision"]!.GetValue<long>(), View());
        var (_, export) = await Preview("export trades existing trade id, ccy, notional, rate", id);
        var (exported, file) = await Content("export trades existing trade id, ccy, notional, rate");
        var (unread, refused) = await Content("copy nothing");
        var (notOwner, _) = await Content("export trades", by: other.CreateClient());
        await Command("entity.set", new JsonObject { ["workspaceId"] = id, ["rows"] = keys[0], ["set"] = "csa=CSA-B" });
        var (moved, _) = await Content("copy trade id, notional", copy["revision"]!.GetValue<long>(), View());

        Same(new JsonObject
        {
            ["kind"] = "select", ["table"] = "xva.trades", ["subset"] = "existing", ["position"] = 1, ["count"] = 2,
            ["said"] = "Selected 2 rows of trades (existing).",
        }, select["asks"]);
        Assert.Equal((null, null), (select["command"], select["inputs"]));
        Assert.Equal("SeveralRuns", apart["refused"]!["code"]!.GetValue<string>());
        // Rows left out are the Selection, in the grid's order; a notional is written in full.
        string Notional(int at) => rows[at]!["notional"]!.GetValue<decimal>().ToString("0.############################", System.Globalization.CultureInfo.InvariantCulture);
        Same(new JsonObject { ["kind"] = "copy", ["said"] = "Copied 2 rows of trades, 2 fields: trade id, notional." }, copy["asks"]);
        Assert.Equal((HttpStatusCode.OK, $"{keys[1]}\t{Notional(1)}\r\n{keys[2]}\t{Notional(2)}"), (copied, cells["text"]!.GetValue<string>()));
        Assert.Equal(copy["revision"]!.GetValue<long>(), cells["revision"]!.GetValue<long>());
        Assert.Null(cells["refused"]);
        Assert.Equal("trades.csv", export["asks"]!["fileName"]!.GetValue<string>());
        var lines = file["text"]!.GetValue<string>().Split("\r\n");
        Assert.Equal((HttpStatusCode.OK, "\uFEFFtrade id,ccy,notional,rate", keys.Count + 2, ""), (exported, lines[0], lines.Length, lines[^1]));
        Assert.All(lines[1..^1], line => Assert.Matches(@"^[A-Z0-9-]+,[A-Z]{3},-?[0-9.]+,(-?[0-9.]+%)?$", line));
        // A line that cannot be read is an answer, as a preview's is; another user's workspace and one that has moved are refused.
        Assert.Equal((HttpStatusCode.OK, "UnknownRow", null), (unread, refused["refused"]!["code"]!.GetValue<string>(), refused["text"]));
        Assert.Equal((HttpStatusCode.Forbidden, HttpStatusCode.Conflict), (notOwner, moved));
    }

    [Fact] // ADR-0016 §10, note 55: the catalog says which commands and verbs are read while a draft is open, so that a line lists the rest as unavailable, with that reason
    public async Task The_catalog_says_which_commands_are_read_while_a_draft_is_open()
    {
        var catalog = (await client.GetFromJsonAsync<JsonObject>("/api/catalog"))!;

        Assert.Equal(["cancel", "draft.apply", "entity.create", "entity.remove", "entity.reset", "entity.set"],
            catalog["draftCommands"]!.AsArray().Select(c => c!.GetValue<string>()).Order(StringComparer.Ordinal));
    }

    [Fact] // ADR-0016 note 55: the draft travels in the preview's view: the host reads it from JSON, answers the line the draft gains as an ask and writes nothing, and apply is invoked as any command
    public async Task A_draft_travels_in_the_previews_view_and_apply_is_invoked_as_any_command()
    {
        var ws = await Create();
        var (id, revision) = (ws["workspaceId"]!.GetValue<string>(), ws["revision"]!.GetValue<long>());
        JsonObject Dialog(params string[] lines) => new()
        {
            ["table"] = "xva.csas",
            ["draft"] = new JsonObject { ["table"] = "xva.csas", ["key"] = "CSA-B", ["revision"] = revision, ["lines"] = new JsonArray([.. lines.Select(l => (JsonNode)l)]) },
        };

        var (_, gained) = await Preview("set currency JPY", id, view: Dialog());
        var (_, other) = await Preview("undo", id, view: Dialog());
        var (_, apply) = await Preview("apply", id, view: Dialog("set csas CSA-B currency JPY"));
        var unwritten = (await client.GetFromJsonAsync<JsonObject>($"/api/xva/workspaces/{id}"))!["revision"]!.GetValue<long>();
        var applied = await Ok(await Command("draft.apply", apply["inputs"]!["request"]!.DeepClone().AsObject()));

        Same(new JsonObject { ["kind"] = "draft", ["line"] = "set csas CSA-B currency JPY", ["said"] = "Added to the draft of CSA-B: set csas CSA-B currency JPY." }, gained["asks"]);
        Assert.Equal((null, "Editing CSA-B: nothing pending."), (gained["command"], gained["view"]!.GetValue<string>()));
        Assert.Equal("DraftOpen", other["refused"]!["code"]!.GetValue<string>());
        Assert.Equal(("""{"id":"draft.apply","version":"1.0.0"}""", "Editing CSA-B: 1 line pending."), (apply["command"]!.ToJsonString(), apply["view"]!.GetValue<string>()));
        Assert.Equal(revision, unwritten);
        Assert.Equal(("Applied 1 line to CSA-B.", revision + 1, "xva.csas", "CSA-B"),
            (applied["said"]!.GetValue<string>(), applied["revision"]!.GetValue<long>(), applied["table"]!.GetValue<string>(), applied["key"]!.GetValue<string>()));
    }

    [Fact] // ADR-0016 note 54: a draft's form is read through the host, which writes nothing; a draft the workspace has left is a conflict, and a row it has not is not found
    public async Task A_drafts_form_is_read_through_the_host()
    {
        var ws = await Create();
        var (id, revision) = (ws["workspaceId"]!.GetValue<string>(), ws["revision"]!.GetValue<long>());
        Task<HttpResponseMessage> Draft(string key, long at, params string[] lines) =>
            client.PostAsJsonAsync($"/api/workspaces/{id}/draft", new { table = "xva.csas", key, revision = at, lines });

        var read = await Draft("CSA-B", revision, "set csas CSA-B currency JPY");
        var form = (await read.Content.ReadFromJsonAsync<JsonObject>())!;
        var left = await Draft("CSA-B", revision - 1);
        var unknown = await Draft("CSA-Z", revision);
        var after = (await (await client.GetAsync($"/api/xva/workspaces/{id}")).Content.ReadFromJsonAsync<JsonObject>())!;

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var currency = form["fields"]!.AsArray().Single(f => f!["field"]!["name"]!.GetValue<string>() == "currency")!;
        Assert.Equal(("JPY", true, "Terms"), (currency["written"]!.GetValue<string>(), currency["changed"]!.GetValue<bool>(), currency["field"]!["group"]!.GetValue<string>()));
        Assert.Equal("Set CSA-B currency to JPY.", form["lines"]![0]!["said"]!.GetValue<string>());
        Assert.Equal((HttpStatusCode.Conflict, "RevisionMoved"), (left.StatusCode, (await left.Content.ReadFromJsonAsync<JsonObject>())!["code"]!.GetValue<string>()));
        Assert.Equal((HttpStatusCode.NotFound, "UnknownRow"), (unknown.StatusCode, (await unknown.Content.ReadFromJsonAsync<JsonObject>())!["code"]!.GetValue<string>()));
        Assert.Equal(revision, after["revision"]!.GetValue<long>());
    }
}
