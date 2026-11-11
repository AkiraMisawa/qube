using System.Text.Json.Nodes;
using Qube.Composition.Application;
using Qube.Composition.Application.Workspaces;

namespace Qube.Composition.Tests;

// ADR-0016 §11 and its notes 1 to 3 of 2026-11-11, on the generic workspace: a command is one accepted call, the log
// says what each of its entries replaced, and undo and redo are read from the log alone. Entities, fields and scopes
// here are illustrative.
public class WorkspaceUndoTests
{
    sealed class Source(string version, Dictionary<(string, string), JsonNode?> values) : ISourceVersion
    {
        public string Version => version;
        public JsonObject Descriptor => new() { ["version"] = version };
        public SourceValue? Read(string entity, string field) =>
            values.TryGetValue((entity, field), out var v) ? new SourceValue(v?.DeepClone()) : null;
    }

    sealed class Rules : IEditRules
    {
        public string KindOf(EditTarget target) => "test.reading";
        public IReadOnlyList<string> ConflictGroup(EditTarget target) => [target.Field];
    }

    const string Scope = "station:north";
    static readonly EditTarget Level = new(Scope, "R1", "level");
    static readonly EditTarget Checked = new(Scope, "R1", "checked");
    static readonly EditTarget Level2 = new(Scope, "R2", "level");

    static Source Day1() => new("d1", new()
    {
        [("R1", "level")] = 10, [("R1", "checked")] = false, [("R2", "level")] = 20,
    });

    static WorkspaceService Service() => new(new Dictionary<string, IEditRules> { ["test"] = new Rules() }, TimeProvider.System);

    static WorkspaceState Made(WorkspaceService service) =>
        service.Create("akira", "test", new JsonObject { ["day"] = "2026-11-11" }, Day1());

    [Fact] // ADR-0016 §11: a command is one accepted write, and every entry of the call carries its id
    public void Every_entry_of_one_call_carries_one_command_id_and_the_next_call_another()
    {
        var service = Service();
        var ws = Made(service);

        ws = service.Batch(ws.WorkspaceId, ws.Revision, "akira", [new WorkspaceChange.Set(Level, 11), new WorkspaceChange.Set(Level2, 21)], said: "set all level +1");
        var batch = ws.Log.Skip(2).ToList();
        ws = service.Set(ws.WorkspaceId, ws.Revision, "akira", Checked, true);

        // The id is c and the first sequence number the call wrote (note 1): the workspace's making wrote 1 and 2.
        Assert.Equal(["c3", "c3"], batch.Select(e => e.Command?.Id));
        Assert.All(batch, e => Assert.Equal("set all level +1", e.Command?.Said));
        Assert.Equal("c5", ws.Log[^1].Command?.Id);
        Assert.Equal(["c1", "c1"], ws.Log.Take(2).Select(e => e.Command?.Id));
    }

    [Fact] // ADR-0016 §11: each entry records what it replaced, so the log alone is enough to write a command's inverse
    public void A_set_records_the_override_it_replaced_and_nothing_where_the_field_followed_its_source()
    {
        var service = Service();
        var ws = Made(service);

        ws = service.Set(ws.WorkspaceId, ws.Revision, "akira", Level, 11);
        var first = ws.Log[^1];
        ws = service.Set(ws.WorkspaceId, ws.Revision, "akira", Level, 12);

        Assert.Null(first.Replaced);
        Assert.Equal((11, 3L), (ws.Log[^1].Replaced!.Override!.Value!.GetValue<int>(), ws.Log[^1].Replaced!.Override!.Seq));
    }

    [Fact] // ADR-0016 §11: a reset records the override it removed, and a remove the entity with the edits that ended with it
    public void A_reset_records_the_override_it_removed_and_a_remove_the_entity_with_its_edits()
    {
        var service = Service();
        var ws = Made(service);
        ws = service.Set(ws.WorkspaceId, ws.Revision, "akira", Level, 11);
        ws = service.Reset(ws.WorkspaceId, ws.Revision, "akira", Level);
        var reset = ws.Log[^1];
        ws = service.Create(ws.WorkspaceId, ws.Revision, "akira", Scope, "R9", new JsonObject { ["level"] = 90 });
        ws = service.Set(ws.WorkspaceId, ws.Revision, "akira", new EditTarget(Scope, "R9", "level"), 91);

        ws = service.Remove(ws.WorkspaceId, ws.Revision, "akira", "R9");

        Assert.Equal(11, reset.Replaced!.Override!.Value!.GetValue<int>());
        var removed = ws.Log[^1].Replaced!;
        Assert.Equal(("R9", 90), (removed.Created!.Entity, removed.Created.Fields["level"]!.GetValue<int>()));
        Assert.Equal(91, Assert.Single(removed.Overrides!).Value!.GetValue<int>());
    }

    [Fact] // ADR-0016 §11, note 1: undo puts back what a set replaced as it stood, its sequence number included, or the field follows its source again
    public void Undo_puts_back_the_override_a_set_replaced_or_removes_the_one_it_made()
    {
        var service = Service();
        var ws = Made(service);
        ws = service.Set(ws.WorkspaceId, ws.Revision, "akira", Level, 11);
        ws = service.Set(ws.WorkspaceId, ws.Revision, "akira", Level, 12);

        var (once, took) = service.Undo(ws.WorkspaceId, ws.Revision, "akira");
        var (twice, _) = service.Undo(once.WorkspaceId, once.Revision, "akira");

        Assert.Equal((11, 3L), (once.Overrides[Level].Value!.GetValue<int>(), once.Overrides[Level].Seq));
        Assert.Equal(("c4", "akira", 1), (took.Command, took.User, took.Edits));
        Assert.Empty(twice.Overrides);
        // An undo is new entries, never a removal (ADR-0003): a set back to 11, then a reset, each naming what it takes back.
        Assert.Equal([(EditKind.Set, "c4"), (EditKind.Reset, "c3")], twice.Log.Skip(4).Select(e => (e.Kind, e.Command!.Undoes!)));
    }

    [Fact] // ADR-0016 §11: a batch is taken back whole, in one command of its own
    public void Undo_takes_back_a_whole_batch_as_one_command()
    {
        var service = Service();
        var ws = Made(service);
        ws = service.Set(ws.WorkspaceId, ws.Revision, "akira", Level, 11);
        ws = service.Batch(ws.WorkspaceId, ws.Revision, "akira",
            [new WorkspaceChange.Set(Level, 12), new WorkspaceChange.Set(Level2, 22), new WorkspaceChange.Reset(Level)], said: "three edits");

        var (undone, took) = service.Undo(ws.WorkspaceId, ws.Revision, "akira");

        Assert.Equal(11, Assert.Single(undone.Overrides).Value.Value!.GetValue<int>());
        Assert.Equal(("c4", "three edits", 3), (took.Command, took.Said, took.Edits));
        Assert.Equal(["c7", "c7", "c7"], undone.Log.Skip(6).Select(e => e.Command!.Id));
    }

    [Fact] // ADR-0016 §11: a create becomes a remove, and a remove the row as it was, with the edits that ended with it
    public void Undo_removes_what_a_create_made_and_puts_back_what_a_remove_took()
    {
        var service = Service();
        var ws = Made(service);
        var onMade = new EditTarget(Scope, "R9", "level");
        ws = service.Create(ws.WorkspaceId, ws.Revision, "akira", Scope, "R9", new JsonObject { ["level"] = 90 });
        var made = ws.Created["R9"];
        ws = service.Set(ws.WorkspaceId, ws.Revision, "akira", onMade, 91);
        var edit = ws.Overrides[onMade];
        ws = service.Remove(ws.WorkspaceId, ws.Revision, "akira", "R9");

        var (back, _) = service.Undo(ws.WorkspaceId, ws.Revision, "akira");
        var (unset, _) = service.Undo(back.WorkspaceId, back.Revision, "akira");
        var (unmade, took) = service.Undo(unset.WorkspaceId, unset.Revision, "akira");

        // The row and its edit come back as they were, so the edit's base still names the row it was made on.
        Assert.Equal((made.Seq, 90, edit.Seq, edit.BaseVersion),
            (back.Created["R9"].Seq, back.Created["R9"].Fields["level"]!.GetValue<int>(), back.Overrides[onMade].Seq, back.Overrides[onMade].BaseVersion));
        Assert.Empty(unset.Overrides);
        Assert.Empty(unmade.Created);
        Assert.Equal("made R9", took.Said);
    }

    [Fact] // ADR-0016 §11, note 2: redo puts back what the last undo took back; any other command ends what can be put back
    public void Redo_puts_back_what_undo_took_back_until_another_command_runs()
    {
        var service = Service();
        var ws = Made(service);
        ws = service.Set(ws.WorkspaceId, ws.Revision, "akira", Level, 11);
        ws = service.Set(ws.WorkspaceId, ws.Revision, "akira", Level2, 21);
        ws = service.Undo(ws.WorkspaceId, ws.Revision, "akira").State;
        ws = service.Undo(ws.WorkspaceId, ws.Revision, "akira").State;

        var (first, put) = service.Redo(ws.WorkspaceId, ws.Revision, "akira");
        var (second, _) = service.Redo(first.WorkspaceId, first.Revision, "akira");

        Assert.Equal([Level], first.Overrides.Keys);
        // A redo acts on the undo before it, c6, which is named by the command it took back.
        Assert.Equal(("c6", "set R1 level"), (put.Command, put.Said));
        Assert.Equal((11, 3L, 21, 4L), (second.Overrides[Level].Value!.GetValue<int>(), second.Overrides[Level].Seq,
            second.Overrides[Level2].Value!.GetValue<int>(), second.Overrides[Level2].Seq));
        Assert.Equal("NothingToRedo", Assert.Throws<WorkspaceRefused>(() => service.Redo(second.WorkspaceId, second.Revision, "akira")).Code);

        // A command put back is taken back again, and a new command ends what could be put back.
        var again = service.Undo(second.WorkspaceId, second.Revision, "akira").State;
        Assert.Equal([Level], again.Overrides.Keys);
        again = service.Set(again.WorkspaceId, again.Revision, "akira", Checked, true);
        Assert.Equal("NothingToRedo", Assert.Throws<WorkspaceRefused>(() => service.Redo(again.WorkspaceId, again.Revision, "akira")).Code);
    }

    [Fact] // ADR-0016 §11, note 2: a change of context that keeps the source version goes back to the context it replaced
    public void Undo_takes_back_a_change_of_context_that_took_no_new_source()
    {
        var service = Service();
        var ws = Made(service);
        ws = service.Set(ws.WorkspaceId, ws.Revision, "akira", Level, 11);
        ws = service.Reacquire(ws.WorkspaceId, ws.Revision, "akira", new JsonObject { ["day"] = "2026-11-11", ["currency"] = "USD" }, ws.Source).State;

        var (undone, took) = service.Undo(ws.WorkspaceId, ws.Revision, "akira");
        var redone = service.Redo(undone.WorkspaceId, undone.Revision, "akira").State;

        Assert.Null(undone.Context["currency"]);
        Assert.Equal(11, undone.Overrides[Level].Value!.GetValue<int>());
        Assert.Equal(("changed the context", 1), (took.Said, took.Edits));
        Assert.Equal("USD", redone.Context["currency"]!.GetValue<string>());
    }

    [Fact] // ADR-0016 §11: a command that took a new source is not taken back, and undo never reaches past it to an older one
    public void Undo_refuses_at_a_new_source_version_and_reaches_nothing_before_it()
    {
        var service = Service();
        var ws = Made(service);
        ws = service.Set(ws.WorkspaceId, ws.Revision, "akira", Level, 11);
        var day2 = new Source("d2", new() { [("R1", "level")] = 10, [("R1", "checked")] = false, [("R2", "level")] = 20 });
        ws = service.Reacquire(ws.WorkspaceId, ws.Revision, "akira", new JsonObject { ["day"] = "2026-11-12" }, day2).State;

        var refused = Assert.Throws<WorkspaceRefused>(() => service.Undo(ws.WorkspaceId, ws.Revision, "akira"));

        Assert.Equal("CannotUndo", refused.Code);
        Assert.Contains("took another source version (d2)", refused.Message);
        Assert.Equal(ws.Revision, service.Find(ws.WorkspaceId)!.Revision);
        Assert.Equal(11, service.Find(ws.WorkspaceId)!.Overrides[Level].Value!.GetValue<int>());
    }

    [Fact] // ADR-0016 §11: a held edit decided, and a scope left, are not taken back either
    public void Undo_refuses_at_a_decided_held_edit_and_at_a_scope_left()
    {
        var service = Service();
        var ws = Made(service);
        ws = service.Set(ws.WorkspaceId, ws.Revision, "akira", Level, 11);
        var day2 = new Source("d2", new() { [("R1", "level")] = 15, [("R1", "checked")] = false, [("R2", "level")] = 20 });
        ws = service.Reacquire(ws.WorkspaceId, ws.Revision, "akira", new JsonObject { ["day"] = "2026-11-12" }, day2).State;
        ws = service.KeepOverride(ws.WorkspaceId, ws.Revision, "akira", Level);
        var kept = Assert.Throws<WorkspaceRefused>(() => service.Undo(ws.WorkspaceId, ws.Revision, "akira"));
        ws = service.DropScope(ws.WorkspaceId, ws.Revision, "akira", Scope, 1);
        var left = Assert.Throws<WorkspaceRefused>(() => service.Undo(ws.WorkspaceId, ws.Revision, "akira"));

        Assert.Equal(("CannotUndo", "CannotUndo"), (kept.Code, left.Code));
        Assert.Contains("decided a held edit", kept.Message);
        Assert.Contains("left a scope", left.Message);
    }

    [Fact] // ADR-0016 §11: the caller's own commands; another user's is refused by name, as is a named command that is not the latest
    public void Undo_refuses_another_users_command_and_a_named_command_that_is_not_the_latest()
    {
        var service = Service();
        var ws = Made(service);
        ws = service.Set(ws.WorkspaceId, ws.Revision, "akira", Level, 11);
        ws = service.Set(ws.WorkspaceId, ws.Revision, "ben", Level2, 21, RunOrigin.Ai);

        var others = Assert.Throws<WorkspaceRefused>(() => service.Undo(ws.WorkspaceId, ws.Revision, "akira"));
        var notLatest = Assert.Throws<WorkspaceRefused>(() => service.Undo(ws.WorkspaceId, ws.Revision, "ben", command: "c3"));
        var (undone, took) = service.Undo(ws.WorkspaceId, ws.Revision, "ben", command: "c4", RunOrigin.Ai);

        Assert.Equal(("NotYourCommand", "NotLatestCommand"), (others.Code, notLatest.Code));
        Assert.Contains("ben's (set R2 level)", others.Message);
        Assert.Contains("c3 is not the latest command; c4 is", notLatest.Message);
        Assert.Equal((RunOrigin.Ai, "ben"), (took.Origin, took.User));
        Assert.Equal(RunOrigin.Ai, undone.Log[^1].Origin);
    }

    [Fact] // ADR-0016 §11, note 2: a workspace as it was made has nothing to take back, and its making is not undone
    public void Undo_on_a_workspace_as_it_was_made_says_there_is_nothing()
    {
        var service = Service();
        var ws = Made(service);

        Assert.Equal("NothingToUndo", Assert.Throws<WorkspaceRefused>(() => service.Undo(ws.WorkspaceId, ws.Revision, "akira")).Code);
        Assert.Equal("NothingToRedo", Assert.Throws<WorkspaceRefused>(() => service.Redo(ws.WorkspaceId, ws.Revision, "akira")).Code);
        ws = service.Set(ws.WorkspaceId, ws.Revision, "akira", Level, 11);
        ws = service.Undo(ws.WorkspaceId, ws.Revision, "akira").State;
        Assert.Equal("NothingToUndo", Assert.Throws<WorkspaceRefused>(() => service.Undo(ws.WorkspaceId, ws.Revision, "akira")).Code);
        Assert.Equal("RevisionMoved", Assert.Throws<WorkspaceRefused>(() => service.Undo(ws.WorkspaceId, ws.Revision - 1, "akira")).Code);
    }

    [Fact] // ADR-0016 §11, note 2: values a workspace is made with are its making's, so there is nothing of them to take back
    public void Values_a_workspace_is_made_with_are_part_of_its_making()
    {
        var service = Service();

        var ws = service.Create("akira", "test", new JsonObject { ["day"] = "2026-11-11" }, Day1(), with: [new WorkspaceChange.Set(Level, 11)]);

        Assert.Equal((11, 3L), (ws.Overrides[Level].Value!.GetValue<int>(), ws.Revision));
        Assert.Equal(["c1", "c1", "c1"], ws.Log.Select(e => e.Command?.Id));
        Assert.Equal("NothingToUndo", Assert.Throws<WorkspaceRefused>(() => service.Undo(ws.WorkspaceId, ws.Revision, "akira")).Code);
        // The next command is taken back alone, and the making's value stands.
        ws = service.Set(ws.WorkspaceId, ws.Revision, "akira", Level, 12);
        ws = service.Undo(ws.WorkspaceId, ws.Revision, "akira").State;
        Assert.Equal(11, ws.Overrides[Level].Value!.GetValue<int>());
    }
}
