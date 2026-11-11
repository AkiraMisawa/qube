using System.Text.Json.Nodes;
using Qube.Composition.Contracts;

namespace Qube.Composition.Application.Workspaces;

/// <summary>
/// <c>workspace.undo</c> and <c>workspace.redo</c> (ADR-0016 §3, §11): the kernel's two commands over any workspace,
/// whichever application it belongs to. Each acts for the caller the server names, on a workspace that caller owns, at
/// the revision the request names or the one the workspace holds when it names none, and answers what it took back.
/// </summary>
public sealed class WorkspaceTakeBack(Func<WorkspaceService> workspaces, bool undo) : ICapabilityHandler
{
    public const string Request = "workspace.take-back-request";
    public const string Answer = "workspace.taken-back";
    public const string Version = "1.0.0";
    public const string Undo = "workspace.undo";
    public const string Redo = "workspace.redo";

    public CapabilityDescriptor Descriptor { get; } = new(undo ? Undo : Redo, Version,
        undo ? "Take back the workspace's latest command" : "Put back what the latest undo took back",
        [new("request", Request, Version, Cardinality.One)],
        [new("answer", Answer, Version, Cardinality.One)],
        ExecutionKind.RequestResponse, [CapabilityEffect.EditWorkspace]);

    public Task<IReadOnlyDictionary<string, JsonNode?>> InvokeAsync(CapabilityInvocation invocation, CancellationToken cancellationToken)
    {
        var caller = invocation.Caller ?? throw new CapabilityFailure("NoCaller", "A command is invoked for a caller, through the command gateway.");
        var request = invocation.Inputs["request"] ?? throw new CapabilityFailure("InvalidInput", "The command takes a request.");
        var workspaceId = request["workspaceId"]?.GetValue<string>() ?? throw new CapabilityFailure("InvalidInput", "The request has no workspaceId.");
        var service = workspaces();
        try
        {
            var state = WorkspaceCommands.Owned(service, workspaceId, caller);
            var revision = request["revision"]?.GetValue<long>() ?? state.Revision;
            var named = request["command"]?.GetValue<string>();
            var (next, told) = undo
                ? service.Undo(workspaceId, revision, caller.UserId, named, caller.Origin)
                : service.Redo(workspaceId, revision, caller.UserId, named, caller.Origin);
            var answer = new JsonObject
            {
                ["workspaceId"] = next.WorkspaceId,
                ["revision"] = next.Revision,
                // This command's own id: the one to name to take it back in turn.
                ["command"] = next.Log[^1].Command?.Id ?? throw new InvalidOperationException("An accepted command has an id."),
                ["said"] = undo ? $"Took back: {told.Said}. redo puts it back." : $"Put back: {told.Said}.",
                ["took"] = told.Command,
                ["by"] = told.User,
                ["at"] = told.At.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                ["edits"] = told.Edits,
            };
            if (told.Origin is { } origin) answer["origin"] = origin.ToString();
            return Task.FromResult<IReadOnlyDictionary<string, JsonNode?>>(new Dictionary<string, JsonNode?> { ["answer"] = answer });
        }
        catch (WorkspaceRefused refused)
        {
            throw new CapabilityFailure(refused.Code, refused.Message);
        }
    }
}

/// <summary>The kernel's own commands over a workspace, and their contracts. Knows no application.</summary>
public sealed class WorkspaceCommandsModule(Func<WorkspaceService> workspaces) : IQubeModule
{
    public void Register(CompositionRegistryBuilder builder)
    {
        builder.AddCapability(new WorkspaceTakeBack(workspaces, undo: true));
        builder.AddCapability(new WorkspaceTakeBack(workspaces, undo: false));
        builder.AddContract(new ContractDefinition(WorkspaceTakeBack.Request, WorkspaceTakeBack.Version, "Take back or put back a command", new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["workspaceId"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["title"] = "Workspace" },
                ["revision"] = new JsonObject { ["type"] = "integer", ["title"] = "Revision the command was read at; the workspace's own when left out" },
                ["command"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["title"] = "The command to take back, by its id; refused unless it is the latest" },
            },
            ["required"] = new JsonArray("workspaceId"),
            ["additionalProperties"] = false,
        }));
        builder.AddContract(new ContractDefinition(WorkspaceTakeBack.Answer, WorkspaceTakeBack.Version, "Command taken back or put back", new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["workspaceId"] = new JsonObject { ["type"] = "string", ["title"] = "Workspace" },
                ["revision"] = new JsonObject { ["type"] = "integer", ["title"] = "Revision" },
                ["command"] = new JsonObject { ["type"] = "string", ["title"] = "This command's id" },
                ["said"] = new JsonObject { ["type"] = "string", ["title"] = "What was done, in words" },
                ["took"] = new JsonObject { ["type"] = "string", ["title"] = "The command it acted on, by its id" },
                ["by"] = new JsonObject { ["type"] = "string", ["title"] = "Whose command that was" },
                ["at"] = new JsonObject { ["type"] = "string", ["title"] = "When that command ran" },
                ["edits"] = new JsonObject { ["type"] = "integer", ["title"] = "How many edits it held" },
                ["origin"] = new JsonObject { ["type"] = "string", ["title"] = "Where that command came from" },
            },
            ["required"] = new JsonArray("workspaceId", "revision", "command", "said", "took", "by", "at", "edits"),
            ["additionalProperties"] = false,
        }));
    }
}

public static class WorkspaceCommands
{
    /// <summary>The workspace a kernel command acts on: one that exists and that its caller owns (ADR-0005 §2).</summary>
    public static WorkspaceState Owned(WorkspaceService service, string workspaceId, Caller caller)
    {
        var state = service.Find(workspaceId) ?? throw new WorkspaceRefused("UnknownWorkspace", $"No workspace {workspaceId}.");
        return state.Owner == caller.UserId ? state : throw new WorkspaceRefused("NotOwner", $"Workspace {workspaceId} belongs to another user.");
    }
}
