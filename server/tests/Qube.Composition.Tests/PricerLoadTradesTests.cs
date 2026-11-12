using System.Text.Json.Nodes;
using Qube.Composition.Application;
using Qube.Composition.Application.Tables;
using Qube.Composition.Application.Workspaces;
using Qube.Samples.Portfolio;

namespace Qube.Composition.Tests;

// ADR-0016 note 59 of 2026-11-12: `load trades <ids>` brings booked trades of the synthetic store into the New grid.
// Driven as a user does, by the line; what a page's own door does (a cell typed, the Changes tab, a date moved) is
// asked of the pricer's service, as the page asks it.
public class PricerLoadTradesTests
{
    static long Revision(PricerRig rig) => rig.State.Revision;

    static WorkspaceRefused Refused(Action act) => Assert.Throws<WorkspaceRefused>(act);

    [Fact] // note 59: the trades named join the New grid, out of Before and in After, as one command that one undo takes back
    public async Task Load_trades_brings_booked_trades_into_the_new_grid()
    {
        var rig = new PricerRig();
        var drafts = rig.Keys("new");

        Assert.Equal("Loaded B00012 and B00013 as new trades.", await rig.Run("load trades B00012, b00013"));

        Assert.Equal([.. drafts, "B00012", "B00013"], rig.Keys("new"));
        Assert.Equal("off on CSA-A", rig.Written(PortfolioTables.Trades, "B00012", "before", "after", "csa"));
        Assert.Equal("off on CSA-A", rig.Written(PortfolioTables.Trades, "B00013", "before", "after", "csa"));
        Assert.Equal("Took back: load trades B00012, B00013. redo puts it back.", await rig.Run("undo"));
        Assert.Equal(drafts, rig.Keys("new"));
    }

    [Fact] // note 59: a reading says each trade it would load, with its product, currency and notional, and loads nothing
    public void A_reading_says_the_trades_it_would_load()
    {
        var rig = new PricerRig();

        Assert.Equal("Loads B00012 (IRS JPY 2.6bn) and B00002 (FXForward USD 25mm) as new trades.", rig.Read("load trades b00012 B00002"));
        Assert.Equal("Loads B00013 (IRS USD 80mm) as a new trade.", rig.Read("load trades B00013"));
        Assert.DoesNotContain("B00012", rig.Keys("new"));
    }

    [Fact] // note 59: no id, an id twice, an id the store has not, and a trade loaded already are refused by name, with nothing loaded
    public async Task What_cannot_be_loaded_is_refused_by_name_and_none_is_loaded()
    {
        var rig = new PricerRig();
        var drafts = rig.Keys("new");

        Assert.Equal("NothingToLoad: load trades takes the ids of the booked trades to load, such as load trades B00001, B00002.", rig.Read("load trades"));
        Assert.Equal("GivenTwice: B00012 is named twice.", rig.Read("load trades B00012, b00012"));
        Assert.Equal("UnknownTrade: The store of booked trades has no trade B00099 or DRAFT-1: its trades are B00001 to B00040.", rig.Read("load trades B00012, B00099, DRAFT-1"));
        Assert.Equal(drafts, rig.Keys("new"));

        await rig.Run("load trades B00012");
        Assert.Equal("TradeHeld: B00012 is loaded already.", rig.Read("load trades B00013, B00012"));
        Assert.Equal([.. drafts, "B00012"], rig.Keys("new"));
    }

    [Fact] // note 59: a trade past its maturity on the value date is not in the store, and is refused as that
    public void A_trade_past_its_maturity_is_not_in_the_store()
    {
        var rig = new PricerRig();
        rig.Service.ChangeValueDate(rig.Id, Revision(rig), "akira", new DateOnly(2030, 1, 15));

        Assert.Equal("UnknownTrade: The store of booked trades has not B00039 on 2030-01-15: it is past maturity then.", rig.Read("load trades B00012, B00039"));
        // Every id it has not is named at once, whichever way it has it not.
        Assert.Equal("UnknownTrade: The store of booked trades has no trade B00099: its trades are B00001 to B00040. And it has not B00039 on 2030-01-15: it is past maturity then.",
            rig.Read("load trades B00039, B00012, B00099"));
    }

    [Fact] // note 59: the line offers the store's ids that begin with what is typed, without those the line or the workspace has
    public async Task The_line_completes_ids_from_the_store()
    {
        var rig = new PricerRig();
        await rig.Run("load trades B00012");
        string[] Offered(string line) => [.. rig.Preview(line).Complete.Select(c => $"{c.Value}@{c.Start} ({c.Detail})")];

        Assert.Equal(["B00010@12 (IRS USD 16mm)", "B00011@12 (FXForward JPY 7bn)", "B00013@12 (IRS USD 80mm)", "B00014@12 (FXForward USD 35mm)", "B00015@12 (IRS JPY 9bn)",
            "B00016@12 (IRS JPY 4.5bn)", "B00017@12 (IRS JPY 9.9bn)", "B00018@12 (FXForward USD 54mm)", "B00019@12 (IRS USD 10mm)"], Offered("load trades b0001"));
        Assert.Equal(["B00014@20 (FXForward USD 35mm)"], Offered("load trades B00013, B0001").Where(o => o.StartsWith("B00014")));
        Assert.DoesNotContain(Offered("load trades B00013, B0001"), o => o.StartsWith("B00013") || o.StartsWith("B00012"));
        // As many at a time as a line lists of a table's rows, with how many more there are.
        var all = rig.Preview("load trades ");
        Assert.Equal((20, 19), (all.Complete.Count, all.More));
    }

    [Fact] // note 59, note 26: a load moves the New grid's order, so rows chosen before it are not acted on; the source version stays the acquired one
    public async Task A_load_moves_the_grids_order_and_leaves_the_source_version()
    {
        var rig = new PricerRig();
        var shown = rig.Service.Page(rig.Id, "akira", 0, 100, [], "new");
        var chosen = new LineView(PortfolioTables.Trades, "new", [], shown.OrderToken, Ranges: [new(0, 2)], Focus: new("notional", Position: 0));
        var version = rig.Service.View(rig.Id, "akira").SourceVersion;
        var existing = rig.Service.Page(rig.Id, "akira", 0, 100, [], "existing").OrderToken;
        var sorted = rig.Service.Page(rig.Id, "akira", 0, 100, [new("notional", "descending")], "existing").OrderToken;
        Assert.Equal("A: 2 on to off", rig.Preview("set selected a off", chosen).Would);

        await rig.Run("load trades B00012");

        var stale = rig.Preview("set selected a off", chosen);
        Assert.Equal(("ViewMoved", "trades (new), no longer in the order it was shown in"), (stale.Refused!.Code, stale.View));
        Assert.NotEqual(shown.OrderToken, rig.Service.Page(rig.Id, "akira", 0, 100, [], "new").OrderToken);
        Assert.Equal(version, rig.Service.View(rig.Id, "akira").SourceVersion);
        // A loaded trade is in the New grid alone: the Existing grid's rows are where they were, sorted or not.
        Assert.Equal(existing, rig.Service.Page(rig.Id, "akira", 0, 100, [], "existing").OrderToken);
        Assert.Equal(sorted, rig.Service.Page(rig.Id, "akira", 0, 100, [new("notional", "descending")], "existing").OrderToken);
        await rig.Run("remove trades B00012");
        Assert.Equal(shown.OrderToken, rig.Service.Page(rig.Id, "akira", 0, 100, [], "new").OrderToken);
    }

    [Fact] // note 59: a loaded trade's terms, legs and independent amounts are the store's, and are set, taken out and reset as any trade's
    public async Task A_loaded_trade_is_edited_as_any_trade()
    {
        var rig = new PricerRig();
        await rig.Run("load trades B00002");

        Assert.Equal("FXForward USD 25mm 2031-10-19 86.881k", rig.Written(PortfolioTables.Trades, "B00002", "product", "currency", "notional", "maturity", "officialPv"));
        Assert.Equal(2, rig.Legs("B00002").Length);
        Assert.Single(rig.Amounts("B00002"));

        await rig.Run("set B00002 notional 30mm");
        await rig.Run("set B00002 model M-FX-09");
        await rig.Run("remove legs B00002/2");
        await rig.Run("new leg B00002 eur 250,000");
        Assert.Equal("30mm M-FX-09", rig.Written(PortfolioTables.Trades, "B00002", "notional", "modelId"));
        Assert.Equal(["1", "3"], rig.Legs("B00002").Select(l => l[..1]));

        await rig.Run("reset B00002 notional");
        Assert.Equal("25mm", rig.Written(PortfolioTables.Trades, "B00002", "notional"));
    }

    [Fact] // note 59: remove trades takes a loaded trade out with every edit of it and what was made under it, as one command
    public async Task Remove_trades_removes_a_loaded_trade_with_its_edits()
    {
        var rig = new PricerRig();
        var drafts = rig.Keys("new");
        await rig.Run("load trades B00002, B00012");
        await rig.Run("set B00002 notional 30mm");
        await rig.Run("set B00002 A off");
        await rig.Run("remove legs B00002/2");
        await rig.Run("new leg B00002 eur 250,000");
        var edits = rig.State.Overrides.Count;

        await rig.Run("remove trades B00002");

        Assert.Equal([.. drafts, "B00012"], rig.Keys("new"));
        Assert.Equal(edits - 3, rig.State.Overrides.Count);
        Assert.DoesNotContain(rig.State.Created.Keys, e => e.Contains("B00002"));
        // Loaded again, it is as the store holds it: nothing of what was set on it came back with it.
        await rig.Run("load trades B00002");
        Assert.Equal("25mm on", rig.Written(PortfolioTables.Trades, "B00002", "notional", "after"));
        Assert.Equal(2, rig.Legs("B00002").Length);
        // One undo takes the load back, and one more the removal: the trade is back as it was edited.
        await rig.Run("undo");
        await rig.Run("undo");
        Assert.Equal("30mm off", rig.Written(PortfolioTables.Trades, "B00002", "notional", "after"));
        Assert.Equal(["1", "3"], rig.Legs("B00002").Select(l => l[..1]));
    }

    [Fact] // note 59: a trade the source gave is refused as before, and so is a loaded trade a case holds a change of
    public async Task What_is_not_removed_says_why()
    {
        var rig = new PricerRig();
        await rig.Run("load trades B00012");
        await rig.Run("vary B00012 notional 1bn, 2bn");

        Assert.Equal("NotRemovable: DRAFT-1 is a trade the workspace was given, not one it made: take it out of After with set DRAFT-1 A off.", rig.Read("remove trades DRAFT-1"));
        Assert.Equal("TradeInCase: 2 cases hold a change of B00012, B00012 notional 1bn and B00012 notional 2bn: remove them first, with case remove or cases clear.",
            rig.Read("remove trades B00012"));

        await rig.Run("case remove B00012 notional 1bn");
        Assert.Equal("TradeInCase: The case B00012 notional 2bn holds a change of B00012: remove the case first, with case remove.", rig.Read("remove trades B00012"));
        // A case of add each holds a change of every candidate: the one it adds, and each it leaves out of After.
        await rig.Run("cases clear");
        await rig.Run("add each");
        Assert.Equal("TradeInCase: 4 cases hold a change of B00012, add DRAFT-1, add DRAFT-2, add DRAFT-3 and 1 more: remove them first, with case remove or cases clear.",
            rig.Read("remove trades B00012"));
        await rig.Run("cases clear");
        Assert.Equal("removed, with its edits", rig.Read("remove trades B00012"));
    }

    [Fact] // note 59: the Changes tab lists a loaded trade where it was loaded, and a reset there removes it as the command does
    public async Task The_changes_tab_lists_a_loaded_trade_and_resets_it()
    {
        var rig = new PricerRig();
        await rig.Run("set DRAFT-1 notional 1bn");
        await rig.Run("load trades B00012");
        await rig.Run("set B00012 notional 3bn");

        var changes = rig.Service.Changes(rig.Id, "akira");

        Assert.Equal(3, changes.Count);
        Assert.Equal(("loaded:B00012", "*", "B00012", "loaded", "not in the workspace"),
            (changes.Common[1].Entity, changes.Common[1].Field, changes.Common[1].Title, changes.Common[1].Text, changes.Common[1].OriginalText));
        Assert.Equal(3, rig.Service.View(rig.Id, "akira").Changes);

        rig.Service.Resolve(rig.Id, Revision(rig), "akira", "loaded:B00012", "*", "reset");

        Assert.DoesNotContain("B00012", rig.Keys("new"));
        Assert.Equal(["DRAFT-1 notional"], rig.Service.Changes(rig.Id, "akira").Common.Select(c => c.Title));
        Assert.Equal("Took back: remove trades B00012. redo puts it back.", await rig.Run("undo"));
        Assert.Equal("3bn", rig.Written(PortfolioTables.Trades, "B00012", "notional"));
    }

    [Fact] // note 59: a counterparty change keeps a loaded trade with its own edits, as it keeps a draft's
    public async Task A_counterparty_change_keeps_a_loaded_trade()
    {
        var rig = new PricerRig();
        await rig.Run("load trades B00012");
        await rig.Run("set B00012 notional 3bn");
        await rig.Run("set B00012 csa CSA-B");
        var dropped = rig.Service.PreviewCounterpartyChange(rig.Id, "akira");

        rig.Service.ChangeCounterparty(rig.Id, Revision(rig), "akira", "ABC/LE-02", dropped.Count);

        Assert.Contains("B00012", rig.Keys("new"));
        // Its notional is the trade's own; the CSA it was put under was the counterparty's, and went with it.
        Assert.Equal("3bn CSA-C", rig.Written(PortfolioTables.Trades, "B00012", "notional", "csa"));
    }

    [Fact] // note 59: the terms are the store's on the value date; past its maturity a loaded trade is in no grid, and the Changes tab says so
    public async Task A_loaded_trade_follows_the_value_date()
    {
        var rig = new PricerRig();
        await rig.Run("load trades B00039, B00012");
        await rig.Run("set B00039 notional 2bn");

        var (_, carried) = rig.Service.ChangeValueDate(rig.Id, Revision(rig), "akira", new DateOnly(2030, 1, 15));

        Assert.Equal((0, 0, 1), (carried.Carried, carried.Conflicted, carried.Lost));
        Assert.Contains("B00012", rig.Keys("new"));
        Assert.DoesNotContain("B00039", rig.Keys("new"));
        var listed = rig.Service.Changes(rig.Id, "akira").Common.Single(c => c.Entity == "loaded:B00039");
        Assert.Equal("loaded: past its maturity on 2030-01-15, and in no grid", listed.Text);
        // Back on a date the store has it, it is in the grid again.
        rig.Service.ChangeValueDate(rig.Id, Revision(rig), "akira", PricerRig.Day1);
        Assert.Contains("B00039", rig.Keys("new"));
    }

    [Fact] // note 59: a value is written on a trade the workspace holds; one of the store it has not loaded is refused by name
    public void A_booked_trade_not_loaded_takes_no_edit()
    {
        var rig = new PricerRig();

        var refused = Refused(() => rig.Service.Edit(rig.Id, Revision(rig), "akira", "trade:B00020", "notional", JsonValue.Create(5_000_000m)));
        var leg = Refused(() => rig.Service.Edit(rig.Id, Revision(rig), "akira", "leg:B00020/1", "pv", JsonValue.Create(1m)));

        Assert.Equal(("UnknownTarget", "No trade B00020 in this workspace: load trades B00020 brings it in."), (refused.Code, refused.Message));
        Assert.Equal(("UnknownTarget", "No trade B00020 in this workspace: load trades B00020 brings it in."), (leg.Code, leg.Message));
        Assert.DoesNotContain(rig.State.Overrides.Keys, t => t.Entity.Contains("B00020"));
    }

    [Fact] // notes 53 and 59: Price freezes a loaded trade as any trade, with its terms, legs, independent amounts and details, and names it as loaded
    public async Task Price_freezes_a_loaded_trade_with_everything_set_on_it()
    {
        var rig = new PricerRig();
        await rig.Run("load trades B00002, B00012");
        await rig.Run("set B00002 notional 30mm");
        await rig.Run("set B00002 model M-FX-09");
        await rig.Run("new leg B00002 eur 250,000");
        await rig.Run("set B00012 A off");

        var input = await rig.Priced();

        Assert.Equal(["B00002", "B00012"], input["loadedTrades"]!.AsArray().Select(id => id!.GetValue<string>()));
        var trades = input["cases"]![0]!["trades"]!.AsArray();
        // B00012 is in neither Before nor After, so the case does not price it.
        Assert.DoesNotContain(trades, t => t!["tradeId"]!.GetValue<string>() == "B00012");
        var frozen = trades.Single(t => t!["tradeId"]!.GetValue<string>() == "B00002")!.AsObject();
        Assert.Equal((true, false, true), (frozen["isNew"]!.GetValue<bool>(), frozen["before"]!.GetValue<bool>(), frozen["after"]!.GetValue<bool>()));
        Assert.Equal((30_000_000m, "2031-10-19", 86_881m, "CSA-A"),
            (frozen["notional"]!.GetValue<decimal>(), frozen["maturity"]!.GetValue<string>(), frozen["officialPv"]!.GetValue<decimal>(), frozen["csa"]!.GetValue<string>()));
        Assert.Equal(["B00002/1 USD", "B00002/2 JPY", "B00002/3 EUR"], frozen["legs"]!.AsArray().Select(l => $"{l!["legId"]!.GetValue<string>()} {l["currency"]!.GetValue<string>()}"));
        Assert.Equal(250_000m, frozen["legs"]![2]!["pv"]!.GetValue<decimal>());
        Assert.Equal("B00002/IA1", Assert.Single(frozen["independentAmounts"]!.AsArray())!["amountId"]!.GetValue<string>());
        Assert.Equal(("M-FX-09", "Forward"), (frozen["details"]!["modelId"]!.GetValue<string>(), frozen["details"]!["tradeType"]!.GetValue<string>()));
        // The store the terms were read from, as of the value date: the source version beside it is the counterparty's.
        Assert.Equal("booked@2026-10-26#40", input["loadedTradesVersion"]!.GetValue<string>());
    }

    [Fact] // notes 53 and 59: the input names the loaded trades the store has on the value date; one past its maturity is not priced and not named
    public async Task Price_names_no_loaded_trade_the_store_has_not_on_the_value_date()
    {
        var rig = new PricerRig();
        var none = await rig.Priced();
        Assert.Equal((0, null), (none["loadedTrades"]!.AsArray().Count, none["loadedTradesVersion"]));
        await rig.Run("load trades B00039, B00012");
        rig.Service.ChangeValueDate(rig.Id, Revision(rig), "akira", new DateOnly(2030, 1, 15));

        var input = await rig.Priced();

        Assert.Equal(["B00012"], input["loadedTrades"]!.AsArray().Select(id => id!.GetValue<string>()));
        Assert.StartsWith("booked@2030-01-15#", input["loadedTradesVersion"]!.GetValue<string>());
        Assert.DoesNotContain(input["cases"]![0]!["trades"]!.AsArray(), t => t!["tradeId"]!.GetValue<string>() == "B00039");
    }

    [Fact] // note 59, §11: a load and a removal are each one command, which redo puts back as undo took it
    public async Task A_load_and_a_removal_are_put_back_by_redo()
    {
        var rig = new PricerRig();
        var drafts = rig.Keys("new");
        await rig.Run("load trades B00012");
        await rig.Run("undo");

        Assert.Equal("Put back: load trades B00012.", await rig.Run("redo"));
        Assert.Equal([.. drafts, "B00012"], rig.Keys("new"));

        await rig.Run("set B00012 notional 3bn");
        await rig.Run("remove trades B00012");
        await rig.Run("undo");
        Assert.Equal("3bn", rig.Written(PortfolioTables.Trades, "B00012", "notional"));
        Assert.Equal("Put back: remove trades B00012.", await rig.Run("redo"));
        Assert.Equal(drafts, rig.Keys("new"));
        Assert.DoesNotContain(rig.State.Overrides.Keys, t => t.Entity.Contains("B00012"));
    }

    [Fact] // note 59: a loaded trade past its maturity is removed from the Changes tab with the edits it lost its place for
    public async Task A_loaded_trade_past_its_maturity_is_removed_with_what_was_set_on_it()
    {
        var rig = new PricerRig();
        await rig.Run("load trades B00039");
        await rig.Run("set B00039 notional 2bn");
        rig.Service.ChangeValueDate(rig.Id, Revision(rig), "akira", new DateOnly(2030, 1, 15));
        Assert.Contains(rig.State.Lost.Keys, t => t.Entity == "trade:B00039");

        rig.Service.Resolve(rig.Id, Revision(rig), "akira", "loaded:B00039", "*", "reset");

        Assert.DoesNotContain(rig.State.Lost.Keys, t => t.Entity.Contains("B00039"));
        Assert.DoesNotContain(rig.State.Created.Keys, e => e.Contains("B00039"));
        Assert.DoesNotContain(rig.Service.Changes(rig.Id, "akira").Common, c => c.Title.Contains("B00039"));
        // Loaded again on a date the store has it, it is as the store holds it.
        rig.Service.ChangeValueDate(rig.Id, Revision(rig), "akira", PricerRig.Day1);
        await rig.Run("load trades B00039");
        Assert.NotEqual("2bn", rig.Written(PortfolioTables.Trades, "B00039", "notional"));
    }

    [Fact] // notes 54 and 59: a loaded trade's dialog has the store's terms as its Original, whatever was set on it since
    public async Task A_loaded_trades_draft_keeps_the_stores_terms_as_its_original()
    {
        var rig = new PricerRig();
        await rig.Run("load trades B00002");
        await rig.Run("set B00002 notional 30mm");
        await rig.Run("remove legs B00002/2");

        var form = rig.Lines.Draft(rig.Id, PricerRig.Akira, new(PortfolioTables.Trades, "B00002", Revision(rig), []));

        var notional = form.Fields.Select((f, at) => (Field: f, At: at)).Single(f => f.Field.Field.Name == "notional");
        Assert.NotNull(form.Original);
        Assert.Equal(("30mm", "25mm"), (notional.Field.Written, form.Original.Written[notional.At]));
        Assert.Equal(2, form.Original.Under.Single(t => t.Table == PortfolioTables.Legs).Rows.Count);
        Assert.Equal(1, form.Under.Single(t => t.Table == PortfolioTables.Legs).Rows.Count(r => r.Mark is null));
    }
}
