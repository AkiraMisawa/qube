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
        var capabilities = (await client.GetFromJsonAsync<JsonObject>("/api/catalog"))!["capabilities"]!.AsArray();
        var typed = capabilities.Where(c => c!["command"] is not null).ToDictionary(c => c!["capabilityId"]!.GetValue<string>(), c => c!["command"]!.ToJsonString());

        Assert.Equal(["entity.remove", "entity.reset", "entity.set", "workspace.redo", "workspace.undo"], typed.Keys.Order());
        Assert.Equal("""{"words":["set"],"arguments":[{"name":"rows","kind":"rows","required":false},{"name":"set","kind":"assignments","required":true}]}""", typed["entity.set"]);
        Assert.Equal("""{"words":["reset"],"arguments":[{"name":"rows","kind":"rows","required":false},{"name":"field","kind":"field","required":false}]}""", typed["entity.reset"]);
        Assert.Equal("""{"words":["remove"],"arguments":[{"name":"rows","kind":"rows","required":true}]}""", typed["entity.remove"]);
        Assert.Equal("""{"words":["undo"],"arguments":[]}""", typed["workspace.undo"]);
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

        Assert.Equal(("csas, 1 selected, CSA-A · threshold", "UnknownCommand"), (opened["view"]!.GetValue<string>(), opened["refused"]!["code"]!.GetValue<string>()));
        Assert.Equal("csas, 1 selected, CSA-A · threshold", preview["view"]!.GetValue<string>());
        Assert.Equal(("csas", "CSA-A", "threshold 5mm"), (preview["inputs"]!["request"]!["table"]!.GetValue<string>(), preview["inputs"]!["request"]!["rows"]!.GetValue<string>(),
            preview["inputs"]!["request"]!["set"]!.GetValue<string>()));
        Assert.Equal("""{"name":"rows","text":"selected","from":"typed"}""", preview["arguments"]![0]!.ToJsonString());
        // A view of what the workspace has not is a page's fault, and is refused whole.
        Assert.Equal((HttpStatusCode.BadRequest, "InvalidView"), (invalid, body["code"]!.GetValue<string>()));

        var ran = await client.PostAsJsonAsync($"/api/commands/{preview["command"]!["id"]!.GetValue<string>()}/{preview["command"]!["version"]!.GetValue<string>()}",
            new { inputs = preview["inputs"], idempotencyKey = Guid.NewGuid().ToString() });
        Assert.Equal("Set CSA-A threshold to 5mm.", (await Ok(ran))["said"]!.GetValue<string>());
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
        var chosen = await client.PostAsJsonAsync($"/api/xva/workspaces/{id}/measures", new { revision = ws["revision"]!.GetValue<long>(), measures = others });
        Assert.True(chosen.IsSuccessStatusCode, await chosen.Content.ReadAsStringAsync());
        var chosenAt = (await chosen.Content.ReadFromJsonAsync<JsonObject>())!["revision"]!.GetValue<long>();

        var taking = await Create();
        var nothing = await Refused(await Command("workspace.undo", new JsonObject { ["workspaceId"] = taking["workspaceId"]!.GetValue<string>(), ["revision"] = taking["revision"]!.GetValue<long>() }));
        // Taken back in the first workspace, the selection is no longer the owner's last: the next takes what stands there.
        var undone = await Ok(await Command("workspace.undo", new JsonObject { ["workspaceId"] = id, ["revision"] = chosenAt }));
        var after = await Create();

        Assert.Equal(others, Selected(taking));
        Assert.Equal((HttpStatusCode.UnprocessableEntity, "NothingToUndo"), (nothing.Status, nothing.Code));
        Assert.Equal("Took back: set the measures. redo puts it back.", undone["said"]!.GetValue<string>());
        Assert.Equal(Selected(ws), Selected(after));
    }

    static string[] Selected(JsonObject view) => [.. view["selectedMeasures"]!.AsArray().Select(m => m!.GetValue<string>())];
}
