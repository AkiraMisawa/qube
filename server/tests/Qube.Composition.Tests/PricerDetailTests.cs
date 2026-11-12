using System.Text.Json.Nodes;
using Qube.Composition.Application;
using Qube.Composition.Application.Workspaces;
using Qube.Samples.Portfolio;

namespace Qube.Composition.Tests;

/// <summary>
/// The pricer's fields beyond its grids (ADR-0016 notes 51 to 53): a CSA's general and per-side terms and its four
/// templates, a trade's identity, agreement, dates and model, and what Price freezes of them. Every value is synthetic.
/// </summary>
public class PricerDetailTests
{
    [Fact] // ADR-0016 note 51: a CSA's general terms are the source's, read by their words, and no command sets them
    public void A_CSAs_general_terms_are_the_sources_and_no_command_sets_them()
    {
        var rig = new PricerRig();

        Assert.Equal("ABC/LE-01 NS-001 ABC-LE01-VM-JPY on on empty",
            rig.Written(PortfolioTables.Csas, "CSA-A", "counterparty", "nettingSet", "agreementName", "enforceable", "main", "userDefined"));
        Assert.Equal("ABC-LE01-VM-USD off", rig.Written(PortfolioTables.Csas, "CSA-B", "agreementName", "main"));
        Assert.Equal(["CSA-B"], rig.Keys("csas where main=off"));
        Assert.Equal(["CSA-A", "CSA-B"], rig.Keys("csas where netting set=NS-001"));
        Assert.StartsWith("NotEditable: enforceable is not a field a command sets", rig.Read("set CSA-A enforceable off"));
        Assert.StartsWith("NotEditable: agreement name is not a field a command sets", rig.Read("set CSA-A agreement name X"));
    }

    static readonly string[] CounterpartySide =
        ["counterpartyPosts", "counterpartyVmCap", "counterpartyVmCapCurrency", "counterpartyIndependentAmount", "counterpartyRounding", "counterpartyDeliverRounding", "counterpartyReturnRounding"];

    static readonly string[] SelfSide = ["selfPosts", "selfVmCap", "selfVmCapCurrency", "selfIndependentAmount", "selfRounding", "selfDeliverRounding", "selfReturnRounding"];

    [Fact] // ADR-0016 note 51: what a side posts is read from the source, said with its side, and set as any field is
    public async Task A_sides_terms_are_said_with_the_side_and_set()
    {
        var rig = new PricerRig();
        Assert.Equal("on 25mm USD 2mm 10k up down", rig.Written(PortfolioTables.Csas, "CSA-B", CounterpartySide));
        Assert.Equal("on empty USD 0 10k up down", rig.Written(PortfolioTables.Csas, "CSA-B", SelfSide));

        Assert.Equal("Set CSA-B self vm cap to 5mm.", await rig.Run("set CSA-B self vm cap 5mm"));
        await rig.Run("set CSA-B cpty posts=off cpty rounding=50k counterparty delivery rounding=nearest self vm cap ccy=JPY");

        Assert.Equal("off 25mm USD 2mm 50k nearest down", rig.Written(PortfolioTables.Csas, "CSA-B", CounterpartySide));
        Assert.Equal("on 5mm JPY 0 10k up down", rig.Written(PortfolioTables.Csas, "CSA-B", SelfSide));
        Assert.Equal(["CSA-B"], rig.Keys("csas where cpty posts=off"));
        // The other CSA is as the source holds it.
        Assert.Equal("on empty JPY 0 1mm up down", rig.Written(PortfolioTables.Csas, "CSA-A", CounterpartySide));
    }

    [Fact] // ADR-0016 note 51: a word the two sides share names neither alone, and `cpty` says the counterparty's side of the grid's fields too
    public async Task A_word_two_sides_share_is_refused_alone_and_cpty_says_the_counterpartys()
    {
        var rig = new PricerRig();

        Assert.Equal("AmbiguousField: posts could be counterparty posts or self posts of csas.", rig.Read("set CSA-B posts off"));
        Assert.Equal("AmbiguousField: vm cap could be counterparty vm cap or self vm cap of csas.", rig.Read("set CSA-B vm cap 5mm"));

        await rig.Run("set CSA-B cpty threshold 2mm");
        await rig.Run("set CSA-B cpty mta=100k cpty currency=JPY");
        Assert.Equal("2mm 100k JPY", rig.Written(PortfolioTables.Csas, "CSA-B", "counterpartyThreshold", "counterpartyMta", "counterpartyCurrency"));
    }

    [Theory] // ADR-0016 note 51: a side's term is checked by its form, as it is typed
    [InlineData("set CSA-B self vm cap -1mm", "InvalidValue: CSA-B self vm cap is an amount of 0 or more; -1mm is not.")]
    [InlineData("set CSA-B self rounding empty", "InvalidValue: CSA-B self rounding is an amount: 5mm, 250k, 1bn, or a bare number of millions; empty is not.")]
    [InlineData("set CSA-B self delivery rounding sideways", "InvalidValue: CSA-B self delivery rounding is one of up, down, nearest; sideways is not.")]
    [InlineData("set CSA-B self posts maybe", "InvalidValue: CSA-B self posts is on or off; maybe is not.")]
    [InlineData("set CSA-B self vm cap currency XXX", "InvalidValue: CSA-B self vm cap currency is one of GBP, JPY, USD, EUR; XXX is not.")]
    public void A_sides_term_is_checked_by_its_form(string line, string refused) => Assert.Equal(refused, new PricerRig().Read(line));

    [Theory] // ADR-0016 note 51: and however it arrives, a typed cell or the API's value included
    [InlineData("selfPosts", "\"yes\"", "self posts is true or false.")]
    [InlineData("selfVmCap", "-1", "self vm cap is an amount of 0 or more, or empty.")]
    [InlineData("selfRounding", "null", "self rounding is an amount of 0 or more.")]
    [InlineData("selfDeliverRounding", "\"sideways\"", "self delivery rounding is one of up, down, nearest.")]
    [InlineData("counterpartyVmCapCurrency", "\"XXX\"", "counterparty vm cap currency is one of GBP, JPY, USD, EUR.")]
    public void A_sides_term_is_checked_however_it_arrives(string field, string json, string problem)
    {
        var rig = new PricerRig();
        var refused = Assert.Throws<WorkspaceRefused>(() => rig.Service.Edit(rig.Id, rig.State.Revision, "akira", "csa:CSA-B", field, JsonNode.Parse(json)));
        Assert.Equal(("InvalidValue", problem), (refused.Code, refused.Message));
        // And a general term is the source's, whoever asks.
        Assert.Equal("NotEditable", Assert.Throws<WorkspaceRefused>(() => rig.Service.Edit(rig.Id, rig.State.Revision, "akira", "csa:CSA-B", "enforceable", false)).Code);
    }

    static string[] Listed(PricerRig rig) => [.. rig.Service.Changes(rig.Id, "akira").Common.Select(c => $"{c.Title}: {c.OriginalText} to {c.Text}{(c.Editable ? "" : " (not typed)")}")];

    [Fact] // ADR-0016 note 51: an edit of one is the row's edit as any other: listed in Changes with the source's value, retyped there, reset and undone
    public async Task An_edit_of_a_sides_term_is_listed_retyped_reset_and_undone()
    {
        var rig = new PricerRig();
        await rig.Run("set CSA-B self vm cap 5mm");
        await rig.Run("set CSA-B cpty posts off");
        Assert.Equal(["CSA-B self vm cap: empty to 5mm", "CSA-B counterparty posts: on to off"], Listed(rig));

        // The Changes tab types a value over the change, read and checked as the cell's is.
        rig.Service.SetText(rig.Id, rig.State.Revision, "akira", "csa:CSA-B", "selfVmCap", "7.5mm");
        Assert.Equal("7.5mm", rig.Written(PortfolioTables.Csas, "CSA-B", "selfVmCap"));

        Assert.Equal("Reset CSA-B self vm cap.", await rig.Run("reset CSA-B self vm cap"));
        Assert.Equal(["CSA-B counterparty posts: on to off"], Listed(rig));
        await rig.Run("undo");
        Assert.Equal("7.5mm", rig.Written(PortfolioTables.Csas, "CSA-B", "selfVmCap"));

        // A whole row's reset takes the fields beyond the grid with the grid's.
        await rig.Run("set CSA-B threshold 2mm");
        await rig.Run("reset CSA-B");
        Assert.Empty(Listed(rig));
        Assert.Equal("on empty 1mm", rig.Written(PortfolioTables.Csas, "CSA-B", "counterpartyPosts", "selfVmCap", "counterpartyThreshold"));
    }

    [Fact] // ADR-0016 note 51, ADR-0010 §3: a case varies a field beyond the grid as it varies a cell, and the Base keeps its own
    public async Task A_case_varies_a_sides_term()
    {
        var rig = new PricerRig();

        await rig.Run("vary CSA-B self vm cap 5mm, 10mm");

        var cases = rig.Service.Changes(rig.Id, "akira").Cases;
        Assert.Equal(["CSA-B self vm cap: empty to 5mm", "CSA-B self vm cap: empty to 10mm"], cases.Select(c => $"{c.Changes.Single().Title}: {c.Changes.Single().OriginalText} to {c.Changes.Single().Text}"));
        Assert.Equal("empty", rig.Written(PortfolioTables.Csas, "CSA-B", "selfVmCap"));
    }

    static readonly string[] Posting =
        ["currency", "counterpartyCurrency", "selfCurrency", "exchange", "oneWay", "counterpartyPosts", "selfPosts", "counterpartyThreshold", "counterpartyMta", "selfThreshold", "selfMta"];

    [Theory] // ADR-0016 note 52: each of the CSA's four templates writes who posts for the currency given, and thresholds and MTAs of 0
    [InlineData("2way", "EUR EUR EUR on off on on 0 0 0 0")]
    [InlineData("1way-self", "EUR EUR EUR on on off on 0 0 0 0")]
    [InlineData("1way-cpty", "EUR EUR EUR on on on off 0 0 0 0")]
    [InlineData("none", "EUR EUR EUR off off off off 0 0 0 0")]
    public async Task Each_CSA_template_writes_who_posts_for_the_currency_given(string template, string written)
    {
        var rig = new PricerRig();

        Assert.Equal($"Set CSA-B as {template} currency=EUR.", await rig.Run($"set CSA-B as {template} eur"));

        Assert.Equal(written, rig.Written(PortfolioTables.Csas, "CSA-B", Posting));
        // What a template does not write stays as it was: curves, frequency, VM caps, independent amounts and rounding.
        Assert.Equal("usd_sofr usd_funding daily 25mm USD 2mm 10k up down",
            rig.Written(PortfolioTables.Csas, "CSA-B", ["curve", "fvaCurve", "frequency", .. CounterpartySide[1..]]));
        await rig.Run("undo");
        Assert.Equal("USD USD USD on off on on 1mm 500k 0 250k", rig.Written(PortfolioTables.Csas, "CSA-B", Posting));
    }

    [Fact] // ADR-0016 notes 50, 52: a template is said by its words, its currency by name or alone, and one it needs is asked for
    public async Task A_CSA_template_takes_its_currency_by_name_or_alone()
    {
        var rig = new PricerRig();

        Assert.Equal("currency: USD to JPY; counterparty currency: USD to JPY; self currency: USD to JPY; one-way: off to on; self posts: on to off; threshold: 1mm to 0; "
            + "mta: 500k to 0; self mta: 250k to 0; 3 values already held", rig.Read("set CSA-B as 1way-cpty jpy"));
        Assert.Equal("Set CSA-A and CSA-B as none currency=USD.", await rig.Run("set csas all as no collateral ccy=usd"));
        Assert.Equal("MissingTerm: 2way still needs currency: type it after it.", rig.Read("set CSA-B as 2way"));
        Assert.StartsWith("UnknownTemplate: csas has no template 3way; its templates are 2way, 1way-self, 1way-cpty and none.", rig.Read("set CSA-B as 3way usd"));
        // A trade has no template: `as` is refused there by name.
        Assert.StartsWith("NoTemplates: trades has no template", rig.Read("set DRAFT-1 as 2way usd"));
    }

    [Fact] // ADR-0016 note 53 (owner, 2026-11-12): each CSA Price freezes carries every field beyond the grid as the case holds it, an empty one as empty
    public async Task The_accepted_input_carries_each_CSAs_details_as_set()
    {
        var rig = new PricerRig();
        await rig.Run("set CSA-B self vm cap 5mm");
        await rig.Run("set CSA-B cpty posts=off cpty delivery rounding=nearest");
        await rig.Run("vary CSA-B self vm cap 10mm");

        var input = await rig.Priced();

        JsonObject Details(int @case, string id) => input["cases"]![@case]!["csas"]!.AsArray().Single(c => c!["csaId"]!.GetValue<string>() == id)!["details"]!.AsObject();
        Assert.Equal("""
            {"counterparty":"ABC/LE-01","nettingSet":"NS-001","agreementName":"ABC-LE01-VM-USD","enforceable":true,"main":false,"userDefined":"legacy",
            "counterpartyPosts":false,"counterpartyVmCap":25000000,"counterpartyVmCapCurrency":"USD","counterpartyIndependentAmount":2000000,"counterpartyRounding":10000,
            "counterpartyDeliverRounding":"nearest","counterpartyReturnRounding":"down",
            "selfPosts":true,"selfVmCap":5000000,"selfVmCapCurrency":"USD","selfIndependentAmount":0,"selfRounding":10000,"selfDeliverRounding":"up","selfReturnRounding":"down"}
            """.ReplaceLineEndings("").Replace("            ", ""), Details(0, "CSA-B").ToJsonString());
        // The case's own value, and the rest as the Base holds it.
        Assert.Equal((10_000_000m, false), (Details(1, "CSA-B")["selfVmCap"]!.GetValue<decimal>(), Details(1, "CSA-B")["counterpartyPosts"]!.GetValue<bool>()));
        // An empty field is said as empty, never left out.
        Assert.True(Details(0, "CSA-A").ContainsKey("userDefined") && Details(0, "CSA-A")["userDefined"] is null);
        Assert.True(Details(0, "CSA-A").ContainsKey("counterpartyVmCap") && Details(0, "CSA-A")["counterpartyVmCap"] is null);
    }

    const string Swap = "ABC-LE-01-E00004", Forward = "ABC-LE-01-E00003";

    static readonly string[] Identity = ["requestedId", "tradeType", "sourceProductType", "simmProductClass", "userDefined", "masterAgreement", "nettingSet"];

    static readonly string[] BreakAndModel =
        ["breakDate", "breakMandatory", "modelId", "pricerId", "interpolationId", "domesticCurrency", "irModel", "irBasis", "equityModel", "creditModel", "inflationModel", "unmodelledMethod"];

    [Fact] // ADR-0016 note 51: a trade's identity and its agreement are the source's, read by their words, and no command sets them
    public void A_trades_identity_and_agreement_are_the_sources_and_no_command_sets_them()
    {
        var rig = new PricerRig();

        Assert.Equal("REQ-ABC-LE-01-E00004 Swap IR-SWAP-FIXFLT RatesFX empty MA-001 NS-001", rig.Written(PortfolioTables.Trades, Swap, Identity));
        Assert.Equal("REQ-ABC-LE-01-E00003 Forward FX-FWD RatesFX empty MA-001 NS-001", rig.Written(PortfolioTables.Trades, Forward, Identity));
        // A new trade is priced under the agreement it would join.
        Assert.Equal("REQ-DRAFT-1 MA-001 NS-001", rig.Written(PortfolioTables.Trades, "DRAFT-1", "requestedId", "masterAgreement", "nettingSet"));
        // The trade type is read as a condition: the forward is among the rows it names, and the swap is not.
        var forwards = rig.Keys("trades where trade type=Forward");
        Assert.Contains(Forward, forwards);
        Assert.DoesNotContain(Swap, forwards);
        Assert.Equal(rig.Keys("trades where product=FXForward"), forwards);
        Assert.Contains(Forward, rig.Keys("trades where source product=FX-FWD"));
        Assert.StartsWith("NotEditable: trade type is not a field a command sets", rig.Read("set E00004 trade type Cap"));
        Assert.StartsWith("NotEditable: master agreement is not a field a command sets", rig.Read("set E00004 ma MA-009"));
    }

    [Fact] // ADR-0016 note 51: a trade's break and its model are read from the source and set by their words
    public async Task A_trades_break_and_model_are_set()
    {
        var rig = new PricerRig();
        Assert.Equal("empty off M-IR-01 P-SWAP-01 I-LIN-01 JPY HW1F deterministic empty empty empty mtm-only", rig.Written(PortfolioTables.Trades, Swap, BreakAndModel));
        Assert.Equal("M-FX-01 P-FWD-01", rig.Written(PortfolioTables.Trades, Forward, "modelId", "pricerId"));

        Assert.Equal("Set ABC-LE-01-E00004 break date to 2028-06-30.", await rig.Run("set E00004 break 2028-06-30"));
        await rig.Run("set E00004 mandatory break=on ir model=hw2f ir basis=stochastic model=M-IR-02 unmodelled=addon credit model=cir domestic ccy=usd");

        Assert.Equal("2028-06-30 on M-IR-02 P-SWAP-01 I-LIN-01 USD HW2F stochastic empty cir empty addon", rig.Written(PortfolioTables.Trades, Swap, BreakAndModel));
        Assert.Equal([Swap], rig.Keys("trades where ir model=HW2F"));
        Assert.Equal(["ABC-LE-01-E00004 break date: empty to 2028-06-30", "ABC-LE-01-E00004 mandatory break: off to on"], Listed(rig)[..2]);
        // Empty again where the field may be, and the source's by reset.
        await rig.Run("set E00004 break empty");
        await rig.Run("reset E00004 ir model");
        Assert.Equal("empty HW1F", rig.Written(PortfolioTables.Trades, Swap, "breakDate", "irModel"));
    }

    [Theory] // ADR-0016 note 51: each is checked by its form as it is typed
    [InlineData("set E00004 break someday", "InvalidValue: ABC-LE-01-E00004 break date is a date, yyyy-mm-dd, or empty; someday is not.")]
    [InlineData("set E00004 ir model vasicek", "InvalidValue: ABC-LE-01-E00004 ir model is one of HW1F, HW2F, LMM; vasicek is not.")]
    [InlineData("set E00004 ir model empty", "InvalidValue: ABC-LE-01-E00004 ir model is one of HW1F, HW2F, LMM; empty is not.")]
    [InlineData("set E00004 mandatory break perhaps", "InvalidValue: ABC-LE-01-E00004 mandatory break is on or off; perhaps is not.")]
    public void A_trades_break_and_model_are_checked_by_their_forms(string line, string refused) => Assert.Equal(refused, new PricerRig().Read(line));

    [Theory] // ADR-0016 note 51: and however it arrives
    [InlineData("breakDate", "\"soon\"", "break date is a date, yyyy-mm-dd, or empty.")]
    [InlineData("breakMandatory", "null", "mandatory break is true or false.")]
    [InlineData("modelId", "\"two words\"", "model id is an identifier of one word.")]
    [InlineData("pricerId", "null", "pricer id is an identifier of one word.")]
    [InlineData("irModel", "\"vasicek\"", "ir model is one of HW1F, HW2F, LMM.")]
    [InlineData("equityModel", "\"vasicek\"", "equity model is one of black-scholes, local-vol, heston, or empty.")]
    public void A_trades_break_and_model_are_checked_however_they_arrive(string field, string json, string problem)
    {
        var rig = new PricerRig();
        var refused = Assert.Throws<WorkspaceRefused>(() => rig.Service.Edit(rig.Id, rig.State.Revision, "akira", $"trade:{Swap}", field, JsonNode.Parse(json)));
        Assert.Equal(("InvalidValue", problem), (refused.Code, refused.Message));
        Assert.Equal("NotEditable", Assert.Throws<WorkspaceRefused>(() => rig.Service.Edit(rig.Id, rig.State.Revision, "akira", $"trade:{Swap}", "tradeType", "Cap")).Code);
    }

    [Fact] // ADR-0016 note 53 (owner, 2026-11-12): each trade Price freezes carries every field beyond the grids as the case holds it, an empty one as empty
    public async Task The_accepted_input_carries_each_trades_details_as_set()
    {
        var rig = new PricerRig();
        await rig.Run("set E00004 break=2028-06-30 mandatory break=on ir model=hw2f credit model=cir");
        await rig.Run("vary ABC-LE-01-E00004 ir model lmm");

        var input = await rig.Priced();

        JsonObject Details(int @case, string id) => input["cases"]![@case]!["trades"]!.AsArray().Single(t => t!["tradeId"]!.GetValue<string>() == id)!["details"]!.AsObject();
        Assert.Equal("""
            {"requestedId":"REQ-ABC-LE-01-E00004","tradeType":"Swap","sourceProductType":"IR-SWAP-FIXFLT","simmProductClass":"RatesFX","userDefined":null,
            "masterAgreement":"MA-001","nettingSet":"NS-001","breakDate":"2028-06-30","breakMandatory":true,
            "modelId":"M-IR-01","pricerId":"P-SWAP-01","interpolationId":"I-LIN-01","domesticCurrency":"JPY","irModel":"HW2F","irBasis":"deterministic",
            "equityModel":null,"creditModel":"cir","inflationModel":null,"unmodelledMethod":"mtm-only"}
            """.ReplaceLineEndings("").Replace("            ", ""), Details(0, Swap).ToJsonString());
        // The case's own value, and the rest as the Base holds it.
        Assert.Equal(("LMM", "2028-06-30"), (Details(1, Swap)["irModel"]!.GetValue<string>(), Details(1, Swap)["breakDate"]!.GetValue<string>()));
        // A trade no one touched carries the source's, a new trade among them.
        Assert.Equal(("FX-FWD", "M-FX-01"), (Details(0, Forward)["sourceProductType"]!.GetValue<string>(), Details(0, Forward)["modelId"]!.GetValue<string>()));
        Assert.True(Details(0, "DRAFT-1").ContainsKey("breakDate") && Details(0, "DRAFT-1")["breakDate"] is null);
    }

    [Fact] // ADR-0016 note 51, ADR-0003: an edit of a field beyond the grids is carried when the source is acquired again, as any edit is
    public async Task An_edit_of_a_field_beyond_the_grids_survives_a_reacquisition()
    {
        var rig = new PricerRig();
        await rig.Run("set E00004 ir model hw2f");
        await rig.Run("set CSA-B self vm cap 5mm");

        var (_, carried) = rig.Service.ChangeValueDate(rig.Id, rig.State.Revision, "akira", PricerRig.Day1.AddDays(1));

        Assert.Equal(new CarrySummary(2, 0, 0), carried);
        Assert.Equal("HW2F", rig.Written(PortfolioTables.Trades, Swap, "irModel"));
        Assert.Equal("5mm", rig.Written(PortfolioTables.Csas, "CSA-B", "selfVmCap"));
        Assert.Equal(["ABC-LE-01-E00004 ir model: HW1F to HW2F", "CSA-B self vm cap: empty to 5mm"], Listed(rig));
    }

    [Fact] // ADR-0016 note 51, D07: a new trade's model is its own content, which stays with it through a counterparty change; a CSA's term is the counterparty's
    public async Task A_new_trades_model_stays_with_it_through_a_counterparty_change()
    {
        var rig = new PricerRig();
        await rig.Run("set DRAFT-1 ir model=lmm break=2029-01-15");
        await rig.Run("set CSA-B self vm cap 5mm");

        rig.Service.ChangeCounterparty(rig.Id, rig.State.Revision, "akira", "ABC/LE-02", 1);

        Assert.Equal("LMM 2029-01-15 MA-002", rig.Written(PortfolioTables.Trades, "DRAFT-1", "irModel", "breakDate", "masterAgreement"));
        Assert.Equal(["DRAFT-1 ir model: HW1F to LMM", "DRAFT-1 break date: empty to 2029-01-15"], Listed(rig));
    }

    [Fact] // ADR-0016 note 51: every field of the trades and of the CSAs has a group, B and A alone having none, and the AI is told each
    public async Task Every_field_of_the_trades_and_the_CSAs_has_a_group()
    {
        var described = await new PricerRig().Described();

        string[] Groups(string table) => [.. described["fields"]!.AsArray().Where(f => f!["table"]!.GetValue<string>() == table)
            .GroupBy(f => f!["group"]?.GetValue<string>()).Select(g => $"{g.Key ?? "none"}: {string.Join(", ", g.Select(f => f!["name"]!.GetValue<string>()))}")];
        Assert.Equal(
        [
            "Identity: tradeId, product, requestedId, tradeType, sourceProductType, simmProductClass, userDefined",
            "Terms: currency, notional, fixedRate, schedule, lastFixing",
            "Dates: maturity, breakDate, breakMandatory",
            "Valuation: officialPv, pv, independentAmount, xva.margin, xva.pvAdj",
            "Agreement: csa, curve, imR, imP, masterAgreement, nettingSet",
            "none: before, after",
            "Model: modelId, pricerId, interpolationId, domesticCurrency, irModel, irBasis, equityModel, creditModel, inflationModel, unmodelledMethod",
        ], Groups(PortfolioTables.Trades));
        Assert.Equal(
        [
            "General: csaId, type, counterparty, nettingSet, agreementName, enforceable, main, userDefined",
            "Counterparty: counterpartyThreshold, counterpartyMta, counterpartyCurrency, counterpartyPosts, counterpartyVmCap, counterpartyVmCapCurrency, "
                + "counterpartyIndependentAmount, counterpartyRounding, counterpartyDeliverRounding, counterpartyReturnRounding",
            "Self: selfThreshold, selfMta, selfCurrency, selfPosts, selfVmCap, selfVmCapCurrency, selfIndependentAmount, selfRounding, selfDeliverRounding, selfReturnRounding",
            "Terms: currency, curve, fvaCurve, frequency, exchange, oneWay",
        ], Groups(PortfolioTables.Csas));
        // The CSA's templates are described with the currency each takes.
        Assert.Equal(["2way", "1way-self", "1way-cpty", "none"], described["templates"]!.AsArray().Where(t => t!["table"]!.GetValue<string>() == PortfolioTables.Csas).Select(t => t!["name"]!.GetValue<string>()));
        Assert.All(described["templateTerms"]!.AsArray(), t => Assert.Equal(("currency", true, true), (t!["name"]!.GetValue<string>(), t["required"]!.GetValue<bool>(), t["typedAlone"]!.GetValue<bool>())));
    }
}
