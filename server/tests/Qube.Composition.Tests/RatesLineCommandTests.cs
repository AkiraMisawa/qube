using System.Text.Json.Nodes;
using Qube.Composition.Application;
using Qube.Composition.Application.Products;
using Qube.Composition.Application.Settings;
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

        /// <param name="settings">The users' settings a new workspace's defaults are copied from (ADR-0017 §3); none for a test of the workspace's own.</param>
        public Rig(UserSettings? settings = null)
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
            Service = service = new RatesWorkspaces(Workspaces, store, Runs, Live, Market, Conventions, provider, settings);
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

        /// <summary>Invokes a command with a request of the workspace alone, as the AI may: answers its one output.</summary>
        public async Task<JsonNode> Ask(string capability, string version, string port = "request")
        {
            var result = await Commands.InvokeAsync(capability, version, new JsonObject { [port] = new JsonObject { ["workspaceId"] = Id } }, Akira, Guid.NewGuid().ToString(), default);
            return result is CommandResult.Done done ? done.Outputs.Values.Single()!
                : throw new Xunit.Sdk.XunitException($"{capability} was refused: {string.Join(" ", ((CommandResult.Refused)result).Issues.Select(i => i.Message))}");
        }

        /// <summary>The workspace's Runs, the newest first.</summary>
        public IReadOnlyList<RunRecord> RunsOf() => Runs.List(new RunQuery("akira", 0, 20, Id)).Runs;
    }

    static IEnumerable<string> Said(LinePreview preview) => preview.Arguments!.Select(a => $"{a.Name} {a.Text}");

    static IEnumerable<string> Offered(LinePreview preview) => preview.Complete.Select(c => c.Value);

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

    [Fact] // ADR-0016 note 34: the frequencies new irs names are fields by the same names, each its leg's own, and refused by name on a line with no such leg
    public async Task An_irs_frequencies_are_fields_by_the_names_new_gives_them()
    {
        var rig = new Rig();
        var (irs, ccs) = (rig.Irs(), rig.Ccs());
        var made = (Text(rig.Line(irs), "leg1.paymentFrequency"), Text(rig.Line(irs), "leg2.paymentFrequency"));

        var fixedLeg = await rig.Enter($"set {irs} fixed 6m");
        var floatingLeg = await rig.Enter($"set {irs} floatFrequency=3m");
        var held = (Text(rig.Line(irs), "leg1.paymentFrequency"), Text(rig.Line(irs), "leg2.paymentFrequency"));
        var onCcs = rig.Preview($"set {ccs} fixed 6m");
        rig.Service.EditLine(rig.Id, rig.State.Revision, "akira", irs, "leg2.floating", false);
        var noFloatingLeg = rig.Preview($"set {irs} float 1m");

        Assert.Equal(("12m", "12m"), made);
        Assert.Equal(($"Set {irs} fixed frequency to 6m.", $"Set {irs} float frequency to 3m."), (fixedLeg, floatingLeg));
        Assert.Equal(("6m", "3m"), held);
        Assert.Equal($"NotApplicable: {ccs} is a cross-currency swap, which has a payment frequency on each leg: set frequency1 or frequency2.", Read(onCcs));
        Assert.Equal($"NotApplicable: Leg 2 of {irs} is fixed, so the line has no floating leg: set frequency2.", Read(noFloatingLeg));
    }

    [Fact] // ADR-0016 note 34: a reset by a term is the reset of the legs' fields that hold it, and a term the line does not have is refused as set refuses it
    public async Task Reset_by_a_term_puts_both_legs_back_and_is_refused_where_the_term_does_not_apply()
    {
        var rig = new Rig();
        var (irs, ccs) = (rig.Irs(), rig.Ccs());
        await rig.Enter($"set {irs} notional 30");
        var written = (Number(rig.Line(irs), "leg1.notional"), Number(rig.Line(irs), "leg2.notional"));

        var reset = await rig.Enter($"reset {irs} notional");
        var back = (Number(rig.Line(irs), "leg1.notional"), Number(rig.Line(irs), "leg2.notional"));
        var refused = rig.Preview($"reset {ccs} notional");

        Assert.Equal((30e6, 30e6), written);
        Assert.Equal($"Reset {irs} notional.", reset);
        Assert.Equal((10e6, 10e6), back);
        Assert.Equal($"NotApplicable: {ccs} is a cross-currency swap, which has a notional on each leg: set notional1 or notional2.", Read(refused));
    }

    [Fact] // ADR-0016 notes 34 and 36: no reading fails; a term that depends on a flag the grid left empty is refused by name, and written once the flag is said
    public async Task A_term_is_refused_by_name_while_its_legs_floating_flag_is_empty()
    {
        var rig = new Rig();
        var line = rig.Irs();
        rig.Service.EditLine(rig.Id, rig.State.Revision, "akira", line, "leg1.floating", null);
        var revision = rig.State.Revision;

        var rate = rig.Preview($"set {line} rate 1.5%");
        var invoked = await rig.Commands.InvokeAsync(EntitySet.Id, TableContracts.Version,
            new JsonObject { ["request"] = new JsonObject { ["workspaceId"] = rig.Id, ["rows"] = line, ["set"] = "rate 1.5%" } }, Akira, Guid.NewGuid().ToString(), default);
        var unwritten = rig.State.Revision;
        // Reading the row's own values does not depend on the flag: another field of the line is set as ever.
        var end = await rig.Enter($"set {line} end 7y");
        await rig.Enter($"set {line} float1 off");
        var said = await rig.Enter($"set {line} rate 1.5%");

        Assert.Equal($"NotSaid: {line} does not say whether leg 1 floats: set float1 on or off first.", Read(rate));
        Assert.Equal("NotSaid", Assert.Single(Assert.IsType<CommandResult.Refused>(invoked).Issues).Code);
        Assert.Equal(revision, unwritten);
        Assert.Equal($"Set {line} end to 7y.", end);
        Assert.Equal(($"Set {line} rate to 1.5%.", "FixedRatePercent", 1.5), (said, Text(rig.Line(line), "leg1.quoteKind"), Number(rig.Line(line), "leg1.quote")));
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

    [Fact] // ADR-0017 §3: a new workspace starts with the user's defaults for a new line, each from their settings; one set in it since is its own
    public async Task A_new_workspace_starts_with_the_users_line_defaults()
    {
        var settings = new UserSettings([new RatesNewLineSettings(Conventions)], new InMemoryUserSettingStore(), TimeProvider.System);
        settings.Set("akira", "new line currency", "tst");
        settings.Set("akira", "new line notional", "250");
        settings.Set("akira", "new line direction", "rec");
        var rig = new Rig(settings);

        var made = rig.View.Defaults;
        var read = rig.Preview("new irs 5y");
        var line = rig.Line(rig.Irs("5y"));
        await rig.Enter("set default notional=100");
        var edited = rig.View.Defaults;
        var next = rig.Line(rig.Irs("7y"));
        settings.Set("akira", "new line currency", "oth");

        Assert.Equal(new LineDefaults("TST", 250e6, "receive") { FromSettings = ["currency", "notional", "direction"] }, made);
        Assert.Contains("currency TST from settings", read.Would);
        Assert.Equal(("TST", "settings", "settings"), (Text(line, "leg1.currency"), line.Defaulted["leg1.currency"], line.Defaulted["leg1.notional"]));
        Assert.Equal(new LineDefaults("TST", 100e6, "receive") { FromSettings = ["currency", "direction"] }, edited);
        Assert.Equal(("settings", "workspace"), (next.Defaulted["leg1.currency"], next.Defaulted["leg1.notional"]));
        // The setting moved after the workspace was made: the workspace keeps what it was made with.
        Assert.Equal("TST", rig.View.Defaults.Currency);
    }

    [Fact] // ADR-0017 §3: a default the user set that is no longer one Rates takes refuses the making, by the setting's name
    public void An_unreadable_line_default_refuses_the_making()
    {
        var store = new InMemoryUserSettingStore();
        store.Put("akira", new StoredSetting(RatesNewLineSettings.Currency, JsonValue.Create("XYZ"), DateTimeOffset.UnixEpoch));
        var settings = new UserSettings([new RatesNewLineSettings(Conventions)], store, TimeProvider.System);

        var refused = Assert.Throws<WorkspaceRefused>(() => new Rig(settings));

        Assert.Equal(("UnreadableSetting", "Your new line currency setting, XYZ, is no longer one of OTH, TST: set it again, or type settings reset new line currency."), (refused.Code, refused.Message));
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

    [Fact] // ADR-0016 note 36: `new <product> <terms>` says the line it would make and what it would fill that was not typed, and Enter makes that line
    public async Task New_says_the_line_its_terms_would_make_and_makes_it()
    {
        var rig = new Rig();

        var read = rig.Preview("new irs 5y tst 10 pay");
        var unwritten = rig.View.Lines.Count;
        var said = await rig.Run(read);
        var line = rig.View.Lines.Single();
        var back = await rig.Enter("undo");

        Assert.Equal(EntityCreate.Id, read.Command!.Id);
        Assert.Equal(["product irs", "end 5y", "currency TST", "notional 10mm", "direction pay"], Said(read));
        Assert.Equal($"Makes {line.Line}: Interest rate swap. Not typed: index TEST-ON from profile:test.TST.TEST-ON@1, spread 0% from C47, "
            + "fixedFrequency 12m from profile:test.TST.TEST-ON@1, floatFrequency 12m from profile:test.TST.TEST-ON@1.", read.Would);
        Assert.Equal(0, unwritten);
        Assert.Equal($"Made {line.Line}: Interest rate swap.", said);
        Assert.Equal(("5y", "TST", 10e6, "pay", true), (Text(line, "end"), Text(line, "leg1.currency"), Number(line, "leg1.notional"), Text(line, "direction"), line.Priceable));
        Assert.Equal($"Took back: made {line.Line}. redo puts it back.", back);
        Assert.Empty(rig.View.Lines);
    }

    [Fact] // ADR-0016 note 36: what `new` cannot make is refused by name before Enter, and nothing is made
    public void New_refuses_by_name_what_it_cannot_make()
    {
        var rig = new Rig();

        var none = rig.Preview("new");
        var unknown = rig.Preview("new fra 5y");
        var unread = rig.Preview("new irs 5y tst 10 pay sideways");
        var missing = rig.Preview("new irs 5y");
        var one = rig.Preview("new irs 5y tst pay");
        // A swap with no spread reads, and the line resolver's economic check refuses it: nothing defaults one (ADR-0008 §7).
        var economic = rig.Preview("new ccs 5y tst oth 10 20 pay");

        Assert.Equal("NoProduct: new takes a product: ccs or irs.", Read(none));
        Assert.Equal("UnknownProduct: fra is no product new makes here: ccs or irs.", Read(unknown));
        Assert.Equal(("fra", 4, 7), (unknown.Refused!.Word, unknown.Refused.Start, unknown.Refused.End));
        Assert.Equal(["ccs", "irs"], unknown.Refused.Candidates);
        Assert.Equal("UnreadWord: 'sideways' is no term of irs left to fill; name it as name=value.", Read(unread));
        Assert.Equal("MissingTerm: irs still needs currency, index, notional, direction, fixedFrequency and floatFrequency: type them, or set a default for each.", Read(missing));
        Assert.Equal("MissingTerm: irs still needs notional: type it, or set a default for it.", Read(one));
        Assert.Equal("InvalidTerms: Both legs are at par; give one leg's quote so the other can be solved.", Read(economic));
        Assert.Empty(rig.View.Lines);
    }

    [Fact] // ADR-0016 note 36: `new` completes its products, then the terms a product still takes, each by its form
    public void New_completes_products_and_terms()
    {
        var rig = new Rig();

        var products = rig.Preview("new ");
        var half = rig.Preview("new i");
        var terms = rig.Preview("new irs ");
        var second = rig.Preview("new ccs 5y tst ");
        var typed = rig.Preview("new irs 5y tst 10 r");

        Assert.Equal(["ccs", "irs"], Offered(products));
        Assert.Equal([new LineCompletion("irs", 4, "Interest rate swap")], half.Complete);
        Assert.Equal(["1y", "2y", "3y", "5y", "7y", "10y", "15y", "20y", "30y", "OTH", "TST", "OTHER-ON", "TEST-ON", "pay", "receive",
            "spread=", "fixedFrequency=", "floatFrequency=", "start=", "risk="], Offered(terms));
        // A sequence offers its next field alone: leg 2's currency once leg 1's is typed.
        Assert.Equal(["OTH", "TST", "pay", "receive", "index1=", "index2=", "spread1=", "spread2=", "frequency1=", "frequency2=", "start=", "risk="], Offered(second));
        Assert.Equal([new LineCompletion("receive", 18, "Pay or receive the fixed leg"), new LineCompletion("risk=", 18, "Risk")], typed.Complete);
    }

    [Fact] // ADR-0016 note 36: `price` names the market snapshot its reading read, and Enter prices on that one, whatever was published since
    public async Task Price_names_the_market_it_read_and_prices_on_it()
    {
        var rig = new Rig();
        var line = rig.Irs();
        var early = rig.Preview("price");
        var first = await rig.Market.AdvanceAsync();

        var read = rig.Preview("price");
        var later = await rig.Market.AdvanceAsync();
        var said = await rig.Run(read);
        var run = Assert.Single(rig.RunsOf());
        // Asked with no market named, as the AI may: the latest one, which the answer names.
        var asked = await rig.Ask("rates.workspace.price", RatesContracts.Version);
        var empty = rig.Service.AddLine(rig.Id, rig.State.Revision, "akira", RatesProduct.Ccs).Line;
        var partial = rig.Preview("price");

        Assert.Equal("NoMarket: No market snapshot has been published yet; price waits for one.", Read(early));
        Assert.Equal($"Prices 1 line on market {first.Id}, as one Run.", read.Would);
        Assert.Equal($"Price was accepted as Run {run.RunId}.", said);
        Assert.Equal(first.Id, run.Input["marketSnapshotId"]!.GetValue<string>());
        Assert.NotEqual(first.Id, later.Id);
        Assert.Equal(later.Id, asked["marketSnapshotId"]!.GetValue<string>());
        Assert.Equal($"Prices 1 of 2 lines on market {later.Id}, as one Run. Left out: {empty}.", partial.Would);
        Assert.Equal([line], rig.View.Lines.Where(l => l.Priceable).Select(l => l.Line));
    }

    [Fact] // ADR-0016 note 36: `price` refuses before Enter what Price would refuse, and submits nothing
    public async Task Price_with_nothing_to_price_is_refused_by_its_reading()
    {
        var rig = new Rig();
        await rig.Market.AdvanceAsync();

        var nothing = rig.Preview("price");
        var words = rig.Preview("price now");

        Assert.Equal("NothingToPrice: No line in this workspace can be priced.", Read(nothing));
        Assert.Equal("UnreadWords: price takes no words after it.", Read(words));
        Assert.Empty(rig.RunsOf());
    }

    [Fact] // ADR-0016 note 36, spine 6: `capture` saves the frame its reading named, with its time, whatever frame was shown since
    public async Task Capture_saves_the_frame_its_reading_named()
    {
        var rig = new Rig();
        rig.Irs();
        var idle = rig.Preview("capture");
        await rig.Enter("live start");
        var unshown = rig.Preview("capture");
        var frames = rig.Live.Attach("akira", rig.View.Live!.SubscriptionId).Frames;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await rig.Market.AdvanceAsync();
        var first = await frames.ReadAsync(timeout.Token);

        var read = rig.Preview("capture");
        await rig.Market.AdvanceAsync();
        var second = await frames.ReadAsync(timeout.Token);
        var said = await rig.Run(read);
        var run = Assert.Single(rig.RunsOf());
        // Asked with no frame named, as the AI may: the frame shown as it runs.
        var asked = await rig.Ask("rates.frame.capture", RatesContracts.Version);

        Assert.Equal("NotRunning: Live is not running: capture saves a frame a running subscription shows.", Read(idle));
        Assert.Equal("NoFrame: The subscription has shown no frame yet.", Read(unshown));
        Assert.Equal(($"Captures the frame of market {first.Market.SnapshotId} at revision {first.InputRevision}, as a Run.", first.CompletedAt), (read.Would, read.At));
        Assert.Equal($"Captured the frame of market {first.Market.SnapshotId} at revision {first.InputRevision} as Run {run.RunId}.", said);
        Assert.Equal(first.Market.SnapshotId, run.Input["marketSnapshotId"]!.GetValue<string>());
        Assert.NotEqual(first.Market.SnapshotId, second.Market.SnapshotId);
        Assert.Equal(second.Market.SnapshotId, rig.RunsOf().Single(r => r.RunId == asked["runId"]!.GetValue<string>()).Input["marketSnapshotId"]!.GetValue<string>());
        // The answer names the market of the frame it saved, which a page titles the Run by.
        Assert.Equal(second.Market.SnapshotId, asked["marketSnapshotId"]!.GetValue<string>());
    }

    [Fact] // ADR-0016 note 36: a frame is named whole or not at all, and by the workspace's own subscription
    public async Task Capture_refuses_a_frame_named_in_part()
    {
        var rig = new Rig();
        rig.Irs();
        await rig.Enter("live start");

        var part = await rig.Commands.InvokeAsync("rates.frame.capture", RatesContracts.Version,
            new JsonObject { ["request"] = new JsonObject { ["workspaceId"] = rig.Id, ["subscriptionId"] = rig.View.Live!.SubscriptionId, ["inputRevision"] = rig.State.Revision } },
            Akira, Guid.NewGuid().ToString(), default);

        Assert.Equal("InvalidInput", Assert.Single(Assert.IsType<CommandResult.Refused>(part).Issues).Code);
        Assert.Empty(rig.RunsOf());
    }

    [Fact] // ADR-0016 note 36: `live start` and `live stop` are typed, say what they would do, and answer what they did
    public async Task Live_starts_and_stops_by_its_words()
    {
        var rig = new Rig();
        rig.Irs();

        var read = rig.Preview("live start");
        var started = await rig.Run(read);
        var running = rig.View.Live;
        var joins = rig.Preview("live start");
        var again = await rig.Run(joins);
        var stops = rig.Preview("live stop");
        var stopped = await rig.Run(stops);
        var idle = rig.Preview("live stop");

        Assert.Equal("Starts ticking this workspace: 1 line would be priced on each market update.", read.Would);
        Assert.Equal("Live is running.", started);
        Assert.True(running is { Running: true });
        Assert.Equal("Live is running on this workspace already; nothing more would start.", joins.Would);
        Assert.Equal("Live is running.", again);
        Assert.Equal("Stops ticking this workspace, for every page watching it.", stops.Would);
        Assert.Equal("Live stopped, for every page watching it.", stopped);
        Assert.Equal("NotRunning: Live is not running on this workspace.", Read(idle));
        Assert.True(rig.View.Live is { Running: false });
    }

    [Fact] // ADR-0016 note 36: `add line <product>` adds an empty line of the product, for the grid to fill
    public async Task Add_line_adds_an_empty_line_of_a_product()
    {
        var rig = new Rig();

        var read = rig.Preview("add line ccs");
        var said = await rig.Run(read);
        var line = rig.View.Lines.Single();
        var none = rig.Preview("add line");
        var unknown = rig.Preview("add line fra");

        Assert.Equal(["product ccs"], Said(read));
        Assert.Equal($"Adds {line.Line}, an empty ccs line to fill in the grid.", read.Would);
        Assert.Equal($"Added {line.Line}, an empty ccs line.", said);
        Assert.Equal(("ccs", false), (Text(line, "product"), line.Priceable));
        Assert.Equal("NoProduct: add line takes a product: irs or ccs.", Read(none));
        Assert.Equal("UnknownProduct: fra is no product a line is added for: irs or ccs.", Read(unknown));
        Assert.Equal(["irs", "ccs"], Offered(rig.Preview("add line ")));
    }
}
