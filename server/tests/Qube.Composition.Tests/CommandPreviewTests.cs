using System.Text.Json.Nodes;
using Qube.Composition.Application;
using Qube.Composition.Application.Tables;
using Qube.Composition.Application.Workspaces;

namespace Qube.Composition.Tests;

// ADR-0016 §5, §7 and its note 38 of 2026-11-11: the line's reading as a capability, for a caller with no screen. It
// answers what a command would change and the inputs that run it, and executes nothing. The registration is station
// readings, with no financial word.
public class CommandPreviewTests
{
    sealed class Rig
    {
        public WorkspaceService Workspaces { get; } = Stations.Service();
        public CompositionRegistry Registry { get; }
        public CommandService Commands { get; }
        public string Id { get; }

        public Rig()
        {
            var register = new TableRegister([new Stations.Catalog()]);
            var tables = new TableService(register, Workspaces);
            CommandLines? lines = null;
            Registry = new CompositionRegistry([new TablesModule(() => tables), new WorkspaceCommandsModule(() => Workspaces),
                new CommandLinesModule(() => lines ?? throw new InvalidOperationException("The lines are read once the registry is built."))]);
            lines = new CommandLines(Registry, Workspaces, register, [new Stations.ShortForms()]);
            Commands = new CommandService(Registry, new InMemoryDataStore());
            Id = Stations.Board(Workspaces).WorkspaceId;
        }

        public WorkspaceState Board => Workspaces.Find(Id)!;

        public Task<CommandResult> Invoke(string capability, JsonObject inputs, string user = "ana") =>
            Commands.InvokeAsync(capability, "1.0.0", inputs, new Caller(user, RunOrigin.Ai), Guid.NewGuid().ToString(), default);

        Task<CommandResult> Ask(string line, long? revision, string user)
        {
            var request = new JsonObject { ["workspaceId"] = Id, ["line"] = line };
            if (revision is { } read) request["revision"] = read;
            return Invoke("commands.preview", new JsonObject { ["request"] = request }, user);
        }

        public async Task<JsonObject> Preview(string line)
        {
            var result = await Ask(line, null, "ana");
            return result is CommandResult.Done done ? done.Outputs["answer"]!.AsObject()
                : throw new Xunit.Sdk.XunitException($"The preview of `{line}` was refused: {string.Join(" ", ((CommandResult.Refused)result).Issues.Select(i => i.Message))}");
        }

        public async Task<(string Code, CompositionIssue Issue)> Refused(string line, long? revision = null, string user = "ana")
        {
            var refused = Assert.IsType<CommandResult.Refused>(await Ask(line, revision, user));
            return (refused.Code, Assert.Single(refused.Issues));
        }
    }

    static string? Text(JsonNode node, string name) => node[name]?.GetValue<string>();

    static string Joined(JsonNode? list) => string.Join("|", list!.AsArray().Select(i => i!.GetValue<string>()));

    [Fact] // ADR-0016 §5, §7, note 38: a line's reading names the command, each argument as read, the rows meant and what would change, and nothing is written
    public async Task A_line_is_answered_as_its_command_its_rows_and_what_would_change_and_nothing_is_written()
    {
        var rig = new Rig();
        var before = rig.Board.Revision;

        var answer = await rig.Preview("set where station=north checked on");

        Assert.Equal((rig.Id, before, "set where station=north checked on"), (Text(answer, "workspaceId"), answer["revision"]!.GetValue<long>(), Text(answer, "line")));
        Assert.Equal(("entity.set", "1.0.0"), (Text(answer, "command"), Text(answer, "version")));
        Assert.Equal(["rows: where station=north", "set: checked on"], answer["arguments"]!.AsArray().Select(a => $"{Text(a!, "name")}: {Text(a!, "text")}"));
        Assert.Equal((Stations.Readings, "readings, 2 rows", 2, "R1|R2"), (Text(answer, "table"), Text(answer, "rows"), answer["count"]!.GetValue<int>(), Joined(answer["first"])));
        Assert.Equal("checked: 1 off to on, 1 already on", Text(answer, "would"));
        Assert.Equal(before, rig.Board.Revision);
        Assert.Empty(rig.Board.Overrides);
        var port = rig.Registry.FindCapability("commands.preview", "1.0.0")!.Descriptor.OutputPorts.Single();
        Assert.Empty(ContractChecks.Check(rig.Registry.FindContract(port.ContractId, port.ContractVersion)!, answer, "/answer"));
    }

    [Fact] // ADR-0016 note 38: the inputs a reading answers are handed to the command's own tool as they are, and do what the reading said
    public async Task The_inputs_a_reading_answers_run_the_command_it_named()
    {
        var rig = new Rig();
        var answer = await rig.Preview("set where station=north checked on");

        var ran = Assert.IsType<CommandResult.Done>(await rig.Invoke(Text(answer, "command")!, JsonNode.Parse(Text(answer, "inputs")!)!.AsObject()));

        Assert.Equal("Set R1 checked to on; 1 already held it.", Text(ran.Outputs["answer"]!, "said"));
    }

    [Fact] // ADR-0016 notes 29 and 38: a short form is answered as the line it stands for, which is what runs
    public async Task A_short_form_is_answered_with_the_line_it_stands_for()
    {
        var rig = new Rig();

        var answer = await rig.Preview("tick manual");

        Assert.Equal(("set manual checked on", "entity.set", "readings, 3 rows"), (Text(answer, "expands"), Text(answer, "command"), Text(answer, "rows")));
    }

    [Fact] // ADR-0016 §6, note 38: a line that cannot be read refuses the call as the reading refuses it: the code, the sentence, the word at fault and what it could have been
    public async Task A_line_that_cannot_be_read_refuses_the_call_with_the_word_at_fault()
    {
        var rig = new Rig();

        var (field, colour) = await rig.Refused("set where colour=red checked on");
        var (command, frobnicate) = await rig.Refused("frobnicate R1");

        Assert.Equal(("UnknownField", "colour"), (field, colour.Word));
        Assert.Contains("station", colour.Candidates!);
        Assert.Equal(("UnknownCommand", "frobnicate"), (command, frobnicate.Word));
        Assert.Contains("set", frobnicate.Candidates!);
    }

    [Fact] // ADR-0016 notes 17, 21 and 38: a caller with no screen is refused the rows of one and the verbs of one, by name
    public async Task The_rows_and_the_verbs_of_a_screen_are_refused()
    {
        var rig = new Rig();

        Assert.Equal("NoView", (await rig.Refused("set selected checked on")).Code);
        Assert.Equal("NoView", (await rig.Refused("set shown checked on")).Code);
        Assert.Equal("NoView", (await rig.Refused("find R1")).Code);
        // Note 44: export needs no grid where it names its table, and is still a page's to do; the rows are tables.read's.
        var (code, export) = await rig.Refused("export readings");
        Assert.Equal(("NoView", "`export readings` is done by a page of the workbench, and no command runs it: tables.read answers the rows of a table."), (code, export.Message));
    }

    [Fact] // ADR-0016 note 38, ADR-0003: a reading is of its caller's own workspace, at the revision the caller read
    public async Task A_preview_is_refused_for_another_user_and_at_a_revision_the_workspace_has_left()
    {
        var rig = new Rig();

        Assert.Equal("NotOwner", (await rig.Refused("set R1 checked on", user: "ben")).Code);
        Assert.Equal("RevisionMoved", (await rig.Refused("set R1 checked on", rig.Board.Revision + 1)).Code);
        // A workspace there is none of is refused as that.
        var unknown = await rig.Invoke("commands.preview", new JsonObject { ["request"] = new JsonObject { ["workspaceId"] = "ws-none", ["line"] = "set R1 checked on" } });
        Assert.Equal("UnknownWorkspace", Assert.IsType<CommandResult.Refused>(unknown).Code);
    }
}
