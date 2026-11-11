using System.Text.Json.Nodes;
using Qube.Composition.Application;
using Qube.Composition.Application.Settings;
using Qube.Persistence.Sqlite;

namespace Qube.Composition.Tests;

// A user's settings (ADR-0017 §2): registered by the module they belong to, kept per user, checked against their form,
// a stored value no longer taken said unreadable and never replaced. The registration is synthetic and names no domain.
public sealed class UserSettingsTests : IDisposable
{
    readonly string path = Path.Combine(Path.GetTempPath(), $"qube-settings-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var file in new[] { path, path + "-wal", path + "-shm" }) File.Delete(file);
    }

    internal sealed class Garden(params string[] colours) : ISettingCatalog
    {
        public IEnumerable<SettingDefinition> Settings =>
        [
            new("garden.colour", "Fence colour", ["colour", "fence colour"], SettingKind.Preference,
                new FieldSpec(FieldForm.Choice, Choices: colours.ToDictionary(c => c, c => c)), JsonValue.Create("green")),
            new("garden.height", "Hedge height", ["height"], SettingKind.Default, new FieldSpec(FieldForm.Amount), null),
            new("garden.beds.*", "Bed widths", ["beds"], SettingKind.Preference, null, null,
                Check: v => v is JsonObject o && o.All(p => p.Value is JsonValue n && n.TryGetValue<int>(out var px) && px > 0) ? null : "an object of positive whole widths"),
        ];
    }

    static UserSettings Service(IUserSettingStore? store = null, ISettingCatalog? catalog = null) =>
        new([catalog ?? new Garden("green", "red")], store ?? new InMemoryUserSettingStore(), TimeProvider.System);

    static SettingRead One(UserSettings settings, string user, string key) => settings.Read(user).Single(s => s.Key == key);

    [Fact] // ADR-0017 §2: a setting reads as its registered value until the user sets one
    public void A_setting_reads_as_its_registered_value_until_set_and_as_the_value_set_after()
    {
        var settings = Service();
        var before = One(settings, "akira", "garden.colour");
        Assert.False(before.Set);
        Assert.Equal("green", before.Value!.GetValue<string>());

        var (replaced, now) = settings.Set("akira", "fence colour", "red");

        Assert.False(replaced.Set);
        Assert.True(now.Set);
        Assert.Equal("red", One(settings, "akira", "garden.colour").Value!.GetValue<string>());
        Assert.Equal("red", settings.Text(now));
    }

    [Fact] // ADR-0017 §2: one user's settings are not another's
    public void One_users_setting_is_not_anothers()
    {
        var settings = Service();
        settings.Set("akira", "colour", "red");

        Assert.False(One(settings, "ben", "garden.colour").Set);
        Assert.Equal("green", One(settings, "ben", "garden.colour").Value!.GetValue<string>());
    }

    [Fact] // ADR-0017 §2: an unregistered key, or a value its form refuses, is refused by name and nothing is written
    public void An_unregistered_setting_and_a_value_its_form_refuses_are_refused_by_name()
    {
        var settings = Service();

        var unknown = Assert.Throws<SettingRefused>(() => settings.Set("akira", "roof", "red"));
        Assert.Equal("UnknownSetting", unknown.Code);
        Assert.Contains("colour", unknown.Message);

        var refused = Assert.Throws<SettingRefused>(() => settings.Set("akira", "colour", "blue"));
        Assert.Equal("InvalidValue", refused.Code);
        Assert.Contains("Fence colour", refused.Message);
        Assert.Contains("red", refused.Message);

        var structured = Assert.Throws<SettingRefused>(() => settings.Set("akira", "beds north", """{"rose":-3}"""));
        Assert.Contains("positive whole widths", structured.Message);

        Assert.All(settings.Read("akira"), s => Assert.False(s.Set));
    }

    [Fact] // ADR-0017 §2: a setting with no registered value reads as none, and a default form reads as typed
    public void A_setting_with_no_registered_value_reads_as_none_until_set()
    {
        var settings = Service();
        Assert.Null(One(settings, "akira", "garden.height").Value);

        var (_, now) = settings.Set("akira", "height", "10mm");

        Assert.Equal(10_000_000m, FieldForms.Held(now.Value!));
        Assert.Equal("10mm", settings.Text(now));
    }

    [Fact] // ADR-0017 §2, §5: a family's members are kept by their own key and listed once set
    public void A_familys_member_is_kept_by_its_own_key()
    {
        var settings = Service();
        Assert.DoesNotContain(settings.Read("akira"), s => s.Key.StartsWith("garden.beds.", StringComparison.Ordinal));

        settings.SetValue("akira", "garden.beds.north", new JsonObject { ["rose"] = 120 });

        var bed = One(settings, "akira", "garden.beds.north");
        Assert.True(bed.Set);
        Assert.Equal(120, bed.Value!["rose"]!.GetValue<int>());
        Assert.Equal("beds north", bed.Name);
    }

    [Fact] // ADR-0017 §2: a reset returns the registered value and says what it replaced
    public void A_reset_returns_the_registered_value_and_says_what_it_replaced()
    {
        var settings = Service();
        settings.Set("akira", "colour", "red");

        var (replaced, now) = settings.Reset("akira", "colour");

        Assert.Equal("red", replaced.Value!.GetValue<string>());
        Assert.False(now.Set);
        Assert.Equal("green", now.Value!.GetValue<string>());
    }

    [Fact] // ADR-0017 §2: a stored value its form no longer takes is unreadable, shown so, never another value
    public void A_stored_value_no_longer_taken_is_unreadable_and_a_default_that_needs_it_is_refused()
    {
        var store = new InMemoryUserSettingStore();
        Service(store, new Garden("green", "red")).Set("akira", "colour", "red");
        // A value written before its form refused it, as a later registration may: an amount below zero.
        store.Put("akira", new StoredSetting("garden.height", JsonValue.Create(-5m), DateTimeOffset.UnixEpoch));

        var later = Service(store, new Garden("green", "blue"));

        var colour = One(later, "akira", "garden.colour");
        Assert.True(colour.Set);
        Assert.NotNull(colour.Unreadable);
        Assert.Equal("red", colour.Stored!.GetValue<string>());
        var refused = Assert.Throws<SettingRefused>(() => later.Default("akira", "garden.height"));
        Assert.Equal("UnreadableSetting", refused.Code);
        Assert.Contains("settings reset height", refused.Message);
    }

    [Fact] // ADR-0017 §3: a default the user has not set is none, and one set is theirs
    public void A_default_is_the_users_value_when_set_and_none_when_not()
    {
        var settings = Service();
        Assert.Null(settings.Default("akira", "garden.height"));

        settings.Set("akira", "height", "5mm");

        Assert.Equal(5_000_000m, FieldForms.Held(settings.Default("akira", "garden.height")!));
    }

    [Fact] // ADR-0017 §2: two registrations of one key, or of one word, stop startup
    public void Two_registrations_of_one_key_or_one_word_stop_startup()
    {
        Assert.Throws<InvalidOperationException>(() => new UserSettings([new Garden("green"), new Garden("red")], new InMemoryUserSettingStore(), TimeProvider.System));
    }

    [Fact] // ADR-0017 §2: a setting kept in SQLite is read again after a restart, and a reset removes it
    public void A_setting_kept_in_sqlite_is_read_again_after_a_restart()
    {
        Service(new SqliteUserSettingStore(path)).Set("akira", "colour", "red");
        Service(new SqliteUserSettingStore(path)).SetValue("akira", "garden.beds.north", new JsonObject { ["rose"] = 80 });

        var again = Service(new SqliteUserSettingStore(path));
        Assert.Equal("red", One(again, "akira", "garden.colour").Value!.GetValue<string>());
        Assert.Equal(80, One(again, "akira", "garden.beds.north").Value!["rose"]!.GetValue<int>());
        Assert.False(One(again, "ben", "garden.colour").Set);

        again.Reset("akira", "colour");
        Assert.False(One(Service(new SqliteUserSettingStore(path)), "akira", "garden.colour").Set);
    }
}
