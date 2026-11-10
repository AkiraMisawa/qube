using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Qube.Composition.Tests;

/// <summary>The web host on a database and a log directory of its own, removed afterwards.</summary>
public sealed class QubeFactory : WebApplicationFactory<Program>
{
    readonly string database = Path.Combine(Path.GetTempPath(), $"qube-api-{Guid.NewGuid():N}.db");
    readonly string logs = Path.Combine(Path.GetTempPath(), $"qube-api-logs-{Guid.NewGuid():N}");

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.UseSetting("Qube:Database", database).UseSetting("Qube:LogDirectory", logs).UseSetting("Qube:DemoUser", "akira");

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var file in new[] { database, database + "-wal", database + "-shm" }) File.Delete(file);
        if (Directory.Exists(logs)) Directory.Delete(logs, recursive: true);
    }
}
