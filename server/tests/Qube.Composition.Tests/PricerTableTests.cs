using System.Text.Json.Nodes;
using Qube.Composition.Application;
using Qube.Composition.Application.Tables;
using Qube.Composition.Application.Workspaces;
using Qube.Samples.Ccr;
using Qube.Samples.Portfolio;
using Qube.Samples.Xva;

namespace Qube.Composition.Tests;

// ADR-0016 §1 to §3 with the pricer registered: its trades, CSAs, netting set, credit, CDS, workspace and a module's
// settings are tables, and the kernel's edit verbs write them as the page's cells do, with the page's checks. Invoked
// as any command is, through the command service. Every name and figure is synthetic.
public class PricerTableTests
{
    static readonly DateOnly Day1 = new(2026, 10, 26);
    static readonly Caller Akira = new("akira", RunOrigin.Pricer);

    sealed class Rig
    {
        public WorkspaceService Workspaces { get; }
        public PortfolioWorkspaces Service { get; }
        public CommandService Commands { get; }
        public CommandLines Lines { get; }
        public string Id { get; }

        public Rig(int existing = 40)
        {
            var register = new MeasureRegister([new XvaMeasures(Pv01Provider.Simulated(TimeSpan.Zero)), new CcrMeasures()]);
            Workspaces = new WorkspaceService(new Dictionary<string, IEditRules> { [PortfolioEditRules.App] = new PortfolioEditRules(register) }, TimeProvider.System);
            var registered = new TableRegister([new PortfolioTables(register)]);
            var tables = new TableService(registered, Workspaces);
            var registry = new CompositionRegistry([new PortfolioModule(), new XvaModule(), new CcrModule(), new TablesModule(() => tables), new WorkspaceCommandsModule(() => Workspaces)]);
            var store = new InMemoryDataStore();
            Service = new PortfolioWorkspaces(Workspaces, store, new RunService(registry, store, TimeProvider.System), register);
            Commands = new CommandService(registry, store);
            Lines = new CommandLines(registry, Workspaces, registered);
            Id = Service.Create("akira", "ABC/LE-01", Day1, existing).WorkspaceId;
        }

        public WorkspaceState State => Workspaces.Find(Id)!;

        public PortfolioWorkspaceView View => Service.View(Id, "akira");

        /// <summary>The rows of a grid as the page reads them: what a command's count is compared with.</summary>
        public IReadOnlyList<EffectiveTrade> Trades(string? grid = null) => Service.Page(Id, "akira", 0, 100_000, [], grid).Rows;

        Task<CommandResult> Invoke(string capability, JsonObject request)
        {
            request["workspaceId"] = Id;
            return Commands.InvokeAsync(capability, "1.0.0", new JsonObject { ["request"] = request }, Akira, Guid.NewGuid().ToString(), default);
        }

        public async Task<JsonObject> Done(string capability, JsonObject request)
        {
            var result = await Invoke(capability, request);
            return result is CommandResult.Done done ? done.Outputs["answer"]!.AsObject()
                : throw new Xunit.Sdk.XunitException($"{capability} was refused: {string.Join(" ", ((CommandResult.Refused)result).Issues.Select(i => i.Message))}");
        }

        public async Task<(string Code, string Message)> Refused(string capability, JsonObject request)
        {
            var refused = Assert.IsType<CommandResult.Refused>(await Invoke(capability, request));
            return (refused.Code, string.Join(" ", refused.Issues.Select(i => i.Message)));
        }

        public Task<JsonObject> Set(string? rows, string set) => Done("entity.set", Request(rows, ("set", set)));

        public Task<(string Code, string Message)> SetRefused(string? rows, string set) => Refused("entity.set", Request(rows, ("set", set)));

        public static JsonObject Request(string? rows, params (string Name, string? Value)[] more)
        {
            var request = new JsonObject();
            if (rows is not null) request["rows"] = rows;
            foreach (var (name, value) in more)
                if (value is not null) request[name] = value;
            return request;
        }

        /// <summary>The edits a command wrote, as the log holds them: each entity's field.</summary>
        public IReadOnlyList<string> Written(long since) => [.. State.Log.Where(e => e.Seq > since).Select(e => $"{e.Target!.Entity}.{e.Target.Field}")];
    }

    static (string Said, string Table, int Matched, int Changed, int Held) Told(JsonObject answer) =>
        (answer["said"]!.GetValue<string>(), answer["table"]!.GetValue<string>(), answer["matched"]!.GetValue<int>(), answer["changed"]!.GetValue<int>(), answer["held"]!.GetValue<int>());

    // An id as a person types it: its end, which names one row (ADR-0016 §2).
    static string End(string tradeId) => tradeId[^6..];

    [Fact] // ADR-0016 §2, §3, §6, §11: `set where ccy=USD after off` switches the USD trades out of After, counts them, and undo puts them back
    public async Task Set_where_switches_the_matching_trades_counts_them_and_undo_puts_them_back()
    {
        var rig = new Rig();
        var usd = rig.Trades().Where(t => t.Currency == "USD").Select(t => t.TradeId).ToList();
        rig.Service.Edit(rig.Id, rig.State.Revision, "akira", $"trade:{usd[0]}", "after", false);
        var before = rig.State.Revision;

        var set = await rig.Set("where ccy=usd", "after=off");

        Assert.True(usd.Count > 4, "The synthetic portfolio holds several USD trades.");
        Assert.Equal(($"Set A to off on {usd.Count - 1} trades; 1 already held it.", PortfolioTables.Trades, usd.Count, usd.Count - 1, 1), Told(set));
        Assert.Equal(before + usd.Count - 1, set["revision"]!.GetValue<long>());
        Assert.All(rig.Trades(), t => Assert.Equal(t.Currency != "USD", t.After));

        var undone = await rig.Done("workspace.undo", new JsonObject { ["command"] = set["command"]!.GetValue<string>() });

        Assert.Equal("Took back: set where ccy=usd after off. redo puts it back.", undone["said"]!.GetValue<string>());
        Assert.Equal([usd[0]], rig.Trades().Where(t => !t.After).Select(t => t.TradeId));
    }

    [Fact] // ADR-0016 §5, note 15: the line `set where ccy=USD after off` says its trades and how many would change before it runs, and offers what the pricer registered
    public async Task The_line_says_its_trades_and_what_would_change_and_offers_the_pricers_words()
    {
        var rig = new Rig();
        var usd = rig.Trades().Where(t => t.Currency == "USD").Select(t => t.TradeId).ToList();
        rig.Service.Edit(rig.Id, rig.State.Revision, "akira", $"trade:{usd[0]}", "after", false);
        var before = rig.State.Revision;
        string Offered(string line) => string.Join(" | ", rig.Lines.Preview(line, rig.Id, null, Akira).Complete.Select(c => c.Value));

        var preview = rig.Lines.Preview("set where ccy=USD after off", rig.Id, null, Akira);

        Assert.Equal((PortfolioTables.Trades, $"trades, {usd.Count} rows", usd.Count), (preview.Rows!.Table, preview.Rows.Title, preview.Rows.Count));
        Assert.Equal(usd.Take(10), preview.Rows.First);
        Assert.Equal($"A: {usd.Count - 1} on to off, 1 already off", preview.Would);
        Assert.Equal(new JsonObject { ["workspaceId"] = rig.Id, ["revision"] = before, ["rows"] = "where ccy=USD", ["set"] = "after off" }.ToJsonString(), preview.Inputs!["request"]!.ToJsonString());
        Assert.Equal(before, rig.State.Revision);
        // A trade's CSA is a row of the CSAs, and a currency one of the registered ones.
        Assert.Equal("CSA-A | CSA-B", Offered($"set {End(usd[0])} csa "));
        Assert.Equal("ccy=GBP | ccy=JPY | ccy=USD | ccy=EUR", Offered("set where ccy="));
        Assert.Equal("existing", Offered("set ex"));
        Assert.Equal("new | netting", Offered("set ne"));

        var ran = await rig.Commands.InvokeAsync(preview.Command!.Id, preview.Command.Version, preview.Inputs!, Akira, Guid.NewGuid().ToString(), default);
        Assert.Equal($"Set A to off on {usd.Count - 1} trades; 1 already held it.", Assert.IsType<CommandResult.Done>(ran).Outputs["answer"]!["said"]!.GetValue<string>());
    }

    [Fact] // ADR-0016 §2, note 8: a condition reads its value as the field's form does, a period from the value date, and an amount only in one currency
    public async Task A_condition_reads_a_period_from_the_value_date_and_an_amount_in_one_currency()
    {
        var rig = new Rig();
        var late = rig.Trades().Where(t => t.Currency == "JPY" && string.CompareOrdinal(t.Maturity, "2028-10-26") > 0).Select(t => t.TradeId).ToList();

        var first = rig.Trades()[0].Currency;
        var other = first == "JPY" ? "USD" : "JPY";

        var none = Told(await rig.Set("where ccy=USD and maturity>20y", "after=off"));
        var mixed = await rig.SetRefused("where notional>1bn", "after=off");
        var dated = Told(await rig.Set("where ccy=JPY and maturity>2y", "after=off"));

        Assert.Equal(("Nothing changed: no row of trades matches ccy = USD and maturity > 2046-10-26.", 0, 0), (none.Said, none.Matched, none.Changed));
        Assert.Equal(("MixedUnits", $"notional is in {first} and {other} here, and an amount is not compared across them; say which, as in where ccy={first} and notional>1bn."), mixed);
        Assert.True(late.Count > 0, "The synthetic portfolio holds JPY trades past two years.");
        Assert.Equal((late.Count, late.Count), (dated.Matched, dated.Changed));
        Assert.Equal(late, rig.Trades().Where(t => !t.After).Select(t => t.TradeId));
    }

    [Fact] // ADR-0016 §2 and note 9, ADR-0010 §6: a whole grid's B or A is one edit, all is one per grid, and a row's later edit wins over it
    public async Task A_whole_grid_takes_one_edit_and_a_rows_later_edit_wins()
    {
        var rig = new Rig();
        var (existing, added) = (rig.Trades("existing"), rig.Trades("new"));
        var before = rig.State.Revision;

        var grid = Told(await rig.Set("existing", "before=off"));
        var afterGrid = rig.State.Revision;
        var all = Told(await rig.Set("all", "A=off"));
        var afterAll = rig.State.Revision;
        var row = Told(await rig.Set(End(existing[0].TradeId), "after=on b=on"));

        Assert.Equal(($"Set B to off on {existing.Count} trades.", existing.Count, existing.Count, 0), (grid.Said, grid.Matched, grid.Changed, grid.Held));
        Assert.Equal(["grid:existing.before"], rig.Written(before).Take(1));
        Assert.Equal(before + 1, afterGrid);
        Assert.Equal(($"Set A to off on {existing.Count + added.Count} trades.", existing.Count + added.Count), (all.Said, all.Changed));
        Assert.Equal(["grid:existing.after", "grid:new.after"], rig.Written(afterGrid).Take(2).Order().ToList());
        Assert.Equal(afterGrid + 2, afterAll);
        Assert.Equal($"Set {existing[0].TradeId} A to on and B to on.", row.Said);
        Assert.Equal([existing[0].TradeId], rig.Trades().Where(t => t.After || t.Before).Select(t => t.TradeId));
    }

    [Fact] // ADR-0016 §1, §3: a CSA's term, trades' CSA, the netting set, the credit, the CDS, a module's setting and the display currency are written as the page's cells write them
    public async Task A_cell_of_every_table_is_written_as_the_pages_cell_writes_it()
    {
        var rig = new Rig();
        var two = rig.Trades("existing").Where(t => t.Csa == "CSA-A").Take(2).Select(t => t.TradeId).ToList();
        var before = rig.State.Revision;

        var threshold = Told(await rig.Set("CSA-A", "threshold=5mm"));
        var csa = Told(await rig.Set($"{End(two[0])}, {End(two[1])}", "csa=CSA-B"));
        var afterTwo = rig.State.Revision;
        var recovery = Told(await rig.Set("NS-001", "recovery=40%"));
        var spread = Told(await rig.Set("cds", "5y=120bp"));
        var credit = Told(await rig.Set("credit", "type=proxy sector=financials region=europe rating=A"));
        var setting = Told(await rig.Set("xva", "cost of capital=12%"));
        var unnamed = Told(await rig.Set(null, "coc=11%"));
        var currency = Told(await rig.Set(null, "currency=usd"));

        var view = rig.View;
        Assert.Equal(("Set CSA-A threshold to 5mm.", PortfolioTables.Csas), (threshold.Said, threshold.Table));
        // The edit a typed cell makes: in the Base, marked against Original, and no case (ADR-0010 §9).
        var term = view.Csas.Single(c => c.CsaId == "CSA-A");
        Assert.Equal((5_000_000m, 0m), (term.CounterpartyThreshold, term.Original!["counterpartyThreshold"]!.GetValue<decimal>()));
        Assert.Equal(["Base"], view.Cases.Select(c => c.Title));
        // One command, an edit a row.
        Assert.Equal(before + 3, afterTwo);
        Assert.Equal(($"Set csa to CSA-B on {two[0]} and {two[1]}.", PortfolioTables.Trades, 2), (csa.Said, csa.Table, csa.Changed));
        Assert.All(rig.Trades().Where(t => two.Contains(t.TradeId)), t => Assert.Equal("CSA-B", t.Csa));
        Assert.Equal(("Set NS-001 recovery to 40%.", PortfolioTables.Netting, 0.4m), (recovery.Said, recovery.Table, view.Credit.CvaRecovery));
        Assert.Equal(("Set cds 5y to 120bp.", PortfolioTables.Cds), (spread.Said, spread.Table));
        Assert.Equal(("Set credit cds type to proxy, proxy sector to financials, proxy region to europe and proxy rating to A.", PortfolioTables.Credit, CdsCatalog.Proxy),
            (credit.Said, credit.Table, view.Credit.Choice.Type));
        Assert.Equal(("Set xva cost of capital to 12%.", PortfolioTables.SettingsOf("xva")), (setting.Said, setting.Table));
        Assert.Equal(("Set xva cost of capital to 11%.", PortfolioTables.SettingsOf("xva")), (unnamed.Said, unnamed.Table));
        Assert.Equal(0.11m, rig.State.Overrides.Values.Single(o => o.Target.Entity == "settings:xva" && o.Target.Field == XvaMeasures.CostOfCapital).Value!.GetValue<decimal>());
        Assert.Equal(("Set workspace currency to USD.", PortfolioTables.Workspace, "USD"), (currency.Said, currency.Table, view.DisplayCurrency));
        // The Changes tab names each as it did before the tables were registered.
        Assert.Equal(["CSA-A threshold 5mm", $"{two[0]} csa CSA-B", $"{two[1]} csa CSA-B", "NS-001 recovery 40%"],
            rig.Service.Changes(rig.Id, "akira").Common.Take(4).Select(c => $"{c.Title} {c.Text}"));
    }

    [Fact] // ADR-0016 §3, ADR-0010 §9: a row that is not there, a value the grid would refuse and a field no command sets are refused as the grid refuses them, and nothing is written
    public async Task A_command_is_refused_as_the_grid_refuses_the_cell()
    {
        var rig = new Rig();
        var one = rig.Trades("existing")[0].TradeId;
        rig.Service.Edit(rig.Id, rig.State.Revision, "akira", "trade:DRAFT-1", "xva.margin", 2m);
        var before = rig.State.Revision;
        var typed = Assert.Throws<WorkspaceRefused>(() => rig.Service.Edit(rig.Id, before, "akira", "trade:DRAFT-1", "xva.pvAdj", 5m));

        var noRow = await rig.SetRefused("CSA-Z", "threshold=5mm");
        var negative = await rig.SetRefused("CSA-A", "threshold=-1mm");
        var readOnly = await rig.SetRefused(End(one), "maturity=2030-01-01");
        var filled = await rig.SetRefused("DRAFT-1", "pv adj=5");
        var unknown = await rig.SetRefused("CSA-A", "colour=red");
        var oneBad = await rig.SetRefused($"{End(one)}, DRAFT-1", "notional=lots");
        var recovery = await rig.SetRefused("NS-001", "recovery=120%");

        Assert.Equal(("UnknownRow", "No row CSA-Z in trades, csas, netting, credit, cds, workspace or xva."), noRow);
        Assert.Equal(("InvalidValue", "CSA-A threshold is an amount of 0 or more; -1mm is not."), negative);
        Assert.Equal(("NotEditable", "maturity is not a field a command sets; trades takes notional, rate, schedule, csa, curve, im-r, im-p, B, A, margin and pvadj."), readOnly);
        Assert.Equal((typed.Code, typed.Message), filled);
        Assert.Equal("UnknownField", unknown.Code);
        Assert.StartsWith("csas has no field colour; its fields are ", unknown.Message);
        Assert.Equal("InvalidValue", oneBad.Code);
        Assert.Equal(("InvalidValue", "A CVA recovery is empty or from 0 up to 1."), recovery);
        Assert.Equal(before, rig.State.Revision);
    }

    [Fact] // ADR-0016 §1, ADR-0010 §8's note of 2026-11-09: the CDS is a table of one row while a curve is chosen, and says why it has none
    public async Task The_cds_has_no_row_while_no_curve_is_chosen_and_says_why()
    {
        var rig = new Rig();
        rig.Service.ChooseCds(rig.Id, rig.State.Revision, "akira", "none");
        var missing = rig.View.Credit.Missing;

        var refused = await rig.SetRefused("cds", "5y=120bp");

        Assert.NotNull(missing);
        Assert.Equal(("NoRow", missing), refused);
    }

    [Fact] // ADR-0016 §1, §2, ADR-0010 §10: a module's typed column is a field while one of its measures is selected, and its amount is compared in one currency
    public async Task A_modules_column_is_a_field_while_it_is_selected_and_its_amount_keeps_to_one_currency()
    {
        var rig = new Rig();
        var first = rig.Trades()[0].Currency;
        var other = first == "JPY" ? "USD" : "JPY";

        var mixed = await rig.SetRefused("where pv adj>100", "after=off");
        var typed = Told(await rig.Set("DRAFT-1", "pv adj=-3k"));
        rig.Service.SetMeasures(rig.Id, rig.State.Revision, "akira", [CcrMeasures.SpotCapital]);
        var hidden = await rig.SetRefused("DRAFT-1", "margin=2");
        var reset = Told(await rig.Done("entity.reset", Rig.Request("DRAFT-1")));

        Assert.Equal(("MixedUnits", $"pvadj is in {first} and {other} here, and an amount is not compared across them; say which, as in where ccy={first} and pvadj>100."), mixed);
        Assert.Equal("Set DRAFT-1 pvadj to -3000.", typed.Said);
        // With no measure of XVA selected the grid shows none of its columns, and a command has none of them as a field.
        Assert.Equal(("UnknownField", "trades has no field margin; its fields are trade id, product, ccy, notional, maturity, rate, schedule, last fixing, official pv, csa, curve, im-r, im-p, B, A."),
            hidden);
        // An edit of a column that does not show stays: reset of every field is of the fields the table has now.
        Assert.Equal("Nothing changed: DRAFT-1 has no edit.", reset.Said);
        Assert.Equal(-3000m, rig.State.Overrides.Values.Single(o => o.Target.Entity == "trade:DRAFT-1" && o.Target.Field == "xva.pvAdj").Value!.GetValue<decimal>());
    }

    [Fact] // ADR-0016 §2, ADR-0003: a row a command touched is read from what stands now, never from an earlier reading of it
    public async Task A_row_a_command_touched_follows_its_csas_curve_its_own_later_edit_and_undo()
    {
        var rig = new Rig();
        var one = rig.Trades("existing").First(t => t.Csa == "CSA-A").TradeId;
        (string Curve, bool After) Read() => rig.Trades().Where(t => t.TradeId == one).Select(t => (t.Curve, t.After)).Single();
        var read = new List<(string Curve, bool After)>();

        // Each reading follows a command, so a row worked out for an earlier state would show here as a stale value.
        await rig.Set("where csa=CSA-A", "after=off");
        read.Add(Read());
        await rig.Set("CSA-A", "curve=usd_sofr");
        read.Add(Read());
        await rig.Set(End(one), "curve=jpy_ois");
        read.Add(Read());
        await rig.Done("workspace.undo", new JsonObject { ["revision"] = rig.State.Revision });
        read.Add(Read());
        await rig.Set("existing", "after=on");
        read.Add(Read());
        await rig.Done("workspace.undo", new JsonObject { ["revision"] = rig.State.Revision });
        read.Add(Read());

        Assert.Equal([("jpy_ois", false), ("usd_sofr", false), ("jpy_ois", false), ("usd_sofr", false), ("usd_sofr", true), ("usd_sofr", false)], read);
    }

    [Fact] // ADR-0016 §3 and note 10: reset removes a row's own edits and a whole grid's, and remove refuses a row the workspace did not make, saying what to do instead
    public async Task Reset_removes_edits_and_remove_refuses_an_acquired_row()
    {
        var rig = new Rig();
        var existing = rig.Trades("existing");
        var one = existing[0].TradeId;
        await rig.Set("existing", "after=off");
        await rig.Set(End(one), "notional=5bn after=on");
        await rig.Set("CSA-A", "threshold=5mm mta=1mm");
        var before = rig.State.Revision;

        var field = Told(await rig.Done("entity.reset", Rig.Request(End(one), ("field", "notional"))));
        var grid = Told(await rig.Done("entity.reset", Rig.Request("existing", ("field", "A"))));
        var csa = Told(await rig.Done("entity.reset", Rig.Request("CSA-A")));
        var removed = await rig.Refused("entity.remove", Rig.Request("DRAFT-1"));
        var term = await rig.Refused("entity.remove", Rig.Request("CSA-A"));

        Assert.Equal($"Reset {one} notional.", field.Said);
        Assert.Equal(($"Reset A on {existing.Count} trades.", existing.Count), (grid.Said, grid.Changed));
        Assert.Equal("Reset CSA-A.", csa.Said);
        Assert.Equal([$"trade:{one}.notional", "grid:existing.after", $"trade:{one}.after", "csa:CSA-A.counterpartyThreshold", "csa:CSA-A.counterpartyMta"], rig.Written(before));
        Assert.Empty(rig.State.Overrides);
        Assert.Equal(("NotRemovable", "DRAFT-1 is a trade the workspace was given, not one it made: take it out of After with set DRAFT-1 A off."), removed);
        Assert.Equal(("NotRemovable", "CSA-A is a CSA of the agreement, which the source holds: a command changes its terms and never removes it."), term);
    }
}
