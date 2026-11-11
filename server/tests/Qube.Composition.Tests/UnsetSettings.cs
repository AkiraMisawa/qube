using Qube.Composition.Application;
using Qube.Composition.Application.Settings;
using Qube.Samples.Portfolio;
using Qube.Samples.Rates;

namespace Qube.Composition.Tests;

/// <summary>
/// The users' settings with nothing set, for a test of a making that is not about defaults: each workspace then starts
/// as it did before a user set any (ADR-0017 §3).
/// </summary>
static class UnsetSettings
{
    public static UserSettings Pricer() => new([new PortfolioSettings()], new InMemoryUserSettingStore(), TimeProvider.System);

    public static UserSettings Rates(ConventionRegistry conventions) => new([new RatesNewLineSettings(conventions)], new InMemoryUserSettingStore(), TimeProvider.System);
}
