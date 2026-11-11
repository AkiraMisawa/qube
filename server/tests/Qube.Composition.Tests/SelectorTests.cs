using Qube.Composition.Application.Tables;
using Qube.Composition.Application.Workspaces;

namespace Qube.Composition.Tests;

// ADR-0016 §2 and its notes 7 and 8 of 2026-11-11: one selector says which rows, the line is one spelling of it, and one
// resolver serves the line, the AI and a workflow. The rows are station readings, with no financial word.
public class SelectorTests
{
    static (IReadOnlyList<BoundTable> Tables, WorkspaceState Board) Rig()
    {
        var service = Stations.Service();
        var board = Stations.Board(service);
        return (new TableRegister([new Stations.Catalog()]).Of(board), board);
    }

    static SelectorReading Read(string text) => SelectorReader.Read(text, [.. Rig().Tables.Select(t => t.Descriptor)]);

    static ResolvedRows Resolve(string text)
    {
        var (tables, board) = Rig();
        var read = SelectorReader.Read(text, [.. tables.Select(t => t.Descriptor)]);
        Assert.Empty(read.Rest);
        return Selectors.Resolve(read.Selector, tables, board);
    }

    static string[] Keys(string text) => [.. Resolve(text).Rows.Select(r => r.Key)];

    static TableRefused Refused(string text) => Assert.Throws<TableRefused>(() => Resolve(text));

    [Fact] // ADR-0016 §2: rows by id, in the order named, whatever the case
    public void Rows_are_named_by_id() => Assert.Equal(["R3", "R1"], Keys("r3, R1"));

    [Fact] // ADR-0016 §2: the end of an id is accepted when it names exactly one row
    public void The_end_of_an_id_names_a_row_when_it_names_one() => Assert.Equal(["R5", "R3"], Keys("5, 3"));

    [Fact] // ADR-0016 §2: the end of an id that names several rows is refused with what it could mean, never the first
    public void The_end_of_an_id_that_names_several_rows_is_refused()
    {
        var refused = Refused("readings 1");

        Assert.Equal(("AmbiguousRow", "1 could be R1 or X1 of readings; write more of the id.", "1"), (refused.Code, refused.Message, refused.Word));
        Assert.Equal(["R1", "X1"], refused.Candidates);
    }

    [Fact] // ADR-0016 §2: ends and whole ids mix in one list, each found by itself, and an end no row has is refused by name
    public void Ends_and_whole_ids_mix_and_an_end_no_row_has_is_refused()
    {
        var unknown = Refused("readings 4, 9");

        Assert.Equal(["R4", "X1", "R2"], Keys("readings r4, x1, 2"));
        Assert.Equal(("UnknownRow", "No row 9 in readings.", "9"), (unknown.Code, unknown.Message, unknown.Word));
    }

    [Fact] // ADR-0016 §2: a row named twice is refused, since one row takes one value
    public void A_row_named_twice_is_refused() =>
        Assert.Equal(("RowTwice", "R1 is named twice; name each row once."), (Refused("R1, 5, r1").Code, Refused("R1, 5, r1").Message));

    [Fact] // note 7: rows named by id belong to the one table of the workspace that has such a row
    public void An_id_with_no_table_belongs_to_the_one_table_that_has_it()
    {
        var resolved = Resolve("N1, S1");

        Assert.Equal((Stations.Sites, 2), (resolved.Table.Descriptor.TableId, resolved.Rows.Count));
    }

    [Fact] // note 7: an id two tables have is refused with both, and so are ids of two tables
    public void An_id_two_tables_have_is_refused_with_both()
    {
        var both = Refused("X1");
        var across = Refused("R1, N1");
        var none = Refused("Q9");

        Assert.Equal(("AmbiguousRow", "X1 is a row of readings and of stations; say which, as in readings X1."), (both.Code, both.Message));
        Assert.Equal(["readings", "stations"], both.Candidates);
        Assert.Equal(("RowsOfTwoTables", "R1 is a row of readings and N1 of stations; a command takes the rows of one table."), (across.Code, across.Message));
        Assert.Equal(("UnknownRow", "No row Q9 in readings, stations or settings.", "Q9"), (none.Code, none.Message, none.Word));
        Assert.Equal(["X1"], Keys("stations X1"));
    }

    [Fact] // ADR-0016 §2, note 7: `all`, a subset, and a subset narrowed by `where`; with no table they mean the application's main one
    public void All_a_subset_and_a_subset_narrowed_by_where()
    {
        Assert.Equal(["R1", "R2", "R3", "R4", "R5", "X1"], Keys("all"));
        Assert.Equal(["R1", "R2", "R3"], Keys("manual"));
        Assert.Equal(["R1", "R2", "R3"], Keys("readings By Hand"));
        Assert.Equal(["R5", "X1"], Keys("auto where station=east"));
        Assert.Equal(["N1", "S1", "X1"], Keys("stations all"));
        Assert.Equal(["R1"], Keys("R1, R4 where station = north"));
    }

    [Fact] // ADR-0016 §2: a whole set is said as one, so a verb may write its one edit; narrowed, it is rows
    public void A_whole_set_is_said_as_one()
    {
        Assert.Equal(["manual", "automatic"], Resolve("all").Whole?.Select(s => s.Name));
        Assert.Equal(["automatic"], Resolve("auto").Whole?.Select(s => s.Name));
        Assert.Null(Resolve("auto where station=east").Whole);
        Assert.Null(Resolve("R1").Whole);
    }

    [Theory] // ADR-0016 §2, note 8: each operator, on a number, a date, a flag, a choice and an id; written with or without spaces
    [InlineData("where wind speed>=22", "R2,R5")]
    [InlineData("where wind speed < 5", "X1")]
    [InlineData("where wind speed != 12 and wind speed<=8", "R3,R4,X1")]
    [InlineData("where wind speed=12", "R1")]
    [InlineData("where wind speed in 5, 8,3", "R3,R4,X1")]
    [InlineData("where taken<2027-01-01", "R1,R4,X1")]
    [InlineData("where taken >= 2029-01-01", "R3,R5")]
    [InlineData("where taken=2026-11-20", "X1")]
    [InlineData("where checked=on", "R2,R5")]
    [InlineData("where ok != yes", "R1,R3,R4,X1")]
    [InlineData("where station=N", "R1,R2")]
    [InlineData("where station in south, east", "R3,R4,R5,X1")]
    [InlineData("where station!=north and checked=off", "R3,R4,X1")]
    [InlineData("where note=gusty", "R2")]
    [InlineData("where note = EMPTY", "R1,R3,R4,R5,X1")]
    [InlineData("where note!=none", "R2")]
    [InlineData("where wind direction~s", "R3,R4")]
    [InlineData("where id ~ r", "R1,R2,R3,R4,R5")]
    [InlineData("where window=1D", "R1,R3,R5,X1")]
    [InlineData("where unit=m3 and volume>1mm", "R1,X1")]
    [InlineData("where unit=gal and vol>=1", "R3,R4")]
    public void Where_reads_each_value_by_its_fields_form(string text, string rows) => Assert.Equal(rows, string.Join(",", Keys(text)));

    [Fact] // note 8: on a date a period counts from the table's date, and the reading says the date it means
    public void A_period_on_a_date_counts_from_the_tables_date()
    {
        var later = Resolve("where taken>5y");
        var sooner = Resolve("where date <= 1m and checked=off");

        Assert.Equal(["R3"], later.Rows.Select(r => r.Key));
        Assert.Equal(["taken > 2031-11-11"], later.Where);
        Assert.Equal(["R1", "X1"], sooner.Rows.Select(r => r.Key));
        Assert.Equal(["taken <= 2026-12-11", "checked = off"], sooner.Where);
    }

    [Fact] // ADR-0016 §2: an amount is not compared across units; pinned, or one unit among the rows asked, it is
    public void An_amount_whose_unit_differs_by_row_is_refused_unless_the_unit_is_pinned()
    {
        var mixed = Refused("where volume>1mm");

        Assert.Equal(("MixedUnits", "volume is in M3 and GAL here, and an amount is not compared across them; say which, as in where unit=M3 and volume>1mm."),
            (mixed.Code, mixed.Message));
        Assert.Equal(["R3"], Keys("where unit=gal and volume>1mm"));
        // R1 and R2 are both in M3, so nothing is compared across units.
        Assert.Equal(["R1"], Keys("R1, R2 where volume>1mm"));
        Assert.Equal(["volume > 1mm"], Resolve("R1, R2 where volume>1mm").Where);
        // No value is in no unit: asking which rows have none compares nothing across units.
        Assert.Empty(Keys("where volume=empty"));
        Assert.Equal(["R1", "R2", "R3"], Keys("manual where volume!=none"));
        Assert.Equal(["R2", "R5"], Keys("where unit=m3 and volume in 250k, 800k"));
    }

    [Theory] // note 8: an operator a field cannot take, a value its form cannot read and a field it has not are refused by name
    [InlineData("where station<north", "UnorderedField", "station is text, which takes =, !=, ~ and in; < is for numbers and dates.")]
    [InlineData("where checked>off", "UnorderedField", "checked is a flag, which takes =, != and in; > is for numbers and dates.")]
    [InlineData("where wind speed~2", "NotText", "wind speed is a number, and ~ is for text.")]
    [InlineData("where checked=maybe", "InvalidValue", "checked is on or off, or empty; maybe is not.")]
    [InlineData("where taken>soon", "InvalidValue", "taken is a period such as 10y, or a date, or empty; soon is not.")]
    [InlineData("where colour=red", "UnknownField", "readings has no field colour; its fields are id, station, volume, unit, taken, checked, note, wind speed, wind direction, window, drift.")]
    [InlineData("where wind=3", "AmbiguousField", "wind could be wind speed or wind direction of readings.")]
    [InlineData("where note==wet", "UnknownOperator", "== is no operator: a condition takes =, !=, <, <=, >, >=, ~ or in.")]
    [InlineData("where wind speed=>5", "UnknownOperator", "=> is no operator: a condition takes =, !=, <, <=, >, >=, ~ or in.")]
    [InlineData("where taken<empty", "UnorderedValue", "taken < empty asks for an order, and what is empty has none; write taken=empty or taken!=empty.")]
    [InlineData("where unit=m3 and volume in 1,500k", "AmbiguousValues", "1,500k could be one value or two after in: write two as 1, 500k and one without its separator, 1500k.")]
    public void A_condition_that_cannot_be_read_is_refused_by_name(string text, string code, string message) =>
        Assert.Equal((code, message), (Refused(text).Code, Refused(text).Message));

    [Theory] // note 8: a condition is a field, an operator and a value; one that is not says how one is written
    [InlineData("where station")]
    [InlineData("where station=")]
    [InlineData("where =north")]
    [InlineData("where station=north and")]
    [InlineData("where station in")]
    public void A_condition_that_is_not_one_says_how_one_is_written(string text)
    {
        var refused = Assert.Throws<TableRefused>(() => Read(text));

        Assert.Equal("UnreadCondition", refused.Code);
        Assert.EndsWith(": a condition is a field, an operator (=, !=, <, <=, >, >=, ~, in) and a value, and conditions are joined by and.", refused.Message);
    }

    [Fact] // note 7: rows that are not said are refused with the ways there are; a table of one row needs none
    public void Rows_that_are_not_said_are_refused_and_a_table_of_one_row_needs_none()
    {
        var none = Refused("readings");
        var nothing = Refused("");

        Assert.Equal(("NoRows", "Say which readings: their ids, all, manual, automatic, or where and a condition."), (none.Code, none.Message));
        Assert.Equal(("NoRows", "Say which rows: their ids, all, manual, automatic, or where and a condition; or name a table (readings, stations, settings)."), (nothing.Code, nothing.Message));
        Assert.Equal(["board"], Keys("settings"));
        Assert.Equal(["board"], Keys("Board"));
    }

    [Fact] // ADR-0016 §2: the selector is a typed value, and the line one spelling of it; the AI and a workflow pass it as it is
    public void The_typed_selector_resolves_as_the_line_does()
    {
        var (tables, board) = Rig();

        var manual = Selectors.Resolve(new RowSelector(Table: Stations.Readings, Subset: "manual", Where: [new("station", "=", "south")]), tables, board);
        var unknown = Assert.Throws<TableRefused>(() => Selectors.Resolve(new RowSelector(Table: "tides", All: true), tables, board));
        var subset = Assert.Throws<TableRefused>(() => Selectors.Resolve(new RowSelector(Subset: "nightly"), tables, board));

        var twice = Assert.Throws<TableRefused>(() => Selectors.Resolve(new RowSelector(Table: Stations.Readings, All: true, Subset: "manual"), tables, board));

        Assert.Equal(["R3"], manual.Rows.Select(r => r.Key));
        Assert.Equal(("RowsSaidTwice", "Rows are said as all or as a subset, not both."), (twice.Code, twice.Message));
        Assert.Equal("UnknownTable", unknown.Code);
        Assert.Equal(("UnknownSubset", "readings has no subset nightly; its subsets are manual, automatic."), (subset.Code, subset.Message));
    }

    [Fact] // ADR-0016 §2, §5: the line's reader takes the rows and leaves the rest of the line to the verb
    public void The_reader_takes_the_rows_and_leaves_the_rest()
    {
        var ids = Read("R1, R2 note gusty");
        var subset = Read("manual checked on");
        var where = Read("where station=east and wind speed > 5 checked on");
        var list = Read("where station in south, east note=wet");
        var table = Read("stations N1 altitude 130");
        var one = Read("alarm 5");
        var named = Read("settings alarm 5");

        Assert.Equal("R1 R2 | note gusty", $"{string.Join(' ', ids.Selector.Ids)} | {string.Join(' ', ids.Rest)}");
        Assert.Equal("manual | checked on", $"{subset.Selector.Subset} | {string.Join(' ', subset.Rest)}");
        Assert.Equal([new Condition("station", "=", "east"), new Condition("wind speed", ">", "5")], where.Selector.Where);
        Assert.Equal(["checked", "on"], where.Rest);
        Assert.Equal([new Condition("station", "in", "south, east")], list.Selector.Where);
        Assert.Equal(["note=wet"], list.Rest);
        Assert.Equal($"{Stations.Sites} N1 | altitude 130", $"{table.Selector.Table} {string.Join(' ', table.Selector.Ids)} | {string.Join(' ', table.Rest)}");
        // A table of one row is addressed without a row where a field's words say which.
        Assert.True(one.Selector.SaysNoRows);
        Assert.Null(one.Selector.Table);
        Assert.Equal(["alarm", "5"], one.Rest);
        Assert.Equal($"{Stations.Settings} | alarm 5", $"{named.Selector.Table} | {string.Join(' ', named.Rest)}");
    }
}
