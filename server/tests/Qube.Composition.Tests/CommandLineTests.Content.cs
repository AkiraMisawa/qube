using Qube.Composition.Application;
using Qube.Composition.Application.Tables;
using Qube.Composition.Application.Workspaces;

namespace Qube.Composition.Tests;

// ADR-0016 note 44 of 2026-11-11: `copy` and `export` are verbs of the screen that take rows out of the workbench. A
// reading says the rows and the fields; the content is asked for at Enter, read from the same line at the reading's
// revision, and written for a spreadsheet.
public partial class CommandLineTests
{
    static readonly Caller Ana = new("ana", RunOrigin.Pricer);

    // What a reading of `copy` or `export` asks the page for, and what Enter would do.
    static string Taking(LinePreview preview) => preview.Asks switch
    {
        LineCopy copy => $"copy | {copy.Said}",
        LineExport export => $"export {export.FileName} | {export.Said}",
        _ => "none",
    };

    // The content of a line, as the page is answered it at Enter; its line breaks and tabs written so that a test reads.
    static string Content(Rig rig, string line, LineView? view = null) =>
        rig.Lines.Content(line, rig.Id, null, Ana, view) is { Text: { } text } ? text.Replace("\r\n", "⏎").Replace("\t", "→") : "none";

    [Fact] // ADR-0016 note 44: copy's reading says the rows and the fields and names no command; its content is the cells, tabs between them, in the grid's order where the server pages it
    public void Copy_reads_rows_and_fields_and_its_content_is_cells_a_spreadsheet_reads()
    {
        var rig = new Rig();
        var before = rig.Board.Revision;
        // The manual readings, largest first: R3, R1, R2.
        ViewSort[] largest = [new("volume", Descending: true)];
        var manual = new LineView(Stations.Readings, "manual", Sorts: largest, Order: rig.Order("manual", largest), Ranges: [new(0, 2)]);

        var selected = rig.Preview("copy selected volume, unit", manual);
        var leftOut = rig.Preview("copy volume, unit", manual);
        var whole = rig.Preview("copy R2");
        var named = rig.Preview("copy where station=north id, wind speed");

        Assert.Equal((null, null), (selected.Command, selected.Inputs));
        Assert.Equal("copy | Copied 2 rows of readings, 2 fields: volume, unit.", Taking(selected));
        Assert.Equal(("readings, 2 rows | Copies 2 rows of readings, 2 fields: volume, unit.", "rows: selected (Typed); fields: volume, unit (Typed)", "R3|R1"),
            (Read(selected), Given(selected), string.Join("|", selected.Rows!.First)));
        Assert.Equal("12000000→GAL⏎5000000→M3", Content(rig, "copy selected volume, unit", manual));
        // Rows left out are the Selection, as `set` reads them.
        Assert.Equal(("copy | Copied 2 rows of readings, 2 fields: volume, unit.", "rows: R3, R1 (Context); fields: volume, unit (Typed)"), (Taking(leftOut), Given(leftOut)));
        Assert.Equal("12000000→GAL⏎5000000→M3", Content(rig, "copy volume, unit", manual));
        // With no field named, every field of the table, the row's key first; no value is an empty cell.
        Assert.Equal(("copy | Copied R2 of readings, every field (11).", "readings, 1 row | Copies R2 of readings, every field (11)."), (Taking(whole), Read(whole)));
        Assert.Equal("R2→NORTH→250000→M3→2027-06-30→true→gusty→30→NE→1w→", Content(rig, "copy R2"));
        // Rows a line names itself need no grid, and come in the table's order.
        Assert.Equal("copy | Copied 2 rows of readings, 2 fields: id, wind speed.", Taking(named));
        Assert.Equal("R1→12⏎R2→30", Content(rig, "copy where station=north id, wind speed"));
        // `shown` comes in the order the grid shows.
        Assert.Equal("R3⏎R1⏎R2", Content(rig, "copy shown id", manual));
        Assert.Equal("id→note⏎R2→gusty", Content(rig, "copy R2 id, note with headers"));
        Assert.Equal("board→10000000→true", Content(rig, "copy settings"));
        Assert.Equal(before, rig.Board.Revision);
        Assert.DoesNotContain(rig.Registry.CapabilityDescriptors, c => c.CapabilityId.Contains("copy", StringComparison.Ordinal) || c.CapabilityId.Contains("export", StringComparison.Ordinal));
    }

    [Fact] // ADR-0016 note 44: export's reading names the file; its content is RFC 4180 with a header line and a byte order mark; a table named alone is every row of it
    public void Export_names_a_file_and_its_content_is_csv_with_a_header_line()
    {
        var rig = new Rig();
        ViewSort[] largest = [new("volume", Descending: true)];
        var manual = new LineView(Stations.Readings, "manual", Sorts: largest, Order: rig.Order("manual", largest));

        var table = rig.Preview("export stations");
        var grid = rig.Preview("export", manual);
        var some = rig.Preview("export readings where unit=gal id, volume");

        Assert.Equal((null, null), (table.Command, table.Inputs));
        Assert.Equal("export stations.csv | Exported 3 rows of stations to stations.csv, every field (3).", Taking(table));
        Assert.Equal(("stations, 3 rows | Exports 3 rows of stations to stations.csv, every field (3).", "rows: stations (Typed)"), (Read(table), Given(table)));
        Assert.Equal("\uFEFFid,altitude,backup⏎N1,120,⏎S1,15,N1⏎X1,40,⏎", Content(rig, "export stations"));
        // With no word, the rows the grid behind the line shows, in its order.
        Assert.Equal(("export readings.csv | Exported 3 rows of readings to readings.csv, every field (11).", "rows: shown (Context)"), (Taking(grid), Given(grid)));
        Assert.Equal("\uFEFFid,volume⏎R3,12000000⏎R1,5000000⏎R2,250000⏎", Content(rig, "export id, volume", manual));
        Assert.Equal("export readings.csv | Exported 2 rows of readings to readings.csv, 2 fields: id, volume.", Taking(some));
        Assert.Equal("\uFEFFid,volume⏎R3,12000000⏎R4,1000000⏎", Content(rig, "export readings where unit=gal id, volume"));
        Assert.Equal("\uFEFFid,alarm volume,daylight⏎board,10000000,true⏎", Content(rig, "export settings"));
    }

    [Fact] // ADR-0016 note 44: a cell that holds the separator, a quote or a line break is quoted, its quotes doubled; a rate is written in percent
    public async Task A_cell_is_quoted_where_it_must_be_and_a_rate_is_written_in_percent()
    {
        var rig = new Rig();
        await rig.Said("set R1 note a\"b");
        await rig.Said("set R1 drift 2.5%");
        await rig.Said("set R2 note=a,b");

        // Tabs between cells: a comma needs no quotes there, and a quote does.
        Assert.Equal("\"a\"\"b\"→2.5%⏎a,b→", Content(rig, "copy R1, R2 note, drift"));
        Assert.Equal("\uFEFFnote,drift⏎\"a\"\"b\",2.5%⏎\"a,b\",⏎", Content(rig, "export readings R1, R2 note, drift"));
    }

    [Theory] // ADR-0016 note 44: copy and export refuse by name rows that are not said, a field the table has not, a field named twice and conditions no row meets
    [InlineData("copy", "NoRows: Say which rows: their ids, all, manual, automatic, or where and a condition; or name a table (readings, stations, settings).")]
    [InlineData("copy readings", "NoRows: Say which readings: their ids, all, manual, automatic, or where and a condition.")]
    [InlineData("copy selected", "NoView: selected means the rows selected on a screen, and no screen is behind this command: name the rows by their ids, all, a subset or where.")]
    [InlineData("copy R1 colour", "UnknownField: readings has no field colour; its fields are id, station, volume, unit, taken, checked, note, wind speed, wind direction, window, drift.")]
    [InlineData("copy R1 wind", "AmbiguousField: wind could be wind speed or wind direction of readings.")]
    [InlineData("copy R1 note, note", "FieldTwice: note is named twice; name each field once.")]
    [InlineData("copy R1 note,", "NoField: A field is named after each comma.")]
    [InlineData("copy where unit=m3 and volume>100bn", "NoRowMatches: No row of readings matches unit = M3 and volume > 100bn: there is nothing to copy.")]
    [InlineData("export", "NoRows: Say which rows: their ids, all, manual, automatic, or where and a condition; or name a table (readings, stations, settings).")]
    [InlineData("export tides", "UnknownRow: No row tides in readings, stations or settings.")]
    // After a table's word, a word that names no field of it is read as a row's id.
    [InlineData("export stations colour", "UnknownRow: No row colour in stations: its rows are N1, S1, X1.")]
    [InlineData("export stations altitude, colour", "UnknownField: stations has no field colour; its fields are id, altitude, backup.")]
    [InlineData("export readings where station=east and unit=gal", "NoRowMatches: No row of readings matches station = EAST and unit = GAL: there is nothing to export.")]
    public void Copy_and_export_refuse_by_name_what_they_cannot_take_out(string line, string read)
    {
        var rig = new Rig();

        var preview = rig.Preview(line);

        Assert.Equal((read, null), (Read(preview), preview.Asks));
        // What a reading refuses, the content refuses the same way: no text is answered.
        Assert.Equal((read, null), (rig.Lines.Content(line, rig.Id, null, Ana) is { Refused: { } refused } content ? ($"{refused.Code}: {refused.Sentence}", content.Text) : ("none", "none")));
    }

    [Fact] // ADR-0016 note 44: more cells than the most a line takes out are refused with the count, never cut; and a line that takes nothing out has no content
    public void More_cells_than_the_most_are_refused_and_a_line_of_another_verb_has_no_content()
    {
        var rig = new Rig();
        var few = new CommandLines(rig.Registry, rig.Workspaces, rig.Register, [new Stations.ShortForms()], mostCells: 20);

        var over = few.Preview("copy all id, volume, unit, note", rig.Id, null, Ana);
        var under = few.Preview("copy all id, volume, unit", rig.Id, null, Ana);
        var other = rig.Lines.Content("set R1 note wet", rig.Id, null, Ana);

        Assert.Equal("TooManyCells: 6 rows of 4 fields are 24 cells, and copy takes 20 at most: name fewer rows or fewer fields.", Read(over));
        Assert.IsType<LineCopy>(under.Asks);
        Assert.Equal(("NoContent: `set R1 note wet` takes nothing out of the workbench: copy and export do.", null), ($"{other.Refused!.Code}: {other.Refused.Sentence}", other.Text));
    }

    [Fact] // ADR-0016 note 44, §5: the content is read at the revision the reading named: a workspace that has moved is refused, and another user's is not read
    public async Task The_content_is_of_the_revision_read_and_of_the_callers_own_workspace()
    {
        var rig = new Rig();
        var read = rig.Preview("copy R1 volume");
        await rig.Said("set R1 volume 9mm");

        var moved = Assert.Throws<WorkspaceRefused>(() => rig.Lines.Content("copy R1 volume", rig.Id, read.Revision, Ana));
        var others = Assert.Throws<WorkspaceRefused>(() => rig.Lines.Content("copy R1 volume", rig.Id, null, new Caller("ben", RunOrigin.Pricer)));

        Assert.Equal(("RevisionMoved", "NotOwner"), (moved.Code, others.Code));
        Assert.Equal("9000000", Content(rig, "copy R1 volume"));
    }

    [Theory] // ADR-0016 notes 15 and 44: after copy and export, the ways to say rows, then the table's fields, again after a comma, whether or not a command sets them
    [InlineData("copy", "")]
    [InlineData("copy sh", "shown (3 rows)@5")]
    [InlineData("copy selected ", "id | station | volume | unit | taken | checked | note | wind speed | wind direction | window | drift | where")]
    [InlineData("copy selected ta", "taken@14")]
    [InlineData("copy selected volume, wind s", "wind speed@22")]
    [InlineData("copy selected volume w", "with headers@21")]
    [InlineData("export st", "stations@7")]
    [InlineData("export stations a", "all@16 | altitude@16")]
    public void After_copy_and_export_rows_then_fields_are_offered(string line, string offered)
    {
        var rig = new Rig();

        var preview = rig.Preview(line, new LineView(Stations.Readings, "manual", Order: rig.Order("manual", [])));

        Assert.Equal(offered, string.Join(" | ", preview.Complete.Select(c => $"{c.Value}{(c.Detail is null ? "" : $" ({c.Detail})")}{(c.Start == line.Length ? "" : $"@{c.Start}")}")));
    }
}
