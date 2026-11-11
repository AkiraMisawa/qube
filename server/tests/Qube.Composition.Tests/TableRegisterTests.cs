using Qube.Composition.Application.Tables;

namespace Qube.Composition.Tests;

// ADR-0016 §1 and its note 6 of 2026-11-11: a domain registers its tables through a catalog port, and the kernel finds
// a table and a field by what a person types for them. The registration is station readings, with no financial word.
public class TableRegisterTests
{
    static IReadOnlyList<BoundTable> Tables()
    {
        var service = Stations.Service();
        return new TableRegister([new Stations.Catalog()]).Of(Stations.Board(service));
    }

    [Theory] // ADR-0016 §1: a table is named by its id, which a saved definition holds, or by any of its words, case ignored
    [InlineData("weather.readings")]
    [InlineData("readings")]
    [InlineData("Obs")]
    [InlineData("  READINGS ")]
    public void A_table_is_found_by_its_id_and_by_each_of_its_words(string name) =>
        Assert.Equal(Stations.Readings, TableRegister.Find(Tables(), name).Descriptor.TableId);

    [Fact] // ADR-0016 §6: a refusal says the word at fault and what it could have been
    public void An_unknown_table_is_refused_with_the_tables_there_are()
    {
        var refused = Assert.Throws<TableRefused>(() => TableRegister.Find(Tables(), "tides"));

        Assert.Equal(("UnknownTable", "No table tides: the tables are readings, stations, settings.", "tides"), (refused.Code, refused.Message, refused.Word));
        Assert.Equal(["readings", "stations", "settings"], refused.Candidates);
    }

    [Theory] // ADR-0016 §1: a field answers to its words, which may be several, and to its name
    [InlineData("vol", "volume")]
    [InlineData("Wind  Speed", "windSpeed")]
    [InlineData("windDirection", "windDirection")]
    [InlineData("OK", "checked")]
    public void A_field_is_found_by_its_words(string words, string field) =>
        Assert.Equal(field, TableRegister.Field(TableRegister.Find(Tables(), "readings"), words).Name);

    [Fact] // ADR-0016 §1, spine 1: words two fields share name neither, and an unknown word is refused with the fields there are
    public void Words_that_name_two_fields_or_none_are_refused()
    {
        var readings = TableRegister.Find(Tables(), "readings");

        var two = Assert.Throws<TableRefused>(() => TableRegister.Field(readings, "wind"));
        var none = Assert.Throws<TableRefused>(() => TableRegister.Field(readings, "colour"));

        Assert.Equal(("AmbiguousField", "wind could be wind speed or wind direction of readings."), (two.Code, two.Message));
        Assert.Equal(["wind speed", "wind direction"], two.Candidates);
        Assert.Equal(("UnknownField", "readings has no field colour; its fields are id, station, volume, unit, taken, checked, note, wind speed, wind direction, window, drift."),
            (none.Code, none.Message));
    }

    [Fact] // ADR-0016 §1: an id is what a saved definition names, so two tables never share one
    public void The_same_id_registered_twice_fails_at_start()
    {
        var twice = Assert.Throws<InvalidOperationException>(() => new TableRegister([new Stations.Catalog(), new Stations.Catalog()]));

        Assert.Equal("Table weather.readings is registered twice.", twice.Message);
    }

    [Fact] // ADR-0016 §1: a workspace has the tables of its own application and no other's
    public void A_workspace_has_the_tables_of_its_application() =>
        Assert.Equal([Stations.Readings, Stations.Sites, Stations.Settings], Tables().Select(t => t.Descriptor.TableId));
}
