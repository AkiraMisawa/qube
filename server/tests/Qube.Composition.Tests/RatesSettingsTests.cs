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
