using System.Text.Json.Nodes;
using Qube.Composition.Application;
using Qube.Composition.Application.Tables;
using Qube.Composition.Application.Workspaces;

namespace Qube.Composition.Tests;

// ADR-0016 note 58 of 2026-11-12: a table may answer the write of a field on several rows, and the removal of several
// rows, as one, where its rows share what holds the value. The registration is the stations a board has muted, which
// it keeps as one list, with no financial word.
public class RowsWrittenTogetherTests
{
    static string[] Muted(SensorRig rig) => [.. Stations.MutedOn(rig.Board).Order(StringComparer.Ordinal)];

    [Fact] // note 58: rows that share what holds a value are written as one, so no row's change leaves another's out
    public async Task Rows_that_share_what_holds_a_value_are_written_as_one()
    {
        var rig = new SensorRig(muted: true);

        var said = await rig.Run("set stations N1, S1 muted on");

        Assert.Equal("Set muted to on on N1 and S1.", said["said"]!.GetValue<string>());
        Assert.Equal(["N1", "S1"], Muted(rig));
        // One write of the list, and one command: one undo unmutes both.
        await rig.Run("undo");
        Assert.Empty(Muted(rig));
    }

    [Fact] // note 58: the rows written together are those that would change; one that holds the value already is held
    public async Task A_row_that_holds_the_value_already_is_left_as_it_is()
    {
        var rig = new SensorRig(muted: true);
        await rig.Run("set stations N1 muted on");

        var preview = rig.Preview("set stations N1, S1, X1 muted on");

        Assert.Equal("muted: 2 off to on, 1 already on", preview.Would);
        await rig.Run("set stations N1, S1, X1 muted on");
        Assert.Equal(["N1", "S1", "X1"], Muted(rig));
        await rig.Run("set stations N1, X1 muted off");
        Assert.Equal(["S1"], Muted(rig));
    }

    [Fact] // note 58: rows removed together leave what they share in one write of it
    public async Task Rows_removed_together_leave_what_they_share_in_one_write()
    {
        var rig = new SensorRig(muted: true);
        foreach (var id in new[] { "M1", "M2" })
            rig.Workspaces.Create(rig.Id, rig.Board.Revision, "ana", Stations.Scope, $"station:{id}", new JsonObject { ["id"] = id, ["altitude"] = 5m, ["backup"] = null });
        await rig.Run("set stations M1, M2, S1 muted on");

        await rig.Run("remove stations M1, M2");

        Assert.Equal(["S1"], Muted(rig));
        Assert.Equal(["N1", "S1", "X1"], rig.Keys("stations all"));
    }

    [Fact] // note 58: a table that answers no write of several rows is written a row at a time, as before
    public async Task A_field_no_rows_share_is_written_a_row_at_a_time()
    {
        var rig = new SensorRig(muted: true);

        await rig.Run("set stations N1, S1 altitude 300");

        Assert.Equal([300m, 300m], rig.Rows("stations N1, S1").Rows.Select(r => r.Value("altitude")!.GetValue<decimal>()));
        Assert.Equal(2, rig.Board.Overrides.Count);
    }
}
