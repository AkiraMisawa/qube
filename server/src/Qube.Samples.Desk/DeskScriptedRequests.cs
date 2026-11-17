using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Qube.Agent;

namespace Qube.Samples.Desk;

/// <summary>
/// What the scripted agent understands of the Desk (ADR-0019 §6, ADR-0007 §6): <c>net risk of &lt;kind&gt;
/// &lt;input&gt;</c>, the Book, What-if and Net lines of one kind and input of the workspace the pane's context names,
/// through <c>desk.position.read</c>. It computes nothing and marks nothing: the lines and their marks are the
/// command's, and the reply counts them.
/// </summary>
public static partial class DeskScriptedRequests
{
    public static IEnumerable<IScriptedRequest> All() => [new NetRisk()];

    sealed class NetRisk : IScriptedRequest
    {
        public string CapabilityId => DeskPositionRead.Id;
        public string Example => "net risk of zero_rate JPY-OIS";
        public string Hint => "open a Desk workspace first, and name a kind and an input as the risk grid shows them";
        public bool TakesTheRest => true;
        public int? FindIn(string request) => NetRiskPattern().Match(request) is { Success: true } said ? said.Index : null;

        public JsonObject Inputs(ScriptedStep step)
        {
            var said = NetRiskPattern().Match(step.Request);
            var request = new JsonObject { ["kind"] = said.Groups["kind"].Value, ["input"] = said.Groups["input"].Value };
            // The workspace the pane shows: the first record of its context that names one. With none the request names
            // none, and the command refuses for it.
            if (step.Shown.Select(shown => shown["workspaceId"]).OfType<JsonValue>().FirstOrDefault(id => id.GetValueKind() == JsonValueKind.String) is { } workspace)
                request["workspaceId"] = workspace.GetValue<string>();
            return new() { ["request"] = request };
        }

        public string Done(JsonObject outputs)
        {
            var lines = (outputs["answer"] ?? throw new InvalidOperationException("A position is answered under answer."))["risk"]!.AsArray();
            if (lines.Count == 0) return "The position has no risk line of that kind and input.";
            var marked = lines.Where(line => line!["marks"]!.AsArray().Count > 0).ToList();
            var of = $"{lines[0]!["kind"]} {lines[0]!["input"]}";
            var read = $"Read {lines.Count} risk line(s) of {of}: Book, What-if and Net by key, in the result.";
            if (marked.Count == 0) return $"{read} None is marked: every figure is the engine's.";
            // The marks said are those the lines carry, each once, in the order the command gives them.
            var marks = marked.SelectMany(line => line!["marks"]!.AsArray().Select(m => m!.GetValue<string>())).Distinct().ToList();
            var says = string.Join(", and ", marks.Select(mark => mark switch
            {
                DeskPosition.WhatIf => "a hypothetical hedge is in them",
                DeskPosition.Simulated => "its risk is not the engine's",
                DeskPosition.FirstOrder => "they are predicted to first order",
                _ => throw new InvalidOperationException($"The position marks a line {mark}, which the reply has no words for."),
            }));
            return $"{read} {marked.Count} of them are marked {string.Join(", ", marks)}: {says}.";
        }
    }

    [GeneratedRegex(@"\bnet\s+risk\s+of\s+(?<kind>\S+)\s+(?<input>\S+)", RegexOptions.CultureInvariant)]
    private static partial Regex NetRiskPattern();
}
