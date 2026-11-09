using System.Globalization;
using System.Text.Json.Nodes;
using Workbench.Composition.Application.Saved;

namespace Workbench.Persistence.Sqlite;

/// <summary>
/// Saved items (C32) in the demonstrator's SQLite file, beside the workspaces: one generic table for every kind, its
/// content JSON the store never reads.
/// </summary>
public sealed class SqliteSavedItemStore : ISavedItemStore
{
    readonly SqliteFile file;

    /// <param name="path">The database file. It is created with its table when missing.</param>
    public SqliteSavedItemStore(string path)
    {
        file = new SqliteFile(path);
        using var connection = file.Open();
        SqliteFile.Execute(connection, Schema);
    }

    const string Schema = """
        CREATE TABLE IF NOT EXISTS SavedItem (
            kind TEXT NOT NULL,
            owner_id TEXT NOT NULL,
            name_key TEXT NOT NULL,
            name TEXT NOT NULL,
            visibility TEXT NOT NULL CHECK (visibility IN ('private', 'desk', 'all')),
            desk TEXT,
            saved_at TEXT NOT NULL,
            content_json TEXT NOT NULL CHECK (json_valid(content_json)),
            PRIMARY KEY (kind, owner_id, name_key)
        );
        """;

    public IReadOnlyList<SavedItem> All(string kind)
    {
        using var connection = file.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name, owner_id, visibility, desk, saved_at, content_json FROM SavedItem WHERE kind = $kind";
        command.Parameters.AddWithValue("$kind", kind);
        using var reader = command.ExecuteReader();
        var items = new List<SavedItem>();
        while (reader.Read())
            items.Add(new SavedItem(kind, reader.GetString(0), reader.GetString(1), VisibilityOf(reader.GetString(2)), reader.IsDBNull(3) ? null : reader.GetString(3),
                DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                JsonNode.Parse(reader.GetString(5)) ?? throw new InvalidOperationException($"A saved {kind} holds no content.")));
        return items;
    }

    public void Save(SavedItem item)
    {
        using var connection = file.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO SavedItem (kind, owner_id, name_key, name, visibility, desk, saved_at, content_json)
            VALUES ($kind, $owner, $key, $name, $visibility, $desk, $at, $content)
            ON CONFLICT (kind, owner_id, name_key) DO UPDATE SET
                name = excluded.name, visibility = excluded.visibility, desk = excluded.desk, saved_at = excluded.saved_at, content_json = excluded.content_json
            """;
        command.Parameters.AddWithValue("$kind", item.Kind);
        command.Parameters.AddWithValue("$owner", item.Owner);
        command.Parameters.AddWithValue("$key", item.Key);
        command.Parameters.AddWithValue("$name", item.Name);
        command.Parameters.AddWithValue("$visibility", TextOf(item.Visibility));
        command.Parameters.AddWithValue("$desk", (object?)item.Desk ?? DBNull.Value);
        command.Parameters.AddWithValue("$at", SqliteFile.Text(item.SavedAt));
        command.Parameters.AddWithValue("$content", item.Content.ToJsonString());
        command.ExecuteNonQuery();
    }

    static string TextOf(SavedVisibility visibility) => visibility switch
    {
        SavedVisibility.Private => "private",
        SavedVisibility.Desk => "desk",
        SavedVisibility.All => "all",
        _ => throw new ArgumentOutOfRangeException(nameof(visibility), visibility, "No such visibility."),
    };

    static SavedVisibility VisibilityOf(string text) => text switch
    {
        "private" => SavedVisibility.Private,
        "desk" => SavedVisibility.Desk,
        "all" => SavedVisibility.All,
        _ => throw new InvalidOperationException($"A saved item's visibility is private, desk or all, not {text}."),
    };
}
