using System.Text.Json.Nodes;
using Qube.Composition.Application;
using Qube.Composition.Application.Products;
using Qube.Composition.Application.Tables;
using Qube.Composition.Application.Workspaces;
using Qube.Samples.Ccr;
using Qube.Samples.Portfolio;
using Qube.Samples.Xva;

namespace Qube.Composition.Tests;

// ADR-0016 note 49 of 2026-11-11: the pricer's PV legs and independent amounts, tables under the trades, and a trade's
// PV, a figure of its legs in the display currency. Every name and figure is synthetic. On 2026-10-26 the synthetic
// FX is 188.1 JPY, 1.2573 USD and 1.1583 EUR to the GBP, which the figures worked by hand here are worked with.
public class PricerLegTests
{
    static readonly DateOnly Day1 = new(2026, 10, 26);
    static readonly Caller Akira = new("akira", RunOrigin.Pricer);

    // Trades of the synthetic source for ABC/LE-01 on Day1: a swap in JPY (official PV 14,286,696), a swap in USD
    // (-768,420), an FX forward that sells JPY against USD (-51,839,837 JPY), and a new trade.
    const string Swap = "ABC-LE-01-E00004", UsdSwap = "ABC-LE-01-E00013", Forward = "ABC-LE-01-E00003", Draft = "DRAFT-1";

    sealed class Rig
    {
        public WorkspaceService Workspaces { get; }
        public PortfolioWorkspaces Service { get; }
        public CommandService Commands { get; }
        public CommandLines Lines { get; }
        public TableRegister Register { get; }
        public TableService Tables { get; }
        public string Id { get; }

        public Rig(string registration = "ABC/LE-01")
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
            var store = new InMemoryDataStore();
            Service = service = new PortfolioWorkspaces(Workspaces, store, new RunService(registry, store, TimeProvider.System), register);
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

    static string Read(LinePreview preview) => preview.Refused is { } refused ? $"{refused.Code}: {refused.Sentence}" : preview.Would!;

    [Fact] // ADR-0016 note 49: an existing swap's legs are the source's, in its currency, and add up to its official PV
    public void A_swaps_two_legs_add_up_to_its_official_PV()
    {
        var rig = new Rig();

        var legs = rig.Legs(Swap);

        Assert.Equal(["1 JPY", "2 JPY"], legs.Select(l => l[..5]));
        Assert.Equal(14_286_696m, rig.PvOfLeg($"{Swap}/1") + rig.PvOfLeg($"{Swap}/2"));
    }

    [Fact] // ADR-0016 note 49: an FX forward has a leg in each currency, which add up at the day's rate to its official PV
    public void An_FX_forwards_legs_are_one_in_each_currency()
    {
        var rig = new Rig();

        Assert.Equal(["1 JPY", "2 USD"], rig.Legs(Forward).Select(l => l[..5]));
        var inJpy = rig.PvOfLeg($"{Forward}/1") + rig.PvOfLeg($"{Forward}/2") * 188.1m / 1.2573m;
        Assert.Equal(-51_839_837m, Math.Round(inJpy, 0));
    }

    [Fact] // ADR-0016 note 49: a new trade has no leg, since no book holds it; its legs match nothing, and its PV is empty
    public void A_new_trade_has_no_leg_and_no_PV()
    {
        var rig = new Rig();

        Assert.Equal("NoRowMatches: DRAFT-1 has no legs.", Read(rig.Preview($"set legs {Draft} pv 1")));
        Assert.Null(rig.Pv(Draft));
    }

    [Fact] // ADR-0016 notes 46, 49: a leg is named by its key, or by how it ends, and its PV is set as any field is
    public async Task A_legs_PV_is_set_by_its_key()
    {
        var rig = new Rig();
        var other = rig.PvOfLeg($"{Swap}/2");

        var said = await rig.Run("set legs E00004/1 pv 250,000");

        Assert.Equal($"Set {Swap}/1 pv to 250000.", said);
        Assert.Equal((250_000m, other), (rig.PvOfLeg($"{Swap}/1"), rig.PvOfLeg($"{Swap}/2")));
    }

    [Theory] // ADR-0016 note 49: a leg's values are checked as its cells are, each refused by name
    [InlineData("set legs E00004/1 currency XXX", "InvalidValue")]
    [InlineData("set legs E00004/1 pv many", "InvalidValue")]
    [InlineData("set legs E00004/1 trade DRAFT-1", "NotEditable")]
    public void A_legs_value_that_cannot_be_read_is_refused(string line, string code) => Assert.StartsWith($"{code}: ", Read(new Rig().Preview(line)));

    [Fact] // ADR-0016 note 49: a trade's PV is its legs' in the display currency; in the legs' own it is the official PV
    public async Task A_trades_PV_is_its_legs_in_the_display_currency()
    {
        var rig = new Rig();

        // The display currency starts as JPY: the swap in JPY reads its official PV, and the one in USD is converted,
        // -768,420 USD x 188.1 / 1.2573.
        Assert.Equal((14_286_696m, -114_960_472.44m), (rig.Pv(Swap), rig.Pv(UsdSwap)));

        await rig.Run("set workspace currency USD");

        // 14,286,696 JPY x 1.2573 / 188.1.
        Assert.Equal((95_495.28m, -768_420m), (rig.Pv(Swap), rig.Pv(UsdSwap)));
    }

    [Fact] // ADR-0016 notes 47, 49: a condition reads the figure as any field, in the display currency
    public void A_condition_reads_a_trades_PV()
    {
        var rig = new Rig();

        var negative = rig.Keys("existing where product=IRS and pv<0");

        // The swap in USD holds a negative PV, the one in JPY a positive one.
        Assert.Contains(UsdSwap, negative);
        Assert.DoesNotContain(Swap, negative);
    }

    [Fact] // ADR-0016 notes 46, 47, 49: the description says legs are under trades and PV is a figure of them, in the display currency
    public async Task The_description_says_what_the_legs_are_under_and_what_PV_is_a_figure_of()
    {
        var rig = new Rig();

        var answer = await rig.Described();

        string? Under(string table) => answer["tables"]!.AsArray().Single(t => t!["table"]!.GetValue<string>() == table)!["under"]?.GetValue<string>();
        Assert.Equal((PortfolioTables.Trades, PortfolioTables.Trades, null), (Under(PortfolioTables.Legs), Under(PortfolioTables.Amounts), Under(PortfolioTables.Trades)));
        var pv = answer["fields"]!.AsArray().Single(f => f!["table"]!.GetValue<string>() == PortfolioTables.Trades && f["name"]!.GetValue<string>() == "pv")!;
        Assert.Equal((PortfolioTables.Legs, "JPY", true, "a number, alone or followed by what it is in: GBP, JPY, USD or EUR"),
            (pv["figureOf"]!.GetValue<string>(), pv["unit"]!.GetValue<string>(), pv["editable"]!.GetValue<bool>(), pv["written"]!.GetValue<string>()));
    }

    [Fact] // ADR-0016 note 49, the owner's rule of 2026-11-10: a PV written leaves the trade one leg of that amount, in the currency written
    public async Task A_PV_written_with_its_currency_leaves_the_trade_one_leg()
    {
        var rig = new Rig();
        var before = rig.Legs(Swap);

        var preview = rig.Preview("set E00004 pv -350,000 USD");
        var said = await rig.Run("set E00004 pv -350,000 USD");

        Assert.Equal("pv: 2 legs replaced by 1 leg, USD", preview.Would);
        Assert.Equal($"Set {Swap} pv to -350000 USD.", said);
        Assert.Equal(["3 USD -350000"], rig.Legs(Swap));
        // -350,000 USD x 188.1 / 1.2573, in the display currency.
        Assert.Equal(-52_362_204.72m, rig.Pv(Swap));

        await rig.Run("undo");

        Assert.Equal(before, rig.Legs(Swap));
        Assert.Equal(14_286_696m, rig.Pv(Swap));
    }

    [Fact] // ADR-0016 note 49: written alone, the amount is in the display currency, and the preview says which
    public async Task A_PV_written_alone_is_in_the_display_currency()
    {
        var rig = new Rig();

        Assert.Equal("pv: 2 legs replaced by 1 leg, JPY", rig.Preview("set E00004 pv 1,000,000").Would);
        await rig.Run("set E00004 pv 1,000,000");

        Assert.Equal(["3 JPY 1000000"], rig.Legs(Swap));
        Assert.Equal(1_000_000m, rig.Pv(Swap));
    }

    [Fact] // ADR-0016 notes 47, 49: a trade that holds that one leg alone holds the value, and nothing is written
    public async Task A_trade_that_holds_the_one_leg_already_is_not_written_again()
    {
        var rig = new Rig();
        await rig.Run("set E00004 pv -350,000 USD");
        var revision = rig.State.Revision;

        var again = rig.Preview("set E00004 pv -350,000 USD");
        var other = rig.Preview("set E00004 pv -350,000 EUR");

        Assert.Equal(($"Nothing would change: {Swap} already holds pv -350000 USD.", "pv: 1 leg replaced by 1 leg, EUR"), (again.Would, other.Would));
        Assert.Equal(revision, rig.State.Revision);
    }

    [Fact] // ADR-0016 note 49: a new trade has no leg to replace, and its PV written makes its first
    public async Task A_new_trades_PV_written_makes_its_first_leg()
    {
        var rig = new Rig();

        Assert.Equal("pv: 1 leg made, EUR", rig.Preview($"set {Draft} pv 250,000 EUR").Would);
        await rig.Run($"set {Draft} pv 250,000 EUR");

        Assert.Equal(["1 EUR 250000"], rig.Legs(Draft));
        // 250,000 EUR x 188.1 / 1.1583.
        Assert.Equal(40_598_290.60m, rig.Pv(Draft));
    }

    [Fact] // ADR-0016 notes 47, 49: reset brings the source's legs back, whatever was written, made or taken out
    public async Task Reset_brings_the_sources_legs_back()
    {
        var rig = new Rig();
        var before = rig.Legs(Swap);
        await rig.Run("set legs E00004/2 pv 1");
        await rig.Run("set E00004 pv -350,000 USD");
        await rig.Run("set E00004 pv -360,000 USD");
        Assert.Equal(["4 USD -360000"], rig.Legs(Swap));

        var said = await rig.Run("reset E00004 pv");

        Assert.Equal($"Reset {Swap} pv.", said);
        Assert.Equal(before, rig.Legs(Swap));
        Assert.Equal($"Nothing would change: {Swap} has no edit of pv.", rig.Preview("reset E00004 pv").Would);
    }

    [Fact] // ADR-0016 notes 47, 49: several trades' PVs are written by one command, each as the table says
    public async Task Several_trades_PVs_are_written_at_once()
    {
        var rig = new Rig();
        await rig.Run("set E00004 pv 0 USD");

        var preview = rig.Preview("set E00004, E00013 pv 0 USD");
        await rig.Run("set E00004, E00013 pv 0 USD");

        Assert.Equal("pv: 1 to 0 USD, 1 already 0 USD", preview.Would);
        Assert.Equal((0m, 0m), (rig.Pv(Swap), rig.Pv(UsdSwap)));
    }

    [Theory] // ADR-0016 note 47: a PV that cannot be read, or in a currency there is none of, is refused by name
    [InlineData("set E00004 pv -350,000 XXX")]
    [InlineData("set E00004 pv lots")]
    public void A_PV_that_cannot_be_read_is_refused(string line) => Assert.StartsWith("InvalidValue: ", Read(new Rig().Preview(line)));

    [Fact] // ADR-0016 note 48, 49: new makes a leg under a trade, numbered after its last, and remove takes it away
    public async Task New_makes_a_leg_under_a_trade_and_remove_takes_it_away()
    {
        var rig = new Rig();
        var before = rig.Legs(Swap);

        var preview = rig.Preview("new leg E00004 eur 250,000");
        var said = await rig.Run("new leg E00004 eur 250,000");

        Assert.Equal($"Makes {Swap}/3: PV leg.", preview.Would);
        Assert.Equal($"trade={Swap} eur 250,000", preview.Inputs!["request"]!["terms"]!.GetValue<string>());
        Assert.Equal($"Made {Swap}/3: PV leg.", said);
        Assert.Equal([.. before, "3 EUR 250000"], rig.Legs(Swap));

        await rig.Run("remove legs E00004/3");

        Assert.Equal(before, rig.Legs(Swap));
        Assert.Empty(PortfolioLegs.Made(rig.State));
    }

    [Fact] // ADR-0016 note 49: a made leg may say its notional by name, and its fields are set as a source's leg's are
    public async Task A_made_leg_holds_what_was_typed_and_is_set_as_any_leg()
    {
        var rig = new Rig();

        await rig.Run($"new leg {Draft} usd 100 notional=5mm");
        await rig.Run($"set legs {Draft}/1 pv=200 ccy=EUR");

        Assert.Equal(["1 EUR 200"], rig.Legs(Draft));
        var leg = rig.Register.Of(rig.State).Single(t => t.Descriptor.TableId == PortfolioTables.Legs).Table.Rows(rig.State).Single(r => r.Key == $"{Draft}/1");
        Assert.Equal(5_000_000m, leg.Value("notional")!.GetValue<decimal>());
    }

    [Theory] // ADR-0016 notes 48, 49: a leg is made under one of the workspace's trades, with its currency and its PV
    [InlineData("new leg trade=NOPE eur 250,000", "InvalidTerms")]
    [InlineData("new leg NOPE eur 250,000", "UnreadWord")]
    [InlineData("new leg E00004 250,000", "MissingTerm")]
    [InlineData("new leg E00004 eur", "MissingTerm")]
    public void A_leg_that_cannot_be_made_is_refused(string line, string code) => Assert.StartsWith($"{code}: ", Read(new Rig().Preview(line)));

    [Fact] // ADR-0016 note 48, 49: a source's leg removed is taken out by an edit of its own, which reset and undo bring back
    public async Task A_sources_leg_removed_comes_back_by_reset_or_undo()
    {
        var rig = new Rig();
        var before = rig.Legs(Swap);

        await rig.Run("remove legs E00004/1");
        Assert.Equal([before[1]], rig.Legs(Swap));
        await rig.Run("undo");
        Assert.Equal(before, rig.Legs(Swap));

        await rig.Run("remove legs E00004");
        Assert.Empty(rig.Legs(Swap));
        Assert.Null(rig.Pv(Swap));
        await rig.Run("reset E00004 pv");
        Assert.Equal(before, rig.Legs(Swap));
    }

    [Fact] // ADR-0016 note 49: an independent amount is removed and brought back, and never changed
    public async Task An_independent_amount_is_removed_and_never_changed()
    {
        var rig = new Rig();
        var held = rig.Register.Of(rig.State).Single(t => t.Descriptor.TableId == PortfolioTables.Amounts).Table.Rows(rig.State);
        var amount = held[0];
        var trade = amount.Under!;
        var before = rig.Amounts(trade);

        Assert.StartsWith("NotEditable: ", Read(rig.Preview($"set amounts {amount.Key} value 1")));
        await rig.Run($"remove amounts {amount.Key}");

        Assert.Equal(before.Skip(1), rig.Amounts(trade));
        await rig.Run($"reset {trade} independent amount");
        Assert.Equal(before, rig.Amounts(trade));
    }

    [Fact] // ADR-0016 note 49: a trade's independent amount is its amounts' in the display currency, and no command writes it
    public void A_trades_independent_amount_is_a_figure_no_command_writes()
    {
        var rig = new Rig();
        decimal? Figure(string trade) => rig.Register.Of(rig.State).Single(t => t.Descriptor.TableId == PortfolioTables.Trades).Table.Rows(rig.State).Single(r => r.Key == trade)
            .Value("independentAmount")?.GetValue<decimal>();

        // The swap holds one amount in JPY, the display currency. The forward E00006 holds two in USD, 719,290 and
        // 1,114,602: together 1,833,892 USD, which at 188.1 JPY and 1.2573 USD to the GBP is 274,361,795.28 JPY.
        Assert.Equal(["IA1 JPY 71506008"], rig.Amounts(Swap));
        Assert.Equal(71_506_008m, Figure(Swap));
        Assert.Equal(["IA1 USD 719290", "IA2 USD 1114602"], rig.Amounts("ABC-LE-01-E00006"));
        Assert.Equal(274_361_795.28m, Figure("ABC-LE-01-E00006"));
        Assert.Null(Figure(Draft));
        Assert.StartsWith("NotEditable: ", Read(rig.Preview($"set {Swap} independent amount 5")));
    }

    [Fact] // ADR-0016 note 49, ADR-0003: a leg's edit is carried to another value date as any edit is, and a made leg stays
    public async Task A_legs_edit_and_a_made_leg_stay_through_a_value_date_change()
    {
        var rig = new Rig();
        await rig.Run("set legs E00004/1 pv 250,000");
        await rig.Run("new leg E00004 eur 10");

        rig.Service.ChangeValueDate(rig.Id, rig.State.Revision, "akira", Day1.AddDays(1));

        Assert.Equal(["1 JPY 250000", "3 EUR 10"], [rig.Legs(Swap)[0], rig.Legs(Swap)[2]]);
    }

    [Fact] // ADR-0016 note 49: a new trade's legs are its own content, and stay with it through a counterparty change
    public async Task A_new_trades_leg_stays_through_a_counterparty_change()
    {
        var rig = new Rig();
        await rig.Run($"new leg {Draft} usd 100");
        await rig.Run("new leg E00004 eur 10");

        // The counterparty's scope holds one thing to drop, the leg made under its trade.
        rig.Service.ChangeCounterparty(rig.Id, rig.State.Revision, "akira", "ABC/LE-02", 1);

        Assert.Equal(["1 USD 100"], rig.Legs(Draft));
        Assert.Equal([$"leg:{Draft}/1"], PortfolioLegs.Made(rig.State).Select(c => c.Entity));
    }

    [Fact] // ADR-0016 notes 3, 49: a command with no words of its own is named by what it did to the leg, in words
    public async Task Undo_names_a_made_leg_in_words()
    {
        var rig = new Rig();
        await rig.Run("new leg E00004 eur 10");

        var (_, took) = rig.Workspaces.Undo(rig.Id, rig.State.Revision, "akira");

        Assert.Equal($"made leg {Swap}/3", took.Said);
    }

    [Fact] // ADR-0016 notes 38, 46: the AI reads a trade's legs by the trade's name, each cell as a command reads it back
    public async Task The_AI_reads_the_legs_under_a_trade()
    {
        var rig = new Rig();

        var result = await rig.Commands.InvokeAsync("tables.read", TableContracts.Version,
            new JsonObject { ["request"] = new JsonObject { ["workspaceId"] = rig.Id, ["rows"] = "legs E00004", ["fields"] = new JsonArray("ccy", "pv") } }, Akira, Guid.NewGuid().ToString(), default);
        var answer = Assert.IsType<CommandResult.Done>(result).Outputs["answer"]!.AsObject();

        Assert.Equal((PortfolioTables.Legs, 2), (answer["table"]!.GetValue<string>(), answer["count"]!.GetValue<int>()));
        Assert.Equal([$"{Swap}/1", $"{Swap}/2"], answer["rows"]!.AsArray().Select(r => r!["key"]!.GetValue<string>()));
        Assert.Equal(["currency", "pv"], answer["fields"]!.AsArray().Select(f => f!.GetValue<string>()));
        // The cells are text a command reads back: the two PVs, read as numbers, are the swap's official PV.
        var cells = answer["rows"]!.AsArray().Select(r => r!["cells"]!.AsArray().Select(c => c!.GetValue<string>()).ToArray()).ToArray();
        Assert.All(cells, c => Assert.Equal("JPY", c[0]));
        Assert.Equal(14_286_696m, cells.Sum(c => decimal.Parse(c[1], System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact] // ADR-0016 notes 16, 49: `ia` is the amounts' table whatever grid is behind the line, never a trade's field
    public void The_word_ia_names_the_amounts_with_a_trade_grid_behind_the_line()
    {
        var rig = new Rig();

        var preview = rig.Lines.Preview($"remove ia {Swap}/IA1", rig.Id, null, Akira, new LineView(PortfolioTables.Trades, "existing"));

        Assert.True(preview.Refused is null, preview.Refused?.Sentence);
        Assert.Equal("entity.remove", preview.Command!.Id);
        Assert.Equal([$"{Swap}/IA1"], rig.Keys($"ia {Swap}/IA1"));
    }

    [Fact] // ADR-0016 notes 48, 49: the trade typed by its name's ending makes the leg the first word would
    public async Task A_leg_is_made_under_the_trade_named_by_the_term()
    {
        var rig = new Rig();

        var said = await rig.Run("new leg trade=E00004 eur 5");

        Assert.Equal($"Made {Swap}/3: PV leg.", said);
        Assert.Equal("3 EUR 5", rig.Legs(Swap)[2]);
    }

    [Fact] // ADR-0016 note 49: a made leg takes the number after the trade's last, so a removed made leg's number is used again, with none of its edits
    public async Task A_removed_made_legs_number_is_used_again_with_none_of_its_edits()
    {
        var rig = new Rig();
        await rig.Run("new leg E00004 eur 10 notional=5mm");
        await rig.Run("set legs E00004/3 pv 20");
        await rig.Run("remove legs E00004/3");

        await rig.Run("new leg E00004 usd 5");

        Assert.Equal("3 USD 5", rig.Legs(Swap)[2]);
        var leg = rig.Register.Of(rig.State).Single(t => t.Descriptor.TableId == PortfolioTables.Legs).Table.Rows(rig.State).Single(r => r.Key == $"{Swap}/3");
        Assert.Null(leg.Value("notional"));
        Assert.DoesNotContain(rig.State.Overrides.Keys, t => t.Entity == $"leg:{Swap}/3");
    }

    [Fact] // ADR-0016 note 49: the workspace's count of edits holds a leg made, as it holds the legs taken out for it
    public async Task The_workspaces_count_of_edits_holds_a_leg_made()
    {
        var rig = new Rig();
        await rig.Run($"set {Draft} pv 250,000 EUR");
        var one = rig.Service.View(rig.Id, "akira").Overrides;
        await rig.Run("set E00004 pv -350,000 USD");

        Assert.Equal((1, 4), (one, rig.Service.View(rig.Id, "akira").Overrides));
    }

    [Fact] // ADR-0016 note 49, ADR-0010 §3: the Changes tab lists what was done to a trade's legs, each named in words, and resets each
    public async Task The_Changes_tab_lists_what_was_done_to_a_trades_legs()
    {
        var rig = new Rig();
        var before = rig.Legs(Swap);
        await rig.Run("set E00004 pv -350,000 USD");
        await rig.Run("set legs E00004/3 pv -360,000");

        var changes = rig.Service.Changes(rig.Id, "akira");

        Assert.Equal([$"leg {Swap}/1: taken out", $"leg {Swap}/2: taken out", $"leg {Swap}/3: made: -350000 USD", $"leg {Swap}/3 pv: -360000"],
            changes.Common.Select(c => $"{c.Title}: {c.Text}"));
        Assert.Equal((4, 4), (changes.Count, rig.Service.View(rig.Id, "akira").Changes));
        Assert.All(changes.Common, c => Assert.False(c.Editable));

        rig.Service.Resolve(rig.Id, rig.State.Revision, "akira", $"leg:{Swap}/1", "out", "reset");
        rig.Service.Resolve(rig.Id, rig.State.Revision, "akira", $"leg:{Swap}/3", "*", "reset");

        Assert.Equal([before[0]], rig.Legs(Swap));
        Assert.Equal([$"leg {Swap}/2"], rig.Service.Changes(rig.Id, "akira").Common.Select(c => c.Title));
    }

    [Fact] // ADR-0016 note 49: a grid's rows carry each trade's PV, and the grid sorts by it
    public void A_page_of_rows_carries_each_trades_PV_and_sorts_by_it()
    {
        var rig = new Rig();

        var page = rig.Service.Page(rig.Id, "akira", 0, 100, [new RowSort("pv", "descending")], "existing");

        var pvs = page.Rows.Select(r => r.Pv).ToList();
        Assert.Equal(14_286_696m, page.Rows.Single(r => r.TradeId == Swap).Pv);
        Assert.All(pvs, pv => Assert.NotNull(pv));
        Assert.Equal(pvs.OrderByDescending(pv => pv), pvs);
    }

    [Fact] // ADR-0016 note 49: a page says the currency its PVs are in, so a header never names another than the rows under it hold
    public void A_page_of_rows_says_the_currency_its_PVs_are_in()
    {
        var rig = new Rig();
        var inYen = rig.Service.Page(rig.Id, "akira", 0, 100, [], "existing");

        rig.Service.SetDisplayCurrency(rig.Id, rig.State.Revision, "akira", "USD");
        var inDollars = rig.Service.Page(rig.Id, "akira", 0, 100, [], "existing");

        // 14,286,696 JPY at 188.1 JPY and 1.2573 USD to the GBP is 95,495.28 USD.
        Assert.Equal(("JPY", 14_286_696m), (inYen.PvCurrency, inYen.Rows.Single(r => r.TradeId == Swap).Pv));
        Assert.Equal(("USD", 95_495.28m), (inDollars.PvCurrency, inDollars.Rows.Single(r => r.TradeId == Swap).Pv));
    }

    [Fact] // ADR-0016 note 49, ADR-0010 §2: a PV that differs from what the source's legs give is marked with Original's, as any value
    public async Task A_PV_that_differs_from_the_sources_is_marked_with_Originals()
    {
        var rig = new Rig();
        EffectiveTrade Row(string trade) => rig.Service.Page(rig.Id, "akira", 0, 100, [], "existing").Rows.Single(r => r.TradeId == trade);
        Assert.Null(Row(Swap).Original);

        await rig.Run("set E00004 pv 1,000,000");

        Assert.Equal((1_000_000m, 14_286_696m), (Row(Swap).Pv, Row(Swap).Original!["pv"]!.GetValue<decimal>()));
        Assert.Null(Row(UsdSwap).Original);
    }

    [Fact] // ADR-0016 note 49: a leg and an independent amount of one trade never share an id
    public void A_leg_and_an_independent_amount_never_share_an_id()
    {
        var rig = new Rig();

        var amounts = rig.Register.Of(rig.State).Single(t => t.Descriptor.TableId == PortfolioTables.Amounts).Table.Rows(rig.State);

        var legs = rig.Register.Of(rig.State).Single(t => t.Descriptor.TableId == PortfolioTables.Legs).Table.Rows(rig.State).Select(l => l.Key).ToHashSet();

        Assert.NotEmpty(amounts);
        Assert.All(amounts, a => Assert.StartsWith($"{a.Under}/IA", a.Key));
        Assert.DoesNotContain(amounts, a => legs.Contains(a.Key));
        // Each kind is a kind of entity of its own, so an edit of one never reads as the other's.
        Assert.Equal(amounts.Count + legs.Count, amounts.Select(a => $"ia:{a.Key}").Concat(legs.Select(l => $"leg:{l}")).Distinct().Count());
    }
}
