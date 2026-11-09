using Workbench.Composition.Application;
using Workbench.Composition.Application.Saved;
using Workbench.Composition.Application.Workspaces;
using Workbench.Persistence.Sqlite;
using Workbench.Samples.Ccr;
using Workbench.Samples.Portfolio;
using Workbench.Samples.Xva;

namespace Workbench.Composition.Tests;

// Saved scenario sets (ADR-0010 §4 and its note of 2026-11-09, C32): a user's alone, their desk's or every user's; saved
// again by their owner alone; loaded into a workspace by name, or by name and owner where two share it. Names are synthetic.
public sealed class ScenarioSetTests : IDisposable
{
    static readonly DateOnly Day1 = new(2026, 10, 26);
    readonly string path = Path.Combine(Path.GetTempPath(), $"workbench-sets-{Guid.NewGuid():N}.db");

    static readonly Desks Desks = new(new Dictionary<string, string> { ["akira"] = "xva", ["ben"] = "xva", ["carol"] = "credit" });

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var file in new[] { path, path + "-wal", path + "-shm" }) File.Delete(file);
    }

    static PortfolioWorkspaces Service(ISavedItemStore? sets = null, WorkspaceService? workspaces = null)
    {
        workspaces ??= new WorkspaceService(new Dictionary<string, IEditRules> { [PortfolioEditRules.App] = new PortfolioEditRules() }, TimeProvider.System);
        var store = new InMemoryDataStore();
        var runs = new RunService(new CompositionRegistry([new PortfolioModule(), new XvaModule(), new CcrModule()]), store, TimeProvider.System);
        var register = new MeasureRegister([new XvaMeasures(Pv01Provider.Simulated(TimeSpan.Zero)), new CcrMeasures()]);
        return new PortfolioWorkspaces(workspaces, store, runs, register, saved: new SavedItems(sets ?? new InMemorySavedItemStore(), Desks, TimeProvider.System));
    }

    static WorkspaceState Workspace(PortfolioWorkspaces service, string user, params (string Name, string Shifts)[] scenarios)
    {
        var ws = service.Create(user, "ABC/LE-01", Day1, 40);
        foreach (var (name, shifts) in scenarios) ws = service.DefineScenario(ws.WorkspaceId, ws.Revision, user, name, shifts).State;
        return ws;
    }

    static SetSaved Save(PortfolioWorkspaces service, WorkspaceState ws, string set, string? share = null) =>
        service.SaveScenarios(ws.WorkspaceId, ws.Revision, ws.Owner, set, share);

    static IReadOnlyList<string> Seen(PortfolioWorkspaces service, string user) => [.. service.ScenarioSets(user).Select(s => s.Label)];

    [Fact] // ADR-0010 §4, C32: a private set is its owner's alone, a desk's is that desk's, one for all is every user's
    public void A_private_set_is_seen_by_its_owner_a_desks_by_that_desk_and_one_for_all_by_every_user()
    {
        var service = Service();
        var ws = Workspace(service, "akira", ("up", "jpy_ois +10bp"));

        Save(service, ws, "mine");
        var ours = Save(service, ws, "ours", "desk");
        Save(service, ws, "open", "all");

        Assert.Equal((SavedVisibility.Desk, "xva", 1), (ours.Visibility, ours.Desk, ours.Scenarios));
        Assert.Equal(["mine", "open", "ours"], Seen(service, "akira"));
        Assert.Equal(["open", "ours"], Seen(service, "ben"));
        Assert.Equal(["open"], Seen(service, "carol"));
        Assert.Equal(["open"], Seen(service, "dana"));
        var theirs = Workspace(service, "ben");
        var refused = Assert.Throws<WorkspaceRefused>(() => service.LoadScenarios(theirs.WorkspaceId, theirs.Revision, "ben", "mine"));
        Assert.Equal(("UnknownSaved", "There is no set mine you can see: there are open, ours."), (refused.Code, refused.Message));
    }

    [Fact] // ADR-0010 §4, C32: saved again, a set replaces its owner's own; another user's save of the name is theirs, not a replacement
    public void Saving_again_replaces_the_owners_set_and_another_user_cannot_replace_it()
    {
        var service = Service();
        var ws = Workspace(service, "akira", ("up", "jpy_ois +10bp"));
        Assert.False(Save(service, ws, "rates", "all").Replaced);
        ws = service.DefineScenario(ws.WorkspaceId, ws.Revision, "akira", "down", "jpy_ois -10bp").State;

        var again = Save(service, ws, "Rates", "all");
        var bens = Save(service, Workspace(service, "ben", ("flat", "usdjpy -5%")), "rates", "all");

        Assert.Equal((true, 2), (again.Replaced, again.Scenarios));
        Assert.Equal((false, "ben"), (bens.Replaced, bens.Owner));
        Assert.Equal([("Rates", "akira", 2), ("rates", "ben", 1)], service.ScenarioSets("carol").Select(s => (s.Name, s.Owner, s.Scenarios)));
    }

    [Fact] // ADR-0010 §4: a set shared with a desk from a user who has none is refused, naming the user
    public void Desk_from_a_user_with_no_desk_is_refused_by_name()
    {
        var service = Service();
        var ws = Workspace(service, "dana", ("up", "jpy_ois +10bp"));

        var refused = Assert.Throws<WorkspaceRefused>(() => Save(service, ws, "rates", "desk"));

        Assert.Equal("NoDesk", refused.Code);
        Assert.StartsWith("dana belongs to no desk", refused.Message);
        Assert.Empty(service.ScenarioSets("dana"));
    }

    [Theory] // ADR-0010 §4: a save that cannot be made is refused, and nothing is saved
    [InlineData(null, "rates up", "InvalidName", "A set's name is one word without @")]
    [InlineData("team", "rates", "InvalidName", "scenario save rates shares the set with desk or all, not team.")]
    [InlineData(null, "", "InvalidName", "A set needs a name")]
    public void A_save_that_cannot_be_made_is_refused(string? share, string set, string code, string said)
    {
        var service = Service();
        var ws = Workspace(service, "akira", ("up", "jpy_ois +10bp"));

        var refused = Assert.Throws<WorkspaceRefused>(() => Save(service, ws, set, share));

        Assert.Equal(code, refused.Code);
        Assert.StartsWith(said, refused.Message);
        Assert.Empty(service.ScenarioSets("akira"));
        var empty = Workspace(service, "akira");
        Assert.Equal("NothingToSave", Assert.Throws<WorkspaceRefused>(() => Save(service, empty, "rates")).Code);
    }

    [Fact] // ADR-0010 §4: load adds the set's scenarios, replacing those of the same name, included as saved, rows as they stood
    public void Load_adds_the_scenarios_replaces_those_of_the_same_name_and_says_so()
    {
        var service = Service();
        var saved = Workspace(service, "akira", ("up", "jpy_ois +10bp; usdjpy -5%"), ("down", "jpy_ois -10bp"));
        saved = service.IncludeScenario(saved.WorkspaceId, saved.Revision, "akira", "down", false);
        saved = service.EditShifts(saved.WorkspaceId, saved.Revision, "akira",
            [new ShiftEdit(null, 4, new Dictionary<string, string?> { ["scenario"] = "half", ["index"] = "usd_sofr", ["description"] = "to finish" })]);
        Save(service, saved, "rates");
        var ws = Workspace(service, "akira", ("UP", "eur_estr +1bp"), ("keep", "gbpjpy +2%"));

        var answer = service.LoadScenarios(ws.WorkspaceId, ws.Revision, "akira", "rates");

        Assert.Equal(["up", "down", "half"], answer.Made);
        Assert.Equal(["up"], answer.Replaced);
        var view = service.View(ws.WorkspaceId, "akira");
        Assert.Equal([("keep", 1, true), ("up", 2, true), ("down", 1, false), ("half", 1, true)], view.Scenarios.Select(s => (s.Name, s.Shifts, s.Included)));
        Assert.Equal(["gbpjpy +2%", "jpy_ois +10bp", "usdjpy -5%", "jpy_ois -10bp", null], view.Shifts.Select(r => r.SizeText is null ? null : $"{r.Index} {r.SizeText}"));
        var half = view.Shifts.Single(r => r.Scenario == "half");
        Assert.Equal(("usd_sofr", "to finish", "it has no size"), (half.Index!, half.Description!, half.Issue!));
    }

    [Fact] // ADR-0010 §4: a name two sets the user can see share is refused naming their owners; <set>@<owner> picks one
    public void An_ambiguous_name_is_refused_naming_the_owners_and_set_at_owner_picks_one()
    {
        var service = Service();
        Save(service, Workspace(service, "akira", ("up", "jpy_ois +10bp")), "rates");
        Save(service, Workspace(service, "ben", ("flat", "usdjpy -5%")), "rates", "desk");
        var ws = Workspace(service, "akira");

        var refused = Assert.Throws<WorkspaceRefused>(() => service.LoadScenarios(ws.WorkspaceId, ws.Revision, "akira", "rates"));
        var picked = service.LoadScenarios(ws.WorkspaceId, ws.Revision, "akira", "rates@ben");

        Assert.Equal(("AmbiguousSaved", "2 sets named rates can be seen, akira's and ben's: name one, as rates@akira or rates@ben."), (refused.Code, refused.Message));
        Assert.Equal(["rates@akira", "rates@ben"], Seen(service, "akira"));
        Assert.Empty(Seen(service, "carol"));
        Assert.Equal(["flat"], picked.Made);
        Assert.Equal(["flat"], service.View(ws.WorkspaceId, "akira").Scenarios.Select(s => s.Name));
    }

    [Fact] // ADR-0010 §4, D04: a set saved before a restart loads after it, from the SQLite file beside the workspaces
    public void A_set_saved_before_a_restart_loads_after_it()
    {
        var workspaces = new WorkspaceService(new Dictionary<string, IEditRules> { [PortfolioEditRules.App] = new PortfolioEditRules() }, TimeProvider.System);
        var before = Service(new SqliteSavedItemStore(path), workspaces);
        Save(before, Workspace(before, "akira", ("up", "jpy_ois +10bp; usdjpy -5%")), "rates", "desk");
        var ws = Workspace(before, "ben");

        var after = Service(new SqliteSavedItemStore(path), workspaces);
        var loaded = after.LoadScenarios(ws.WorkspaceId, ws.Revision, "ben", "rates");

        Assert.Equal([("rates", "akira", SavedVisibility.Desk, "xva", 1)], after.ScenarioSets("ben").Select(s => (s.Name, s.Owner, s.Visibility, s.Desk, s.Scenarios)));
        Assert.Equal(["up"], loaded.Made);
    }
}
