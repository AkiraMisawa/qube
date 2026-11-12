using System.Text.Json.Nodes;
using Qube.Composition.Contracts;

namespace Qube.Composition.Application.Layout;

/// <summary>
/// The contracts of <c>layout.propose</c> (ADR-0018 §8): an arrangement of the browser's panels, said as flat lists
/// (ADR-0016 notes 12 and 37). The areas of the window are placed one beside another, each by its side and its share;
/// a floating area stands over the panels' space at one of five places; each place puts an open panel, a Run or an
/// application page in an area. The kernel knows no page: a name is checked where the proposal is applied.
/// </summary>
public static class LayoutContracts
{
    public const string Version = "1.0.0";
    public const string Request = "layout.propose-request";
    public const string Proposed = "layout.proposed";

    /// <summary>Where an area stands from the one it is placed beside.</summary>
    public static readonly IReadOnlyList<string> Sides = ["right", "left", "down", "up"];

    /// <summary>Where a floating area stands over the panels' space: a half of it, or its middle.</summary>
    public static readonly IReadOnlyList<string> FloatPlaces = ["centre", "left", "right", "top", "bottom"];

    /// <summary>The smallest an area may be (ADR-0018 §5), in CSS pixels: the narrowest phone a page is laid out for.</summary>
    public const int AreaWidth = 320;
    public const int AreaHeight = 240;

    static JsonObject Text(string title) => new() { ["type"] = "string", ["title"] = title };

    static JsonObject Name(string title) => new() { ["type"] = "string", ["minLength"] = 1, ["title"] = title };

    static JsonObject Choice(string title, IEnumerable<string> choices) =>
        new() { ["type"] = "string", ["title"] = title, ["enum"] = new JsonArray([.. choices.Select(c => (JsonNode)c)]) };

    static JsonObject Whole(string title) => new() { ["type"] = "integer", ["title"] = title };

    static JsonObject Record(JsonObject properties, params string[] required) => new()
    {
        ["type"] = "object",
        ["properties"] = properties,
        ["required"] = new JsonArray([.. required.Select(r => (JsonNode)r)]),
        ["additionalProperties"] = false,
    };

    static JsonObject List(string title, JsonObject items) => new() { ["type"] = "array", ["title"] = title, ["items"] = items, ["minItems"] = 1 };

    static JsonObject Areas() => List("The areas: those of the window first, each placed beside one listed before it, then the floating ones", Record(new JsonObject
    {
        ["area"] = Name("A name for the area, which beside and the places name it by"),
        ["beside"] = Text("The area of the window, listed before this one, that it is placed beside; left out for the first area of the window and for a floating area"),
        ["side"] = Choice("Where it stands from beside: right, left, down or up; given with beside", Sides),
        ["share"] = new JsonObject
        {
            ["type"] = "integer", ["minimum"] = 10, ["maximum"] = 90,
            ["title"] = "Its share, in percent, of the room it splits with beside; given with beside",
        },
        ["float"] = Choice("Floats the area over the panels' space at this place, a half of it or its middle; given without beside", FloatPlaces),
    }, "area"));

    static JsonObject Places() => List("What each area holds, in its tabs' order: an open panel, a Run or an application page", Record(new JsonObject
    {
        ["area"] = Name("The area it is in, by the proposal's name for it"),
        ["panel"] = Text("An open panel, by its id as the context gives it"),
        ["title"] = Text("That panel's title as the context gives it; given with panel"),
        ["run"] = Text("A Run not open, by its id, opened here"),
        ["app"] = Text("An application page not open, by its name as the context lists it, opened here"),
        ["workspace"] = Text("The workspace that application page shows, by its id; given with app where the page takes one"),
        ["shown"] = new JsonObject { ["type"] = "boolean", ["title"] = "The one its area shows: one place in each area" },
    }, "area"));

    internal static void Register(CompositionRegistryBuilder builder)
    {
        builder.AddContract(new ContractDefinition(Request, Version, "An arrangement of the panels, to propose", Record(new JsonObject
        {
            ["areas"] = Areas(),
            ["places"] = Places(),
        }, "areas", "places")));
        builder.AddContract(new ContractDefinition(Proposed, Version, "An arrangement of the panels, proposed and not applied", Record(new JsonObject
        {
            ["areas"] = Areas(),
            ["places"] = Places(),
            ["count"] = Whole("How many areas it makes"),
            ["width"] = Whole("The least width of the panels' space it fits, in CSS pixels"),
            ["height"] = Whole("The least height of the panels' space it fits, in CSS pixels"),
            ["said"] = Text("What was proposed, in words"),
        }, "areas", "places", "count", "width", "height", "said")));
    }
}

/// <summary>
/// <c>layout.propose</c> (ADR-0018 §8, CONFIRMED by the owner: the AI proposes, the user applies): checks the shape of an
/// arrangement and answers it with how many areas it makes and the least room it fits, and changes nothing. No panel
/// moves, nothing is written, and no workspace or Run is made. Whether the panels it names are open, and still so
/// titled, is checked where it is applied, in the browser that has them.
/// </summary>
public sealed class LayoutPropose : ICapabilityHandler
{
    public const string Id = "layout.propose";

    const string At = "/inputs/request";

    public CapabilityDescriptor Descriptor { get; } = new(Id, LayoutContracts.Version,
        "Propose an arrangement of the user's panels, in areas side by side or floating; the user applies it, and nothing moves until then",
        [new("request", LayoutContracts.Request, LayoutContracts.Version, Cardinality.One)],
        [new("answer", LayoutContracts.Proposed, LayoutContracts.Version, Cardinality.One)],
        ExecutionKind.RequestResponse, [CapabilityEffect.Propose]);

    public Task<IReadOnlyDictionary<string, JsonNode?>> InvokeAsync(CapabilityInvocation invocation, CancellationToken cancellationToken)
    {
        var request = invocation.Inputs["request"] as JsonObject ?? throw new CapabilityFailure("InvalidInput", "The command takes a request.");
        var areas = Records(request, "areas").Select((a, i) => new ProposedArea(
            Field(a, "area") ?? throw new CapabilityFailure("InvalidInput", $"Area {i} has no name."),
            Field(a, "beside"), Field(a, "side"), a["share"]?.GetValue<int>(), Field(a, "float"), i)).ToList();
        var places = Records(request, "places").Select((p, i) => new ProposedPlace(
            Field(p, "area") ?? throw new CapabilityFailure("InvalidInput", $"Place {i} names no area."),
            Field(p, "panel"), Field(p, "title"), Field(p, "run"), Field(p, "app"), Field(p, "workspace"), p["shown"]?.GetValue<bool>() == true, i)).ToList();

        var issues = Problems(areas, places).ToList();
        if (issues.Count > 0)
            throw new CapabilityFailure("InvalidProposal", $"The arrangement cannot be proposed: {string.Join("; ", issues.Select(i => $"{i.Path}: {i.Message}"))}", issues);

        var (width, height) = Needs(areas);
        var windowed = areas.Count(a => a.Float is null);
        var floating = areas.Count - windowed;
        var said = $"Proposed {Counted(areas.Count, "area")}{(floating == 0 ? "" : $", {floating} of them floating")}, for a panels' space of at least {width} by {height}px. "
            + "Nothing has moved: apply it from its card, or with arrange on the command line.";
        return Task.FromResult<IReadOnlyDictionary<string, JsonNode?>>(new Dictionary<string, JsonNode?>
        {
            ["answer"] = new JsonObject
            {
                ["areas"] = request["areas"]!.DeepClone(),
                ["places"] = request["places"]!.DeepClone(),
                ["count"] = areas.Count,
                ["width"] = width,
                ["height"] = height,
                ["said"] = said,
            },
        });
    }

    static IEnumerable<JsonObject> Records(JsonObject request, string name) =>
        request[name] is JsonArray list ? list.Select(r => r as JsonObject ?? throw new CapabilityFailure("InvalidInput", $"An item of {name} is not a record."))
            : throw new CapabilityFailure("InvalidInput", $"The request has no {name}.");

    static string? Field(JsonObject record, string name) => record[name]?.GetValue<string>();

    static string Counted(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    /// <summary>Every reason the arrangement cannot be proposed, by the path of what is at fault.</summary>
    static IEnumerable<CompositionIssue> Problems(IReadOnlyList<ProposedArea> areas, IReadOnlyList<ProposedPlace> places)
    {
        var named = new Dictionary<string, ProposedArea>(StringComparer.Ordinal);
        var windowed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var area in areas)
        {
            var at = $"{At}/areas/{area.Index}";
            if (!named.TryAdd(area.Name, area))
            {
                yield return new("AreaNamedTwice", $"{at}/area", $"Area {area.Name} is named twice.");
                continue;
            }
            if (area.Float is not null)
            {
                if (area.Beside is not null || area.Side is not null || area.Share is not null)
                    yield return new("FloatsBeside", $"{at}/float", $"Area {area.Name} floats over the panels' space, beside no area: give it float, or beside with side and share, not both.");
                continue;
            }
            if (windowed.Count == 0)
            {
                if (area.Beside is not null || area.Side is not null || area.Share is not null)
                    yield return new("FirstAreaBeside", $"{at}/beside", $"Area {area.Name} is the first of the window, which the others are placed beside: it takes no beside, side or share.");
                windowed.Add(area.Name);
                continue;
            }
            if (area.Beside is null)
                yield return new("Required", $"{at}/beside", $"Area {area.Name} names the area of the window it is placed beside: every area of the window but the first is placed beside one listed before it.");
            else if (!windowed.Contains(area.Beside))
                yield return new("UnknownArea", $"{at}/beside", named.TryGetValue(area.Beside, out var other) && other.Float is not null
                    ? $"Area {area.Beside} floats: an area of the window is placed beside another of the window."
                    : $"Area {area.Name} is placed beside {area.Beside}, which is not an area of the window listed before it.");
            if (area.Side is null) yield return new("Required", $"{at}/side", $"Area {area.Name} says on which side of {area.Beside ?? "its area"} it stands.");
            if (area.Share is null) yield return new("Required", $"{at}/share", $"Area {area.Name} says its share, in percent, of the room it splits with {area.Beside ?? "its area"}.");
            windowed.Add(area.Name);
        }
        if (windowed.Count == 0)
            yield return new("NoWindowArea", $"{At}/areas", "A proposal has at least one area of the window: a floating area stands over it.");

        var held = new Dictionary<string, List<ProposedPlace>>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var place in places)
        {
            var at = $"{At}/places/{place.Index}";
            if (!named.ContainsKey(place.Area))
                yield return new("UnknownArea", $"{at}/area", $"Place {place.Index} is in area {place.Area}, which the proposal does not list.");
            else
                (held.TryGetValue(place.Area, out var list) ? list : held[place.Area] = []).Add(place);
            var kinds = new[] { place.Panel, place.Run, place.App }.Count(k => k is not null);
            if (kinds != 1)
            {
                yield return new("PlaceHoldsOne", at, $"Place {place.Index} holds one of an open panel, a Run or an application page: give one of panel, run and app.");
                continue;
            }
            if (place.Panel is not null && place.Title is null)
                yield return new("Required", $"{at}/title", $"Panel {place.Panel} is named with its title, as the context gives it, so that a panel retitled since is not moved for it.");
            if (place.Panel is null && place.Title is not null)
                yield return new("TitleWithoutPanel", $"{at}/title", "title is an open panel's, given with panel.");
            if (place.App is null && place.Workspace is not null)
                yield return new("WorkspaceWithoutApp", $"{at}/workspace", "workspace is the workspace an application page shows, given with app.");
            var key = place.Panel is not null ? $"panel {place.Panel}" : place.Run is not null ? $"Run {place.Run}" : $"{place.App}{(place.Workspace is null ? "" : $" of workspace {place.Workspace}")}";
            if (!seen.Add(key))
                yield return new("PlacedTwice", at, $"{char.ToUpperInvariant(key[0])}{key[1..]} is placed twice: a page stands in one place.");
        }
        foreach (var area in named.Values)
        {
            var at = $"{At}/areas/{area.Index}";
            if (!held.TryGetValue(area.Name, out var holds))
            {
                yield return new("EmptyArea", at, $"Area {area.Name} holds nothing: place a panel in it, or leave it out.");
                continue;
            }
            var shown = holds.Where(p => p.Shown).ToList();
            if (shown.Count == 0)
                yield return new("NoneShown", at, $"Area {area.Name} shows one of its panels: mark one place in it shown.");
            foreach (var also in shown.Skip(1))
                yield return new("ShownTwice", $"{At}/places/{also.Index}/shown", $"Area {area.Name} shows one panel, and place {shown[0].Index} is already marked shown in it.");
        }
    }

    /// <summary>
    /// The least room the arrangement fits (ADR-0018 §5): an area is 320 by 240px, so a row gives each part its share
    /// and is as wide as the part that needs most for its share; a floating area needs an area's own size.
    /// </summary>
    static (int Width, int Height) Needs(IReadOnlyList<ProposedArea> areas)
    {
        Part? root = null;
        var leaves = new Dictionary<string, Part>(StringComparer.Ordinal);
        foreach (var area in areas.Where(a => a.Float is null))
        {
            var leaf = new Part(area.Name);
            leaves[area.Name] = leaf;
            if (root is null)
            {
                root = leaf;
                continue;
            }
            var beside = leaves[area.Beside!];
            var row = area.Side is "right" or "left";
            var before = area.Side is "left" or "up";
            var share = area.Share!.Value / 100.0;
            if (beside.Parent is { } parent && parent.Row == row)
            {
                var at = parent.Parts.FindIndex(p => p.Part == beside);
                var whole = parent.Parts[at].Share;
                parent.Parts[at] = (beside, whole * (1 - share));
                parent.Parts.Insert(before ? at : at + 1, (leaf, whole * share));
                leaf.Parent = parent;
                continue;
            }
            var split = new Part(null) { Row = row, Parent = beside.Parent };
            split.Parts.AddRange(before ? [(leaf, share), (beside, 1 - share)] : [(beside, 1 - share), (leaf, share)]);
            if (beside.Parent is { } holder)
            {
                var at = holder.Parts.FindIndex(p => p.Part == beside);
                holder.Parts[at] = (split, holder.Parts[at].Share);
            }
            else
            {
                root = split;
            }
            beside.Parent = split;
            leaf.Parent = split;
        }
        var grid = root is null ? (0.0, 0.0) : NeedsOf(root);
        var floats = areas.Any(a => a.Float is not null);
        return ((int)Math.Max(Math.Ceiling(grid.Item1 - 1e-9), floats ? LayoutContracts.AreaWidth : 0),
            (int)Math.Max(Math.Ceiling(grid.Item2 - 1e-9), floats ? LayoutContracts.AreaHeight : 0));
    }

    static (double Width, double Height) NeedsOf(Part part)
    {
        if (part.Area is not null) return (LayoutContracts.AreaWidth, LayoutContracts.AreaHeight);
        var parts = part.Parts.Select(p => (Need: NeedsOf(p.Part), p.Share)).ToList();
        return part.Row
            ? (parts.Max(p => p.Need.Width / p.Share), parts.Max(p => p.Need.Height))
            : (parts.Max(p => p.Need.Width), parts.Max(p => p.Need.Height / p.Share));
    }

    /// <summary>A part of the window: an area, or a row or column of parts, each with its share of it.</summary>
    sealed class Part(string? area)
    {
        public string? Area { get; } = area;
        public bool Row { get; init; }
        public Part? Parent { get; set; }
        public List<(Part Part, double Share)> Parts { get; } = [];
    }
}

sealed record ProposedArea(string Name, string? Beside, string? Side, int? Share, string? Float, int Index);

sealed record ProposedPlace(string Area, string? Panel, string? Title, string? Run, string? App, string? Workspace, bool Shown, int Index);

/// <summary>The kernel's proposal of an arrangement (ADR-0018 §8), with its contracts. Knows no page and no domain.</summary>
public sealed class LayoutModule : IQubeModule
{
    public void Register(CompositionRegistryBuilder builder)
    {
        builder.AddCapability(new LayoutPropose());
        LayoutContracts.Register(builder);
    }
}
