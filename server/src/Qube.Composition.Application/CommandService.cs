using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Qube.Composition.Contracts;
using Tags = Qube.Composition.Application.QubeTelemetry.Tags;

namespace Qube.Composition.Application;

/// <summary>What invoking a command produced: its outputs, or a refusal with every reason by field.</summary>
public abstract record CommandResult
{
    public sealed record Done(string CapabilityId, string Version, IReadOnlyDictionary<string, JsonNode?> Outputs) : CommandResult;
    public sealed record Refused(string Code, IReadOnlyList<CompositionIssue> Issues) : CommandResult;
}

/// <summary>
/// The command gateway (ADR-0005): invokes a capability that edits a workspace, runs a subscription or reads a Run, for a caller
/// the server authenticated, outside Runs. Inputs are checked against the capability's contracts before the handler
/// sees them; what the command did is recorded where it happened (the workspace's edit log, its live status).
///
/// An idempotency key makes a retry answer the first result instead of acting twice; the same key with other
/// inputs is refused. Keys are held in memory for the life of the process: a retry after a restart acts again.
/// </summary>
public sealed class CommandService(CompositionRegistry registry, IDataReader data)
{
    readonly ConcurrentDictionary<(string User, string Key), (string Request, Lazy<Task<CommandResult>> Result)> done = new();

    public async Task<CommandResult> InvokeAsync(string capabilityId, string version, JsonObject inputs, Caller caller, string idempotencyKey,
        CancellationToken cancellationToken)
    {
        using var activity = QubeTelemetry.Source.StartActivity($"command {capabilityId}");
        activity?.SetTag(Tags.CapabilityId, capabilityId);
        activity?.SetTag(Tags.Origin, caller.Origin.ToString());
        var outcome = "failed";
        try
        {
            var result = await InvokeOnceAsync(capabilityId, version, inputs, caller, idempotencyKey, cancellationToken);
            outcome = result is CommandResult.Refused refused ? refused.Code : "done";
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller went away: the command answered nobody, and the workbench did not fail.
            outcome = "cancelled";
            throw;
        }
        catch (Exception failure)
        {
            activity?.SetStatus(ActivityStatusCode.Error, failure.Message);
            throw;
        }
        finally
        {
            // A refusal is the command answering, not the workbench failing: it is an outcome, not an error status.
            activity?.SetTag(Tags.Outcome, outcome);
            QubeTelemetry.Commands.Add(1, new(Tags.CapabilityId, capabilityId), new(Tags.Origin, caller.Origin.ToString()), new(Tags.Outcome, outcome));
        }
    }

    async Task<CommandResult> InvokeOnceAsync(string capabilityId, string version, JsonObject inputs, Caller caller, string idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            return Refuse("ValidationFailed", "Required", "/idempotencyKey", "An idempotency key is required.");
        var handler = registry.FindCapability(capabilityId, version);
        if (handler is null)
            return Refuse("UnknownCapability", "UnknownCapability", "/capabilityId", $"Capability {capabilityId}@{version} is not registered.");
        if (!handler.Descriptor.IsCommand())
            return Refuse("NotACommand", "NotACommand", "/capabilityId", $"{capabilityId} computes in a Run; submit a workflow that uses it.");
        var issues = Check(handler.Descriptor, inputs).ToList();
        if (issues.Count > 0) return new CommandResult.Refused("ValidationFailed", issues);

        var request = $"{capabilityId}@{version}:{inputs.ToJsonString()}";
        var mine = (request, new Lazy<Task<CommandResult>>(() => Invoke(handler, inputs, caller, idempotencyKey, cancellationToken)));
        var entry = done.GetOrAdd((caller.UserId, idempotencyKey), mine);
        if (entry.Request != request)
            return Refuse("IdempotencyConflict", "IdempotencyConflict", "/idempotencyKey", "This key was used for another command or other inputs.");
        // A request that finds its key taken joins the command running under it, and stops waiting when its own caller
        // goes away; the command made it runs on its own caller's token.
        var joined = !ReferenceEquals(entry.Result, mine.Item2);
        var running = entry.Result.Value;
        CommandResult result;
        try
        {
            result = await (joined ? running.WaitAsync(cancellationToken) : running);
        }
        catch when (!running.IsCompleted || running.IsCompletedSuccessfully)
        {
            // This caller went away while the command ran on, or as it finished: the key stays with that command.
            throw;
        }
        catch (OperationCanceledException) when (joined && !cancellationToken.IsCancellationRequested)
        {
            // The command this request joined was cancelled by its own caller, as one that waits, such as Price, can be.
            // A failed command frees its key, so this caller, still waiting, acts on it itself.
            done.TryRemove(new((caller.UserId, idempotencyKey), entry));
            return await InvokeOnceAsync(capabilityId, version, inputs, caller, idempotencyKey, cancellationToken);
        }
        catch
        {
            done.TryRemove(new((caller.UserId, idempotencyKey), entry));
            throw;
        }
        // Only what was done is answered again; a refusal leaves nothing done, so a retry tries again.
        if (result is CommandResult.Refused) done.TryRemove(new((caller.UserId, idempotencyKey), entry));
        return result;
    }

    async Task<CommandResult> Invoke(ICapabilityHandler handler, JsonObject inputs, Caller caller, string idempotencyKey, CancellationToken cancellationToken)
    {
        var bound = handler.Descriptor.InputPorts.ToDictionary(p => p.Name, p => inputs[p.Name]?.DeepClone());
        try
        {
            var outputs = await handler.InvokeAsync(new($"cmd-{Guid.CreateVersion7():N}", bound, data, caller, idempotencyKey), cancellationToken);
            var missing = handler.Descriptor.OutputPorts.Where(p => !outputs.ContainsKey(p.Name)).Select(p => p.Name).ToList();
            if (missing.Count > 0)
                throw new InvalidOperationException($"{handler.Descriptor.CapabilityId} did not answer {string.Join(", ", missing)}.");
            return new CommandResult.Done(handler.Descriptor.CapabilityId, handler.Descriptor.Version, outputs);
        }
        catch (CapabilityFailure failure)
        {
            return new CommandResult.Refused(failure.Code, failure.Issues.Count > 0 ? failure.Issues : [new(failure.Code, "", failure.Message)]);
        }
    }

    /// <summary>Every input port is given once and nothing else; a form-sized one meets its contract.</summary>
    IEnumerable<CompositionIssue> Check(CapabilityDescriptor descriptor, JsonObject inputs)
    {
        foreach (var extra in inputs.Select(p => p.Key).Where(k => descriptor.InputPorts.All(p => p.Name != k)))
            yield return new("UnknownInput", $"/inputs/{extra}", $"{descriptor.CapabilityId} takes no input {extra}.");
        foreach (var port in descriptor.InputPorts)
        {
            if (inputs[port.Name] is not JsonObject value)
            {
                yield return new("Required", $"/inputs/{port.Name}", $"{port.Name} is required, as an object of {port.ContractId}.");
                continue;
            }
            var contract = registry.FindContract(port.ContractId, port.ContractVersion)
                ?? throw new InvalidOperationException($"{descriptor.CapabilityId} names contract {port.ContractId}@{port.ContractVersion}, which is not registered.");
            foreach (var issue in ContractChecks.Check(contract, value, $"/inputs/{port.Name}")) yield return issue;
        }
    }

    static CommandResult.Refused Refuse(string code, string issue, string path, string message) => new(code, [new(issue, path, message)]);
}
