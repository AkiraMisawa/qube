using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Qube.Composition.Application;
using Qube.Composition.Application.Settings;
using Tags = Qube.Composition.Application.QubeTelemetry.Tags;

namespace Qube.Agent;

/// <summary>What starting a run produced: the run to read (new, or the one this run id already names), or a refusal.</summary>
public abstract record AgentStart
{
    public sealed record Running(AgentRun Run) : AgentStart;
    public sealed record Refused(string Code, string Message) : AgentStart;
}

sealed class AgentRunaway(string message) : Exception(message);

/// <summary>
/// The agent loop (ADR-0007): takes an AG-UI <c>RunAgentInput</c> for the signed-in user, keeps the conversation on
/// the server, asks the configured runtime for each turn, invokes the tool calls it proposes through the tool
/// gateway, and records every step as AG-UI events. The runtime proposes; only this loop acts. A conversation runs on
/// the model it holds from its first turn (ADR-0017 §6.3): its user's default model then, or one chosen in the pane,
/// which <c>forwardedProps.qube.model</c> carries and which is refused unless the runtime allows it.
/// </summary>
public sealed class AgentService(IAgentRuntime runtime, AgentToolGateway tools, AgentThreads threads, UserSettings settings, ILogger<AgentService>? logger = null)
{
    readonly ILogger log = logger ?? NullLogger<AgentService>.Instance;

    /// <summary>Tool calls a run may make before it is stopped as a runaway.</summary>
    public const int MaxToolCalls = 16;

    public AgentRuntimeInfo Runtime => runtime.Info;

    public AgentStart Start(JsonObject input, string userId)
    {
        if (Text(input, "threadId") is not { } threadId) return new AgentStart.Refused("ValidationFailed", "threadId is required.");
        if (Text(input, "runId") is not { } runId) return new AgentStart.Refused("ValidationFailed", "runId is required.");
        // Frontend tools would let a page give the agent powers the server has not registered (AI06).
        if (input["tools"] is JsonArray { Count: > 0 })
            return new AgentStart.Refused("FrontendToolsRefused", "The agent calls the server's registered commands only; tools offered by the client are refused.");
        if (!TryContext(input["context"], out var context, out var contextProblem)) return new AgentStart.Refused("ValidationFailed", contextProblem);
        if (!TryChosenModel(input["forwardedProps"], out var chosen, out var chosenProblem)) return new AgentStart.Refused("ValidationFailed", chosenProblem);

        var thread = threads.GetOrStart(threadId, userId);
        if (thread.Owner != userId) return new AgentStart.Refused("UnknownThread", $"Thread {threadId} does not exist.");

        AgentRun run;
        lock (thread.Gate)
        {
            // The same run id answers the same run again, so a retried request is never run twice.
            if (thread.FindRun(runId) is { } existing) return new AgentStart.Running(existing);
            if (thread.Active is { Finished: false } active)
                return new AgentStart.Refused("RunInProgress", $"Run {active.RunId} is still answering on this thread; stop it or wait for it.");
            if (!TryNewRequests(input["messages"], thread, out var requests, out var problem)) return new AgentStart.Refused("ValidationFailed", problem);
            if (!TryModel(thread, chosen, userId, out var model, out var refusal)) return refusal;
            foreach (var request in requests) thread.Add(request);
            run = new AgentRun(threadId, runId, model);
            thread.AddRun(run);
            thread.Active = run;
        }
        _ = Task.Run(() => RunAsync(thread, run, context, userId));
        return new AgentStart.Running(run);
    }

    /// <summary>
    /// The model a run is on (ADR-0017 §6.3): one chosen in the pane, where the runtime allows it; else the one the
    /// conversation holds; else, on its first turn, the user's default model. A default the configuration no longer
    /// allows is a preference no longer taken: the registered model, the first allowed, applies, and the settings say
    /// the user's is unreadable (§2). A runtime with no model runs on none.
    /// </summary>
    bool TryModel(AgentThread thread, string? chosen, string userId, out string? model, [NotNullWhen(false)] out AgentStart.Refused? refusal)
    {
        model = null;
        refusal = null;
        var allowed = runtime.Info.Models;
        if (allowed.Count == 0)
        {
            if (chosen is null) return true;
            refusal = new("ModelNotAllowed", $"{runtime.Info.Label} runs on no model, so no model is chosen for its conversations.");
            return false;
        }
        if (chosen is not null)
        {
            if (!allowed.Contains(chosen, StringComparer.Ordinal))
            {
                refusal = new("ModelNotAllowed", $"{chosen} is not one of the models the AI may use: {string.Join(", ", allowed)}.");
                return false;
            }
            model = chosen;
            return true;
        }
        if (thread.Model is { } held)
        {
            model = held;
            return true;
        }
        model = settings.One(userId, AgentSettings.DefaultModel).Value?.GetValue<string>()
            ?? throw new InvalidOperationException($"{AgentSettings.DefaultModel} reads as no model, though the first allowed is registered.");
        return true;
    }

    async Task RunAsync(AgentThread thread, AgentRun run, IReadOnlyList<AgentContext> context, string userId)
    {
        // The conversation's words are not logged or tagged: they may hold what the user would not have written down.
        using var activity = QubeTelemetry.Source.StartActivity("agent run");
        activity?.SetTag(AgentTelemetry.ThreadId, run.ThreadId);
        activity?.SetTag(AgentTelemetry.RunId, run.RunId);
        activity?.SetTag(AgentTelemetry.Runtime, runtime.Info.Name);
        activity?.SetTag(AgentTelemetry.Model, run.Model);
        var outcome = "finished";
        try
        {
            run.Append(AgUiEvents.RunStarted(run, runtime.Info));
            await runtime.RunAsync(new(run.ThreadId, run.RunId, thread.Messages, tools.Tools, context, run.Model), new Sink(thread, run, tools, userId), run.Cancellation);
            if (run.Cancellation.IsCancellationRequested) outcome = "cancelled";
            run.Append(AgUiEvents.RunFinished(run.ThreadId, run.RunId, cancelled: run.Cancellation.IsCancellationRequested), last: true);
        }
        catch (OperationCanceledException) when (run.Cancellation.IsCancellationRequested)
        {
            outcome = "cancelled";
            run.Append(AgUiEvents.RunFinished(run.ThreadId, run.RunId, cancelled: true), last: true);
        }
        catch (AgentRunaway runaway)
        {
            outcome = "runaway";
            log.LogWarning("Agent run {AgentRunId} on thread {AgentThreadId} was stopped: {Reason}", run.RunId, run.ThreadId, runaway.Message);
            run.Append(AgUiEvents.RunError("TooManyToolCalls", runaway.Message), last: true);
        }
        catch (Exception failure)
        {
            outcome = "failed";
            activity?.SetStatus(ActivityStatusCode.Error, failure.Message);
            log.LogError(failure, "Agent run {AgentRunId} on thread {AgentThreadId} failed in {AgentRuntime} on {AgentModel}", run.RunId, run.ThreadId, runtime.Info.Name, run.Model);
            var answerer = run.Model is null ? runtime.Info.Label : $"{runtime.Info.Label} on {run.Model}";
            run.Append(AgUiEvents.RunError("AgentFailed", $"{answerer} failed: {failure.Message}"), last: true);
        }
        finally
        {
            activity?.SetTag(Tags.Outcome, outcome);
            AgentTelemetry.Runs.Add(1, new(AgentTelemetry.Runtime, runtime.Info.Name), new(Tags.Outcome, outcome));
        }
    }

    /// <summary>
    /// What a runtime reaches the run through: every word it says and every tool it calls is recorded here, in order,
    /// and a call is invoked by the gateway, never by the runtime. A cancelled run invokes nothing more.
    /// </summary>
    sealed class Sink(AgentThread thread, AgentRun run, AgentToolGateway tools, string userId) : IAgentTurnSink
    {
        int calls;

        public Task SayAsync(string text, CancellationToken cancellationToken)
        {
            run.Cancellation.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(text)) return Task.CompletedTask;
            var messageId = NewId("msg");
            foreach (var e in AgUiEvents.TextMessage(messageId, text)) run.Append(e);
            thread.Add(new AgentMessage(messageId, "assistant", text));
            return Task.CompletedTask;
        }

        public async Task<ToolResult> CallAsync(AgentToolCall call, CancellationToken cancellationToken)
        {
            run.Cancellation.ThrowIfCancellationRequested();
            if (++calls > MaxToolCalls)
                throw new AgentRunaway($"The agent asked for more than {MaxToolCalls} tool calls in one run; the run was stopped.");
            var messageId = NewId("msg");
            foreach (var e in AgUiEvents.ToolCall(call, messageId)) run.Append(e);
            thread.Add(new AgentMessage(messageId, "assistant", null, [call]));
            // A command once invoked is not interrupted: its effect may already be in place (ADR-0007 §4).
            using var activity = QubeTelemetry.Source.StartActivity($"agent tool {call.Name}");
            activity?.SetTag(AgentTelemetry.Tool, call.Name);
            var result = await tools.InvokeAsync(call, userId, thread.ThreadId, CancellationToken.None);
            var outcome = result.Succeeded ? "done" : "refused";
            activity?.SetTag(Tags.Outcome, outcome);
            AgentTelemetry.ToolCalls.Add(1, new(AgentTelemetry.Tool, call.Name), new(Tags.Outcome, outcome));
            var resultId = NewId("msg");
            thread.Add(AgentMessage.Tool(resultId, call.Id, result.Text));
            run.Append(AgUiEvents.ToolCallResult(resultId, call.Id, result.Text));
            return result;
        }
    }

    /// <summary>The user messages the thread has not seen. History the client sends is not believed (ADR-0007 §2).</summary>
    static bool TryNewRequests(JsonNode? node, AgentThread thread, out List<AgentMessage> requests, out string problem)
    {
        requests = [];
        problem = "";
        if (node is not JsonArray messages)
        {
            problem = "messages is required.";
            return false;
        }
        foreach (var item in messages)
        {
            if (item is not JsonObject message || Text(message, "id") is not { } id || Text(message, "role") is not { } role)
            {
                problem = "Every message has an id and a role.";
                return false;
            }
            if (thread.Knows(id) || requests.Any(r => r.Id == id)) continue;
            if (role != "user")
            {
                problem = $"Message {id} ({role}) is not one this thread holds; only new user messages are taken from the client.";
                return false;
            }
            if (message["content"] is not JsonValue content || !content.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text))
            {
                problem = $"Message {id} has no text.";
                return false;
            }
            requests.Add(AgentMessage.User(id, text));
        }
        if (requests.Count == 0)
        {
            problem = "A run answers a new user message, and none was sent.";
            return false;
        }
        return true;
    }

    /// <summary>The model chosen in the pane for this conversation, where the request names one (<c>forwardedProps.qube.model</c>).</summary>
    static bool TryChosenModel(JsonNode? node, out string? chosen, out string problem)
    {
        chosen = null;
        problem = "";
        if (node is not JsonObject props || props["qube"] is not { } qube) return true;
        if (qube is not JsonObject workbench || workbench["model"] is not JsonValue model || !model.TryGetValue<string>(out var id) || string.IsNullOrWhiteSpace(id))
        {
            problem = "forwardedProps.qube names the model chosen for the conversation, as model: its id.";
            return false;
        }
        chosen = id;
        return true;
    }

    static bool TryContext(JsonNode? node, out List<AgentContext> context, out string problem)
    {
        context = [];
        problem = "";
        if (node is null) return true;
        if (node is not JsonArray entries)
        {
            problem = "context is a list.";
            return false;
        }
        foreach (var entry in entries)
        {
            if (entry is not JsonObject e || Text(e, "description") is not { } description || e["value"] is not JsonValue value || !value.TryGetValue<string>(out var text))
            {
                problem = "Every context entry has a description and a value, both text.";
                return false;
            }
            context.Add(new(description, text));
        }
        return true;
    }

    static string? Text(JsonObject json, string name) =>
        json[name] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text : null;

    static string NewId(string prefix) => $"{prefix}-{Guid.CreateVersion7():N}";
}
