using System.Text.Json.Nodes;
using Qube.Composition.Application;
using Qube.Composition.Application.Tables;

namespace Qube.Composition.Tests;

// ADR-0016 note 58 of 2026-11-12: a field may hold a number with what it is in, and a condition compares such a value
// with its unit said, never across units. The registration is how far a station reaches, with no financial word: N1
// reaches 40 M3, S1 40 GAL and X1 has no reach.
public class MoneyFieldTests
{
    [Fact] // note 58: equal is the same number in the same unit
    public void A_number_with_what_it_is_in_equals_the_same_number_in_the_same_unit()
    {
        var rig = new SensorRig(muted: true);

        Assert.Equal(["N1"], rig.Keys("stations where reach = 40m3"));
        Assert.Equal(["S1", "X1"], rig.Keys("stations where reach != 40m3"));
        Assert.Equal(["N1", "S1"], rig.Keys("stations where reach in 40m3, 40gal"));
    }

    [Fact] // note 58: an order is among the values in the unit asked; one in another unit is in none
    public void A_number_with_what_it_is_in_is_ordered_among_those_in_its_unit()
    {
        var rig = new SensorRig(muted: true);

        Assert.Equal(["N1"], rig.Keys("stations where reach > 30m3"));
        Assert.Equal(["S1"], rig.Keys("stations where reach <= 40gal"));
        Assert.Empty(rig.Keys("stations where reach < 40m3"));
    }

    [Fact] // note 58: a number alone would be compared across units, which nobody meant: refused, with the units
    public void A_condition_on_it_that_says_no_unit_is_refused()
    {
        var rig = new SensorRig(muted: true);

        var refused = rig.RowsRefused("stations where reach > 30");

        Assert.Equal("InvalidValue", refused.Code);
        Assert.Equal("reach is compared with what it is in, M3 or GAL; 30 says neither.", refused.Message);
        Assert.Equal("30", refused.Word);
    }

    [Fact] // note 58: whether a row has a value at all compares none
    public void A_row_with_no_such_value_is_asked_for_as_empty()
    {
        var rig = new SensorRig(muted: true);

        Assert.Equal(["X1"], rig.Keys("stations where reach = empty"));
        Assert.Equal(["N1", "S1"], rig.Keys("stations where reach != empty"));
    }

    [Fact] // note 58: the field is written as it is read, its unit against its number or after it
    public async Task It_is_written_with_its_unit_against_the_number()
    {
        var rig = new SensorRig(muted: true);

        var said = await rig.Run("set stations X1 reach 12gal");

        Assert.Equal("Set X1 reach to 12 GAL.", said["said"]!.GetValue<string>());
        Assert.Equal(["X1"], rig.Keys("stations where reach = 12gal"));
        Assert.Equal("Nothing would change: X1 already holds reach 12 GAL.", rig.Preview("set stations X1 reach 12 gal").Would);
    }

    [Fact] // notes 54 and 58: a draft's form writes it as a line does, and offers no values to choose from: its units are not values
    public void A_drafts_form_writes_it_and_offers_no_values()
    {
        var rig = new SensorRig(muted: true);

        var form = rig.Lines.Draft(rig.Id, SensorRig.Ana, new(Stations.Sites, "N1", rig.Board.Revision, ["set stations N1 reach 12gal"]));

        var reach = form.Fields.Single(f => f.Field.Name == "reach");
        Assert.Equal(("12 GAL", true, "money", null), (reach.Written, reach.Changed, reach.Field.Form, reach.Field.Choices));
        Assert.Equal("a number, alone or followed by what it is in: M3 or GAL, or empty", reach.Field.Takes);
    }

    [Fact] // notes 38 and 58: a description says what its number may be in, which are no values of the field
    public async Task A_description_says_what_it_may_be_in()
    {
        var rig = new SensorRig(muted: true);

        var result = await rig.Commands.InvokeAsync("tables.describe", TableContracts.Version, new JsonObject { ["request"] = new JsonObject { ["workspaceId"] = rig.Id } }, SensorRig.Ana,
            Guid.NewGuid().ToString(), default);

        var reach = Assert.IsType<CommandResult.Done>(result).Outputs["answer"]!["fields"]!.AsArray().Single(f => f!["name"]!.GetValue<string>() == "reach")!;
        Assert.Equal("money", reach["form"]!.GetValue<string>());
        Assert.Equal(["M3", "GAL"], reach["units"]!.AsArray().Select(u => u!.GetValue<string>()));
        Assert.Null(reach["choices"]);
    }
}
