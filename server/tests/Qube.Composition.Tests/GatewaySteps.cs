using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Qube.Composition.Tests;

/// <summary>The command gateway as the tests over HTTP invoke it (ADR-0016 §5).</summary>
static class GatewaySteps
{
    /// <summary>
    /// <c>price</c> as the page's button and the line invoke it (ADR-0016 note 33): the workspace at the revision the
    /// view given shows, under the key of its submission.
    /// </summary>
    public static Task<HttpResponseMessage> PriceWorkspace(this HttpClient client, JsonObject workspace, string key) =>
        client.PostAsJsonAsync("/api/commands/xva.workspace.price/1.0.0", new
        {
            inputs = new JsonObject
            {
                ["request"] = new JsonObject { ["workspaceId"] = workspace["workspaceId"]!.GetValue<string>(), ["revision"] = workspace["revision"]!.GetValue<long>() },
            },
            idempotencyKey = key,
        });

    /// <summary>The Run a Price was accepted as; a Price that was refused fails the test with what the server said.</summary>
    public static async Task<string> RunOf(HttpResponseMessage accepted)
    {
        Assert.True(accepted.IsSuccessStatusCode, await accepted.Content.ReadAsStringAsync());
        return (await accepted.Content.ReadFromJsonAsync<JsonObject>())!["outputs"]!["answer"]!["runId"]!.GetValue<string>();
    }
}
