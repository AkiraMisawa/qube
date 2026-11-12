using Qube.Composition.Application;
using Qube.Composition.Application.Products;
using Qube.Composition.Application.Tables;
using Qube.Composition.Application.Workspaces;

namespace Qube.Composition.Tests;

// ADR-0016 §10 and its notes 54 and 55 of 2026-11-12: a draft is a row, the revision it was opened at and its lines,
// read in order by the server, which stores nothing. The registration is a station with its sensors under it, and has
// no financial word. N1 stands at 120 with no backup and two sensors; S1 at 15, backed up by N1, with one.
public class DraftTests
{
    static readonly Caller Ana = new("ana", RunOrigin.Pricer);

    sealed class Rig
    {
        public WorkspaceService Workspaces { get; } = Stations.Service();
        public TableRegister Register { get; } = new([new Stations.Catalog(sensors: true)]);
        public CommandService Commands { get; }
        public CommandLines Lines { get; }
        public string Id { get; }

        public Rig()
        {
            var tables = new TableService(Register, Workspaces);
            var products = new ProductService([new Stations.Products()], [new Stations.SensorRules(Workspaces)], Workspaces, Register);
            var registry = new CompositionRegistry([new TablesModule(() => tables), new WorkspaceCommandsModule(() => Workspaces), new ProductsModule(() => products)]);
            Commands = new CommandService(registry, new InMemoryDataStore());
            Lines = new CommandLines(registry, Workspaces, Register, []);
            Id = Stations.Board(Workspaces).WorkspaceId;
        }

        public WorkspaceState Board => Workspaces.Find(Id)!;

        /// <summary>The form of a station's draft opened on the board as it stands, with the lines given.</summary>
        public DraftForm Form(string station, params string[] lines) => Lines.Draft(Id, Ana, new(Stations.Sites, station, Board.Revision, lines));
    }

    // A field of the form as its name, its value as written and, where the draft changed it, a mark.
    static string[] Fields(DraftForm form, params string[] names) =>
        [.. form.Fields.Where(f => names.Contains(f.Name)).Select(f => $"{f.Name} {f.Written}{(f.Changed ? " *" : "")}")];

    [Fact] // ADR-0016 note 54: the form of a draft with no line is the row as the workspace has it, with the rows under it
    public void The_form_of_a_row_with_no_line_is_the_row_as_the_workspace_has_it()
    {
        var rig = new Rig();

        var form = rig.Form("S1");

        Assert.Equal(["id S1", "altitude 15", "backup N1"], Fields(form, "id", "altitude", "backup"));
        Assert.Equal([false, true, true], form.Fields.Where(f => f.Name is "id" or "altitude" or "backup").Select(f => f.Editable));
        var sensors = Assert.Single(form.Under);
        Assert.Equal((Stations.Sensors, "Sensors"), (sensors.Table, sensors.Title));
        Assert.Equal(["S1/1"], sensors.Rows.Select(r => r.Key));
        Assert.All(sensors.Rows, r => Assert.Null(r.Mark));
        Assert.Empty(form.Lines);
        Assert.Equal(rig.Board.Revision, form.Revision);
    }

    // A line of the form as the line and what it said, or its refusal's code.
    static string[] Lines(DraftForm form) => [.. form.Lines.Select(l => $"{l.Line} => {l.Said ?? l.Refused!.Code}")];

    [Fact] // ADR-0016 note 54: each line is read on what the lines before it left, the form marks what the draft changed, and nothing is written
    public void Each_line_is_read_on_what_the_lines_before_it_left()
    {
        var rig = new Rig();
        var revision = rig.Board.Revision;

        // The third line sets a sensor the second makes: it reads only on what the second left.
        var form = rig.Form("S1", "set stations S1 altitude 20", "new sensor S1 wind 2 m3", "set sensors S1/2 kind gust");

        Assert.Equal(["altitude 20 *", "backup N1"], Fields(form, "altitude", "backup"));
        Assert.Equal(["set stations S1 altitude 20 => Set S1 altitude to 20.", "new sensor S1 wind 2 m3 => Made S1/2: Sensor.", "set sensors S1/2 kind gust => Set S1/2 kind to gust."],
            Lines(form));
        Assert.Equal(["S1/1", "S1/2"], Assert.Single(form.Under).Rows.Select(r => r.Key));
        Assert.Equal(revision, rig.Board.Revision);
        Assert.Empty(rig.Board.Overrides);
    }
}
