using System.Text.Json.Nodes;
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

    // The company profile's is registered (ADR-0017's first registrations): a user who sets none starts each workspace
    // in it, and the page says it is the profile's. The making copies in only what the user set (§6.1).
    public IEnumerable<SettingDefinition> Settings =>
    [
        new(DisplayCurrency, "Display currency", ["display currency"], SettingKind.Default, new FieldSpec(FieldForm.Choice, Choices: PortfolioFields.Currencies),
            JsonValue.Create(PortfolioCatalog.Profile.DisplayCurrency)),
    ];
}
