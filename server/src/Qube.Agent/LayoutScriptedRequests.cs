using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Qube.Agent;

/// <summary>
/// The stand-in's request for an arrangement of the panels (ADR-0018 §8; ADR-0007 §6): <c>put &lt;panel&gt; and
/// &lt;panel&gt; side by side</c>, or <c>put &lt;panel&gt; beside &lt;panel&gt;</c>. The panels are those the pane's
/// context names (<c>qube.panels</c>), found by their titles in the request; each stands in an area of its own, in a
/// row of equal shares. It proposes and moves nothing: the user applies the proposal from its card.
/// </summary>
public static partial class LayoutScriptedRequests
{
    public static IEnumerable<IScriptedRequest> All() => [new SideBySide()];

    /// <summary>The open panels the pane's context names, each by its id and its title as its tab says it.</summary>
    static IReadOnlyList<(string Id, string Title)> OpenPanels(ScriptedStep step)
    {
        var panels = step.Shown.FirstOrDefault(r => r["kind"] is JsonValue kind && kind.GetValueKind() == JsonValueKind.String && kind.GetValue<string>() == "qube.panels")?["panels"];
        if (panels is not JsonArray list) return [];
        return [.. list.OfType<JsonObject>().Select(p => (Id: p["panel"]?.GetValue<string>(), Title: p["title"]?.GetValue<string>()))
            .Where(p => p.Id is not null && p.Title is not null).Select(p => (p.Id!, p.Title!))];
    }

    /// <summary>
    /// The panels a request names by their titles, in the order it names them. A longer title is found before a shorter
    /// one it holds (<c>Rates 2026-10-27 · 2</c> before <c>Rates 2026-10-27</c>), and a title is found once.
    /// </summary>
    static IReadOnlyList<(string Id, string Title, int At)> Named(string request, IReadOnlyList<(string Id, string Title)> open)
    {
        var taken = new List<(int Start, int End)>();
        var named = new List<(string Id, string Title, int At)>();
        foreach (var (id, title) in open.OrderByDescending(p => p.Title.Length))
        {
            var words = title.ToLowerInvariant();
            for (var at = request.IndexOf(words, StringComparison.Ordinal); at >= 0; at = request.IndexOf(words, at + 1, StringComparison.Ordinal))
            {
                if (taken.Any(t => at < t.End && at + words.Length > t.Start)) continue;
                taken.Add((at, at + words.Length));
                named.Add((id, title, at));
                break;
            }
        }
        return [.. named.OrderBy(n => n.At)];
    }

    sealed class SideBySide : IScriptedRequest
    {
        public string CapabilityId => "layout.propose";
        public string Example => "put <panel> and <panel> side by side";
        public string Hint => "two open panels by their titles, as their tabs say them";
        // The panels' titles are its words, wherever they stand, and a title may hold a date or a product: no other
        // request is looked for in them (a Rates panel's date is not a new workspace's).
        public bool TakesTheRest => true;
        public int? FindIn(string request) => Pattern().Match(request) is { Success: true } m ? m.Index : null;

        public JsonObject Inputs(ScriptedStep step)
        {
            var named = Named(step.Request, OpenPanels(step));
            // "a beside b" keeps b where the eye starts, on the left, and puts a on its right.
            var beside = Pattern().Match(step.Request).Groups["beside"];
            var order = beside.Success
                ? [.. named.Where(n => n.At > beside.Index), .. named.Where(n => n.At < beside.Index)]
                : named.ToList();
            // A row of equal shares: each area is placed on the right of the one before, taking its share of what is
            // left. Two areas are always proposed, so a request that names fewer is refused for the area it left empty.
            var count = Math.Max(2, order.Count);
            var areas = new JsonArray();
            var places = new JsonArray();
            for (var i = 0; i < count; i++)
            {
                var area = new JsonObject { ["area"] = $"area {i + 1}" };
                if (i > 0)
                {
                    area["beside"] = $"area {i}";
                    area["side"] = "right";
                    area["share"] = (int)Math.Round(100.0 * (count - i) / (count - i + 1));
                }
                areas.Add(area);
                if (i < order.Count)
                    places.Add(new JsonObject { ["area"] = $"area {i + 1}", ["panel"] = order[i].Id, ["title"] = order[i].Title, ["shown"] = true });
            }
            return new() { ["request"] = new JsonObject { ["areas"] = areas, ["places"] = places } };
        }

        public string Done(JsonObject outputs) =>
            outputs["answer"]?["said"]?.GetValue<string>() ?? throw new InvalidOperationException("layout.propose answers what it proposed under answer.said.");
    }

    [GeneratedRegex(@"\bside\s+by\s+side\b|(?<beside>\bbeside\b|\bnext\s+to\b)", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
