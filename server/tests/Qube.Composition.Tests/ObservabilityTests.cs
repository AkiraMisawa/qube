using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Qube.Composition.Application;
using Qube.Composition.Contracts;

namespace Qube.Composition.Tests;

// ADR-0014: the browser's events reach the server's log within bounds, and a fault of the workbench's own is a
// failed Run that says so, never a Run left running.
public class ObservabilityTests(QubeFactory factory) : IClassFixture<QubeFactory>
{
    readonly HttpClient client = factory.CreateClient();

    static object Event(string level = "error", string kind = "uncaught", string message = "boom") =>
        new { level, kind, message, traceparent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01" };

    [Fact]
    public async Task A_page_s_events_are_taken_and_a_long_text_is_cut_rather_than_refused()
    {
        var response = await client.PostAsJsonAsync("/api/telemetry/client", new { session = "page-1", events = new[] { Event(message: new string('x', 10_000)) } });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task A_batch_too_large_or_of_an_unknown_level_is_refused()
    {
        var many = await client.PostAsJsonAsync("/api/telemetry/client", new { session = "page-1", events = Enumerable.Repeat(Event(), 51).ToArray() });
        var level = await client.PostAsJsonAsync("/api/telemetry/client", new { session = "page-1", events = new[] { Event(level: "fatal") } });
        Assert.Equal(HttpStatusCode.BadRequest, many.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, level.StatusCode);
    }

    [Fact]
    public async Task A_run_whose_capability_throws_ends_failed_instead_of_running_forever()
    {
        var harness = new Harness(new EchoModule(), new FaultModule());
        var run = await harness.RunAsync(harness.Workflow("workflow.test.fault"), Harness.Context(), new JsonObject { ["text"] = "x" });
        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Equal("InternalError", run.Error!.Code);
    }

    /// <summary>A capability with a bug: it throws what no capability should, rather than refusing.</summary>
    sealed class FaultModule : IQubeModule
    {
        sealed class Fault : ICapabilityHandler
        {
            public CapabilityDescriptor Descriptor { get; } = new("test.fault", "1.0.0", "Fault",
                [new("text", "test.text", "1.0.0", Cardinality.One)], [new("echo", "test.echo-table", "1.0.0", Cardinality.One)],
                ExecutionKind.RequestResponse, [CapabilityEffect.Compute]);

            public Task<IReadOnlyDictionary<string, JsonNode?>> InvokeAsync(CapabilityInvocation invocation, CancellationToken cancellationToken) =>
                throw new InvalidOperationException("A bug, not a refusal.");
        }

        public void Register(CompositionRegistryBuilder builder)
        {
            builder.AddCapability(new Fault());
            builder.AddWorkflow(Harness.Parse<WorkflowDefinition>("""
                {
                  "documentType": "workflow", "schemaVersion": "1.0.0", "definitionId": "workflow.test.fault", "version": "1.0.0",
                  "title": "Fault", "inputContractId": "test.echo-input", "inputContractVersion": "1.0.0",
                  "steps": [ { "stepId": "fault", "capabilityId": "test.fault", "capabilityVersion": "1.0.0", "dependsOn": [],
                               "bindings": { "text": { "kind": "input", "path": "/text" } }, "failurePolicy": "stop" } ],
                  "outputs": { "echo": { "kind": "output", "stepId": "fault", "port": "echo" } }
                }
                """));
        }
    }
}
