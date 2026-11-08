using System.Text.Json.Nodes;
using Workbench.Composition.Application;
using Workbench.Composition.Application.Traces;
using Workbench.Composition.Contracts;

namespace Workbench.Web;

/// <summary>Thin HTTP endpoints over the application services. No business logic lives here.</summary>
public static class Api
{
    public sealed record ErrorEnvelope(string Code, string Message, IReadOnlyList<CompositionIssue> Issues, bool Retryable = false);

    public sealed record Catalog(
        IEnumerable<CapabilityDescriptor> Capabilities,
        IEnumerable<ContractDefinition> Contracts,
        IEnumerable<WorkflowDefinition> Workflows,
        IEnumerable<ViewDefinition> Views,
        IEnumerable<ActionDefinition> Actions);

    public sealed record RunRequest(WorkflowDefinition Workflow, JsonObject Context, JsonObject Input, string IdempotencyKey);

    public sealed record ActionRequest(IReadOnlyDictionary<string, JsonNode?> Parameters, JsonObject Context, string IdempotencyKey);

    public sealed record CommandRequest(JsonObject Inputs, string IdempotencyKey);

    public sealed record ViewValidationRequest(ViewDefinition View, IReadOnlyList<string> BoundNames);

    public sealed record HistoryRequest(string? Domain, int Start, int Count);

    public static void MapWorkbenchApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/catalog", (CompositionRegistry registry) => new Catalog(
            registry.CapabilityDescriptors, registry.ContractDefinitions, registry.WorkflowDefinitions,
            registry.ViewDefinitions, registry.ActionDefinitions));

        api.MapPost("/runs", (RunRequest request, RunService runs, CurrentUser user) =>
            ToResult(runs.Submit(new(request.Workflow, request.Context, request.Input, request.IdempotencyKey, user.Id, RunOrigin.Workflow, RunRelations.None))));

        api.MapGet("/runs/{runId}", (string runId, RunService runs) =>
            runs.Find(runId) is { } run ? Results.Ok(run) : NotFound("UnknownRun", $"Run {runId} does not exist."));

        // A Run's calculation trace, read a term at a time and never whole (ADR-0011 §5). The kinds' formulas are read
        // once per method version from the catalogue.
        api.MapGet("/runs/{runId}/cases/{caseId}/traces/{measure}", (string runId, string caseId, string measure, string? scenario, string? path,
            string? order, int? offset, int? limit, TraceService traces, CurrentUser user) =>
            Traced(() => traces.Read(new(runId, caseId, scenario ?? TraceService.BaseScenario, measure), user.Id, path, TraceService.OrderOf(order), offset ?? 0, limit ?? TraceService.DefaultPage)));

        api.MapGet("/runs/{runId}/cases/{caseId}/traces/{measure}/leaves", (string runId, string caseId, string measure, string? scenario, string? path,
            int? offset, int? limit, string? sort, bool? descending, TraceService traces, CurrentUser user) =>
            Traced(() => traces.Leaves(new(runId, caseId, scenario ?? TraceService.BaseScenario, measure), user.Id,
                path ?? throw new TraceRefused("Required", "path names the term whose leaf rows are read."), offset ?? 0, limit ?? TraceService.DefaultPage, sort, descending ?? false)));

        api.MapGet("/traces/catalogs/{method}/{version}", (string method, string version, CompositionRegistry registry) =>
            registry.FindTraceCatalog(method, version) is { } catalog
                ? Results.Ok(catalog)
                : NotFound("UnknownMethod", $"No module registered the kinds of {method} {version}."));

        // Download (ADR-0010 §7): a finished Run as a zip of its record, its frozen inputs, its results and its lineage.
        api.MapGet("/runs/{runId}/archive", (string runId, RunService runs, IDataStore store, CurrentUser user) =>
        {
            if (runs.Find(runId) is not { } run) return NotFound("UnknownRun", $"Run {runId} does not exist.");
            if (run.Owner != user.Id) return Results.Json(new ErrorEnvelope("NotOwner", $"Run {runId} belongs to another user.", []), statusCode: 403);
            if (run.Status == RunStatus.Running) return Results.Json(new ErrorEnvelope("RunRunning", $"Run {runId} is still running; download it once it finishes.", []), statusCode: 409);
            var zip = new MemoryStream();
            RunArchive.Write(run, store, zip);
            zip.Position = 0;
            return Results.File(zip, "application/zip", $"{runId}.zip");
        });

        api.MapPost("/data/read", (TypedDataRef reference, IDataStore store) => store.Read(reference) switch
        {
            DataLookup.Found found => Results.Ok(found.Content),
            DataLookup.Expired => Results.Json(new ErrorEnvelope("DataExpired", $"{reference.DataId} has expired and is not recomputed.", []), statusCode: 410),
            _ => NotFound("DataUnavailable", $"{reference.DataId}@{reference.Revision} is not retained."),
        });

        api.MapPost("/actions/{actionId}/invoke", (string actionId, ActionRequest request, ActionService actions, CurrentUser user) =>
            actions.Invoke(actionId, request.Parameters, request.Context, request.IdempotencyKey, user.Id) switch
            {
                ActionResult.ViewOpened opened => Results.Ok(new { kind = "openView", view = opened.View }),
                ActionResult.Submitted { Submission: SubmissionResult.Accepted accepted } => Results.Ok(new { kind = "run", run = accepted.Run }),
                ActionResult.Submitted { Submission: SubmissionResult.Rejected rejected } => Rejected(rejected.Code, rejected.Issues),
                ActionResult.Refused refused => Rejected(refused.Code, refused.Issues),
                _ => throw new InvalidOperationException("Unhandled action result."),
            });

        // Commands (ADR-0005), as the agent's tool calls reach them: the caller is the signed-in user, acting through
        // the AI. The fixed pages keep their own endpoints, which call the same application services as the pricer.
        api.MapPost("/agent/commands/{capabilityId}/{version}", async (string capabilityId, string version, CommandRequest request,
            CommandService commands, CurrentUser user, CancellationToken cancellationToken) =>
            await commands.InvokeAsync(capabilityId, version, request.Inputs, new Caller(user.Id, RunOrigin.Ai), request.IdempotencyKey, cancellationToken) switch
            {
                CommandResult.Done done => Results.Ok(new { capabilityId = done.CapabilityId, version = done.Version, outputs = done.Outputs }),
                CommandResult.Refused { Code: "UnknownCapability" or "UnknownWorkspace" } refused =>
                    Results.Json(new ErrorEnvelope(refused.Code, refused.Issues[0].Message, refused.Issues), statusCode: 404),
                CommandResult.Refused { Code: "NotOwner" } refused =>
                    Results.Json(new ErrorEnvelope(refused.Code, refused.Issues[0].Message, refused.Issues), statusCode: 403),
                CommandResult.Refused { Code: "RevisionMoved" } refused =>
                    Results.Json(new ErrorEnvelope(refused.Code, refused.Issues[0].Message, refused.Issues, Retryable: true), statusCode: 409),
                CommandResult.Refused refused => Rejected(refused.Code, refused.Issues),
                _ => throw new InvalidOperationException("Unhandled command result."),
            });

        // The Pricing DB. Until roles arrive with sign-in (C30, C31), a user sees their own Runs only.
        api.MapGet("/history/catalog", (HistoryService history) => history.Catalog());

        api.MapPost("/history/rows", (HistoryRequest request, HistoryService history, CurrentUser user) =>
        {
            if (request.Start < 0 || request.Count is < 1 or > 2000)
                return Results.Json(new ErrorEnvelope("InvalidRange", "A range starts at 0 or later and holds 1 to 2000 rows.", []), statusCode: 422);
            try
            {
                return Results.Ok(history.Page(user.Id, request.Domain, request.Start, request.Count));
            }
            catch (HistoryRefused refused)
            {
                return Results.Json(new ErrorEnvelope(refused.Code, refused.Message, []), statusCode: 422);
            }
        });

        api.MapPost("/definitions/validate-workflow", (WorkflowDefinition workflow, CompositionRegistry registry) =>
            Results.Ok(new CompositionValidator(registry).Validate(workflow).ToList()));

        api.MapPost("/definitions/validate-view", (ViewValidationRequest request, CompositionRegistry registry) =>
            Results.Ok(new CompositionValidator(registry).Validate(request.View, request.BoundNames.ToHashSet()).ToList()));
    }

    /// <summary>A trace's answer, or its refusal with the status that says whose it is to act on.</summary>
    static IResult Traced<T>(Func<T> read)
    {
        try
        {
            return Results.Ok(read());
        }
        catch (TraceRefused refused)
        {
            var status = refused.Code switch
            {
                "UnknownRun" or "UnknownTrace" or "UnknownTerm" => 404,
                "NotOwner" => 403,
                "RunRunning" => 409,
                "TraceExpired" or "TraceUnavailable" => 410,
                "UnknownMethod" or "TraceInvalid" => 500,
                "InvalidOrder" or "InvalidRange" or "Required" => 422,
                // A refusal no status was chosen for is a defect here, said as one, never answered as the caller's.
                var code => throw new InvalidOperationException($"No status is set for the trace refusal {code}."),
            };
            return Results.Json(new ErrorEnvelope(refused.Code, refused.Message, [], refused.Retryable), statusCode: status);
        }
    }

    static IResult ToResult(SubmissionResult result) => result switch
    {
        SubmissionResult.Accepted accepted => Results.Accepted($"/api/runs/{accepted.Run.RunId}", accepted.Run),
        SubmissionResult.Rejected rejected => Rejected(rejected.Code, rejected.Issues),
        _ => throw new InvalidOperationException("Unhandled submission result."),
    };

    static IResult Rejected(string code, IReadOnlyList<CompositionIssue> issues) => Results.Json(
        new ErrorEnvelope(code, issues.Count == 1 ? issues[0].Message : $"{issues.Count} problems were found.", issues),
        statusCode: code == "IdempotencyConflict" ? 409 : code is "DataUnavailable" ? 410 : 422);

    static IResult NotFound(string code, string message) => Results.Json(new ErrorEnvelope(code, message, []), statusCode: 404);
}
