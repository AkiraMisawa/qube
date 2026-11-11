using Qube.Composition.Application.Products;
using System.Text.Json.Nodes;
using Qube.Agent;
using Qube.Composition.Application;
using Qube.Composition.Application.Saved;
using Qube.Composition.Application.Tables;
using Qube.Composition.Application.Traces;
using Qube.Composition.Application.Workspaces;
using Qube.Composition.Contracts;
using Qube.Persistence.Sqlite;
using Qube.Samples.Rates;
using Qube.Samples.Simm;
using Qube.Samples.Synthetic;
using Qube.Samples.Ccr;
using Qube.Samples.Portfolio;
using Qube.Samples.Xva;
using Qube.Web;

var builder = WebApplication.CreateBuilder(args);
builder.AddObservability();
builder.Services.ConfigureHttpJsonOptions(o =>
{
    foreach (var converter in WireJson.Options.Converters) o.SerializerOptions.Converters.Add(converter);
    o.SerializerOptions.UnmappedMemberHandling = WireJson.Options.UnmappedMemberHandling;
    o.SerializerOptions.DefaultIgnoreCondition = WireJson.Options.DefaultIgnoreCondition;
    o.SerializerOptions.RespectNullableAnnotations = true;
    o.SerializerOptions.RespectRequiredConstructorParameters = true;
});

// The composition root: the only place that names modules and providers.
builder.Services.AddSingleton<IQubeModule, SyntheticModule>();
builder.Services.AddSingleton<IQubeModule, PortfolioModule>();
builder.Services.AddSingleton<IQubeModule, XvaModule>();
builder.Services.AddSingleton<IQubeModule, CcrModule>();

// Rates (docs/07): conventions and the synthetic market come from configuration; the provider is the fake one,
// which says simulated on every figure. A real worker replaces IRatesPricingProvider here and nowhere else.
var rates = builder.Configuration.GetSection("Rates").Get<RatesSettings>() ?? new RatesSettings();
var conventions = rates.Registry();
var syntheticMarket = new SyntheticMarket(rates.SyntheticMarket.Config());
builder.Services.AddSingleton(conventions);
builder.Services.AddSingleton<IRatesPricingProvider, FakeRatesPricing>();
builder.Services.AddSingleton(sp => new RatesLiveService(sp.GetRequiredService<IRatesPricingProvider>(), syntheticMarket.Resolve, sp.GetRequiredService<TimeProvider>(),
    TimeSpan.FromSeconds(rates.LiveIdleStopSeconds)));
builder.Services.AddSingleton(sp => new MarketFeed(syntheticMarket, sp.GetRequiredService<RatesLiveService>()));
builder.Services.AddSingleton<IQubeModule>(sp => new RatesModule(sp.GetRequiredService<IRatesPricingProvider>(), syntheticMarket.Resolve));
builder.Services.AddSingleton<RatesWorkspaces>();
// Spot SIMM (ADR-0012): the synthetic IM source, CRIF from the same Rates provider and market, and the reference engine
// on the DEMO table only.
// ISDA's calibration is licensed; a table loaded from outside the repository would be added here. The page's spot IM
// validates against the same tables the request prices with.
IReadOnlyList<SimmParameters> simmTables = [SimmParameters.Demo];
builder.Services.AddSingleton<IQubeModule>(sp => new SimmModule(new SyntheticImSource(), sp.GetRequiredService<IRatesPricingProvider>(), syntheticMarket.Resolve, conventions,
    simmTables));
// The commands reach the workspace service only when invoked: it is built from the registry they are part of.
builder.Services.AddSingleton<IQubeModule>(sp => new RatesCommandsModule(sp.GetRequiredService<RatesWorkspaces>));
builder.Services.AddSingleton<IQubeModule>(sp => new PortfolioCommandsModule(sp.GetRequiredService<PortfolioWorkspaces>));
// Products are descriptions from catalogs (ADR-0008 §7); the kernel's entity.create reads them. Rates describes its IRS and CCS.
builder.Services.AddSingleton<IProductCatalog>(new RatesProductCatalog(conventions));
builder.Services.AddSingleton<IProductRules>(sp => new IrsRules(conventions, sp.GetRequiredService<RatesWorkspaces>));
builder.Services.AddSingleton<IProductRules>(sp => new CcsRules(conventions, sp.GetRequiredService<RatesWorkspaces>));
builder.Services.AddSingleton<ProductService>();
builder.Services.AddSingleton<IQubeModule>(sp => new ProductsModule(sp.GetRequiredService<ProductService>));
// The kernel's commands over any workspace (ADR-0016 §11): undo and redo.
builder.Services.AddSingleton<IQubeModule>(sp => new WorkspaceCommandsModule(sp.GetRequiredService<WorkspaceService>));
// Tables are descriptions from catalogs too (ADR-0016 §1); the kernel's entity.set, entity.reset and entity.remove read
// them. The pricer registers its trades, CSAs, netting set, credit, CDS, workspace and each module's settings.
builder.Services.AddSingleton<ITableCatalog>(sp => new PortfolioTables(sp.GetRequiredService<MeasureRegister>()));
builder.Services.AddSingleton<TableRegister>();
builder.Services.AddSingleton<TableService>();
builder.Services.AddSingleton<IQubeModule>(sp => new TablesModule(sp.GetRequiredService<TableService>));
builder.Services.AddSingleton(sp => new CommandService(sp.GetRequiredService<CompositionRegistry>(), sp.GetRequiredService<IDataStore>()));
builder.Services.AddSingleton<CommandLines>();
// A Run's calculation traces (ADR-0011 §5, §7): the read endpoint and trace.read open them through one service.
builder.Services.AddSingleton(sp => new TraceService(sp.GetRequiredService<RunService>(), sp.GetRequiredService<IDataStore>(), sp.GetRequiredService<CompositionRegistry>()));
builder.Services.AddSingleton<IQubeModule>(sp => new TracesModule(sp.GetRequiredService<TraceService>));
if (rates.Ticker)
    builder.Services.AddHostedService(sp => new MarketTicker(sp.GetRequiredService<MarketFeed>(), rates.SyntheticMarket.Config().Step, sp.GetRequiredService<ILogger<MarketTicker>>()));
builder.Services.AddSingleton<CompositionRegistry>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<RunService>();
builder.Services.AddSingleton<ActionService>();
builder.Services.AddSingleton<HistoryService>();

// Workspaces: one SQLite file (D04, C35). Each module registers its edit rules and how its sources are re-acquired.
var database = LocalState.Database(builder.Configuration, builder.Environment.ContentRootPath);
Directory.CreateDirectory(Path.GetDirectoryName(database)!);
builder.Services.AddSingleton(_ => new SqliteRunStore(database));
builder.Services.AddSingleton<IRunStore>(sp => sp.GetRequiredService<SqliteRunStore>());
builder.Services.AddSingleton<IDataStore>(sp => sp.GetRequiredService<SqliteRunStore>());
builder.Services.AddSingleton<IWorkspaceStore>(_ => new SqliteWorkspaceStore(database,
    new Dictionary<string, SourceResolver> { [PortfolioEditRules.App] = PortfolioSourceVersion.Resolve, [RatesEditRules.App] = RatesWorkspaces.Resolve }));
builder.Services.AddSingleton(sp => new WorkspaceService(
    new Dictionary<string, IEditRules>
    {
        [PortfolioEditRules.App] = new PortfolioEditRules(sp.GetRequiredService<MeasureRegister>()), [RatesEditRules.App] = new RatesEditRules(conventions),
    },
    sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<IWorkspaceStore>()));
// New trades' PV01 (ADR-0010 §6) comes from the simulated engine, which answers after a configured delay as an engine
// would; there is no real one to fall back from.
builder.Services.AddSingleton(sp => Pv01Provider.Simulated(TimeSpan.FromMilliseconds(builder.Configuration.GetValue("Xva:Pv01DelayMilliseconds", 400)),
    sp.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton<IMeasureModule, XvaMeasures>();
// Spot CCR capital (ADR-0013), priced by XVA's request with KVA on. κ is configuration: 8% unless Ccr:CapitalRatio says.
builder.Services.AddSingleton<IMeasureModule>(_ => new CcrMeasures(builder.Configuration["Ccr:CapitalRatio"] is { } ratio
    ? new CapitalRatio(double.Parse(ratio, System.Globalization.CultureInfo.InvariantCulture), "Ccr:CapitalRatio", "configuration")
    : null));
// Spot IM (ADR-0012 §4's note of 2026-11-08), priced by SIMM's own request in the PoC: CRIF on the synthetic market's
// opening snapshot, which stands for the value date's close, with the portfolio's FX; the DEMO table only.
builder.Services.AddSingleton<IMeasureModule>(_ => new SimmMeasures(conventions, _ => syntheticMarket.Snapshot(0), simmTables));
builder.Services.AddSingleton<MeasureRegister>();
// How many cases one Price prices, the Base among them (ADR-0010 §3's note of 2026-11-08): 50 unless configured.
builder.Services.AddSingleton(new PortfolioOptions(builder.Configuration.GetValue("Portfolio:CaseCap", PortfolioCases.DefaultCap)));
// Saved sets (C32, ADR-0010 §4) beside the workspaces; a user's desk is configuration, Qube:Desks: { "<user>": "<desk>" }.
builder.Services.AddSingleton<ISavedItemStore>(_ => new SqliteSavedItemStore(database));
builder.Services.AddSingleton(new Desks(builder.Configuration.GetSection("Qube:Desks").GetChildren()
    .ToDictionary(d => d.Key, d => d.Value ?? throw new InvalidOperationException($"Qube:Desks:{d.Key} names no desk."), StringComparer.OrdinalIgnoreCase)));
builder.Services.AddSingleton<SavedItems>();
builder.Services.AddSingleton<PortfolioWorkspaces>();
// The AI pane (ADR-0007): the agent runtime is chosen here and nowhere else, by configuration, and an unknown one stops
// startup rather than falling back to another. "copilot" talks to the agent host, a separate process under the agent's
// own identity (C44); this process never holds the model credential.
builder.Services.AddSingleton<AgentThreads>();
builder.Services.AddSingleton<AgentToolGateway>();
builder.Services.AddSingleton<AgentService>();
foreach (var request in RatesScriptedRequests.All(conventions)) builder.Services.AddSingleton(request);
var agentRuntime = builder.Configuration["Agent:Runtime"];
switch (agentRuntime)
{
    case "scripted":
        builder.Services.AddSingleton<IAgentRuntime, ScriptedAgent>();
        break;
    case "copilot":
        var copilot = new CopilotRuntimeOptions(
            new Uri(Required("Agent:Copilot:HostUrl")),
            Required("Agent:Copilot:HostKey"),
            Required("Agent:Copilot:Model"));
        builder.Services.AddHttpClient(CopilotHttpClient).ConfigureHttpClient(c => c.Timeout = Timeout.InfiniteTimeSpan);
        builder.Services.AddSingleton<IAgentRuntime>(sp => new CopilotAgentRuntime(sp.GetRequiredService<IHttpClientFactory>().CreateClient(CopilotHttpClient), copilot));
        break;
    default:
        throw new InvalidOperationException($"Agent:Runtime is {(agentRuntime is null ? "not set" : $"'{agentRuntime}'")}; the runtimes are: scripted, copilot.");
}
builder.Services.AddSingleton(new CurrentUser(builder.Configuration["Qube:DemoUser"] ?? "demo"));

var app = builder.Build();
app.UseRequestLogging();
// Resolve the registry now, so an invalid registered definition stops startup instead of a later request.
app.Services.GetRequiredService<CompositionRegistry>();
// And the reader of typed lines, for the same reason (ADR-0016 §4): a command that says how it is typed and cannot
// read its line, or two that answer to the same words, stop startup.
app.Services.GetRequiredService<CommandLines>();

app.MapQubeApi();
app.MapXvaApi();
app.MapRatesApi();
app.MapProductsApi();
app.MapAgentApi();
app.MapClientTelemetry();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");
app.LogDestination();
app.Run();

string Required(string key) => builder.Configuration[key] is { Length: > 0 } value
    ? value
    : throw new InvalidOperationException($"{key} is not set; Agent:Runtime copilot needs it.");

/// <summary>Exposed for the in-process API tests.</summary>
public partial class Program
{
    /// <summary>The named client the Copilot runtime reaches the agent host with.</summary>
    public const string CopilotHttpClient = "agent-host";
}
