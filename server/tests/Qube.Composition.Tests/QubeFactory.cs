using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Qube.Composition.Tests;

/// <summary>The web host on a database and a log directory of its own, removed afterwards.</summary>
public sealed class QubeFactory : WebApplicationFactory<Program>
{
    readonly string database;
    readonly bool ownsDatabase;
    readonly string logs = Path.Combine(Path.GetTempPath(), $"qube-api-logs-{Guid.NewGuid():N}");

    public QubeFactory() : this(Path.Combine(Path.GetTempPath(), $"qube-api-{Guid.NewGuid():N}.db"), ownsDatabase: true)
    {
    }

    /// <summary>A host on a database that outlives it, as a restart finds it: the caller removes the database.</summary>
    internal QubeFactory(string database, bool ownsDatabase)
    {
        this.database = database;
        this.ownsDatabase = ownsDatabase;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.UseSetting("Qube:Database", database).UseSetting("Qube:LogDirectory", logs).UseSetting("Qube:DemoUser", "akira");

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (ownsDatabase) RemoveDatabase(database);
        if (Directory.Exists(logs)) Directory.Delete(logs, recursive: true);
    }

    internal static void RemoveDatabase(string database)
    {
        foreach (var file in new[] { database, database + "-wal", database + "-shm" }) File.Delete(file);
    }
}
