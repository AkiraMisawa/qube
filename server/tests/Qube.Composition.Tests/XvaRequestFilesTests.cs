using System.Globalization;
using System.Text.Json.Nodes;
using Qube.Composition.Application;
using Qube.Samples.Xva;

namespace Qube.Composition.Tests;

// ADR-0016 note 69 (owner, 2026-11-14): the request the XVA analytics takes holds the official PV by leg, in
// /deals/official_pv.csv, written from the trades of the accepted input. Every name and figure is synthetic.
public class XvaRequestFilesTests
{
    const string Swap = "ABC-LE-01-E00004", UsdSwap = "ABC-LE-01-E00013", Forward = "ABC-LE-01-E00003";

    // A trade as the accepted input carries it, as far as the file reads it. A leg's own key is made unlike its place.
    static JsonObject Trade(string id, params (string Currency, decimal Pv, decimal? Notional)[] legs) => new()
    {
        ["tradeId"] = id,
        ["legs"] = new JsonArray([.. legs.Select((l, at) => new JsonObject { ["legId"] = $"{id}/{at + 7}", ["currency"] = l.Currency, ["pv"] = l.Pv, ["notional"] = l.Notional })]),
    };

    [Fact] // ADR-0016 note 69: a row for each leg, its id the trade's, an underscore and the leg's place under its trade from 0
    public void The_file_holds_a_row_for_each_leg_numbered_from_0_under_its_trade()
    {
        var file = XvaRequestFiles.OfficialPv([
            Trade("T-1", ("GBP", 1_000_000m, 10_000_000m), ("USD", -250_000.5m, 12_500_000m)),
            Trade("T-2"),
            Trade("T_3", ("JPY", -350_000m, null))]);

        // A trade with no leg has no row, and a leg with no notional leaves the field empty: nothing is written for it.
        Assert.Equal("id,ccy,pv,notional\nT-1_0,GBP,1000000,10000000\nT-1_1,USD,-250000.5,12500000\nT_3_0,JPY,-350000,\n", file);
    }

    [Fact] // ADR-0016 note 69: a row that would not be read back as it was written is refused, not written
    public void A_trade_id_that_would_break_its_row_is_refused()
    {
        var failure = Assert.Throws<CapabilityFailure>(() => XvaRequestFiles.OfficialPv([Trade("T,1", ("GBP", 1m, 1m))]));

        Assert.Equal("/deals/official_pv.csv cannot hold T,1_0: a comma, a quote or a line break would break its row.", failure.Message);
    }

    [Fact] // ADR-0016 notes 53, 69: the file is written from the legs Price froze: an overwritten PV is its trade's one leg, numbered 0
    public async Task The_file_is_written_from_the_legs_Price_froze()
    {
        var rig = new PricerRig();
        await rig.Run("set E00004 pv -350,000 USD");
        await rig.Run("new leg E00013 eur 250,000");
        var input = await rig.Priced();
        // After's request: Before and After are requests of their own (docs/03), each with its trades.
        var after = input["cases"]![0]!["trades"]!.AsArray().Where(t => t!["after"]!.GetValue<bool>()).Select(t => t!);

        var rows = XvaRequestFiles.OfficialPv(after).Split('\n');

        Assert.Equal("id,ccy,pv,notional", rows[0]);
        // The swap's two legs were taken out by the overwrite, and the leg made in their place is the trade's first.
        Assert.Equal([$"{Swap}_0,USD,-350000,"], rows.Where(r => r.StartsWith(Swap)));
        // The source's two legs stand as they were, adding up to the official PV, and the leg made is the third.
        var usd = rows.Where(r => r.StartsWith(UsdSwap)).ToList();
        Assert.Equal([$"{UsdSwap}_0,USD", $"{UsdSwap}_1,USD", $"{UsdSwap}_2,EUR"], usd.Select(r => r[..(UsdSwap.Length + 6)]));
        Assert.Equal(-768_420m, usd.Take(2).Sum(r => decimal.Parse(r.Split(',')[2], CultureInfo.InvariantCulture)));
        Assert.Equal($"{UsdSwap}_2,EUR,250000,", usd[2]);
        Assert.Equal([$"{Forward}_0,JPY", $"{Forward}_1,USD"], rows.Where(r => r.StartsWith(Forward)).Select(r => r[..(Forward.Length + 6)]));
        // A new trade no leg was made for has no row: no book holds a PV for it.
        Assert.DoesNotContain(rows, r => r.StartsWith("DRAFT-"));
    }
}
