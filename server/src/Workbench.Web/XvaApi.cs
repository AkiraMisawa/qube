using System.Text.Json.Nodes;
using Workbench.Composition.Application;
using Workbench.Composition.Application.Workspaces;
using Workbench.Samples.Portfolio;
using Workbench.Samples.Xva;

namespace Workbench.Web;

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
    /// <summary>A case to remove, by its title or id (<c>case remove</c>), or every case (<c>cases clear</c>).</summary>
    public sealed record CaseRequest(long Revision, string? Name = null);
    /// <summary>
    /// <c>vary</c> (ADR-0010 §3, §9): the values, each as written, of a cell named as the command line names it
    /// (<c>CSA-A threshold</c>), or of an entity's field, as <em>Vary values…</em> on a cell names it.
    /// </summary>
    public sealed record VaryRequest(long Revision, IReadOnlyList<string> Values, string? Cell = null, string? Entity = null, string? Field = null);
    /// <summary>What <c>vary</c> made, by title, and each value it skipped with why.</summary>
    public sealed record Varied(PortfolioWorkspaceView Workspace, IReadOnlyList<string> Made, IReadOnlyList<string> Skipped);
    /// <summary>What <c>add trades</c> made, by title, and the cases of the set it replaced (docs/03 §5).</summary>
    public sealed record TradesAdded(PortfolioWorkspaceView Workspace, IReadOnlyList<string> Made, IReadOnlyList<string> Replaced);
    public sealed record ValueDateRequest(long Revision, DateOnly ValueDate);
    public sealed record CounterpartyRequest(long Revision, string Registration, int ConfirmedCount);
    public sealed record PriceRequest(long Revision, string IdempotencyKey);
    public sealed record MeasuresRequest(long Revision, IReadOnlyList<string> Measures);
    public sealed record RefreshRequest(long Revision);
    /// <summary>Asks again for the failed results on one trade, or on every trade when none is named (ADR-0010 §6).</summary>
    public sealed record RetryRequest(string? TradeId = null);
    public sealed record Retried(PortfolioWorkspaceView Workspace, int Asked);
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
        api.MapGet("/runs/{runId}/quote", (string runId, RunService runs, IDataStore store, CurrentUser user) => Guard(() =>
        {
            if (runs.Find(runId) is not { } run) throw new WorkspaceRefused("UnknownRun", $"Run {runId} does not exist.");
            if (run.Owner != user.Id) throw new WorkspaceRefused("NotOwner", $"Run {runId} belongs to another user.");
            return Results.Ok(XvaQuote.Of(run, store));
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

        ws.MapPost("/{id}/measures", (string id, MeasuresRequest request, CurrentUser user, PortfolioWorkspaces service) => Guard(() =>
        {
            service.SetMeasures(id, request.Revision, user.Id, request.Measures);
            return Results.Ok(service.View(id, user.Id));
        }));

        ws.MapPost("/{id}/vary", (string id, VaryRequest request, CurrentUser user, PortfolioWorkspaces service) => Guard(() =>
        {
            var answer = (request.Cell, request.Entity, request.Field) switch
            {
                (null, { } entity, { } field) => service.VaryField(id, request.Revision, user.Id, entity, field, request.Values),
                ({ } cell, null, null) => service.Vary(id, request.Revision, user.Id, cell, request.Values),
                _ => throw new WorkspaceRefused("UnknownCell", "vary names a cell, or an entity and its field, and not both."),
            };
            return Results.Ok(new Varied(service.View(id, user.Id), answer.Made, answer.Skipped));
        }));

        ws.MapPost("/{id}/add-trades", (string id, CaseRequest request, CurrentUser user, PortfolioWorkspaces service) => Guard(() =>
        {
            var answer = service.AddTrades(id, request.Revision, user.Id);
            return Results.Ok(new TradesAdded(service.View(id, user.Id), answer.Made, answer.Replaced));
        }));

        ws.MapGet("/{id}/changes", (string id, CurrentUser user, PortfolioWorkspaces service) =>
            Guard(() => Results.Ok(service.Changes(id, user.Id))));

        ws.MapPost("/{id}/changes", (string id, ChangeRequest request, CurrentUser user, PortfolioWorkspaces service) => Guard(() =>
        {
            service.SetText(id, request.Revision, user.Id, request.Entity, request.Field, request.Text, request.Case);
            return Results.Ok(service.View(id, user.Id));
        }));

        ws.MapPost("/{id}/cases/remove", (string id, CaseRequest request, CurrentUser user, PortfolioWorkspaces service) => Guard(() =>
        {
            service.RemoveCase(id, request.Revision, user.Id, request.Name ?? throw new WorkspaceRefused("UnknownCase", "case remove names the case to remove."));
            return Results.Ok(service.View(id, user.Id));
        }));

        ws.MapPost("/{id}/cases/clear", (string id, CaseRequest request, CurrentUser user, PortfolioWorkspaces service) => Guard(() =>
        {
            service.ClearCases(id, request.Revision, user.Id);
            return Results.Ok(service.View(id, user.Id));
        }));

        ws.MapPost("/{id}/refresh", (string id, RefreshRequest request, CurrentUser user, PortfolioWorkspaces service) => Guard(() =>
        {
            var (_, carried) = service.Refresh(id, request.Revision, user.Id);
            return Results.Ok(new Reacquired(service.View(id, user.Id), carried));
        }));

        ws.MapPost("/{id}/retry", (string id, RetryRequest request, CurrentUser user, PortfolioWorkspaces service) => Guard(() =>
        {
            var asked = service.Retry(id, user.Id, request.TradeId);
            return Results.Ok(new Retried(service.View(id, user.Id), asked));
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

        ws.MapPost("/{id}/price", (string id, PriceRequest request, CurrentUser user, PortfolioWorkspaces service) => Guard(() =>
            service.Price(id, request.Revision, user.Id, request.IdempotencyKey) switch
            {
                SubmissionResult.Accepted accepted => Results.Accepted($"/api/runs/{accepted.Run.RunId}", accepted.Run),
                SubmissionResult.Rejected rejected => Results.Json(new Api.ErrorEnvelope(rejected.Code, "The Run was not accepted.", rejected.Issues), statusCode: 422),
                _ => throw new InvalidOperationException("Unhandled submission result."),
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
                "UnknownWorkspace" or "UnknownRun" => 404,
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
