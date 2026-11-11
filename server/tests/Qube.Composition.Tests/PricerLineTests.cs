using System.Text.Json.Nodes;
using Qube.Composition.Application;
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
        public string Id { get; }

        public Rig(int existing = 40)
        {
            var register = new MeasureRegister([new XvaMeasures(Pv01Provider.Simulated(TimeSpan.Zero)), new CcrMeasures()]);
            Workspaces = new WorkspaceService(new Dictionary<string, IEditRules> { [PortfolioEditRules.App] = new PortfolioEditRules(register) }, TimeProvider.System);
            var registered = new TableRegister([new PortfolioTables(register)]);
            var tables = new TableService(registered, Workspaces);
            PortfolioWorkspaces? service = null;
            var registry = Registry = new CompositionRegistry([new PortfolioModule(), new XvaModule(), new CcrModule(), new TablesModule(() => tables), new WorkspaceCommandsModule(() => Workspaces),
                new PortfolioCommandsModule(() => service ?? throw new InvalidOperationException("The pricer's service is not built yet."), register)]);
            var store = new InMemoryDataStore();
            Service = service = new PortfolioWorkspaces(Workspaces, store, new RunService(registry, store, TimeProvider.System), register);
            Commands = new CommandService(registry, store);
            Lines = new CommandLines(registry, Workspaces, registered, [new PortfolioShortForms()]);
            Id = Service.Create("akira", "ABC/LE-01", Day1, existing).WorkspaceId;
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
    [InlineData("cds", "")]
    public void Cds_offers_the_catalogues_words(string line, string offered) => Assert.Contains(offered, Offered(new Rig().Preview(line)));

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

    [Fact] // ADR-0016 note 27: each of the pricer's verbs says it is the pricer's, and the kernel's say no application
    public void The_pricers_verbs_say_their_application()
    {
        var rig = new Rig();
        static string Typed(string id, Qube.Composition.Contracts.CommandForm form) =>
            $"{id}: {string.Join(" ", [.. form.Words, .. form.Arguments.Select(a => a.Required ? $"<{a.Name}>" : $"[{a.Name}]")])} ({form.App})";

        var verbs = rig.Registry.CapabilityDescriptors.Where(c => c.Command is not null && c.CapabilityId.StartsWith("xva.")).Select(c => Typed(c.CapabilityId, c.Command!));
        var kernel = rig.Registry.CapabilityDescriptors.Where(c => c.Command is not null && !c.CapabilityId.StartsWith("xva.")).Select(c => c.Command!.App);

        Assert.Equal(["xva.vary: vary <cell> <values> (xva)", "xva.trades.add: add each (xva)", "xva.cds: cds <cds> (xva)", "xva.measures: measures <measures> (xva)",
            "xva.case.remove: case remove <case> (xva)", "xva.cases.clear: cases clear (xva)"], verbs);
        Assert.Equal(["xva.before: before <which> [grid] (xva)", "xva.after: after <which> [grid] (xva)", "xva.currency: currency <currency> (xva)"],
            rig.Lines.ShortForms.Select(f => Typed(f.FormId, f.Command)));
        Assert.All(kernel, Assert.Null);
    }
}
