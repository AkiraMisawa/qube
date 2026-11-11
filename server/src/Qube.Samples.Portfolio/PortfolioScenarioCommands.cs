using System.Text.Json.Nodes;
using Qube.Composition.Application;
using Qube.Composition.Application.Saved;
using Qube.Composition.Application.Tables;
using Qube.Composition.Application.Workspaces;
using Qube.Composition.Contracts;

namespace Qube.Samples.Portfolio;

/// <summary>
/// The scenario commands as command capabilities (ADR-0005, ADR-0010 §4, ADR-0016 note 32 of step 3): define, include,
/// exclude, remove, duplicate, clear, ladder, cross, save and load. Each says how it is typed and reads its own line into
/// the request the AI writes, so the line, the Scenarios tab and the AI reach one handler with one set of checks. A
/// reading is a trial of the command's plan, with nothing written; what a command does is in the workspace's log with
/// the caller's origin, named by the words that would type it. A scenario is named by its name, of several words where
/// a generator made it, so these are verbs of the pricer and no rows of a registered table (note 32).
/// </summary>
public static class PortfolioScenarioCommands
{
    public const string DefineRequest = "xva.scenario-request";
    public const string NameRequest = "xva.scenario-name-request";
    public const string DuplicateRequest = "xva.scenario-duplicate-request";
    public const string ClearRequest = "xva.scenarios-clear-request";
    public const string GeneratorRequest = "xva.scenario-generator-request";
    public const string SaveRequest = "xva.scenario-save-request";
    public const string LoadRequest = "xva.scenario-load-request";
    public const string ScenariosMade = "xva.scenarios-made";

    /// <summary>Names as a reading lists them: the first three, then how many more, since a set may hold dozens.</summary>
    public static string Some(IReadOnlyList<string> names) =>
        names.Count <= 3 ? TableWords.And(names) : $"{string.Join(", ", names.Take(3))} and {names.Count - 3} more";

    /// <summary>What a reading of <c>scenario &lt;name&gt; …</c> says it would do: the scenario made, or the one whose shifts are replaced, with the shifts as read.</summary>
    public static string WouldDefine(ScenarioPlan plan) =>
        $"{(plan.Replaced.Count == 0 ? $"Makes the scenario {plan.Made[0]}" : $"Replaces the shifts of {plan.Made[0]}")} with {PortfolioCommand.Counted(plan.Shifts.Count, "shift")}: {string.Join("; ", plan.Shifts.Select(s => s.Text))}.";

    /// <summary>What <c>scenario &lt;name&gt; …</c> says back, in the command line's words.</summary>
    public static string Defined(ScenarioPlan plan) =>
        plan.Replaced.Count == 0 ? $"Made the scenario {plan.Made[0]}." : $"Replaced the shifts of {plan.Made[0]}.";

    static string Making(string verb, ScenarioPlan plan, string names) =>
        $"{verb} {plan.Made.Count} scenario(s){(plan.Replaced.Count == 0 ? "" : $", replacing {plan.Replaced.Count} of the same name")}: {names}.";

    /// <summary>
    /// What a reading of a generator says it would do: how many scenarios it makes, and the pairs of a case and a market
    /// Price would then price, or refuse to (ADR-0010 §4's note of 2026-11-09): a set that is made may leave more than Price prices.
    /// </summary>
    public static string WouldGenerate(ScenarioPlan plan, int pairs, int cap) =>
        $"{Making("Makes", plan, Some(plan.Made))} " + (pairs <= cap
            ? $"Price would then price {PortfolioCommand.Counted(pairs, "pair")} of a case and a market, of {cap} at most."
            : $"Price would then refuse to price {PortfolioCommand.Counted(pairs, "pair")} of a case and a market: {pairs - cap} more than the {cap} it prices at most.");

    /// <summary>What a generator says back: how many it made, and those of the same name it replaced.</summary>
    public static string Generated(ScenarioPlan plan) => Making("Made", plan, string.Join(", ", plan.Made));

    // A set loaded, in a reading's words or the answer's: the names it replaces and the names it adds are said alike.
    static string Loading(string verb, string set, ScenarioPlan plan, Func<IReadOnlyList<string>, string> names) =>
        $"{verb} {plan.Made.Count} scenario(s) from {set}{(plan.Replaced.Count == 0 ? "" : $", replacing {names(plan.Replaced)}")}: {names(plan.Made)}.";

    public static string Loaded(string set, ScenarioPlan plan) => Loading("Loaded", set, plan, names => string.Join(", ", names));

    public static string Included(string name, bool included) =>
        included ? $"Price prices the scenario {name}." : $"Price leaves the scenario {name} out; its shifts stay.";

    static string Already(string name, bool included) =>
        included ? $"Price already prices the scenario {name}." : $"Price already leaves the scenario {name} out.";

    // A saved set in words: how many scenarios, under which name, for whom, and whether it replaces the user's own.
    static string SavedAs(SetSaved saved)
    {
        var who = saved.Visibility switch
        {
            SavedVisibility.All => "every user",
            SavedVisibility.Desk => $"the desk {saved.Desk ?? throw new InvalidOperationException($"The set {saved.Set} is shared with a desk it does not name.")}",
            SavedVisibility.Private => "you alone",
            _ => throw new InvalidOperationException($"No visibility {saved.Visibility}."),
        };
        return $"{saved.Scenarios} scenario(s) as the set {saved.Set}, for {who}{(saved.Replaced ? $", replacing your {saved.Set}" : "")}.";
    }

    public static string Saved(SetSaved saved) => $"Saved {SavedAs(saved)}";

    // What a reading has to say what its command would do: the plan, the request it is of, and the workspace as it
    // stands and as the plan would leave it.
    sealed record Trial(ScenarioPlan Plan, JsonNode Request, WorkspaceState Before, WorkspaceState After, PortfolioWorkspaces Service);

    // One scenario command: how a line's words are its request's fields, the plan a request asks for, and what is
    // said of the plan before it is written and after.
    sealed record Verb(string Id, string Title, string Request, string Answer, CommandForm Form,
        Func<LineContext, JsonObject> Typed,
        Func<PortfolioWorkspaces, WorkspaceState, string, JsonNode, ScenarioPlan> Plan,
        Func<Trial, string> Would,
        Func<ScenarioPlan, JsonNode, string> Said,
        Func<LineContext, PortfolioWorkspaces, LineCompletions>? Complete = null);

    static string Text(JsonNode request, string name) => PortfolioCommand.Text(request, name).Trim();

    static CommandForm Form(string words, params (string Name, bool Required)[] arguments) =>
        new([words], [.. arguments.Select(a => new CommandArgument(a.Name, CommandArgumentKind.Text, a.Required))], PortfolioEditRules.App);

    // The scenario a line names, which may be several words: every word after the command's own. None is refused with the names there are.
    static JsonObject Named(LineContext line, string said)
    {
        var all = PortfolioScenarios.All(line.State);
        return line.Text.Trim() is { Length: > 0 } name ? new JsonObject { ["name"] = name }
            : throw new WorkspaceRefused("UnknownScenario", $"{said} takes the scenario's name: " +
                (all.Count == 0 ? "the workspace has none." : $"the scenarios are {string.Join(", ", all.Select(s => s.Name))}."));
    }

    // The scenarios whose names begin with what is typed after the command's words, each offered whole with its shift count.
    static LineCompletions Names(LineContext line, Func<ScenarioView, bool> offered)
    {
        // No space after the command's own words: they are still being typed, and nothing follows yet.
        if (line.Text.Length == 0) return LineCompletions.None;
        var from = line.Text.Length - line.Text.TrimStart().Length;
        var typed = line.Text[from..];
        return new([.. PortfolioScenarios.All(line.State).Where(s => offered(s) && s.Name.StartsWith(typed, StringComparison.OrdinalIgnoreCase))
            .Select(s => new LineCompletion(s.Name, from, $"{s.Shifts} shift(s){(s.Included ? "" : ", excluded")}"))]);
    }

    static int ShiftsOf(WorkspaceState state, string name) => PortfolioScenarios.All(state).Single(s => PortfolioScenarios.Same(s.Name, name)).Shifts;

    public static void Register(CompositionRegistryBuilder builder, Func<PortfolioWorkspaces> workspaces)
    {
        void Add(Verb verb) => builder.AddCapability(new Command(verb, workspaces));

        Add(new("xva.scenario", "Define a scenario of an XVA workspace with all its shifts, replacing one of its name, such as scenario s1 jpy_ois +10bp; usdjpy -5%", DefineRequest, ScenariosMade,
            Form("scenario", ("name", true), ("shifts", true)),
            line =>
            {
                var words = LineWords.Of(line.Text);
                return words.Count < 2
                    ? throw new WorkspaceRefused("InvalidScenario", "scenario takes a name, then its shifts separated by semicolons, such as scenario s1 jpy_ois +10bp; usdjpy -5%.")
                    : new JsonObject { ["name"] = words[0].Word, ["shifts"] = line.Text[words[1].Start..].Trim() };
            },
            (service, raw, _, request) => service.PlanScenario(raw, raw.Revision, Text(request, "name"), Text(request, "shifts")),
            trial => WouldDefine(trial.Plan),
            (plan, _) => Defined(plan)));
        foreach (var (verb, included, title) in new[] { ("include", true, "Price a scenario of an XVA workspace beside the base market"), ("exclude", false, "Leave a scenario of an XVA workspace out of Price; its shifts stay") })
            Add(new($"xva.scenario.{verb}", title, NameRequest, PortfolioCommandContracts.Edited,
                Form($"scenario {verb}", ("name", true)),
                line => Named(line, $"scenario {verb}"),
                (service, raw, _, request) => service.PlanInclude(raw, Text(request, "name"), included),
                trial => trial.Plan.Command.Changes.Count == 0 ? $"Nothing would change: {Already(trial.Plan.Named[0], included)}"
                    : included ? $"Price would price the scenario {trial.Plan.Named[0]}." : $"Price would leave the scenario {trial.Plan.Named[0]} out; its shifts stay.",
                (plan, _) => plan.Command.Changes.Count == 0 ? $"Nothing changed: {Already(plan.Named[0], included)}" : Included(plan.Named[0], included),
                // What the command would change: exclude offers the scenarios Price prices, include those it leaves out.
                (line, _) => Names(line, s => s.Included != included)));
        Add(new("xva.scenario.remove", "Remove a scenario of an XVA workspace with all its shifts", NameRequest, PortfolioCommandContracts.Edited,
            Form("scenario remove", ("name", true)),
            line => Named(line, "scenario remove"),
            (service, raw, _, request) => service.PlanRemoveScenario(raw, Text(request, "name")),
            trial => $"Removes the scenario {trial.Plan.Named[0]}, with its {PortfolioCommand.Counted(ShiftsOf(trial.Before, trial.Plan.Named[0]), "shift")}.",
            (plan, _) => $"Removed the scenario {plan.Named[0]}.",
            (line, _) => Names(line, _ => true)));
        Add(new("xva.scenario.duplicate", "Copy a scenario of an XVA workspace under a new name, to be edited", DuplicateRequest, ScenariosMade,
            Form("scenario duplicate", ("name", true), ("copy", true)),
            line =>
            {
                // The copy's name is one word, the last; the scenario to copy is every word before it.
                var words = LineWords.Of(line.Text);
                return words.Count < 2
                    ? throw new WorkspaceRefused("InvalidScenario", "scenario duplicate takes the scenario to copy, then the copy's name in one word.")
                    : new JsonObject { ["name"] = line.Text[..words[^1].Start].Trim(), ["copy"] = words[^1].Word };
            },
            (service, raw, _, request) => service.PlanDuplicate(raw, raw.Revision, Text(request, "name"), Text(request, "copy")),
            trial => $"Copies the {PortfolioCommand.Counted(ShiftsOf(trial.Before, trial.Plan.Named[0]), "shift")} of {trial.Plan.Named[0]} as the scenario {trial.Plan.Made[0]}.",
            (plan, _) => $"Duplicated {plan.Named[0]} as {plan.Made[0]}.",
            (line, _) => Names(line, _ => true)));
        Add(new("xva.scenarios.clear", "Remove every scenario of an XVA workspace; Price then prices the base market alone", ClearRequest, PortfolioCommandContracts.Edited,
            Form("scenarios clear"),
            _ => [],
            (service, raw, _, _) => service.PlanClearScenarios(raw),
            trial => $"Removes {PortfolioCommand.Counted(trial.Plan.Named.Count, "scenario")}: {Some(trial.Plan.Named)}.",
            (_, _) => "Removed every scenario; Price prices the base market alone."));
        foreach (var (generator, title) in new[]
        {
            ("ladder", "Make a scenario per size of one factor of an XVA workspace, such as scenario ladder jpy_ois -50bp..+50bp step 10bp"),
            ("cross", "Make a scenario per combination of several factors' sizes of an XVA workspace, such as scenario cross jpy_ois -50bp, 0, +50bp x usdjpy -10%, 0, +10%"),
        })
            Add(new($"xva.scenario.{generator}", title, GeneratorRequest, ScenariosMade,
                Form($"scenario {generator}", ("factors", true)),
                line => new JsonObject { ["factors"] = line.Text.Trim() },
                (service, raw, _, request) => service.PlanGenerate(raw, raw.Revision, generator, Text(request, "factors")),
                trial =>
                {
                    var (pairs, cap) = trial.Service.Pairs(trial.After);
                    return WouldGenerate(trial.Plan, pairs, cap);
                },
                (plan, _) => Generated(plan)));
        builder.AddCapability(new Save(workspaces));
        Add(new("xva.scenario.load", "Add a saved set's scenarios to an XVA workspace, replacing those of the same name", LoadRequest, ScenariosMade,
            Form("scenario load", ("set", true)),
            line => new JsonObject { ["set"] = line.Text.Trim() },
            (service, raw, user, request) => service.PlanLoad(raw, raw.Revision, user, Text(request, "set")),
            trial => Loading("Loads", Text(trial.Request, "set"), trial.Plan, Some),
            (plan, request) => Loaded(Text(request, "set"), plan),
            // The sets the user can see whose names begin with what is typed, each by the name load takes for it.
            (line, service) =>
            {
                if (line.Text.Length == 0) return LineCompletions.None;
                var from = line.Text.Length - line.Text.TrimStart().Length;
                return new([.. service.ScenarioSets(line.Caller.UserId).Where(s => s.Label.StartsWith(line.Text[from..], StringComparison.OrdinalIgnoreCase))
                    .Select(s => new LineCompletion(s.Label, from, $"{s.Scenarios} scenario(s), for {For(s.Visibility)}, saved by {s.Owner}"))]);
            }));

        JsonObject Field(string title, string description) => new() { ["type"] = "string", ["minLength"] = 1, ["title"] = title, ["description"] = description };
        void Contract(string id, string title, JsonObject properties, params string[] required) =>
            builder.AddContract(new ContractDefinition(id, PortfolioContracts.Version, title, new JsonObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = new JsonArray([.. required.Select(r => JsonValue.Create(r))]),
                ["additionalProperties"] = false,
            }));
        Contract(DefineRequest, "Define a scenario", new JsonObject
        {
            ["workspaceId"] = PortfolioCommand.WorkspaceField(),
            ["revision"] = PortfolioCommand.RevisionField(),
            ["name"] = Field("Name", "The scenario's name, such as s1 or rates-up. One of that name is replaced whole."),
            ["shifts"] = Field("Shifts", "Every shift of the scenario as written, separated by semicolons: jpy_ois +10bp; usdjpy -5%; jpy_ois 5y +3bp. "
                + $"An index alone shifts the whole curve, a tenor narrows it to that point; bp is absolute, % relative. The indices are {ScenarioCatalog.Listed}."),
        }, "workspaceId", "name", "shifts");
        Contract(NameRequest, "A scenario by name", new JsonObject
        {
            ["workspaceId"] = PortfolioCommand.WorkspaceField(),
            ["revision"] = PortfolioCommand.RevisionField(),
            ["name"] = Field("Scenario", "The scenario's name, as the workspace lists it."),
        }, "workspaceId", "name");
        Contract(DuplicateRequest, "Duplicate a scenario", new JsonObject
        {
            ["workspaceId"] = PortfolioCommand.WorkspaceField(),
            ["revision"] = PortfolioCommand.RevisionField(),
            ["name"] = Field("Scenario", "The scenario to copy, as the workspace lists it."),
            ["copy"] = Field("New name", "The copy's name, which no scenario of the workspace has."),
        }, "workspaceId", "name", "copy");
        Contract(ClearRequest, "Remove every scenario", new JsonObject
        {
            ["workspaceId"] = PortfolioCommand.WorkspaceField(),
            ["revision"] = PortfolioCommand.RevisionField(),
        }, "workspaceId");
        Contract(GeneratorRequest, "Generate scenarios", new JsonObject
        {
            ["workspaceId"] = PortfolioCommand.WorkspaceField(),
            ["revision"] = PortfolioCommand.RevisionField(),
            ["factors"] = Field("Factors", "As the command line takes it after scenario ladder or scenario cross. Ladder: a factor, a range and a step, "
                + "jpy_ois -50bp..+50bp step 10bp. Cross: lists separated by x, each a factor and its sizes, jpy_ois -50bp, 0, +50bp x usdjpy -10%, 0, +10%."),
        }, "workspaceId", "factors");
        Contract(SaveRequest, "Save the scenarios as a set", new JsonObject
        {
            ["workspaceId"] = PortfolioCommand.WorkspaceField(),
            ["revision"] = PortfolioCommand.RevisionField(),
            ["set"] = Field("Set", "The set's name, one word without @, such as rates-up. The user's own set of that name is replaced."),
            ["share"] = new JsonObject
            {
                ["type"] = "string", ["enum"] = new JsonArray("desk", "all"), ["title"] = "Shared with",
                ["description"] = "desk: the user's desk sees it; all: every user does. Left out, the user alone.",
            },
        }, "workspaceId", "set");
        Contract(LoadRequest, "Load a saved set", new JsonObject
        {
            ["workspaceId"] = PortfolioCommand.WorkspaceField(),
            ["revision"] = PortfolioCommand.RevisionField(),
            ["set"] = Field("Set", "A set the user can see, by name, or set@owner where two share a name, such as rates@ben."),
        }, "workspaceId", "set");
        Contract(ScenariosMade, "Scenarios made", new JsonObject
        {
            ["workspaceId"] = new JsonObject { ["type"] = "string", ["title"] = "Workspace" },
            ["revision"] = new JsonObject { ["type"] = "integer", ["title"] = "Revision" },
            ["made"] = new JsonObject { ["type"] = "integer", ["title"] = "Scenarios made" },
            ["said"] = new JsonObject { ["type"] = "string", ["title"] = "What was made and replaced, as the command line says it" },
        }, "workspaceId", "revision", "made", "said");
    }

    static string For(SavedVisibility visibility) => visibility switch
    {
        SavedVisibility.All => "all",
        SavedVisibility.Desk => "the desk",
        SavedVisibility.Private => "you alone",
        _ => throw new InvalidOperationException($"No visibility {visibility}."),
    };

    // A scenario command that changes the workspace: its reading is a trial of its plan, and invoking it writes the plan.
    sealed class Command(Verb verb, Func<PortfolioWorkspaces> workspaces) : ICapabilityHandler, ILineCommand
    {
        public CapabilityDescriptor Descriptor { get; } = new(verb.Id, PortfolioContracts.Version, verb.Title,
            [new("request", verb.Request, PortfolioContracts.Version, Cardinality.One)],
            [new("answer", verb.Answer, PortfolioContracts.Version, Cardinality.One)],
            ExecutionKind.RequestResponse, [CapabilityEffect.EditWorkspace], verb.Form);

        public LineRead Read(LineContext line)
        {
            var service = workspaces();
            var typed = verb.Typed(line);
            var plan = verb.Plan(service, line.State, line.Caller.UserId, typed);
            var after = service.Trial(line.State, line.Caller.UserId, plan.Command, line.Caller.Origin);
            var would = verb.Would(new(plan, typed, line.State, after, service));
            return PortfolioCommand.Read(line, typed, would);
        }

        public LineCompletions Complete(LineContext line) => verb.Complete is { } complete ? complete(line, workspaces()) : LineCompletions.None;

        public Task<IReadOnlyDictionary<string, JsonNode?>> InvokeAsync(CapabilityInvocation invocation, CancellationToken cancellationToken) =>
            PortfolioCommand.Answer(invocation, workspaces, (service, request, caller, revision) =>
            {
                var (state, plan) = service.WriteScenarios(Text(request, "workspaceId"), revision, caller.UserId, raw => verb.Plan(service, raw, caller.UserId, request), caller.Origin);
                return (state.WorkspaceId, state.Revision, verb.Answer == ScenariosMade ? plan.Made.Count : null, verb.Said(plan, request));
            });
    }

    /// <summary>
    /// <c>xva.scenario.save</c> (<c>scenario save rates-up desk</c>, ADR-0010 §4, C32): every scenario of the workspace as
    /// a set of the user's. It changes no workspace and is in no workspace's log; its reading checks the save as the save
    /// checks it, says whom the set would be for and whether it replaces one, and keeps nothing.
    /// </summary>
    sealed class Save(Func<PortfolioWorkspaces> workspaces) : ICapabilityHandler, ILineCommand
    {
        public CapabilityDescriptor Descriptor { get; } = new("xva.scenario.save", PortfolioContracts.Version,
            "Save every scenario of an XVA workspace as a set, for the user alone unless shared with their desk or all",
            [new("request", SaveRequest, PortfolioContracts.Version, Cardinality.One)],
            [new("answer", PortfolioCommandContracts.Edited, PortfolioContracts.Version, Cardinality.One)],
            ExecutionKind.RequestResponse, [CapabilityEffect.SaveItem], Form("scenario save", ("set", true), ("share", false)));

        static readonly (string Word, string Detail)[] Shares = [("desk", "your desk sees it"), ("all", "every user sees it")];

        public LineRead Read(LineContext line)
        {
            var words = LineWords.Of(line.Text);
            if (words.Count > 2) throw new TableRefused("UnreadWords", "scenario save takes a set's name, then desk or all: more is not read.", words[2].Word);
            if (words.Count == 0) throw new WorkspaceRefused("InvalidName", "scenario save takes the set's name, then desk or all where it is shared.");
            var typed = new JsonObject { ["set"] = words[0].Word };
            if (words.Count > 1) typed["share"] = words[1].Word.ToLowerInvariant();
            var saved = workspaces().PlanSave(line.State, line.Caller.UserId, Text(typed, "set"), PortfolioCommand.Optional(typed, "share"));
            return PortfolioCommand.Read(line, typed, $"Saves {SavedAs(saved)}");
        }

        /// <summary>After the set's name, whom it may be shared with.</summary>
        public LineCompletions Complete(LineContext line)
        {
            if (line.Text.Length == 0) return LineCompletions.None;
            var (done, typing, start) = LineWords.Typing(line.Text);
            return done.Count != 1 ? LineCompletions.None
                : new([.. Shares.Where(s => s.Word.StartsWith(typing, StringComparison.OrdinalIgnoreCase)).Select(s => new LineCompletion(s.Word, start, s.Detail))]);
        }

        public Task<IReadOnlyDictionary<string, JsonNode?>> InvokeAsync(CapabilityInvocation invocation, CancellationToken cancellationToken) =>
            PortfolioCommand.Answer(invocation, workspaces, (service, request, caller, revision) =>
            {
                var id = Text(request, "workspaceId");
                var saved = service.SaveScenarios(id, revision, caller.UserId, Text(request, "set"), PortfolioCommand.Optional(request, "share"));
                return (id, revision, null, Saved(saved));
            });
    }
}
