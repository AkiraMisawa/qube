using System.Text.Json.Nodes;
using Qube.Agent;
using Qube.Composition.Application;
using Qube.Composition.Application.Layout;
using Qube.Composition.Contracts;

namespace Qube.Composition.Tests;

// ADR-0018 §8: the AI proposes an arrangement of the panels through `layout.propose`, which checks its shape, answers it
// with how many areas it makes and the least room it fits, and changes nothing; the user applies it in the browser.
public class LayoutProposeTests
{
    readonly CompositionRegistry registry = new([new LayoutModule()]);

    CommandService Commands => new(registry, new InMemoryDataStore());

    Task<CommandResult> Propose(JsonObject request) =>
        Commands.InvokeAsync(LayoutPropose.Id, LayoutContracts.Version, new JsonObject { ["request"] = request }, new Caller("ana", RunOrigin.Ai), Guid.NewGuid().ToString(), default);

    async Task<JsonObject> Proposed(JsonObject request)
    {
        var result = await Propose(request);
        return result is CommandResult.Done done ? done.Outputs["answer"]!.AsObject()
            : throw new Xunit.Sdk.XunitException($"Refused: {string.Join(" ", ((CommandResult.Refused)result).Issues.Select(i => $"{i.Path} {i.Message}"))}");
    }

    async Task<(string Code, IReadOnlyList<CompositionIssue> Issues)> Refused(JsonObject request)
    {
        var refused = Assert.IsType<CommandResult.Refused>(await Propose(request));
        return (refused.Code, refused.Issues);
    }

    static JsonObject Area(string name, string? beside = null, string? side = null, int? share = null, string? floats = null)
    {
        var area = new JsonObject { ["area"] = name };
        if (beside is not null) area["beside"] = beside;
        if (side is not null) area["side"] = side;
        if (share is not null) area["share"] = share;
        if (floats is not null) area["float"] = floats;
        return area;
    }

    static JsonObject Panel(string area, string id, string title, bool shown = true)
    {
        var place = new JsonObject { ["area"] = area, ["panel"] = id, ["title"] = title };
        if (shown) place["shown"] = true;
        return place;
    }

    static JsonObject Request(JsonArray areas, JsonArray places) => new() { ["areas"] = areas, ["places"] = places };

    // Two panels side by side, the one on the left holding a second tab, and a third floating in the middle.
    static JsonObject WellFormed() => Request(
        [Area("left"), Area("right", "left", "right", 50), Area("over", floats: "centre")],
        [Panel("left", "panel-1", "Pricing DB"), Panel("left", "panel-4", "Rates 2026-10-27", shown: false), Panel("right", "panel-2", "XVA ABC/LE-01"),
            new JsonObject { ["area"] = "over", ["run"] = "run-7", ["shown"] = true }]);

    [Fact] // ADR-0018 §8: a well-formed proposal is answered as it was given, with how many areas it makes and the least room it fits
    public async Task A_well_formed_proposal_is_answered_with_its_areas_and_the_room_it_needs()
    {
        var request = WellFormed();

        var answer = await Proposed(request);

        Assert.True(JsonNode.DeepEquals(request["areas"], answer["areas"]));
        Assert.True(JsonNode.DeepEquals(request["places"], answer["places"]));
        Assert.Equal((3, 640, 240), (answer["count"]!.GetValue<int>(), answer["width"]!.GetValue<int>(), answer["height"]!.GetValue<int>()));
        Assert.StartsWith("Proposed 3 areas, 1 of them floating, for a panels' space of at least 640 by 240px.", answer["said"]!.GetValue<string>());
        var port = registry.FindCapability(LayoutPropose.Id, LayoutContracts.Version)!.Descriptor.OutputPorts.Single();
        Assert.Empty(ContractChecks.Check(registry.FindContract(port.ContractId, port.ContractVersion)!, answer, "/answer"));
    }

    [Fact] // ADR-0018 §5, §8: an area is 320 by 240px, so a row is as wide as its narrowest share needs, and a column as high
    public async Task The_room_a_proposal_needs_follows_each_areas_share()
    {
        var quarter = await Proposed(Request([Area("a"), Area("b", "a", "right", 25)], [Panel("a", "panel-1", "A"), Panel("b", "panel-2", "B")]));
        // A row of a third each, the last placed above the middle one: the row is 960 wide, and the column 480 high.
        var nested = await Proposed(Request(
            [Area("a"), Area("b", "a", "right", 67), Area("c", "b", "right", 50), Area("d", "b", "up", 50)],
            [Panel("a", "panel-1", "A"), Panel("b", "panel-2", "B"), Panel("c", "panel-3", "C"), Panel("d", "panel-4", "D")]));
        var floating = await Proposed(Request([Area("a"), Area("f", floats: "right")], [Panel("a", "panel-1", "A"), Panel("f", "panel-2", "B")]));

        Assert.Equal((1280, 240), (quarter["width"]!.GetValue<int>(), quarter["height"]!.GetValue<int>()));
        Assert.Equal((4, 970, 480), (nested["count"]!.GetValue<int>(), nested["width"]!.GetValue<int>(), nested["height"]!.GetValue<int>()));
        Assert.Equal((320, 240), (floating["width"]!.GetValue<int>(), floating["height"]!.GetValue<int>()));
    }

    public static TheoryData<string, JsonObject, string, string> Malformed() => new()
    {
        { "an area named twice", Request([Area("a"), Area("a", "a", "right", 50)], [Panel("a", "panel-1", "A")]), "AreaNamedTwice", "/inputs/request/areas/1/area" },
        { "the first area placed beside another", Request([Area("a", "b", "right", 50)], [Panel("a", "panel-1", "A")]), "FirstAreaBeside", "/inputs/request/areas/0/beside" },
        { "a second area beside none", Request([Area("a"), Area("b", side: "right", share: 50)], [Panel("a", "panel-1", "A"), Panel("b", "panel-2", "B")]), "Required", "/inputs/request/areas/1/beside" },
        { "an area beside one listed after it", Request([Area("a"), Area("b", "c", "right", 50), Area("c", "a", "down", 50)], [Panel("a", "panel-1", "A"), Panel("b", "panel-2", "B"), Panel("c", "panel-3", "C")]), "UnknownArea", "/inputs/request/areas/1/beside" },
        { "an area beside a floating one", Request([Area("a"), Area("f", floats: "left"), Area("b", "f", "right", 50)], [Panel("a", "panel-1", "A"), Panel("f", "panel-2", "B"), Panel("b", "panel-3", "C")]), "UnknownArea", "/inputs/request/areas/2/beside" },
        { "an area with no side", Request([Area("a"), Area("b", "a", share: 50)], [Panel("a", "panel-1", "A"), Panel("b", "panel-2", "B")]), "Required", "/inputs/request/areas/1/side" },
        { "an area with no share", Request([Area("a"), Area("b", "a", "down")], [Panel("a", "panel-1", "A"), Panel("b", "panel-2", "B")]), "Required", "/inputs/request/areas/1/share" },
        { "a floating area placed beside another", Request([Area("a"), Area("f", "a", "right", 50, "centre")], [Panel("a", "panel-1", "A"), Panel("f", "panel-2", "B")]), "FloatsBeside", "/inputs/request/areas/1/float" },
        { "floating areas alone", Request([Area("f", floats: "centre")], [Panel("f", "panel-1", "A")]), "NoWindowArea", "/inputs/request/areas" },
        { "a place in no listed area", Request([Area("a")], [Panel("a", "panel-1", "A"), Panel("z", "panel-2", "B")]), "UnknownArea", "/inputs/request/places/1/area" },
        { "a place holding two pages", Request([Area("a")], [new JsonObject { ["area"] = "a", ["panel"] = "panel-1", ["title"] = "A", ["run"] = "run-1", ["shown"] = true }]), "PlaceHoldsOne", "/inputs/request/places/0" },
        { "a place holding nothing", Request([Area("a")], [new JsonObject { ["area"] = "a", ["shown"] = true }]), "PlaceHoldsOne", "/inputs/request/places/0" },
        { "an open panel without its title", Request([Area("a")], [new JsonObject { ["area"] = "a", ["panel"] = "panel-1", ["shown"] = true }]), "Required", "/inputs/request/places/0/title" },
        { "a title with no panel", Request([Area("a")], [new JsonObject { ["area"] = "a", ["run"] = "run-1", ["title"] = "A", ["shown"] = true }]), "TitleWithoutPanel", "/inputs/request/places/0/title" },
        { "a workspace with no application page", Request([Area("a")], [new JsonObject { ["area"] = "a", ["run"] = "run-1", ["workspace"] = "ws-1", ["shown"] = true }]), "WorkspaceWithoutApp", "/inputs/request/places/0/workspace" },
        { "a panel placed twice", Request([Area("a"), Area("b", "a", "right", 50)], [Panel("a", "panel-1", "A"), Panel("b", "panel-1", "A")]), "PlacedTwice", "/inputs/request/places/1" },
        { "a Run placed twice", Request([Area("a")], [new JsonObject { ["area"] = "a", ["run"] = "run-1", ["shown"] = true }, new JsonObject { ["area"] = "a", ["run"] = "run-1" }]), "PlacedTwice", "/inputs/request/places/1" },
        { "an area holding nothing", Request([Area("a"), Area("b", "a", "right", 50)], [Panel("a", "panel-1", "A")]), "EmptyArea", "/inputs/request/areas/1" },
        { "an area showing none of its panels", Request([Area("a")], [Panel("a", "panel-1", "A", shown: false)]), "NoneShown", "/inputs/request/areas/0" },
        { "an area showing two panels", Request([Area("a")], [Panel("a", "panel-1", "A"), Panel("a", "panel-2", "B")]), "ShownTwice", "/inputs/request/places/1/shown" },
    };

    [Theory] // ADR-0018 §8: each malformed proposal is refused by the path of what is at fault, and nothing is answered for it
    [MemberData(nameof(Malformed))]
    public async Task A_malformed_proposal_is_refused_by_the_path_at_fault(string what, JsonObject request, string code, string path)
    {
        var (refused, issues) = await Refused(request);

        Assert.Equal("InvalidProposal", refused);
        var issue = Assert.Single(issues);
        Assert.True((code, path) == (issue.Code, issue.Path), $"{what}: {issue.Code} at {issue.Path}: {issue.Message}");
    }

    [Fact] // ADR-0018 §5, §8: a share is a whole percent from 10 to 90, and a side and a place are of their few; the contract refuses others by path
    public async Task A_share_side_or_place_outside_the_contract_is_refused_by_it()
    {
        var share = await Refused(Request([Area("a"), Area("b", "a", "right", 5)], [Panel("a", "panel-1", "A"), Panel("b", "panel-2", "B")]));
        var side = await Refused(Request([Area("a"), Area("b", "a", "below", 50)], [Panel("a", "panel-1", "A"), Panel("b", "panel-2", "B")]));
        var place = await Refused(Request([Area("a"), Area("f", floats: "middle")], [Panel("a", "panel-1", "A"), Panel("f", "panel-2", "B")]));

        Assert.Equal(("ValidationFailed", "/inputs/request/areas/1/share"), (share.Code, Assert.Single(share.Issues).Path));
        Assert.Equal(("ValidationFailed", "/inputs/request/areas/1/side"), (side.Code, Assert.Single(side.Issues).Path));
        Assert.Equal(("ValidationFailed", "/inputs/request/areas/1/float"), (place.Code, Assert.Single(place.Issues).Path));
    }

    [Fact] // ADR-0018 §8, ADR-0007 §3: the proposal is a command offered to the agent, which writes nothing, so it is never a workflow step
    public void The_proposal_is_offered_to_the_agent_and_writes_nothing()
    {
        var descriptor = registry.FindCapability(LayoutPropose.Id, LayoutContracts.Version)!.Descriptor;
        var tools = new AgentToolGateway(registry, Commands);

        Assert.Equal([CapabilityEffect.Propose], descriptor.Effects);
        Assert.True(descriptor.IsCommand());
        Assert.Contains(tools.Tools, t => t.Name == "layout_propose");
    }
}
