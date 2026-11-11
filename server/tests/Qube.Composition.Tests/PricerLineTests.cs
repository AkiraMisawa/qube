using System.Text.Json.Nodes;
using Qube.Composition.Application;
using Qube.Composition.Application.Saved;
using Qube.Composition.Application.Tables;
using Qube.Composition.Application.Workspaces;
using Qube.Samples.Ccr;
using Qube.Samples.Portfolio;
using Qube.Samples.Xva;

namespace Qube.Composition.Tests;

// ADR-0016 §3, §5 and its notes 27 to 29 of 2026-11-11: the pricer's own words on the line. A short form is read as
// the kernel line it stands for; a domain verb reads its own line into the request its capability takes, and its
// reading is a trial of the command, with nothing written. Every name and figure is synthetic.
public class PricerLineTests
{
    static readonly DateOnly Day1 = new(2026, 10, 26);
    static readonly Caller Akira = new("akira", RunOrigin.Pricer);

    sealed class Rig
    {
        public WorkspaceService Workspaces { get; }
        public PortfolioWorkspaces Service { get; }
        public CommandService Commands { get; }
        public CompositionRegistry Registry { get; }
        public CommandLines Lines { get; }
        public RunService Runs { get; }
        public IDataStore Store { get; }
        public string Id { get; }

        public Rig(int existing = 40, int? caseCap = null, Pv01Provider? pv01 = null, string registration = "ABC/LE-01")
        {
            var register = new MeasureRegister([new XvaMeasures(pv01 ?? Pv01Provider.Simulated(TimeSpan.Zero)), new CcrMeasures()]);
            Workspaces = new WorkspaceService(new Dictionary<string, IEditRules> { [PortfolioEditRules.App] = new PortfolioEditRules(register) }, TimeProvider.System);
            var registered = new TableRegister([new PortfolioTables(register)]);
            var tables = new TableService(registered, Workspaces);
            PortfolioWorkspaces? service = null;
            var registry = Registry = new CompositionRegistry([new PortfolioModule(), new XvaModule(), new CcrModule(), new TablesModule(() => tables), new WorkspaceCommandsModule(() => Workspaces),
                new PortfolioCommandsModule(() => service ?? throw new InvalidOperationException("The pricer's service is not built yet."), register)]);
            var store = new InMemoryDataStore();
            Runs = new RunService(registry, store, TimeProvider.System);
            Service = service = new PortfolioWorkspaces(Workspaces, store, Runs, register,
                caseCap is { } cap ? new PortfolioOptions(cap) : null,
                new SavedItems(new InMemorySavedItemStore(), new Desks(new Dictionary<string, string> { ["akira"] = "rates" }), TimeProvider.System));
            Commands = new CommandService(registry, store);
            Store = store;
            Lines = new CommandLines(registry, Workspaces, registered, [new PortfolioShortForms()]);
            Id = Service.Create("akira", registration, Day1, existing).WorkspaceId;
        }

        public WorkspaceState State => Workspaces.Find(Id)!;

        public PortfolioWorkspaceView View => Service.View(Id, "akira");

        public LinePreview Preview(string line, LineView? view = null) => Lines.Preview(line, Id, null, Akira, view);

        /// <summary>Invokes the command a preview read, with the inputs it answered, as Enter does; answers what it said.</summary>
        public async Task<string> Run(LinePreview preview, RunOrigin origin = RunOrigin.Pricer)
        {
            var result = await Commands.InvokeAsync(preview.Command!.Id, preview.Command.Version, preview.Inputs!, Akira with { Origin = origin }, Guid.NewGuid().ToString(), default);
            return result is CommandResult.Done done ? done.Outputs["answer"]!["said"]!.GetValue<string>()
                : throw new Xunit.Sdk.XunitException($"{preview.Command.Id} was refused: {string.Join(" ", ((CommandResult.Refused)result).Issues.Select(i => i.Message))}");
        }

        /// <summary>The command by its id with a request, as a button or the AI invokes it; answers what it said, or the code it was refused with.</summary>
        public async Task<string> Invoke(string capability, JsonObject request, RunOrigin origin = RunOrigin.Pricer)
        {
            request["workspaceId"] = Id;
            var result = await Commands.InvokeAsync(capability, PortfolioContracts.Version, new JsonObject { ["request"] = request }, Akira with { Origin = origin }, Guid.NewGuid().ToString(), default);
            return result is CommandResult.Done done ? done.Outputs["answer"]!["said"]!.GetValue<string>()
                : $"{string.Join(" ", ((CommandResult.Refused)result).Issues.Select(i => $"{i.Code}: {i.Message}"))}";
        }

        /// <summary>Where each issue of a request the command refuses is, as its caller is told.</summary>
        public async Task<IReadOnlyList<string>> Paths(string capability, JsonObject request)
        {
            request["workspaceId"] = Id;
            var result = await Commands.InvokeAsync(capability, PortfolioContracts.Version, new JsonObject { ["request"] = request }, Akira, Guid.NewGuid().ToString(), default);
            return [.. Assert.IsType<CommandResult.Refused>(result).Issues.Select(i => i.Path)];
        }
    }

    static string Read(LinePreview preview) => preview.Refused is { } refused ? $"{refused.Code}: {refused.Sentence}" : preview.Would!;

    static string Offered(LinePreview preview) => string.Join(" | ", preview.Complete.Select(c => c.Detail is null ? c.Value : $"{c.Value} ({c.Detail})"));

    static readonly LineView ExistingGrid = new(PortfolioTables.Trades, "existing");
    static readonly LineView CsaGrid = new(PortfolioTables.Csas, Keys: ["CSA-A"]);

    [Theory] // ADR-0016 note 29: before, after and currency stand for a line of set, with the grid typed or the one behind the line
    [InlineData("before all existing", false, "set existing before on")]
    [InlineData("after none new", false, "set new after off")]
    [InlineData("AFTER  None", true, "set existing after off")]
    [InlineData("before all new", true, "set new before on")]
    [InlineData("currency usd", false, "set workspace currency USD")]
    public void The_pricers_short_forms_stand_for_a_line_of_set(string line, bool onExisting, string stands)
    {
        var rig = new Rig();
        var view = onExisting ? ExistingGrid : null;

        var typed = rig.Preview(line, view);
        var meant = rig.Preview(stands, view);

        Assert.Null(typed.Refused);
        Assert.Equal((stands, "entity.set"), (typed.Expands, typed.Command!.Id));
        Assert.Equal((meant.Inputs!.ToJsonString(), meant.Rows!.Title, meant.Would), (typed.Inputs!.ToJsonString(), typed.Rows!.Title, typed.Would));
    }

    [Fact] // ADR-0016 note 29: what runs, and what undo names, is the kernel command by the expanded line
    public async Task A_short_form_runs_the_kernel_command_and_undo_names_its_line()
    {
        var rig = new Rig();
        var existing = rig.View.Trades - rig.View.NewTrades;

        var said = await rig.Run(rig.Preview("after none", ExistingGrid));
        var other = new Rig();
        var meant = await other.Run(other.Preview("set existing after off"));

        Assert.Equal(meant, said);
        Assert.Equal(0, rig.Service.Page(rig.Id, "akira", 0, 100_000, [], "existing").Rows.Count(t => t.After));
        Assert.True(existing > 0);
        Assert.StartsWith("Takes back: set existing after off (you,", rig.Preview("undo").Would);
    }

    [Theory] // ADR-0016 note 29: a short form is refused by its own rule, by name, with what it could have been
    [InlineData("before", false, "NoChoice: before takes all or none, then new or existing.", "all, none")]
    [InlineData("before some", false, "UnknownChoice: before takes all or none, not some.", "all, none")]
    [InlineData("before all", false, "NoGrid: before all takes new or existing, or the trade grid the keyboard is in.", "new, existing")]
    [InlineData("after none csas", false, "UnknownGrid: after none takes new or existing, not csas.", "new, existing")]
    [InlineData("after none new existing", false, "UnreadWords: after none new is the whole line: existing is not read.", "")]
    [InlineData("currency", false, "NoCurrency: currency takes the currency to show the figures in: GBP, JPY, USD, EUR.", "GBP, JPY, USD, EUR")]
    public void A_short_form_of_the_pricers_is_refused_by_its_own_rule(string line, bool onCsas, string refused, string candidates)
    {
        var rig = new Rig();

        var preview = rig.Preview(line, onCsas ? CsaGrid : null);

        Assert.Equal((refused, candidates), (Read(preview), string.Join(", ", preview.Refused!.Candidates)));
        Assert.Null(preview.Command);
    }

    [Fact] // ADR-0016 note 29: with the keyboard in a grid that is not the trades', before all names no grid; a currency the market has not is the kernel's refusal
    public void A_grid_of_another_table_is_no_trade_grid_and_a_currency_unknown_is_the_kernels_refusal()
    {
        var rig = new Rig();

        var csas = rig.Preview("before all", CsaGrid);
        var unknown = rig.Preview("currency xyz");

        Assert.Equal("NoGrid", csas.Refused!.Code);
        Assert.Equal(("set workspace currency XYZ", "InvalidValue", "XYZ"), (unknown.Expands, unknown.Refused!.Code, unknown.Refused.Word));
        Assert.Equal((9, 12), (unknown.Refused.Start, unknown.Refused.End));
    }

    [Theory] // ADR-0016 note 29: a short form completes its own words
    [InlineData("before ", "all (tick every row) | none (clear every row)")]
    [InlineData("before n", "none (clear every row)")]
    [InlineData("after all ", "new | existing")]
    [InlineData("after all e", "existing")]
    [InlineData("currency ", "GBP | JPY | USD | EUR")]
    [InlineData("currency u", "USD")]
    [InlineData("currency", "")]
    public void A_short_form_of_the_pricers_completes_its_own_words(string line, string offered) => Assert.Equal(offered, Offered(new Rig().Preview(line)));

    static string Given(LinePreview preview) => string.Join("; ", preview.Arguments!.Select(a => $"{a.Name}: {a.Text}"));

    static string Request(LinePreview preview) => preview.Inputs!["request"]!.ToJsonString();

    [Fact] // ADR-0016 note 28: vary reads its line into the request the AI writes, and its reading is a trial: the cases it would make, with nothing written
    public async Task Vary_is_read_as_a_trial_and_enter_makes_what_was_read()
    {
        var rig = new Rig();
        var before = rig.State.Revision;

        var preview = rig.Preview("vary CSA-A threshold 10mm, 5mm, 0");

        Assert.Equal(("xva.vary", "cell: CSA-A threshold; values: 10mm, 5mm, 0"), (preview.Command!.Id, Given(preview)));
        Assert.Equal(new JsonObject { ["workspaceId"] = rig.Id, ["revision"] = before, ["cell"] = "CSA-A threshold", ["values"] = "10mm, 5mm, 0" }.ToJsonString(), Request(preview));
        Assert.Equal("Makes 2 case(s): CSA-A threshold 10mm, CSA-A threshold 5mm; skips 0 is the workspace's value.", preview.Would);
        Assert.Equal((before, 1), (rig.State.Revision, rig.View.Cases.Count));

        var said = await rig.Run(preview);

        Assert.Equal("Made 2 case(s): CSA-A threshold 10mm, CSA-A threshold 5mm; skipped 0 is the workspace's value.", said);
        Assert.Equal(["Base", "CSA-A threshold 10mm", "CSA-A threshold 5mm"], rig.View.Cases.Select(c => c.Title));
        // The command is in the log by its words, which undo names; a cell of several words is read too.
        Assert.StartsWith("Takes back: vary CSA-A threshold 10mm, 5mm, 0 (you,", rig.Preview("undo").Would);
        Assert.Equal("Makes 1 case(s): DRAFT-1 rate 1%.", rig.Preview("vary draft-1 fixed rate 1%").Would);
        Assert.Equal("Makes no case; skips 10mm is case CSA-A threshold 10mm, made already.", rig.Preview("vary CSA-A threshold 10mm").Would);
    }

    [Theory] // ADR-0016 note 28: a line vary cannot read is refused in the preview as the command would refuse it, by its code, and nothing is made
    [InlineData("vary", "NothingToVary: vary takes a cell, its row and its column, then the values separated by commas, such as vary CSA-A threshold 10mm, 5mm, 0.")]
    [InlineData("vary CSA-A threshold", "NothingToVary: vary takes a cell, its row and its column, then the values separated by commas, such as vary CSA-A threshold 10mm, 5mm, 0.")]
    [InlineData("vary CSA-Z threshold 5mm", "UnknownCell: No row CSA-Z: a row is a CSA (CSA-A, CSA-B), the netting set (NS-001), credit, cds, a trade's id, or xva for XVA's settings.")]
    [InlineData("vary CSA-A threshold 5mm, lots", "InvalidValue: CSA-A threshold is an amount: 5mm, 250k, 1bn, or a bare number of millions; lots is not.")]
    [InlineData("vary CSA-A threshold 10,000k", "NothingToVary: 10,000k could be one value or two: a comma separates values here, so write an amount without separators, such as 10000k or 10mm.")]
    [InlineData("vary CSA-A threshold 5mm,, 1mm", "NothingToVary: vary takes one value or more for CSA-A threshold, separated by commas, none of them empty.")]
    public void A_line_vary_cannot_read_is_refused_as_the_command_would_refuse_it(string line, string refused)
    {
        var rig = new Rig();

        var preview = rig.Preview(line);

        Assert.Equal(refused, Read(preview));
        Assert.Equal(("xva.vary", 1), (preview.Command!.Id, rig.View.Cases.Count));
    }

    // The word a refusal names, where it stands in the line, and what it could have been.
    static string Marked(LinePreview preview) => preview.Refused is not { } refused ? "read"
        : $"{refused.Code}: {refused.Word}@{refused.Start}-{refused.End} | {string.Join(", ", refused.Candidates)}";

    [Theory] // ADR-0016 note 45: where one of the pricer's verbs refuses a word of its line, the refusal names it, where it stands, and what it could have been
    [InlineData("vary CSA-Z threshold 5mm", "UnknownCell: CSA-Z@5-10 | CSA-A, CSA-B, NS-001, credit, cds, xva")]
    [InlineData("vary CSA-A colour 5mm", "UnknownCell: colour@11-17 | threshold, mta, counterparty currency, self threshold, self mta, self currency, currency, curve, fva curve, frequency, exchange, one-way")]
    // A column of several words is named whole.
    [InlineData("vary DRAFT-1 fixed colour 1%", "UnknownCell: fixed colour@13-25 | notional, rate, schedule, csa, curve, im-r, im-p, B, A, margin, pvadj")]
    [InlineData("measures cva dva2", "UnknownMeasure: dva2@13-17 | cva, fva, kva, ccr.spotCapital")]
    [InlineData("cds zzz", "UnknownCds: zzz@4-7 | ABCH USD SNRFOR XR14, ABCH USD SUBLT2 XR14, ABCH JPY SNRFOR CR14, ABCS USD SNRFOR XR14, DEFG EUR SNRFOR MM14, HIJK USD SNRFOR XR14, LMNO JPY SNRFOR CR14")]
    // What a proxy's word could have been is of a kind the line has not given: the sector is given here.
    [InlineData("cds proxy financials zz", "InvalidCds: zz@21-23 | asia, europe, americas, AAA, AA, A, BBB, BB, B")]
    // A workspace with no scenario has none to offer for the name.
    [InlineData("scenario remove nope", "UnknownScenario: nope@16-20 | ")]
    // A refusal of the whole line names no word.
    [InlineData("vary CSA-A threshold", "NothingToVary: @- | ")]
    public void A_word_the_pricer_refuses_is_named_where_it_stands_with_what_it_could_have_been(string line, string marked) =>
        Assert.Equal(marked, Marked(new Rig().Preview(line)));

    [Fact] // ADR-0016 note 45: a case and a scenario that are not there are named, with the cases and the scenarios there are
    public async Task A_case_and_a_scenario_that_are_not_there_are_named_with_those_there_are()
    {
        var rig = new Rig();
        await rig.Run(rig.Preview("vary CSA-A threshold 10mm, 5mm"));
        await rig.Run(rig.Preview("scenario up jpy_ois +10bp"));

        Assert.Equal("UnknownCase: CSA-A threshold 7mm@12-31 | CSA-A threshold 10mm, CSA-A threshold 5mm", Marked(rig.Preview("case remove CSA-A threshold 7mm")));
        Assert.Equal("UnknownScenario: nope@16-20 | up", Marked(rig.Preview("scenario remove nope")));
        // No case named is a refusal of the whole line.
        Assert.Equal("UnknownCase: @- | ", Marked(rig.Preview("case remove")));
    }

    // What a line offers, each with what it is and, where it is not written at the line's end, where it is written from.
    static string OfferedAt(string line, LinePreview preview) => string.Join(" | ", preview.Complete.Select(c =>
        $"{c.Value}{(c.Detail is null ? "" : $" ({c.Detail})")}{(c.Start == line.Length ? "" : $"@{c.Start}")}"));

    [Theory] // ADR-0016 note 45: vary offers the rows a cell is named on, then the row's columns, then what a flag or a choice has to choose from, again after each comma
    [InlineData("vary", "")]
    [InlineData("vary c", "CSA-A (a CSA)@5 | CSA-B (a CSA)@5 | credit (the counterparty's CDS choice)@5 | cds (the chosen curve's spreads)@5")]
    [InlineData("vary dr", "DRAFT-1 (a trade)@5 | DRAFT-2 (a trade)@5 | DRAFT-3 (a trade)@5")]
    // A row's columns, by the word that names each.
    [InlineData("vary CSA-A ", "threshold | mta | counterparty currency | self threshold | self mta | self currency | currency | curve | fva curve | frequency | exchange | one-way")]
    [InlineData("vary NS-001 ", "recovery")]
    // A column of several words is written from its first word, and one typed by another of its names is offered by the word that names it.
    [InlineData("vary CSA-A self ", "self threshold@11 | self mta@11 | self currency@11")]
    [InlineData("vary CSA-A self c", "self currency@11")]
    [InlineData("vary DRAFT-1 fixed", "rate@13")]
    // A flag's two words and a choice's values, as a line writes them.
    [InlineData("vary CSA-A exchange ", "on | off")]
    [InlineData("vary DRAFT-1 A o", "on@15 | off@15")]
    [InlineData("vary CSA-A frequency w", "weekly@21")]
    // After a comma, the values not listed yet.
    [InlineData("vary CSA-A currency USD, ", "GBP | JPY | EUR")]
    [InlineData("vary CSA-A currency usd,j", "JPY@24")]
    // A column that may be empty offers empty; an amount, a rate and an id are typed freely.
    [InlineData("vary NS-001 recovery ", "empty")]
    [InlineData("vary CSA-A threshold ", "")]
    [InlineData("vary CSA-A curve ", "")]
    // A row or a column the workspace has not offers nothing: the reading says which.
    [InlineData("vary CSA-Z ", "")]
    [InlineData("vary CSA-A colour ", "")]
    public void Vary_offers_rows_then_columns_then_what_a_column_has_to_choose_from(string line, string offered) =>
        Assert.Equal(offered, OfferedAt(line, new Rig().Preview(line)));

    [Fact] // ADR-0016 note 45: vary offers the rows that are no trade first, then trades, a few at a time with how many more there are, as set does (note 15)
    public void Vary_offers_rows_a_few_at_a_time_and_says_how_many_more()
    {
        var rig = new Rig();
        // Every trade, existing and new, is a row, beside the six rows that are none.
        var trades = rig.View.Trades;

        var all = rig.Preview("vary ");
        var existing = rig.Preview("vary abc-le-01-e000");

        Assert.Equal("CSA-A (a CSA) | CSA-B (a CSA) | NS-001 (the netting set) | credit (the counterparty's CDS choice) | cds (the chosen curve's spreads) | xva (XVA's settings)",
            OfferedAt("vary ", all with { Complete = [.. all.Complete.Take(6)] }));
        Assert.All(all.Complete.Skip(6), c => Assert.Equal("a trade", c.Detail));
        Assert.Equal((20, 6 + trades - 20), (all.Complete.Count, all.More));
        Assert.All(existing.Complete, c => Assert.StartsWith("ABC-LE-01-E000", c.Value));
        Assert.Equal((20, trades - 3 - 20, 5), (existing.Complete.Count, existing.More, existing.Complete[0].Start));
    }

    [Fact] // ADR-0016 note 28: the AI's request with the same words gets the same answer and the same refusal; a cell is also named as a page names it, by entity and field
    public async Task The_same_words_from_the_ai_or_a_dialog_get_the_same_answer_and_refusal()
    {
        var rig = new Rig();

        var refused = await rig.Invoke("xva.vary", new JsonObject { ["cell"] = "CSA-A threshold", ["values"] = "5mm, lots" }, RunOrigin.Ai);
        var made = await rig.Invoke("xva.vary", new JsonObject { ["cell"] = "CSA-A threshold", ["values"] = "5mm" }, RunOrigin.Ai);
        var dialog = await rig.Invoke("xva.vary", new JsonObject { ["entity"] = "csa:CSA-A", ["field"] = "counterpartyThreshold", ["values"] = "5mm, 7mm" });
        var both = await rig.Invoke("xva.vary", new JsonObject { ["cell"] = "CSA-A threshold", ["entity"] = "csa:CSA-A", ["field"] = "counterpartyThreshold", ["values"] = "9mm" });
        var neither = await rig.Invoke("xva.vary", new JsonObject { ["values"] = "9mm" });

        Assert.Equal("InvalidValue: CSA-A threshold is an amount: 5mm, 250k, 1bn, or a bare number of millions; lots is not.", refused);
        Assert.Equal("Made 1 case(s): CSA-A threshold 5mm.", made);
        Assert.Equal("Made 1 case(s): CSA-A threshold 7mm; skipped 5mm is case CSA-A threshold 5mm, made already.", dialog);
        Assert.Equal("InvalidInput: vary names its cell once: as cell, or as entity and field.", both);
        Assert.Equal("InvalidInput: vary names its cell once: as cell, or as entity and field.", neither);
        Assert.StartsWith("Takes back: vary CSA-A threshold 5mm, 7mm (you,", rig.Preview("undo").Would);
    }

    [Fact] // ADR-0016 note 28, §3: add each is read as a trial, says the cases it would make and those it would replace, and takes no word after it
    public async Task Add_each_is_read_as_a_trial_and_enter_makes_what_was_read()
    {
        var rig = new Rig();
        var before = rig.State.Revision;

        var preview = rig.Preview("add each");

        Assert.Equal(("xva.trades.add", new JsonObject { ["workspaceId"] = rig.Id, ["revision"] = before }.ToJsonString()), (preview.Command!.Id, Request(preview)));
        Assert.Equal("Makes 3 case(s): add DRAFT-1, add DRAFT-2, add DRAFT-3.", preview.Would);
        Assert.Equal((before, 1), (rig.State.Revision, rig.View.Cases.Count));

        Assert.Equal("Made 3 case(s): add DRAFT-1, add DRAFT-2, add DRAFT-3.", await rig.Run(preview));
        Assert.StartsWith("Takes back: add each (you,", rig.Preview("undo").Would);
        Assert.Equal("Makes 3 case(s): add DRAFT-1, add DRAFT-2, add DRAFT-3; replaces add DRAFT-1, add DRAFT-2, add DRAFT-3.", rig.Preview("add each").Would);
        Assert.Equal("UnreadWords: add each takes no words after it.", Read(rig.Preview("add each DRAFT-1")));
        // The name it had is retired, and is no second spelling (note 28).
        Assert.Equal("UnknownCommand", rig.Preview("add trades").Refused!.Code);
    }

    [Fact] // ADR-0016 §3, note 37: the request holds the iterations as a list, checked against its contract at each item's path, and the command makes them
    public async Task Add_each_takes_its_iterations_as_a_list_its_contract_checks()
    {
        var rig = new Rig();
        var taken = rig.Service.Page(rig.Id, "akira", 0, 1, [], "existing").Rows[0].TradeId;
        var start = rig.State.Revision;
        static JsonObject Given(params JsonNode?[] iterations) => new() { ["iterations"] = new JsonArray(iterations) };

        var text = await rig.Invoke("xva.trades.add", new JsonObject { ["iterations"] = "DRAFT-1" });
        var none = await rig.Invoke("xva.trades.add", Given(new JsonObject { ["add"] = new JsonArray(), ["without"] = new JsonArray(taken) }));
        var shape = await rig.Invoke("xva.trades.add", Given(new JsonObject { ["add"] = new JsonArray("DRAFT-1", 2) }, "DRAFT-2", new JsonObject { ["without"] = new JsonArray(taken), ["also"] = true }));
        var refused = rig.State.Revision;
        var made = await rig.Invoke("xva.trades.add", Given(new JsonObject { ["add"] = new JsonArray("DRAFT-1", "DRAFT-2"), ["without"] = new JsonArray(taken) }, new JsonObject { ["add"] = new JsonArray("draft-1") }));
        var undoes = rig.Preview("undo").Would;
        // With keep, the cases made stay and the answer names the one made beside them (note 37, corrected in review).
        var beside = Given(new JsonObject { ["add"] = new JsonArray("DRAFT-2", "DRAFT-3") });
        beside["keep"] = true;
        var kept = await rig.Invoke("xva.trades.add", beside);
        var flag = await rig.Invoke("xva.trades.add", new JsonObject { ["keep"] = "yes" });
        // An empty list is the command as typed: a case per New row with A ticked.
        var typed = await rig.Invoke("xva.trades.add", Given());

        Assert.Equal("InvalidField: A list is required.", text);
        Assert.Equal("InvalidField: At least 1 item is required.", none);
        Assert.Equal("InvalidField: A text value is required. InvalidField: A record is required. Required: add is required. UnknownField: An item of iterations has no field also.", shape);
        // Each issue is at its item's path in the request, where a caller that built the list finds it.
        Assert.Equal(["/inputs/request/iterations/0/add/1", "/inputs/request/iterations/1", "/inputs/request/iterations/2/add", "/inputs/request/iterations/2/also"],
            await rig.Paths("xva.trades.add", Given(new JsonObject { ["add"] = new JsonArray("DRAFT-1", 2) }, "DRAFT-2", new JsonObject { ["without"] = new JsonArray(taken), ["also"] = true })));
        Assert.Equal(start, refused);
        Assert.Equal($"Made 3 case(s): add DRAFT-1 + DRAFT-2 without {taken}, add DRAFT-1, add DRAFT-3.", made);
        // undo names the command by the iterations it was given, as the log does.
        Assert.StartsWith($"Takes back: add each: DRAFT-1 + DRAFT-2 without {taken}; DRAFT-1 (you,", undoes);
        Assert.Equal("Made 1 case(s): add DRAFT-2 + DRAFT-3.", kept);
        Assert.Equal("InvalidField: true or false is required.", flag);
        Assert.Equal($"Made 3 case(s): add DRAFT-1, add DRAFT-2, add DRAFT-3; replaced add DRAFT-1 + DRAFT-2 without {taken}, add DRAFT-1, add DRAFT-3, add DRAFT-2 + DRAFT-3.", typed);
        Assert.StartsWith("Takes back: add each (you,", rig.Preview("undo").Would);
    }

    [Fact] // ADR-0016 note 37: a case stored before an iteration was a list names one trade, and is read as the iteration of that trade alone
    public void A_case_stored_with_one_trade_is_read_as_that_trade_alone()
    {
        var rig = new Rig();
        rig.Workspaces.Batch(rig.Id, rig.State.Revision, "akira",
            [new WorkspaceChange.Create("cp:ABC/LE-01", PortfolioCases.Of("c900"), new JsonObject { ["title"] = "add DRAFT-2", ["kind"] = PortfolioCases.Add, ["trade"] = "DRAFT-2" })]);

        var stored = rig.View.Cases[1];

        Assert.Equal(("add DRAFT-2", "add", "DRAFT-2", 0), (stored.Title, stored.Kind, string.Join(", ", stored.Iteration!.Add), stored.Iteration.Without.Count));
    }

    [Fact] // ADR-0016 note 28, docs/03 §5: what add each cannot do is refused in the preview by the command's own code and sentence
    public async Task Add_each_is_refused_in_the_preview_as_the_command_would_refuse_it()
    {
        var rig = new Rig();
        await rig.Run(rig.Preview("set DRAFT-2 before on"));
        var inBefore = rig.Preview("add each");
        await rig.Run(rig.Preview("after none new"));
        var none = rig.Preview("add each");

        Assert.Equal("CandidateInBefore: DRAFT-2 is in Before, and a new trade is priced alone only out of Before. Clear B on it, or A to leave it out, then add each again.", Read(inBefore));
        Assert.Equal("NothingToAdd: add each makes a case per New row with A ticked, and no New row has A ticked.", Read(none));
    }

    [Fact] // ADR-0016 note 28, ADR-0010 §8: cds reads its words against the catalogue as a trial, says the curve it would choose, and Enter chooses it
    public async Task Cds_is_read_as_a_trial_and_enter_chooses_what_was_read()
    {
        var rig = new Rig();
        var before = rig.State.Revision;

        var proxy = rig.Preview("cds proxy industrials asia BBB");
        var same = rig.Preview("cds ABCH USD SNRFOR");
        var none = rig.Preview("cds none");

        Assert.Equal(("xva.cds", "cds: proxy industrials asia BBB"), (proxy.Command!.Id, Given(proxy)));
        Assert.Equal(new JsonObject { ["workspaceId"] = rig.Id, ["revision"] = before, ["cds"] = "proxy industrials asia BBB" }.ToJsonString(), Request(proxy));
        Assert.StartsWith("Chooses the proxy ", proxy.Would);
        Assert.StartsWith("Nothing would change: the CDS is the single name ABCH | USD | SNRFOR | XR14", same.Would);
        Assert.StartsWith("Leaves no CDS: ", none.Would);
        Assert.Equal(before, rig.State.Revision);

        var said = await rig.Run(proxy);

        Assert.StartsWith("The CDS of ABC/LE-01 is the proxy ", said);
        Assert.Equal(proxy.Would!["Chooses ".Length..], said["The CDS of ABC/LE-01 is ".Length..]);
        Assert.StartsWith("Takes back: cds proxy industrials asia BBB (you,", rig.Preview("undo").Would);
    }

    [Theory] // ADR-0016 note 28: cds offers the catalogue's words, and refuses what names no curve or several as the command does
    [InlineData("cds ", "none (no CDS: CVA is refused) | proxy (a sector, a region and a rating) | ABCH")]
    [InlineData("cds proxy ", "financials (the proxy's sector)")]
    [InlineData("cds proxy industrials asia ", "BBB (the proxy's rating)")]
    [InlineData("cds ABCH ", "USD (currency)")]
    public void Cds_offers_the_catalogues_words(string line, string offered) => Assert.Contains(offered, Offered(new Rig().Preview(line)));

    [Fact] // ADR-0016 note 28: while the command's own word is still being typed, nothing follows it yet
    public void Cds_offers_nothing_while_its_own_word_is_typed() => Assert.Equal("", Offered(new Rig().Preview("cds")));

    [Fact] // ADR-0016 note 28: words that name no curve are refused in the preview by the command's code
    public void Cds_words_that_name_no_curve_are_refused_in_the_preview()
    {
        var rig = new Rig();

        Assert.Equal("InvalidCds", rig.Preview("cds").Refused!.Code);
        Assert.Equal(Assert.Throws<WorkspaceRefused>(() => rig.Service.ChooseCds(rig.Id, rig.State.Revision, "akira", "ZZZZ")).Code, rig.Preview("cds ZZZZ").Refused!.Code);
    }

    [Fact] // ADR-0016 note 28, ADR-0010 §10: measures reads the measures by their ids whatever their case, as a trial, offers those not typed yet, and Enter selects them
    public async Task Measures_is_read_as_a_trial_offers_what_is_not_typed_and_enter_selects()
    {
        var rig = new Rig();
        var before = rig.State.Revision;
        var all = rig.View.Measures.Select(m => m.Id).ToList();

        var preview = rig.Preview("measures FVA cva");

        Assert.Equal(("xva.measures", "measures: FVA cva"), (preview.Command!.Id, Given(preview)));
        Assert.Equal(new JsonObject { ["workspaceId"] = rig.Id, ["revision"] = before, ["measures"] = "FVA cva" }.ToJsonString(), Request(preview));
        Assert.Equal("Price would price CVA and FVA.", preview.Would);
        Assert.Equal(all.Where(id => id is not ("cva" or "fva")), rig.Preview("measures FVA cva ").Complete.Select(c => c.Value));
        Assert.Equal(["cva"], rig.Preview("measures fva c").Complete.Where(c => c.Value == "cva").Select(c => c.Value));
        Assert.Equal(before, rig.State.Revision);

        Assert.Equal("Price prices CVA and FVA.", await rig.Run(preview));
        Assert.Equal(["cva", "fva"], rig.View.SelectedMeasures);
        Assert.StartsWith("Takes back: measures cva fva (you,", rig.Preview("undo").Would);
        Assert.Equal("Nothing would change: Price prices CVA and FVA.", rig.Preview("measures cva fva").Would);
    }

    [Theory] // ADR-0016 note 28: no measure, or one the register has not, is refused in the preview by the command's code
    [InlineData("measures", "NoMeasure")]
    [InlineData("measures cva dva2", "UnknownMeasure")]
    public void Measures_that_cannot_be_read_are_refused_in_the_preview(string line, string code) => Assert.Equal(code, new Rig().Preview(line).Refused!.Code);

    [Fact] // ADR-0016 note 28: the page's measure buttons invoke the command with the selection they leave, and one that leaves none is refused as the line is
    public async Task The_measure_buttons_invoke_measures_and_a_selection_of_none_is_refused()
    {
        var rig = new Rig();

        Assert.Equal("Price prices KVA.", await rig.Invoke("xva.measures", new JsonObject { ["measures"] = "kva" }));
        Assert.Equal("NoMeasure: Select at least one measure to price.", await rig.Invoke("xva.measures", new JsonObject { ["measures"] = "" }));
        Assert.Equal(["kva"], rig.View.SelectedMeasures);
    }

    [Fact] // ADR-0016 note 28, ADR-0010 §3: case remove names a case by its title of several words, offers the titles, and Enter removes it; cases clear removes them all
    public async Task Case_remove_and_cases_clear_are_read_as_trials_and_run_what_was_read()
    {
        var rig = new Rig();
        await rig.Run(rig.Preview("vary CSA-A threshold 10mm, 5mm, 20mm"));
        var before = rig.State.Revision;

        var remove = rig.Preview("case remove csa-a threshold 10MM");
        var clear = rig.Preview("cases clear");

        Assert.Equal(("xva.case.remove", "case: csa-a threshold 10MM", "Removes the case CSA-A threshold 10mm, with its 1 change."), (remove.Command!.Id, Given(remove), remove.Would));
        Assert.Equal(new JsonObject { ["workspaceId"] = rig.Id, ["revision"] = before, ["case"] = "csa-a threshold 10MM" }.ToJsonString(), Request(remove));
        Assert.Equal(("xva.cases.clear", "Removes 3 cases: CSA-A threshold 10mm, CSA-A threshold 5mm and CSA-A threshold 20mm."), (clear.Command!.Id, clear.Would));
        Assert.Equal(["CSA-A threshold 10mm", "CSA-A threshold 5mm", "CSA-A threshold 20mm"], rig.Preview("case remove ").Complete.Select(c => c.Value));
        Assert.Equal(["CSA-A threshold 5mm"], rig.Preview("case remove csa-a threshold 5").Complete.Select(c => c.Value));
        // No case named is said as that, with the cases there are, never as a case of an empty name.
        Assert.Equal("UnknownCase: case remove takes the case to remove, by its title; the cases are CSA-A threshold 10mm, CSA-A threshold 5mm, CSA-A threshold 20mm.",
            Read(rig.Preview("case remove")));
        Assert.Equal((before, 4), (rig.State.Revision, rig.View.Cases.Count));

        Assert.Equal("Removed the case CSA-A threshold 10mm.", await rig.Run(remove));
        Assert.StartsWith("Takes back: case remove CSA-A threshold 10mm (you,", rig.Preview("undo").Would);
        Assert.Equal("Removed 2 cases: CSA-A threshold 5mm and CSA-A threshold 20mm.", await rig.Run(rig.Preview("cases clear")));
        Assert.Equal(["Base"], rig.View.Cases.Select(c => c.Title));
        Assert.StartsWith("Takes back: cases clear (you,", rig.Preview("undo").Would);
        Assert.Equal("NothingToChange: The workspace has no case to remove.", Read(rig.Preview("cases clear")));
        Assert.Equal("UnknownCase: The workspace has no case; the Base is the workspace itself and is never removed.", Read(rig.Preview("case remove CSA-A threshold 5mm")));
        Assert.Equal("UnknownCase", rig.Preview("case remove").Refused!.Code);
    }

    [Fact] // ADR-0016 note 32, ADR-0010 §4: scenario reads a name and its shifts, says what it would make as a trial, and Enter makes what was read; one of its name is replaced
    public async Task Scenario_is_read_as_a_trial_and_enter_makes_what_was_read()
    {
        var rig = new Rig();
        var before = rig.State.Revision;

        var preview = rig.Preview("scenario s1 jpy_ois +10bp;  usdjpy -5%");

        Assert.Equal(("xva.scenario", "name: s1; shifts: jpy_ois +10bp;  usdjpy -5%"), (preview.Command!.Id, Given(preview)));
        Assert.Equal(new JsonObject { ["workspaceId"] = rig.Id, ["revision"] = before, ["name"] = "s1", ["shifts"] = "jpy_ois +10bp;  usdjpy -5%" }.ToJsonString(), Request(preview));
        Assert.Equal("Makes the scenario s1 with 2 shifts: jpy_ois +10bp; usdjpy -5%.", preview.Would);
        Assert.Equal((before, 0), (rig.State.Revision, rig.View.Scenarios.Count));

        Assert.Equal("Made the scenario s1.", await rig.Run(preview));
        Assert.Equal([("s1", 2, true)], rig.View.Scenarios.Select(s => (s.Name, s.Shifts, s.Included)));
        Assert.StartsWith("Takes back: scenario s1 jpy_ois +10bp; usdjpy -5% (you,", rig.Preview("undo").Would);
        // One of its name is replaced whole, its name with it: the name is as typed now.
        Assert.Equal("Replaces the shifts of S1 with 1 shift: jpy_ois 5y +3bp.", rig.Preview("scenario S1 jpy_ois 5y +3bp").Would);
    }

    [Theory] // ADR-0016 note 32: a scenario line that cannot be read is refused in the preview, as the command would refuse it
    [InlineData("scenario", "InvalidScenario: scenario takes a name, then its shifts separated by semicolons, such as scenario s1 jpy_ois +10bp; usdjpy -5%.")]
    [InlineData("scenario s1", "InvalidScenario: scenario takes a name, then its shifts separated by semicolons, such as scenario s1 jpy_ois +10bp; usdjpy -5%.")]
    [InlineData("scenario up jpy_ois +10%", "InvalidShift")]
    [InlineData("scenario save2 nothing 1", "InvalidShift")]
    public void A_scenario_line_that_cannot_be_read_is_refused_in_the_preview(string line, string refused) => Assert.StartsWith(refused, Read(new Rig().Preview(line)));

    [Fact] // ADR-0016 note 32, ADR-0010 §4: include, exclude and remove name a scenario by its name, of several words where a generator made it, as trials; clear removes them all
    public async Task Scenario_include_exclude_remove_and_clear_are_read_as_trials_and_run_what_was_read()
    {
        var rig = new Rig();
        await rig.Run(rig.Preview("scenario up jpy_ois +10bp; usdjpy -5%"));
        await rig.Run(rig.Preview("scenario cross jpy_ois -50bp, 0 x usdjpy -10%, 0"));
        var before = rig.State.Revision;

        var exclude = rig.Preview("scenario exclude JPY_OIS -50bp · usdjpy -10%");

        Assert.Equal(("xva.scenario.exclude", "name: JPY_OIS -50bp · usdjpy -10%", "Price would leave the scenario jpy_ois -50bp · usdjpy -10% out; its shifts stay."),
            (exclude.Command!.Id, Given(exclude), exclude.Would));
        Assert.Equal(new JsonObject { ["workspaceId"] = rig.Id, ["revision"] = before, ["name"] = "JPY_OIS -50bp · usdjpy -10%" }.ToJsonString(), Request(exclude));
        Assert.Equal("up (2 shift(s)) | jpy_ois -50bp · usdjpy -10% (2 shift(s)) | jpy_ois -50bp (1 shift(s)) | usdjpy -10% (1 shift(s))", Offered(rig.Preview("scenario exclude ")));
        Assert.Equal("jpy_ois -50bp · usdjpy -10% (2 shift(s)) | jpy_ois -50bp (1 shift(s))", Offered(rig.Preview("scenario remove jpy_ois -5")));
        Assert.Equal("", Offered(rig.Preview("scenario include ")));
        Assert.Equal(before, rig.State.Revision);

        Assert.Equal("Price leaves the scenario jpy_ois -50bp · usdjpy -10% out; its shifts stay.", await rig.Run(exclude));
        Assert.StartsWith("Takes back: scenario exclude jpy_ois -50bp · usdjpy -10% (you,", rig.Preview("undo").Would);
        Assert.Equal("Nothing would change: Price already leaves the scenario jpy_ois -50bp · usdjpy -10% out.", rig.Preview("scenario exclude jpy_ois -50bp · usdjpy -10%").Would);
        Assert.Equal("jpy_ois -50bp · usdjpy -10% (2 shift(s), excluded)", Offered(rig.Preview("scenario include ")));
        Assert.Equal("Price would price the scenario jpy_ois -50bp · usdjpy -10%.", rig.Preview("scenario include jpy_ois -50bp · usdjpy -10%").Would);
        Assert.Equal("Nothing would change: Price already prices the scenario up.", rig.Preview("scenario include UP").Would);

        var remove = rig.Preview("scenario remove up");
        Assert.Equal(("xva.scenario.remove", "Removes the scenario up, with its 2 shifts."), (remove.Command!.Id, remove.Would));
        Assert.Equal("Removed the scenario up.", await rig.Run(remove));
        Assert.StartsWith("Takes back: scenario remove up (you,", rig.Preview("undo").Would);
        Assert.StartsWith("UnknownScenario: There is no scenario up: the scenarios are ", Read(rig.Preview("scenario exclude up")));
        Assert.StartsWith("UnknownScenario: scenario remove takes the scenario's name: the scenarios are ", Read(rig.Preview("scenario remove")));

        var clear = rig.Preview("scenarios clear");
        Assert.Equal(("xva.scenarios.clear", "Removes 3 scenarios: jpy_ois -50bp · usdjpy -10%, jpy_ois -50bp and usdjpy -10%."), (clear.Command!.Id, clear.Would));
        Assert.Equal("Removed every scenario; Price prices the base market alone.", await rig.Run(clear));
        Assert.StartsWith("Takes back: scenarios clear (you,", rig.Preview("undo").Would);
        Assert.Equal("NothingToChange: The workspace has no scenario to remove.", Read(rig.Preview("scenarios clear")));
    }

    [Fact] // ADR-0016 note 32, ADR-0010 §4: duplicate takes the scenario to copy, of several words, then the copy's name in one, as a trial
    public async Task Scenario_duplicate_is_read_as_a_trial_and_enter_copies_what_was_read()
    {
        var rig = new Rig();
        await rig.Run(rig.Preview("scenario ladder jpy_ois 5y -10bp..+10bp step 10bp"));
        var before = rig.State.Revision;

        var preview = rig.Preview("scenario duplicate jpy_ois 5y -10bp down");

        Assert.Equal(("xva.scenario.duplicate", "name: jpy_ois 5y -10bp; copy: down", "Copies the 1 shift of jpy_ois 5y -10bp as the scenario down."), (preview.Command!.Id, Given(preview), preview.Would));
        Assert.Equal(new JsonObject { ["workspaceId"] = rig.Id, ["revision"] = before, ["name"] = "jpy_ois 5y -10bp", ["copy"] = "down" }.ToJsonString(), Request(preview));
        Assert.Equal("jpy_ois 5y -10bp (1 shift(s)) | jpy_ois 5y +10bp (1 shift(s))", Offered(rig.Preview("scenario duplicate ")));
        Assert.Equal(before, rig.State.Revision);

        Assert.Equal("Duplicated jpy_ois 5y -10bp as down.", await rig.Run(preview));
        Assert.StartsWith("Takes back: scenario duplicate jpy_ois 5y -10bp down (you,", rig.Preview("undo").Would);
        Assert.StartsWith("InvalidScenario: A scenario down exists", Read(rig.Preview("scenario duplicate jpy_ois 5y -10bp down")));
        Assert.Equal("InvalidScenario: scenario duplicate takes the scenario to copy, then the copy's name in one word.", Read(rig.Preview("scenario duplicate down")));
    }

    [Fact] // ADR-0016 note 32, ADR-0010 §4: a generator says how many scenarios it would make and the pairs Price would then price, and the cap is refused in the preview
    public async Task A_generator_is_read_as_a_trial_says_the_pairs_it_leaves_and_the_cap_is_refused_in_the_preview()
    {
        var rig = new Rig(caseCap: 6);
        var before = rig.State.Revision;

        var ladder = rig.Preview("scenario ladder jpy_ois -20bp..+20bp step 10bp");

        Assert.Equal(("xva.scenario.ladder", "factors: jpy_ois -20bp..+20bp step 10bp"), (ladder.Command!.Id, Given(ladder)));
        Assert.Equal(new JsonObject { ["workspaceId"] = rig.Id, ["revision"] = before, ["factors"] = "jpy_ois -20bp..+20bp step 10bp" }.ToJsonString(), Request(ladder));
        Assert.Equal("Makes 4 scenario(s): jpy_ois -20bp, jpy_ois -10bp, jpy_ois +10bp and 1 more. Price would then price 5 pairs of a case and a market, of 6 at most.", ladder.Would);
        Assert.Equal((before, 0), (rig.State.Revision, rig.View.Scenarios.Count));
        Assert.StartsWith("InvalidGenerator: scenario ladder makes 6 scenarios, and a set is 5 at most", Read(rig.Preview("scenario ladder jpy_ois -30bp..+30bp step 10bp")));
        Assert.StartsWith("InvalidGenerator: scenario cross needs two lists or more", Read(rig.Preview("scenario cross jpy_ois -50bp, 0")));
        Assert.StartsWith("InvalidGenerator: scenario ladder needs a factor, a range and a step", Read(rig.Preview("scenario ladder")));

        Assert.Equal("Made 4 scenario(s): jpy_ois -20bp, jpy_ois -10bp, jpy_ois +10bp, jpy_ois +20bp.", await rig.Run(ladder));
        Assert.StartsWith("Takes back: scenario ladder jpy_ois -20bp..+20bp step 10bp (you,", rig.Preview("undo").Would);
        // A second case doubles the pairs: the set that is made is said to leave more than Price prices, and is still made.
        await rig.Run(rig.Preview("vary CSA-A threshold 10mm"));
        Assert.Equal("Makes 2 scenario(s), replacing 2 of the same name: jpy_ois -10bp and jpy_ois +10bp. "
            + "Price would then refuse to price 10 pairs of a case and a market: 4 more than the 6 it prices at most.", rig.Preview("scenario ladder jpy_ois -10bp..+10bp step 10bp").Would);
    }

    [Fact] // ADR-0016 note 32, ADR-0010 §4, C32: save says what it would keep and for whom and changes no workspace; load offers the sets the user sees and says what it would add
    public async Task Scenario_save_and_load_are_read_before_they_run_and_load_offers_the_saved_sets()
    {
        var rig = new Rig();
        Assert.StartsWith("NothingToSave: ", Read(rig.Preview("scenario save rates-up")));
        await rig.Run(rig.Preview("scenario up jpy_ois +10bp"));
        await rig.Run(rig.Preview("scenario down jpy_ois -10bp"));
        var before = rig.State.Revision;

        var save = rig.Preview("scenario save rates-up DESK");

        Assert.Equal(("xva.scenario.save", "set: rates-up; share: desk", "Saves 2 scenario(s) as the set rates-up, for the desk rates."), (save.Command!.Id, Given(save), save.Would));
        Assert.Equal(new JsonObject { ["workspaceId"] = rig.Id, ["revision"] = before, ["set"] = "rates-up", ["share"] = "desk" }.ToJsonString(), Request(save));
        Assert.Equal("desk (your desk sees it) | all (every user sees it)", Offered(rig.Preview("scenario save rates-up ")));
        Assert.Equal("", Offered(rig.Preview("scenario load ")));
        Assert.StartsWith("InvalidShare: ", Read(rig.Preview("scenario save rates-up everyone")));
        Assert.Equal("UnreadWords: scenario save takes a set's name, then desk or all: more is not read.", Read(rig.Preview("scenario save rates-up desk now")));
        Assert.StartsWith("InvalidName: ", Read(rig.Preview("scenario save")));

        Assert.Equal("Saved 2 scenario(s) as the set rates-up, for the desk rates.", await rig.Run(save));
        Assert.Equal(before, rig.State.Revision);
        Assert.Equal("Saves 2 scenario(s) as the set rates-up, for you alone, replacing your rates-up.", rig.Preview("scenario save rates-up").Would);

        await rig.Run(rig.Preview("scenario remove down"));
        var load = rig.Preview("scenario load rates-up");
        Assert.Equal(("xva.scenario.load", "set: rates-up", "Loads 2 scenario(s) from rates-up, replacing up: up and down."), (load.Command!.Id, Given(load), load.Would));
        Assert.Equal("rates-up (2 scenario(s), for the desk, saved by akira)", Offered(rig.Preview("scenario load ra")));
        Assert.StartsWith("UnknownSaved: ", Read(rig.Preview("scenario load no-such-set")));
        Assert.Equal("Loaded 2 scenario(s) from rates-up, replacing up: up, down.", await rig.Run(load));
        Assert.StartsWith("Takes back: scenario load rates-up (you,", rig.Preview("undo").Would);

        // A reading names three scenarios at most, of those a set replaces as of those it adds; the answer names each.
        await rig.Run(rig.Preview("scenario mid jpy_ois +1bp"));
        await rig.Run(rig.Preview("scenario wide jpy_ois +50bp"));
        await rig.Run(rig.Preview("scenario save four"));
        Assert.Equal("Loads 4 scenario(s) from four, replacing up, down, mid and 1 more: up, down, mid and 1 more.", rig.Preview("scenario load four").Would);
        Assert.Equal("Loaded 4 scenario(s) from four, replacing up, down, mid, wide: up, down, mid, wide.", await rig.Run(rig.Preview("scenario load four")));
    }

    [Fact] // ADR-0016 note 32: the AI and the Scenarios tab invoke the same commands with the words a line reads, and are refused as the line is
    public async Task The_scenario_commands_invoked_by_request_answer_and_refuse_as_the_line_does()
    {
        var rig = new Rig();

        Assert.Equal("Made the scenario up.", await rig.Invoke("xva.scenario", new JsonObject { ["name"] = "up", ["shifts"] = "jpy_ois +10bp" }, RunOrigin.Ai));
        Assert.Equal("Price leaves the scenario up out; its shifts stay.", await rig.Invoke("xva.scenario.exclude", new JsonObject { ["name"] = "up" }));
        Assert.Equal("Made 2 scenario(s): jpy_ois -10bp, jpy_ois +10bp.", await rig.Invoke("xva.scenario.ladder", new JsonObject { ["factors"] = "jpy_ois -10bp..+10bp step 10bp" }, RunOrigin.Ai));
        Assert.StartsWith("UnknownScenario: There is no scenario dn", await rig.Invoke("xva.scenario.remove", new JsonObject { ["name"] = "dn" }));
        Assert.Equal(RunOrigin.Ai, rig.State.Log.First(e => e.Kind == EditKind.Create).Origin);
    }

    [Fact] // ADR-0016 note 33, ADR-0010 §1, §9: price is not tried: its reading says what Price would price and starts nothing, and Enter submits one Run with the caller's origin
    public async Task Price_is_read_without_being_tried_and_enter_submits_one_run()
    {
        var rig = new Rig();
        await rig.Run(rig.Preview("measures cva fva"));
        var before = rig.State.Revision;

        var preview = rig.Preview("price");

        Assert.Equal(("xva.workspace.price", new JsonObject { ["workspaceId"] = rig.Id, ["revision"] = before }.ToJsonString()), (preview.Command!.Id, Request(preview)));
        Assert.Equal("Prices CVA and FVA for 1 case, as one Run.", preview.Would);
        Assert.Empty(rig.View.Runs);
        Assert.Equal("UnreadWords: price takes no words after it.", Read(rig.Preview("price now")));

        var said = await rig.Run(preview, RunOrigin.Ai);

        var run = Assert.Single(rig.View.Runs);
        Assert.Equal(($"Price was accepted as Run {run.RunId}.", before, RunOrigin.Ai), (said, rig.State.Revision, rig.Runs.Find(run.RunId)!.Origin));
    }

    [Fact] // ADR-0016 note 33, XV13: the command asked again with its key, by a gateway that has forgotten the first answer, is the same business submission and no second Run
    public async Task Price_asked_again_with_its_key_is_the_same_run()
    {
        var rig = new Rig();
        var inputs = rig.Preview("price").Inputs!;
        async Task<string> Asked(CommandService gateway) =>
            ((CommandResult.Done)await gateway.InvokeAsync("xva.workspace.price", PortfolioContracts.Version, (JsonObject)inputs.DeepClone(), Akira, "key-1", default)).Outputs["answer"]!["runId"]!.GetValue<string>();

        var first = await Asked(rig.Commands);
        var again = await Asked(new CommandService(rig.Registry, rig.Store));

        Assert.Equal((first, 1), (again, rig.View.Runs.Count));
    }

    [Fact] // ADR-0016 note 33, ADR-0010 §3, §4: the markets are said beside the cases, and what Price refuses is refused in the preview by its code and sentence, with no Run made
    public async Task Price_says_its_markets_and_is_refused_in_the_preview_as_price_refuses()
    {
        var rig = new Rig(caseCap: 3);
        await rig.Run(rig.Preview("measures cva fva"));
        await rig.Run(rig.Preview("scenario up jpy_ois +10bp"));
        Assert.Equal("Prices CVA and FVA for 1 case on 2 markets (the base market and 1 scenario), as one Run.", rig.Preview("price").Would);
        await rig.Run(rig.Preview("vary CSA-A threshold 10mm"));

        Assert.StartsWith("TooManyCases: Price prices 3 pairs of a case and a market at most: 2 case(s) × 2 markets", Read(rig.Preview("price")));
        Assert.StartsWith("TooManyCases: Price prices 3 pairs", await rig.Invoke("xva.workspace.price", []));

        var set = new Rig();
        await set.Run(set.Preview("add each"));
        await set.Run(set.Preview("set DRAFT-2 after off"));
        Assert.Equal("AdditionSetChanged: add each's cases add DRAFT-1, DRAFT-2, DRAFT-3, and DRAFT-2 is in a case and no longer ticked. Run add each again to make the set anew.",
            Read(set.Preview("price")));
        Assert.Empty(rig.View.Runs.Concat(set.View.Runs));
    }

    [Fact] // ADR-0016 note 33, ADR-0003 §6: refresh is not tried: its reading says what it would read again, and Enter says what carried; the log names it by its word
    public async Task Refresh_is_read_without_being_tried_and_enter_says_what_carried()
    {
        var rig = new Rig();
        await rig.Run(rig.Preview("set CSA-A threshold 5mm"));
        var before = rig.State.Revision;

        var preview = rig.Preview("refresh");

        Assert.Equal(("xva.refresh", new JsonObject { ["workspaceId"] = rig.Id, ["revision"] = before }.ToJsonString()), (preview.Command!.Id, Request(preview)));
        Assert.Equal("Reads ABC/LE-01 again as of 2026-10-26, and carries your edits onto what it reads.", preview.Would);
        Assert.Equal(before, rig.State.Revision);

        Assert.Equal("Refreshed: 1 edit(s) carried, 0 in conflict, 0 lost.", await rig.Run(preview, RunOrigin.Ai));
        Assert.True(rig.State.Revision > before);
        Assert.Equal(RunOrigin.Ai, rig.State.Log[^1].Origin);
        Assert.StartsWith("Takes back: refresh (the AI for you,", rig.Preview("undo").Would);
    }

    [Fact] // ADR-0016 note 33, ADR-0003 §6: a refresh that still holds an edit for the user says that it waits, and one that holds none does not
    public async Task Refresh_says_that_it_stays_while_an_edit_is_held()
    {
        var rig = new Rig();
        var later = Day1.AddYears(1);
        var after = new PortfolioSourceVersion("ABC/LE-01", later, 40);
        var matured = ((PortfolioSourceVersion)rig.State.Source).Existing.First(t => after.Trade(t.TradeId) is null);
        rig.Service.Edit(rig.Id, rig.State.Revision, "akira", $"trade:{matured.TradeId}", "notional", 1_000_000m);
        async Task<(string Said, bool Stays)> Refreshed()
        {
            var answer = Assert.IsType<CommandResult.Done>(await rig.Commands.InvokeAsync("xva.refresh", PortfolioContracts.Version,
                new JsonObject { ["request"] = new JsonObject { ["workspaceId"] = rig.Id } }, Akira, Guid.NewGuid().ToString(), default)).Outputs["answer"]!;
            return (answer["said"]!.GetValue<string>(), answer["stays"]!.GetValue<bool>());
        }

        Assert.Equal(("Refreshed: 1 edit(s) carried, 0 in conflict, 0 lost.", false), await Refreshed());

        // The trade matured before the later value date: its edit is lost, and held until the user says so.
        rig.Service.ChangeValueDate(rig.Id, rig.State.Revision, "akira", later);
        Assert.Equal(("Refreshed: 0 edit(s) carried, 0 in conflict, 1 lost.", true), await Refreshed());
    }

    [Fact] // ADR-0016 note 33: a refusal that names the edits it waits on says them in its sentence, since a line and a notice show a sentence alone
    public void A_refusal_says_the_edits_it_waits_on_in_its_sentence()
    {
        var refused = new WorkspaceRefused("UnresolvedEdits", "Resolve the conflicts and lost edits on the trades being priced.",
            [new EditTarget("cp:ABC/LE-01", "trade:T-1", "notional"), new EditTarget("cp:ABC/LE-01", "csa:CSA-A", "threshold")]).Named();

        Assert.Equal(("UnresolvedEdits", "Resolve the conflicts and lost edits on the trades being priced: trade:T-1 notional, csa:CSA-A threshold.", 2),
            (refused.Code, refused.Message, refused.Targets.Count));
    }

    [Fact] // ADR-0016 note 33, ADR-0010 §8, §10: a side that holds no trade is said, and a figure a case's input already refuses is counted with the first said, while Price is still read
    public async Task Price_says_the_side_priced_alone_and_the_first_figure_its_input_refuses()
    {
        var alone = new Rig(existing: 3);
        await alone.Run(alone.Preview("measures cva fva"));
        await alone.Run(alone.Preview("set all after off"));
        Assert.Equal("Prices CVA and FVA for 1 case, Before only, as one Run.", alone.Preview("price").Would);

        var rig = new Rig(existing: 10, registration: "XYZ/LE-03");
        await rig.Run(rig.Preview("measures cva fva"));
        rig.Service.Edit(rig.Id, rig.State.Revision, "akira", "credit:XYZ/LE-03", "proxyRating", null);

        Assert.Equal("Prices CVA and FVA for 1 case, as one Run. 1 figure of it would be refused, first CVA of Base: "
            + "No CDS for XYZ/LE-03: its proxy has no rating. Choose it on the Credit tab, or with cds proxy.", rig.Preview("price").Would);
    }

    [Fact] // ADR-0016 note 33, XV13: price is one business submission, so a caller that invokes its handler with no key is refused and no Run is made
    public async Task Price_invoked_with_no_key_is_refused()
    {
        var rig = new Rig();
        var request = new Dictionary<string, JsonNode?> { ["request"] = new JsonObject { ["workspaceId"] = rig.Id } };

        var refused = await Assert.ThrowsAsync<CapabilityFailure>(() => new PortfolioPriceCommand(() => rig.Service).InvokeAsync(new CapabilityInvocation("no-key", request, rig.Store, Akira), default));

        Assert.Equal("NoIdempotencyKey", refused.Code);
        Assert.Empty(rig.View.Runs);
    }

    [Fact] // ADR-0016 note 32, §11: a scenario command is one command of the log, and undo takes each back whole
    public async Task Undo_takes_a_scenario_command_back_whole()
    {
        var rig = new Rig();
        await rig.Run(rig.Preview("scenario up jpy_ois +10bp; usdjpy -5%"));
        await rig.Run(rig.Preview("scenario ladder jpy_ois -10bp..+10bp step 10bp"));
        Assert.Equal(["up", "jpy_ois -10bp", "jpy_ois +10bp"], rig.View.Scenarios.Select(s => s.Name));

        await rig.Run(rig.Preview("undo"));
        Assert.Equal(["up"], rig.View.Scenarios.Select(s => s.Name));

        await rig.Run(rig.Preview("undo"));
        Assert.Empty(rig.View.Scenarios);
        Assert.Equal(["up"], (await RunAnd(rig, "redo")).Scenarios.Select(s => s.Name));
    }

    static async Task<PortfolioWorkspaceView> RunAnd(Rig rig, string line)
    {
        await rig.Run(rig.Preview(line));
        return rig.View;
    }

    [Fact] // ADR-0016 note 33, ADR-0010 §6: pv01 retry says the trades whose results failed before it asks, takes one trade, and says how many it asked again
    public async Task Pv01_retry_says_what_failed_and_enter_asks_again()
    {
        var down = true;
        var pv01 = new Pv01Provider((request, _) => down ? Task.FromException<decimal>(new InvalidOperationException("The PV01 engine is down.")) : Task.FromResult(1m));
        var rig = new Rig(pv01: pv01);
        await rig.Run(rig.Preview("measures cva fva"));
        // A result is asked for the first time its trade is read, as the page's grid reads it; here each has failed
        // by the time the line is read.
        _ = rig.View;
        await pv01.Settled();
        var before = rig.State.Revision;

        var all = rig.Preview("pv01 retry");
        var one = rig.Preview("pv01 retry draft-2");

        Assert.Equal(("xva.pv01.retry", "", "Asks again for the results that failed on 3 trades: DRAFT-1, DRAFT-2 and DRAFT-3."), (all.Command!.Id, Given(all), all.Would));
        Assert.Equal(("trade: DRAFT-2", "Asks again for the results that failed on 1 trade: DRAFT-2."), (Given(one), one.Would));
        Assert.Equal(new JsonObject { ["workspaceId"] = rig.Id, ["revision"] = before, ["trade"] = "DRAFT-2" }.ToJsonString(), Request(one));
        Assert.Equal("UnknownTarget: No trade T-0 in this workspace.", Read(rig.Preview("pv01 retry T-0")));
        Assert.Equal("UnreadWords: pv01 retry takes one trade, or none for every trade: DRAFT-2 is not read.", Read(rig.Preview("pv01 retry DRAFT-1 DRAFT-2")));

        // Asked against a revision the workspace has left, nothing is asked again (ADR-0016 §2).
        Assert.StartsWith("RevisionMoved: ", await rig.Invoke("xva.pv01.retry", new JsonObject { ["revision"] = before - 1 }));

        down = false;
        Assert.Equal("Asked again for 1 result(s).", await rig.Run(one));
        Assert.Equal("Asked again for 2 result(s).", await rig.Run(all));
        Assert.Equal(before, rig.State.Revision);
        Assert.Equal("Nothing would be asked again: no result has failed.", rig.Preview("pv01 retry").Would);
        Assert.Equal("No result had failed, so nothing was asked again.", await rig.Invoke("xva.pv01.retry", []));
    }
}
