using Qube.Composition.Application;
using Qube.Composition.Application.Settings;

namespace Qube.Samples.Portfolio;

/// <summary>
/// The pricer's settings (ADR-0017 §6.1): the display currency a new workspace starts in. A default: the making copies
/// it in (§3), and a workspace made before it was set, or edited since, keeps its own.
/// </summary>
public sealed class PortfolioSettings : ISettingCatalog
{
    public const string DisplayCurrency = "pricer.displayCurrency";

    // None registered: a user who sets none starts each workspace at the company profile's, which the page says.
    public IEnumerable<SettingDefinition> Settings =>
    [
        new(DisplayCurrency, "Display currency", ["display currency"], SettingKind.Default, new FieldSpec(FieldForm.Choice, Choices: PortfolioFields.Currencies), null),
    ];
}
