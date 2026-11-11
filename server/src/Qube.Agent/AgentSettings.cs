using System.Text.Json.Nodes;
using Qube.Composition.Application;
using Qube.Composition.Application.Settings;

namespace Qube.Agent;

/// <summary>
/// The AI's setting (ADR-0017 §1, §6.3): the user's default model, which a new conversation starts on. A preference: it
/// changes which model answers, never a figure. It is registered only where the runtime runs on models, which
/// configuration lists, the first of them as its registered value; the stand-in agent runs on none and registers nothing.
/// </summary>
public sealed class AgentSettings : ISettingCatalog
{
    public const string DefaultModel = "agent.defaultModel";

    readonly IReadOnlyList<string> allowed;

    /// <summary>The runtime's models, as it lists them. Fails where one is blank, or listed twice whatever its case (a line reads a model's id in any case).</summary>
    public AgentSettings(AgentRuntimeInfo runtime)
    {
        allowed = runtime.Models;
        if (allowed.FirstOrDefault(string.IsNullOrWhiteSpace) is not null) throw new InvalidOperationException("A model the AI may use is listed without its id.");
        if (allowed.GroupBy(m => m, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1) is { } twice)
            throw new InvalidOperationException($"The model {twice.Key} is listed twice among those the AI may use.");
    }

    public IEnumerable<SettingDefinition> Settings => allowed.Count == 0 ? [] :
    [
        new(DefaultModel, "Default model", ["default model"], SettingKind.Preference,
            new FieldSpec(FieldForm.Choice, Choices: allowed.ToDictionary(m => m.ToLowerInvariant(), m => m)), JsonValue.Create(allowed[0])),
    ];
}
