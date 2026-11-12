using System.Text.Json.Nodes;
using Qube.Composition.Application;
using Qube.Composition.Application.Products;
using Qube.Composition.Application.Tables;
using Qube.Composition.Application.Workspaces;

namespace Qube.Composition.Tests;

/// <summary>
/// The stations with their sensors under them, their commands and the line that reads them, over one board of Ana's:
/// the kernel's rig for a table under a row (ADR-0016 notes 46 to 48) and for a draft of a row (notes 54 and 55).
/// </summary>
sealed class SensorRig
{
    public static readonly Caller Ana = new("ana", RunOrigin.Pricer);

    public WorkspaceService Workspaces { get; } = Stations.Service();
    public TableRegister Register { get; } = new([new Stations.Catalog(sensors: true)]);
    public CommandService Commands { get; }
    public CommandLines Lines { get; }
    public string Id { get; }

    public SensorRig(bool described = true, ProductDescriptor? sensor = null)
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

    public LinePreview Preview(string line) => Lines.Preview(line, Id, null, Ana);

    public async Task<JsonObject> Run(string line)
    {
        var preview = Preview(line);
        Assert.True(preview.Refused is null, preview.Refused?.Sentence);
        var result = await Commands.InvokeAsync(preview.Command!.Id, preview.Command.Version, preview.Inputs!, Ana, Guid.NewGuid().ToString(), default);
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
