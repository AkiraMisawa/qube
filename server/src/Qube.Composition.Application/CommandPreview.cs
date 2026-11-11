using System.Text.Json.Nodes;
using Qube.Composition.Application.Workspaces;
using Qube.Composition.Contracts;

namespace Qube.Composition.Application;

// ADR-0016 §5, §7 and its note 38 of 2026-11-11: the line's reading as a capability, so that a caller with no screen
// can say what a command would change before it runs it. It reads its caller's own workspace and runs nothing.

/// <summary>
/// <c>commands.preview</c> (ADR-0016 §7, note 38): a line as typed, read for the caller's workspace as the command line
/// reads it. It answers the command and the inputs that run it, each argument as read, the rows meant and what would
/// change. A line that cannot be read refuses the call as the reading refuses it. It gives the reading no view: the
/// rows and the verbs of a screen are refused by name.
/// </summary>
public sealed class CommandPreview(Func<CommandLines> lines) : ICapabilityHandler
{
    public const string Id = "commands.preview";
    public const string Version = "1.0.0";
    public const string Request = "commands.preview-request";
    public const string Read = "commands.line-read";

    public CapabilityDescriptor Descriptor { get; } = new(Id, Version, "Say what a command line would do to a workspace, and run nothing",
        [new("request", Request, Version, Cardinality.One)], [new("answer", Read, Version, Cardinality.One)],
        ExecutionKind.RequestResponse, [CapabilityEffect.ReadWorkspace]);

    public Task<IReadOnlyDictionary<string, JsonNode?>> InvokeAsync(CapabilityInvocation invocation, CancellationToken cancellationToken)
    {
        var caller = invocation.Caller ?? throw new CapabilityFailure("NoCaller", "A command is invoked for a caller, through the command gateway.");
        var request = invocation.Inputs["request"] ?? throw new CapabilityFailure("InvalidInput", "The command takes a request.");
        var line = CommandRequests.Text(request, "line");
        LinePreview preview;
        try
        {
            preview = lines().Preview(line, CommandRequests.Text(request, "workspaceId"), request["revision"]?.GetValue<long>(), caller);
        }
        catch (WorkspaceRefused refused)
        {
            throw new CapabilityFailure(refused.Code, refused.Message);
        }
        if (preview.Refused is { } refusal)
            throw new CapabilityFailure(refusal.Code, refusal.Sentence,
                [new(refusal.Code, "", refusal.Sentence) { Word = refusal.Word, Candidates = refusal.Candidates.Count > 0 ? refusal.Candidates : null }]);
        // A verb of the screen answers what its page is to do and names no command. Each refuses a caller with no
        // screen itself; one that did not is still nothing this caller can run.
        if (preview.Command is not { } command || preview.Inputs is not { } inputs)
            throw new CapabilityFailure("NoView", $"`{line}` is done by a page that shows a grid, and no command runs it.");
        var arguments = preview.Arguments ?? throw new InvalidOperationException($"`{line}` was read as {command.Id} with no arguments said.");

        var answer = new JsonObject
        {
            ["workspaceId"] = CommandRequests.Text(request, "workspaceId"),
            ["revision"] = preview.Revision,
            ["line"] = line,
            ["command"] = command.Id,
            ["version"] = command.Version,
            // Text, since a contract holds no object in an object: handed to the command's own tool as they are.
            ["inputs"] = inputs.ToJsonString(),
            ["arguments"] = new JsonArray([.. arguments.Select(a => (JsonNode)new JsonObject { ["name"] = a.Name, ["text"] = a.Text })]),
        };
        if (preview.Rows is { } rows)
        {
            answer["table"] = rows.Table;
            answer["rows"] = rows.Title;
            answer["count"] = rows.Count;
            answer["first"] = new JsonArray([.. rows.First.Select(key => (JsonNode)key)]);
        }
        if (preview.Would is { } would) answer["would"] = would;
        if (preview.Expands is { } expands) answer["expands"] = expands;
        if (preview.At is { } at) answer["at"] = at.ToString("O");
        return Task.FromResult<IReadOnlyDictionary<string, JsonNode?>>(new Dictionary<string, JsonNode?> { ["answer"] = answer });
    }
}

/// <summary>
/// The kernel's reading of a line as a capability, and its contracts. The lines are given when first asked for: they
/// are built from the registry this module registers into. Knows no command and no table.
/// </summary>
public sealed class CommandLinesModule(Func<CommandLines> lines) : IQubeModule
{
    public void Register(CompositionRegistryBuilder builder)
    {
        builder.AddCapability(new CommandPreview(lines));
        builder.AddContract(new ContractDefinition(CommandPreview.Request, CommandPreview.Version, "Preview a command line", new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["workspaceId"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["title"] = "Workspace" },
                ["revision"] = new JsonObject { ["type"] = "integer", ["title"] = "Revision the workspace was read at; the one it holds when left out" },
                ["line"] = new JsonObject
                {
                    ["type"] = "string", ["minLength"] = 1,
                    ["title"] = "The line as a person types it: a command's words, then its rows and values (set where <field>=<value> <field> <value>)",
                },
            },
            ["required"] = new JsonArray("workspaceId", "line"),
            ["additionalProperties"] = false,
        }));
        builder.AddContract(new ContractDefinition(CommandPreview.Read, CommandPreview.Version, "A command line as read", new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["workspaceId"] = new JsonObject { ["type"] = "string", ["title"] = "Workspace" },
                ["revision"] = new JsonObject { ["type"] = "integer", ["title"] = "Revision read" },
                ["line"] = new JsonObject { ["type"] = "string", ["title"] = "The line as given" },
                ["expands"] = new JsonObject { ["type"] = "string", ["title"] = "The line a short form stands for, which is what runs" },
                ["command"] = new JsonObject { ["type"] = "string", ["title"] = "The command the line names, by its capability id" },
                ["version"] = new JsonObject { ["type"] = "string", ["title"] = "Its version" },
                ["inputs"] = new JsonObject { ["type"] = "string", ["title"] = "The inputs that run it at the revision read, as JSON: the command's own tool takes them as they are" },
                ["arguments"] = new JsonObject
                {
                    ["type"] = "array",
                    ["title"] = "Each argument as read",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["name"] = new JsonObject { ["type"] = "string", ["title"] = "The name the request holds it by" },
                            ["text"] = new JsonObject { ["type"] = "string", ["title"] = "Its words" },
                        },
                        ["required"] = new JsonArray("name", "text"),
                        ["additionalProperties"] = false,
                    },
                },
                ["table"] = new JsonObject { ["type"] = "string", ["title"] = "The id of the table of the rows meant" },
                ["rows"] = new JsonObject { ["type"] = "string", ["title"] = "The rows meant, in words" },
                ["count"] = new JsonObject { ["type"] = "integer", ["title"] = "How many rows are meant" },
                ["first"] = new JsonObject { ["type"] = "array", ["title"] = "The first of them, by their keys", ["items"] = new JsonObject { ["type"] = "string" } },
                ["would"] = new JsonObject { ["type"] = "string", ["title"] = "What running it would change, in one sentence" },
                ["at"] = new JsonObject { ["type"] = "string", ["title"] = "The moment the reading speaks of, where it speaks of one" },
            },
            ["required"] = new JsonArray("workspaceId", "revision", "line", "command", "version", "inputs", "arguments"),
            ["additionalProperties"] = false,
        }));
    }
}
