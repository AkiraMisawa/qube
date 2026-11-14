using System.Globalization;
using System.Text.Json.Nodes;
using Qube.Composition.Application;
using Qube.Samples.Xva;

namespace Qube.Composition.Tests;

// ADR-0016 note 69 (owner, 2026-11-14): the request the XVA analytics takes holds the official PV by leg, in
// /deals/official_pv.csv, written from a case of the accepted input for one of its sides. Every name and figure is
// synthetic.
public class XvaRequestFilesTests
{
    const string Swap = "ABC-LE-01-E00004", UsdSwap = "ABC-LE-01-E00013", Forward = "ABC-LE-01-E00003";
    const string File = "/deals/official_pv.csv";

    // A trade as a case of the accepted input carries it, as far as the file reads it, held by both sides. A leg's own
    // key is made unlike its place.
    static JsonObject Trade(string id, params (string Currency, decimal Pv, decimal? Notional)[] legs) => new()
    {
        ["tradeId"] = id,
        ["before"] = true,
        ["after"] = true,
        ["legs"] = new JsonArray([.. legs.Select((l, at) => new JsonObject { ["legId"] = $"{id}/{at + 7}", ["currency"] = l.Currency, ["pv"] = l.Pv, ["notional"] = l.Notional })]),
    };

    static JsonObject Case(params JsonNode?[] trades) => new() { ["trades"] = new JsonArray(trades) };

    static string Refusal(JsonNode @case) => Assert.Throws<CapabilityFailure>(() => XvaRequestFiles.OfficialPv(@case, XvaRequestSide.After)).Message;

    [Fact] // ADR-0016 note 69: a row for each leg, its id the trade's, an underscore and the leg's place under its trade from 0
    public void The_file_holds_a_row_for_each_leg_numbered_from_0_under_its_trade()
    {
        var @case = Case(
            Trade("T-1", ("GBP", 1_000_000m, 10_000_000m), ("USD", -250_000.50m, 12_500_000m)),
            Trade("T-2"),
            Trade("T_3", ("JPY", -350_000m, null)));
        // A figure is written as the input holds it wherever the server runs: here numbers are said with a decimal comma.
        var here = CultureInfo.CurrentCulture;
        var comma = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        (comma.NumberFormat.NumberDecimalSeparator, comma.NumberFormat.NegativeSign) = (",", "~");
        CultureInfo.CurrentCulture = comma;
        try
        {
            // A trade with no leg has no row, and a leg with no notional leaves the field empty: nothing is written for it.
            Assert.Equal("id,ccy,pv,notional\nT-1_0,GBP,1000000,10000000\nT-1_1,USD,-250000.50,12500000\nT_3_0,JPY,-350000,\n",
                XvaRequestFiles.OfficialPv(@case, XvaRequestSide.After));
        }
        finally
        {
            CultureInfo.CurrentCulture = here;
        }
    }

    [Fact] // ADR-0016 note 69: Before and After are requests of their own, so a request's file holds its side's trades and no other
    public void A_request_holds_the_trades_of_its_side_only()
    {
        var added = Trade("NEW", ("USD", 5m, 50m));
        added["before"] = false;
        var taken = Trade("OUT", ("EUR", 7m, 70m));
        taken["after"] = false;
        var @case = Case(Trade("BOTH", ("GBP", 1m, 10m)), added, taken);

        Assert.Equal("id,ccy,pv,notional\nBOTH_0,GBP,1,10\nOUT_0,EUR,7,70\n", XvaRequestFiles.OfficialPv(@case, XvaRequestSide.Before));
        Assert.Equal("id,ccy,pv,notional\nBOTH_0,GBP,1,10\nNEW_0,USD,5,50\n", XvaRequestFiles.OfficialPv(@case, XvaRequestSide.After));
    }

    [Theory] // ADR-0016 note 69: a row that would not be read back as it was written is refused, not written
    [InlineData("T,1")]
    [InlineData("T\"1")]
    [InlineData("T\n1")]
    [InlineData("T\r1")]
    public void A_trade_id_that_would_break_its_row_is_refused(string id) =>
        Assert.Equal($"{File} cannot hold {id}_0: a comma, a quote or a line break would break its row.", Refusal(Case(Trade(id, ("GBP", 1m, 1m)))));

    [Fact] // CLAUDE.md, no fallback: a request prices Before or After, and a side that is neither is not taken for one of them
    public void A_side_that_is_neither_Before_nor_After_is_refused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => XvaRequestFiles.OfficialPv(Case(Trade("T-1", ("GBP", 1m, 1m))), (XvaRequestSide)7));

    [Fact] // ADR-0016 note 69: a case holds a trade once, so one it holds twice is refused with its name, on the other side too
    public void A_trade_a_case_holds_twice_is_refused()
    {
        var again = Trade("T-1");
        again["after"] = false;

        Assert.Equal($"{File} cannot hold trade T-1 twice: a case holds a trade once.", Refusal(Case(Trade("T-1", ("GBP", 1m, 1m)), Trade("T-2"), again)));
    }

    [Theory] // ADR-0016 notes 53, 69: the input says a leg's notional even where it holds none, so a leg without the name is refused, as one without a PV is
    [InlineData("notional", "T-1_0 carries no notional, which /deals/official_pv.csv is written from.")]
    [InlineData("pv", "T-1_0 carries no pv, which /deals/official_pv.csv is written from.")]
    [InlineData("currency", "T-1_0 carries no currency, which /deals/official_pv.csv is written from.")]
    public void A_leg_without_a_field_the_input_carries_is_refused_with_its_name(string field, string refusal)
    {
        var trade = Trade("T-1", ("GBP", 1m, 1m));
        trade["legs"]![0]!.AsObject().Remove(field);

        Assert.Equal(refusal, Refusal(Case(trade)));
    }

    [Fact] // ADR-0016 note 69: what is not of the form the input carries is refused, with the trade or the leg it is said of
    public void What_is_of_another_form_is_refused_with_its_name()
    {
        static JsonObject With(string field, JsonNode? value, bool onLeg = false)
        {
            var trade = Trade("T-1", ("GBP", 1m, 1m));
            (onLeg ? trade["legs"]![0]!.AsObject() : trade)[field] = value;
            return trade;
        }
        var unflagged = Trade("T-1");
        unflagged.Remove("after");

        Assert.Equal("T-1_0 carries a pv that is no number.", Refusal(Case(With("pv", "1,000", onLeg: true))));
        Assert.Equal("T-1_0 carries a notional that is no number.", Refusal(Case(With("notional", "none", onLeg: true))));
        Assert.Equal("T-1_0 carries a currency that is no text.", Refusal(Case(With("currency", 7, onLeg: true))));
        Assert.Equal("T-1_0 carries an empty currency.", Refusal(Case(With("currency", "", onLeg: true))));
        Assert.Equal("T-1_1 is no leg: the accepted input carries nothing in its place.", Refusal(Case(With("legs", new JsonArray(Trade("T-1", ("GBP", 1m, 1m))["legs"]![0]!.DeepClone(), null)))));
        Assert.Equal("Trade T-1's legs are no list.", Refusal(Case(With("legs", "none"))));
        Assert.Equal($"Trade T-1 carries no legs, which {File} is written from.", Refusal(Case(With("legs", null))));
        Assert.Equal($"Trade T-1 carries no after, which {File} is written from.", Refusal(Case(unflagged)));
        Assert.Equal("Trade T-1's after is neither true nor false.", Refusal(Case(With("after", "yes"))));
        Assert.Equal($"A trade of the accepted input carries no tradeId, which {File} is written from.", Refusal(Case(With("tradeId", null))));
        Assert.Equal("A trade of the accepted input carries a tradeId that is no text.", Refusal(Case(With("tradeId", 7))));
        Assert.Equal("A trade of the accepted input carries an empty tradeId.", Refusal(Case(With("tradeId", ""))));
        Assert.Equal("A case of the accepted input holds nothing in a trade's place.", Refusal(Case([null])));
        Assert.Equal($"A case of the accepted input carries no trades, which {File} is written from.", Refusal(new JsonObject()));
        Assert.Equal("A case of the accepted input holds trades that are no list.", Refusal(new JsonObject { ["trades"] = "none" }));
    }

    [Fact] // ADR-0016 notes 53, 69: the file is written from the legs Price froze, numbered by their place under their trade then
    public async Task The_file_is_written_from_the_legs_Price_froze()
    {
        var rig = new PricerRig();
        await rig.Run("set E00004 pv -350,000 USD");
        await rig.Run("new leg E00013 eur 250,000.50");
        await rig.Run("remove legs E00013/1");
        var @case = (await rig.Priced())["cases"]![0]!;

        var rows = XvaRequestFiles.OfficialPv(@case, XvaRequestSide.After).Split('\n');

        Assert.Equal("id,ccy,pv,notional", rows[0]);
        // The swap's two legs were taken out by the overwrite, and the leg made in their place is the trade's first.
        Assert.Equal([$"{Swap}_0,USD,-350000,"], rows.Where(r => r.StartsWith(Swap)));
        // The source's first leg (`/1` in the workspace) was removed: its second (`/2`) is the trade's first then, and
        // the leg made is its second, its fraction as the input the Run keeps holds it (the line's 250,000.50 is 250000.5 there).
        Assert.Equal([$"{UsdSwap}_0,USD,-313247,81000000", $"{UsdSwap}_1,EUR,250000.5,"], rows.Where(r => r.StartsWith(UsdSwap)));
        // An untouched trade's legs keep the source's order.
        Assert.Equal([$"{Forward}_0,JPY", $"{Forward}_1,USD"], rows.Where(r => r.StartsWith(Forward)).Select(r => r[..(Forward.Length + 6)]));
        // A new trade no leg was made for is in After and has no row: no book holds a PV for it.
        Assert.Contains(@case["trades"]!.AsArray(), t => t!["after"]!.GetValue<bool>() && t["tradeId"]!.GetValue<string>().StartsWith("DRAFT-"));
        Assert.DoesNotContain(rows, r => r.StartsWith("DRAFT-"));
    }
}
