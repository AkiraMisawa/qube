using Qube.Composition.Application;
using Qube.Composition.Application.Tables;
using Qube.Composition.Application.Workspaces;

namespace Qube.Composition.Tests;

// ADR-0016 §3 and its notes 39 to 41 of 2026-11-11: `sort` and `filter` are verbs of the screen. The server reads
// their lines and answers what the page is to do with the grid behind the line; a view carries its grid's conditions,
// and `shown`, a position and the order's token are read under them.
public partial class CommandLineTests
{
    static string Sorted(LinePreview preview) => preview.Sort is not { } sort ? "none"
        : $"{sort.Table} {sort.Subset}: {string.Join(", ", sort.Sorts.Select(s => $"{s.Column}{(s.Descending ? " desc" : "")}"))} | {sort.Said}";

    [Fact] // ADR-0016 notes 39 and 40: sort is read by the server, which answers the sort the page gives its grid, and no command; nothing is written
    public void Sort_answers_the_sort_the_page_gives_its_grid_and_no_command()
    {
        var rig = new Rig();
        var before = rig.Board.Revision;
        var manual = new LineView(Stations.Readings, "manual", Order: rig.Order("manual", []));

        var one = rig.Preview("sort by volume desc", manual);
        var named = rig.Preview("sort obs by vol ascending", manual);
        var off = rig.Preview("sort off", manual);
        // The stations have no reader that pages them: their page holds every row, and its grid sorts by any field.
        var held = rig.Preview("sort by altitude descending, backup", new LineView(Stations.Sites));

        Assert.Equal((null, null, null, null), (one.Command, one.Inputs, one.Place, one.Filter));
        Assert.Equal("weather.readings manual: volume desc | Sorted readings (manual) by volume, descending.", Sorted(one));
        Assert.Equal(("Sorts readings (manual) by volume, descending.", "sort: by volume desc (Typed)", null), (one.Would, Given(one), one.Rows));
        Assert.Equal("weather.readings manual: volume | Sorted readings (manual) by volume.", Sorted(named));
        Assert.Equal(("weather.readings manual:  | Took the sort off readings (manual).", "Takes the sort off readings (manual)."), (Sorted(off), off.Would));
        Assert.Equal("weather.stations : altitude desc, backup | Sorted stations by altitude, descending, then backup.", Sorted(held));
        Assert.Equal(before, rig.Board.Revision);
        Assert.Contains("sort", rig.Lines.ViewVerbs.Select(v => v.VerbId));
        Assert.DoesNotContain(rig.Registry.CapabilityDescriptors, c => c.CapabilityId.Contains("sort", StringComparison.Ordinal));
    }

    [Theory] // ADR-0016 notes 39 and 40: sort refuses by name a line it cannot read, a field its table has not and one the table's reader does not sort by
    [InlineData("sort", "NoSort: Say what to sort readings (manual) by: sort by a field, or sort off.")]
    [InlineData("sort volume", "NoSort: Say what to sort readings (manual) by: sort by a field, or sort off.")]
    [InlineData("sort by", "NoField: Say which field to sort readings (manual) by.")]
    [InlineData("sort by volume,", "NoField: Say which field to sort readings (manual) by.")]
    [InlineData("sort by colour", "UnknownField: readings has no field colour; its fields are id, station, volume, unit, taken, checked, note, wind speed, wind direction, window, drift.")]
    [InlineData("sort by wind", "AmbiguousField: wind could be wind speed or wind direction of readings.")]
    [InlineData("sort by taken desc", "NotSortable: Readings cannot be sorted by taken.")]
    [InlineData("sort by volume, taken", "NotSortable: Readings cannot be sorted by taken.")]
    [InlineData("sort by volume, vol desc", "FieldTwice: volume is named twice; name each field once.")]
    [InlineData("sort off now", "UnreadWords: sort off takes no words after it.")]
    [InlineData("sort stations by altitude", "OtherGrid: The grid behind the line is of readings (manual), and stations is another table: go to its grid first.")]
    public void Sort_refuses_by_name_what_it_cannot_ask_the_page_for(string line, string read)
    {
        var rig = new Rig();

        var preview = rig.Preview(line, new LineView(Stations.Readings, "manual", Order: rig.Order("manual", [])));

        Assert.Equal((read, null), (Read(preview), preview.Sort));
    }

    [Fact] // ADR-0016 note 39: with no grid behind the line a verb of the screen is refused, and the word at fault is marked where it stands
    public void Sort_needs_a_grid_behind_the_line_and_marks_the_word_at_fault()
    {
        var rig = new Rig();

        var none = rig.Preview("sort by volume");
        var unsortable = rig.Preview("sort by volume, taken desc", new LineView(Stations.Readings));

        Assert.Equal("NoView: sort sorts the grid on screen, and no grid of a table is behind this line: go to one first.", Read(none));
        Assert.Equal(("taken", 16, 21), (unsortable.Refused!.Word, unsortable.Refused.Start, unsortable.Refused.End));
    }

    [Theory] // ADR-0016 note 40: after sort, `by` and `off` are offered, then the table's fields, then `desc`
    [InlineData("sort", "")]
    [InlineData("sort ", "by | off")]
    [InlineData("sort o", "off@5")]
    [InlineData("sort by ", "id | station | volume | unit | taken | checked | note | wind speed | wind direction | window | drift")]
    [InlineData("sort by wi", "wind speed@8 | wind direction@8 | window@8")]
    [InlineData("sort by wind s", "wind speed@8")]
    [InlineData("sort by volume ", "desc")]
    [InlineData("sort by volume d", "desc@15")]
    [InlineData("sort by volume desc, ", "id | station | volume | unit | taken | checked | note | wind speed | wind direction | window | drift")]
    [InlineData("sort by volume desc, ta", "taken@21")]
    [InlineData("sort off ", "")]
    public void After_sort_by_and_off_then_the_fields_then_desc_are_offered(string line, string offered)
    {
        var preview = new Rig().Preview(line, new LineView(Stations.Readings, "manual"));

        Assert.Equal(offered, string.Join(" | ", preview.Complete.Select(c => $"{c.Value}{(c.Start == line.Length ? "" : $"@{c.Start}")}")));
    }

    static string Filtered(LinePreview preview) => preview.Filter is not { } filter ? "none" : $"{filter.Table} {filter.Subset}: {filter.Where ?? "off"} | {filter.Said}";

    [Fact] // ADR-0016 notes 39 and 41: filter is read by the server, which answers the conditions the page gives its grid and how many rows they leave, and no command; nothing is written
    public void Filter_answers_the_conditions_the_page_gives_its_grid_and_the_rows_they_leave()
    {
        var rig = new Rig();
        var before = rig.Board.Revision;
        var manual = new LineView(Stations.Readings, "manual", Order: rig.Order("manual", []));

        var north = rig.Preview("filter where station=north", manual);
        var two = rig.Preview("filter readings manual where  unit=m3 and wind speed>20", manual);
        // Conditions may leave no row: the reading says so before Enter.
        var none = rig.Preview("filter where station=east", manual);
        var off = rig.Preview("filter off", manual with { Where = "station=north" });
        var held = rig.Preview("filter where altitude>20", new LineView(Stations.Sites));

        Assert.Equal((null, null, null, null), (north.Command, north.Inputs, north.Place, north.Sort));
        Assert.Equal("weather.readings manual: station=north | Showing 2 of 3 rows of readings (manual) where station = NORTH.", Filtered(north));
        Assert.Equal(("readings (manual), 2 of 3 rows | Shows 2 of 3 rows of readings (manual).", "where: station=north (Typed)", 2, "R1|R2"),
            (Read(north), Given(north), north.Rows!.Count, string.Join("|", north.Rows.First)));
        // The conditions are kept as typed, with one space between their words.
        Assert.Equal("weather.readings manual: unit=m3 and wind speed>20 | Showing 1 of 3 rows of readings (manual) where unit = M3 and wind speed > 20.", Filtered(two));
        Assert.Equal(("weather.readings manual: station=east | Showing 0 of 3 rows of readings (manual) where station = EAST.", 0), (Filtered(none), none.Rows!.Count));
        Assert.Equal(("weather.readings manual: off | Took the filter off readings (manual).", "Takes the filter off readings (manual).", null), (Filtered(off), off.Would, off.Rows));
        Assert.Equal("weather.stations : altitude>20 | Showing 2 of 3 rows of stations where altitude > 20.", Filtered(held));
        Assert.Equal(before, rig.Board.Revision);
        Assert.Equal(["find", "sort", "filter"], rig.Lines.ViewVerbs.Select(v => v.VerbId));
    }

    [Theory] // ADR-0016 notes 39 and 41: filter refuses conditions as `where` refuses them, and by name a line that asks for another grid's rows
    [InlineData("filter", "NoFilter: Say which rows of readings (manual) to show: filter where and conditions, or filter off.")]
    [InlineData("filter station=north", "NoFilter: Say which rows of readings (manual) to show: filter where and conditions, or filter off.")]
    [InlineData("filter where", "UnreadCondition: where names no condition: a condition is a field, an operator (=, !=, <, <=, >, >=, ~, in) and a value, and conditions are joined by and.")]
    [InlineData("filter where colour=red", "UnknownField: readings has no field colour; its fields are id, station, volume, unit, taken, checked, note, wind speed, wind direction, window, drift.")]
    [InlineData("filter where station=nowhere", "InvalidValue: station is one of NORTH, SOUTH, EAST, or empty; nowhere is not.")]
    [InlineData("filter where volume>1mm", "MixedUnits: volume is in M3 and GAL here, and an amount is not compared across them; say which, as in where unit=M3 and volume>1mm.")]
    [InlineData("filter where station=north checked on", "UnreadWords: checked belongs to no condition: conditions are joined by and.")]
    [InlineData("filter off now", "UnreadWords: filter off takes no words after it.")]
    [InlineData("filter stations where altitude>1", "OtherGrid: The grid behind the line is of readings (manual), and stations is another table: go to its grid first.")]
    [InlineData("filter automatic where unit=m3", "OtherGrid: The grid behind the line is of readings (manual), and automatic is another grid: go to it first.")]
    public void Filter_refuses_by_name_what_it_cannot_ask_the_page_for(string line, string read)
    {
        var rig = new Rig();

        var preview = rig.Preview(line, new LineView(Stations.Readings, "manual", Order: rig.Order("manual", [])));

        Assert.Equal((read, null), (Read(preview), preview.Filter));
        Assert.Equal("NoView: filter narrows the grid on screen, and no grid of a table is behind this line: go to one first.", Read(rig.Preview("filter where unit=m3")));
    }

    [Theory] // ADR-0016 note 41: after filter, `where` and `off` are offered, and after `where` what the selector's reader awaits in the grid's table
    [InlineData("filter", "")]
    [InlineData("filter ", "where | off")]
    [InlineData("filter wh", "where@7")]
    [InlineData("filter where ", "id | station | volume | unit | taken | checked | note | wind speed | wind direction | window | drift")]
    [InlineData("filter where u", "unit@13")]
    [InlineData("filter where wind ", "wind speed@13 | wind direction@13")]
    [InlineData("filter where unit ", "= | != | ~ | in")]
    [InlineData("filter where unit=", "unit=M3@13 | unit=GAL@13")]
    [InlineData("filter where unit=m3 ", "and")]
    [InlineData("filter where unit=m3 and ch", "checked@25")]
    [InlineData("filter off ", "")]
    public void After_filter_where_and_off_then_the_conditions_of_the_grids_table_are_offered(string line, string offered)
    {
        // The stations' grid is behind the line in no case: the fields offered are the readings', the grid's own.
        var preview = new Rig().Preview(line, new LineView(Stations.Readings, "manual"));

        Assert.Equal(offered, string.Join(" | ", preview.Complete.Select(c => $"{c.Value}{(c.Start == line.Length ? "" : $"@{c.Start}")}")));
    }

    [Fact] // ADR-0016 note 41, §2: a view carries its grid's conditions: `shown` is the rows they leave, a position is one among those rows, and the view's sentence says how many
    public async Task Under_a_views_conditions_shown_and_positions_are_of_the_rows_they_leave()
    {
        var rig = new Rig();
        ViewSort[] largest = [new("volume", Descending: true)];
        // The readings in m3, as sent: R1, R2, R5, X1; largest first: R1, X1, R5, R2.
        var m3 = new LineView(Stations.Readings, Where: "unit=m3");
        var sorted = m3 with { Sorts = largest };
        var paged = sorted with { Order = rig.Order(sorted), Ranges = [new(1, 2)], Focus = new("note", Position: 3) };

        var shown = rig.Preview("set shown checked on", m3);
        var narrowed = rig.Preview("set shown where station=north checked on", m3);
        var selected = rig.Preview("set selected note wet", paged);
        var found = rig.Preview("find R5", paged);
        var hidden = rig.Preview("find R3", paged);
        var offered = rig.Preview("set ", m3);
        // A page that names the rows it shows and carries conditions shows the rows of those that meet them.
        var both = rig.Preview("set shown altitude 130", new LineView(Stations.Sites, Shown: ["S1", "X1"], Where: "altitude>20"));

        Assert.Equal(("readings, filtered to 4, nothing selected", ("readings", "R1, R2, R5, X1")), (shown.View, Asked(shown)));
        Assert.Equal(("readings", "R1, R2"), Asked(narrowed));
        Assert.Equal(("readings, filtered to 4, 2 selected, R2 · note", ("readings", "X1, R5")), (selected.View, Asked(selected)));
        Assert.Equal(("R5", 2), (found.Place!.Key, found.Place.Position));
        Assert.Equal("NotOnScreen: R3 is a row of readings that the filter of the grid on screen leaves out.", Read(hidden));
        Assert.Contains("shown (4 rows)", offered.Complete.Select(c => $"{c.Value} ({c.Detail})"));
        Assert.Equal(("stations, filtered to 1, nothing selected", ("stations", "X1")), (both.View, Asked(both)));

        // What runs is the rows as read: of the four, R2 and R5 were checked already, and no reading in gallons is written.
        Assert.Equal("Set checked to on on R1 and X1; 2 already held it.", (await rig.Run(shown))["said"]!.GetValue<string>());
        Assert.Equal(["reading:R1", "reading:X1"], rig.Board.Overrides.Keys.Select(t => t.Entity).Order(StringComparer.Ordinal));
    }

    [Fact] // ADR-0016 note 41: the token of an order under conditions moves when a row enters or leaves it, and only then; positions read against another are refused
    public async Task The_order_under_conditions_moves_when_a_row_enters_or_leaves()
    {
        var rig = new Rig();
        var m3 = new LineView(Stations.Readings, Where: "unit=m3");
        var before = rig.Order(m3);
        var selected = m3 with { Order = before, Ranges = [new(0, 1)] };
        var largest = m3 with { Sorts = [new("volume", Descending: true)] };
        var sortedBefore = rig.Order(largest);

        // An edit that moves no row in or out leaves the order as sent, and its token.
        await rig.Said("set R1 note wet");
        var still = rig.Order(m3);
        var held = rig.Preview("set selected checked on", selected);
        // Sorted, the reader's own order moves with a reading in gallons, which the conditions leave out: theirs does not.
        await rig.Said("set R3 volume 9");
        var sortedStill = rig.Order(largest);
        await rig.Said("set R1 unit gal");
        var after = rig.Order(m3);
        var moved = rig.Preview("set selected checked on", selected);

        Assert.Equal((before, ("readings", "R1")), (still, Asked(held)));
        Assert.Equal(sortedBefore, sortedStill);
        Assert.NotEqual(before, sortedBefore);
        Assert.NotEqual(before, after);
        Assert.NotEqual(rig.Order(null, []), before);
        Assert.Equal(("readings, no longer in the order it was shown in", true, "ViewMoved: The readings are no longer in the order they were selected in: select them again."),
            (moved.View, moved.ViewMoved, Read(moved)));
    }

    [Fact] // ADR-0016 note 41: conditions that can no longer be read show no row and say why, never every row; a line may still take the filter off
    public void A_views_conditions_that_cannot_be_read_are_refused_and_never_read_as_every_row()
    {
        var rig = new Rig();
        var unread = new LineView(Stations.Readings, "manual", Where: "colour=red");
        const string Why = "FilterUnread: The filter of readings (manual) can no longer be read (readings has no field colour; its fields are id, station, volume, unit, taken, "
            + "checked, note, wind speed, wind direction, window, drift): take it off with filter off.";

        var shown = rig.Preview("set shown checked on", unread);
        var found = rig.Preview("find R1", unread);
        var named = rig.Preview("set R1 checked on", unread);
        var off = rig.Preview("filter off", unread);
        var offered = rig.Preview("set ", unread);
        var paged = Assert.Throws<WorkspaceRefused>(() => GridRows.Paged(unread, rig.Register.Of(rig.Board), rig.Board));

        Assert.Equal((Why, "readings (manual), under a filter that can no longer be read", true), (Read(shown), shown.View, shown.ViewMoved));
        Assert.Equal(Why, Read(found));
        // A line that names its rows itself is read as it is.
        Assert.Equal("readings, 1 row | checked: off to on", Read(named));
        Assert.Equal("weather.readings manual: off | Took the filter off readings (manual).", Filtered(off));
        Assert.DoesNotContain(offered.Complete, c => c.Value == "shown");
        Assert.Contains("manual", offered.Complete.Select(c => c.Value));
        Assert.Equal(Why, $"{paged.Code}: {paged.Message}");
    }

    [Fact] // ADR-0016 note 41: the rows a view shows are read for a page that holds every row: their keys and how many the grid has, at the revision read
    public void The_rows_a_view_shows_are_read_for_a_page_that_holds_them()
    {
        var rig = new Rig();
        var ana = new Caller("ana", RunOrigin.Pricer);
        string Refused(LineView view, string user = "ana") => Assert.Throws<WorkspaceRefused>(() => rig.Lines.Shown(rig.Id, new Caller(user, RunOrigin.Pricer), view)).Code;

        var high = rig.Lines.Shown(rig.Id, ana, new LineView(Stations.Sites, Where: "altitude>20"));
        var manual = rig.Lines.Shown(rig.Id, ana, new LineView(Stations.Readings, "manual", Where: "station=north and checked=off"));
        var all = rig.Lines.Shown(rig.Id, ana, new LineView(Stations.Sites));
        var none = rig.Lines.Shown(rig.Id, ana, new LineView(Stations.Sites, Where: "altitude>9000"));

        Assert.Equal((Stations.Sites, rig.Board.Revision, "N1, X1", 3), (high.Table, high.Revision, string.Join(", ", high.Keys), high.Of));
        Assert.Equal(("R1", 3), (string.Join(", ", manual.Keys), manual.Of));
        Assert.Equal(("N1, S1, X1", 3), (string.Join(", ", all.Keys), all.Of));
        Assert.Equal((0, 3), (none.Keys.Count, none.Of));
        Assert.Equal("FilterUnread", Refused(new LineView(Stations.Sites, Where: "colour=red")));
        Assert.Equal("InvalidView", Refused(new LineView("weather.tides", Where: "altitude>20")));
        Assert.Equal("NotOwner", Refused(new LineView(Stations.Sites), "ben"));
    }
}
