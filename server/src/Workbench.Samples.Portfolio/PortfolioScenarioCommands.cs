using System.Text.Json.Nodes;
using Workbench.Composition.Application;
using Workbench.Composition.Application.Saved;
using Workbench.Composition.Application.Workspaces;
using Workbench.Composition.Contracts;

namespace Workbench.Samples.Portfolio;

/// <summary>
/// The scenario commands as command capabilities (ADR-0005, ADR-0010 §4): define, include, exclude, remove, duplicate,
/// clear, ladder, cross, save and load, for the AI through the command gateway. Each takes what the command line takes, as written,
/// goes through the same workspace service, and says back what the command line says; what it does is in the
/// workspace's log with the caller's origin. A cross asked of the AI is one call, as it is one command.
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

    /// <summary>What <c>scenario &lt;name&gt; …</c> says back, in the command line's words.</summary>
    public static string Defined(ScenarioAnswer answer)
    {
        var kept = answer.Made.Where(m => !answer.Replaced.Any(r => PortfolioScenarios.Same(r, m))).ToList();
        var said = string.Join("; ", new[]
        {
            kept.Count == 0 ? null : $"made the scenario{(kept.Count == 1 ? "" : "s")} {string.Join(", ", kept)}",
            answer.Replaced.Count == 0 ? null : $"replaced the shifts of {string.Join(", ", answer.Replaced)}",
        }.OfType<string>());
        return $"{char.ToUpperInvariant(said[0])}{said[1..]}.";
    }

    /// <summary>What a generator, or a load, says back: how many it made, and those of the same name it replaced.</summary>
    public static string Generated(ScenarioAnswer answer) =>
        $"Made {answer.Made.Count} scenario(s){(answer.Replaced.Count == 0 ? "" : $", replacing {answer.Replaced.Count} of the same name")}: {string.Join(", ", answer.Made)}.";

    public static string Loaded(string set, ScenarioAnswer answer) =>
        $"Loaded {answer.Made.Count} scenario(s) from {set}{(answer.Replaced.Count == 0 ? "" : $", replacing {string.Join(", ", answer.Replaced)}")}: {string.Join(", ", answer.Made)}.";

    public static string Included(string name, bool included) =>
        included ? $"Price prices the scenario {name}." : $"Price leaves the scenario {name} out; its shifts stay.";

    public static string Saved(SetSaved saved)
    {
        var who = saved.Visibility switch
        {
            SavedVisibility.All => "every user",
            SavedVisibility.Desk => $"the desk {saved.Desk ?? throw new InvalidOperationException($"The set {saved.Set} is shared with a desk it does not name.")}",
            SavedVisibility.Private => "you alone",
            _ => throw new InvalidOperationException($"No visibility {saved.Visibility}."),
        };
        return $"Saved {saved.Scenarios} scenario(s) as the set {saved.Set}, for {who}{(saved.Replaced ? $", replacing your {saved.Set}" : "")}.";
    }

    public static void Register(CompositionRegistryBuilder builder, Func<PortfolioWorkspaces> workspaces)
    {
        string Text(JsonNode request, string name) => PortfolioCommand.Text(request, name).Trim();
        void Add(string id, string title, string request, string answer, CapabilityEffect effect,
            Func<PortfolioWorkspaces, JsonNode, Caller, long, (string WorkspaceId, long Revision, int? Made, string Said)> act) =>
            builder.AddCapability(new Command(new(id, PortfolioContracts.Version, title,
                [new("request", request, PortfolioContracts.Version, Cardinality.One)],
                [new("answer", answer, PortfolioContracts.Version, Cardinality.One)],
                ExecutionKind.RequestResponse, [effect]), workspaces, act));
        (string, long, int?, string) Made(ScenarioAnswer answer, string said) => (answer.State.WorkspaceId, answer.State.Revision, answer.Made.Count, said);
        (string, long, int?, string) Edited(WorkspaceState state, string said) => (state.WorkspaceId, state.Revision, null, said);

        Add("xva.scenario", "Define a scenario of an XVA workspace with all its shifts, replacing one of its name", DefineRequest, ScenariosMade, CapabilityEffect.EditWorkspace,
            (service, request, caller, revision) =>
            {
                var answer = service.DefineScenario(Text(request, "workspaceId"), revision, caller.UserId, Text(request, "name"), Text(request, "shifts"), caller.Origin);
                return Made(answer, Defined(answer));
            });
        foreach (var (verb, included, title) in new[] { ("include", true, "Price a scenario of an XVA workspace beside the base market"), ("exclude", false, "Leave a scenario of an XVA workspace out of Price; its shifts stay") })
            Add($"xva.scenario.{verb}", title, NameRequest, PortfolioCommandContracts.Edited, CapabilityEffect.EditWorkspace, (service, request, caller, revision) =>
            {
                var name = Text(request, "name");
                return Edited(service.IncludeScenario(Text(request, "workspaceId"), revision, caller.UserId, name, included, caller.Origin), Included(name, included));
            });
        Add("xva.scenario.remove", "Remove a scenario of an XVA workspace with all its shifts", NameRequest, PortfolioCommandContracts.Edited, CapabilityEffect.EditWorkspace,
            (service, request, caller, revision) =>
            {
                var name = Text(request, "name");
                return Edited(service.RemoveScenario(Text(request, "workspaceId"), revision, caller.UserId, name, caller.Origin), $"Removed the scenario {name}.");
            });
        Add("xva.scenario.duplicate", "Copy a scenario of an XVA workspace under a new name, to be edited", DuplicateRequest, ScenariosMade, CapabilityEffect.EditWorkspace,
            (service, request, caller, revision) =>
            {
                var (name, copy) = (Text(request, "name"), Text(request, "copy"));
                var answer = service.DuplicateScenario(Text(request, "workspaceId"), revision, caller.UserId, name, copy, caller.Origin);
                return Made(answer, $"Duplicated {name} as {string.Join(", ", answer.Made)}.");
            });
        Add("xva.scenarios.clear", "Remove every scenario of an XVA workspace; Price then prices the base market alone", ClearRequest, PortfolioCommandContracts.Edited,
            CapabilityEffect.EditWorkspace, (service, request, caller, revision) =>
                Edited(service.ClearScenarios(Text(request, "workspaceId"), revision, caller.UserId, caller.Origin), "Removed every scenario; Price prices the base market alone."));
        foreach (var (generator, title) in new[]
        {
            ("ladder", "Make a scenario per size of one factor of an XVA workspace, such as jpy_ois -50bp..+50bp step 10bp"),
            ("cross", "Make a scenario per combination of several factors' sizes of an XVA workspace, such as jpy_ois -50bp, 0, +50bp x usdjpy -10%, 0, +10%"),
        })
            Add($"xva.scenario.{generator}", title, GeneratorRequest, ScenariosMade, CapabilityEffect.EditWorkspace, (service, request, caller, revision) =>
            {
                var answer = service.GenerateScenarios(Text(request, "workspaceId"), revision, caller.UserId, generator, Text(request, "text"), caller.Origin);
                return Made(answer, Generated(answer));
            });
        Add("xva.scenario.save", "Save every scenario of an XVA workspace as a set, for the user alone unless shared with their desk or all", SaveRequest,
            PortfolioCommandContracts.Edited, CapabilityEffect.SaveItem, (service, request, caller, revision) =>
            {
                var id = Text(request, "workspaceId");
                var saved = service.SaveScenarios(id, revision, caller.UserId, Text(request, "set"), PortfolioCommand.Optional(request, "share"));
                return (id, revision, null, Saved(saved));
            });
        Add("xva.scenario.load", "Add a saved set's scenarios to an XVA workspace, replacing those of the same name", LoadRequest, ScenariosMade, CapabilityEffect.EditWorkspace,
            (service, request, caller, revision) =>
            {
                var set = Text(request, "set");
                var answer = service.LoadScenarios(Text(request, "workspaceId"), revision, caller.UserId, set, caller.Origin);
                return Made(answer, Loaded(set, answer));
            });

        JsonObject Workspace() => new() { ["type"] = "string", ["minLength"] = 1, ["title"] = "Workspace", ["description"] = "The XVA workspace's id, as the context names it." };
        JsonObject Revision() => new()
        {
            ["type"] = "integer", ["title"] = "Revision",
            ["description"] = "The revision the request was made against; refused if the workspace has moved since. Left out, the one it holds.",
        };
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
            ["workspaceId"] = Workspace(),
            ["revision"] = Revision(),
            ["name"] = Field("Name", "The scenario's name, such as s1 or rates-up. One of that name is replaced whole."),
            ["shifts"] = Field("Shifts", "Every shift of the scenario as written, separated by semicolons: jpy_ois +10bp; usdjpy -5%; jpy_ois 5y +3bp. "
                + $"An index alone shifts the whole curve, a tenor narrows it to that point; bp is absolute, % relative. The indices are {ScenarioCatalog.Listed}."),
        }, "workspaceId", "name", "shifts");
        Contract(NameRequest, "A scenario by name", new JsonObject
        {
            ["workspaceId"] = Workspace(),
            ["revision"] = Revision(),
            ["name"] = Field("Scenario", "The scenario's name, as the workspace lists it."),
        }, "workspaceId", "name");
        Contract(DuplicateRequest, "Duplicate a scenario", new JsonObject
        {
            ["workspaceId"] = Workspace(),
            ["revision"] = Revision(),
            ["name"] = Field("Scenario", "The scenario to copy, as the workspace lists it."),
            ["copy"] = Field("New name", "The copy's name, which no scenario of the workspace has."),
        }, "workspaceId", "name", "copy");
        Contract(ClearRequest, "Remove every scenario", new JsonObject
        {
            ["workspaceId"] = Workspace(),
            ["revision"] = Revision(),
        }, "workspaceId");
        Contract(GeneratorRequest, "Generate scenarios", new JsonObject
        {
            ["workspaceId"] = Workspace(),
            ["revision"] = Revision(),
            ["text"] = Field("Generator", "As the command line takes it after scenario ladder or scenario cross. Ladder: a factor, a range and a step, "
                + "jpy_ois -50bp..+50bp step 10bp. Cross: lists separated by x, each a factor and its sizes, jpy_ois -50bp, 0, +50bp x usdjpy -10%, 0, +10%."),
        }, "workspaceId", "text");
        Contract(SaveRequest, "Save the scenarios as a set", new JsonObject
        {
            ["workspaceId"] = Workspace(),
            ["revision"] = Revision(),
            ["set"] = Field("Set", "The set's name, one word without @, such as rates-up. The user's own set of that name is replaced."),
            ["share"] = new JsonObject
            {
                ["type"] = "string", ["enum"] = new JsonArray("desk", "all"), ["title"] = "Shared with",
                ["description"] = "desk: the user's desk sees it; all: every user does. Left out, the user alone.",
            },
        }, "workspaceId", "set");
        Contract(LoadRequest, "Load a saved set", new JsonObject
        {
            ["workspaceId"] = Workspace(),
            ["revision"] = Revision(),
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

    sealed class Command(CapabilityDescriptor descriptor, Func<PortfolioWorkspaces> workspaces,
        Func<PortfolioWorkspaces, JsonNode, Caller, long, (string WorkspaceId, long Revision, int? Made, string Said)> act) : ICapabilityHandler
    {
        public CapabilityDescriptor Descriptor { get; } = descriptor;

        public Task<IReadOnlyDictionary<string, JsonNode?>> InvokeAsync(CapabilityInvocation invocation, CancellationToken cancellationToken) =>
            PortfolioCommand.Answer(invocation, workspaces, act);
    }
}
