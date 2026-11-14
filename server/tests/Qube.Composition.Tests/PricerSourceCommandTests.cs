using System.Text.Json.Nodes;
using Qube.Composition.Application;
using Qube.Composition.Application.Workspaces;
using Qube.Samples.Portfolio;

namespace Qube.Composition.Tests;

// ADR-0016 note 69, ADR-0003 §6, ADR-0010 §1: the value date and the counterparty on the line, `value date` and
// `counterparty` (`cpty`), with the toolbar's semantics; and the dummy counterparty a workspace takes where none is
// chosen. Every name and figure is synthetic.
public class PricerSourceCommandTests
{
    static readonly Caller Akira = PricerRig.Akira;

    static string Request(LinePreview preview) => preview.Inputs!["request"]!.ToJsonString();

    static async Task<JsonObject> Invoke(PricerRig rig, string capability, JsonObject request, RunOrigin origin = RunOrigin.Pricer)
    {
        request["workspaceId"] = rig.Id;
        var result = await rig.Commands.InvokeAsync(capability, PortfolioContracts.Version, new JsonObject { ["request"] = request }, Akira with { Origin = origin },
            Guid.NewGuid().ToString(), default);
        return result is CommandResult.Done done ? done.Outputs["answer"]!.AsObject()
            : new JsonObject { ["refused"] = string.Join(" ", ((CommandResult.Refused)result).Issues.Select(i => $"{i.Code}: {i.Message}")) };
    }

    [Fact] // ADR-0016 note 69: value date is read without being tried, says the dates it moves between, and Enter moves the source and says what carried
    public async Task Value_date_is_read_without_being_tried_and_enter_moves_the_source()
    {
        var rig = new PricerRig();
        await rig.Run("set CSA-A threshold 5mm");
        var before = rig.State.Revision;

        var preview = rig.Preview("value date 2026-10-31");

        Assert.Equal(("xva.value-date.change", new JsonObject { ["workspaceId"] = rig.Id, ["revision"] = before, ["valueDate"] = "2026-10-31" }.ToJsonString()),
            (preview.Command!.Id, Request(preview)));
        Assert.Equal("Moves the value date from 2026-10-26 to 2026-10-31: reads ABC/LE-01 as of it, and carries your edits onto what it reads.", preview.Would);
        Assert.Equal(before, rig.State.Revision);

        Assert.Equal("Value date moved to 2026-10-31: 1 edit(s) carried, 0 in conflict, 0 lost.", await rig.Run("value date 2026-10-31"));
        Assert.Equal("2026-10-31", rig.Service.View(rig.Id, "akira").Context["valueDate"]!.GetValue<string>());
        Assert.Equal(new DateOnly(2026, 10, 31), PortfolioSourceVersion.Of(rig.State).ValueDate);
        // It took another source version, which no undo takes back (ADR-0016 §11's note 2), and the log names it by the
        // words that type it.
        Assert.StartsWith("CannotUndo: The latest command took another source version", rig.Read("undo"));
        Assert.Equal("value date 2026-10-31", rig.State.Log[^1].Command!.Said);
    }

    [Fact] // ADR-0016 note 69, spine 1: a value date that is none, or is the workspace's, is refused by name before Enter, and by the command
    public async Task A_value_date_that_is_none_or_the_workspaces_is_refused()
    {
        var rig = new PricerRig();
        var before = rig.State.Revision;

        Assert.Equal("NoValueDate: value date takes a date as yyyy-MM-dd, such as 2026-10-31: the workspace is as of 2026-10-26.", rig.Read("value date"));
        Assert.Equal("InvalidDate: 2026-13-01 is not a date as yyyy-MM-dd, such as 2026-10-31.", rig.Read("value date 2026-13-01"));
        Assert.Equal("InvalidDate: 31/10/2026 is not a date as yyyy-MM-dd, such as 2026-10-31.", rig.Read("value date 31/10/2026"));
        Assert.Equal("UnreadWords: value date takes one date: now is not read.", rig.Read("value date 2026-10-31 now"));
        Assert.Equal("SameValueDate: The workspace is already as of 2026-10-26: refresh reads it again.", rig.Read("value date 2026-10-26"));
        Assert.Equal("SameValueDate: The workspace is already as of 2026-10-26: refresh reads it again.",
            (await Invoke(rig, "xva.value-date.change", new JsonObject { ["valueDate"] = "2026-10-26" }))["refused"]!.GetValue<string>());
        Assert.Equal(before, rig.State.Revision);
    }

    [Fact] // ADR-0016 note 69, ADR-0003 §6: counterparty and cpty are one command; its reading names what it drops, and Enter drops it and keeps a new trade's own edit
    public async Task Counterparty_says_what_it_drops_before_enter_and_keeps_a_new_trades_own_edit()
    {
        var rig = new PricerRig();
        Assert.Equal("Moves the workspace to ABC/LE-02 (ABC Securities (synthetic)) as of 2026-10-26; nothing made for ABC/LE-01 is dropped.", rig.Read("counterparty ABC/LE-02"));
        await rig.Run("set DRAFT-1 csa CSA-B");
        await rig.Run("set CSA-A threshold 5mm");
        await rig.Run("set DRAFT-1 notional 7mm");
        var before = rig.State.Revision;

        // Either spelling, in any case: the request names the counterparty as the catalogue does, and the count read.
        var preview = rig.Preview("cpty abc/le-02");

        Assert.Equal(("xva.counterparty.change", new JsonObject { ["workspaceId"] = rig.Id, ["revision"] = before, ["registration"] = "ABC/LE-02", ["drops"] = 2 }.ToJsonString()),
            (preview.Command!.Id, Request(preview)));
        Assert.Equal(rig.Read("counterparty ABC/LE-02"), preview.Would);
        Assert.Equal("Moves the workspace to ABC/LE-02 (ABC Securities (synthetic)) as of 2026-10-26, and drops 2 edits made for ABC/LE-01: DRAFT-1 csa and CSA-A threshold. "
            + "They stay in the log and in any Run that priced them.", preview.Would);
        Assert.Equal(before, rig.State.Revision);

        Assert.Equal("Counterparty changed to ABC/LE-02: 2 edit(s) dropped; 1 carried, 0 in conflict, 0 lost.", await rig.Run("cpty abc/le-02"));
        var view = rig.Service.View(rig.Id, "akira");
        Assert.Equal("ABC/LE-02", view.Registration.Key);
        Assert.Equal(7_000_000m, PortfolioMaterialiser.Trades(rig.State).Single(t => t.TradeId == "DRAFT-1").Notional);
        Assert.All(rig.State.Log.Where(e => e.Seq > before), e => Assert.Equal((RunOrigin.Pricer, "counterparty ABC/LE-02"), (e.Origin, e.Command!.Said)));
        // What it dropped is not brought back by `undo` (ADR-0016 §11, ADR-0003 §6): it stays in the log.
        Assert.StartsWith("CannotUndo: The latest command left a scope with its edits", rig.Read("undo"));
    }

    [Fact] // ADR-0003 §6, spine 6: a change asked with another count than the workspace holds drops nothing, whoever asks
    public async Task A_counterparty_change_with_another_count_drops_nothing()
    {
        var rig = new PricerRig();
        await rig.Run("set CSA-A threshold 5mm");
        var before = rig.State.Revision;

        var stale = await Invoke(rig, "xva.counterparty.change", new JsonObject { ["registration"] = "ABC/LE-02", ["drops"] = 0 }, RunOrigin.Ai);

        Assert.StartsWith("ConfirmationStale: ", stale["refused"]!.GetValue<string>());
        Assert.Equal((before, "ABC/LE-01"), (rig.State.Revision, PortfolioSourceVersion.Of(rig.State).Registration.Key));
        var moved = await Invoke(rig, "xva.counterparty.change", new JsonObject { ["registration"] = "ABC/LE-02", ["drops"] = 1 }, RunOrigin.Ai);
        Assert.Equal((1, 1), (moved["dropped"]!.GetValue<int>(), rig.State.Log.Count(e => e.Kind == EditKind.DropScope)));
        Assert.Equal(RunOrigin.Ai, rig.State.Log.Single(e => e.Kind == EditKind.DropScope).Origin);
    }

    [Fact] // ADR-0016 note 69, CLAUDE.md: a counterparty is its code and its legal entity; a code alone, a key none has, and the workspace's own are refused by name
    public void A_counterparty_is_named_by_its_code_and_legal_entity()
    {
        var rig = new PricerRig();

        Assert.Equal("NoCounterparty: counterparty takes a counterparty's code and legal entity, such as XYZ/LE-03: DUMMY/LE-00, ABC/LE-01, ABC/LE-02 or XYZ/LE-03.", rig.Read("counterparty"));
        Assert.Equal("CodeAlone: ABC is a code, and a counterparty is its code and its legal entity: ABC/LE-01 or ABC/LE-02.", rig.Read("cpty ABC"));
        Assert.Equal("CodeAlone: xyz is a code, and a counterparty is its code and its legal entity: XYZ/LE-03.", rig.Read("cpty xyz"));
        Assert.Equal("UnknownCounterparty: No counterparty ABC/LE-09: the counterparties are DUMMY/LE-00, ABC/LE-01, ABC/LE-02 and XYZ/LE-03.", rig.Read("cpty ABC/LE-09"));
        Assert.Equal("SameCounterparty: The workspace is already on ABC/LE-01.", rig.Read("cpty abc/le-01"));
        Assert.Equal("UnreadWords: counterparty takes one counterparty: now is not read.", rig.Read("cpty ABC/LE-02 now"));
        var refused = rig.Preview("cpty ABC").Refused!;
        Assert.Equal("ABC", refused.Word);
        Assert.Equal(["ABC/LE-01", "ABC/LE-02"], refused.Candidates);
    }

    [Fact] // ADR-0016 note 69: the line offers the counterparties by their key, their code or a word of their name, never the one the workspace is on
    public void The_line_offers_the_other_counterparties()
    {
        var rig = new PricerRig();
        string[] Offered(string line) => [.. rig.Preview(line).Complete.Select(c => $"{c.Value} {c.Detail}")];

        Assert.Equal(["DUMMY/LE-00 Dummy (synthetic): nothing is booked with it", "ABC/LE-02 ABC Securities (synthetic)", "XYZ/LE-03 XYZ Industries (synthetic)"],
            Offered("cpty "));
        Assert.Equal(["ABC/LE-02 ABC Securities (synthetic)"], Offered("counterparty ab"));
        Assert.Equal(["DUMMY/LE-00 Dummy (synthetic): nothing is booked with it"], Offered("cpty dum"));
        Assert.Equal(["XYZ/LE-03 XYZ Industries (synthetic)"], Offered("cpty indus"));
        Assert.Empty(Offered("cpty"));
        Assert.Equal("cpty ".Length, rig.Preview("cpty ab").Complete.Single().Start);
    }

    [Fact] // ADR-0016 notes 61 and 69: a sequence may move the value date, and holds no counterparty change, since it would drop edits no reading showed
    public async Task A_sequence_holds_a_value_date_and_no_counterparty_change()
    {
        var rig = new PricerRig();

        Assert.StartsWith("Saved", await rig.Run("sequence save later value date 2026-10-31 then price"));
        Assert.Equal("NotInASequence: Line 1 (`cpty ABC/LE-02`): cpty drops the edits its reading counts, and a sequence runs a line without showing its reading.",
            rig.Read("sequence save move cpty ABC/LE-02"));
    }

    [Fact] // ADR-0010 §1, docs/03 §2.4, owner 2026-11-14: the dummy counterparty is a prospect's: no booked trade, no CSA, no CDS and no recovery of its own
    public async Task The_dummy_counterparty_has_nothing_booked_and_no_credit_of_its_own()
    {
        var rig = new PricerRig(PortfolioCatalog.DummyKey);
        var view = rig.Service.View(rig.Id, "akira");

        Assert.True(view.Registration.Dummy);
        Assert.Equal(("Dummy (synthetic)", "MA-000", "NS-000", true, 0), (view.Registration.Name, view.Agreement.MaId, view.Agreement.NettingSetId, view.Agreement.Enforceable, view.Csas.Count));
        Assert.Equal(view.NewTrades, view.Trades);
        // A new trade with it is uncollateralised: the agreement's sentinel and the configured curve (docs/03 §2.4).
        Assert.All(PortfolioMaterialiser.Trades(rig.State), t => Assert.Equal(("nocsa_MA-000", "jpy_ois"), (t.Csa, t.Curve)));
        Assert.Null(view.Credit.Cds);
        Assert.Null(view.Credit.CvaRecovery);
        Assert.Equal("No CDS for DUMMY/LE-00: choose a single name or a proxy on the Credit tab, or with cds.", view.Credit.Missing);
        Assert.Contains("No CDS for DUMMY/LE-00", rig.Read("price"));

        // The size asked for is kept: a move to a booked counterparty reads its trades as a workspace made on it does.
        await rig.Run("cpty ABC/LE-01");
        var moved = rig.Service.View(rig.Id, "akira");
        var booked = new PricerRig();
        var made = booked.Service.View(booked.Id, "akira");
        Assert.Equal((false, made.Trades - made.NewTrades), (moved.Registration.Dummy, moved.Trades - moved.NewTrades));
        Assert.True(moved.Trades > moved.NewTrades);
    }
}
