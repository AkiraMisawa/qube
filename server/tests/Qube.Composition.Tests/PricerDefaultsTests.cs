using System.Text.Json.Nodes;
using Qube.Composition.Application;
using Qube.Composition.Application.Settings;
using Qube.Composition.Application.Workspaces;
using Qube.Samples.Ccr;
using Qube.Samples.Portfolio;
using Qube.Samples.Xva;

namespace Qube.Composition.Tests;

// ADR-0017 §3: the user's display currency is a default. The making of a pricer workspace copies it in, as the making's
// own edit, and the page says where the currency came from; a workspace made before it was set, or edited since, keeps
// its own. Every figure is the simulated engine's.
public class PricerDefaultsTests
{
    static readonly DateOnly Day1 = new(2026, 10, 26);

    sealed record Rig(PortfolioWorkspaces Service, WorkspaceService Workspaces, UserSettings Settings, InMemoryUserSettingStore Store);

    static Rig Built()
    {
        var workspaces = new WorkspaceService(new Dictionary<string, IEditRules> { [PortfolioEditRules.App] = new PortfolioEditRules() }, TimeProvider.System);
        var data = new InMemoryDataStore();
        var runs = new RunService(new CompositionRegistry([new PortfolioModule(), new XvaModule(), new CcrModule()]), data, TimeProvider.System);
        var register = new MeasureRegister([new XvaMeasures(Pv01Provider.Simulated(TimeSpan.Zero)), new CcrMeasures()]);
        var store = new InMemoryUserSettingStore();
        var settings = new UserSettings([new ShellSettings(), new PortfolioSettings()], store, TimeProvider.System);
        return new(new PortfolioWorkspaces(workspaces, data, runs, register, settings: settings), workspaces, settings, store);
    }

    static (string Currency, string From) Shown(Rig rig, WorkspaceState ws)
    {
        var view = rig.Service.View(ws.WorkspaceId, ws.Owner);
        return (view.DisplayCurrency, view.DisplayCurrencyFrom);
    }

    [Fact] // ADR-0017 §3: a workspace made after the display currency is set starts in it and says so; one made before, and another user's, do not
    public void A_new_workspace_starts_in_the_display_currency_set()
    {
        var rig = Built();
        var before = rig.Service.Create("akira", "ABC/LE-01", Day1, 0);

        rig.Settings.Set("akira", "display currency", "usd");
        var after = rig.Service.Create("akira", "ABC/LE-01", Day1, 0);
        var other = rig.Service.Create("bea", "ABC/LE-01", Day1, 0);

        Assert.Equal(("JPY", "profile"), Shown(rig, before));
        Assert.Equal(("USD", "setting"), Shown(rig, after));
        Assert.Equal(("JPY", "profile"), Shown(rig, other));
    }

    [Fact] // ADR-0017 §3, ADR-0016 §11: a currency chosen in the workspace is its own and undo takes it back; the copy is the making's, which undo never takes
    public void A_currency_chosen_in_the_workspace_is_its_own_and_undo_finds_the_copy_the_making_wrote()
    {
        var rig = Built();
        rig.Settings.Set("akira", "display currency", "USD");
        var made = rig.Service.Create("akira", "ABC/LE-01", Day1, 0);

        var edited = rig.Service.SetDisplayCurrency(made.WorkspaceId, made.Revision, "akira", "EUR");
        var chosen = Shown(rig, edited);
        var (undone, _) = rig.Workspaces.Undo(edited.WorkspaceId, edited.Revision, "akira");
        rig.Settings.Set("akira", "display currency", "GBP");

        Assert.Equal(("EUR", "workspace"), chosen);
        Assert.Equal(("USD", "setting"), Shown(rig, undone));
        Assert.Equal("NothingToUndo", Assert.Throws<WorkspaceRefused>(() => rig.Workspaces.Undo(undone.WorkspaceId, undone.Revision, "akira")).Code);
    }

    [Fact] // ADR-0017 §3: a display currency the user set that the pricer no longer offers refuses the making, by the setting's name
    public void An_unreadable_display_currency_refuses_the_making()
    {
        var rig = Built();
        rig.Store.Put("akira", new StoredSetting(PortfolioSettings.DisplayCurrency, JsonValue.Create("CHF"), DateTimeOffset.UnixEpoch));

        var refused = Assert.Throws<WorkspaceRefused>(() => rig.Service.Create("akira", "ABC/LE-01", Day1, 0));

        Assert.Equal(("UnreadableSetting", "Your display currency setting, CHF, is no longer one of GBP, JPY, USD, EUR: set it again, or type settings reset display currency."),
            (refused.Code, refused.Message));
        Assert.Empty(rig.Service.OwnedBy("akira"));
    }
}
