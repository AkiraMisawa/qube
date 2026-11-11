using Qube.Composition.Application;
using Qube.Composition.Application.Tables;

namespace Qube.Composition.Tests;

// ADR-0016 note 43 of 2026-11-11: `select` is a verb of the screen. The server reads the rows with the selector and
// the view, and answers the one run of rows the page places its grid's Selection on, and no command.
public partial class CommandLineTests
{
    // What a reading of `select` asks the page for: the grid, the run by its first position or the rows by their keys,
    // how many, the Focus's column, and the sentence.
    static string Selecting(LinePreview preview) => preview.Asks is not LineSelect asked ? "none"
        : $"{asked.Table} {asked.Subset}: {(asked.Position is { } at ? $"from {at}" : string.Join(",", asked.Keys!))} x{asked.Count} @{asked.Column} | {asked.Said}";

    [Fact] // ADR-0016 note 43: where the server pages the grid, select answers one run of rows by its first position in the view's order, and no command; nothing is written
    public void Select_answers_one_run_of_rows_by_position_where_the_server_pages_the_grid()
    {
        var rig = new Rig();
        var before = rig.Board.Revision;
        // The readings as sent: R1, R2, R3 (manual), R4, R5, X1 (automatic).
        var readings = new LineView(Stations.Readings, Order: rig.Order(null, []), Focus: new("note", Position: 4));
        // Largest first: R3, R1, X1, R4, R5, R2.
        ViewSort[] largest = [new("volume", Descending: true)];
        var sorted = new LineView(Stations.Readings, Sorts: largest, Order: rig.Order(null, largest));

        var north = rig.Preview("select where station=north", readings);
        var listed = rig.Preview("select R3, R2", readings);
        var one = rig.Preview("select 5", readings);
        var subset = rig.Preview("select automatic", readings);
        var shown = rig.Preview("select shown", readings);
        var narrowed = rig.Preview("select selected where unit=gal", readings with { Ranges = [new(1, 3)] });
        var inOrder = rig.Preview("select R1, X1", sorted);

        Assert.Equal((null, null), (north.Command, north.Inputs));
        Assert.Equal("weather.readings : from 0 x2 @note | Selected 2 rows of readings.", Selecting(north));
        Assert.Equal(("readings, 2 rows | Selects 2 rows of readings.", "rows: where station=north (Typed)", "R1|R2"), (Read(north), Given(north), string.Join("|", north.Rows!.First)));
        // Rows are a run whatever order they were named in.
        Assert.Equal("weather.readings : from 1 x2 @note | Selected 2 rows of readings.", Selecting(listed));
        Assert.Equal(("weather.readings : from 4 x1 @note | Selected R5 in readings.", "readings, 1 row | Selects R5 in readings."), (Selecting(one), Read(one)));
        Assert.Equal("weather.readings : from 3 x3 @note | Selected 3 rows of readings.", Selecting(subset));
        Assert.Equal("weather.readings : from 0 x6 @note | Selected 6 rows of readings.", Selecting(shown));
        // Of R2, R3 and R4, the two in gallons.
        Assert.Equal("weather.readings : from 2 x2 @note | Selected 2 rows of readings.", Selecting(narrowed));
        // A position is one in the view's order: R1 and X1 are apart as sent, and next to one another by volume.
        Assert.Equal("weather.readings : from 1 x2 @ | Selected 2 rows of readings.", Selecting(inOrder));
        Assert.Equal(before, rig.Board.Revision);
        Assert.Contains("select", rig.Lines.ViewVerbs.Select(v => v.VerbId));
        Assert.DoesNotContain(rig.Registry.CapabilityDescriptors, c => c.CapabilityId.Contains("select", StringComparison.Ordinal));
    }

    [Fact] // ADR-0016 note 43: where the page holds every row its grid orders them, so select answers the rows by their keys and no position
    public void Select_answers_the_rows_by_key_where_the_page_holds_them()
    {
        var rig = new Rig();

        var two = rig.Preview("select N1, X1", new LineView(Stations.Sites, Focus: new("altitude", Key: "S1")));
        var high = rig.Preview("select where altitude>20", new LineView(Stations.Sites));
        // A grid that filters its own columns names the rows it shows: one it does not show is refused.
        var hidden = rig.Preview("select N1, S1", new LineView(Stations.Sites, Shown: ["N1", "X1"]));

        Assert.Equal("weather.stations : N1,X1 x2 @altitude | Selected 2 rows of stations.", Selecting(two));
        Assert.Equal(("weather.stations : N1,X1 x2 @ | Selected 2 rows of stations.", "stations, 2 rows | Selects 2 rows of stations."), (Selecting(high), Read(high)));
        Assert.Equal("NotOnScreen: S1 is a row of stations that the grid on screen, stations, does not show: select places the Selection of that grid, and shown says its rows alone.", Read(hidden));
    }

    [Theory] // ADR-0016 note 43: select refuses by name rows it was not told, rows of another grid, rows the grid does not show, rows in several runs and conditions no row meets
    [InlineData("select", "NoRows: Say which readings: their ids, all, manual, automatic, or where and a condition.")]
    [InlineData("select R9", "UnknownRow: No row R9 in readings.")]
    [InlineData("select N1", "UnknownRow: No row N1 in readings.")]
    [InlineData("select stations where altitude>1", "OtherGrid: The grid behind the line is of readings (manual), and the rows are said to be of stations: go to its grid first.")]
    [InlineData("select all", "NotOnScreen: R4, R5, X1 are rows of readings that the grid on screen, readings (manual), does not show: select places the Selection of that grid, and shown says its rows alone.")]
    [InlineData("select automatic", "NotOnScreen: R4, R5, X1 are rows of readings that the grid on screen, readings (manual), does not show: select places the Selection of that grid, and shown says its rows alone.")]
    [InlineData("select R4", "NotOnScreen: R4 is a row of readings that the grid on screen, readings (manual), does not show: select places the Selection of that grid, and shown says its rows alone.")]
    [InlineData("select R1, R3", "SeveralRuns: The 2 rows are in 2 runs in readings (manual), and the line selects rows that are next to one another: filter or sort the grid so that they are, then select shown.")]
    [InlineData("select where unit=m3 and volume>100bn", "NoRowMatches: No row of readings matches unit = M3 and volume > 100bn: there is nothing to select.")]
    [InlineData("select where colour=red", "UnknownField: readings has no field colour; its fields are id, station, volume, unit, taken, checked, note, wind speed, wind direction, window, drift.")]
    [InlineData("select selected", "NothingSelected: No row of readings (manual) is selected.")]
    [InlineData("select R1 now", "UnreadRows: now is left over after the rows: rows are ids separated by commas, all, a subset, or where and its conditions.")]
    public void Select_refuses_by_name_what_the_page_could_not_place(string line, string read)
    {
        var rig = new Rig();

        var preview = rig.Preview(line, new LineView(Stations.Readings, "manual", Order: rig.Order("manual", [])));

        Assert.Equal((read, null), (Read(preview), preview.Asks));
    }

    [Fact] // ADR-0016 notes 39 and 43: select needs a grid behind the line, and positions of an order that has moved are refused as moved
    public async Task Select_needs_a_grid_and_an_order_that_has_not_moved()
    {
        var rig = new Rig();
        ViewSort[] largest = [new("volume", Descending: true)];
        var sorted = new LineView(Stations.Readings, Sorts: largest, Order: rig.Order(null, largest));

        var none = rig.Preview("select R1");
        var before = rig.Preview("select R1, X1", sorted);
        // R2 becomes the largest: the order the grid was drawn from is no longer the table's.
        await rig.Said("set R2 volume 20mm");
        var moved = rig.Preview("select R1, X1", sorted);

        Assert.Equal("NoView: select selects rows of the grid on screen, and no grid of a table is behind this line: go to one first.", Read(none));
        Assert.Equal(1, Assert.IsType<LineSelect>(before.Asks).Position);
        Assert.Equal("ViewMoved: The readings are no longer in the order the grid shows them in: open the line again.", Read(moved));
    }

    [Theory] // ADR-0016 notes 15 and 43: after select, the ways to say rows of the grid are offered
    [InlineData("select", "")]
    [InlineData("select s", "selected (2 rows)@7 | shown (3 rows)@7")]
    [InlineData("select wh", "where@7")]
    [InlineData("select where u", "unit@13")]
    // The grid's table by its word, and its rows: one the grid does not show is refused when the line is read.
    [InlineData("select R", "readings@7 | R1 (readings)@7 | R2 (readings)@7 | R3 (readings)@7 | R4 (readings)@7 | R5 (readings)@7")]
    public void After_select_the_ways_to_say_rows_are_offered(string line, string offered)
    {
        var rig = new Rig();

        var preview = rig.Preview(line, new LineView(Stations.Readings, "manual", Order: rig.Order("manual", []), Ranges: [new(0, 2)]));

        Assert.Equal(offered, string.Join(" | ", preview.Complete.Select(c => $"{c.Value}{(c.Detail is null ? "" : $" ({c.Detail})")}{(c.Start == line.Length ? "" : $"@{c.Start}")}")));
    }
}
