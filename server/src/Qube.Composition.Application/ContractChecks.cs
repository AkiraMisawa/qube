using System.Text.Json;
using System.Text.Json.Nodes;

namespace Qube.Composition.Application;

/// <summary>
/// Checks a small value against its contract's schema. Only a declared subset of JSON Schema is understood; a schema
/// using anything else is refused at registration rather than half-checked. A field is one value (text, a number, a
/// flag) or a list (ADR-0016 note 37 of 2026-11-11): a list of values, or of records whose fields are values or lists
/// of values, and nothing deeper.
/// </summary>
public static class ContractChecks
{
    const string List = "array";
    const string Record = "object";
    static readonly HashSet<string> ObjectKeywords = ["type", "properties", "required", "additionalProperties", "title"];
    static readonly HashSet<string> RecordKeywords = ["type", "properties", "required", "additionalProperties"];
    static readonly HashSet<string> ValueKeywords = ["type", "title", "description", "enum", "minimum", "maximum", "minLength", "format", "default"];
    static readonly HashSet<string> ListKeywords = ["type", "title", "description", "items", "minItems"];
    static readonly HashSet<string> ValueTypes = ["string", "integer", "number", "boolean"];

    public static IEnumerable<string> SchemaProblems(ContractDefinition contract)
    {
        var schema = contract.Schema;
        foreach (var keyword in schema.Select(p => p.Key).Where(k => !ObjectKeywords.Contains(k)))
            yield return $"{contract.ContractId}: keyword '{keyword}' is not supported on the object.";
        if (TypeOf(schema) != Record)
            yield return $"{contract.ContractId}: a form contract is an object.";
        if (schema["additionalProperties"]?.GetValue<bool>() != false)
            yield return $"{contract.ContractId}: a form contract refuses additional properties.";
        foreach (var (name, field) in Fields(schema))
            foreach (var problem in FieldProblems(field, $"{contract.ContractId}.{name}", records: true))
                yield return problem;
    }

    /// <summary>The fields of a contract that are lists, by name: what a form, which draws one control for one value, cannot draw.</summary>
    public static IReadOnlyList<string> Lists(ContractDefinition contract) => [.. Fields(contract.Schema).Where(f => TypeOf(f.Field) == List).Select(f => f.Name)];

    static string? TypeOf(JsonObject schema) => schema["type"]?.GetValue<string>();

    static IEnumerable<(string Name, JsonObject Field)> Fields(JsonObject schema) =>
        schema["properties"] is JsonObject properties ? properties.Select(p => (p.Key, p.Value!.AsObject())) : [];

    // A field's schema: a value's, or a list's. A list's items are values or, where the list is a contract's own
    // field (records), records of fields that are values or lists of values.
    static IEnumerable<string> FieldProblems(JsonObject field, string at, bool records)
    {
        if (TypeOf(field) != List)
        {
            foreach (var problem in ValueProblems(field, at)) yield return problem;
            yield break;
        }
        foreach (var keyword in field.Select(p => p.Key).Where(k => !ListKeywords.Contains(k)))
            yield return $"{at}: keyword '{keyword}' is not supported on a list.";
        if (field["items"] is not JsonObject items)
        {
            yield return $"{at}: a list says its items.";
            yield break;
        }
        switch (TypeOf(items))
        {
            case List:
                yield return $"{at}: a list holds values or records, not lists.";
                break;
            case Record when !records:
                yield return $"{at}: a list in a record holds values.";
                break;
            case Record:
                foreach (var keyword in items.Select(p => p.Key).Where(k => !RecordKeywords.Contains(k)))
                    yield return $"{at}[]: keyword '{keyword}' is not supported on a record.";
                if (items["additionalProperties"]?.GetValue<bool>() != false)
                    yield return $"{at}[]: a record refuses additional properties.";
                foreach (var (name, inner) in Fields(items))
                    foreach (var problem in FieldProblems(inner, $"{at}[].{name}", records: false))
                        yield return problem;
                break;
            default:
                foreach (var problem in ValueProblems(items, $"{at}[]")) yield return problem;
                break;
        }
    }

    static IEnumerable<string> ValueProblems(JsonObject field, string at)
    {
        foreach (var keyword in field.Select(p => p.Key).Where(k => !ValueKeywords.Contains(k)))
            yield return $"{at}: keyword '{keyword}' is not supported.";
        if (TypeOf(field) is not { } type || !ValueTypes.Contains(type))
            yield return $"{at}: type must be one of {string.Join(", ", ValueTypes)}, or {List}.";
    }

    public static IEnumerable<CompositionIssue> Check(ContractDefinition contract, JsonObject value, string at) =>
        RecordIssues(contract.Schema, value, at, contract.ContractId);

    // A record against its schema: what it must hold, and each field it holds. `named` is what a field it has not is
    // said of: the contract, or an item of a list.
    static IEnumerable<CompositionIssue> RecordIssues(JsonObject schema, JsonObject value, string at, string named)
    {
        var fields = Fields(schema).ToDictionary(f => f.Name, f => f.Field);
        foreach (var required in schema["required"]?.AsArray().Select(r => r!.GetValue<string>()) ?? [])
            if (!value.ContainsKey(required))
                yield return new("Required", $"{at}/{required}", $"{required} is required.");
        foreach (var (name, node) in value)
        {
            if (!fields.TryGetValue(name, out var field))
            {
                yield return new("UnknownField", $"{at}/{name}", $"{named} has no field {name}.");
                continue;
            }
            foreach (var issue in FieldIssues(field, node, $"{at}/{name}", name)) yield return issue;
        }
    }

    static IEnumerable<CompositionIssue> FieldIssues(JsonObject field, JsonNode? node, string at, string name)
    {
        if (TypeOf(field) != List)
        {
            if (ValueProblem(field, node) is { } problem) yield return new("InvalidField", at, problem);
            yield break;
        }
        if (node is not JsonArray list)
        {
            yield return new("InvalidField", at, "A list is required.");
            yield break;
        }
        if (field["minItems"]?.GetValue<int>() is { } least && list.Count < least)
            yield return new("InvalidField", at, $"At least {least} item{(least == 1 ? " is" : "s are")} required.");
        var items = field["items"]!.AsObject();
        for (var i = 0; i < list.Count; i++)
        {
            var item = $"{at}/{i}";
            if (TypeOf(items) != Record)
            {
                if (ValueProblem(items, list[i]) is { } problem) yield return new("InvalidField", item, problem);
            }
            else if (list[i] is JsonObject record)
            {
                foreach (var issue in RecordIssues(items, record, item, $"An item of {name}")) yield return issue;
            }
            else
            {
                yield return new("InvalidField", item, "A record is required.");
            }
        }
    }

    static string? ValueProblem(JsonObject field, JsonNode? node)
    {
        if (node is not JsonValue value) return "A value is required.";
        var type = field["type"]!.GetValue<string>();
        switch (type)
        {
            case "string":
                if (value.GetValueKind() != JsonValueKind.String) return "A text value is required.";
                var text = value.GetValue<string>();
                if (field["minLength"] is { } min && text.Length < min.GetValue<int>()) return $"At least {min} characters are required.";
                if (field["enum"] is JsonArray options && options.All(o => o!.GetValue<string>() != text))
                    return $"Must be one of {string.Join(", ", options)}.";
                if (field["format"]?.GetValue<string>() == "date" && !DateOnly.TryParseExact(text, "yyyy-MM-dd", out _))
                    return "A date as yyyy-MM-dd is required.";
                return null;
            case "integer":
            case "number":
                if (value.GetValueKind() != JsonValueKind.Number) return "A number is required.";
                var number = decimal.Parse(value.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture);
                if (type == "integer" && number != decimal.Truncate(number)) return "A whole number is required.";
                if (field["minimum"] is { } lo && number < lo.GetValue<decimal>()) return $"Must be at least {lo}.";
                if (field["maximum"] is { } hi && number > hi.GetValue<decimal>()) return $"Must be at most {hi}.";
                return null;
            case "boolean":
                return value.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? null : "true or false is required.";
            default:
                return $"Unsupported type {type}.";
        }
    }
}
