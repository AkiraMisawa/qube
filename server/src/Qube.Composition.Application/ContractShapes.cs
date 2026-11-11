using System.Text.Json.Nodes;

namespace Qube.Composition.Application;

/// <summary>
/// What a contract's schema is written from (ADR-0016 notes 12 and 37): a value, a list of values, a flat record and a
/// list of flat records, each with the title a caller reads. Nothing deeper is a contract, so nothing deeper is here.
/// </summary>
static class ContractShapes
{
    public static JsonObject Text(string title) => new() { ["type"] = "string", ["title"] = title };

    /// <summary>A text that is given: an empty one is refused by the contract.</summary>
    public static JsonObject Given(string title) => new() { ["type"] = "string", ["minLength"] = 1, ["title"] = title };

    public static JsonObject Whole(string title) => new() { ["type"] = "integer", ["title"] = title };

    public static JsonObject Flag(string title) => new() { ["type"] = "boolean", ["title"] = title };

    public static JsonObject Texts(string title) => new() { ["type"] = "array", ["title"] = title, ["items"] = new JsonObject { ["type"] = "string" } };

    public static JsonObject Record(JsonObject properties, params string[] required) => new()
    {
        ["type"] = "object",
        ["properties"] = properties,
        ["required"] = new JsonArray([.. required.Select(r => (JsonNode)r)]),
        ["additionalProperties"] = false,
    };

    public static JsonObject Records(string title, JsonObject properties, params string[] required) => new() { ["type"] = "array", ["title"] = title, ["items"] = Record(properties, required) };

    /// <summary>A list of values as an answer holds it.</summary>
    public static JsonArray List(IEnumerable<string> items) => new([.. items.Select(i => (JsonNode)i)]);
}
