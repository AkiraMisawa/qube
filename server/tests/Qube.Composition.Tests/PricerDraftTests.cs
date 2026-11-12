using Qube.Composition.Application;
using Qube.Composition.Application.Tables;
using Qube.Samples.Portfolio;

namespace Qube.Composition.Tests;

// ADR-0016 §10 and its notes 54 and 55 on the pricer: a trade's draft holds its fields by group with its PV legs and
// its independent amounts under it, and a CSA's its fields and its templates. Synthetic: ABC/LE-01 at size 40, where
// E00004 is a JPY swap of two legs and one independent amount.
public class PricerDraftTests
{
    const string Trade = "ABC-LE-01-E00004";

    static DraftForm Form(PricerRig rig, string table, string key, params string[] lines) => rig.Lines.Draft(rig.Id, PricerRig.Akira, new(table, key, rig.State.Revision, lines));

    static string[] Rows(DraftForm form, string table) => [.. form.Under.Single(t => t.Table == table).Rows.Select(r => $"{r.Key[(Trade.Length + 1)..]} {r.Written[2]} {r.Written[3]} {r.Mark}".TrimEnd())];

    [Fact] // ADR-0016 note 54: a trade's draft holds its legs and its independent amounts; a leg is made and removed within it, its PV follows, and nothing is written
    public void A_trades_draft_makes_and_removes_its_legs_and_writes_nothing()
    {
        var rig = new PricerRig();
        var revision = rig.State.Revision;

        var form = Form(rig, PortfolioTables.Trades, Trade, $"set {Trade} notional 5bn", $"new leg {Trade} eur 250,000", $"remove legs {Trade}/1");

        Assert.Equal(["Identity", "Terms", "Dates", "Valuation", "Agreement", null, "Model"], form.Fields.Select(f => f.Field.Group).Distinct());
        // The trade's PV is a figure of its legs (note 47), so it moved with them; what it is then is PricerLegTests' to hold.
        Assert.Equal(["notional", "pv"], form.Fields.Where(f => f.Changed).Select(f => f.Field.Word));
        Assert.Equal("5bn", form.Fields.Single(f => f.Field.Name == "notional").Written);
        Assert.Equal(["1 JPY -191258226 removed", "2 JPY 205544922", "3 EUR 250000 made"], Rows(form, PortfolioTables.Legs));
        Assert.Equal(["IA1 JPY 2025-07-18"], Rows(form, PortfolioTables.Amounts));
        Assert.Equal([$"Set {Trade} notional to 5bn.", $"Made {Trade}/3: PV leg.", $"Removed {Trade}/1."], form.Lines.Select(l => l.Said ?? l.Refused!.Sentence));
        Assert.Equal(revision, rig.State.Revision);
    }

    [Fact] // ADR-0016 note 54: a CSA's draft takes a template as one line and marks what it wrote, and Original keeps the source's terms
    public void A_CSAs_draft_takes_a_template_and_Original_keeps_the_sources_terms()
    {
        var rig = new PricerRig();

        var form = Form(rig, PortfolioTables.Csas, "CSA-B", "set csas CSA-B as 1way-cpty ccy=JPY");

        Assert.Equal(["2way: currency*", "1way-self: currency*", "1way-cpty: currency*", "none: currency*"],
            form.Templates.Select(t => $"{t.Word}: {string.Join(", ", t.Takes.Select(x => $"{x.Name}{(x.Required ? "*" : "")}"))}"));
        Assert.Contains("JPY", form.Templates[0].Takes[0].Choices!);
        var terms = form.Fields.Zip(form.Original!.Written).Where(f => f.First.Field.Name is "currency" or "oneWay")
            .Select(f => $"{f.First.Field.Name} {f.First.Written}{(f.First.Changed ? " *" : "")}, from {f.Second}");
        Assert.Equal(["currency JPY *, from USD", "oneWay on *, from off"], terms);
        Assert.Equal(["General", "Counterparty", "Self", "Terms"], form.Fields.Select(f => f.Field.Group).Distinct());
        Assert.Empty(form.Under);
    }

    static LineView Dialog(PricerRig rig, params string[] lines) => new(PortfolioTables.Trades, Draft: new(PortfolioTables.Trades, Trade, rig.State.Revision, lines));

    [Fact] // ADR-0016 note 55: in a trade's draft the line names no row: a field of the trade, a leg made and a leg removed are each the whole line the draft gains, and price is refused while it is open
    public void In_a_trades_draft_the_line_names_no_row_and_price_waits_for_the_draft()
    {
        var rig = new PricerRig();
        var revision = rig.State.Revision;

        var set = rig.Preview("set notional 5bn", Dialog(rig));
        var made = rig.Preview("new leg eur 250,000", Dialog(rig));
        var removed = rig.Preview("remove legs 1", Dialog(rig));
        var price = rig.Preview("price", Dialog(rig, $"set trades {Trade} notional 5bn"));

        Assert.Equal($"set trades {Trade} notional 5bn", Assert.IsType<LineDrafted>(set.Asks).Line);
        Assert.Equal($"new leg trade={Trade} eur 250,000", Assert.IsType<LineDrafted>(made.Asks).Line);
        Assert.Equal($"remove legs {Trade}/1", Assert.IsType<LineDrafted>(removed.Asks).Line);
        Assert.Equal(($"Editing {Trade}: nothing pending.", $"Editing {Trade}: 1 line pending."), (set.View, price.View));
        Assert.Equal(("DraftOpen", $"price is not read while the draft of {Trade} is open: apply writes it, and cancel discards it."), (price.Refused?.Code, price.Refused?.Sentence));
        Assert.Equal(revision, rig.State.Revision);
    }

    [Fact] // ADR-0016 §10, note 55: in a CSA's draft a template is typed with no row, as its button says it, and `leg` is the legs' word in a trade's
    public void In_a_draft_a_template_and_a_row_under_it_are_typed_with_no_row()
    {
        var rig = new PricerRig();
        var csa = new LineView(PortfolioTables.Csas, Draft: new(PortfolioTables.Csas, "CSA-B", rig.State.Revision, []));

        var template = rig.Preview("set as 1way-cpty ccy=JPY", csa);
        var unfilled = rig.Preview("set as 1way-cpty", csa);
        var leg = rig.Preview("remove leg 1", Dialog(rig));

        Assert.Equal("set csas CSA-B as 1way-cpty currency=JPY", Assert.IsType<LineDrafted>(template.Asks).Line);
        Assert.Equal(("MissingTerm", null), (unfilled.Refused?.Code, unfilled.Asks));
        Assert.Equal($"remove legs {Trade}/1", Assert.IsType<LineDrafted>(leg.Asks).Line);
    }

    [Fact] // ADR-0016 note 55: apply writes a trade's draft as one command: the notional, the leg made and the leg removed arrive together, and one undo takes them back
    public async Task Apply_writes_a_trades_draft_as_one_command()
    {
        var rig = new PricerRig();
        string[] lines = [$"set trades {Trade} notional 5bn", $"new leg trade={Trade} eur 250,000", $"remove legs {Trade}/1"];
        var (legs, notional) = (rig.Legs(Trade), rig.Written(PortfolioTables.Trades, Trade, "notional"));

        var said = await rig.Run("apply", Dialog(rig, lines));

        Assert.Equal($"Applied 3 lines to {Trade}.", said);
        Assert.Equal(["2 JPY 205544922", "3 EUR 250000"], rig.Legs(Trade));
        Assert.Equal("5bn", rig.Written(PortfolioTables.Trades, Trade, "notional"));
        await rig.Run("undo");
        Assert.Equal(legs, rig.Legs(Trade));
        Assert.Equal(notional, rig.Written(PortfolioTables.Trades, Trade, "notional"));
        Assert.Equal(2, legs.Length);
    }
}
