using System.Text.Json.Nodes;
using Qube.Composition.Application;
using Qube.Composition.Application.Products;
using Qube.Composition.Application.Tables;
using Qube.Composition.Application.Workspaces;

namespace Qube.Composition.Tests;

// ADR-0016 §1 and its notes 46 to 48 of 2026-11-11: a table under a row, a figure of the rows under a row, and the
// rows a command makes and removes there. The registration is a station's sensors, with no financial word.
public class RowsUnderARowTests
{
    sealed class Rig
    {
        public WorkspaceService Workspaces { get; } = Stations.Service();
        public TableRegister Register { get; } = new([new Stations.Catalog(sensors: true)]);
        public CommandService Commands { get; }
        public CommandLines Lines { get; }
        public string Id { get; }

        public Rig(bool described = true, ProductDescriptor? sensor = null)
        {
            var tables = new TableService(Register, Workspaces);
            var products = described
                ? new ProductService([new Stations.Products(sensor)], [new Stations.SensorRules(Workspaces)], Workspaces, Register)
                : new ProductService([], [], Workspaces, Register);
            var registry = new CompositionRegistry([new TablesModule(() => tables), new WorkspaceCommandsModule(() => Workspaces), new ProductsModule(() => products)]);
            Commands = new CommandService(registry, new InMemoryDataStore());
            Lines = new CommandLines(registry, Workspaces, Register, []);
            Id = Stations.Board(Workspaces).WorkspaceId;
        }

        public WorkspaceState Board => Workspaces.Find(Id)!;

        public ResolvedRows Rows(string rows)
        {
            var tables = Register.Of(Board);
            var read = SelectorReader.Read(rows, [.. tables.Select(t => t.Descriptor)]);
            Assert.Empty(read.Rest);
            return Selectors.Resolve(read.Selector, tables, Board);
        }

        public string[] Keys(string rows) => [.. Rows(rows).Rows.Select(r => r.Key)];

        public TableRefused RowsRefused(string rows) => Assert.Throws<TableRefused>(() => Keys(rows));

        public LinePreview Preview(string line) => Lines.Preview(line, Id, null, new Caller("ana", RunOrigin.Pricer));

        public async Task<JsonObject> Run(string line)
        {
            var preview = Preview(line);
            Assert.True(preview.Refused is null, preview.Refused?.Sentence);
            var result = await Commands.InvokeAsync(preview.Command!.Id, preview.Command.Version, preview.Inputs!, new Caller("ana", RunOrigin.Pricer), Guid.NewGuid().ToString(), default);
            return result is CommandResult.Done done ? done.Outputs.Values.Single()!.AsObject()
                : throw new Xunit.Sdk.XunitException($"{line} was refused: {string.Join(" ", ((CommandResult.Refused)result).Issues.Select(i => i.Message))}");
        }

        /// <summary>A station's sensors as they stand, each as its key, load and unit.</summary>
        public string[] Sensors(string station) =>
        [
            .. Register.Of(Board).Single(t => t.Descriptor.TableId == Stations.Sensors).Table.Rows(Board).Where(r => r.Under == station)
                .Select(r => $"{r.Key} {r.Value("load")!.GetValue<decimal>():0.##} {r.Value("unit")!.GetValue<string>()}"),
        ];

        public decimal Load(string station) =>
            Register.Of(Board).Single(t => t.Descriptor.TableId == Stations.Sites).Table.Rows(Board).Single(r => r.Key == station).Value("load")!.GetValue<decimal>();
    }

    [Fact] // ADR-0016 note 46: a row under a row is named by its whole key, or by how it ends, as any row is
    public void A_row_under_a_row_is_named_by_its_key() => Assert.Equal(["S1/1", "N1/2"], new Rig().Keys("sensors S1/1, 1/2"));

    [Fact] // ADR-0016 note 46: in a table under another, the id of a row above names every row under it
    public void The_id_of_the_row_above_names_every_row_under_it()
    {
        var rig = new Rig();

        Assert.Equal(["N1/1", "N1/2"], rig.Keys("sensors N1"));
        Assert.Equal(["S1/1", "N1/1", "N1/2"], rig.Keys("sensors S1, N1"));
    }

    [Fact] // ADR-0016 note 46: a row above with nothing under it matches nothing, and is said so
    public void A_row_above_with_nothing_under_it_is_refused()
    {
        var refused = new Rig().RowsRefused("sensors X1");

        Assert.Equal(("NoRowMatches", "X1 has no sensors.", "X1"), (refused.Code, refused.Message, refused.Word));
    }

    [Fact] // ADR-0016 note 46: an id that names neither a row of the table nor one above it is refused as any unknown row is
    public void An_id_of_neither_is_refused()
    {
        var refused = new Rig().RowsRefused("sensors Q9");

        Assert.Equal(("UnknownRow", "Q9"), (refused.Code, refused.Word));
    }

    [Fact] // ADR-0016 notes 7 and 46: with no table said, an id is looked for among each table's own rows alone
    public void With_no_table_an_id_names_a_row_of_its_own_table()
    {
        var rig = new Rig();

        Assert.Equal(["N1/2"], rig.Keys("N1/2"));
        // N1 is a station, and never its sensors: those are said with their table.
        var station = rig.Rows("N1");
        Assert.Equal((Stations.Sites, "N1"), (station.Table.Descriptor.TableId, Assert.Single(station.Rows).Key));
    }

    [Theory] // ADR-0016 note 46: the registration is checked when the tables are read, by name
    [InlineData("weather.nowhere", false, false, "Table weather.sensors is under weather.nowhere, which this workspace has not.")]
    [InlineData(Stations.Sites, true, false, "Table weather.sensors is under another and cannot be the main table.")]
    [InlineData(Stations.Sites, false, true, "Table weather.sensors is under another and cannot be a table of one row.")]
    public void A_table_under_another_is_checked_when_the_tables_are_read(string under, bool main, bool single, string message)
    {
        var service = Stations.Service();
        // The readings are the main table of the board: one that is not is described for the fault of two.
        var register = new TableRegister([new Stations.Catalog(sensorsAs: Stations.SensorsTable with { Under = under, Main = main, SingleRow = single })]);

        var thrown = Assert.Throws<InvalidOperationException>(() => register.Of(Stations.Board(service)));

        Assert.Equal(message, thrown.Message);
    }

    [Fact] // ADR-0016 note 47: a figure is read as a number worked out from the rows under its row
    public void A_figure_is_read_from_the_rows_under_its_row()
    {
        var rig = new Rig();

        // N1's two sensors, 40 and 25 cubic metres; S1's one of 10 gallons, each four cubic metres.
        Assert.Equal((65m, 40m, 0m), (rig.Load("N1"), rig.Load("S1"), rig.Load("X1")));
    }

    [Fact] // ADR-0016 note 47: a condition compares a figure as the number it reads as
    public void A_condition_compares_a_figure_as_a_number()
    {
        var rig = new Rig();

        Assert.Equal(["N1"], rig.Keys("stations where load>50"));
        Assert.Equal(["S1"], rig.Keys("stations where load=40"));
    }

    [Theory] // ADR-0016 note 47 (corrected in review): a number with a unit that has no unit to be in fails when the tables are read, by name
    [InlineData(false)]
    [InlineData(true)]
    public void Money_with_nothing_it_may_be_in_is_checked_when_the_tables_are_read(bool figure)
    {
        TableField load = figure
            ? new("load", "Load", ["load"], FieldForm.Number, Editable: true, Unit: "M3", Figure: new(Stations.Sensors, new(FieldForm.Money)))
            : new("load", "Load", ["load"], FieldForm.Money, Editable: true);
        var register = new TableRegister([new Stations.Catalog(Stations.SitesTable with { Fields = [.. Stations.SitesTable.Fields, load] }, sensors: true)]);

        var thrown = Assert.Throws<InvalidOperationException>(() => register.Of(Stations.Board(Stations.Service())));

        Assert.Equal("Table weather.stations's field load is a number with what it is in, and has nothing it may be in.", thrown.Message);
    }

    [Fact] // ADR-0016 note 47: a write of a figure is the changes its table answers, as one command; for one row the line says the table's own sentence
    public async Task Writing_a_figure_writes_what_its_table_answers()
    {
        var rig = new Rig();

        Assert.Equal("load: 2 sensors replaced by 1 sensor, GAL", rig.Preview("set N1 load 30 gal").Would);
        var told = await rig.Run("set N1 load 30 gal");

        Assert.Equal(("Set N1 load to 30 GAL.", 1, 0), (told["said"]!.GetValue<string>(), told["changed"]!.GetValue<int>(), told["held"]!.GetValue<int>()));
        Assert.Equal(["N1/3 30 GAL"], rig.Sensors("N1"));
        Assert.Equal(120m, rig.Load("N1"));
        // One command: undo brings both sensors back.
        await rig.Run("undo");
        Assert.Equal(["N1/1 40 M3", "N1/2 25 M3"], rig.Sensors("N1"));
    }

    [Fact] // ADR-0016 note 47: written alone, the unit is the table's to supply
    public async Task A_figure_written_alone_is_in_the_tables_own_unit()
    {
        var rig = new Rig();

        await rig.Run("set N1 load 30");

        Assert.Equal(["N1/3 30 M3"], rig.Sensors("N1"));
    }

    [Fact] // ADR-0016 note 47: a row whose write changes nothing is counted as holding the value, whatever the figure reads
    public async Task A_row_whose_write_changes_nothing_holds_the_value()
    {
        var rig = new Rig();

        // S1 holds one sensor of 10 gallons already: its load reads 40, and writing 10 gallons changes nothing.
        Assert.Equal("Nothing would change: S1 already holds load 10 GAL.", rig.Preview("set S1 load 10 gal").Would);
        var both = rig.Preview("set stations S1, N1 load 10 gal");
        Assert.Equal("load: 1 to 10 GAL, 1 already 10 GAL", both.Would);

        var told = await rig.Run("set stations S1, N1 load 10 gal");
        Assert.Equal(("Set N1 load to 10 GAL; 1 already held it.", 1, 1), (told["said"]!.GetValue<string>(), told["changed"]!.GetValue<int>(), told["held"]!.GetValue<int>()));
        Assert.Equal(["S1/1 10 GAL"], rig.Sensors("S1"));
    }

    [Fact] // ADR-0016 note 47: a value that is not written as the figure's write is refused with what it takes, naming the value
    public void A_figure_written_in_no_unit_it_takes_is_refused()
    {
        var refused = new Rig().Preview("set N1 load 30 litres").Refused!;

        Assert.Equal(("InvalidValue", "N1 load is a number, alone or followed by what it is in: M3 or GAL; 30 litres is not.", "30 litres"), (refused.Code, refused.Sentence, refused.Word));
    }

    [Fact] // ADR-0016 note 47: reset of a figure is the table's too, and brings back the rows that were sent
    public async Task Resetting_a_figure_brings_back_the_rows_under_it()
    {
        var rig = new Rig();
        await rig.Run("set N1 load 30 gal");

        var told = await rig.Run("reset N1 load");

        Assert.Equal("Reset N1 load.", told["said"]!.GetValue<string>());
        Assert.Equal(["N1/1 40 M3", "N1/2 25 M3"], rig.Sensors("N1"));
    }

    [Fact] // ADR-0016 notes 46 and 48: a row under a row is set and removed as any row is; one that was sent is taken out by an edit, which undo takes back
    public async Task A_row_under_a_row_is_set_and_removed()
    {
        var rig = new Rig();

        await rig.Run("set sensors N1/2 load 5");
        await rig.Run("remove sensors N1/1");

        Assert.Equal(["N1/2 5 M3"], rig.Sensors("N1"));
        Assert.Equal(5m, rig.Load("N1"));
        await rig.Run("undo");
        Assert.Equal(["N1/1 40 M3", "N1/2 5 M3"], rig.Sensors("N1"));
    }

    [Theory] // ADR-0016 note 47: a figure of a table that is not under its own, or of none, fails when the tables are read, by name
    [InlineData(Stations.Readings, "Table weather.stations's field load is a figure of weather.readings, which is not under it.")]
    [InlineData("weather.nowhere", "Table weather.stations's field load is a figure of weather.nowhere, which is not under it.")]
    public void A_figure_is_checked_when_the_tables_are_read(string of, string message)
    {
        var stations = Stations.SitesTable with { Fields = [.. Stations.SitesTable.Fields, new("load", "Load", ["load"], FieldForm.Number, Figure: new(of))] };
        var register = new TableRegister([new Stations.Catalog(stations, sensors: true)]);

        var thrown = Assert.Throws<InvalidOperationException>(() => register.Of(Stations.Board(Stations.Service())));

        Assert.Equal(message, thrown.Message);
    }

    [Theory] // ADR-0016 note 48: after the product's words, the first word is the row above where it names one, whole or by how it ends, in any case
    [InlineData("new sensor N1 rain 30 gal")]
    [InlineData("new sensor n1 30 rain gal")]
    [InlineData("new sensor rain 30 gal station=N1")]
    public async Task New_makes_a_row_under_the_row_named(string line)
    {
        var rig = new Rig();

        var preview = rig.Preview(line);
        Assert.Equal("Makes N1/3: Sensor.", preview.Would);
        Assert.Equal("station N1|kind rain|load 30|unit GAL", string.Join("|", preview.Arguments!.Where(a => a.Name != "product").Select(a => $"{a.Name} {a.Text}")));
        await rig.Run(line);

        Assert.Equal(["N1/1 40 M3", "N1/2 25 M3", "N1/3 30 GAL"], rig.Sensors("N1"));
    }

    [Fact] // ADR-0016 note 48: the row above is written into the terms by its name and its whole key, so what is invoked names the row exactly
    public void The_request_names_the_row_above_by_its_whole_key() =>
        Assert.Equal("station=S1 rain 30 gal", new Rig().Preview("new sensor s1 rain 30 gal").Inputs!["request"]!["terms"]!.GetValue<string>());

    [Theory] // ADR-0016 note 48 (corrected in review): the term typed by a name of its own names the row as the first word does, whole or by how it ends, and is written by the term's name and the whole key
    [InlineData("new sensor rain 30 gal station=s1", "rain 30 gal station=S1")]
    [InlineData("new sensor at=s1 rain 30 gal", "station=S1 rain 30 gal")]
    public void The_term_typed_by_its_name_names_the_row_as_the_first_word_does(string line, string terms)
    {
        var sensor = Stations.SensorProduct with
        {
            Fields = [.. Stations.SensorProduct.Fields.Select(f => f.Name == "station" ? f with { Aliases = ["at"] } : f)],
        };

        Assert.Equal(terms, new Rig(sensor: sensor).Preview(line).Inputs!["request"]!["terms"]!.GetValue<string>());
    }

    [Fact] // ADR-0016 note 48 (corrected in review): a name that could be several rows above is refused with them, typed by the term's name as it is as the first word
    public void A_named_term_that_could_be_several_rows_above_is_refused()
    {
        var refused = new Rig().Preview("new sensor rain 30 gal station=1").Refused!;

        Assert.Equal("AmbiguousRow", refused.Code);
    }

    [Fact] // ADR-0016 note 36: `new` is the kernel's and is read on every workspace; where the application describes no product, it says so
    public void New_on_a_workspace_whose_application_describes_no_product_is_refused_by_name()
    {
        var refused = new Rig(described: false).Preview("new sensor s1 rain 30 gal").Refused!;

        Assert.Equal(("NoProduct", "new makes nothing in this workspace: no product is described for it."), (refused.Code, refused.Sentence));
    }

    [Fact] // ADR-0016 note 48: with no row above named, the product is refused as missing that term, as any required term is
    public void With_no_row_above_the_term_is_missing()
    {
        var refused = new Rig().Preview("new sensor rain 30 gal").Refused!;

        Assert.Equal(("MissingTerm", "sensor still needs station: type it, or set a default for it."), (refused.Code, refused.Sentence));
    }

    [Fact] // ADR-0016 note 48: a first word whose end names several rows above is refused with those it could be
    public async Task A_first_word_that_could_be_several_rows_above_is_refused()
    {
        var rig = new Rig();

        var refused = rig.Preview("new sensor 1 rain 30 gal").Refused!;

        Assert.Equal(("AmbiguousRow", "1 could be N1, S1 or X1 of stations; write more of the id.", "1"), (refused.Code, refused.Sentence, refused.Word));
        Assert.Equal(["N1", "S1", "X1"], refused.Candidates);
        // Invoked with the same terms, the command fails by the same code.
        var invoked = Assert.IsType<CommandResult.Refused>(await rig.Commands.InvokeAsync("entity.create", "1.0.0",
            new JsonObject { ["request"] = new JsonObject { ["workspaceId"] = rig.Id, ["product"] = "weather.sensor", ["terms"] = "1 rain 30 gal" } }, new Caller("ana", RunOrigin.Ai), "k1", default));
        Assert.Equal("AmbiguousRow", invoked.Code);
    }

    [Fact] // ADR-0016 note 48: a row made under a row is removed as a row the workspace made
    public async Task A_row_made_under_a_row_is_removed()
    {
        var rig = new Rig();
        await rig.Run("new sensor S1 wind 2 m3");

        await rig.Run("remove sensors S1/2");

        Assert.Equal(["S1/1 10 GAL"], rig.Sensors("S1"));
        Assert.Empty(rig.Board.Created);
    }

    [Theory] // ADR-0016 note 48: a product that names the row above by a term it has not, says no table, or says one its application has not registered, fails at start, by name
    [InlineData(Stations.Sensors, "place", "Product weather.sensor is made under the row its term place names, and has no such term.")]
    [InlineData(null, "station", "Product weather.sensor is made under a row and does not say the table its rows are of.")]
    [InlineData("weather.nowhere", "station", "Product weather.sensor makes rows of weather.nowhere, which no table of weather registers.")]
    public void A_product_made_under_a_row_is_checked_at_start(string? table, string under, string message)
    {
        var workspaces = Stations.Service();

        var thrown = Assert.Throws<InvalidOperationException>(() => new ProductService([new Stations.Products(Stations.SensorProduct with { Table = table, Under = under })],
            [new Stations.SensorRules(workspaces)], workspaces, new TableRegister([new Stations.Catalog(sensors: true)])));

        Assert.Equal(message, thrown.Message);
    }

    [Fact] // ADR-0016 note 48 (corrected in review): a product made under a row is given the tables it is read with, or fails at start
    public void A_product_made_under_a_row_needs_the_tables_at_start()
    {
        var workspaces = Stations.Service();

        var thrown = Assert.Throws<InvalidOperationException>(() => new ProductService([new Stations.Products()], [new Stations.SensorRules(workspaces)], workspaces));

        Assert.Equal("Product weather.sensor is made under a row, and the products were given no tables to read it in.", thrown.Message);
    }

    [Fact] // ADR-0016 note 48: a table is under another as a workspace describes it, so a product whose table is under none fails when its terms are first read there, by name
    public void A_product_whose_table_is_under_none_fails_when_its_terms_are_read()
    {
        var rig = new Rig(sensor: Stations.SensorProduct with { Table = Stations.Sites });

        var thrown = Assert.Throws<InvalidOperationException>(() => rig.Preview("new sensor s1 rain 30 gal"));

        Assert.Equal("Product weather.sensor is made under a row, and weather.stations is under no table.", thrown.Message);
    }
}
