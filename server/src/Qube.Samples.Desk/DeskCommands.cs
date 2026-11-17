using System.Text.Json.Nodes;
using Qube.Composition.Application;
using Qube.Composition.Application.Workspaces;
using Qube.Composition.Contracts;

namespace Qube.Samples.Desk;

public static class DeskContracts
{
    public const string Version = "1.0.0";
    public const string NewWorkspace = "desk.workspace-new";
    public const string Workspace = "desk.workspace";
    public const string PositionRequest = "desk.position-request";
    public const string Position = "desk.position";
}

/// <summary>
/// <c>desk.position.read</c> (ADR-0019 §3, §7): Book, What-if and Net for each sensitivity of the caller's Desk
/// workspace, and the first-order prediction for the moves it holds. Each What-if and Net figure travels with its
/// marks; a client draws them and never infers them. It reads; it makes no Run.
/// </summary>
public sealed class DeskPositionRead(Func<WorkspaceService> workspaces, IHedgeRisk provider) : ICapabilityHandler
{
    public const string Id = "desk.position.read";

    public CapabilityDescriptor Descriptor { get; } = new(Id, DeskContracts.Version,
        "Read a Desk workspace's position: values, Book / What-if / Net risk, and the predicted profit and loss of the moves held",
        [new("request", DeskContracts.PositionRequest, DeskContracts.Version, Cardinality.One)],
        [new("answer", DeskContracts.Position, DeskContracts.Version, Cardinality.One)],
        ExecutionKind.RequestResponse, [CapabilityEffect.ReadWorkspace]);

    public Task<IReadOnlyDictionary<string, JsonNode?>> InvokeAsync(CapabilityInvocation invocation, CancellationToken cancellationToken)
    {
        var caller = invocation.Caller ?? throw new CapabilityFailure("NoCaller", "A command is invoked for a caller, through the command gateway.");
        var request = invocation.Inputs["request"] ?? throw new CapabilityFailure("InvalidInput", "The command takes a request.");
        var id = CommandRequests.Text(request, "workspaceId");
        try
        {
            var state = workspaces().Find(id);
            if (state is null || state.App != DeskEditRules.App) throw new WorkspaceRefused("UnknownWorkspace", $"No Desk workspace {id}.");
            if (state.Owner != caller.UserId) throw new WorkspaceRefused("NotOwner", "This workspace belongs to another user.");
            if (request["revision"]?.GetValue<long>() is { } revision && revision != state.Revision)
                throw new WorkspaceRefused("RevisionMoved", $"The workspace is at revision {state.Revision}, not {revision}.");
            IReadOnlyList<string>? counterparties = request["counterparties"] is JsonArray named ? [.. named.Select(c => c!.GetValue<string>())] : null;
            var (kind, input) = (CommandRequests.Optional(request, "kind"), CommandRequests.Optional(request, "input"));
            var position = DeskPosition.Of(state, provider, counterparties);
            return Task.FromResult<IReadOnlyDictionary<string, JsonNode?>>(new Dictionary<string, JsonNode?>
            {
                ["answer"] = Write(state, position, kind, input),
            });
        }
        catch (WorkspaceRefused refused)
        {
            throw new CapabilityFailure(refused.Code, refused.Message);
        }
    }

    static JsonArray List(IEnumerable<JsonNode?> items) => new([.. items]);
    static JsonArray Marks(IReadOnlyList<string> marks) => List(marks.Select(m => (JsonNode)m));

    static JsonObject Write(WorkspaceState state, Position p, string? kind, string? input)
    {
        // A figure there is none of is left out, as every contract has it: absent is none, never a value made up for it.
        static JsonObject Without(JsonObject record)
        {
            foreach (var name in record.Where(f => f.Value is null).Select(f => f.Key).ToList()) record.Remove(name);
            return record;
        }
        JsonObject Pnl(PositionPnl line) => Without(new()
        {
            ["kind"] = line.Kind, ["input"] = line.Input, ["move"] = line.Move, ["typedIn"] = line.TypedIn,
            ["book"] = line.Book, ["whatIf"] = line.WhatIf, ["net"] = line.Net, ["marks"] = Marks(line.Marks),
        });
        var answer = new JsonObject
        {
            ["workspaceId"] = state.WorkspaceId,
            ["revision"] = state.Revision,
            ["resultSet"] = p.ResultSet,
            ["engine"] = p.Book.Engine,
            ["engineCommit"] = p.Book.EngineCommit,
            ["inputSet"] = p.Book.InputSet,
            ["valuationDate"] = p.Book.ValuationDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            ["counterparties"] = List(p.Counterparties.Select(c => (JsonNode)c)),
            ["values"] = List(p.Values.Select(v => (JsonNode)Without(new JsonObject
            {
                ["counterparty"] = v.Counterparty, ["legalEntity"] = v.LegalEntity, ["metric"] = v.Metric, ["value"] = v.Value, ["stdError"] = v.StdError,
            }))),
            ["totals"] = List(p.Totals.OrderBy(t => t.Key, StringComparer.Ordinal).Select(t => (JsonNode)new JsonObject { ["metric"] = t.Key, ["value"] = t.Value })),
            ["hedges"] = List(p.Hedges.Select(h => (JsonNode)new JsonObject
            {
                ["hedge"] = h.Hedge, ["value"] = h.Value, ["provider"] = h.Provider, ["executionMode"] = h.Mode.ToString(),
                ["marks"] = Marks([DeskPosition.WhatIf, .. h.Mode == ExecutionMode.Simulated ? new[] { DeskPosition.Simulated } : []]),
            })),
            ["risk"] = List(p.Risk.Where(r => (kind is null || r.Kind == kind) && (input is null || r.Input == input)).Select(r => (JsonNode)Without(new JsonObject
            {
                ["kind"] = r.Kind, ["input"] = r.Input, ["key"] = r.Key, ["per"] = r.Per switch { SensitivityPer.Rate => "rate", SensitivityPer.Level => "level", _ => null },
                ["book"] = r.Book, ["whatIf"] = r.WhatIf, ["net"] = r.Net, ["marks"] = Marks(r.Marks),
            }))),
            ["pnl"] = List(p.Pnl.Select(l => (JsonNode)Pnl(l))),
        };
        if (p.PnlTotal is { } total)
        {
            answer["pnlTotalBook"] = total.Book;
            if (total.WhatIf is { } hypothetical) answer["pnlTotalWhatIf"] = hypothetical;
            answer["pnlTotalNet"] = total.Net;
            answer["pnlTotalMarks"] = Marks(total.Marks);
        }
        return answer;
    }
}

/// <summary>
/// <c>desk.workspace.create</c> (ADR-0019 §2, ADR-0005): a Desk workspace of the caller's on one result set. Its answer
/// says which book it is: the engine and its commit, the input set and the valuation date.
/// </summary>
public sealed class DeskWorkspaceCreate(Func<DeskWorkspaces> workspaces) : ICapabilityHandler
{
    public const string Id = "desk.workspace.create";

    public CapabilityDescriptor Descriptor { get; } = new(Id, DeskContracts.Version, "Create a Desk workspace on a result set of the engine",
        [new("request", DeskContracts.NewWorkspace, DeskContracts.Version, Cardinality.One)],
        [new("workspace", DeskContracts.Workspace, DeskContracts.Version, Cardinality.One)],
        ExecutionKind.RequestResponse, [CapabilityEffect.EditWorkspace]);

    public Task<IReadOnlyDictionary<string, JsonNode?>> InvokeAsync(CapabilityInvocation invocation, CancellationToken cancellationToken)
    {
        var caller = invocation.Caller ?? throw new CapabilityFailure("NoCaller", "A command is invoked for a caller, through the command gateway.");
        var request = invocation.Inputs["request"] ?? throw new CapabilityFailure("InvalidInput", "The command takes a request.");
        try
        {
            var state = workspaces().Create(caller.UserId, CommandRequests.Optional(request, "resultSet"), caller.Origin);
            var workspace = (JsonObject)state.Context.DeepClone();
            workspace["workspaceId"] = state.WorkspaceId;
            workspace["revision"] = state.Revision;
            return Task.FromResult<IReadOnlyDictionary<string, JsonNode?>>(new Dictionary<string, JsonNode?> { ["workspace"] = workspace });
        }
        catch (DeskResultsRefused refused)
        {
            throw new CapabilityFailure(refused.Code.ToString(), refused.Message);
        }
    }
}

/// <summary>Registers the Desk's commands and their contracts (ADR-0019 §5): nothing of it is in the kernel.</summary>
public sealed class DeskModule(Func<DeskWorkspaces> workspaces, Func<WorkspaceService> service, IHedgeRisk provider) : IQubeModule
{
    public void Register(CompositionRegistryBuilder builder)
    {
        builder.AddCapability(new DeskWorkspaceCreate(workspaces));
        builder.AddCapability(new DeskPositionRead(service, provider));

        static JsonObject Text(string title, string? description = null)
        {
            var text = new JsonObject { ["type"] = "string", ["title"] = title };
            if (description is not null) text["description"] = description;
            return text;
        }
        void Contract(string id, string title, JsonObject properties, params string[] required) =>
            builder.AddContract(new ContractDefinition(id, DeskContracts.Version, title, new JsonObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = new JsonArray([.. required.Select(r => JsonValue.Create(r))]),
                ["additionalProperties"] = false,
            }));

        Contract(DeskContracts.NewWorkspace, "New Desk workspace", new JsonObject
        {
            ["resultSet"] = Text("Result set", "The result set to read the book from. Left out, the configured one; refused where none is configured."),
        });
        Contract(DeskContracts.Workspace, "Desk workspace", new JsonObject
        {
            ["workspaceId"] = Text("Workspace"),
            ["revision"] = new JsonObject { ["type"] = "integer", ["title"] = "Revision" },
            ["resultSet"] = Text("Result set"),
            ["engine"] = Text("Engine"),
            ["engineCommit"] = Text("Engine commit"),
            ["inputSet"] = Text("Input set"),
            ["valuationDate"] = new JsonObject { ["type"] = "string", ["format"] = "date", ["title"] = "Valuation date" },
        }, "workspaceId", "revision", "resultSet", "engine", "engineCommit", "inputSet", "valuationDate");

        static JsonObject Number(string title) => new() { ["type"] = "number", ["title"] = title };
        static JsonObject Maybe(string title) => new() { ["type"] = "number", ["title"] = title + "; absent where there is none" };
        static JsonObject Words(string title) => new() { ["type"] = "array", ["title"] = title, ["items"] = new JsonObject { ["type"] = "string" } };
        static JsonObject Records(string title, JsonObject properties) => new()
        {
            ["type"] = "array", ["title"] = title,
            ["items"] = new JsonObject { ["type"] = "object", ["properties"] = properties, ["additionalProperties"] = false },
        };
        const string Signs = "A cost is negative, as the engine has it; a profit and loss is the change of the value.";
        const string MarksSay = "What the What-if and Net figures are: WHAT-IF where a hypothetical hedge is in them, SIMULATED where a figure in them is not the engine's, FIRST ORDER for a prediction from sensitivities.";

        Contract(DeskContracts.PositionRequest, "Desk position request", new JsonObject
        {
            ["workspaceId"] = Text("Workspace"),
            ["revision"] = CommandRequests.RevisionField(),
            ["counterparties"] = Words("The counterparties whose book is read, by code; all where left out"),
            ["kind"] = Text("Kind", "Only the risk lines of this kind (zero_rate, fx_spot, …); every kind where left out."),
            ["input"] = Text("Input", "Only the risk lines of this input (a curve, a currency pair, …); every input where left out."),
        }, "workspaceId");
        Contract(DeskContracts.Position, "Desk position", new JsonObject
        {
            ["workspaceId"] = Text("Workspace"),
            ["revision"] = new JsonObject { ["type"] = "integer", ["title"] = "Revision" },
            ["resultSet"] = Text("Result set"),
            ["engine"] = Text("Engine"),
            ["engineCommit"] = Text("Engine commit"),
            ["inputSet"] = Text("Input set"),
            ["valuationDate"] = new JsonObject { ["type"] = "string", ["format"] = "date", ["title"] = "Valuation date" },
            ["counterparties"] = Words("Counterparties read"),
            ["values"] = Records("The book's values. " + Signs, new JsonObject
            {
                ["counterparty"] = Text("Counterparty"), ["legalEntity"] = Text("Legal entity; absent where the result set maps none"),
                ["metric"] = Text("Metric"), ["value"] = Number("Value"), ["stdError"] = Number("Standard error"),
            }),
            ["totals"] = Records("Each metric summed over the counterparties read. No standard error: the parts share paths, so theirs do not add.", new JsonObject
            {
                ["metric"] = Text("Metric"), ["value"] = Number("Value"),
            }),
            ["hedges"] = Records("The what-if hedges, each with who computed its risk", new JsonObject
            {
                ["hedge"] = Text("Hedge"), ["value"] = Number("Value"), ["provider"] = Text("Provider"), ["executionMode"] = Text("Actual or Simulated"), ["marks"] = Words(MarksSay),
            }),
            ["risk"] = Records("First-order sensitivities, summed over the counterparties read and their metrics: the derivative of the adjustment to the input.", new JsonObject
            {
                ["kind"] = Text("Kind"), ["input"] = Text("Input"), ["key"] = Text("Key"),
                ["per"] = Text("Per what: rate (per 1.0 of a rate; a basis point is 1e-4 of it) or level (per unit of the quote); absent where the result set declares no unit"),
                ["book"] = Number("Book"), ["whatIf"] = Maybe("What-if: the hedges' figure where any has a row here"), ["net"] = Number("Net"), ["marks"] = Words(MarksSay),
            }),
            ["pnl"] = Records("Predicted profit and loss of each move held: sensitivity times move, first order.", new JsonObject
            {
                ["kind"] = Text("Kind"), ["input"] = Text("Input"), ["move"] = Number("Move, as typed"), ["typedIn"] = Text("bp for a rate, quote for a quoted level"),
                ["book"] = Number("Book"), ["whatIf"] = Maybe("What-if"), ["net"] = Number("Net"), ["marks"] = Words(MarksSay),
            }),
            ["pnlTotalBook"] = Maybe("The predicted profit and loss of every move held, Book"),
            ["pnlTotalWhatIf"] = Maybe("The same, What-if"),
            ["pnlTotalNet"] = Maybe("The same, Net"),
            ["pnlTotalMarks"] = Words(MarksSay),
        }, "workspaceId", "revision", "resultSet", "engineCommit", "inputSet", "valuationDate", "values", "risk", "pnl");
    }
}
