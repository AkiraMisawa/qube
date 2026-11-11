using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Qube.Composition.Application;
using Qube.Composition.Application.Workspaces;
using Qube.Composition.Contracts;

namespace Qube.Samples.Portfolio;

/// <summary>
/// The XVA page's command capabilities (ADR-0005, ADR-0010 §9): <c>vary</c>, <c>add trades</c> and <c>cds</c>, as the
/// command line has them, for the AI through the command gateway. Each takes what the command line takes, as written,
/// and goes through the same workspace service, so its checks and its answer are the command line's; what it does is in
/// the workspace's log with the caller's origin. <c>set</c> is the kernel's <c>entity.set</c> over the pricer's
/// registered tables (ADR-0016 §3, <see cref="PortfolioTables"/>).
/// </summary>
public static partial class PortfolioCommandContracts
{
    public const string VaryRequest = "xva.vary-request";
    public const string TradesAddRequest = "xva.trades-add-request";
    public const string CdsRequest = "xva.cds-request";
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

    /// <summary>What vary says back, in the command line's words.</summary>
    public static string Varied(VaryAnswer answer) =>
        string.Join("; ", new[] { answer.Made.Count == 0 ? "vary made no case" : $"Made {answer.Made.Count} case(s): {string.Join(", ", answer.Made)}" }
            .Concat(answer.Skipped.Count == 0 ? [] : [$"skipped {string.Join("; ", answer.Skipped)}"])) + ".";

    /// <summary>What add trades says back, in the command line's words.</summary>
    public static string Added(AddAnswer answer) =>
        string.Join("; ", new[] { $"Made {answer.Made.Count} case(s): {string.Join(", ", answer.Made)}" }
            .Concat(answer.Replaced.Count == 0 ? [] : [$"replaced {string.Join(", ", answer.Replaced)}"])) + ".";
}

/// <summary><c>xva.vary</c>: a case per value of a cell, named as the command line names it (<c>CSA-A threshold</c>).</summary>
public sealed class PortfolioVary(Func<PortfolioWorkspaces> workspaces) : ICapabilityHandler
{
    public CapabilityDescriptor Descriptor { get; } = new("xva.vary", PortfolioContracts.Version, "Make a case per value of a cell of an XVA workspace",
        [new("request", PortfolioCommandContracts.VaryRequest, PortfolioContracts.Version, Cardinality.One)],
        [new("answer", PortfolioCommandContracts.CasesMade, PortfolioContracts.Version, Cardinality.One)],
        ExecutionKind.RequestResponse, [CapabilityEffect.EditWorkspace]);

    public Task<IReadOnlyDictionary<string, JsonNode?>> InvokeAsync(CapabilityInvocation invocation, CancellationToken cancellationToken) =>
        PortfolioCommand.Run(invocation, workspaces, (service, request, caller, revision) =>
        {
            var values = PortfolioCommandContracts.ValuesOf(PortfolioCommand.Text(request, "values"));
            var answer = service.Vary(PortfolioCommand.Text(request, "workspaceId"), revision, caller.UserId, PortfolioCommand.Text(request, "cell"), values, caller.Origin);
            return (answer.State, answer.Made.Count, PortfolioCommandContracts.Varied(answer));
        });
}

/// <summary><c>xva.trades.add</c>: a case per New row with A ticked, each that trade alone (docs/03 §5).</summary>
public sealed class PortfolioTradesAdd(Func<PortfolioWorkspaces> workspaces) : ICapabilityHandler
{
    public CapabilityDescriptor Descriptor { get; } = new("xva.trades.add", PortfolioContracts.Version, "Price each new trade of an XVA workspace alone, a case per trade",
        [new("request", PortfolioCommandContracts.TradesAddRequest, PortfolioContracts.Version, Cardinality.One)],
        [new("answer", PortfolioCommandContracts.CasesMade, PortfolioContracts.Version, Cardinality.One)],
        ExecutionKind.RequestResponse, [CapabilityEffect.EditWorkspace]);

    public Task<IReadOnlyDictionary<string, JsonNode?>> InvokeAsync(CapabilityInvocation invocation, CancellationToken cancellationToken) =>
        PortfolioCommand.Run(invocation, workspaces, (service, request, caller, revision) =>
        {
            var answer = service.AddTrades(PortfolioCommand.Text(request, "workspaceId"), revision, caller.UserId, caller.Origin);
            return (answer.State, answer.Made.Count, PortfolioCommandContracts.Added(answer));
        });
}

/// <summary><c>xva.cds</c>: the counterparty's CDS, a single name by its words, a proxy, or none (ADR-0010 §8's note of 2026-11-09).</summary>
public sealed class PortfolioCds(Func<PortfolioWorkspaces> workspaces) : ICapabilityHandler
{
    public CapabilityDescriptor Descriptor { get; } = new("xva.cds", PortfolioContracts.Version, "Choose the counterparty's CDS curve of an XVA workspace",
        [new("request", PortfolioCommandContracts.CdsRequest, PortfolioContracts.Version, Cardinality.One)],
        [new("answer", PortfolioCommandContracts.Edited, PortfolioContracts.Version, Cardinality.One)],
        ExecutionKind.RequestResponse, [CapabilityEffect.EditWorkspace]);

    public Task<IReadOnlyDictionary<string, JsonNode?>> InvokeAsync(CapabilityInvocation invocation, CancellationToken cancellationToken) =>
        PortfolioCommand.Run(invocation, workspaces, (service, request, caller, revision) =>
        {
            var workspaceId = PortfolioCommand.Text(request, "workspaceId");
            var (state, credit) = service.ChooseCds(workspaceId, revision, caller.UserId, PortfolioCommand.Text(request, "cds"), caller.Origin);
            return (state, null, PortfolioWorkspaces.CdsSaid(((PortfolioSourceVersion)state.Source).Registration, credit));
        });
}

static class PortfolioCommand
{
    public static string Text(JsonNode request, string name) =>
        request[name]?.GetValue<string>() ?? throw new CapabilityFailure("InvalidInput", $"The request has no {name}.");

    /// <summary>An optional field: absent is none, never a value made up for it.</summary>
    public static string? Optional(JsonNode request, string name) => request[name]?.GetValue<string>();

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
        Func<PortfolioWorkspaces, JsonNode, Caller, long, (string WorkspaceId, long Revision, int? Made, string Said)> act)
    {
        var caller = invocation.Caller ?? throw new CapabilityFailure("NoCaller", "A command is invoked for a caller, through the command gateway.");
        var request = invocation.Inputs["request"] ?? throw new CapabilityFailure("InvalidInput", "The command takes a request.");
        var service = workspaces();
        try
        {
            var workspaceId = Text(request, "workspaceId");
            var revision = request["revision"]?.GetValue<long>() ?? service.Revision(workspaceId, caller.UserId);
            var (id, at, made, said) = act(service, request, caller, revision);
            var answer = new JsonObject { ["workspaceId"] = id, ["revision"] = at, ["said"] = said };
            if (made is { } count) answer["made"] = count;
            return Task.FromResult<IReadOnlyDictionary<string, JsonNode?>>(new Dictionary<string, JsonNode?> { ["answer"] = answer });
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
public sealed class PortfolioCommandsModule(Func<PortfolioWorkspaces> workspaces) : IQubeModule
{
    public void Register(CompositionRegistryBuilder builder)
    {
        builder.AddCapability(new PortfolioVary(workspaces));
        builder.AddCapability(new PortfolioTradesAdd(workspaces));
        builder.AddCapability(new PortfolioCds(workspaces));
        PortfolioScenarioCommands.Register(builder, workspaces);
        JsonObject Workspace() => new() { ["type"] = "string", ["minLength"] = 1, ["title"] = "Workspace", ["description"] = "The XVA workspace's id, as the context names it." };
        JsonObject Revision() => new()
        {
            ["type"] = "integer", ["title"] = "Revision",
            ["description"] = "The revision the request was made against; refused if the workspace has moved since. Left out, the one it holds.",
        };
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
            ["workspaceId"] = Workspace(),
            ["revision"] = Revision(),
            ["cell"] = Text("Cell", "Its row and its column, as the command line names a cell: CSA-A threshold, NS-001 recovery, T-1001 notional."),
            ["values"] = Text("Values", "The values, each as written, separated by commas: 10mm, 5mm, 0. Amounts as 5mm, 250k or 1bn; rates as 25% or 30bp."),
        }, "workspaceId", "cell", "values");
        Request(PortfolioCommandContracts.TradesAddRequest, "Price each new trade alone", new JsonObject
        {
            ["workspaceId"] = Workspace(),
            ["revision"] = Revision(),
        }, "workspaceId");
        Request(PortfolioCommandContracts.CdsRequest, "Choose the CDS", new JsonObject
        {
            ["workspaceId"] = Workspace(),
            ["revision"] = Revision(),
            ["cds"] = Text("CDS", "A single name's words in any order (ABCH USD SNRFOR), proxy with a sector, a region and a rating (proxy industrials asia BBB), or none."),
        }, "workspaceId", "cds");
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
