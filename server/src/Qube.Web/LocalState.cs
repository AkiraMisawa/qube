namespace Qube.Web;

/// <summary>
/// Where the host keeps its SQLite file, and what it will not start beside (C50). Until 2026-11-10 the configuration
/// keys were <c>Workbench:*</c> and the file was <c>workbench.db</c>. A key or a file left under the earlier name would
/// be passed over without a word, and the host would start empty beside the user's data, so each is refused by name.
/// </summary>
public static class LocalState
{
    const string Name = "qube.db";
    const string EarlierName = "workbench.db";
    const string EarlierSection = "Workbench";

    /// <summary>The file <c>Qube:Database</c> names, or <c>App_Data/qube.db</c> under the content root.</summary>
    public static string Database(IConfiguration configuration, string contentRoot)
    {
        var earlierKeys = configuration.GetSection(EarlierSection).GetChildren().Select(c => $"{EarlierSection}:{c.Key}").ToList();
        if (earlierKeys.Count > 0)
            throw new InvalidOperationException(
                $"The configuration holds {string.Join(", ", earlierKeys)}. These keys are Qube:* since the application was named Qube "
                + "(Qube__Database in the environment, for example); rename them.");

        if (configuration["Qube:Database"] is { } configured) return Path.GetFullPath(configured);

        var directory = Path.Combine(contentRoot, "App_Data");
        var file = Path.Combine(directory, Name);
        if (!File.Exists(file) && File.Exists(Path.Combine(directory, EarlierName)))
            throw new InvalidOperationException(
                $"{directory} holds {EarlierName}, the database's name before the application was named Qube, and no {Name}. "
                + $"Rename it to {Name}, with its -wal and -shm files if it has them, to keep its data, or remove it to start empty.");
        return file;
    }
}
