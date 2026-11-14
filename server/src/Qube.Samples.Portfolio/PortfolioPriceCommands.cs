using System.Globalization;
using System.Text.Json.Nodes;
using Qube.Composition.Application;
using Qube.Composition.Application.Tables;
using Qube.Composition.Application.Workspaces;
using Qube.Composition.Contracts;

namespace Qube.Samples.Portfolio;

// The pricer's commands that start work or read the source again (ADR-0010 §1, §6, §9, ADR-0016 note 33 of step 3):
// `price`, `refresh` and `pv01 retry`, and `value date` and `counterparty` (`cpty`), which read it on another date or
// for another counterparty (note 69). Each says how it is typed and reads its own line, as the commands that edit do,
// but none is tried: a Run submitted, a source read again and a result asked for again cannot be done and not done.
// A reading says what would start, and refuses what the command would refuse before it started anything.

public static class PortfolioPriceContracts
{
    public const string PriceRequest = "xva.price-request";
    public const string PriceAccepted = "xva.price-accepted";
    public const string RefreshRequest = "xva.refresh-request";
    public const string Refreshed = "xva.refreshed";
    public const string RetryRequest = "xva.pv01-retry-request";
    public const string ValueDateRequest = "xva.value-date-request";
    public const string CounterpartyRequest = "xva.counterparty-request";
    public const string Moved = "xva.source-moved";

    /// <summary>
    /// What a reading of <c>price</c> says Price would price: the measures, the cases, the markets where a scenario is
    /// included, the side priced alone where the other holds no trade, and what a case's input already refuses.
    /// </summary>
    public static string WouldPrice(PricePlan plan)
    {
        var cases = TableWords.Counted(plan.Cases, "case");
        var on = plan.Markets == 1 ? cases : $"{cases} on {plan.Markets} markets (the base market and {TableWords.Counted(plan.Markets - 1, "scenario")})";
        var side = plan.Before && !plan.After ? ", Before only" : !plan.Before && plan.After ? ", After only" : "";
        var refused = plan.Refused.Count == 0 ? ""
            : $" {TableWords.Counted(plan.Refused.Count, "figure")} of it would be refused, first {plan.Refused[0].Measure} of {plan.Refused[0].Case}: {plan.Refused[0].Message}";
        return $"Prices {TableWords.And(plan.Measures)} for {on}{side}, as one Run.{refused}";
    }

    /// <summary>What <c>refresh</c> says back: the edits to the portfolio's data that carried, and those held for the user.</summary>
    public static string Carried(CarrySummary carried) => $"Refreshed: {carried.Carried} edit(s) carried, {carried.Conflicted} in conflict, {carried.Lost} lost.";

    /// <summary>What <c>value date</c> says back: the date moved to, and what carried and what is held.</summary>
    public static string ValueDateMoved(DateOnly valueDate, CarrySummary carried) =>
        $"Value date moved to {valueDate:yyyy-MM-dd}: {carried.Carried} edit(s) carried, {carried.Conflicted} in conflict, {carried.Lost} lost.";

    /// <summary>What <c>counterparty</c> says back: the counterparty moved to, what was dropped, and what carried and what is held.</summary>
    public static string CounterpartyChanged(string registration, int dropped, CarrySummary carried) =>
        $"Counterparty changed to {registration}: {dropped} edit(s) dropped; {carried.Carried} carried, {carried.Conflicted} in conflict, {carried.Lost} lost.";

    /// <summary>The answer of <c>value date</c> and <c>counterparty</c>: where the workspace was left, and what was dropped, carried and held.</summary>
    public static JsonObject MovedAnswer(WorkspaceState state, string said, int dropped, CarrySummary carried) => new()
    {
        ["workspaceId"] = state.WorkspaceId, ["revision"] = state.Revision, ["said"] = said, ["dropped"] = dropped,
        ["carried"] = carried.Carried, ["conflicted"] = carried.Conflicted, ["lost"] = carried.Lost, ["stays"] = carried.Conflicted + carried.Lost > 0,
    };

    /// <summary>What <c>pv01 retry</c> says back.</summary>
    public static string Retried(int asked) => asked == 0 ? "No result had failed, so nothing was asked again." : $"Asked again for {asked} result(s).";

    public static void Register(CompositionRegistryBuilder builder)
    {
        JsonObject Integer(string title) => new() { ["type"] = "integer", ["title"] = title };
        void Contract(string id, string title, JsonObject properties, params string[] required) =>
            builder.AddContract(new ContractDefinition(id, PortfolioContracts.Version, title, new JsonObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = new JsonArray([.. required.Select(r => JsonValue.Create(r))]),
                ["additionalProperties"] = false,
            }));
        Contract(PriceRequest, "Price the workspace", new JsonObject { ["workspaceId"] = PortfolioCommand.WorkspaceField(), ["revision"] = CommandRequests.RevisionField() }, "workspaceId");
        Contract(PriceAccepted, "Price accepted", new JsonObject
        {
            ["workspaceId"] = new JsonObject { ["type"] = "string", ["title"] = "Workspace" },
            ["revision"] = Integer("Revision priced"),
            ["said"] = new JsonObject { ["type"] = "string", ["title"] = "What was accepted, as the command line says it" },
            ["runId"] = new JsonObject { ["type"] = "string", ["title"] = "Run", ["description"] = "The Run that prices it; its results are read from the Run once it completes." },
        }, "workspaceId", "revision", "said", "runId");
        Contract(RefreshRequest, "Read the source again", new JsonObject { ["workspaceId"] = PortfolioCommand.WorkspaceField(), ["revision"] = CommandRequests.RevisionField() }, "workspaceId");
        Contract(Refreshed, "Workspace refreshed", new JsonObject
        {
            ["workspaceId"] = new JsonObject { ["type"] = "string", ["title"] = "Workspace" },
            ["revision"] = Integer("Revision"),
            ["said"] = new JsonObject { ["type"] = "string", ["title"] = "What carried and what is held, as the command line says it" },
            ["carried"] = Integer("Edits carried"),
            ["conflicted"] = Integer("Edits in conflict"),
            ["lost"] = Integer("Edits lost"),
            ["stays"] = new JsonObject
            {
                ["type"] = "boolean", ["title"] = "Waits for the user",
                ["description"] = "True where an edit is in conflict or lost: what was said waits for the user's decision, and a page keeps it in view.",
            },
        }, "workspaceId", "revision", "said", "carried", "conflicted", "lost", "stays");
        Contract(ValueDateRequest, "Move the value date", new JsonObject
        {
            ["workspaceId"] = PortfolioCommand.WorkspaceField(),
            ["revision"] = CommandRequests.RevisionField(),
            ["valueDate"] = new JsonObject { ["type"] = "string", ["format"] = "date", ["title"] = "Value date", ["description"] = "The date to read the counterparty's trades and CSAs as of, yyyy-MM-dd: another than the workspace's." },
        }, "workspaceId", "valueDate");
        Contract(CounterpartyRequest, "Change the counterparty", new JsonObject
        {
            ["workspaceId"] = PortfolioCommand.WorkspaceField(),
            ["revision"] = CommandRequests.RevisionField(),
            ["registration"] = new JsonObject
            {
                ["type"] = "string", ["minLength"] = 1, ["title"] = "Counterparty",
                ["description"] = "Its code and its legal entity, such as ABC/LE-02; DUMMY/LE-00 is the dummy counterparty. A code alone names none.",
            },
            ["drops"] = new JsonObject
            {
                ["type"] = "integer", ["minimum"] = 0, ["title"] = "Edits dropped",
                ["description"] = "How many edits made for the counterparty the workspace is on the change drops, as its reading says: a workspace that holds another count by then is refused, so nothing is dropped unseen.",
            },
        }, "workspaceId", "registration", "drops");
        Contract(Moved, "Workspace moved", new JsonObject
        {
            ["workspaceId"] = new JsonObject { ["type"] = "string", ["title"] = "Workspace" },
            ["revision"] = Integer("Revision"),
            ["said"] = new JsonObject { ["type"] = "string", ["title"] = "What moved, what was dropped and what carried, as the command line says it" },
            ["dropped"] = Integer("Edits dropped"),
            ["carried"] = Integer("Edits carried"),
            ["conflicted"] = Integer("Edits in conflict"),
            ["lost"] = Integer("Edits lost"),
            ["stays"] = new JsonObject
            {
                ["type"] = "boolean", ["title"] = "Waits for the user",
                ["description"] = "True where an edit is in conflict or lost: what was said waits for the user's decision, and a page keeps it in view.",
            },
        }, "workspaceId", "revision", "said", "dropped", "carried", "conflicted", "lost", "stays");
        Contract(RetryRequest, "Ask again for failed results", new JsonObject
        {
            ["workspaceId"] = PortfolioCommand.WorkspaceField(),
            ["revision"] = CommandRequests.RevisionField(),
            ["trade"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["title"] = "Trade", ["description"] = "One trade's id, such as DRAFT-2. Left out, every trade." },
        }, "workspaceId");
    }
}

/// <summary>
/// <c>xva.workspace.price</c>, typed <c>price</c> (ADR-0010 §1, §9): the workspace as it stands, its selected measures
/// for every case on every market, as one Run. (<c>xva.price</c> is the step that prices XVA inside that Run.) Its
/// reading builds what Price would freeze, keeps and submits nothing, says what would be priced, and refuses what Price
/// would refuse. Invoked, it gives the Run service the key it was invoked with, so that the command asked again is the
/// same business submission (XV13).
/// </summary>
public sealed class PortfolioPriceCommand(Func<PortfolioWorkspaces> workspaces) : ICapabilityHandler, ILineCommand
{
    public CapabilityDescriptor Descriptor { get; } = new("xva.workspace.price", PortfolioContracts.Version,
        "Price an XVA workspace as it stands: its selected measures for every case and every market, as one Run",
        [new("request", PortfolioPriceContracts.PriceRequest, PortfolioContracts.Version, Cardinality.One)],
        [new("answer", PortfolioPriceContracts.PriceAccepted, PortfolioContracts.Version, Cardinality.One)],
        ExecutionKind.RequestResponse, [CapabilityEffect.Execute], new(["price"], [], PortfolioEditRules.App));

    public LineRead Read(LineContext line) =>
        PortfolioCommand.Read(line, [], PortfolioPriceContracts.WouldPrice(
            CommandRequests.Naming(() => workspaces().PlanPrice(line.State.WorkspaceId, line.State.Revision, line.Caller.UserId))));

    public LineCompletions Complete(LineContext line) => LineCompletions.None;

    public Task<IReadOnlyDictionary<string, JsonNode?>> InvokeAsync(CapabilityInvocation invocation, CancellationToken cancellationToken) =>
        PortfolioCommand.Asked(invocation, workspaces, (service, request, caller, revision) =>
        {
            var id = CommandRequests.Text(request, "workspaceId");
            var run = CommandRequests.Accepted(CommandRequests.Naming(() => service.Price(id, revision, caller.UserId, CommandRequests.Key(invocation, "Price"), caller.Origin)));
            return new JsonObject { ["workspaceId"] = id, ["revision"] = revision, ["said"] = $"Price was accepted as Run {run.RunId}.", ["runId"] = run.RunId };
        });
}

/// <summary>
/// <c>xva.refresh</c>, typed <c>refresh</c> (ADR-0003 §6): the counterparty's trades and CSAs read again on the value
/// date the workspace holds, the edits carried onto them or held. Its reading reads no source: it says what would be
/// read again. The answer counts what carried and what is held, and says that it waits for the user where any is held.
/// </summary>
public sealed class PortfolioRefresh(Func<PortfolioWorkspaces> workspaces) : ICapabilityHandler, ILineCommand
{
    public CapabilityDescriptor Descriptor { get; } = new("xva.refresh", PortfolioContracts.Version,
        "Read the counterparty's trades and CSAs of an XVA workspace again; its edits carry onto them or are held",
        [new("request", PortfolioPriceContracts.RefreshRequest, PortfolioContracts.Version, Cardinality.One)],
        [new("answer", PortfolioPriceContracts.Refreshed, PortfolioContracts.Version, Cardinality.One)],
        ExecutionKind.RequestResponse, [CapabilityEffect.EditWorkspace], new(["refresh"], [], PortfolioEditRules.App));

    public LineRead Read(LineContext line)
    {
        var source = PortfolioSourceVersion.Of(PortfolioCases.Base(line.State));
        return PortfolioCommand.Read(line, [], $"Reads {source.Registration.Key} again as of {source.ValueDate:yyyy-MM-dd}, and carries your edits onto what it reads.");
    }

    public LineCompletions Complete(LineContext line) => LineCompletions.None;

    public Task<IReadOnlyDictionary<string, JsonNode?>> InvokeAsync(CapabilityInvocation invocation, CancellationToken cancellationToken) =>
        PortfolioCommand.Asked(invocation, workspaces, (service, request, caller, revision) =>
        {
            var (state, carried) = service.Refresh(CommandRequests.Text(request, "workspaceId"), revision, caller.UserId, caller.Origin);
            return new JsonObject
            {
                ["workspaceId"] = state.WorkspaceId, ["revision"] = state.Revision, ["said"] = PortfolioPriceContracts.Carried(carried),
                ["carried"] = carried.Carried, ["conflicted"] = carried.Conflicted, ["lost"] = carried.Lost, ["stays"] = carried.Conflicted + carried.Lost > 0,
            };
        });
}

/// <summary>
/// <c>xva.value-date.change</c>, typed <c>value date 2026-10-31</c> (ADR-0003 §6, ADR-0016 note 69): the counterparty's
/// trades and CSAs read as of another date, the edits carried onto them or held, as the toolbar's Value date does,
/// which invokes it. Its reading reads no source, as <c>refresh</c>'s does not: it says the date it would move from
/// and to, and refuses a date that is none and the date the workspace is on.
/// </summary>
public sealed class PortfolioValueDate(Func<PortfolioWorkspaces> workspaces) : ICapabilityHandler, ILineCommand
{
    public CapabilityDescriptor Descriptor { get; } = new("xva.value-date.change", PortfolioContracts.Version,
        "Move an XVA workspace to another value date: its counterparty's trades and CSAs are read as of it, and its edits carry onto them or are held",
        [new("request", PortfolioPriceContracts.ValueDateRequest, PortfolioContracts.Version, Cardinality.One)],
        [new("answer", PortfolioPriceContracts.Moved, PortfolioContracts.Version, Cardinality.One)],
        ExecutionKind.RequestResponse, [CapabilityEffect.EditWorkspace],
        new(["value date"], [new("valueDate", CommandArgumentKind.Text, Required: true)], PortfolioEditRules.App));

    public LineRead Read(LineContext line)
    {
        var source = PortfolioSourceVersion.Of(PortfolioCases.Base(line.State));
        var words = LineWords.Of(line.Text);
        var now = $"{source.ValueDate:yyyy-MM-dd}";
        if (words.Count == 0)
            throw new TableRefused("NoValueDate", $"value date takes a date as yyyy-MM-dd, such as 2026-10-31: the workspace is as of {now}.", null);
        if (words.Count > 1) throw new TableRefused("UnreadWords", $"value date takes one date: {words[1].Word} is not read.", words[1].Word);
        var typed = words[0].Word;
        if (!DateOnly.TryParseExact(typed, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            throw new TableRefused("InvalidDate", $"{typed} is not a date as yyyy-MM-dd, such as 2026-10-31.", typed);
        PortfolioWorkspaces.SameValueDate(line.State, date);
        return PortfolioCommand.Read(line, new JsonObject { ["valueDate"] = $"{date:yyyy-MM-dd}" },
            $"Moves the value date from {now} to {date:yyyy-MM-dd}: reads {source.Registration.Key} as of it, and carries your edits onto what it reads.");
    }

    public LineCompletions Complete(LineContext line) => LineCompletions.None;

    public Task<IReadOnlyDictionary<string, JsonNode?>> InvokeAsync(CapabilityInvocation invocation, CancellationToken cancellationToken) =>
        PortfolioCommand.Asked(invocation, workspaces, (service, request, caller, revision) =>
        {
            var date = DateOnly.ParseExact(CommandRequests.Text(request, "valueDate"), "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var (state, carried) = service.ChangeValueDate(CommandRequests.Text(request, "workspaceId"), revision, caller.UserId, date, caller.Origin);
            return PortfolioPriceContracts.MovedAnswer(state, PortfolioPriceContracts.ValueDateMoved(date, carried), 0, carried);
        });
}

/// <summary>
/// <c>xva.counterparty.change</c>, typed <c>counterparty ABC/LE-02</c> or <c>cpty ABC/LE-02</c> (ADR-0003 §6, ADR-0016
/// note 69): the workspace moves to another counterparty on the same value date. The edits made for the one it leaves
/// are dropped, and a new trade's own carry, as the toolbar's Counterparty does, which invokes it. Its reading reads no
/// source: it says what would be dropped, each edit by the page's words, and the request it gives carries that count,
/// so that a workspace holding another by the time it runs is refused and nothing is dropped unseen. The reading is
/// the line's confirmation, as the toolbar's dialog is the select's.
/// </summary>
public sealed class PortfolioCounterparty(Func<PortfolioWorkspaces> workspaces) : ICapabilityHandler, ILineCommand
{
    public CapabilityDescriptor Descriptor { get; } = new("xva.counterparty.change", PortfolioContracts.Version,
        "Change the counterparty of an XVA workspace, by its code and legal entity: the edits made for the one it leaves are dropped, a new trade's own carry",
        [new("request", PortfolioPriceContracts.CounterpartyRequest, PortfolioContracts.Version, Cardinality.One)],
        [new("answer", PortfolioPriceContracts.Moved, PortfolioContracts.Version, Cardinality.One)],
        ExecutionKind.RequestResponse, [CapabilityEffect.EditWorkspace, CapabilityEffect.DropEdits],
        new(["counterparty", "cpty"], [new("registration", CommandArgumentKind.Text, Required: true)], PortfolioEditRules.App));

    public LineRead Read(LineContext line)
    {
        var state = PortfolioCases.Base(line.State);
        var words = LineWords.Of(line.Text);
        if (words.Count > 1) throw new TableRefused("UnreadWords", $"counterparty takes one counterparty: {words[1].Word} is not read.", words[1].Word);
        var named = PortfolioWorkspaces.CounterpartyNamed(line.State, words.Count == 0 ? "" : words[0].Word);
        var source = PortfolioSourceVersion.Of(state);
        var dropped = workspaces().DroppedTitles(line.State);
        var to = $"{named.Key} ({named.Name}{(named.Dummy ? ": nothing is booked with it" : "")}) as of {source.ValueDate:yyyy-MM-dd}";
        var dropping = dropped.Count == 0 ? $"; nothing made for {source.Registration.Key} is dropped."
            : $", and drops {TableWords.Counted(dropped.Count, "edit")} made for {source.Registration.Key}: {TableWords.Some(dropped)}. They stay in the log and in any Run that priced them.";
        var read = PortfolioCommand.Read(line, new JsonObject { ["registration"] = named.Key }, $"Moves the workspace to {to}{dropping}");
        // The count is the request's as the registration is, but a number: the read takes only the words typed.
        ((JsonObject)read.Inputs["request"]!)["drops"] = dropped.Count;
        return read;
    }

    /// <summary>
    /// The counterparties a word begins, by its key or its code or a word of its name, with what each is; the one the
    /// workspace is on is not offered, since a move to it is refused.
    /// </summary>
    public LineCompletions Complete(LineContext line)
    {
        // No space after the command's own word: it is still being typed, and nothing follows yet.
        if (line.Text.Length == 0) return LineCompletions.None;
        var (done, typing, start) = LineWords.Typing(line.Text);
        if (done.Count > 0) return LineCompletions.None;
        var on = PortfolioSourceVersion.Of(line.State).Registration.Key;
        static bool Begins(string text, string typing) => text.StartsWith(typing, StringComparison.OrdinalIgnoreCase);
        return new([.. PortfolioCatalog.Registrations
            .Where(r => r.Key != on && (Begins(r.Key, typing) || r.Name.Split(' ').Any(w => Begins(w, typing))))
            .Select(r => new LineCompletion(r.Key, start, r.Dummy ? $"{r.Name}: nothing is booked with it" : r.Name))]);
    }

    public Task<IReadOnlyDictionary<string, JsonNode?>> InvokeAsync(CapabilityInvocation invocation, CancellationToken cancellationToken) =>
        PortfolioCommand.Asked(invocation, workspaces, (service, request, caller, revision) =>
        {
            var drops = request["drops"]?.GetValue<int>() ?? throw new CapabilityFailure("InvalidInput", "counterparty takes drops, the count of edits its reading said it drops.");
            // The user confirms what a change drops (ADR-0003 §6), on the page; the AI is shown no dialog, so it moves a
            // workspace only where nothing drops, and a count it gives that is not the workspace's is refused below as
            // anyone's is (ADR-0016 note 69).
            if (caller.Origin == RunOrigin.Ai && drops > 0)
                throw new WorkspaceRefused("UserConfirms", $"Moving to {CommandRequests.Text(request, "registration")} drops {TableWords.Counted(drops, "edit")}, which the user confirms: "
                    + "they move the counterparty themselves, from the toolbar or the line.");
            var (state, carried) = service.ChangeCounterparty(CommandRequests.Text(request, "workspaceId"), revision, caller.UserId,
                CommandRequests.Text(request, "registration"), drops, caller.Origin);
            var registration = PortfolioSourceVersion.Of(state).Registration.Key;
            return PortfolioPriceContracts.MovedAnswer(state, PortfolioPriceContracts.CounterpartyChanged(registration, drops, carried), drops, carried);
        });
}

/// <summary>
/// <c>xva.pv01.retry</c>, typed <c>pv01 retry [trade]</c> (ADR-0010 §6): asks again for every failed result among the
/// selected measures' cells, on every trade or on one, of the workspace at the revision named. Its reading asks again
/// for nothing: it reads the cells as the grid's rows do, so a result never asked for is asked for by that read as by
/// the grid's, and says the trades whose results have failed. It writes no workspace; a result that arrives is read
/// with the rows.
/// </summary>
public sealed class PortfolioPv01Retry(Func<PortfolioWorkspaces> workspaces) : ICapabilityHandler, ILineCommand
{
    public CapabilityDescriptor Descriptor { get; } = new("xva.pv01.retry", PortfolioContracts.Version,
        "Ask again for the results of an XVA workspace that failed, such as a PV01, on every trade or on one",
        [new("request", PortfolioPriceContracts.RetryRequest, PortfolioContracts.Version, Cardinality.One)],
        [new("answer", PortfolioCommandContracts.Edited, PortfolioContracts.Version, Cardinality.One)],
        ExecutionKind.RequestResponse, [CapabilityEffect.Execute],
        new(["pv01 retry"], [new("trade", CommandArgumentKind.Text, Required: false)], PortfolioEditRules.App));

    public LineRead Read(LineContext line)
    {
        var words = LineWords.Of(line.Text);
        if (words.Count > 1) throw new TableRefused("UnreadWords", $"pv01 retry takes one trade, or none for every trade: {words[1].Word} is not read.", words[1].Word);
        var failed = workspaces().Failed(line.State, words.Count == 0 ? null : words[0].Word);
        // The trade as the workspace names it, whatever the case it was typed in.
        var typed = words.Count == 0 ? [] : new JsonObject { ["trade"] = Named(line.State, words[0].Word) };
        return PortfolioCommand.Read(line, typed, failed.Count == 0
            ? $"Nothing would be asked again: no result has failed{(words.Count == 0 ? "" : $" on {typed["trade"]!.GetValue<string>()}")}."
            : $"Asks again for the results that failed on {TableWords.Counted(failed.Count, "trade")}: {TableWords.Some(failed)}.");
    }

    static string Named(WorkspaceState state, string typed) =>
        PortfolioMaterialiser.Trades(PortfolioCases.Base(state)).First(t => string.Equals(t.TradeId, typed, StringComparison.OrdinalIgnoreCase)).TradeId;

    public LineCompletions Complete(LineContext line) => LineCompletions.None;

    public Task<IReadOnlyDictionary<string, JsonNode?>> InvokeAsync(CapabilityInvocation invocation, CancellationToken cancellationToken) =>
        PortfolioCommand.Answer(invocation, workspaces, (service, request, caller, revision) =>
        {
            var id = CommandRequests.Text(request, "workspaceId");
            return (id, revision, null, PortfolioPriceContracts.Retried(service.Retry(id, revision, caller.UserId, CommandRequests.Optional(request, "trade"))));
        });
}
