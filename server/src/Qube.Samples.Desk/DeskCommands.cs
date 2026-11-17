using System.Text.Json.Nodes;
using Qube.Composition.Application;
using Qube.Composition.Contracts;

namespace Qube.Samples.Desk;

public static class DeskContracts
{
    public const string Version = "1.0.0";
    public const string NewWorkspace = "desk.workspace-new";
    public const string Workspace = "desk.workspace";
}

/// <summary>
/// <c>desk.workspace.create</c> (ADR-0019 §2, ADR-0005): a Desk workspace of the caller's on one result set. Its answer
/// says which book it is: the engine and its commit, the input set and the valuation date.
/// </summary>
public sealed class DeskWorkspaceCreate(Func<DeskWorkspaces> workspaces) : ICapabilityHandler
{
    public const string Id = "desk.workspace.create";

    public CapabilityDescriptor Descriptor { get; } = new(Id, DeskContracts.Version, "Create a Desk workspace on a result set of the engine",
        [new("request", DeskContracts.NewWorkspace, DeskContracts.Version, Cardinality.One)],
        [new("workspace", DeskContracts.Workspace, DeskContracts.Version, Cardinality.One)],
        ExecutionKind.RequestResponse, [CapabilityEffect.EditWorkspace]);

    public Task<IReadOnlyDictionary<string, JsonNode?>> InvokeAsync(CapabilityInvocation invocation, CancellationToken cancellationToken)
    {
        var caller = invocation.Caller ?? throw new CapabilityFailure("NoCaller", "A command is invoked for a caller, through the command gateway.");
        var request = invocation.Inputs["request"] ?? throw new CapabilityFailure("InvalidInput", "The command takes a request.");
        try
        {
            var state = workspaces().Create(caller.UserId, CommandRequests.Optional(request, "resultSet"), caller.Origin);
            var workspace = (JsonObject)state.Context.DeepClone();
            workspace["workspaceId"] = state.WorkspaceId;
            workspace["revision"] = state.Revision;
            return Task.FromResult<IReadOnlyDictionary<string, JsonNode?>>(new Dictionary<string, JsonNode?> { ["workspace"] = workspace });
        }
        catch (DeskResultsRefused refused)
        {
            throw new CapabilityFailure(refused.Code.ToString(), refused.Message);
        }
    }
}

/// <summary>Registers the Desk's commands and their contracts (ADR-0019 §5): nothing of it is in the kernel.</summary>
public sealed class DeskModule(Func<DeskWorkspaces> workspaces) : IQubeModule
{
    public void Register(CompositionRegistryBuilder builder)
    {
        builder.AddCapability(new DeskWorkspaceCreate(workspaces));

        static JsonObject Text(string title, string? description = null)
        {
            var text = new JsonObject { ["type"] = "string", ["title"] = title };
            if (description is not null) text["description"] = description;
            return text;
        }
        void Contract(string id, string title, JsonObject properties, params string[] required) =>
            builder.AddContract(new ContractDefinition(id, DeskContracts.Version, title, new JsonObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = new JsonArray([.. required.Select(r => JsonValue.Create(r))]),
                ["additionalProperties"] = false,
            }));

        Contract(DeskContracts.NewWorkspace, "New Desk workspace", new JsonObject
        {
            ["resultSet"] = Text("Result set", "The result set to read the book from. Left out, the configured one; refused where none is configured."),
        });
        Contract(DeskContracts.Workspace, "Desk workspace", new JsonObject
        {
            ["workspaceId"] = Text("Workspace"),
            ["revision"] = new JsonObject { ["type"] = "integer", ["title"] = "Revision" },
            ["resultSet"] = Text("Result set"),
            ["engine"] = Text("Engine"),
            ["engineCommit"] = Text("Engine commit"),
            ["inputSet"] = Text("Input set"),
            ["valuationDate"] = new JsonObject { ["type"] = "string", ["format"] = "date", ["title"] = "Valuation date" },
        }, "workspaceId", "revision", "resultSet", "engine", "engineCommit", "inputSet", "valuationDate");
    }
}
