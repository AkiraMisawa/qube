using System.Text.Json.Nodes;
using Qube.Composition.Application;
using Qube.Composition.Application.Settings;

namespace Qube.Agent;

/// <summary>
/// The AI's setting (ADR-0017 §1, §6.3): the user's default model, which a new conversation starts on. A preference: it
/// changes which model answers, never a figure. It is registered only where configuration lists the models the AI may
/// use, the first of them as its registered value; the stand-in agent runs on none and registers nothing.
/// </summary>
public sealed class AgentSettings : ISettingCatalog
{
    public const string DefaultModel = "agent.defaultModel";

    readonly IReadOnlyDictionary<string, string> models;
    readonly string first;

    /// <summary>Fails where no model is listed, or one is blank or listed twice.</summary>
    public AgentSettings(IReadOnlyList<string> allowed)
    {
        if (allowed.Count == 0) throw new InvalidOperationException("The AI's default model is chosen among the models it may use, and none is listed.");
        if (allowed.FirstOrDefault(string.IsNullOrWhiteSpace) is not null) throw new InvalidOperationException("A model the AI may use is listed without its id.");
        if (allowed.GroupBy(m => m, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1) is { } twice)
            throw new InvalidOperationException($"The model {twice.Key} is listed twice among those the AI may use.");
        models = allowed.ToDictionary(m => m.ToLowerInvariant(), m => m);
        first = allowed[0];
    }

    public IEnumerable<SettingDefinition> Settings =>
    [
        new(DefaultModel, "Default model", ["default model"], SettingKind.Preference, new FieldSpec(FieldForm.Choice, Choices: models), JsonValue.Create(first)),
    ];
}
