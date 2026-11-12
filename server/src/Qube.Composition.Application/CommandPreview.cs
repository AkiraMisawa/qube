using System.Text.Json.Nodes;
using Qube.Composition.Application.Tables;
using Qube.Composition.Contracts;
using static Qube.Composition.Application.ContractShapes;

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
    public const string Answer = "commands.line-read";

    public CapabilityDescriptor Descriptor { get; } = new(Id, Version, "Say what a command line would do to a workspace, and run nothing",
        [new("request", Request, Version, Cardinality.One)], [new("answer", Answer, Version, Cardinality.One)],
        ExecutionKind.RequestResponse, [CapabilityEffect.ReadWorkspace]);

    public Task<IReadOnlyDictionary<string, JsonNode?>> InvokeAsync(CapabilityInvocation invocation, CancellationToken cancellationToken) =>
        TableContracts.Answered(invocation, (request, caller) =>
        {
            var line = CommandRequests.Text(request, "line");
            var workspaceId = CommandRequests.Text(request, "workspaceId");
            var preview = lines().Preview(line, workspaceId, request["revision"]?.GetValue<long>(), caller);
            // A line that cannot be read refuses the call as the tables refuse one: the word at fault goes with the sentence.
            if (preview.Refused is { } refusal) throw new TableRefused(refusal.Code, refusal.Sentence, refusal.Word, refusal.Candidates);
            // A verb of the screen names no command: its reading asks its page. Each that needs a grid refuses a caller
            // with no screen itself; one that needs none (`export` and a table's word, ADR-0016 note 44) is still
            // nothing this caller can run, and the rows it would take out are this caller's to read.
            if (preview.Command is not { } command)
                throw new TableRefused(TableRefused.NoView, preview.Asks is GridAsk
                    ? $"`{line}` is done by a page that shows a grid, and no command runs it."
                    : $"`{line}` is done by a page of the workbench, and no command runs it: tables.read answers the rows of a table.");
            // A command's reading says its inputs, its arguments and what would change: one that does not is a defect.
            T Said<T>(T? part, string what) where T : class => part ?? throw new InvalidOperationException($"`{line}` was read as {command.Id} with no {what} said.");

            var answer = new JsonObject
            {
                ["workspaceId"] = workspaceId,
                ["revision"] = preview.Revision,
                ["line"] = line,
                ["command"] = command.Id,
                ["version"] = command.Version,
                // Text, since a contract holds no object in an object: handed to the command's own tool as they are.
                ["inputs"] = Said(preview.Inputs, "inputs").ToJsonString(),
                ["arguments"] = new JsonArray([.. Said(preview.Arguments, "arguments").Select(a => (JsonNode)new JsonObject { ["name"] = a.Name, ["text"] = a.Text })]),
                ["would"] = Said(preview.Would, "change"),
            };
            if (preview.Rows is { } rows)
            {
                answer["table"] = rows.Table;
                answer["rows"] = rows.Title;
                answer["count"] = rows.Count;
                answer["first"] = List(rows.First);
            }
            if (preview.Expands is { } expands) answer["expands"] = expands;
            if (preview.At is { } at) answer["at"] = at.ToString("O");
            return answer;
        });
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
        builder.AddContract(new ContractDefinition(CommandPreview.Request, CommandPreview.Version, "Preview a command line", Record(new JsonObject
        {
            ["workspaceId"] = Given("Workspace"),
            ["revision"] = Whole("Revision the workspace was read at; the one it holds when left out"),
            ["line"] = Given("The line as a person types it: a command's words, then its rows and values (set where <field>=<value> <field> <value>)"),
        }, "workspaceId", "line")));
        builder.AddContract(new ContractDefinition(CommandPreview.Answer, CommandPreview.Version, "A command line as read", Record(new JsonObject
        {
            ["workspaceId"] = Text("Workspace"),
            ["revision"] = Whole("Revision read"),
            ["line"] = Text("The line as given"),
            ["expands"] = Text("The line a short form stands for, which is what runs"),
            ["command"] = Text("The command the line names, by its capability id"),
            ["version"] = Text("Its version"),
            ["inputs"] = Text("The inputs that run it at the revision read, as JSON: the command's own tool takes them as they are"),
            ["arguments"] = Records("Each argument as read", new JsonObject
            {
                ["name"] = Text("The name the request holds it by"),
                ["text"] = Text("Its words"),
            }, "name", "text"),
            ["table"] = Text("The id of the table of the rows meant"),
            ["rows"] = Text("The rows meant, in words"),
            ["count"] = Whole("How many rows are meant"),
            ["first"] = Texts("The first of them, by their keys"),
            ["would"] = Text("What running it would change, in one sentence"),
            ["at"] = Text("The moment the reading speaks of, where it speaks of one"),
        }, "workspaceId", "revision", "line", "command", "version", "inputs", "arguments", "would")));
    }
}
