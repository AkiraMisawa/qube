using System.Text.Json.Nodes;
using Qube.Composition.Application;
using Qube.Composition.Application.Settings;

namespace Qube.Samples.Rates;

/// <summary>
/// Rates' settings (ADR-0017 §6.1): the currency, notional and direction a new workspace's lines take where a line
/// leaves them out (ADR-0008 §4b). Defaults: the making copies them into the workspace's own defaults (§3), and a
/// workspace made before they were set, or whose defaults were set in it since, keeps its own.
/// </summary>
public sealed class RatesNewLineSettings(ConventionRegistry conventions) : ISettingCatalog
{
    public const string Currency = "rates.newLine.currency";
    public const string Notional = "rates.newLine.notional";
    public const string Direction = "rates.newLine.direction";

    // None registered: a user who sets none starts each workspace with no defaults, as before, and a line names its own.
    public IEnumerable<SettingDefinition> Settings =>
    [
        new(Currency, "New line currency", ["new line currency"], SettingKind.Default, new FieldSpec(FieldForm.Choice, Choices: RatesProducts.Currencies(conventions)), null),
        new(Notional, "New line notional", ["new line notional"], SettingKind.Default, new FieldSpec(FieldForm.Amount), null, MoreThanZero),
        new(Direction, "New line direction", ["new line direction"], SettingKind.Default, new FieldSpec(FieldForm.Choice, Choices: RatesProducts.Directions), null),
    ];

    // As a workspace's own default notional: an amount of 0 is no notional a line is made with.
    static string? MoreThanZero(JsonNode value) => LineDefaults.NotANotional(value) is { } why ? $"an amount of {why}" : null;
}
