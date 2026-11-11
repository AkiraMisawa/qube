using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Qube.Composition.Application;
using Qube.Composition.Application.Tables;
using Qube.Composition.Application.Workspaces;
using Qube.Composition.Contracts;

namespace Qube.Samples.Portfolio;

/// <summary>
/// The pricer's command capabilities (ADR-0005, ADR-0010 §9, ADR-0016 note 28 of step 3): <c>vary</c>, <c>add each</c>,
/// <c>cds</c>, <c>measures</c>, <c>case remove</c> and <c>cases clear</c>. Each says how it is typed and reads its own
/// line into the request the AI writes, so the line, a button and the AI reach one handler with one set of checks; what
/// it does is in the workspace's log with the caller's origin, named by the words that would type it. <c>set</c> is
/// the kernel's <c>entity.set</c> over the pricer's registered tables (ADR-0016 §3, <see cref="PortfolioTables"/>).
/// </summary>
public static partial class PortfolioCommandContracts
{
    public const string VaryRequest = "xva.vary-request";
    public const string TradesAddRequest = "xva.trades-add-request";
    public const string CdsRequest = "xva.cds-request";
    public const string MeasuresRequest = "xva.measures-request";
    public const string CaseRemoveRequest = "xva.case-remove-request";
    public const string CasesClearRequest = "xva.cases-clear-request";
    public const string CasesMade = "xva.cases-made";
    public const string Edited = "xva.workspace-edited";

    /// <summary>
    /// A list of values as written, separated by commas, as the command line reads it. A comma between two digits could
    /// separate two values or the thousands of one (<c>10,000k</c>), so such a list is refused rather than read either way.
    /// </summary>
    public static IReadOnlyList<string> ValuesOf(string text) =>
        CommaBetweenDigits().Match(text) is { Success: true } ambiguous
            ? throw new WorkspaceRefused("NothingToVary",
                $"{ambiguous.Value} could be one value or two: a comma separates values here, so write an amount without separators, such as 10000k or 10mm.")
            : [.. text.Split(',').Select(v => v.Trim())];

    [GeneratedRegex(@"[^\s,]*\d(?:,\d[^\s,]*)+")]
    private static partial Regex CommaBetweenDigits();

    /// <summary>What a reading of vary says it would do (ADR-0016 note 28), in the words vary then says back.</summary>
    public static string WouldVary(VaryPlan plan) =>
        string.Join("; ", new[] { plan.Made.Count == 0 ? "Makes no case" : $"Makes {plan.Made.Count} case(s): {string.Join(", ", plan.Made)}" }
            .Concat(plan.Skipped.Count == 0 ? [] : [$"skips {string.Join("; ", plan.Skipped)}"])) + ".";

    /// <summary>What a reading of add each says it would do.</summary>
    public static string WouldAdd(AddPlan plan) =>
        string.Join("; ", new[] { $"Makes {plan.Made.Count} case(s): {string.Join(", ", plan.Made)}" }
            .Concat(plan.Replaced.Count == 0 ? [] : [$"replaces {string.Join(", ", plan.Replaced)}"])) + ".";

    /// <summary>The cases a command removes, counted and named: <c>2 cases: a and b</c>.</summary>
    public static string Cases(CasesPlan plan) =>
        $"{plan.Removed.Count} case{(plan.Removed.Count == 1 ? "" : "s")}: {TableWords.And(plan.Removed.Select(c => c.Title))}";

    /// <summary>What vary says back, in the command line's words.</summary>
    public static string Varied(VaryAnswer answer) =>
        string.Join("; ", new[] { answer.Made.Count == 0 ? "vary made no case" : $"Made {answer.Made.Count} case(s): {string.Join(", ", answer.Made)}" }
            .Concat(answer.Skipped.Count == 0 ? [] : [$"skipped {string.Join("; ", answer.Skipped)}"])) + ".";

    /// <summary>What add each says back, in the command line's words.</summary>
    public static string Added(AddAnswer answer) =>
        string.Join("; ", new[] { $"Made {answer.Made.Count} case(s): {string.Join(", ", answer.Made)}" }
            .Concat(answer.Replaced.Count == 0 ? [] : [$"replaced {string.Join(", ", answer.Replaced)}"])) + ".";
}

/// <summary>
/// <c>xva.vary</c>: a case per value of a cell. The line and the AI name the cell as the command line names it
/// (<c>CSA-A threshold</c>); a page's dialog names it by its entity and field. Its line is read into the same request
/// (ADR-0016 note 28 of step 3), and the reading is a trial: the cases it would make, with nothing written.
/// </summary>
public sealed class PortfolioVary(Func<PortfolioWorkspaces> workspaces) : ICapabilityHandler, ILineCommand
{
    public CapabilityDescriptor Descriptor { get; } = new("xva.vary", PortfolioContracts.Version, "Make a case per value of a cell, such as vary CSA-A threshold 10mm, 5mm",
        [new("request", PortfolioCommandContracts.VaryRequest, PortfolioContracts.Version, Cardinality.One)],
        [new("answer", PortfolioCommandContracts.CasesMade, PortfolioContracts.Version, Cardinality.One)],
        ExecutionKind.RequestResponse, [CapabilityEffect.EditWorkspace],
        new(["vary"], [new("cell", CommandArgumentKind.Text, Required: true), new("values", CommandArgumentKind.Text, Required: true)], PortfolioEditRules.App));

    /// <summary>
    /// <c>vary CSA-A threshold 10mm, 5mm, 0</c>: the cell, its row and its column, then the values separated by commas.
    /// The first value is the last word before the first comma, so a column of several words is read whole.
    /// </summary>
    public LineRead Read(LineContext line)
    {
        var comma = line.Text.IndexOf(',');
        var words = LineWords.Of(comma < 0 ? line.Text : line.Text[..comma]);
        if (words.Count < 3)
        {
            // An example that can be typed as it is: a CSA the workspace has, where it has one.
            var example = PortfolioMaterialiser.Csas(PortfolioCases.Base(line.State)).FirstOrDefault() is { } csa ? $", such as vary {csa.CsaId} threshold 10mm, 5mm, 0" : "";
            throw new WorkspaceRefused("NothingToVary", $"vary takes a cell, its row and its column, then the values separated by commas{example}.");
        }
        var (cell, values) = (line.Text[..words[^1].Start].Trim(), line.Text[words[^1].Start..].Trim());
        var service = workspaces();
        var plan = service.PlanVary(line.State, line.State.Revision, cell, PortfolioCommandContracts.ValuesOf(values));
        service.Trial(line.State, line.Caller.UserId, plan.Command, line.Caller.Origin);
        return PortfolioCommand.Read(line, new JsonObject { ["cell"] = cell, ["values"] = values }, PortfolioCommandContracts.WouldVary(plan));
    }

    public LineCompletions Complete(LineContext line) => LineCompletions.None;

    public Task<IReadOnlyDictionary<string, JsonNode?>> InvokeAsync(CapabilityInvocation invocation, CancellationToken cancellationToken) =>
        PortfolioCommand.Run(invocation, workspaces, (service, request, caller, revision) =>
        {
            var values = PortfolioCommandContracts.ValuesOf(PortfolioCommand.Text(request, "values"));
            var workspaceId = PortfolioCommand.Text(request, "workspaceId");
            var (cell, entity, field) = (PortfolioCommand.Optional(request, "cell"), PortfolioCommand.Optional(request, "entity"), PortfolioCommand.Optional(request, "field"));
            var answer = (cell, entity, field) switch
            {
                ({ } named, null, null) => service.Vary(workspaceId, revision, caller.UserId, named, values, caller.Origin),
                (null, { } of, { } its) => service.VaryField(workspaceId, revision, caller.UserId, of, its, values, caller.Origin),
                _ => throw new CapabilityFailure("InvalidInput", "vary names its cell once: as cell, or as entity and field."),
            };
            return (answer.State, answer.Made.Count, PortfolioCommandContracts.Varied(answer));
        });
}

/// <summary>
/// <c>xva.trades.add</c>, typed <c>add each</c> (ADR-0016 §3, note 28): a case per New row with A ticked, each that
/// trade alone (docs/03 §5). Its reading is a trial: the cases it would make, and those it would replace.
/// </summary>
public sealed class PortfolioTradesAdd(Func<PortfolioWorkspaces> workspaces) : ICapabilityHandler, ILineCommand
{
    public CapabilityDescriptor Descriptor { get; } = new("xva.trades.add", PortfolioContracts.Version, "Price each new trade alone, a case per New row with A ticked",
        [new("request", PortfolioCommandContracts.TradesAddRequest, PortfolioContracts.Version, Cardinality.One)],
        [new("answer", PortfolioCommandContracts.CasesMade, PortfolioContracts.Version, Cardinality.One)],
        ExecutionKind.RequestResponse, [CapabilityEffect.EditWorkspace],
        new(["add each"], [], PortfolioEditRules.App));

    public LineRead Read(LineContext line)
    {
        var service = workspaces();
        var plan = service.PlanAdd(line.State, line.State.Revision);
        service.Trial(line.State, line.Caller.UserId, plan.Command, line.Caller.Origin);
        return PortfolioCommand.Read(line, [], PortfolioCommandContracts.WouldAdd(plan));
    }

    public LineCompletions Complete(LineContext line) => LineCompletions.None;

    public Task<IReadOnlyDictionary<string, JsonNode?>> InvokeAsync(CapabilityInvocation invocation, CancellationToken cancellationToken) =>
        PortfolioCommand.Run(invocation, workspaces, (service, request, caller, revision) =>
        {
            var answer = service.AddTrades(PortfolioCommand.Text(request, "workspaceId"), revision, caller.UserId, caller.Origin);
            return (answer.State, answer.Made.Count, PortfolioCommandContracts.Added(answer));
        });
}

/// <summary>
/// <c>xva.cds</c>: the counterparty's CDS, a single name by its words, a proxy, or none (ADR-0010 §8's note of
/// 2026-11-09). Its reading is a trial: the curve it would choose, or why the counterparty would have none.
/// </summary>
public sealed class PortfolioCds(Func<PortfolioWorkspaces> workspaces) : ICapabilityHandler, ILineCommand
{
    public CapabilityDescriptor Descriptor { get; } = new("xva.cds", PortfolioContracts.Version, "Choose the counterparty's CDS: a single name's words, proxy with a sector, a region and a rating, or none",
        [new("request", PortfolioCommandContracts.CdsRequest, PortfolioContracts.Version, Cardinality.One)],
        [new("answer", PortfolioCommandContracts.Edited, PortfolioContracts.Version, Cardinality.One)],
        ExecutionKind.RequestResponse, [CapabilityEffect.EditWorkspace],
        new(["cds"], [new("cds", CommandArgumentKind.Text, Required: true)], PortfolioEditRules.App));

    public LineRead Read(LineContext line)
    {
        var service = workspaces();
        var typed = line.Text.Trim();
        var plan = service.PlanCds(line.State, typed);
        var credit = PortfolioWorkspaces.CreditOf(service.Trial(line.State, line.Caller.UserId, plan, line.Caller.Origin));
        var would = (plan.Changes.Count, credit.Cds) switch
        {
            (0, { } held) => $"Nothing would change: the CDS is {PortfolioWorkspaces.CdsNamed(held)}.",
            (0, null) => $"Nothing would change: {PortfolioWorkspaces.WhyNoCds(credit)}",
            (_, { } chosen) => $"Chooses {PortfolioWorkspaces.CdsNamed(chosen)}.",
            (_, null) => $"Leaves no CDS: {PortfolioWorkspaces.WhyNoCds(credit)}",
        };
        return PortfolioCommand.Read(line, new JsonObject { ["cds"] = typed }, would);
    }

    /// <summary>
    /// What <c>cds</c> offers for the word being typed, given every word typed so far: <c>none</c>, <c>proxy</c> and the
    /// single names' tickers first; after <c>proxy</c>, the sectors, regions and ratings it has none of yet; after a
    /// single name's word, the other words of the names it still matches.
    /// </summary>
    public LineCompletions Complete(LineContext line)
    {
        // No space after the command's own word: it is still being typed, and nothing follows yet.
        if (line.Text.Length == 0) return LineCompletions.None;
        var (done, typing, start) = LineWords.Typing(line.Text);
        static bool Is(string word, IEnumerable<string> values) => values.Any(v => v.Equals(word, StringComparison.OrdinalIgnoreCase));
        IEnumerable<(string Value, string Detail)> offered;
        if (done.Count > 0 && done[0].Equals(CdsCatalog.Proxy, StringComparison.OrdinalIgnoreCase))
            offered = new (string Key, IReadOnlyList<string> Values)[] { ("sector", CdsCatalog.Sectors), ("region", CdsCatalog.Regions), ("rating", CdsCatalog.Ratings) }
                .Where(key => !done.Any(w => Is(w, key.Values))).SelectMany(key => key.Values.Select(v => (v, $"the proxy's {key.Key}")));
        else
        {
            static (string Word, string Detail)[] Of(CdsCurve c) => [(c.Ticker, c.Reference), (c.Currency, "currency"), (c.Seniority, "seniority"), (c.DocClause, "doc clause")];
            var matching = CdsCatalog.SingleNames.Where(c => done.All(w => Is(w, Of(c).Select(x => x.Word))));
            IEnumerable<(string, string)> first = done.Count == 0 ? [("none", "no CDS: CVA is refused"), (CdsCatalog.Proxy, "a sector, a region and a rating")] : [];
            offered = first.Concat(matching.SelectMany(Of).Where(x => !Is(x.Word, done))).DistinctBy(x => x.Item1, StringComparer.OrdinalIgnoreCase);
        }
        return new([.. offered.Where(o => o.Value.StartsWith(typing, StringComparison.OrdinalIgnoreCase)).Select(o => new LineCompletion(o.Value, start, o.Detail))]);
    }

    public Task<IReadOnlyDictionary<string, JsonNode?>> InvokeAsync(CapabilityInvocation invocation, CancellationToken cancellationToken) =>
        PortfolioCommand.Run(invocation, workspaces, (service, request, caller, revision) =>
        {
            var workspaceId = PortfolioCommand.Text(request, "workspaceId");
            var (state, credit) = service.ChooseCds(workspaceId, revision, caller.UserId, PortfolioCommand.Text(request, "cds"), caller.Origin);
            return (state, null, PortfolioWorkspaces.CdsSaid(((PortfolioSourceVersion)state.Source).Registration, credit));
        });
}

/// <summary>
/// <c>xva.measures</c>: the measures Price prices (<c>measures cva fva</c>, ADR-0010 §10), by their ids or a module's,
/// whatever their case. A domain verb while a field holds one value (ADR-0016 note 28): its value is a list.
/// </summary>
public sealed class PortfolioMeasures(Func<PortfolioWorkspaces> workspaces, MeasureRegister register) : ICapabilityHandler, ILineCommand
{
    public CapabilityDescriptor Descriptor { get; } = new("xva.measures", PortfolioContracts.Version, "Choose the measures Price prices, such as measures cva fva",
        [new("request", PortfolioCommandContracts.MeasuresRequest, PortfolioContracts.Version, Cardinality.One)],
        [new("answer", PortfolioCommandContracts.Edited, PortfolioContracts.Version, Cardinality.One)],
        ExecutionKind.RequestResponse, [CapabilityEffect.EditWorkspace],
        new(["measures"], [new("measures", CommandArgumentKind.Text, Required: true)], PortfolioEditRules.App));

    public LineRead Read(LineContext line)
    {
        var service = workspaces();
        var typed = line.Text.Trim();
        var plan = service.PlanMeasures(line.State, typed);
        service.Trial(line.State, line.Caller.UserId, plan.Command, line.Caller.Origin);
        var priced = TableWords.And(plan.Titles);
        return PortfolioCommand.Read(line, new JsonObject { ["measures"] = typed },
            plan.Command.Changes.Count == 0 ? $"Nothing would change: Price prices {priced}." : $"Price would price {priced}.");
    }

    /// <summary>The measures not typed yet, each with its title, for the word being typed.</summary>
    public LineCompletions Complete(LineContext line)
    {
        if (line.Text.Length == 0) return LineCompletions.None;
        var (done, typing, start) = LineWords.Typing(line.Text);
        return new([.. register.Measures.Where(m => !done.Contains(m.Id, StringComparer.OrdinalIgnoreCase) && m.Id.StartsWith(typing, StringComparison.OrdinalIgnoreCase))
            .Select(m => new LineCompletion(m.Id, start, m.Title))]);
    }

    public Task<IReadOnlyDictionary<string, JsonNode?>> InvokeAsync(CapabilityInvocation invocation, CancellationToken cancellationToken) =>
        PortfolioCommand.Run(invocation, workspaces, (service, request, caller, revision) =>
        {
            var (state, plan) = service.SelectMeasures(PortfolioCommand.Text(request, "workspaceId"), revision, caller.UserId, PortfolioCommand.Text(request, "measures"), caller.Origin);
            var priced = TableWords.And(plan.Titles);
            return (state, null, plan.Command.Changes.Count == 0 ? $"Nothing changed: Price prices {priced}." : $"Price prices {priced}.");
        });
}

/// <summary>
/// <c>xva.case.remove</c> (<c>case remove CSA-A threshold 5mm</c>, ADR-0010 §3): a case with every change it holds,
/// named by its title or its id. A domain verb (ADR-0016 note 28): a title is several words, and the selector's row is one.
/// </summary>
public sealed class PortfolioCaseRemove(Func<PortfolioWorkspaces> workspaces) : ICapabilityHandler, ILineCommand
{
    public CapabilityDescriptor Descriptor { get; } = new("xva.case.remove", PortfolioContracts.Version, "Remove a case and its changes, by its title",
        [new("request", PortfolioCommandContracts.CaseRemoveRequest, PortfolioContracts.Version, Cardinality.One)],
        [new("answer", PortfolioCommandContracts.Edited, PortfolioContracts.Version, Cardinality.One)],
        ExecutionKind.RequestResponse, [CapabilityEffect.EditWorkspace],
        new(["case remove"], [new("case", CommandArgumentKind.Text, Required: true)], PortfolioEditRules.App));

    public LineRead Read(LineContext line)
    {
        var service = workspaces();
        var typed = line.Text.Trim();
        var plan = service.PlanRemoveCase(line.State, typed);
        service.Trial(line.State, line.Caller.UserId, plan.Command, line.Caller.Origin);
        var (title, changes) = plan.Removed[0];
        return PortfolioCommand.Read(line, new JsonObject { ["case"] = typed }, $"Removes the case {title}, with its {changes} change{(changes == 1 ? "" : "s")}.");
    }

    /// <summary>The titles of the cases that begin with what is typed after the command's words, each offered whole.</summary>
    public LineCompletions Complete(LineContext line)
    {
        if (line.Text.Length == 0) return LineCompletions.None;
        var from = line.Text.Length - line.Text.TrimStart().Length;
        var typed = line.Text[from..];
        return new([.. PortfolioCases.All(line.State).Skip(1).Where(c => c.Title.StartsWith(typed, StringComparison.OrdinalIgnoreCase)).Select(c => new LineCompletion(c.Title, from))]);
    }

    public Task<IReadOnlyDictionary<string, JsonNode?>> InvokeAsync(CapabilityInvocation invocation, CancellationToken cancellationToken) =>
        PortfolioCommand.Run(invocation, workspaces, (service, request, caller, revision) =>
        {
            var (state, plan) = service.RemoveCase(PortfolioCommand.Text(request, "workspaceId"), revision, caller.UserId, PortfolioCommand.Text(request, "case"), caller.Origin);
            return (state, null, $"Removed the case {plan.Removed[0].Title}.");
        });
}

/// <summary><c>xva.cases.clear</c> (<c>cases clear</c>, ADR-0010 §3): every case with its changes, as one command.</summary>
public sealed class PortfolioCasesClear(Func<PortfolioWorkspaces> workspaces) : ICapabilityHandler, ILineCommand
{
    public CapabilityDescriptor Descriptor { get; } = new("xva.cases.clear", PortfolioContracts.Version, "Remove every case",
        [new("request", PortfolioCommandContracts.CasesClearRequest, PortfolioContracts.Version, Cardinality.One)],
        [new("answer", PortfolioCommandContracts.Edited, PortfolioContracts.Version, Cardinality.One)],
        ExecutionKind.RequestResponse, [CapabilityEffect.EditWorkspace],
        new(["cases clear"], [], PortfolioEditRules.App));

    public LineRead Read(LineContext line)
    {
        var service = workspaces();
        var plan = service.PlanClearCases(line.State);
        service.Trial(line.State, line.Caller.UserId, plan.Command, line.Caller.Origin);
        return PortfolioCommand.Read(line, [], $"Removes {PortfolioCommandContracts.Cases(plan)}.");
    }

    public LineCompletions Complete(LineContext line) => LineCompletions.None;

    public Task<IReadOnlyDictionary<string, JsonNode?>> InvokeAsync(CapabilityInvocation invocation, CancellationToken cancellationToken) =>
        PortfolioCommand.Run(invocation, workspaces, (service, request, caller, revision) =>
        {
            var (state, plan) = service.ClearCases(PortfolioCommand.Text(request, "workspaceId"), revision, caller.UserId, caller.Origin);
            return (state, null, $"Removed {PortfolioCommandContracts.Cases(plan)}.");
        });
}

static class PortfolioCommand
{
    public static string Text(JsonNode request, string name) =>
        request[name]?.GetValue<string>() ?? throw new CapabilityFailure("InvalidInput", $"The request has no {name}.");

    /// <summary>An optional field: absent is none, never a value made up for it.</summary>
    public static string? Optional(JsonNode request, string name) => request[name]?.GetValue<string>();

    /// <summary>The workspace a request of one of the pricer's commands names, as its contract says it.</summary>
    public static JsonObject WorkspaceField() =>
        new() { ["type"] = "string", ["minLength"] = 1, ["title"] = "Workspace", ["description"] = "The XVA workspace's id, as the context names it." };

    /// <summary>The revision such a request was made against, as its contract says it.</summary>
    public static JsonObject RevisionField() => new()
    {
        ["type"] = "integer", ["title"] = "Revision",
        ["description"] = "The revision the request was made against; refused if the workspace has moved since. Left out, the one it holds.",
    };

    /// <summary>
    /// A line as one of the pricer's commands read it (ADR-0016 note 28): the request to invoke it with, which names the
    /// workspace and the revision read, each argument as typed, and what it would do.
    /// </summary>
    public static LineRead Read(LineContext line, JsonObject typed, string would)
    {
        var request = new JsonObject { ["workspaceId"] = line.State.WorkspaceId, ["revision"] = line.State.Revision };
        var arguments = typed.Select(p => new LineArgument(p.Key, p.Value!.GetValue<string>())).ToList();
        foreach (var (name, value) in typed.ToList()) { typed.Remove(name); request[name] = value; }
        return new(new JsonObject { ["request"] = request }, arguments, null, would);
    }

    /// <summary>
    /// Runs a command for its caller on the revision the request names, or the one the workspace holds when it names none,
    /// and answers the revision it left and what it said; the workspace service's refusal is the command's, by code.
    /// </summary>
    public static Task<IReadOnlyDictionary<string, JsonNode?>> Run(CapabilityInvocation invocation, Func<PortfolioWorkspaces> workspaces,
        Func<PortfolioWorkspaces, JsonNode, Caller, long, (WorkspaceState State, int? Made, string Said)> act) =>
        Answer(invocation, workspaces, (service, request, caller, revision) =>
        {
            var (state, made, said) = act(service, request, caller, revision);
            return (state.WorkspaceId, state.Revision, made, said);
        });

    /// <summary>As <see cref="Run"/>, for a command that may leave the workspace where it stands, such as a save.</summary>
    public static Task<IReadOnlyDictionary<string, JsonNode?>> Answer(CapabilityInvocation invocation, Func<PortfolioWorkspaces> workspaces,
        Func<PortfolioWorkspaces, JsonNode, Caller, long, (string WorkspaceId, long Revision, int? Made, string Said)> act) =>
        Asked(invocation, workspaces, (service, request, caller, revision) =>
        {
            var (id, at, made, said) = act(service, request, caller, revision);
            var answer = new JsonObject { ["workspaceId"] = id, ["revision"] = at, ["said"] = said };
            if (made is { } count) answer["made"] = count;
            return answer;
        });

    /// <summary>
    /// A command asked of the pricer for its caller, on the revision the request names or the one the workspace holds
    /// when it names none; what <paramref name="act"/> answers is the command's answer, and the workspace service's
    /// refusal is the command's, by code.
    /// </summary>
    public static Task<IReadOnlyDictionary<string, JsonNode?>> Asked(CapabilityInvocation invocation, Func<PortfolioWorkspaces> workspaces,
        Func<PortfolioWorkspaces, JsonNode, Caller, long, JsonObject> act)
    {
        var caller = invocation.Caller ?? throw new CapabilityFailure("NoCaller", "A command is invoked for a caller, through the command gateway.");
        var request = invocation.Inputs["request"] ?? throw new CapabilityFailure("InvalidInput", "The command takes a request.");
        var service = workspaces();
        try
        {
            var workspaceId = Text(request, "workspaceId");
            var revision = request["revision"]?.GetValue<long>() ?? service.Revision(workspaceId, caller.UserId);
            return Task.FromResult<IReadOnlyDictionary<string, JsonNode?>>(new Dictionary<string, JsonNode?> { ["answer"] = act(service, request, caller, revision) });
        }
        catch (WorkspaceRefused refused)
        {
            throw new CapabilityFailure(refused.Code, refused.Message);
        }
    }
}

/// <summary>
/// Registers the XVA page's commands and their contracts. Apart from <see cref="PortfolioModule"/> because they act
/// through the workspace service, which is built from the registry; they reach it only when invoked.
/// </summary>
public sealed class PortfolioCommandsModule(Func<PortfolioWorkspaces> workspaces, MeasureRegister register) : IQubeModule
{
    public void Register(CompositionRegistryBuilder builder)
    {
        // The order here is the order the line lists them in (ADR-0016 note 33): Price first, as the page puts it.
        builder.AddCapability(new PortfolioPriceCommand(workspaces));
        builder.AddCapability(new PortfolioVary(workspaces));
        builder.AddCapability(new PortfolioTradesAdd(workspaces));
        builder.AddCapability(new PortfolioCds(workspaces));
        builder.AddCapability(new PortfolioMeasures(workspaces, register));
        builder.AddCapability(new PortfolioCaseRemove(workspaces));
        builder.AddCapability(new PortfolioCasesClear(workspaces));
        PortfolioScenarioCommands.Register(builder, workspaces);
        builder.AddCapability(new PortfolioRefresh(workspaces));
        builder.AddCapability(new PortfolioPv01Retry(workspaces));
        PortfolioPriceContracts.Register(builder);
        JsonObject Text(string title, string description) => new() { ["type"] = "string", ["minLength"] = 1, ["title"] = title, ["description"] = description };
        void Request(string id, string title, JsonObject properties, params string[] required) =>
            builder.AddContract(new ContractDefinition(id, PortfolioContracts.Version, title, new JsonObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = new JsonArray([.. required.Select(r => JsonValue.Create(r))]),
                ["additionalProperties"] = false,
            }));
        Request(PortfolioCommandContracts.VaryRequest, "Vary a cell", new JsonObject
        {
            ["workspaceId"] = PortfolioCommand.WorkspaceField(),
            ["revision"] = PortfolioCommand.RevisionField(),
            ["cell"] = Text("Cell", "Its row and its column, as the command line names a cell: CSA-A threshold, NS-001 recovery, T-1001 notional."),
            ["entity"] = Text("Entity", "The cell's entity, as a page names it (csa:CSA-A), with field and in place of cell. A page's own dialog writes this; say cell otherwise."),
            ["field"] = Text("Field", "The cell's field, as a page names it (counterpartyThreshold), with entity."),
            ["values"] = Text("Values", "The values, each as written, separated by commas: 10mm, 5mm, 0. Amounts as 5mm, 250k or 1bn; rates as 25% or 30bp."),
        }, "workspaceId", "values");
        Request(PortfolioCommandContracts.TradesAddRequest, "Price each new trade alone", new JsonObject
        {
            ["workspaceId"] = PortfolioCommand.WorkspaceField(),
            ["revision"] = PortfolioCommand.RevisionField(),
        }, "workspaceId");
        Request(PortfolioCommandContracts.CdsRequest, "Choose the CDS", new JsonObject
        {
            ["workspaceId"] = PortfolioCommand.WorkspaceField(),
            ["revision"] = PortfolioCommand.RevisionField(),
            ["cds"] = Text("CDS", "A single name's words in any order (ABCH USD SNRFOR), proxy with a sector, a region and a rating (proxy industrials asia BBB), or none."),
        }, "workspaceId", "cds");
        Request(PortfolioCommandContracts.MeasuresRequest, "Choose the measures", new JsonObject
        {
            ["workspaceId"] = PortfolioCommand.WorkspaceField(),
            ["revision"] = PortfolioCommand.RevisionField(),
            // No least length: a selection of none is the command's to refuse, in its own words, as the page's buttons can ask for it.
            ["measures"] = new JsonObject
            {
                ["type"] = "string", ["title"] = "Measures",
                ["description"] = "The measures Price prices, by their ids, separated by spaces: cva fva. A module's id stands for all of its measures. At least one.",
            },
        }, "workspaceId", "measures");
        Request(PortfolioCommandContracts.CaseRemoveRequest, "Remove a case", new JsonObject
        {
            ["workspaceId"] = PortfolioCommand.WorkspaceField(),
            ["revision"] = PortfolioCommand.RevisionField(),
            ["case"] = Text("Case", "The case, by its title (CSA-A threshold 5mm) or its id."),
        }, "workspaceId", "case");
        Request(PortfolioCommandContracts.CasesClearRequest, "Remove every case", new JsonObject
        {
            ["workspaceId"] = PortfolioCommand.WorkspaceField(),
            ["revision"] = PortfolioCommand.RevisionField(),
        }, "workspaceId");
        Request(PortfolioCommandContracts.CasesMade, "Cases made", new JsonObject
        {
            ["workspaceId"] = new JsonObject { ["type"] = "string", ["title"] = "Workspace" },
            ["revision"] = new JsonObject { ["type"] = "integer", ["title"] = "Revision" },
            ["made"] = new JsonObject { ["type"] = "integer", ["title"] = "Cases made" },
            ["said"] = new JsonObject { ["type"] = "string", ["title"] = "What was made and skipped, as the command line says it" },
        }, "workspaceId", "revision", "made", "said");
        Request(PortfolioCommandContracts.Edited, "Workspace edited", new JsonObject
        {
            ["workspaceId"] = new JsonObject { ["type"] = "string", ["title"] = "Workspace" },
            ["revision"] = new JsonObject { ["type"] = "integer", ["title"] = "Revision" },
            ["said"] = new JsonObject { ["type"] = "string", ["title"] = "What was done, as the command line says it" },
        }, "workspaceId", "revision", "said");
    }
}
