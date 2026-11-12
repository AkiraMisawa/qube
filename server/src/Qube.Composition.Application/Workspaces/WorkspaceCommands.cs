using System.Text.Json.Nodes;
using Qube.Composition.Contracts;

namespace Qube.Composition.Application.Workspaces;

/// <summary>
/// <c>workspace.undo</c> and <c>workspace.redo</c> (ADR-0016 §3, §11): the kernel's two commands over any workspace,
/// whichever application it belongs to. Each acts for the caller the server names, on a workspace that caller owns, and
/// answers what it took back. A request says what it means (§11): the revision its caller read, so a workspace that
/// moved refuses it, or the id of the command, which is refused unless it is the latest. The AI's names the command.
/// </summary>
public sealed class WorkspaceTakeBack(Func<WorkspaceService> workspaces, bool undo) : ICapabilityHandler, ILineCommand
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
        ExecutionKind.RequestResponse, [CapabilityEffect.EditWorkspace],
        new([undo ? "undo" : "redo"], []));

    /// <summary>
    /// The line names the command it would act on (ADR-0016 §5), so that Enter takes back the one that was shown or is
    /// refused, and says whose it was (§11): the reader's own, or one the AI or a saved sequence ran for them (note 62
    /// of step 6), which they did not type.
    /// The sentence holds no time: a client writes when in its user's zone.
    /// </summary>
    public LineRead Read(LineContext line)
    {
        var told = workspaces().WouldTakeBack(line.State, line.Caller.UserId, undo);
        var request = new JsonObject { ["workspaceId"] = line.State.WorkspaceId, ["command"] = told.Command };
        var whose = told.User == line.Caller.UserId ? "you" : told.User;
        var who = told.Origin switch { RunOrigin.Ai => $"the AI for {whose}", RunOrigin.Workflow => $"a saved sequence for {whose}", _ => whose };
        return new(new JsonObject { ["request"] = request }, [], null,
            $"{(undo ? "Takes back" : "Puts back")}: {told.Said} ({who}, {told.Edits} edit{(told.Edits == 1 ? "" : "s")})", told.At);
    }

    public LineCompletions Complete(LineContext line) => LineCompletions.None;

    public Task<IReadOnlyDictionary<string, JsonNode?>> InvokeAsync(CapabilityInvocation invocation, CancellationToken cancellationToken)
    {
        var caller = invocation.Caller ?? throw new CapabilityFailure("NoCaller", "A command is invoked for a caller, through the command gateway.");
        var request = invocation.Inputs["request"] ?? throw new CapabilityFailure("InvalidInput", "The command takes a request.");
        var workspaceId = CommandRequests.Text(request, "workspaceId");
        var service = workspaces();
        try
        {
            var state = WorkspaceCommands.Owned(service, workspaceId, caller);
            var named = request["command"]?.GetValue<string>();
            var read = request["revision"]?.GetValue<long>();
            var verb = undo ? "undo" : "redo";
            if (named is null && caller.Origin == RunOrigin.Ai)
                throw new WorkspaceRefused("CommandNotNamed", $"{verb} names the command it means by its id, which that command's result carries (such as c12).");
            if (named is null && read is null)
                throw new WorkspaceRefused("CommandNotNamed", $"{verb} takes the revision the workspace was read at, or the id of the command it means.");
            // A named command is the latest or the call is refused, so it needs no revision to say what it saw.
            var revision = read ?? state.Revision;
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
            // As the wire writes an origin everywhere else: `pricer`, `ai`.
            if (told.Origin is { } origin) answer["origin"] = System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(origin.ToString());
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
                ["revision"] = new JsonObject { ["type"] = "integer", ["title"] = "Revision the workspace was read at; needed unless the command is named" },
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
    /// <summary>
    /// The workspace a kernel command acts on: one that exists and that its caller owns (ADR-0005 §2), at the revision
    /// the caller read where it says one, which a workspace that moved refuses.
    /// </summary>
    public static WorkspaceState Owned(WorkspaceService service, string workspaceId, Caller caller, long? revision = null)
    {
        var state = service.Find(workspaceId) ?? throw new WorkspaceRefused("UnknownWorkspace", $"No workspace {workspaceId}.");
        if (state.Owner != caller.UserId) throw new WorkspaceRefused("NotOwner", $"Workspace {workspaceId} belongs to another user.");
        return revision is { } read && read != state.Revision ? throw new WorkspaceRefused("RevisionMoved", $"The workspace is at revision {state.Revision}, not {read}.") : state;
    }
}
