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
    /// <summary>A case to remove, by its title or id (<c>case remove</c>), or every case (<c>cases clear</c>).</summary>
    public sealed record CaseRequest(long Revision, string? Name = null);
    /// <summary>
    /// <c>vary</c> (ADR-0010 §3, §9): the values, each as written, of a cell named as the command line names it
    /// (<c>CSA-A threshold</c>), or of an entity's field, as <em>Vary values…</em> on a cell names it.
    /// </summary>
    public sealed record VaryRequest(long Revision, IReadOnlyList<string> Values, string? Cell = null, string? Entity = null, string? Field = null);
    /// <summary>What <c>vary</c> made, by title, and each value it skipped with why.</summary>
    public sealed record Varied(PortfolioWorkspaceView Workspace, IReadOnlyList<string> Made, IReadOnlyList<string> Skipped);
    /// <summary><c>cds &lt;words&gt;</c> (ADR-0010 §8's note of 2026-11-09): the CDS as written.</summary>
    public sealed record CdsRequest(long Revision, string Cds);
    /// <summary>What <c>cds</c> chose, as the command line says it.</summary>
    public sealed record CdsChosen(PortfolioWorkspaceView Workspace, string Said);
    /// <summary>The CDS catalogue the Credit tab searches: the single names, and the keys a proxy is chosen by.</summary>
    public sealed record CdsCatalogue(IReadOnlyList<CdsCurve> SingleNames, IReadOnlyList<string> Sectors, IReadOnlyList<string> Regions,
        IReadOnlyList<string> Ratings, IReadOnlyList<string> Tenors);
    /// <summary>What <c>add trades</c> made, by title, and the cases of the set it replaced (docs/03 §5).</summary>
    public sealed record TradesAdded(PortfolioWorkspaceView Workspace, IReadOnlyList<string> Made, IReadOnlyList<string> Replaced);
    /// <summary><c>scenario &lt;name&gt; &lt;shift&gt;; …</c> (ADR-0010 §4): the scenario's name and its shifts as written.</summary>
    public sealed record ScenarioRequest(long Revision, string Name, string Shifts);
    /// <summary>The Scenarios grid's rows as edited (ADR-0010 §4): each an existing shift's id, or none for a blank row,
    /// where the grid shows it, and its cells as typed, by column.</summary>
    public sealed record ShiftsRequest(long Revision, IReadOnlyList<ShiftRowRequest> Rows);
    public sealed record ShiftRowRequest(string? Id, int Row, Dictionary<string, string?> Cells);
    public sealed record IncludeRequest(long Revision, string Name, bool Included);
    public sealed record ScenarioNameRequest(long Revision, string Name);
    public sealed record DuplicateRequest(long Revision, string Name, string Copy);
    /// <summary><c>scenario ladder …</c> or <c>scenario cross …</c>: the generator and its text as typed.</summary>
    public sealed record GenerateRequest(long Revision, string Generator, string Text);
    public sealed record RevisionRequest(long Revision);
    public sealed record SaveSetRequest(long Revision, string Set, string? Share = null);
    public sealed record LoadSetRequest(long Revision, string Set);
    /// <summary>What a scenario command made, and those of the same name it replaced.</summary>
    public sealed record ScenariosMade(PortfolioWorkspaceView Workspace, IReadOnlyList<string> Made, IReadOnlyList<string> Replaced);
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

        ws.MapPost("/{id}/cds", (string id, CdsRequest request, CurrentUser user, PortfolioWorkspaces service) => Guard(() =>
        {
            var (state, credit) = service.ChooseCds(id, request.Revision, user.Id, request.Cds);
            return Results.Ok(new CdsChosen(service.View(id, user.Id), PortfolioWorkspaces.CdsSaid(((PortfolioSourceVersion)state.Source).Registration, credit)));
        }));

        ws.MapPost("/{id}/add-trades", (string id, CaseRequest request, CurrentUser user, PortfolioWorkspaces service) => Guard(() =>
        {
            var answer = service.AddTrades(id, request.Revision, user.Id);
            return Results.Ok(new TradesAdded(service.View(id, user.Id), answer.Made, answer.Replaced));
        }));

        // Scenarios (ADR-0010 §4 and its note of 2026-11-09): named sets of market-data shifts, crossed with every case.
        ws.MapPost("/{id}/scenarios", (string id, ScenarioRequest request, CurrentUser user, PortfolioWorkspaces service) => Guard(() =>
        {
            var answer = service.DefineScenario(id, request.Revision, user.Id, request.Name, request.Shifts);
            return Results.Ok(new ScenariosMade(service.View(id, user.Id), answer.Made, answer.Replaced));
        }));

        // The Scenarios tab (ADR-0010 §4): the grid's edits as one batch, and the list's include, remove and duplicate.
        ws.MapPost("/{id}/shifts", (string id, ShiftsRequest request, CurrentUser user, PortfolioWorkspaces service) => Guard(() =>
        {
            service.EditShifts(id, request.Revision, user.Id, [.. request.Rows.Select(r => new ShiftEdit(r.Id, r.Row, r.Cells))]);
            return Results.Ok(service.View(id, user.Id));
        }));

        ws.MapPost("/{id}/scenarios/generate", (string id, GenerateRequest request, CurrentUser user, PortfolioWorkspaces service) => Guard(() =>
        {
            var answer = service.GenerateScenarios(id, request.Revision, user.Id, request.Generator, request.Text);
            return Results.Ok(new ScenariosMade(service.View(id, user.Id), answer.Made, answer.Replaced));
        }));

        ws.MapPost("/{id}/scenarios/include", (string id, IncludeRequest request, CurrentUser user, PortfolioWorkspaces service) => Guard(() =>
        {
            service.IncludeScenario(id, request.Revision, user.Id, request.Name, request.Included);
            return Results.Ok(service.View(id, user.Id));
        }));

        ws.MapPost("/{id}/scenarios/remove", (string id, ScenarioNameRequest request, CurrentUser user, PortfolioWorkspaces service) => Guard(() =>
        {
            service.RemoveScenario(id, request.Revision, user.Id, request.Name);
            return Results.Ok(service.View(id, user.Id));
        }));

        ws.MapPost("/{id}/scenarios/duplicate", (string id, DuplicateRequest request, CurrentUser user, PortfolioWorkspaces service) => Guard(() =>
        {
            var answer = service.DuplicateScenario(id, request.Revision, user.Id, request.Name, request.Copy);
            return Results.Ok(new ScenariosMade(service.View(id, user.Id), answer.Made, answer.Replaced));
        }));

        // Saved sets (ADR-0010 §4, C32): the user's, their desk's and all's; load adds a set's scenarios to the workspace.
        ws.MapPost("/{id}/scenarios/save", (string id, SaveSetRequest request, CurrentUser user, PortfolioWorkspaces service) =>
            Guard(() => Results.Ok(service.SaveScenarios(id, request.Revision, user.Id, request.Set, request.Share))));

        ws.MapPost("/{id}/scenarios/load", (string id, LoadSetRequest request, CurrentUser user, PortfolioWorkspaces service) => Guard(() =>
        {
            var answer = service.LoadScenarios(id, request.Revision, user.Id, request.Set);
            return Results.Ok(new ScenariosMade(service.View(id, user.Id), answer.Made, answer.Replaced));
        }));

        api.MapGet("/scenario-sets", (CurrentUser user, PortfolioWorkspaces service) => Guard(() => Results.Ok(service.ScenarioSets(user.Id))));

        ws.MapPost("/{id}/scenarios/clear", (string id, RevisionRequest request, CurrentUser user, PortfolioWorkspaces service) => Guard(() =>
        {
            service.ClearScenarios(id, request.Revision, user.Id);
            return Results.Ok(service.View(id, user.Id));
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
