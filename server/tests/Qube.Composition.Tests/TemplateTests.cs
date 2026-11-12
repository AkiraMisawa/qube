using System.Text.Json.Nodes;
using Qube.Composition.Application;
using Qube.Composition.Application.Tables;
using Qube.Composition.Application.Workspaces;

namespace Qube.Composition.Tests;

// ADR-0016 §1, §3 and its note 50 of 2026-11-12: a table's templates, named sets of field values that take terms of
// their own, and `set <rows> as <template>`. The registration is the board's readings, with no financial word.
public class TemplateTests
{
    static readonly Caller Ana = new("ana", RunOrigin.Pricer);

    sealed class Rig
    {
        public WorkspaceService Workspaces { get; } = Stations.Service();
        public TableRegister Register { get; }
        public CommandService Commands { get; }
        public CommandLines Lines { get; }
        public string Id { get; }

        public Rig(TableDescriptor? readings = null)
        {
            Register = new([new Stations.Catalog(readings: readings ?? Stations.ReadingsWithTemplates, settings: Stations.SettingsWithTemplates)]);
            var tables = new TableService(Register, Workspaces);
            var registry = new CompositionRegistry([new TablesModule(() => tables), new WorkspaceCommandsModule(() => Workspaces)]);
            Commands = new CommandService(registry, new InMemoryDataStore());
            Lines = new CommandLines(registry, Workspaces, Register, []);
            Id = Stations.Board(Workspaces).WorkspaceId;
        }

        public WorkspaceState Board => Workspaces.Find(Id)!;

        public LinePreview Preview(string line, LineView? view = null) => Lines.Preview(line, Id, null, Ana, view);

        /// <summary>Runs a line as Enter does, and answers what the command said.</summary>
        public async Task<string> Run(string line, LineView? view = null)
        {
            var preview = Preview(line, view);
            Assert.True(preview.Refused is null, preview.Refused?.Sentence);
            var result = await Commands.InvokeAsync(preview.Command!.Id, preview.Command.Version, preview.Inputs!, Ana, Guid.NewGuid().ToString(), default);
            return result is CommandResult.Done done ? done.Outputs.Values.Single()!["said"]!.GetValue<string>()
                : throw new Xunit.Sdk.XunitException($"{line} was refused: {string.Join(" ", ((CommandResult.Refused)result).Issues.Select(i => i.Message))}");
        }

        /// <summary>A reading's values in the fields named, each as a command writes it.</summary>
        public string Reading(string key, params string[] fields)
        {
            var table = Register.Of(Board).Single(t => t.Descriptor.TableId == Stations.Readings);
            var row = table.Table.Rows(Board).Single(r => r.Key == key);
            return string.Join(" ", fields.Select(f => FieldForms.Write(table.Descriptor.Fields.Single(d => d.Name == f).Spec, row.Value(f))));
        }
    }

    static string Read(LinePreview preview) => preview.Refused is { } refused ? $"{refused.Code}: {refused.Sentence}" : preview.Would!;

    [Fact] // ADR-0016 note 50: a template writes the fields it names, with the term given alone or by name, and no other field of the row
    public async Task A_template_writes_its_fields_over_a_row()
    {
        var rig = new Rig();
        Assert.Equal("M3 off empty empty 5mm", rig.Reading("R1", "unit", "checked", "note", "drift", "volume"));

        var said = await rig.Run("set R1 as gauged gal");

        Assert.Equal("Set R1 as gauged unit=GAL.", said);
        Assert.Equal("GAL on gauged empty 5mm", rig.Reading("R1", "unit", "checked", "note", "drift", "volume"));
    }

    [Fact] // ADR-0016 note 50: before Enter the line names each cell that would change, and counts those that hold their value
    public void The_preview_names_the_cells_that_change_and_counts_the_rest()
    {
        var rig = new Rig();

        Assert.Equal("unit: M3 to GAL; checked: off to on; note: empty to gauged; 1 value already held", Read(rig.Preview("set R1 as gauged gal")));
    }

    [Fact] // ADR-0016 note 50, the owner's rule of 2026-11-04: a term is typed alone or by a name of its own, in any order, and the template by any of its words
    public async Task Terms_are_read_by_name_or_by_form_in_any_order()
    {
        var rig = new Rig();

        Assert.Equal("unit: M3 to GAL; checked: off to on; note: empty to gauged; drift: empty to 2%", Read(rig.Preview("set R1 as by gauge drift=2% in=gal")));
        Assert.Equal("Set R1 as gauged unit=GAL drift=2%.", await rig.Run("set R1 as GAUGED drift=2% GAL"));
        Assert.Equal("GAL on gauged 2%", rig.Reading("R1", "unit", "checked", "note", "drift"));
    }

    [Fact] // ADR-0016 note 50, note 9: a row that holds every value takes no edit, and the line says so
    public async Task A_row_that_holds_every_value_is_not_written()
    {
        var rig = new Rig();
        await rig.Run("set R1 as gauged gal");
        var revision = rig.Board.Revision;

        Assert.Equal("Nothing would change: R1 already holds every value of gauged unit=GAL.", Read(rig.Preview("set R1 as gauged gal")));
        Assert.Equal("Nothing changed: R1 already holds every value of gauged unit=GAL.", await rig.Run("set R1 as gauged gal"));
        Assert.Equal(revision, rig.Board.Revision);
    }

    [Fact] // ADR-0016 note 50, §11: several rows are written by one command, which the log names with the terms as read and one undo takes back
    public async Task Several_rows_are_one_command_and_one_undo()
    {
        var rig = new Rig();
        var before = new[] { "R1", "R2", "R3" }.Select(k => rig.Reading(k, "windSpeed", "windDirection")).ToList();
        Assert.Equal("12 N", before[0]);

        Assert.Equal("wind speed: 3 to 0; wind direction: 3 to none", Read(rig.Preview("set R1, R2, R3 as calm")));
        Assert.Equal("Set R1, R2 and R3 as calm.", await rig.Run("set R1, R2, R3 as calm"));

        Assert.All(new[] { "R1", "R2", "R3" }, k => Assert.Equal("0 none", rig.Reading(k, "windSpeed", "windDirection")));
        Assert.Equal("set R1, R2, R3 as calm", rig.Board.Log[^1].Command!.Said);
        await rig.Run("undo");
        Assert.Equal(before, new[] { "R1", "R2", "R3" }.Select(k => rig.Reading(k, "windSpeed", "windDirection")));
    }

    [Theory] // ADR-0016 note 50: a template the table has not, a term it needs, a term it has not and a value that cannot be read are refused by name
    [InlineData("set R1 as", "UnknownTemplate: as takes a template of readings: gauged or calm.")]
    [InlineData("set R1 as windy", "UnknownTemplate: readings has no template windy; its templates are gauged and calm.")]
    [InlineData("set R1 as gauged", "MissingTerm: gauged still needs unit: type it after it.")]
    [InlineData("set R1 as gauged drift=2%", "MissingTerm: gauged still needs unit: type it after it.")]
    [InlineData("set R1 as gauged gal litres", "InvalidTerms: 'litres' is no term of gauged left to fill; name it as name=value.")]
    [InlineData("set R1 as gauged gal size=3", "InvalidTerms: gauged has no term named size; it takes unit, drift.")]
    [InlineData("set R1 as gauged unit=stone", "InvalidTerms: unit is one of m3, gal.")]
    [InlineData("set R1 as gauged gal unit=m3", "InvalidTerms: unit is given twice.")]
    [InlineData("set R1 as calm now", "InvalidTerms: calm takes no term; 'now' is not read.")]
    public void A_template_that_cannot_be_read_is_refused_by_name(string line, string refusal)
    {
        var rig = new Rig();
        var revision = rig.Board.Revision;

        Assert.Equal(refusal, Read(rig.Preview(line)));
        Assert.Equal(revision, rig.Board.Revision);
    }

    [Fact] // ADR-0016 note 50: `as` on a table with no template is refused, and the refusal marks the word
    public void A_table_with_no_template_refuses_as()
    {
        var preview = new Rig(Stations.ReadingsTable).Preview("set R1 as gauged gal");

        Assert.Equal(("NoTemplates", "readings has no template: set takes a field and its value.", "as"), (preview.Refused!.Code, preview.Refused.Sentence, preview.Refused.Word));
    }

    [Fact] // ADR-0016 note 50: a template the table has not is refused with those it has, at the word typed
    public void An_unknown_template_is_marked_with_those_there_are()
    {
        var refused = new Rig().Preview("set R1 as windy").Refused!;

        Assert.Equal(("windy", 10, 15), (refused.Word, refused.Start, refused.End));
        Assert.Equal(["gauged", "calm"], refused.Candidates);
    }

    [Fact] // ADR-0016 note 50: a template that writes a value its table's rule needs another for writes them in its order, and one that cannot be written writes nothing
    public void The_values_are_written_in_the_templates_order_and_all_or_none()
    {
        // The board's rule: a drift is explained by a note. R1 has none, and gauged writes the note before the drift.
        Assert.Equal("unit: M3 to GAL; checked: off to on; note: empty to gauged; drift: empty to 2%", Read(new Rig().Preview("set R1 as gauged gal drift=2%")));

        // The same template with the drift first is refused by the rule, and no value of it is written.
        var drifted = new Rig(Stations.ReadingsTable with { Templates = [Stations.Gauged with { Writes = ["unit", "checked", "drift", "note"] }] });
        Assert.Equal("InvalidValue: R1 has no note, and a drift is explained by one.", Read(drifted.Preview("set R1 as gauged gal drift=2%")));
        Assert.Equal("M3 off empty empty", drifted.Reading("R1", "unit", "checked", "note", "drift"));
    }

    [Fact] // ADR-0016 note 50, note 7: a table of one row takes its template with no row said
    public async Task A_table_of_one_row_takes_a_template_with_no_row()
    {
        var rig = new Rig();

        Assert.Equal("alarm volume: 10mm to 1mm; daylight: on to off", Read(rig.Preview("set as night")));
        Assert.Equal("Set board as night.", await rig.Run("set as night"));
        await rig.Run("undo");
        // Said by its word, it reads the same.
        Assert.Equal("Set board as night.", await rig.Run("set board as night"));
    }

    [Fact] // ADR-0016 note 50, note 18: with a grid behind the line, a template of its table is written over its selection
    public async Task With_a_grid_behind_the_line_a_template_is_written_over_its_selection()
    {
        var rig = new Rig();
        var selected = new LineView(Stations.Readings, Keys: ["R1", "R3"]);

        var preview = rig.Preview("set as calm", selected);

        Assert.Equal("wind speed: 2 to 0; wind direction: 2 to none", preview.Would);
        Assert.Equal(("readings", "R1, R3", "as calm"),
            (preview.Inputs!["request"]!["table"]!.GetValue<string>(), preview.Inputs["request"]!["rows"]!.GetValue<string>(), preview.Inputs["request"]!["set"]!.GetValue<string>()));
        Assert.Equal("Set R1 and R3 as calm.", await rig.Run("set as calm", selected));
        // A template of a table of one row is that table's, whichever grid is behind the line.
        Assert.Equal("alarm volume: 10mm to 1mm; daylight: on to off", rig.Preview("set as night", selected).Would);
    }
}
