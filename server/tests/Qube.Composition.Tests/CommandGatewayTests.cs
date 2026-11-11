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
