using System.Text.Json.Nodes;
using Qube.Composition.Application;
using Qube.Composition.Application.Products;
using Qube.Composition.Application.Tables;
using Qube.Composition.Application.Workspaces;
using Qube.Samples.Rates;
using LineView = Qube.Samples.Rates.LineView;
using ScreenView = Qube.Composition.Application.Tables.LineView;

namespace Qube.Composition.Tests;

// ADR-0016 notes 34 to 36 of 2026-11-11: Rates on the generic verbs. Its lines and its workspace's defaults are
// registered tables, a live subscription is told of a revision whoever wrote it, and its own verbs read their lines.
// Profiles, curves and figures are synthetic test ones; the provider is the fake one.
public class RatesLineCommandTests
{
    static readonly Caller Akira = new("akira", RunOrigin.Pricer);
    static readonly IBusinessCalendar Calendar = new WeekendsOnlyCalendar();

    static ConventionProfile Profile(string currency, string index, bool overnight, IReadOnlyDictionary<string, string>? standard = null) =>
        new($"test.{currency}.{index}", "1", currency, index, DayCountBasis.Act360, Calendar.Id, BusinessDayAdjustment.ModifiedFollowing,
            2, 2, StubRule.ShortFront, false, overnight, standard);

    // One index a currency, so a line's index follows its currency, with the standard frequencies a line is made with.
    static readonly IReadOnlyDictionary<string, string> Standard = new Dictionary<string, string>
    {
        ["irs.fixedFrequency"] = "12m", ["irs.floatFrequency"] = "12m", ["ccs.frequency"] = "3m",
    };

    static readonly ConventionRegistry Conventions = new([Profile("TST", "TEST-ON", true, Standard), Profile("OTH", "OTHER-ON", true, Standard)], [Calendar]);

    static SyntheticMarketConfig MarketConfig => new("test-1", 7, new DateTimeOffset(2026, 10, 27, 0, 0, 0, TimeSpan.Zero), TimeSpan.FromSeconds(1),
        [new("TST", "TEST-ON", [(Tenor.Parse("1y"), 1.0), (Tenor.Parse("5y"), 1.5), (Tenor.Parse("10y"), 2.0)]),
         new("OTH", "OTHER-ON", [(Tenor.Parse("1y"), 2.0), (Tenor.Parse("5y"), 2.5), (Tenor.Parse("10y"), 3.0)])],
        new Dictionary<string, double> { ["TST"] = 1, ["OTH"] = 0.5 }, 0.5, 0.2, 0.0005);

    sealed class Rig
    {
        public WorkspaceService Workspaces { get; }
        public RatesWorkspaces Service { get; }
        public ProductService Products { get; }
        public CommandService Commands { get; }
        public CommandLines Lines { get; }
        public RunService Runs { get; }
        public IDataStore Store { get; }
        public MarketFeed Market { get; }
        public RatesLiveService Live { get; }
        public string Id { get; }

        public Rig()
        {
            var market = new SyntheticMarket(MarketConfig);
            var provider = new FakeRatesPricing();
            Live = new RatesLiveService(provider, market.Resolve, TimeProvider.System, TimeSpan.FromMinutes(5));
            Market = new MarketFeed(market, Live);
            var store = new InMemoryDataStore();
            Workspaces = new WorkspaceService(new Dictionary<string, IEditRules> { [RatesEditRules.App] = new RatesEditRules(Conventions) }, TimeProvider.System,
                observers: [new RatesLiveObserver(Live, Conventions, provider)]);
            var registered = new TableRegister([new RatesTables(Conventions)]);
            var tables = new TableService(registered, Workspaces);
            RatesWorkspaces? service = null;
            ProductService? products = null;
            RatesWorkspaces Built() => service ?? throw new InvalidOperationException("The Rates service is not built yet.");
            var registry = new CompositionRegistry([new RatesModule(provider, market.Resolve), new TablesModule(() => tables), new WorkspaceCommandsModule(() => Workspaces),
                new RatesCommandsModule(Built), new ProductsModule(() => products ?? throw new InvalidOperationException("The products are not built yet."))]);
            Runs = new RunService(registry, store, TimeProvider.System);
            Service = service = new RatesWorkspaces(Workspaces, store, Runs, Live, Market, Conventions, provider);
            Products = products = new ProductService([new RatesProductCatalog(Conventions)], [new IrsRules(Conventions, Built), new CcsRules(Conventions, Built)], Workspaces);
            Commands = new CommandService(registry, store);
            Store = store;
            Lines = new CommandLines(registry, Workspaces, registered, []);
            Id = Service.Create("akira", new DateOnly(2026, 10, 27)).WorkspaceId;
        }

        public WorkspaceState State => Workspaces.Find(Id)!;

        public RatesWorkspaceView View => Service.View(Id, "akira");

        /// <summary>A five-year IRS of 10mm in the test currency, paying fixed at par; answers its line.</summary>
        public string Irs(string terms = "5y tst 10 pay") => Products.Create(Id, null, "akira", RatesProducts.Irs, terms, RunOrigin.Pricer).Entity;

        /// <summary>A five-year cross-currency swap with a spread on its second leg; answers its line.</summary>
        public string Ccs() => Products.Create(Id, null, "akira", RatesProducts.Ccs, "5y tst oth 10 20 pay spread2=10bp", RunOrigin.Pricer).Entity;

        public LineView Line(string line) => View.Lines.Single(l => l.Line == line);

        public LinePreview Preview(string line, ScreenView? view = null) => Lines.Preview(line, Id, null, Akira, view);

        /// <summary>Invokes the command a preview read, with the inputs it answered, as Enter does; answers what it said.</summary>
        public async Task<string> Run(LinePreview preview)
        {
            Assert.True(preview.Refused is null, preview.Refused?.Sentence);
            var result = await Commands.InvokeAsync(preview.Command!.Id, preview.Command.Version, preview.Inputs!, Akira, Guid.NewGuid().ToString(), default);
            return result is CommandResult.Done done ? done.Outputs.Values.Single()!["said"]!.GetValue<string>()
                : throw new Xunit.Sdk.XunitException($"{preview.Command.Id} was refused: {string.Join(" ", ((CommandResult.Refused)result).Issues.Select(i => i.Message))}");
        }

        public Task<string> Enter(string line, ScreenView? view = null) => Run(Preview(line, view));
    }

    static string Read(LinePreview preview) => preview.Refused is { } refused ? $"{refused.Code}: {refused.Sentence}" : preview.Would!;

    // A number as the line holds it, whatever numeric type wrote it: a command writes a decimal, a product's term a double.
    static double Number(LineView line, string field) => RatesMaterialiser.Number(line.Fields[field]);

    static string? Text(LineView line, string field) => line.Fields[field]?.GetValue<string>();

    [Fact] // ADR-0016 note 34: an IRS's terms are fields of its line, and a leg's field is typed by a word that ends in the leg's number
    public async Task Set_edits_a_line_by_an_irs_terms_and_by_a_legs_own_field()
    {
        var rig = new Rig();
        var line = rig.Irs();

        var read = rig.Preview($"set {line} notional 5mm");
        var said = await rig.Run(read);
        var both = rig.Line(line);
        var one = await rig.Enter($"set {line} notional1 7mm");
        var legs = rig.Line(line);

        Assert.Equal(("lines, 1 row", "notional: 10mm to 5mm", $"Set {line} notional to 5mm."), (read.Rows!.Title, read.Would, said));
        Assert.Equal((5e6, 5e6), (Number(both, "leg1.notional"), Number(both, "leg2.notional")));
        Assert.Equal($"Set {line} notional1 to 7mm.", one);
        Assert.Equal((7e6, 5e6), (Number(legs, "leg1.notional"), Number(legs, "leg2.notional")));
        // A line whose legs differ holds no one notional, and says so by its own issue.
        Assert.Contains("An IRS has one notional.", legs.Issues);
    }

    [Fact] // ADR-0016 note 34: a rate is typed with its unit, as in the pricer, and written as the fixed leg's quote in percent
    public async Task A_rate_is_typed_with_its_unit_and_held_as_the_fixed_legs_quote()
    {
        var rig = new Rig();
        var line = rig.Irs();

        var bare = rig.Preview($"set {line} rate 1.25");
        var said = await rig.Enter($"set {line} rate 1.25%");
        var after = rig.Line(line);
        var spread = await rig.Enter($"set {line} spread 5bp");
        var matched = rig.Preview("set where rate>1% risk on");

        Assert.Equal($"InvalidValue: {line} rate is a rate with its unit: 1.25% or 5bp; 1.25 is not.", Read(bare));
        Assert.Equal($"Set {line} rate to 1.25%.", said);
        Assert.Equal(("FixedRatePercent", 1.25), (Text(after, "leg1.quoteKind"), Number(after, "leg1.quote")));
        Assert.Null(after.ParLeg);
        Assert.Equal($"Set {line} spread to 0.05%.", spread);
        Assert.Equal(0.05, Number(rig.Line(line), "leg2.quote"));
        Assert.Equal([line], matched.Rows!.First);
    }

    [Fact] // ADR-0016 note 34: a term that does not apply to a line is refused by the line's name, with the fields that do, and nothing is written
    public async Task A_term_that_does_not_apply_to_a_line_is_refused_by_name()
    {
        var rig = new Rig();
        var (irs, ccs) = (rig.Irs(), rig.Ccs());
        var revision = rig.State.Revision;

        var notional = rig.Preview($"set {ccs} notional 5mm");
        var all = rig.Preview("set all notional 5mm");
        var rate = rig.Preview($"set {ccs} rate 1%");
        var fixedSpread = rig.Preview($"set {irs} spread1 5bp");
        // Invoked as the AI would, with no reading before it: the IRS could take the notional, the swap beside it cannot, and neither is written.
        var invoked = await rig.Commands.InvokeAsync(EntitySet.Id, TableContracts.Version,
            new JsonObject { ["request"] = new JsonObject { ["workspaceId"] = rig.Id, ["rows"] = "all", ["set"] = "notional 5mm" } }, Akira, Guid.NewGuid().ToString(), default);
        var unwritten = (rig.State.Revision, Number(rig.Line(irs), "leg1.notional"));
        var leg = await rig.Enter($"set {ccs} notional2 30");

        Assert.Equal($"NotApplicable: {ccs} is a cross-currency swap, which has a notional on each leg: set notional1 or notional2.", Read(notional));
        Assert.Equal(Read(notional), Read(all));
        Assert.Equal($"NotApplicable: Leg 1 of {ccs} is floating and takes no fixed rate: set spread1.", Read(rate));
        Assert.Equal($"NotApplicable: Leg 1 of {irs} is fixed and takes no spread: set rate.", Read(fixedSpread));
        Assert.Equal("NotApplicable", Assert.Single(Assert.IsType<CommandResult.Refused>(invoked).Issues).Code);
        Assert.Equal((revision, 10e6), unwritten);
        Assert.Equal(($"Set {ccs} notional2 to 30mm.", 30e6), (leg, Number(rig.Line(ccs), "leg2.notional")));
    }

    [Fact] // ADR-0016 note 34: reset puts a line's field back to what the line was made with, remove removes the line, and undo takes each back
    public async Task Reset_puts_a_field_back_and_remove_removes_a_line()
    {
        var rig = new Rig();
        var line = rig.Irs();
        await rig.Enter($"set {line} end=7y direction=receive");

        var reset = await rig.Enter($"reset {line} end");
        var kept = rig.Line(line);
        var removed = await rig.Enter($"remove {line}");
        var gone = rig.View.Lines;
        var back = await rig.Enter("undo");

        Assert.Equal($"Reset {line} end.", reset);
        Assert.Equal(("5y", "receive"), (Text(kept, "end"), Text(kept, "direction")));
        Assert.Equal($"Removed {line}.", removed);
        Assert.Empty(gone);
        Assert.Equal($"Took back: remove {line}. redo puts it back.", back);
        Assert.Equal("receive", Text(rig.Line(line), "direction"));
    }

    [Fact] // ADR-0016 note 34: a value is checked for its form, as a typed cell is; what the terms then lack shows on the row
    public async Task A_set_is_checked_for_its_form_and_the_row_says_what_the_terms_lack()
    {
        var rig = new Rig();
        var line = rig.Irs();

        var unknown = rig.Preview($"set {line} ccy1 xyz");
        var product = rig.Preview($"set {line} product ccs");
        await rig.Enter($"set {line} kind1=empty quote1=empty kind2=empty quote2=empty");

        Assert.Equal($"InvalidValue: {line} currency1 is one of OTH, TST; xyz is not.", Read(unknown));
        Assert.StartsWith("NotEditable: product is not a field a command sets", Read(product));
        Assert.Contains("Both legs are at par; give one leg's quote so the other can be solved.", rig.Line(line).Issues);
    }

    [Fact] // ADR-0016 note 34: a default is set alone, by its own words, and undo takes it back; the others stay as they were
    public async Task A_default_is_set_alone_and_taken_back()
    {
        var rig = new Rig();

        var read = rig.Preview("set default currency tst");
        var said = await rig.Run(read);
        await rig.Enter("set default notional=250 default direction=rec");
        var set = rig.View.Defaults;
        await rig.Enter("undo");
        var undone = rig.View.Defaults;
        // A new line takes the default a command set: the product reads the same context the table wrote.
        var line = rig.Line(rig.Irs("5y 10 pay"));
        var removed = await rig.Enter("reset default currency");

        Assert.Equal(("workspace", "default currency: empty to TST", "Set workspace default currency to TST."), (read.Rows!.Title, read.Would, said));
        Assert.Equal(new LineDefaults("TST", 250e6, "receive"), set);
        Assert.Equal(new LineDefaults("TST", null, null), undone);
        Assert.Equal(("TST", "workspace"), (Text(line, "leg1.currency"), line.Defaulted["leg1.currency"]));
        Assert.Equal("Reset workspace default currency.", removed);
        Assert.Equal(new LineDefaults(null, null, null), rig.View.Defaults);
    }

    [Fact] // ADR-0016 note 34: a default is checked as the page's control checked it, and its words never mean a line's field
    public void A_default_is_checked_and_its_words_are_its_own()
    {
        var rig = new Rig();
        var line = rig.Irs();
        var grid = new ScreenView(RatesTables.Lines, Keys: [line]);

        var nothing = rig.Preview("set default notional 0");
        var unknown = rig.Preview("set default currency xyz");
        var old = rig.Preview("set workspace currency tst");
        var selected = rig.Preview("set notional 5mm", grid);

        Assert.Equal("InvalidValue: A default notional is more than 0.", Read(nothing));
        Assert.Equal("InvalidValue: workspace default currency is one of OTH, TST, or empty; xyz is not.", Read(unknown));
        Assert.StartsWith("UnknownField: workspace has no field currency; its fields are id, default currency, default notional, default direction", Read(old));
        Assert.Equal(("lines, 1 row", "notional: 10mm to 5mm"), (selected.Rows!.Title, selected.Would));
    }

    [Fact] // ADR-0016 note 35: a ticking workspace prices the revision a kernel verb wrote, and the one its undo left
    public async Task A_live_subscription_prices_the_revision_a_kernel_verb_wrote()
    {
        var rig = new Rig();
        var line = rig.Irs();
        var subscription = rig.Service.StartLive(rig.Id, "akira");
        var frames = rig.Live.Attach("akira", subscription).Frames;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        async Task<LiveDelivery> FrameAt(long revision)
        {
            while (true)
            {
                var frame = await frames.ReadAsync(timeout.Token);
                if (frame.InputRevision == revision) return frame;
            }
        }
        await rig.Market.AdvanceAsync();
        var first = await FrameAt(rig.State.Revision);

        await rig.Enter($"set {line} notional 20");
        var set = rig.State.Revision;
        var edited = await FrameAt(set);
        await rig.Enter("undo");
        var undone = await FrameAt(rig.State.Revision);

        Assert.True(first.InputRevision < set && set < undone.InputRevision);
        Assert.Equal([line], edited.Rows.Select(r => r.RowKey));
    }
}
