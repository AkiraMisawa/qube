using System.Globalization;
using System.Text.Json.Nodes;
using Qube.Composition.Application.Settings;

namespace Qube.Persistence.Sqlite;

/// <summary>
/// Users' settings (ADR-0017 §2) in the demonstrator's SQLite file, beside the saved items: one generic table, one row
/// per user and key, its value JSON the store never reads. A write replaces the row.
/// </summary>
public sealed class SqliteUserSettingStore : IUserSettingStore
{
    readonly SqliteFile file;

    /// <param name="path">The database file. It is created with its table when missing.</param>
    public SqliteUserSettingStore(string path)
    {
        file = new SqliteFile(path);
        using var connection = file.Open();
        SqliteFile.Execute(connection, Schema);
    }

    const string Schema = """
        CREATE TABLE IF NOT EXISTS UserSetting (
            user_id TEXT NOT NULL,
            key TEXT NOT NULL,
            value_json TEXT NOT NULL CHECK (json_valid(value_json)),
            updated_at TEXT NOT NULL,
            PRIMARY KEY (user_id, key)
        );
        """;

    public IReadOnlyList<StoredSetting> All(string user)
    {
        using var connection = file.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT key, value_json, updated_at FROM UserSetting WHERE user_id = $user";
        command.Parameters.AddWithValue("$user", user);
        using var reader = command.ExecuteReader();
        var settings = new List<StoredSetting>();
        while (reader.Read())
            settings.Add(new(reader.GetString(0),
                JsonNode.Parse(reader.GetString(1)) ?? throw new InvalidOperationException($"{user}'s setting {reader.GetString(0)} holds null, which no write keeps."),
                DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture)));
        return settings;
    }

    public void Put(string user, StoredSetting setting)
    {
        using var connection = file.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO UserSetting (user_id, key, value_json, updated_at) VALUES ($user, $key, $value, $at)
            ON CONFLICT (user_id, key) DO UPDATE SET value_json = excluded.value_json, updated_at = excluded.updated_at
            """;
        command.Parameters.AddWithValue("$user", user);
        command.Parameters.AddWithValue("$key", setting.Key);
        command.Parameters.AddWithValue("$value", setting.Value.ToJsonString());
        command.Parameters.AddWithValue("$at", setting.At.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    public bool Remove(string user, string key)
    {
        using var connection = file.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM UserSetting WHERE user_id = $user AND key = $key";
        command.Parameters.AddWithValue("$user", user);
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteNonQuery() > 0;
    }
}
