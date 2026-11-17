using Qube.Web;

namespace Qube.Composition.Tests;

// docs/reports/rates-live-30-users.html §6: the live calculation slots are a setting, matched to the engine's parallelism.
public class RatesSettingsTests
{
    [Fact]
    public void Live_calculation_slots_default_to_four_and_can_be_set()
    {
        Assert.Equal(4, new RatesSettings().LiveCalculationSlots());
        Assert.Equal(16, new RatesSettings { LiveMaxCalculations = 16 }.LiveCalculationSlots());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Fewer_than_one_slot_stops_startup(int slots)
    {
        var failure = Assert.Throws<InvalidOperationException>(() => new RatesSettings { LiveMaxCalculations = slots }.LiveCalculationSlots());
        Assert.Contains("Rates:LiveMaxCalculations", failure.Message);
    }
}

// docs/08 and CLAUDE.md: the provider is chosen by configuration, and a chosen engine that is not fully configured stops
// startup rather than running on the fake one.
public class RatesProviderSettingsTests
{
    static RatesSettings Alpha(Action<AlphaSetting>? edit = null)
    {
        var s = new RatesSettings
        {
            Provider = "alpha",
            Alpha = new AlphaSetting
            {
                Command = "/opt/alpha/xva-rates-worker",
                Curves = [new() { Name = "JPY-OIS", Currency = "JPY", Role = "discount", Interpolation = "log_linear_df", Quotes = "JPY/TONA" }],
                Forward = new() { ["JPY/TONA"] = "JPY-OIS" },
                Discount = new() { ["JPY@JPY"] = "JPY-OIS" },
            },
        };
        edit?.Invoke(s.Alpha);
        return s;
    }

    [Fact]
    public void The_fake_provider_is_the_default()
    {
        Assert.Equal("fake", new RatesSettings().ProviderChoice());
    }

    [Fact]
    public void A_configured_alpha_provider_gives_its_curve_map()
    {
        var s = Alpha();
        Assert.Equal("alpha", s.ProviderChoice());
        var map = s.Alpha.CurveMap();
        Assert.Equal("JPY-OIS", map.Forward["JPY/TONA"]);
        Assert.Equal(("JPY-OIS", "JPY/TONA"), (map.Curves[0].Name, map.Curves[0].Quotes));
    }

    [Fact]
    public void Alpha_without_a_command_stops_startup()
    {
        var failure = Assert.Throws<InvalidOperationException>(() => Alpha(a => a.Command = "").ProviderChoice());
        Assert.Contains("Rates:Alpha:Command", failure.Message);
    }

    [Fact]
    public void A_map_naming_a_curve_that_is_not_defined_stops_startup()
    {
        var failure = Assert.Throws<InvalidOperationException>(() => Alpha(a => a.Forward["USD/SOFR"] = "USD-OIS").ProviderChoice());
        Assert.Contains("USD-OIS", failure.Message);
    }

    [Fact]
    public void An_unknown_provider_stops_startup()
    {
        Assert.Throws<InvalidOperationException>(() => new RatesSettings { Provider = "quantlib" }.ProviderChoice());
    }
}
