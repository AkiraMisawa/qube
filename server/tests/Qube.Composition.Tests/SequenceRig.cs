using System.Text.Json.Nodes;
using Qube.Composition.Application;
using Qube.Composition.Application.Products;
using Qube.Composition.Application.Saved;
using Qube.Composition.Application.Tables;
using Qube.Composition.Application.Workspaces;
using Qube.Composition.Contracts;

namespace Qube.Composition.Tests;

/// <summary>
/// The stations with their sensors, the board's short forms, the kernel's commands and the line that reads them, over
/// one board of Ana's: the kernel's rig for saved sequences of edits (ADR-0016 §8, notes 60 to 63). Ana and Ben are
/// of the desk north, and Cy of none. Four commands are the rig's own: <c>survey</c> starts work on the board as it
/// stands and edits nothing, as a Price does; <c>watch</c> neither edits nor starts work; <c>nudge</c> edits the
/// board, and can be told to let another hand move it behind its back, to answer nothing of where it left it, or to
/// say that what it did waits for the user; and <c>resend</c> takes another version of the source, which no undo takes
/// back. One short form is the rig's own: <c>poll</c> stands for <c>survey</c>.
/// </summary>
sealed class SequenceRig
{
    public static readonly Caller Ana = new("ana", RunOrigin.Pricer);
    public static readonly Caller Ben = new("ben", RunOrigin.Pricer);
    public static readonly Caller Cy = new("cy", RunOrigin.Pricer);
    public static readonly string Kind = Sequences.Kind(Stations.App);

    public WorkspaceService Workspaces { get; } = Stations.Service();
    public TableRegister Register { get; }
    public CommandService Commands { get; }
    public CommandLines Lines { get; }
    public SavedItems Saved { get; }
    public Survey Surveys { get; }
    public Nudge Nudges { get; }
    public Resend Resends { get; }
    public ManualTime Clock { get; } = new();
    public string Id { get; }

    public SequenceRig(ISavedItemStore? store = null)
    {
        Saved = new SavedItems(store ?? new InMemorySavedItemStore(), new Desks(new Dictionary<string, string> { ["ana"] = "north", ["ben"] = "north" }), Clock);
        Surveys = new Survey(() => Workspaces);
        Nudges = new Nudge(() => Workspaces);
        Resends = new Resend(() => Workspaces);
        var sequences = new Sequences(() => Lines!, () => Commands!, () => Workspaces, Saved);
        Register = new([new Stations.Catalog(sensors: true)]);
        var tables = new TableService(Register, Workspaces);
        var products = new ProductService([new Stations.Products()], [new Stations.SensorRules(Workspaces)], Workspaces, Register);
        var registry = new CompositionRegistry([new TablesModule(() => tables), new WorkspaceCommandsModule(() => Workspaces), new ProductsModule(() => products),
            new DraftsModule(() => Lines!), new SequencesModule(sequences), new Doubles(Surveys, Nudges, Resends)]);
        Commands = new CommandService(registry, new InMemoryDataStore());
        Lines = new CommandLines(registry, Workspaces, Register, [new Stations.ShortForms(new Poll())]);
        Id = Stations.Board(Workspaces).WorkspaceId;
    }

    public WorkspaceState Board => Workspaces.Find(Id)!;

    /// <summary>Another user's board, with the readings the stations sent.</summary>
    public string BoardOf(Caller owner) => Stations.Board(Workspaces, owner.UserId).WorkspaceId;

    public LinePreview Preview(string line, Caller? caller = null, string? board = null) => Lines.Preview(line, board ?? Id, null, caller ?? Ana);

    /// <summary>What a line would do as typed, before Enter; a refusal as its code and its sentence.</summary>
    public string Read(string line, Caller? caller = null, string? board = null)
    {
        var preview = Preview(line, caller, board);
        return preview.Refused is { } refused ? $"{refused.Code}: {refused.Sentence}" : preview.Would!;
    }

    /// <summary>Invokes a command with a request, as the AI's tool does; a refusal as its code and its sentence.</summary>
    public async Task<(JsonObject? Answer, string? Refused)> Ask(string capability, JsonObject request, Caller? caller = null, string? key = null)
    {
        var result = await Commands.InvokeAsync(capability, "1.0.0", new JsonObject { ["request"] = request }, caller ?? Ana, key ?? Guid.NewGuid().ToString(), default);
        return result switch
        {
            CommandResult.Done done => (done.Outputs["answer"]!.AsObject(), null),
            CommandResult.Refused refused => (null, $"{refused.Code}: {string.Join(" ", refused.Issues.Select(i => i.Message))}"),
            _ => throw new InvalidOperationException("Unhandled command result."),
        };
    }

    /// <summary>Saves lines as a sequence through <c>sequence.save</c>, for Ana unless another caller is given.</summary>
    public Task<(JsonObject? Answer, string? Refused)> Save(string name, string? share, string[] lines, Caller? caller = null, string? board = null)
    {
        var request = new JsonObject { ["workspaceId"] = board ?? Id, ["name"] = name, ["lines"] = new JsonArray([.. lines.Select(l => (JsonNode)l)]) };
        if (share is not null) request["share"] = share;
        return Ask(SequenceSave.Id, request, caller);
    }

    /// <summary>Runs a saved sequence through <c>sequence.run</c>, on Ana's board for Ana unless told otherwise.</summary>
    public Task<(JsonObject? Answer, string? Refused)> RunSequence(string sequence, Caller? caller = null, string? board = null, long? revision = null, DateTimeOffset? saved = null,
        string? key = null)
    {
        var request = new JsonObject { ["workspaceId"] = board ?? Id, ["sequence"] = sequence };
        if (revision is { } read) request["revision"] = read;
        if (saved is { } at) request["saved"] = at.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        return Ask(SequenceRun.Id, request, caller, key);
    }

    /// <summary>A sequence as its store holds it, kept for Ana without the save's checks: one saved before the commands were what they are.</summary>
    public void Keep(string name, params (string Line, string Names)[] lines) => Saved.Save(Kind, "sequence", "ana", name, SavedVisibility.Private,
        new JsonObject { ["lines"] = new JsonArray([.. lines.Select(l => (JsonNode)new JsonObject { ["line"] = l.Line, ["names"] = l.Names })]) });

    /// <summary>The commands the board's log holds that can be taken back, the latest last, each as its words and where it came from.</summary>
    public (string? Said, RunOrigin? Origin)[] Logged(string? board = null) =>
        [.. CommandHistory.Read(Workspaces.Find(board ?? Id)!.Log).Undo.Select(c => (c.Stamp?.Said, c.First.Origin))];

    /// <summary>A row's value in a field, as a command writes it.</summary>
    public string Written(string table, string key, string field, string? board = null)
    {
        var state = Workspaces.Find(board ?? Id)!;
        var bound = Register.Of(state).Single(t => t.Descriptor.TableId == table);
        return FieldForms.Write(bound.Descriptor.Fields.Single(f => f.Name == field).Spec, bound.Table.Rows(state).Single(r => r.Key == key).Value(field));
    }

    /// <summary>The lines a user's sequence of the board's application holds, each with what it named.</summary>
    public string[] Kept(string name, string owner = "ana") =>
        [.. Saved.Visible(Kind, owner).Single(i => i.Owner == owner && i.Name == name).Content["lines"]!.AsArray().Select(l => $"{l!["line"]} [{l["names"]}]")];

    /// <summary>Runs a line as Enter does, and answers what its command answered.</summary>
    public async Task<JsonObject> Run(string line, Caller? caller = null)
    {
        var preview = Preview(line, caller);
        Assert.True(preview.Refused is null, preview.Refused?.Sentence);
        var result = await Commands.InvokeAsync(preview.Command!.Id, preview.Command.Version, preview.Inputs!, caller ?? Ana, Guid.NewGuid().ToString(), default);
        return result is CommandResult.Done done ? done.Outputs.Values.Single()!.AsObject()
            : throw new Xunit.Sdk.XunitException($"{line} was refused: {string.Join(" ", ((CommandResult.Refused)result).Issues.Select(i => i.Message))}");
    }
}

/// <summary>
/// <c>survey</c> (<c>weather.survey</c>): starts a survey of the board at the revision it stands at, and edits nothing.
/// It remembers the revision each was started at, and whose command it was.
/// </summary>
sealed class Survey(Func<WorkspaceService> workspaces) : ICapabilityHandler, ILineCommand
{
    public const string Id = "weather.survey";

    public List<(long Revision, RunOrigin Origin)> Started { get; } = [];

    public CapabilityDescriptor Descriptor { get; } = new(Id, "1.0.0", "Start a survey of the board as it stands",
        [new("request", "weather.board-request", "1.0.0", Cardinality.One)], [new("answer", "weather.board-answer", "1.0.0", Cardinality.One)],
        ExecutionKind.RequestResponse, [CapabilityEffect.Execute], new(["survey"], [], Stations.App));

    public LineRead Read(LineContext line) =>
        new(new JsonObject { ["request"] = new JsonObject { ["workspaceId"] = line.State.WorkspaceId, ["revision"] = line.State.Revision } }, [], null,
            $"Starts a survey of the board at revision {line.State.Revision}.");

    public LineCompletions Complete(LineContext line) => LineCompletions.None;

    public Task<IReadOnlyDictionary<string, JsonNode?>> InvokeAsync(CapabilityInvocation invocation, CancellationToken cancellationToken)
    {
        var request = invocation.Inputs["request"]!;
        try
        {
            var state = WorkspaceCommands.Owned(workspaces(), request["workspaceId"]!.GetValue<string>(), invocation.Caller!, request["revision"]?.GetValue<long>());
            Started.Add((state.Revision, invocation.Caller!.Origin));
            var answer = new JsonObject { ["workspaceId"] = state.WorkspaceId, ["revision"] = state.Revision, ["said"] = $"Survey {Started.Count} was started at revision {state.Revision}.", ["survey"] = Started.Count };
            return Task.FromResult<IReadOnlyDictionary<string, JsonNode?>>(new Dictionary<string, JsonNode?> { ["answer"] = answer });
        }
        catch (WorkspaceRefused refused)
        {
            throw new CapabilityFailure(refused.Code, refused.Message);
        }
    }
}

/// <summary>
/// <c>nudge</c> (<c>weather.nudge</c>): raises N1's altitude by one, as its own command. <see cref="After"/> is what
/// another hand does to the board once the nudge is written and before it answers; <see cref="Silent"/> has it answer
/// nothing of where it left the board, which no command of a workspace may; <see cref="Waits"/> has it say that what
/// it did waits for the user (<c>stays</c>, note 33).
/// </summary>
sealed class Nudge(Func<WorkspaceService> workspaces) : ICapabilityHandler, ILineCommand
{
    public Action? After { get; set; }
    public bool Silent { get; set; }
    public bool Waits { get; set; }

    public CapabilityDescriptor Descriptor { get; } = new("weather.nudge", "1.0.0", "Raise the first station by one",
        [new("request", "weather.board-request", "1.0.0", Cardinality.One)], [new("answer", "weather.board-answer", "1.0.0", Cardinality.One)],
        ExecutionKind.RequestResponse, [CapabilityEffect.EditWorkspace], new(["nudge"], [], Stations.App));

    public LineRead Read(LineContext line) =>
        new(new JsonObject { ["request"] = new JsonObject { ["workspaceId"] = line.State.WorkspaceId, ["revision"] = line.State.Revision } }, [], null, "Raises N1 by one.");

    public LineCompletions Complete(LineContext line) => LineCompletions.None;

    public Task<IReadOnlyDictionary<string, JsonNode?>> InvokeAsync(CapabilityInvocation invocation, CancellationToken cancellationToken)
    {
        var request = invocation.Inputs["request"]!;
        var caller = invocation.Caller!;
        try
        {
            var service = workspaces();
            var state = WorkspaceCommands.Owned(service, request["workspaceId"]!.GetValue<string>(), caller, request["revision"]?.GetValue<long>());
            var target = Stations.Target("station:N1", "altitude");
            var raised = state.Overrides.TryGetValue(target, out var held) ? held.Value!.GetValue<decimal>() + 1 : 121m;
            var next = service.Set(state.WorkspaceId, state.Revision, caller.UserId, target, raised, caller.Origin);
            After?.Invoke();
            var answer = Silent ? new JsonObject { ["workspaceId"] = next.WorkspaceId } : new JsonObject { ["workspaceId"] = next.WorkspaceId, ["revision"] = next.Revision, ["said"] = $"Raised N1 to {raised:0}." };
            if (Waits) answer["stays"] = true;
            return Task.FromResult<IReadOnlyDictionary<string, JsonNode?>>(new Dictionary<string, JsonNode?> { ["answer"] = answer });
        }
        catch (WorkspaceRefused refused)
        {
            throw new CapabilityFailure(refused.Code, refused.Message);
        }
    }
}

/// <summary>
/// <c>resend</c> (<c>weather.resend</c>): takes the stations' readings as sent again, each time another version of
/// the board's source. It edits the board, and is a command no undo takes back, nor reaches past (ADR-0016 §11).
/// </summary>
sealed class Resend(Func<WorkspaceService> workspaces) : ICapabilityHandler, ILineCommand
{
    // The readings as the stations sent them, under the version of a later sending.
    sealed class Sent(int sending) : ISourceVersion
    {
        readonly Stations.Source first = new();

        public string Version => $"sent-{sending}";
        public JsonObject Descriptor => new() { ["version"] = Version };
        public SourceValue? Read(string entity, string field) => first.Read(entity, field);
    }

    int sendings = 1;

    public CapabilityDescriptor Descriptor { get; } = new("weather.resend", "1.0.0", "Take the readings as the stations sent them again",
        [new("request", "weather.board-request", "1.0.0", Cardinality.One)], [new("answer", "weather.board-answer", "1.0.0", Cardinality.One)],
        ExecutionKind.RequestResponse, [CapabilityEffect.EditWorkspace], new(["resend"], [], Stations.App));

    public LineRead Read(LineContext line) =>
        new(new JsonObject { ["request"] = new JsonObject { ["workspaceId"] = line.State.WorkspaceId, ["revision"] = line.State.Revision } }, [], null, "Takes the readings as sent again.");

    public LineCompletions Complete(LineContext line) => LineCompletions.None;

    public Task<IReadOnlyDictionary<string, JsonNode?>> InvokeAsync(CapabilityInvocation invocation, CancellationToken cancellationToken)
    {
        var request = invocation.Inputs["request"]!;
        var caller = invocation.Caller!;
        try
        {
            var service = workspaces();
            var state = WorkspaceCommands.Owned(service, request["workspaceId"]!.GetValue<string>(), caller, request["revision"]?.GetValue<long>());
            var next = service.Reacquire(state.WorkspaceId, state.Revision, caller.UserId, state.Context, new Sent(++sendings), caller.Origin, "resend").State;
            var answer = new JsonObject { ["workspaceId"] = next.WorkspaceId, ["revision"] = next.Revision, ["said"] = "Took the readings as sent again." };
            return Task.FromResult<IReadOnlyDictionary<string, JsonNode?>>(new Dictionary<string, JsonNode?> { ["answer"] = answer });
        }
        catch (WorkspaceRefused refused)
        {
            throw new CapabilityFailure(refused.Code, refused.Message);
        }
    }
}

/// <summary><c>poll</c> (<c>weather.poll</c>): a short form that stands for <c>survey</c>, a command that starts work.</summary>
sealed class Poll : IShortForm
{
    public ShortFormDescriptor Descriptor { get; } = new("weather.poll", "Start a survey of the board", new(["poll"], [], Stations.App));

    public string Expand(LineContext line) => "survey";

    public LineCompletions Complete(LineContext line) => LineCompletions.None;
}

/// <summary>A clock a test moves: a sequence saved twice is saved at two moments.</summary>
sealed class ManualTime : TimeProvider
{
    DateTimeOffset now = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => now;

    public void Advance(TimeSpan by) => now += by;
}

/// <summary><c>watch</c> (<c>weather.watch</c>): follows the board, which neither edits it nor starts work on it.</summary>
sealed class Watch : ICapabilityHandler, ILineCommand
{
    public CapabilityDescriptor Descriptor { get; } = new("weather.watch", "1.0.0", "Follow the board",
        [new("request", "weather.board-request", "1.0.0", Cardinality.One)], [new("answer", "weather.board-answer", "1.0.0", Cardinality.One)],
        ExecutionKind.RequestResponse, [CapabilityEffect.Subscribe], new(["watch"], [], Stations.App));

    public LineRead Read(LineContext line) =>
        new(new JsonObject { ["request"] = new JsonObject { ["workspaceId"] = line.State.WorkspaceId, ["revision"] = line.State.Revision } }, [], null, "Follows the board.");

    public LineCompletions Complete(LineContext line) => LineCompletions.None;

    public Task<IReadOnlyDictionary<string, JsonNode?>> InvokeAsync(CapabilityInvocation invocation, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("No test follows the board.");
}

/// <summary>The rig's four commands and the contracts they share.</summary>
sealed class Doubles(Survey survey, Nudge nudge, Resend resend) : IQubeModule
{
    public void Register(CompositionRegistryBuilder builder)
    {
        builder.AddCapability(survey);
        builder.AddCapability(nudge);
        builder.AddCapability(resend);
        builder.AddCapability(new Watch());
        builder.AddContract(new ContractDefinition("weather.board-request", "1.0.0", "A request of the board", new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["workspaceId"] = new JsonObject { ["type"] = "string" }, ["revision"] = new JsonObject { ["type"] = "integer" } },
            ["required"] = new JsonArray("workspaceId"),
            ["additionalProperties"] = false,
        }));
        builder.AddContract(new ContractDefinition("weather.board-answer", "1.0.0", "What a command of the board answered", new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["workspaceId"] = new JsonObject { ["type"] = "string" }, ["revision"] = new JsonObject { ["type"] = "integer" }, ["said"] = new JsonObject { ["type"] = "string" },
                ["survey"] = new JsonObject { ["type"] = "integer" }, ["stays"] = new JsonObject { ["type"] = "boolean" },
            },
            ["required"] = new JsonArray("workspaceId"),
            ["additionalProperties"] = false,
        }));
    }
}
