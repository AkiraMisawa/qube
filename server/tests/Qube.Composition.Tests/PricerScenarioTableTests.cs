using Qube.Composition.Application;
using Qube.Composition.Application.Workspaces;
using Qube.Samples.Portfolio;

namespace Qube.Composition.Tests;

// ADR-0016 note 58 of 2026-11-12: the pricer's scenarios are a table, with their shifts under them. Driven as a user
// does, by the line; a scenario whose name is several words is typed into the Scenarios grid, as the page types it.
public class PricerScenarioTableTests
{
    // A shift typed into the grid's blank row, which makes it.
    static void Typed(PricerRig rig, string? scenario, string index, string size) =>
        rig.Service.EditShifts(rig.Id, rig.State.Revision, "akira", [new(null, rig.Service.View(rig.Id, "akira").Shifts.Count + 1,
            new Dictionary<string, string?> { ["scenario"] = scenario, ["index"] = index, ["size"] = size })]);

    static (string Name, bool Included)[] Scenarios(PricerRig rig) => [.. rig.Service.View(rig.Id, "akira").Scenarios.Select(s => (s.Name, s.Included))];

    [Fact] // note 58: a row a scenario, in the order of their first shifts, keyed by one word, with whether Price prices it
    public async Task A_scenario_is_a_row_keyed_by_one_word()
    {
        var rig = new PricerRig();
        await rig.Run("scenario up jpy_ois +10bp; usdjpy -5%");
        await rig.Run("scenario cross jpy_ois -50bp, 0 x usdjpy -10%, 0");
        await rig.Run("scenario exclude up");

        Assert.Equal(["up", "jpy_ois_-50bp_usdjpy_-10%", "jpy_ois_-50bp", "usdjpy_-10%"], rig.Keys("scenarios all"));
        Assert.Equal("up 2 off", rig.Written(PortfolioTables.Scenarios, "up", "name", "count", "included"));
        Assert.Equal("jpy_ois -50bp · usdjpy -10% 2 on", rig.Written(PortfolioTables.Scenarios, "jpy_ois_-50bp_usdjpy_-10%", "name", "count", "included"));
        Assert.Equal(["up"], rig.Keys("scenarios where included = off"));
    }

    [Fact] // note 58: names that give one key are told apart in the scenarios' order, and no key is a word that says rows
    public void Names_that_give_one_key_are_told_apart()
    {
        var rig = new PricerRig();
        foreach (var name in new[] { "a b", "a_b", "all", "a, b" }) Typed(rig, name, "jpy_ois", "+1bp");

        Assert.Equal(["a_b", "a_b_2", "all_", "a_b_3"], rig.Keys("scenarios all"));
        Assert.Equal("a, b", rig.Written(PortfolioTables.Scenarios, "a_b_3", "name"));
    }

    [Fact] // note 58: a shift is a row under its scenario, keyed by the scenario and its own number; one that names none is under none
    public async Task A_shift_is_a_row_under_its_scenario()
    {
        var rig = new PricerRig();
        await rig.Run("scenario up jpy_ois 5y +10bp; usdjpy -5%");
        await rig.Run("scenario down jpy_ois -10bp");
        Typed(rig, null, "eurjpy", "-1%");

        var up = rig.Keys("shifts up");

        Assert.Equal(2, up.Length);
        Assert.All(up, key => Assert.StartsWith("up/", key));
        Assert.Equal("up IR jpy_ois 5y abs 10 bp", rig.Written(PortfolioTables.Shifts, up[0], "scenario", "category", "index", "factor", "type", "size"));
        Assert.Equal("up FX usdjpy spot rel -5 %", rig.Written(PortfolioTables.Shifts, up[1], "scenario", "category", "index", "factor", "type", "size"));
        var alone = Assert.Single(rig.Keys("shifts where scenario = empty"));
        Assert.StartsWith("/", alone);
        Assert.Equal("it names no scenario", rig.Written(PortfolioTables.Shifts, alone, "issue"));
        Assert.Equal(4, rig.Keys("shifts all").Length);
        Assert.Equal(up, rig.Keys("shifts where size in 10bp, -5%"));
    }

    [Fact] // note 58: the scenarios left out of Price are one list, written once for every row a command names
    public async Task Scenarios_are_left_out_of_Price_together()
    {
        var rig = new PricerRig();
        await rig.Run("scenario up jpy_ois +10bp");
        await rig.Run("scenario down jpy_ois -10bp");
        await rig.Run("scenario fx usdjpy -5%");

        var said = await rig.Run("set scenarios up, down included off");

        Assert.Equal("Set included to off on up and down.", said);
        Assert.Equal([("up", false), ("down", false), ("fx", true)], Scenarios(rig));
        Assert.Equal("included: 1 off to on, 1 already on", rig.Read("set scenarios up, fx included on"));
        // One command, which one undo takes back.
        await rig.Run("undo");
        Assert.Equal([("up", true), ("down", true), ("fx", true)], Scenarios(rig));
    }

    [Fact] // note 58: `set scenarios <key> included off` is `scenario exclude <name>`: the same edit, whichever is typed
    public async Task Included_set_on_a_row_is_the_edit_scenario_exclude_makes()
    {
        var (typed, verb) = (new PricerRig(), new PricerRig());
        foreach (var rig in new[] { typed, verb }) Typed(rig, "parallel up", "jpy_ois", "+10bp");

        await typed.Run("set scenarios parallel_up included off");
        await verb.Run("scenario exclude parallel up");

        Assert.Equal(Scenarios(verb), Scenarios(typed));
        Assert.Equal([("parallel up", false)], Scenarios(typed));
        Assert.Equal(verb.State.Created[PortfolioScenarios.Inclusion].Fields.ToJsonString(), typed.State.Created[PortfolioScenarios.Inclusion].Fields.ToJsonString());
    }

    [Fact] // note 58: a size is written with its unit, read as the grid's cell reads it, and refused as that cell refuses
    public async Task A_shifts_size_is_written_with_its_unit()
    {
        var rig = new PricerRig();
        await rig.Run("scenario up jpy_ois +10bp; usdjpy -5%");
        var up = rig.Keys("shifts up");

        var said = await rig.Run($"set shifts {up[0]} size +25bp");

        Assert.Equal($"Set {up[0]} size to 25 bp.", said);
        Assert.Equal(["+25bp", "-5%"], rig.Service.View(rig.Id, "akira").Shifts.Select(r => r.SizeText));
        // The unit says how the index shifts: an IR index is not shifted in %, and a size with no unit says neither.
        Assert.Equal($"InvalidShift: {up[0]}, Size: an IR shift is absolute, in bp: write +25bp.", rig.Read($"set shifts {up[0]} size 25%"));
        Assert.Equal($"InvalidShift: {up[1]} size has no unit, and the unit says how it shifts: write -7%.", rig.Read($"set shifts {up[1]} size -7"));
        Assert.Equal($"Nothing would change: {up[0]} already holds size 25 bp.", rig.Read($"set shifts {up[0]} size 25 bp"));
    }

    [Fact] // note 58: a write is the grid's own edit of that cell, with its checks; what follows the index and the size is read only
    public async Task A_shifts_cell_is_written_as_the_grid_writes_it()
    {
        var rig = new PricerRig();
        await rig.Run("scenario up jpy_ois +10bp; usdjpy -5%");
        var up = rig.Keys("shifts up");

        await rig.Run($"set shifts {up[0]} factor 10Y");
        await rig.Run($"set shifts {up[0]} description the long end");
        await rig.Run($"set shifts {up[1]} index eurjpy");

        Assert.Equal("jpy_ois 10y the long end", rig.Written(PortfolioTables.Shifts, up[0], "index", "factor", "description"));
        Assert.Equal("FX eurjpy spot -5 %", rig.Written(PortfolioTables.Shifts, up[1], "category", "index", "factor", "size"));
        Assert.Equal($"InvalidShift: {up[1]}, Risk factor: eurjpy is a spot rate and has no tenor: leave it spot.", rig.Read($"set shifts {up[1]} factor 5y"));
        Assert.StartsWith("NotEditable: category is not a field a command sets; shifts takes index, factor, size and description.", rig.Read($"set shifts {up[1]} category IR"));
        Assert.StartsWith("NotEditable: scenario is not a field a command sets", rig.Read($"set shifts {up[1]} scenario down"));
        // Several rows are one edit, each cell read against its own row.
        Assert.Equal("Set description to both on " + string.Join(" and ", up) + ".", await rig.Run("set shifts up description both"));
    }

    [Fact] // note 58: a shift reset goes back to what it was made as
    public async Task A_shift_reset_goes_back_to_what_it_was_made_as()
    {
        var rig = new PricerRig();
        await rig.Run("scenario up jpy_ois +10bp");
        var shift = Assert.Single(rig.Keys("shifts up"));
        await rig.Run($"set shifts {shift} size=-3bp factor=5y");

        await rig.Run($"reset shifts {shift} size");

        Assert.Equal("5y 10 bp", rig.Written(PortfolioTables.Shifts, shift, "factor", "size"));
        await rig.Run($"reset shifts {shift}");
        Assert.Equal("all 10 bp", rig.Written(PortfolioTables.Shifts, shift, "factor", "size"));
        Assert.Equal("NotResettable: up is a scenario the workspace made, and has nothing to go back to: set up included on prices it, and off leaves it out.", rig.Read("reset scenarios up"));
    }

    [Fact] // note 58: shifts removed together are one edit: a scenario whose last shifts go is gone, and is left out of nothing
    public async Task A_scenario_whose_last_shifts_are_removed_is_gone()
    {
        var rig = new PricerRig();
        await rig.Run("scenario up jpy_ois +10bp; usdjpy -5%");
        await rig.Run("scenario down jpy_ois -10bp");
        await rig.Run("scenario exclude up");
        var up = rig.Keys("shifts up");

        var said = await rig.Run("remove shifts up");

        Assert.Equal($"Removed {up[0]} and {up[1]}.", said);
        Assert.Equal(["down"], rig.Keys("scenarios all"));
        // Made again it is priced: the list of those left out lost its name with its last shift.
        await rig.Run("scenario up jpy_ois +10bp");
        Assert.Equal([("down", true), ("up", true)], Scenarios(rig));
        // One shift of two removed leaves the scenario, with the other.
        await rig.Run("scenario fx usdjpy -5%; eurjpy -5%");
        await rig.Run($"remove shifts {rig.Keys("shifts fx")[0]}");
        Assert.Equal("fx 1 on", rig.Written(PortfolioTables.Scenarios, "fx", "name", "count", "included"));
    }

    [Fact] // note 58: scenarios removed together are their shifts removed and the list written once; `remove scenarios` is `scenario remove`
    public async Task Scenarios_are_removed_together()
    {
        var rig = new PricerRig();
        await rig.Run("scenario up jpy_ois +10bp; usdjpy -5%");
        await rig.Run("scenario down jpy_ois -10bp");
        await rig.Run("scenario fx usdjpy -5%");
        await rig.Run("set scenarios up, down, fx included off");

        var said = await rig.Run("remove scenarios up, down");

        Assert.Equal("Removed up and down.", said);
        Assert.Equal([("fx", false)], Scenarios(rig));
        Assert.Equal(["fx"], rig.Service.View(rig.Id, "akira").Shifts.Select(r => r.Scenario));
        await rig.Run("scenario up jpy_ois +10bp");
        await rig.Run("scenario down jpy_ois -10bp");
        Assert.Equal([("fx", false), ("up", true), ("down", true)], Scenarios(rig));
        await rig.Run("undo");
        await rig.Run("undo");
        await rig.Run("undo");
        Assert.Equal([("up", false), ("down", false), ("fx", false)], Scenarios(rig));
    }

    [Fact] // notes 54 and 58: a scenario's draft holds its shifts under it, written and removed by lines, and nothing is written
    public async Task A_scenarios_draft_holds_its_shifts()
    {
        var rig = new PricerRig();
        await rig.Run("scenario up jpy_ois +10bp; usdjpy -5%");
        var up = rig.Keys("shifts up");
        var revision = rig.State.Revision;

        var form = rig.Lines.Draft(rig.Id, PricerRig.Akira, new(PortfolioTables.Scenarios, "up", revision, [$"set shifts {up[0]} size +25bp", $"remove shifts {up[1]}", "set scenarios up included off"]));

        // A scenario is the workspace's own: no source gave it, so there is no Original.
        Assert.Null(form.Original);
        Assert.Equal(["scenarioId up", "name up", "count 1 *", "included off *", "issue empty"], form.Fields.Select(f => $"{f.Field.Name} {f.Written}{(f.Changed ? " *" : "")}"));
        var shifts = Assert.Single(form.Under);
        Assert.Equal((PortfolioTables.Shifts, "shifts"), (shifts.Table, shifts.Word));
        // The field that names the scenario is left out: every row listed is under it.
        Assert.Equal(["shiftId", "category", "index", "factor", "type", "size", "description", "issue"], shifts.Fields.Select(f => f.Name));
        Assert.Equal([$"{up[0]} 25 bp changed", $"{up[1]} -5 % removed"], shifts.Rows.Select(r => $"{r.Key} {r.Written[5]} {r.Mark}"));
        Assert.Empty(shifts.Makes);
        Assert.Equal(revision, rig.State.Revision);
    }

    [Fact] // notes 56 and 58: open answers a scenario's row, and a shift's, as any row's
    public async Task Open_answers_a_scenarios_row()
    {
        var rig = new PricerRig();
        await rig.Run("scenario up jpy_ois +10bp");
        var shift = Assert.Single(rig.Keys("shifts up"));

        Assert.Equal(new LineOpen(PortfolioTables.Scenarios, "up", "Opened up."), rig.Preview("open up").Asks);
        Assert.Equal(new LineOpen(PortfolioTables.Shifts, shift, $"Opened {shift}."), rig.Preview($"open shifts {shift}").Asks);
    }
}
