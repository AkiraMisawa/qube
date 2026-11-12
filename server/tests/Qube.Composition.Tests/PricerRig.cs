using System.Text.Json.Nodes;
using Qube.Composition.Application;
using Qube.Composition.Application.Products;
using Qube.Composition.Application.Tables;
using Qube.Composition.Application.Workspaces;
using Qube.Composition.Contracts;
using Qube.Samples.Ccr;
using Qube.Samples.Portfolio;
using Qube.Samples.Xva;

namespace Qube.Composition.Tests;

/// <summary>
/// The pricer as the application registers it, with XVA and SA-CCR, on the synthetic source for ABC/LE-01 on Day1: its
/// tables, its commands and its line, for the tests that drive it as a user does. Every name and figure is synthetic.
/// </summary>
sealed class PricerRig
{
    public static readonly DateOnly Day1 = new(2026, 10, 26);
    public static readonly Caller Akira = new("akira", RunOrigin.Pricer);

    public WorkspaceService Workspaces { get; }
    public PortfolioWorkspaces Service { get; }
    public CommandService Commands { get; }
    public CommandLines Lines { get; }
    public TableRegister Register { get; }
    public TableService Tables { get; }
    public string Id { get; }
    InMemoryDataStore Store { get; }
    RunService Runs { get; }

    public PricerRig(string registration = "ABC/LE-01")
    {
        var register = new MeasureRegister([new XvaMeasures(Pv01Provider.Simulated(TimeSpan.Zero)), new CcrMeasures()]);
        Workspaces = new WorkspaceService(new Dictionary<string, IEditRules> { [PortfolioEditRules.App] = new PortfolioEditRules(register) }, TimeProvider.System);
        Register = new TableRegister([new PortfolioTables(register)]);
        var tables = Tables = new TableService(Register, Workspaces);
        PortfolioWorkspaces? service = null;
        PortfolioWorkspaces Pricer() => service ?? throw new InvalidOperationException("The pricer's service is not built yet.");
        var products = new ProductService([new PortfolioProducts()], [new LegRules(Pricer)], Workspaces, Register);
        var registry = new CompositionRegistry([new PortfolioModule(), new XvaModule(), new CcrModule(), new TablesModule(() => tables), new WorkspaceCommandsModule(() => Workspaces),
            new ProductsModule(() => products), new PortfolioCommandsModule(Pricer, register)]);
        var store = Store = new InMemoryDataStore();
        Runs = new RunService(registry, store, TimeProvider.System);
        Service = service = new PortfolioWorkspaces(Workspaces, store, Runs, register, UnsetSettings.Pricer());
        Commands = new CommandService(registry, store);
        Lines = new CommandLines(registry, Workspaces, Register, [new PortfolioShortForms()]);
        Id = Service.Create("akira", registration, Day1, 40).WorkspaceId;
    }

    public WorkspaceState State => Workspaces.Find(Id)!;

    public LinePreview Preview(string line) => Lines.Preview(line, Id, null, Akira);

    /// <summary>Runs a line as Enter does, and answers what the command said.</summary>
    public async Task<string> Run(string line)
    {
        var preview = Preview(line);
        Assert.True(preview.Refused is null, preview.Refused?.Sentence);
        var result = await Commands.InvokeAsync(preview.Command!.Id, preview.Command.Version, preview.Inputs!, Akira, Guid.NewGuid().ToString(), default);
        return result is CommandResult.Done done ? done.Outputs.Values.Single()!["said"]!.GetValue<string>()
            : throw new Xunit.Sdk.XunitException($"{line} was refused: {string.Join(" ", ((CommandResult.Refused)result).Issues.Select(i => i.Message))}");
    }

    /// <summary>Prices the workspace as it stands and answers the accepted input its Run keeps.</summary>
    public async Task<JsonObject> Priced()
    {
        var accepted = Assert.IsType<SubmissionResult.Accepted>(Service.Price(Id, State.Revision, "akira", Guid.NewGuid().ToString()));
        await Runs.Completion(accepted.Run.RunId);
        var run = Runs.Find(accepted.Run.RunId)!;
        return Store.ReadRequired(((DataReferenceBinding)run.Workflow.Steps[0].Bindings["acceptedInput"]).Reference).AsObject();
    }

    /// <summary>What <c>tables.describe</c> answers the AI for this workspace.</summary>
    public async Task<JsonObject> Described()
    {
        var result = await Commands.InvokeAsync("tables.describe", TableContracts.Version, new JsonObject { ["request"] = new JsonObject { ["workspaceId"] = Id } }, Akira,
            Guid.NewGuid().ToString(), default);
        return Assert.IsType<CommandResult.Done>(result).Outputs["answer"]!.AsObject();
    }

    /// <summary>The keys of the rows a selector names, as a command would read it.</summary>
    public string[] Keys(string rows)
    {
        var tables = Register.Of(State);
        var read = SelectorReader.Read(rows, [.. tables.Select(t => t.Descriptor)]);
        Assert.Empty(read.Rest);
        return [.. Selectors.Resolve(read.Selector, tables, State).Rows.Select(r => r.Key)];
    }

    /// <summary>A row's values in the fields named, each as a command writes it, one space between.</summary>
    public string Written(string table, string key, params string[] fields)
    {
        var bound = Register.Of(State).Single(t => t.Descriptor.TableId == table);
        var row = bound.Table.Rows(State).Single(r => r.Key == key);
        return string.Join(" ", fields.Select(f => FieldForms.Write(bound.Descriptor.Fields.Single(d => d.Name == f).Spec, row.Value(f))));
    }

    IReadOnlyList<ITableRow> RowsOf(string table) => Register.Of(State).Single(t => t.Descriptor.TableId == table).Table.Rows(State);

    /// <summary>A trade's legs as they stand, each as its number, currency and PV.</summary>
    public string[] Legs(string trade) =>
        [.. RowsOf(PortfolioTables.Legs).Where(r => r.Under == trade).Select(r => $"{r.Key[(trade.Length + 1)..]} {r.Value("currency")!.GetValue<string>()} {r.Value("pv")!.GetValue<decimal>():0.##}")];

    /// <summary>A trade's independent amounts as they stand, each as its number, currency and value.</summary>
    public string[] Amounts(string trade) =>
        [.. RowsOf(PortfolioTables.Amounts).Where(r => r.Under == trade).Select(r => $"{r.Key[(trade.Length + 1)..]} {r.Value("currency")!.GetValue<string>()} {r.Value("value")!.GetValue<decimal>():0.##}")];

    public decimal PvOfLeg(string leg) => RowsOf(PortfolioTables.Legs).Single(r => r.Key == leg).Value("pv")!.GetValue<decimal>();

    /// <summary>A trade's PV as its row holds it, in the display currency; null where it has no leg.</summary>
    public decimal? Pv(string trade) => RowsOf(PortfolioTables.Trades).Single(r => r.Key == trade).Value("pv")?.GetValue<decimal>();
}
