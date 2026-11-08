using System.Text.Json;
using System.Text.Json.Nodes;
using Workbench.Composition.Application;
using Workbench.Composition.Application.Traces;
using Workbench.Composition.Contracts;

namespace Workbench.Composition.Tests;

// Reading a Run's trace through the kernel's service (ADR-0011 §5), over the garden's water budget: a request that
// writes one trace alone, as spot SIMM's does (ADR-0012), and the leaf rows of a term sorted by a column.
public partial class TraceTests
{
    /// <summary>A request that waters the garden and writes its trace alone, not in a set.</summary>
    sealed class GardenWater : ICapabilityHandler
    {
        public CapabilityDescriptor Descriptor { get; } = new("garden.water", "1.0", "Water the garden", [],
            [new("trace", CalculationTrace.ContractId, CalculationTrace.ContractVersion, Cardinality.One)], ExecutionKind.RequestResponse, [CapabilityEffect.Compute]);

        public Task<IReadOnlyDictionary<string, JsonNode?>> InvokeAsync(CapabilityInvocation invocation, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, JsonNode?>>(new Dictionary<string, JsonNode?>
            {
                ["trace"] = JsonSerializer.SerializeToNode(Garden(after: true), WireJson.Options),
            });
    }

    sealed class GardenModule : IWorkbenchModule
    {
        public void Register(CompositionRegistryBuilder builder)
        {
            builder.AddCapability(new GardenWater());
            builder.AddTraceCatalog(Catalog());
            builder.AddContract(new ContractDefinition("garden-input", "1.0", "Garden", new JsonObject
            {
                ["type"] = "object", ["properties"] = new JsonObject(), ["additionalProperties"] = false,
            }));
        }
    }

    static async Task<(TraceService Traces, RunRecord Run)> Watered()
    {
        var harness = new Harness(new GardenModule());
        var workflow = new WorkflowDefinition("workflow", CompositionValidator.SchemaVersion, "garden.water", "1.0", "Water the garden", "garden-input", "1.0",
            [new WorkflowStep("water", "garden.water", "1.0", [], new Dictionary<string, Binding>(), FailurePolicy.Stop)],
            new Dictionary<string, Binding> { ["trace"] = new OutputBinding("water", "trace") });
        var run = await harness.RunAsync(workflow, Harness.Context(), []);
        Assert.Equal(RunStatus.Succeeded, run.Status);
        return (new TraceService(harness.Runs, harness.Store, harness.Registry), run);
    }

    [Fact] // ADR-0011 §5 (DEFAULT 2026-11-08): a request that wrote one trace alone priced one side of one case, read as After of base
    public async Task A_trace_written_alone_is_read_as_after_of_the_base_case()
    {
        var (traces, run) = await Watered();

        var answer = traces.Read(new(run.RunId, "base", "base", "garden.water"), "akira", null);

        Assert.Equal(["after"], answer.Trace.Sides);
        Assert.Equal("total", answer.Term.Term.Id);
        Assert.Null(answer.Term.Term.Before);
        Assert.Null(answer.Term.Term.Diff);
        Assert.Equal(["north", "south", "east"], answer.Term.Children.Select(c => c.Id));
        Assert.Equal(11, answer.Trace.Roots.Count);
        // Another case, scenario or caller is refused by name, never answered with this one.
        Assert.Equal("UnknownTrace", Assert.Throws<TraceRefused>(() => traces.Read(new(run.RunId, "case-2", "base", "garden.water"), "akira", null)).Code);
        Assert.Equal("NotOwner", Assert.Throws<TraceRefused>(() => traces.Read(new(run.RunId, "base", "base", "garden.water"), "someone", null)).Code);
    }

    [Fact] // ADR-0011 §5: a term's leaf rows page and sort on the server, a row without the column last either way
    public async Task Leaf_rows_sort_by_a_column_with_the_rows_without_it_last()
    {
        var (traces, run) = await Watered();
        var address = new TraceAddress(run.RunId, "base", "base", "garden.water");

        string[] Plants(string? sort, bool descending, int limit = 10) =>
            [.. traces.Leaves(address, "akira", "south/area", 0, limit, sort, descending).Leaves.Rows.Select(r => r.Entity)];

        Assert.Equal(["bean", "mint"], Plants(null, false));
        Assert.Equal(["mint", "bean"], Plants("area", false));
        Assert.Equal(["bean", "mint"], Plants("area", true));
        Assert.Equal(["mint", "bean"], Plants("plant", true));
        Assert.Equal(["bean", "mint"], Plants("nothing", true));
        Assert.Equal(["mint"], Plants("area", false, 1));
        Assert.Equal(2, traces.Leaves(address, "akira", "south/area", 0, 1).Leaves.Total);
        Assert.Equal("InvalidRange", Assert.Throws<TraceRefused>(() => traces.Leaves(address, "akira", "south/area", 0, TraceService.MaxPage + 1)).Code);
    }
}
