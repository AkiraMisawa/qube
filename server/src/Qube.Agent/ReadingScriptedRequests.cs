using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Qube.Agent;

/// <summary>
/// The stand-in's requests for the kernel's readings (ADR-0016 §7, note 38; ADR-0007 §6): <c>describe the tables</c>,
/// <c>read &lt;rows&gt; [fields &lt;field&gt;, &lt;field&gt;]</c> and <c>preview &lt;line&gt;</c>, each of the workspace
/// the pane's context names. They know no table and no command: the rows, the fields and the line are handed to the
/// tool as written, and the tool refuses what it cannot read.
/// </summary>
public static partial class ReadingScriptedRequests
{
    public static IEnumerable<IScriptedRequest> All() => [new Describe(), new Read(), new Preview()];

    // The workspace the pane shows: the first entry of its context that names one. With none the request names none,
    // and the tool refuses for it.
    static JsonObject Request(ScriptedStep step)
    {
        var request = new JsonObject();
        foreach (var entry in step.Context)
        {
            JsonNode? value;
            try
            {
                value = JsonNode.Parse(entry.Value);
            }
            catch (JsonException)
            {
                continue;
            }
            if (value is not JsonObject shown || shown["workspaceId"] is not JsonValue id || id.GetValueKind() != JsonValueKind.String) continue;
            request["workspaceId"] = id.GetValue<string>();
            break;
        }
        return request;
    }

    static JsonNode Answer(JsonObject outputs) => outputs["answer"] ?? throw new InvalidOperationException("A reading answers under answer.");

    static string Counted(int count, string noun) => $"{count} {noun}(s)";

    sealed class Describe : IScriptedRequest
    {
        public string CapabilityId => "tables.describe";
        public string Example => "describe the tables";
        public string Hint => "open a workspace first";
        public bool TakesTheRest => true;
        public int? FindIn(string request) => DescribePattern().IsMatch(request) ? 0 : null;
        public JsonObject Inputs(ScriptedStep step) => new() { ["request"] = Request(step) };

        public string Done(JsonObject outputs)
        {
            var tables = Answer(outputs)["tables"]!.AsArray();
            return $"The workspace has {Counted(tables.Count, "table")}: {string.Join(", ", tables.Select(t => $"{t!["words"]![0]} ({Counted(t["rows"]!.GetValue<int>(), "row")})"))}.";
        }
    }

    sealed class Read : IScriptedRequest
    {
        public string CapabilityId => "tables.read";
        public string Example => "read where <field>=<value> fields <field>, <field>";
        public string Hint => "rows as the command line spells them, such as all or where <field>=<value>, then fields and their names";
        public bool TakesTheRest => true;
        public int? FindIn(string request) => ReadPattern().IsMatch(request) ? 0 : null;

        public JsonObject Inputs(ScriptedStep step)
        {
            var said = ReadPattern().Match(step.Request);
            var request = Request(step);
            request["rows"] = said.Groups["rows"].Value;
            if (said.Groups["fields"].Success)
                request["fields"] = new JsonArray([.. said.Groups["fields"].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(f => (JsonNode)f)]);
            return new() { ["request"] = request };
        }

        public string Done(JsonObject outputs)
        {
            var answer = Answer(outputs);
            var (count, listed, more) = (answer["count"]!.GetValue<int>(), answer["rows"]!.AsArray().Count, answer["more"]!.GetValue<int>());
            var matched = $"{Counted(count, "row")} of {answer["table"]} match";
            return listed == count ? $"{matched}, and all are listed." : $"{matched}; {listed} are listed, and {more} follow.";
        }
    }

    sealed class Preview : IScriptedRequest
    {
        public string CapabilityId => "commands.preview";
        public string Example => "preview set where <field>=<value> <field> <value>";
        public string Hint => "a command line as it is typed";
        public bool TakesTheRest => true;
        public int? FindIn(string request) => PreviewPattern().IsMatch(request) ? 0 : null;

        public JsonObject Inputs(ScriptedStep step)
        {
            var request = Request(step);
            request["line"] = PreviewPattern().Match(step.Request).Groups["line"].Value;
            return new() { ["request"] = request };
        }

        public string Done(JsonObject outputs)
        {
            var answer = Answer(outputs);
            var line = $"`{answer["line"]}`";
            var would = answer["would"]?.GetValue<string>() is { } change ? change : $"runs {answer["command"]}";
            return answer["rows"] is { } rows ? $"{line} would act on {rows}: {would}. Nothing was run." : $"{line}: {would}. Nothing was run.";
        }
    }

    [GeneratedRegex(@"^\s*describe\s+(?:the\s+)?tables\b", RegexOptions.CultureInvariant)]
    private static partial Regex DescribePattern();

    [GeneratedRegex(@"^\s*read\s+(?<rows>.+?)(?:\s+fields?\s+(?<fields>.+?))?\s*$", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex ReadPattern();

    [GeneratedRegex(@"^\s*preview\s+(?<line>.+?)\s*$", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex PreviewPattern();
}
