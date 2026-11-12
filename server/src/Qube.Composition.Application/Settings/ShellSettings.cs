using System.Text.Json.Nodes;

namespace Qube.Composition.Application.Settings;

/// <summary>
/// The settings of the workbench's own shell and grids (ADR-0017's first registrations): the theme, the command
/// ranking and column widths. Preferences all three: none changes a figure.
/// </summary>
public sealed class ShellSettings : ISettingCatalog
{
    public const string Theme = "shell.theme";
    public const string Usage = "commands.usage";
    public const string Widths = "grid.widths.*";

    /// <summary>The widest a dragged column is kept at; a wider one is no width a grid was dragged to.</summary>
    public const int WidestColumn = 10_000;

    public IEnumerable<SettingDefinition> Settings =>
    [
        new(Theme, "Theme", ["theme"], SettingKind.Preference,
            new FieldSpec(FieldForm.Choice, Choices: new Dictionary<string, string> { ["dark"] = "dark", ["light"] = "light", ["auto"] = "auto" }),
            JsonValue.Create("dark")),
        // None registered: a user who ran no command has no ranking.
        new(Usage, "Command ranking", ["command ranking", "ranking"], SettingKind.Preference, null, null, Ranking),
        new(Widths, "Column widths", ["widths"], SettingKind.Preference, null, null, ColumnWidths),
    ];

    // How often and how lately each command ran (ADR-0006 §6): by command id, a count of 1 or more and the last run in
    // milliseconds since 1970.
    static string? Ranking(JsonNode value) =>
        value is JsonObject uses && uses.All(u => u.Key.Length > 0 && u.Value is JsonObject use && use.Count == 2
            && use["count"] is JsonValue count && count.TryGetValue<long>(out var n) && n >= 1
            && use["last"] is JsonValue last && last.TryGetValue<double>(out var ms) && ms >= 0)
            ? null
            : "an object of command ids, each with a count of 1 or more and the time it last ran";

    // The widths a user dragged in one grid, by column name, in whole pixels.
    static string? ColumnWidths(JsonNode value) =>
        value is JsonObject widths && widths.All(w => w.Key.Length > 0 && w.Value is JsonValue px && px.TryGetValue<int>(out var n) && n is > 0 and <= WidestColumn)
            ? null
            : $"an object of column names, each with a whole width of 1 to {WidestColumn} pixels";
}
