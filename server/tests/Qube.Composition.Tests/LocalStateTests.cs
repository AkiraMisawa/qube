using Microsoft.Extensions.Configuration;
using Qube.Web;

namespace Qube.Composition.Tests;

// C50: the keys were Workbench:* and the file workbench.db until 2026-11-10. A key or a file left under the earlier
// name is refused by name, never passed over: the host would otherwise start empty beside the user's data.
public sealed class LocalStateTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("qube-root-").FullName;

    public void Dispose() => Directory.Delete(root, recursive: true);

    static IConfiguration Configuration(params (string Key, string Value)[] settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings.Select(s => KeyValuePair.Create(s.Key, (string?)s.Value))).Build();

    string DataFile(string name)
    {
        var file = Path.Combine(Directory.CreateDirectory(Path.Combine(root, "App_Data")).FullName, name);
        File.WriteAllBytes(file, []);
        return file;
    }

    [Fact]
    public void The_database_is_the_configured_file_or_qube_db_under_the_content_root()
    {
        var configured = Path.Combine(root, "elsewhere.db");
        Assert.Equal(configured, LocalState.Database(Configuration(("Qube:Database", configured)), root));
        Assert.Equal(Path.Combine(root, "App_Data", "qube.db"), LocalState.Database(Configuration(), root));
    }

    [Fact]
    public void A_folder_that_holds_only_the_earlier_file_is_refused_with_what_to_do()
    {
        DataFile("workbench.db");
        var refusal = Assert.Throws<InvalidOperationException>(() => LocalState.Database(Configuration(), root));
        Assert.Contains("workbench.db", refusal.Message);
        Assert.Contains("Rename it to qube.db", refusal.Message);
    }

    [Fact]
    public void The_earlier_file_beside_the_new_one_or_a_configured_one_stops_nothing()
    {
        DataFile("workbench.db");
        var configured = Path.Combine(root, "elsewhere.db");
        Assert.Equal(configured, LocalState.Database(Configuration(("Qube:Database", configured)), root));
        var file = DataFile("qube.db");
        Assert.Equal(file, LocalState.Database(Configuration(), root));
    }

    [Fact]
    public void A_key_left_under_the_earlier_name_is_refused_by_name()
    {
        var refusal = Assert.Throws<InvalidOperationException>(
            () => LocalState.Database(Configuration(("Workbench:Database", "old.db"), ("Workbench:DemoUser", "akira")), root));
        Assert.Contains("Workbench:Database", refusal.Message);
        Assert.Contains("Workbench:DemoUser", refusal.Message);
        Assert.Contains("Qube:", refusal.Message);
    }
}
