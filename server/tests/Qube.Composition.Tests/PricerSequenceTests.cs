using System.Text.Json.Nodes;
using Qube.Composition.Application;
using Qube.Composition.Application.Workspaces;
using Qube.Samples.Portfolio;

namespace Qube.Composition.Tests;

/// <summary>
/// A saved sequence of edits on the pricer (ADR-0016 §8, notes 60 to 62): the owner's example, "clear After, put the
/// USD trades back, price", saved on one counterparty and run on another. Every name and figure is synthetic.
/// </summary>
public sealed class PricerSequenceTests
{
    const string UsdOnly = "sequence save usd-only after none existing then set existing where currency = USD after on then price";

    [Fact] // ADR-0016 §8: the selectors are kept as written, so the sequence serves another counterparty, and it ends by pricing the revision it left
    public async Task A_sequence_saved_on_one_counterparty_runs_on_another_and_prices_what_its_edits_left()
    {
        var rig = new PricerRig();
        Assert.Equal("Saved 3 lines as the sequence usd-only, for you alone.", await rig.Run(UsdOnly));
        var first = rig.State.Revision;

        // Another counterparty's workspace: its own trades, in other currencies.
        var other = rig.Service.Create("akira", "ABC/LE-02", PricerRig.Day1, 40);
        var existing = rig.Register.Of(other).Single(t => t.Descriptor.TableId == PortfolioTables.Trades).Table.Rows(other)
            .Where(r => r.Key.StartsWith("ABC-LE-02-E", StringComparison.Ordinal)).ToDictionary(r => r.Key, r => r.Value("currency")!.GetValue<string>());
        Assert.Contains("USD", existing.Values);
        Assert.Contains("JPY", existing.Values);

        var result = await rig.Commands.InvokeAsync(SequenceRun.Id, SequenceRun.Version,
            new JsonObject { ["request"] = new JsonObject { ["workspaceId"] = other.WorkspaceId, ["sequence"] = "usd-only" } }, PricerRig.Akira, "run-1", default);

        var ran = Assert.IsType<CommandResult.Done>(result).Outputs["answer"]!;
        var steps = ran["steps"]!.AsArray();
        Assert.Equal(["after none existing [entity.set]", "set existing where currency = USD after on [entity.set]", "price [xva.workspace.price]"],
            steps.Select(s => $"{s!["line"]} [{s["command"]}]"));
        var left = steps[1]!["revision"]!.GetValue<long>();
        Assert.True(left > other.Revision);
        // The last line's answer names the Run it started, as `price` typed alone does.
        var runId = JsonNode.Parse(steps[2]!["answer"]!.GetValue<string>())!["runId"]!.GetValue<string>();
        Assert.Equal($"Ran usd-only: 3 lines, from revision {other.Revision} to {left}. Price was accepted as Run {runId}.", ran["said"]!.GetValue<string>());

        // One Run, of the revision the edits left, from a saved definition (§8: origin workflow).
        var run = rig.Runs.Find(runId)!;
        Assert.Equal((RunOrigin.Workflow, other.WorkspaceId, left), (run.Origin, run.Relations.WorkspaceId, run.Relations.WorkspaceRevision));
        // The input Price froze is that revision's: of this counterparty's existing trades, the USD ones are in After and no other.
        var input = await rig.Accepted(runId);
        Assert.Equal(("ABC/LE-02", left), (input["registration"]!.GetValue<string>(), input["revision"]!.GetValue<long>()));
        var frozen = input["cases"]![0]!["trades"]!.AsArray().ToDictionary(t => t!["tradeId"]!.GetValue<string>(), t => t!["after"]!.GetValue<bool>());
        Assert.All(existing, trade => Assert.Equal(trade.Value == "USD", frozen[trade.Key]));

        // Each edit is a command of its own in that workspace's log, and the workspace the sequence was saved on is as it was.
        var log = CommandHistory.Read(rig.Workspaces.Find(other.WorkspaceId)!.Log).Undo;
        Assert.Equal([RunOrigin.Workflow, RunOrigin.Workflow], log.TakeLast(2).Select(c => c.First.Origin));
        Assert.Equal(first, rig.State.Revision);
    }

    [Fact] // ADR-0016 note 63: the run's reading says the lines, and what the first would do on this counterparty
    public async Task The_runs_reading_says_what_the_first_line_would_do_on_the_workspace_it_is_typed_on()
    {
        var rig = new PricerRig();
        await rig.Run(UsdOnly);

        Assert.Equal("Runs usd-only, of 3 lines, each as its own command, until one is refused: 1. `after none existing` 2. `set existing where currency = USD after on` 3. `price`. "
            + $"Each line after the first is read as it is reached, on what the lines before it leave. Line 1, as the workspace stands: {rig.Read("after none existing")}",
            rig.Read("sequence run usd-only"));
        // What the run did is undone a line at a time: the latest command is the sequence's, for the user.
        await rig.Run("sequence run usd-only");
        Assert.StartsWith("Takes back: ", rig.Read("undo"));
        Assert.Contains("(a saved sequence for you, ", rig.Read("undo"));
    }
}
