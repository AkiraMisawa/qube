using System.Text.Json.Nodes;
using Qube.Composition.Application.Workspaces;

namespace Qube.Composition.Application;

/// <summary>
/// What a command's handler reads of its request and of its invocation, and how it says what the kernel refused
/// (ADR-0016 §5): the same for every module, so a module's command holds only what is its own.
/// </summary>
public static class CommandRequests
{
    /// <summary>A field the request names; one it leaves out is the request's defect, said by the field's name.</summary>
    public static string Text(JsonNode request, string name) =>
        request[name]?.GetValue<string>() ?? throw new CapabilityFailure("InvalidInput", $"The request has no {name}.");

    /// <summary>An optional field: absent is none, never a value made up for it.</summary>
    public static string? Optional(JsonNode request, string name) => request[name]?.GetValue<string>();

    /// <summary>The revision a request was made against, as its contract says it.</summary>
    public static JsonObject RevisionField() => new()
    {
        ["type"] = "integer", ["title"] = "Revision",
        ["description"] = "The revision the request was made against; refused if the workspace has moved since. Left out, the one it holds.",
    };

    /// <summary>The key a command that submits a Run was invoked with: one business submission, asked again, is the same Run.</summary>
    public static string Key(CapabilityInvocation invocation, string command) => invocation.IdempotencyKey
        ?? throw new CapabilityFailure("NoIdempotencyKey", $"{command} is one business submission: it is invoked with a key, through the command gateway.");

    /// <summary>The Run a submission was accepted as; a submission the Run service rejected is the command's failure, each issue said.</summary>
    public static RunRecord Accepted(SubmissionResult submitted) => submitted switch
    {
        SubmissionResult.Accepted accepted => accepted.Run,
        // A line and a notice show a sentence alone, so the sentence says each issue the Run service found.
        SubmissionResult.Rejected rejected => throw new CapabilityFailure(rejected.Code,
            rejected.Issues.Count == 0 ? "The Run was not accepted."
                : $"The Run was not accepted: {string.Join("; ", rejected.Issues.Select(i => $"{i.Path}: {i.Message}"))}", rejected.Issues),
        _ => throw new InvalidOperationException("Unhandled submission result."),
    };

    /// <summary>A refusal that names the edits it waits on says them in its sentence (ADR-0016 note 33).</summary>
    public static T Naming<T>(Func<T> act)
    {
        try
        {
            return act();
        }
        catch (WorkspaceRefused refused) when (refused.Targets.Count > 0)
        {
            throw refused.Named();
        }
    }
}
