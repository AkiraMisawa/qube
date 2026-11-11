using System.Globalization;
using System.Text.Json.Nodes;
using Qube.Composition.Application;
using Qube.Composition.Application.Tables;
using Qube.Composition.Application.Workspaces;
using Qube.Composition.Contracts;

namespace Qube.Samples.Rates;

// Rates' own commands (ADR-0005; ADR-0016 note 36 of step 3). A workspace is made by `rates.workspace.create`; the
// rest say how they are typed and read their own line, as the pricer's verbs do: `price`, `live start`, `live stop`,
// `capture` and `add line`. None of them is tried: a Run submitted and a subscription started cannot be done and not
// done, so a reading says what would start and refuses what the command would refuse before it started anything.
// A line is made from typed terms by the kernel's `new`, and edited and removed by its `set`, `reset` and `remove`.

public static class RatesCommandContracts
{
    public const string NewWorkspace = "rates.workspace-new";
    public const string Workspace = "rates.workspace";
    public const string LiveTarget = "rates.live-target";
    public const string LiveSubscription = "rates.live-subscription";
    public const string PriceRequest = "rates.price-request";
    public const string PriceAccepted = "rates.price-accepted";
    public const string CaptureRequest = "rates.capture-request";
    public const string Captured = "rates.captured";
    public const string LineAddRequest = "rates.line-add-request";
    public const string LineAdded = "rates.line-added";
}

/// <summary>
/// <c>rates.workspace.create</c> (ADR-0005, ADR-0008 §5): an empty Rates workspace of the caller's at a valuation date.
/// Lines go into it through the generic <c>entity.create</c>, with the same short terms the command line takes; this
/// command replaced <c>rates.line.create</c>, which took every leg field and made the workspace as it went.
/// </summary>
public sealed class RatesWorkspaceCreate(Func<RatesWorkspaces> workspaces) : ICapabilityHandler
{
    public CapabilityDescriptor Descriptor { get; } = new("rates.workspace.create", RatesContracts.Version, "Create a rates workspace",
        [new("request", RatesCommandContracts.NewWorkspace, RatesContracts.Version, Cardinality.One)],
        [new("workspace", RatesCommandContracts.Workspace, RatesContracts.Version, Cardinality.One)],
        ExecutionKind.RequestResponse, [CapabilityEffect.EditWorkspace]);

    public Task<IReadOnlyDictionary<string, JsonNode?>> InvokeAsync(CapabilityInvocation invocation, CancellationToken cancellationToken)
    {
        var caller = RatesCommand.CallerOf(invocation);
        var date = DateOnly.ParseExact(invocation.Inputs["request"]!["valuationDate"]!.GetValue<string>(), "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var state = workspaces().Create(caller.UserId, date, caller.Origin);
        return Task.FromResult<IReadOnlyDictionary<string, JsonNode?>>(new Dictionary<string, JsonNode?>
        {
            ["workspace"] = new JsonObject
            {
                ["workspaceId"] = state.WorkspaceId, ["revision"] = state.Revision,
                ["valuationDate"] = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            },
        });
    }
}

/// <summary>
/// <c>rates.workspace.price</c>, typed <c>price</c> (docs/07 §3; ADR-0016 note 36): the workspace's priceable lines at
/// one revision on one market snapshot, as one Run. (<c>rates.price</c> is the step that prices inside that Run.) Its
/// reading names the snapshot that is the latest as it is read and the lines left out, and the inputs carry that
/// snapshot, so Enter prices what was shown whatever is published meanwhile. Asked with no snapshot, it prices on the
/// latest then, and its answer names it. It gives the Run service the key it was invoked with, so that the command
/// asked again is the same business submission.
/// </summary>
public sealed class RatesWorkspacePrice(Func<RatesWorkspaces> workspaces) : ICapabilityHandler, ILineCommand
{
    public const string Id = "rates.workspace.price";

    public CapabilityDescriptor Descriptor { get; } = new(Id, RatesContracts.Version,
        "Price a rates workspace as it stands: its priceable lines on one market snapshot, as one Run",
        [new("request", RatesCommandContracts.PriceRequest, RatesContracts.Version, Cardinality.One)],
        [new("answer", RatesCommandContracts.PriceAccepted, RatesContracts.Version, Cardinality.One)],
        ExecutionKind.RequestResponse, [CapabilityEffect.Execute], new(["price"], [], RatesEditRules.App));

    public LineRead Read(LineContext line)
    {
        var plan = CommandRequests.Naming(() => workspaces().PlanPrice(line.State.WorkspaceId, line.State.Revision, line.Caller.UserId, null));
        var lines = plan.LeftOut.Count == 0 ? TableWords.Counted(plan.Priced.Count, "line")
            : $"{plan.Priced.Count} of {TableWords.Counted(plan.Priced.Count + plan.LeftOut.Count, "line")}";
        return RatesCommand.Read(line, new JsonObject { ["marketSnapshotId"] = plan.MarketSnapshotId }, [],
            $"Prices {lines} on market {plan.MarketSnapshotId}, as one Run.{(plan.LeftOut.Count == 0 ? "" : $" Left out: {TableWords.Some(plan.LeftOut)}.")}");
    }

    public LineCompletions Complete(LineContext line) => LineCompletions.None;

    public Task<IReadOnlyDictionary<string, JsonNode?>> InvokeAsync(CapabilityInvocation invocation, CancellationToken cancellationToken) =>
        RatesCommand.Asked(invocation, workspaces, (service, request, caller) =>
        {
            var id = CommandRequests.Text(request, "workspaceId");
            var revision = RatesCommand.Revision(service, request, caller);
            var submitted = CommandRequests.Naming(() => service.Price(id, revision, caller.UserId, CommandRequests.Key(invocation, "Price"), CommandRequests.Optional(request, "marketSnapshotId"), caller.Origin));
            var run = CommandRequests.Accepted(submitted);
            return new JsonObject
            {
                ["workspaceId"] = id, ["revision"] = revision, ["said"] = $"Price was accepted as Run {run.RunId}.", ["runId"] = run.RunId,
                // The snapshot the Run was priced on, as the Run's own input names it: the one asked for, or the latest then.
                ["marketSnapshotId"] = CommandRequests.Text(run.Input, "marketSnapshotId"),
            };
        });
}

/// <summary>
/// <c>rates.live.start</c>, typed <c>live start</c> (ADR-0005; ADR-0016 note 36): starts ticking a workspace of the
/// caller's, or joins its running subscription.
/// </summary>
public sealed class RatesLiveStart(Func<RatesWorkspaces> workspaces) : ICapabilityHandler, ILineCommand
{
    public CapabilityDescriptor Descriptor { get; } = new("rates.live.start", RatesContracts.Version, "Start ticking a rates workspace",
        [new("workspace", RatesCommandContracts.LiveTarget, RatesContracts.Version, Cardinality.One)],
        [new("subscription", RatesCommandContracts.LiveSubscription, RatesContracts.Version, Cardinality.One)],
        ExecutionKind.Subscription, [CapabilityEffect.Subscribe], new(["live start"], [], RatesEditRules.App));

    public LineRead Read(LineContext line)
    {
        var service = workspaces();
        var would = service.LiveOn(line.State.WorkspaceId, line.Caller.UserId) is { Running: true }
            ? "Live is running on this workspace already; nothing more would start."
            : service.Priced(line.State) is > 0 and var priced
                ? $"Starts ticking this workspace: {TableWords.Counted(priced, "line")} would be priced on each market update."
                : "Starts ticking this workspace; no line in it can be priced yet.";
        return RatesCommand.ReadLive(line, would);
    }

    public LineCompletions Complete(LineContext line) => LineCompletions.None;

    // The sentence is true whether this started the subscription or joined one that another page started a moment before.
    public Task<IReadOnlyDictionary<string, JsonNode?>> InvokeAsync(CapabilityInvocation invocation, CancellationToken cancellationToken) =>
        RatesCommand.Live(invocation, workspaces, (service, workspaceId, caller) => (service.StartLive(workspaceId, caller.UserId, caller.Origin), "Live is running."));
}

/// <summary>
/// <c>rates.live.stop</c>, typed <c>live stop</c> (ADR-0005; ADR-0016 note 36): stops a workspace's running
/// subscription, for every page watching it.
/// </summary>
public sealed class RatesLiveStop(Func<RatesWorkspaces> workspaces) : ICapabilityHandler, ILineCommand
{
    public CapabilityDescriptor Descriptor { get; } = new("rates.live.stop", RatesContracts.Version, "Stop ticking a rates workspace",
        [new("workspace", RatesCommandContracts.LiveTarget, RatesContracts.Version, Cardinality.One)],
        [new("subscription", RatesCommandContracts.LiveSubscription, RatesContracts.Version, Cardinality.One)],
        ExecutionKind.RequestResponse, [CapabilityEffect.Subscribe], new(["live stop"], [], RatesEditRules.App));

    public LineRead Read(LineContext line)
    {
        workspaces().RunningOn(line.State.WorkspaceId, line.Caller.UserId);
        return RatesCommand.ReadLive(line, "Stops ticking this workspace, for every page watching it.");
    }

    public LineCompletions Complete(LineContext line) => LineCompletions.None;

    public Task<IReadOnlyDictionary<string, JsonNode?>> InvokeAsync(CapabilityInvocation invocation, CancellationToken cancellationToken) =>
        RatesCommand.Live(invocation, workspaces, (service, workspaceId, caller) =>
            (service.StopLiveOn(workspaceId, caller.UserId, caller.Origin), "Live stopped, for every page watching it."));
}

/// <summary>
/// <c>rates.frame.capture</c>, typed <c>capture</c> (docs/07 §3; ADR-0016 note 36): saves a frame a running
/// subscription showed as a Run. (<c>rates.capture</c> is the step inside that Run.) A request names the frame it
/// means whole, or names none. A reading names the frame the subscription shows as it is read, with its time, and
/// Enter captures that frame and never a later one (spine 6); the page's button names the frame the page drew. With
/// none named, it is the frame shown when the command runs.
/// </summary>
public sealed class RatesFrameCapture(Func<RatesWorkspaces> workspaces) : ICapabilityHandler, ILineCommand
{
    public const string Id = "rates.frame.capture";

    // What names a frame: its subscription, that subscription's revision, the input revision and the market snapshot.
    static readonly string[] Frame = ["subscriptionId", "subscriptionRevision", "inputRevision", "marketSnapshotId"];

    public CapabilityDescriptor Descriptor { get; } = new(Id, RatesContracts.Version,
        "Save a frame a rates workspace's live subscription showed, as a Run",
        [new("request", RatesCommandContracts.CaptureRequest, RatesContracts.Version, Cardinality.One)],
        [new("answer", RatesCommandContracts.Captured, RatesContracts.Version, Cardinality.One)],
        ExecutionKind.RequestResponse, [CapabilityEffect.Execute], new(["capture"], [], RatesEditRules.App));

    public LineRead Read(LineContext line)
    {
        var frame = workspaces().ShownOn(line.State.WorkspaceId, line.Caller.UserId).Delivery;
        return RatesCommand.Read(line, new JsonObject
        {
            ["subscriptionId"] = frame.SubscriptionId, ["subscriptionRevision"] = frame.SubscriptionRevision,
            ["inputRevision"] = frame.InputRevision, ["marketSnapshotId"] = frame.Market.SnapshotId,
        }, [], $"Captures the frame of market {frame.Market.SnapshotId} at revision {frame.InputRevision}, as a Run.", frame.CompletedAt);
    }

    public LineCompletions Complete(LineContext line) => LineCompletions.None;

    public Task<IReadOnlyDictionary<string, JsonNode?>> InvokeAsync(CapabilityInvocation invocation, CancellationToken cancellationToken) =>
        RatesCommand.Asked(invocation, workspaces, (service, request, caller) =>
        {
            var id = CommandRequests.Text(request, "workspaceId");
            var named = Frame.Count(name => request[name] is not null);
            if (named != 0 && named != Frame.Length)
                throw new CapabilityFailure("InvalidInput", $"A capture names its frame whole ({TableWords.And(Frame)}), or names none for the frame shown as it runs.");
            var (subscription, seen) = named == 0 ? Shown(service.ShownOn(id, caller.UserId).Delivery)
                : (CommandRequests.Text(request, "subscriptionId"),
                    new FrameIdentity(request["subscriptionRevision"]!.GetValue<int>(), request["inputRevision"]!.GetValue<long>(), CommandRequests.Text(request, "marketSnapshotId")));
            var run = CommandRequests.Accepted(service.Capture(id, subscription, caller.UserId, seen, CommandRequests.Key(invocation, "Capture"), caller.Origin));
            return new JsonObject
            {
                // The revision the workspace holds, which a capture leaves as it is; the frame's own is in the sentence.
                ["workspaceId"] = id, ["revision"] = service.Revision(id, caller.UserId), ["runId"] = run.RunId,
                ["said"] = $"Captured the frame of market {seen.MarketSnapshotId} at revision {seen.InputRevision} as Run {run.RunId}.",
                ["marketSnapshotId"] = seen.MarketSnapshotId,
            };
        });

    static (string Subscription, FrameIdentity Frame) Shown(LiveDelivery frame) =>
        (frame.SubscriptionId, new(frame.SubscriptionRevision, frame.InputRevision, frame.Market.SnapshotId));
}

/// <summary>
/// <c>rates.line.add</c>, typed <c>add line &lt;product&gt;</c> (ADR-0016 note 36): an empty line of the product, to
/// fill in the grid, named by the revision that makes it. A line with its terms typed is the kernel's <c>new</c>.
/// </summary>
public sealed class RatesLineAdd(Func<RatesWorkspaces> workspaces) : ICapabilityHandler, ILineCommand
{
    public const string Id = "rates.line.add";

    // A product by the word it is typed with, in the order the page offers them.
    static readonly IReadOnlyDictionary<string, RatesProduct> Products = Enum.GetValues<RatesProduct>().ToDictionary(p => p.ToString().ToLowerInvariant());

    public CapabilityDescriptor Descriptor { get; } = new(Id, RatesContracts.Version, "Add an empty line of a product to a rates workspace, to fill in its grid",
        [new("request", RatesCommandContracts.LineAddRequest, RatesContracts.Version, Cardinality.One)],
        [new("answer", RatesCommandContracts.LineAdded, RatesContracts.Version, Cardinality.One)],
        ExecutionKind.RequestResponse, [CapabilityEffect.EditWorkspace],
        new(["add line"], [new("product", CommandArgumentKind.Text, Required: true)], RatesEditRules.App));

    public LineRead Read(LineContext line)
    {
        var words = LineWords.Of(line.Text);
        if (words.Count == 0) throw new TableRefused("NoProduct", $"add line takes a product: {TableWords.Or(Products.Keys)}.", null, [.. Products.Keys]);
        var product = words[0].Word.ToLowerInvariant();
        if (!Products.ContainsKey(product))
            throw new TableRefused("UnknownProduct", $"{words[0].Word} is no product a line is added for: {TableWords.Or(Products.Keys)}.", words[0].Word, [.. Products.Keys]);
        if (words.Count > 1) throw new TableRefused("UnreadWords", $"add line takes a product and nothing after it: {words[1].Word} is not read.", words[1].Word);
        return RatesCommand.Read(line, new JsonObject { ["product"] = product }, [new("product", product)],
            $"Adds {RatesWorkspaces.LineAt(line.State.Revision)}, an empty {product} line to fill in the grid.");
    }

    public LineCompletions Complete(LineContext line)
    {
        var (done, word, start) = LineWords.Typing(line.Text);
        return done.Count > 0 ? LineCompletions.None
            : new([.. Products.Keys.Where(p => p.StartsWith(word, StringComparison.OrdinalIgnoreCase)).Select(p => new LineCompletion(p, start))]);
    }

    public Task<IReadOnlyDictionary<string, JsonNode?>> InvokeAsync(CapabilityInvocation invocation, CancellationToken cancellationToken) =>
        RatesCommand.Asked(invocation, workspaces, (service, request, caller) =>
        {
            var named = CommandRequests.Text(request, "product");
            if (!Products.TryGetValue(named, out var product))
                throw new CapabilityFailure("UnknownProduct", $"{named} is no product a line is added for: {TableWords.Or(Products.Keys)}.");
            var (state, line) = service.AddLine(CommandRequests.Text(request, "workspaceId"), RatesCommand.Revision(service, request, caller), caller.UserId, product, caller.Origin);
            return new JsonObject { ["workspaceId"] = state.WorkspaceId, ["revision"] = state.Revision, ["said"] = $"Added {line}, an empty {named} line.", ["line"] = line };
        });
}

static class RatesCommand
{
    public static Caller CallerOf(CapabilityInvocation invocation) =>
        invocation.Caller ?? throw new CapabilityFailure("NoCaller", "A command is invoked for a caller, through the command gateway.");

    /// <summary>The revision a request was made against, or the one the workspace holds where it names none, as its contract says.</summary>
    public static long Revision(RatesWorkspaces service, JsonNode request, Caller caller) =>
        request["revision"]?.GetValue<long>() ?? service.Revision(CommandRequests.Text(request, "workspaceId"), caller.UserId);

    /// <summary>
    /// A line as one of Rates' commands read it (ADR-0016 note 36): the request to invoke it with, which names the
    /// workspace and the revision read with what the reading read for it, each argument as typed, and what it would do.
    /// </summary>
    public static LineRead Read(LineContext line, JsonObject read, IReadOnlyList<LineArgument> arguments, string would, DateTimeOffset? at = null)
    {
        var request = new JsonObject { ["workspaceId"] = line.State.WorkspaceId, ["revision"] = line.State.Revision };
        foreach (var (name, value) in read.ToList()) { read.Remove(name); request[name] = value; }
        return new(new JsonObject { ["request"] = request }, arguments, null, would, at);
    }

    /// <summary>A line of <c>live start</c> or <c>live stop</c> as read: they name the workspace alone, as their contract has it.</summary>
    public static LineRead ReadLive(LineContext line, string would) =>
        new(new JsonObject { ["workspace"] = new JsonObject { ["workspaceId"] = line.State.WorkspaceId } }, [], null, would);

    /// <summary>
    /// A command asked of Rates for its caller: what <paramref name="act"/> answers is the command's answer, and the
    /// workspace service's refusal is the command's, by code.
    /// </summary>
    public static Task<IReadOnlyDictionary<string, JsonNode?>> Asked(CapabilityInvocation invocation, Func<RatesWorkspaces> workspaces,
        Func<RatesWorkspaces, JsonNode, Caller, JsonObject> act)
    {
        var caller = CallerOf(invocation);
        var request = invocation.Inputs["request"] ?? throw new CapabilityFailure("InvalidInput", "The command takes a request.");
        return Refusing(() => Answer("answer", act(workspaces(), request, caller)));
    }

    /// <summary>
    /// <c>live start</c> or <c>live stop</c> for its caller: the answer names the workspace's subscription, the revision
    /// the workspace holds, which neither moves, and what was done.
    /// </summary>
    public static Task<IReadOnlyDictionary<string, JsonNode?>> Live(CapabilityInvocation invocation, Func<RatesWorkspaces> workspaces,
        Func<RatesWorkspaces, string, Caller, (string SubscriptionId, string Said)> act)
    {
        var caller = CallerOf(invocation);
        var workspaceId = CommandRequests.Text(invocation.Inputs["workspace"] ?? throw new CapabilityFailure("InvalidInput", "The command takes a workspace."), "workspaceId");
        return Refusing(() =>
        {
            var service = workspaces();
            var (subscriptionId, said) = act(service, workspaceId, caller);
            return Answer("subscription", new JsonObject
            {
                ["workspaceId"] = workspaceId, ["subscriptionId"] = subscriptionId, ["revision"] = service.Revision(workspaceId, caller.UserId), ["said"] = said,
            });
        });
    }

    static Task<IReadOnlyDictionary<string, JsonNode?>> Answer(string port, JsonObject answer) =>
        Task.FromResult<IReadOnlyDictionary<string, JsonNode?>>(new Dictionary<string, JsonNode?> { [port] = answer });

    // The workspace service's refusals, as the command's: by code, with every reason a line's terms failed on.
    static Task<IReadOnlyDictionary<string, JsonNode?>> Refusing(Func<Task<IReadOnlyDictionary<string, JsonNode?>>> act)
    {
        try
        {
            return act();
        }
        catch (LineTermsRefused refused)
        {
            throw new CapabilityFailure("InvalidTerms", refused.Message, [.. refused.Issues.Select(i => new CompositionIssue("InvalidTerms", "/inputs/terms", i))]);
        }
        catch (WorkspaceRefused refused)
        {
            throw new CapabilityFailure(refused.Code, refused.Message);
        }
        catch (KeyNotFoundException missing)
        {
            throw new CapabilityFailure("UnknownSubscription", missing.Message);
        }
    }
}

/// <summary>
/// The Rates commands and their contracts (ADR-0005). Registered apart from <see cref="RatesModule"/> because they act
/// through the workspace service, which is built from the registry; they reach it only when invoked.
/// </summary>
public sealed class RatesCommandsModule(Func<RatesWorkspaces> workspaces) : IQubeModule
{
    public void Register(CompositionRegistryBuilder builder)
    {
        builder.AddCapability(new RatesWorkspaceCreate(workspaces));
        // The order here is the order the line lists them in (ADR-0016 note 33): Price first, as the page puts it.
        builder.AddCapability(new RatesWorkspacePrice(workspaces));
        builder.AddCapability(new RatesLiveStart(workspaces));
        builder.AddCapability(new RatesLiveStop(workspaces));
        builder.AddCapability(new RatesFrameCapture(workspaces));
        builder.AddCapability(new RatesLineAdd(workspaces));

        JsonObject Text(string title, string? description = null)
        {
            var text = new JsonObject { ["type"] = "string", ["title"] = title };
            if (description is not null) text["description"] = description;
            return text;
        }
        JsonObject Named(string title, string? description = null)
        {
            var text = Text(title, description);
            text["minLength"] = 1;
            return text;
        }
        JsonObject Integer(string title, string? description = null)
        {
            var integer = new JsonObject { ["type"] = "integer", ["title"] = title };
            if (description is not null) integer["description"] = description;
            return integer;
        }
        void Contract(string id, string title, JsonObject properties, params string[] required) =>
            builder.AddContract(new ContractDefinition(id, RatesContracts.Version, title, new JsonObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = new JsonArray([.. required.Select(r => JsonValue.Create(r))]),
                ["additionalProperties"] = false,
            }));

        Contract(RatesCommandContracts.NewWorkspace, "New rates workspace",
            new JsonObject { ["valuationDate"] = new JsonObject { ["type"] = "string", ["format"] = "date", ["title"] = "Valuation date" } }, "valuationDate");
        Contract(RatesCommandContracts.Workspace, "Rates workspace", new JsonObject
        {
            ["workspaceId"] = Text("Workspace"),
            ["revision"] = Integer("Revision"),
            ["valuationDate"] = new JsonObject { ["type"] = "string", ["format"] = "date", ["title"] = "Valuation date" },
        }, "workspaceId", "revision", "valuationDate");
        Contract(RatesCommandContracts.LiveTarget, "Rates workspace to tick", new JsonObject { ["workspaceId"] = Named("Workspace") }, "workspaceId");
        Contract(RatesCommandContracts.LiveSubscription, "Rates live subscription", new JsonObject
        {
            ["workspaceId"] = Text("Workspace"),
            ["subscriptionId"] = Text("Live subscription"),
            ["revision"] = Integer("Revision", "The revision the workspace holds; starting and stopping leave it as it is."),
            ["said"] = Text("What was done, as the command line says it"),
        }, "workspaceId", "subscriptionId", "revision", "said");
        Contract(RatesCommandContracts.PriceRequest, "Price the workspace", new JsonObject
        {
            ["workspaceId"] = Named("Workspace"),
            ["revision"] = CommandRequests.RevisionField(),
            ["marketSnapshotId"] = Named("Market snapshot", "The snapshot to price on, as a market update names it. Left out, the latest as the command runs, which the answer names."),
        }, "workspaceId");
        Contract(RatesCommandContracts.PriceAccepted, "Price accepted", new JsonObject
        {
            ["workspaceId"] = Text("Workspace"),
            ["revision"] = Integer("Revision priced"),
            ["said"] = Text("What was accepted, as the command line says it"),
            ["runId"] = Text("Run", "The Run that prices it; its results are read from the Run once it completes."),
            ["marketSnapshotId"] = Text("Market snapshot priced on"),
        }, "workspaceId", "revision", "said", "runId", "marketSnapshotId");
        Contract(RatesCommandContracts.CaptureRequest, "Capture a live frame", new JsonObject
        {
            ["workspaceId"] = Named("Workspace"),
            ["revision"] = Integer("Revision", "The revision the caller held. A capture saves the frame it names whatever the workspace holds since, so this is not checked."),
            ["subscriptionId"] = Named("Live subscription", "With the three below, the frame to save. All four left out, the frame the subscription shows as the command runs."),
            ["subscriptionRevision"] = Integer("The subscription's revision of the frame"),
            ["inputRevision"] = Integer("The workspace revision the frame was calculated from"),
            ["marketSnapshotId"] = Named("The market snapshot of the frame"),
        }, "workspaceId");
        Contract(RatesCommandContracts.Captured, "Frame captured", new JsonObject
        {
            ["workspaceId"] = Text("Workspace"),
            ["revision"] = Integer("Revision", "The revision the workspace holds; a capture leaves it as it is."),
            ["said"] = Text("What was captured, as the command line says it"),
            ["runId"] = Text("Run", "The Run that holds the frame."),
            ["marketSnapshotId"] = Text("Market snapshot of the frame captured"),
        }, "workspaceId", "revision", "said", "runId", "marketSnapshotId");
        Contract(RatesCommandContracts.LineAddRequest, "Add an empty line", new JsonObject
        {
            ["workspaceId"] = Named("Workspace"),
            ["revision"] = CommandRequests.RevisionField(),
            ["product"] = Named("Product", "irs or ccs."),
        }, "workspaceId", "product");
        Contract(RatesCommandContracts.LineAdded, "Line added", new JsonObject
        {
            ["workspaceId"] = Text("Workspace"),
            ["revision"] = Integer("Revision"),
            ["said"] = Text("What was added, as the command line says it"),
            ["line"] = Text("Line"),
        }, "workspaceId", "revision", "said", "line");
    }
}
