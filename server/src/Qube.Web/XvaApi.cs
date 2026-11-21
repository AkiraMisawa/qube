using System.Text.Json.Nodes;
using Qube.Composition.Application;
using Qube.Composition.Application.Workspaces;
using Qube.Samples.Portfolio;
using Qube.Samples.Xva;

namespace Qube.Web;

/// <summary>Thin HTTP endpoints over the XVA workspace service. No rule lives here.</summary>
public static class XvaApi
{
    public sealed record CreateRequest(string Registration, DateOnly ValueDate, int PortfolioSize, string? Project = null);
    /// <summary>A page of trades; <c>Grid</c> names the New or Existing grid (ADR-0010 §6), or all when absent.</summary>
    /// <param name="Where">The grid's conditions as typed (ADR-0016 note 41 of step 4), where it is filtered from the line.</param>
    public sealed record PageRequest(int Start, int Count, IReadOnlyList<RowSort> Sorts, string? Grid = null, string? Where = null);
    public sealed record EditRequest(long Revision, string Entity, string Field, JsonNode? Value);
    /// <summary>Resolves a held edit, or removes a change (<c>reset</c>); <c>Case</c> names the case it is a change of, none
    /// for a Common edit (ADR-0010 §3).</summary>
    public sealed record ResolveRequest(long Revision, string Entity, string Field, string Resolution, string? Case = null);
    /// <summary>A change's value as written, set from the Changes tab in the Base or a case (ADR-0010 §3's note).</summary>
    public sealed record ChangeRequest(long Revision, string Entity, string Field, string Text, string? Case = null);

    /// <summary>
    /// The trades *Make package…* lists, to be told which a package would add and which it would take out, at the revision
    /// the page shows, which the press then writes at (ADR-0016 note 96).
    /// </summary>
    public sealed record PackageRolesRequest(long? Revision = null, IReadOnlyList<string>? Trades = null);
    /// <summary>The CDS catalogue the Credit tab searches: the single names, and the keys a proxy is chosen by.</summary>
    public sealed record CdsCatalogue(IReadOnlyList<CdsCurve> SingleNames, IReadOnlyList<string> Sectors, IReadOnlyList<string> Regions,
        IReadOnlyList<string> Ratings, IReadOnlyList<string> Tenors);
    /// <summary>The Scenarios grid's rows as edited (ADR-0010 §4): each an existing shift's id, or none for a blank row,
    /// where the grid shows it, and its cells as typed, by column.</summary>
    public sealed record ShiftsRequest(long Revision, IReadOnlyList<ShiftRowRequest> Rows);
    public sealed record ShiftRowRequest(string? Id, int Row, Dictionary<string, string?> Cells);
    public sealed record WorkspaceSummary(string WorkspaceId, long Revision, JsonObject Context);

    /// <summary>The largest page a request may ask for; ExGrid asks for its Window plus read-ahead.</summary>
    const int MaxPage = 2000;

    public static void MapXvaApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/xva");

        api.MapGet("/registrations", () => PortfolioCatalog.Registrations.Select(r => new
        {
            r.Key, r.Code, r.LegalEntity, r.Name, r.Dummy, Agreement = PortfolioCatalog.AgreementOf(r.Key),
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
            var created = service.Create(user.Id, request.Registration, request.ValueDate, request.PortfolioSize, request.Project);
            return Results.Created($"/api/xva/workspaces/{created.WorkspaceId}", service.View(created.WorkspaceId, user.Id));
        }));

        ws.MapGet("/{id}", (string id, CurrentUser user, PortfolioWorkspaces service) =>
            Guard(() => Results.Ok(service.View(id, user.Id))));

        ws.MapPost("/{id}/rows", (string id, PageRequest request, CurrentUser user, PortfolioWorkspaces service) => Guard(() =>
        {
            if (request.Count > MaxPage) throw new WorkspaceRefused("InvalidRange", $"A page holds at most {MaxPage} rows.");
            return Results.Ok(service.Page(id, user.Id, request.Start, request.Count, request.Sorts, request.Grid, request.Where));
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

        // What a change of counterparty would drop, which the toolbar's confirmation lists by kind before it invokes
        // `counterparty` (ADR-0003 §6). The value date and the counterparty are changed by their commands, which the
        // toolbar invokes as the line does (ADR-0016 note 100).
        ws.MapGet("/{id}/counterparty-change", (string id, CurrentUser user, PortfolioWorkspaces service) =>
            Guard(() => Results.Ok(service.PreviewCounterpartyChange(id, user.Id))));

        // Which of the trades named a package would add and which it would take out, as *Make package…* lists them
        // before its press: a New row in Before is taken out, as an existing trade is (ADR-0016 note 96).
        ws.MapPost("/{id}/package-roles", (string id, PackageRolesRequest request, CurrentUser user, PortfolioWorkspaces service) =>
            Guard(() => Results.Ok(service.RolesInPackage(id, Read(request.Revision), user.Id, request.Trades))));

        // What Price would price at the revision the page shows, which Price's confirmation lists case by case before
        // it invokes `price` at that revision (ADR-0010 §3's note of 2026-11-19): a workspace moved since is refused,
        // here and at the press, rather than listed or priced unseen.
        // It waits, as Price does, for a cell still being calculated (ADR-0010 §6's note of 2026-11-21).
        ws.MapGet("/{id}/price-plan", (string id, long? revision, CurrentUser user, PortfolioWorkspaces service, CancellationToken cancellationToken) =>
            Guard(async () => Results.Ok(await service.WhenAnswered(() => service.PlanPrice(id, Read(revision), user.Id), cancellationToken))));

        ws.MapDelete("/{id}", (string id, CurrentUser user, PortfolioWorkspaces service) => Guard(() =>
        {
            service.Delete(id, user.Id);
            return Results.NoContent();
        }));
    }

    // The revision a read is asked at, which a read that a command then acts on must name (spine 6): never the latest
    // taken in its stead.
    static long Read(long? revision) =>
        revision ?? throw new WorkspaceRefused("InvalidValue", "Name the revision the page shows: what is read here is what the press that follows acts on.");

    static IResult Guard(Func<IResult> handle)
    {
        try
        {
            return handle();
        }
        catch (WorkspaceRefused refused)
        {
            return Refused(refused);
        }
    }

    static async Task<IResult> Guard(Func<Task<IResult>> handle)
    {
        try
        {
            return await handle();
        }
        catch (WorkspaceRefused refused)
        {
            return Refused(refused);
        }
    }

    static IResult Refused(WorkspaceRefused refused)
    {
        var issues = refused.Targets.Select(t => new CompositionIssue(refused.Code, $"/{t.Entity}/{t.Field}", refused.Message)).ToList();
        var status = refused.Code switch
        {
            "UnknownWorkspace" or "UnknownRun" or "UnknownScenario" => 404,
            "NotOwner" => 403,
            "RevisionMoved" or "ConfirmationStale" or "UnresolvedEdits" or "ConflictUnresolved" or "RunRunning" or PortfolioInput.CellPending => 409,
            _ => 422,
        };
        return Results.Json(new Api.ErrorEnvelope(refused.Code, refused.Message, issues), statusCode: status);
    }
}

/// <summary>
/// The user a request acts as. Until sign-in through ME-ID (C30) is wired, every request acts as the one
/// configured demonstration user; nothing here pretends to authenticate anyone.
/// </summary>
public sealed record CurrentUser(string Id);
