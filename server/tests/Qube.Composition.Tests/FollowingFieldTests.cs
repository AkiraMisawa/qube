using System.Text.Json.Nodes;
using Qube.Composition.Application;
using Qube.Composition.Application.Tables;
using Qube.Composition.Application.Workspaces;

namespace Qube.Composition.Tests;

/// <summary>
/// A field that follows other fields of its row (ADR-0016 note 66): no command sets it, a line that tries is told which
/// fields to set, and a description says which it follows. The window here follows the wind for the test's sake alone.
/// </summary>
public class FollowingFieldTests
{
    static readonly Caller Ana = new("ana", RunOrigin.Pricer);

    static TableDescriptor Readings(string field, params string[] from) =>
        Stations.ReadingsTable with { Fields = [.. Stations.ReadingsTable.Fields.Select(f => f.Name == field ? f with { From = from } : f)] };

    sealed class Rig
    {
        public WorkspaceService Workspaces { get; } = Stations.Service();
        public TableRegister Register { get; }
        public CommandService Commands { get; }
        public CommandLines Lines { get; }
        public string Id { get; }

        public Rig(TableDescriptor readings)
        {
            Register = new([new Stations.Catalog(readings: readings)]);
            var tables = new TableService(Register, Workspaces);
            var registry = new CompositionRegistry([new TablesModule(() => tables), new WorkspaceCommandsModule(() => Workspaces)]);
            Commands = new CommandService(registry, new InMemoryDataStore());
            Lines = new CommandLines(registry, Workspaces, Register, []);
            Id = Stations.Board(Workspaces).WorkspaceId;
        }

        public string Read(string line) => Lines.Preview(line, Id, null, Ana, null) is var preview && preview.Refused is { } refused ? $"{refused.Code}: {refused.Sentence}" : preview.Would!;
    }

    [Theory] // ADR-0016 note 66: a field that follows others is refused with the fields to set in its place, one or several
    [InlineData(new[] { "windSpeed", "windDirection" }, "NotEditable: window is not a field a command sets: it follows wind speed and wind direction. Set those.")]
    [InlineData(new[] { "note" }, "NotEditable: window is not a field a command sets: it follows note. Set that.")]
    public void A_field_that_follows_others_is_refused_with_the_fields_to_set(string[] from, string refusal)
    {
        var rig = new Rig(Readings("window", from));

        Assert.Equal(refusal, rig.Read("set R1 window 3M"));
        // A field that follows nothing is refused as before, with every field a command sets.
        Assert.StartsWith("NotEditable: station is not a field a command sets; readings takes volume,", rig.Read("set R1 station south"));
    }

    [Fact] // ADR-0016 note 66: a reset of a field that follows others, which holds no edit of its own, names the fields whose edits it shows
    public void A_reset_of_a_field_that_follows_others_names_the_fields_to_reset()
    {
        var rig = new Rig(Readings("window", "windSpeed", "windDirection"));

        Assert.Equal("Nothing would change: R1 has no edit of window: it follows wind speed and wind direction, and is reset through those.", rig.Read("reset R1 window"));
        // A field that follows nothing is answered as before.
        Assert.Equal("Nothing would change: R1 has no edit of volume.", rig.Read("reset R1 volume"));
    }

    [Fact] // ADR-0016 note 66, §7: tables.describe says which fields a field follows, and nothing of one that follows none
    public async Task The_description_says_which_fields_a_field_follows()
    {
        var rig = new Rig(Readings("window", "windSpeed", "windDirection"));

        var result = await rig.Commands.InvokeAsync("tables.describe", TableContracts.Version, new JsonObject { ["request"] = new JsonObject { ["workspaceId"] = rig.Id } }, Ana,
            Guid.NewGuid().ToString(), default);

        var fields = ((CommandResult.Done)result).Outputs.Values.Single()!["fields"]!.AsArray().Where(f => f!["table"]!.GetValue<string>() == Stations.Readings).ToList();
        var window = fields.Single(f => f!["name"]!.GetValue<string>() == "window")!;
        Assert.Equal((false, "windSpeed, windDirection"), (window["editable"]!.GetValue<bool>(), string.Join(", ", window["from"]!.AsArray().Select(n => n!.GetValue<string>()))));
        Assert.All(fields.Where(f => f != window), f => Assert.Null(f!["from"]));
    }

    [Theory] // ADR-0016 note 66: a field that follows a field its table has not, follows none, or is set by a command too, fails when the tables are read, by name
    [InlineData("window", new[] { "height" }, "Table weather.readings's field window follows height, which the table has not.")]
    [InlineData("window", new string[0], "Table weather.readings's field window follows no field.")]
    [InlineData("volume", new[] { "unit", "note" }, "Table weather.readings's field volume follows unit, note and is set by a command: it is set through them alone.")]
    public void A_field_that_cannot_follow_what_it_names_fails_at_start(string field, string[] from, string failure)
    {
        var rig = new Rig(Readings(field, from));

        Assert.Equal(failure, Assert.Throws<InvalidOperationException>(() => rig.Register.Of(rig.Workspaces.Find(rig.Id)!)).Message);
    }
}
