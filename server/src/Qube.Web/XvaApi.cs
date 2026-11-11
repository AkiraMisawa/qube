using System.Text.Json.Nodes;
using Qube.Composition.Application;
using Qube.Composition.Application.Workspaces;
using Qube.Samples.Portfolio;
using Qube.Samples.Xva;

namespace Qube.Web;

/// <summary>Thin HTTP endpoints over the XVA workspace service. No rule lives here.</summary>
public static class XvaApi
{
    public sealed record CreateRequest(string Registration, DateOnly ValueDate, int PortfolioSize);
    /// <summary>A page of trades; <c>Grid</c> names the New or Existing grid (ADR-0010 §6), or all when absent.</summary>
    public sealed record PageRequest(int Start, int Count, IReadOnlyList<RowSort> Sorts, string? Grid = null);
    public sealed record EditRequest(long Revision, string Entity, string Field, JsonNode? Value);
    /// <summary>Resolves a held edit, or removes a change (<c>reset</c>); <c>Case</c> names the case it is a change of, none
    /// for a Common edit (ADR-0010 §3).</summary>
    public sealed record ResolveRequest(long Revision, string Entity, string Field, string Resolution, string? Case = null);
    /// <summary>A change's value as written, set from the Changes tab in the Base or a case (ADR-0010 §3's note).</summary>
    public sealed record ChangeRequest(long Revision, string Entity, string Field, string Text, string? Case = null);
    /// <summary>The CDS catalogue the Credit tab searches: the single names, and the keys a proxy is chosen by.</summary>
    public sealed record CdsCatalogue(IReadOnlyList<CdsCurve> SingleNames, IReadOnlyList<string> Sectors, IReadOnlyList<string> Regions,
        IReadOnlyList<string> Ratings, IReadOnlyList<string> Tenors);
    /// <summary>The Scenarios grid's rows as edited (ADR-0010 §4): each an existing shift's id, or none for a blank row,
    /// where the grid shows it, and its cells as typed, by column.</summary>
    public sealed record ShiftsRequest(long Revision, IReadOnlyList<ShiftRowRequest> Rows);
    public sealed record ShiftRowRequest(string? Id, int Row, Dictionary<string, string?> Cells);
    public sealed record ValueDateRequest(long Revision, DateOnly ValueDate);
    public sealed record CounterpartyRequest(long Revision, string Registration, int ConfirmedCount);
    public sealed record Reacquired(PortfolioWorkspaceView Workspace, CarrySummary Carried);
    public sealed record WorkspaceSummary(string WorkspaceId, long Revision, JsonObject Context);

    /// <summary>The largest page a request may ask for; ExGrid asks for its Window plus read-ahead.</summary>
    const int MaxPage = 2000;

    public static void MapXvaApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/xva");

        api.MapGet("/registrations", () => PortfolioCatalog.Registrations.Select(r => new
        {
            r.Key, r.Code, r.LegalEntity, r.Name, Agreement = PortfolioCatalog.AgreementOf(r.Key),
        }));

        // Copy quote (ADR-0010 §7, §11): read from the Run's retained input and result, never from a screen.
        // It quotes the scenario in view, the base market unless one is named (ADR-0010 §4's note of 2026-11-09).
        api.MapGet("/cds", () => new CdsCatalogue(CdsCatalog.SingleNames, CdsCatalog.Sectors, CdsCatalog.Regions, CdsCatalog.Ratings, CdsCatalog.Tenors));

        api.MapGet("/runs/{runId}/quote", (string runId, string? scenario, RunService runs, IDataStore store, CurrentUser user) => Guard(() =>
        {
            if (runs.Find(runId) is not { } run) throw new WorkspaceRefused("UnknownRun", $"Run {runId} does not exist.");
            if (run.Owner != user.Id) throw new WorkspaceRefused("NotOwner", $"Run {runId} belongs to another user.");
            return Results.Ok(XvaQuote.Of(run, store, scenario ?? PortfolioScenarios.BaseId));
        }));

        var ws = api.MapGroup("/workspaces");

        ws.MapGet("/", (CurrentUser user, PortfolioWorkspaces service) =>
            service.OwnedBy(user.Id).Select(w => new WorkspaceSummary(w.WorkspaceId, w.Revision, w.Context)));

        ws.MapPost("/", (CreateRequest request, CurrentUser user, PortfolioWorkspaces service) => Guard(() =>
        {
            if (request.PortfolioSize is < 0 or > 100_000) throw new WorkspaceRefused("InvalidValue", "The portfolio size is from 0 to 100,000.");
            var created = service.Create(user.Id, request.Registration, request.ValueDate, request.PortfolioSize);
            return Results.Created($"/api/xva/workspaces/{created.WorkspaceId}", service.View(created.WorkspaceId, user.Id));
        }));

        ws.MapGet("/{id}", (string id, CurrentUser user, PortfolioWorkspaces service) =>
            Guard(() => Results.Ok(service.View(id, user.Id))));

        ws.MapPost("/{id}/rows", (string id, PageRequest request, CurrentUser user, PortfolioWorkspaces service) => Guard(() =>
        {
            if (request.Count > MaxPage) throw new WorkspaceRefused("InvalidRange", $"A page holds at most {MaxPage} rows.");
            return Results.Ok(service.Page(id, user.Id, request.Start, request.Count, request.Sorts, request.Grid));
        }));

        ws.MapPost("/{id}/edits", (string id, EditRequest request, CurrentUser user, PortfolioWorkspaces service) => Guard(() =>
        {
            service.Edit(id, request.Revision, user.Id, request.Entity, request.Field, request.Value);
            return Results.Ok(service.View(id, user.Id));
        }));

        // The Scenarios tab's grid (ADR-0010 §4): its edits as one batch. A scenario by name (define, include, remove,
        // duplicate, clear, the generators and the saved sets) is a command of the gateway (ADR-0016 note 32).
        ws.MapPost("/{id}/shifts", (string id, ShiftsRequest request, CurrentUser user, PortfolioWorkspaces service) => Guard(() =>
        {
            service.EditShifts(id, request.Revision, user.Id, [.. request.Rows.Select(r => new ShiftEdit(r.Id, r.Row, r.Cells))]);
            return Results.Ok(service.View(id, user.Id));
        }));

        ws.MapGet("/{id}/changes", (string id, CurrentUser user, PortfolioWorkspaces service) =>
            Guard(() => Results.Ok(service.Changes(id, user.Id))));

        ws.MapPost("/{id}/changes", (string id, ChangeRequest request, CurrentUser user, PortfolioWorkspaces service) => Guard(() =>
        {
            service.SetText(id, request.Revision, user.Id, request.Entity, request.Field, request.Text, request.Case);
            return Results.Ok(service.View(id, user.Id));
        }));

        ws.MapPost("/{id}/resolutions", (string id, ResolveRequest request, CurrentUser user, PortfolioWorkspaces service) => Guard(() =>
        {
            service.Resolve(id, request.Revision, user.Id, request.Entity, request.Field, request.Resolution, request.Case);
            return Results.Ok(service.View(id, user.Id));
        }));

        ws.MapPost("/{id}/value-date", (string id, ValueDateRequest request, CurrentUser user, PortfolioWorkspaces service) => Guard(() =>
        {
            var (_, carried) = service.ChangeValueDate(id, request.Revision, user.Id, request.ValueDate);
            return Results.Ok(new Reacquired(service.View(id, user.Id), carried));
        }));

        ws.MapGet("/{id}/counterparty-change", (string id, CurrentUser user, PortfolioWorkspaces service) =>
            Guard(() => Results.Ok(service.PreviewCounterpartyChange(id, user.Id))));

        ws.MapPost("/{id}/counterparty", (string id, CounterpartyRequest request, CurrentUser user, PortfolioWorkspaces service) => Guard(() =>
        {
            var (_, carried) = service.ChangeCounterparty(id, request.Revision, user.Id, request.Registration, request.ConfirmedCount);
            return Results.Ok(new Reacquired(service.View(id, user.Id), carried));
        }));

        ws.MapDelete("/{id}", (string id, CurrentUser user, PortfolioWorkspaces service) => Guard(() =>
        {
            service.Delete(id, user.Id);
            return Results.NoContent();
        }));
    }

    static IResult Guard(Func<IResult> handle)
    {
        try
        {
            return handle();
        }
        catch (WorkspaceRefused refused)
        {
            var issues = refused.Targets.Select(t => new CompositionIssue(refused.Code, $"/{t.Entity}/{t.Field}", refused.Message)).ToList();
            var status = refused.Code switch
            {
                "UnknownWorkspace" or "UnknownRun" or "UnknownScenario" or "UnknownSaved" => 404,
                "NotOwner" => 403,
                "RevisionMoved" or "ConfirmationStale" or "UnresolvedEdits" or "ConflictUnresolved" or "RunRunning" => 409,
                _ => 422,
            };
            return Results.Json(new Api.ErrorEnvelope(refused.Code, refused.Message, issues), statusCode: status);
        }
    }
}

/// <summary>
/// The user a request acts as. Until sign-in through ME-ID (C30) is wired, every request acts as the one
/// configured demonstration user; nothing here pretends to authenticate anyone.
/// </summary>
public sealed record CurrentUser(string Id);
