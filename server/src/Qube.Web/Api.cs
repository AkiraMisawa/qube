using System.Text.Json.Nodes;
using Qube.Composition.Application;
using Qube.Composition.Application.Saved;
using Qube.Composition.Application.Settings;
using Qube.Composition.Application.Tables;
using Qube.Composition.Application.Traces;
using Qube.Composition.Application.Workspaces;
using Qube.Composition.Contracts;

namespace Qube.Web;

/// <summary>Thin HTTP endpoints over the application services. No business logic lives here.</summary>
public static class Api
{
    public sealed record ErrorEnvelope(string Code, string Message, IReadOnlyList<CompositionIssue> Issues, bool Retryable = false);

    /// <param name="ViewVerbs">The verbs of the screen a line may say (ADR-0016 §3, note 21 of step 2): listed apart from the capabilities, which they are not.</param>
    /// <param name="ShortForms">The words an application's users type for a line of a kernel verb (ADR-0016 §3, note 29 of step 3): listed apart too, since what runs is the kernel command.</param>
    /// <param name="DraftCommands">The ids of the commands and the verbs read while a draft is open (ADR-0016 §10, note 55): a line lists the rest as unavailable.</param>
    public sealed record Catalog(
        IEnumerable<CapabilityDescriptor> Capabilities,
        IEnumerable<ContractDefinition> Contracts,
        IEnumerable<WorkflowDefinition> Workflows,
        IEnumerable<ViewDefinition> Views,
        IEnumerable<ActionDefinition> Actions,
        IEnumerable<ViewVerbDescriptor> ViewVerbs,
        IEnumerable<ShortFormDescriptor> ShortForms,
        IEnumerable<string> DraftCommands);

    public sealed record RunRequest(WorkflowDefinition Workflow, JsonObject Context, JsonObject Input, string IdempotencyKey);

    public sealed record ActionRequest(IReadOnlyDictionary<string, JsonNode?> Parameters, JsonObject Context, string IdempotencyKey);

    public sealed record CommandRequest(JsonObject Inputs, string IdempotencyKey);

    /// <summary>A line as typed, for a workspace, at the revision its caller read where it says one (ADR-0016 §5).</summary>
    /// <param name="View">What the screen behind the line showed as it opened (ADR-0016 §9, note 16 of step 2); none where no screen is.</param>
    public sealed record PreviewRequest(string Line, string WorkspaceId, long? Revision = null, LineView? View = null);

    /// <summary>A grid's view, whose selection's rows are asked for (ADR-0016 note 37 of step 3).</summary>
    public sealed record SelectionRequest(LineView View);

    /// <summary>The view of a grid whose page holds every row, to be told the rows its conditions leave (ADR-0016 note 41 of step 4).</summary>
    public sealed record ShownRequest(LineView View);

    public sealed record ViewValidationRequest(ViewDefinition View, IReadOnlyList<string> BoundNames);

    public sealed record HistoryRequest(string? Domain, int Start, int Count);

    /// <summary>
    /// Who the page is for, as its account menu names them: the user's id, and their desk where configuration names one.
    /// Until ME-ID sign-in (C30) the user is the host's <c>Qube:DemoUser</c>.
    /// </summary>
    public sealed record Me(string Id, string? Desk);

    public static void MapQubeApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/catalog", (CompositionRegistry registry, CommandLines lines) => new Catalog(
            registry.CapabilityDescriptors, registry.ContractDefinitions, registry.WorkflowDefinitions,
            registry.ViewDefinitions, registry.ActionDefinitions, lines.ViewVerbs, lines.ShortForms, lines.DraftCommands));

        // The signed-in user's settings (ADR-0017 §2), as settings.read answers them, for the page as it starts. Written
        // through the gateway, as settings.set and settings.reset.
        api.MapGet("/settings", (UserSettings settings, CurrentUser user) => SettingsContracts.Listed(settings, settings.Read(user.Id)));

        // Who the page is for (the header's account menu, ADR-0017 §4).
        api.MapGet("/me", (CurrentUser user, Desks desks) => new Me(user.Id, desks.Of(user.Id)));

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
            int? offset, int? limit, string? sort, bool? descending, bool? below, TraceService traces, CurrentUser user) =>
            Traced(() => traces.Leaves(new(runId, caseId, scenario ?? TraceService.BaseScenario, measure), user.Id,
                path ?? throw new TraceRefused("Required", "path names the term whose leaf rows are read."), offset ?? 0, limit ?? TraceService.DefaultPage, sort, descending ?? false, below ?? false)));

        // The entities below a term, each with the sum of its leaf rows' contributions on each side (ADR-0011 §5, ADR-0012 §6).
        api.MapGet("/runs/{runId}/cases/{caseId}/traces/{measure}/entities", (string runId, string caseId, string measure, string? scenario, string? path,
            int? offset, int? limit, TraceService traces, CurrentUser user) =>
            Traced(() => traces.Entities(new(runId, caseId, scenario ?? TraceService.BaseScenario, measure), user.Id,
                path ?? throw new TraceRefused("Required", "path names the term whose entities are read."), offset ?? 0, limit ?? TraceService.DefaultPage)));

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

        // Commands (ADR-0005), through the one gateway and its two doors (ADR-0016 §5): the caller is the signed-in user,
        // acting through the AI or on a page. The server names the caller and where the command came from, never the
        // request. A page's write endpoints go as its commands move behind the second door.
        foreach (var (door, origin) in new[] { ("/agent/commands", RunOrigin.Ai), ("/commands", RunOrigin.Pricer) })
            api.MapPost(door + "/{capabilityId}/{version}", async (string capabilityId, string version, CommandRequest request,
                CommandService commands, CurrentUser user, CancellationToken cancellationToken) =>
                await commands.InvokeAsync(capabilityId, version, request.Inputs, new Caller(user.Id, origin), request.IdempotencyKey, cancellationToken) switch
                {
                    CommandResult.Done done => Results.Ok(new { capabilityId = done.CapabilityId, version = done.Version, outputs = done.Outputs }),
                    CommandResult.Refused refused when DoorStatus(refused.Code) is { } status =>
                        Results.Json(new ErrorEnvelope(refused.Code, refused.Issues[0].Message, refused.Issues, Retryable: status == 409), statusCode: status),
                    CommandResult.Refused refused => Rejected(refused.Code, refused.Issues),
                    _ => throw new InvalidOperationException("Unhandled command result."),
                });

        // The preview (ADR-0016 §5): the server reads a line as typed and executes nothing. A line it cannot read is an
        // answer, with the word at fault; a workspace it may not read, or one that moved, is refused as the door refuses it.
        api.MapPost("/commands/preview", (PreviewRequest request, CommandLines lines, CurrentUser user) =>
        {
            try
            {
                return Results.Ok(lines.Preview(request.Line, request.WorkspaceId, request.Revision, new Caller(user.Id, RunOrigin.Pricer), request.View));
            }
            catch (WorkspaceRefused refused)
            {
                return ViewRefused(refused, "the preview");
            }
        });

        // The content of a line of `copy` or `export` (ADR-0016 note 44 of step 4): what the page asks for at Enter, read
        // from the same line at the revision its reading named. A line that cannot be read is an answer; a workspace
        // that has moved is a conflict, to be read again.
        api.MapPost("/commands/content", (PreviewRequest request, CommandLines lines, CurrentUser user) =>
        {
            try
            {
                return Results.Ok(lines.Content(request.Line, request.WorkspaceId, request.Revision, new Caller(user.Id, RunOrigin.Pricer), request.View));
            }
            catch (WorkspaceRefused refused)
            {
                return ViewRefused(refused, "the content");
            }
        });

        // The rows a view's selection names (ADR-0016 note 37 of step 3): what a page reads to make a command's typed
        // value from a grid the server pages. A view of an order that has moved is a conflict, to be read again; one that
        // names what the workspace has not is the page's own fault.
        api.MapPost("/workspaces/{workspaceId}/selection", (string workspaceId, SelectionRequest request, CommandLines lines, CurrentUser user) =>
        {
            try
            {
                return Results.Ok(lines.Selection(workspaceId, new Caller(user.Id, RunOrigin.Pricer), request.View));
            }
            catch (WorkspaceRefused refused)
            {
                return ViewRefused(refused, "the selection");
            }
        });

        // The rows a view's grid shows (ADR-0016 note 41 of step 4): what a page that holds every row reads, at each
        // revision it shows, to show the rows its grid's conditions leave. Conditions that cannot be read are refused
        // as that, and no retry reads them.
        api.MapPost("/workspaces/{workspaceId}/shown", (string workspaceId, ShownRequest request, CommandLines lines, CurrentUser user) =>
        {
            try
            {
                return Results.Ok(lines.Shown(workspaceId, new Caller(user.Id, RunOrigin.Pricer), request.View));
            }
            catch (WorkspaceRefused refused)
            {
                return ViewRefused(refused, "the rows shown");
            }
        });

        // A draft's form (ADR-0016 §10, note 54): what a page draws a row's dialog from, read from the lines the page
        // holds and writing nothing. A draft the workspace has left is a conflict, with what moved it; a table or a row
        // the workspace has not is not found.
        api.MapPost("/workspaces/{workspaceId}/draft", (string workspaceId, LineDraft draft, CommandLines lines, CurrentUser user) =>
        {
            try
            {
                return Results.Ok(lines.Draft(workspaceId, new Caller(user.Id, RunOrigin.Pricer), draft));
            }
            catch (WorkspaceRefused refused)
            {
                return ViewRefused(refused, "the draft");
            }
            catch (TableRefused refused) when (refused.Code is "UnknownTable" or "UnknownRow")
            {
                return NotFound(refused.Code, refused.Message);
            }
        });

        // A workspace's tables as a screen is told of them (ADR-0016 §4, note 25 of step 2): the actions a grid bound to
        // each offers in its Context Menu. Refused as the door refuses a workspace: unknown, or another user's.
        api.MapGet("/workspaces/{workspaceId}/tables", (string workspaceId, CommandLines lines, CurrentUser user) =>
        {
            try
            {
                return Results.Ok(lines.Tables(workspaceId, new Caller(user.Id, RunOrigin.Pricer)));
            }
            catch (WorkspaceRefused refused)
            {
                var status = DoorStatus(refused.Code) ?? throw new InvalidOperationException($"No status is set for the tables' refusal {refused.Code}.");
                return Results.Json(new ErrorEnvelope(refused.Code, refused.Message, []), statusCode: status);
            }
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
                "InvalidOrder" or "InvalidRange" or "Required" or "NoAllocation" or "NoContributions" => 422,
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

    /// <summary>
    /// The status of a refusal that is about what a command or a line was sent to, the same wherever the door answers
    /// it: no such command or workspace, another user's workspace, a workspace that has moved (which may be tried again
    /// once read). None for a refusal about the request itself, which is its endpoint's to place.
    /// </summary>
    static int? DoorStatus(string code) => code switch
    {
        "UnknownCapability" or "UnknownWorkspace" => 404,
        "NotOwner" => 403,
        "RevisionMoved" => 409,
        _ => null,
    };

    // A reading asked with a view, refused (the preview, a view's selection). A view that names what the workspace has
    // not is the page's own fault: a bad request, never a reading. One whose order has moved since its rows were read
    // is a conflict, to be read again. Else the workspace is refused as the door refuses it.
    static IResult ViewRefused(WorkspaceRefused refused, string reading)
    {
        var status = refused.Code switch
        {
            "InvalidView" => 400,
            "ViewMoved" => 409,
            "FilterUnread" => 422,
            _ => DoorStatus(refused.Code) ?? throw new InvalidOperationException($"No status is set for {reading}'s refusal {refused.Code}."),
        };
        return Results.Json(new ErrorEnvelope(refused.Code, refused.Message, [], Retryable: status == 409), statusCode: status);
    }

    static IResult NotFound(string code, string message) => Results.Json(new ErrorEnvelope(code, message, []), statusCode: 404);
}
