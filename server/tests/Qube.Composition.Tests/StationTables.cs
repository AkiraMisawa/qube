using System.Text.Json.Nodes;
using Qube.Composition.Application;
using Qube.Composition.Application.Tables;
using Qube.Composition.Application.Workspaces;

namespace Qube.Composition.Tests;

// A registration with no financial word (as CO01), for ADR-0016's tables, selector and edit verbs: readings that
// stations send, the stations, and the settings of the board that shows them. Every name and number is made up.
static class Stations
{
    public const string App = "weather";
    public const string Scope = "board";
    public const string Readings = "weather.readings";
    public const string Sites = "weather.stations";
    public const string Settings = "weather.settings";

    static readonly IReadOnlyDictionary<string, string> Station = new Dictionary<string, string> { ["north"] = "NORTH", ["n"] = "NORTH", ["south"] = "SOUTH", ["east"] = "EAST" };
    static readonly IReadOnlyDictionary<string, string> Units = new Dictionary<string, string> { ["m3"] = "M3", ["gal"] = "GAL" };

    public static readonly TableSubset Manual = new("manual", "Manual", ["manual", "by hand"]);
    public static readonly TableSubset Automatic = new("automatic", "Automatic", ["automatic", "auto"]);

    public static readonly TableDescriptor ReadingsTable = new(Readings, "1.0", App, "Readings", ["readings", "obs"], "id",
    [
        new("id", "Reading", ["id"], FieldForm.Id),
        new("station", "Station", ["station", "site"], FieldForm.Choice, Choices: Station, Table: Sites),
        new("volume", "Volume", ["volume", "vol", "limit"], FieldForm.Amount, Editable: true, UnitField: "unit"),
        new("unit", "Unit", ["unit"], FieldForm.Choice, Editable: true, Choices: Units),
        new("taken", "Taken", ["taken", "date"], FieldForm.Date),
        new("checked", "Checked", ["checked", "ok"], FieldForm.Flag, Editable: true),
        new("note", "Note", ["note"], FieldForm.Id, Editable: true, Optional: true),
        new("windSpeed", "Wind speed", ["wind speed", "wind", "speed"], FieldForm.Number, Editable: true),
        new("windDirection", "Wind direction", ["wind direction", "wind"], FieldForm.Id, Editable: true),
        new("window", "Window", ["window"], FieldForm.Period),
        new("drift", "Drift", ["drift"], FieldForm.Rate, Editable: true, Optional: true),
    ], [Manual, Automatic], Main: true);

    public static readonly TableSubset High = new("high", "High", ["high", "upland"]);

    // The table answers to a word a reading's field has too (`site`), and the settings' alarm to one the volume has
    // (`limit`): a line that starts with either, with the readings on screen, cannot say which it means.
    public static readonly TableDescriptor SitesTable = new(Sites, "1.0", App, "Stations", ["stations", "site"], "id",
    [
        new("id", "Station", ["id"], FieldForm.Id),
        new("altitude", "Altitude", ["altitude"], FieldForm.Number, Editable: true),
        // A field that points into a table, here its own: the station that stands in for this one.
        new("backup", "Backup", ["backup"], FieldForm.Id, Editable: true, Optional: true, Table: Sites),
    ], [High]);

    public static readonly TableDescriptor SettingsTable = new(Settings, "1.0", App, "Board settings", ["settings", "board"], "id",
    [
        new("id", "Settings", ["id"], FieldForm.Id),
        new("alarm", "Alarm volume", ["alarm volume", "alarm", "limit"], FieldForm.Amount, Editable: true),
        new("daylight", "Daylight", ["daylight"], FieldForm.Flag, Editable: true),
    ], [], SingleRow: true);

    sealed record Sent(string Id, string Subset, string Station, decimal Volume, string Unit, string Taken, bool Checked, string? Note, decimal WindSpeed, string WindDirection, string Window);

    static readonly Sent[] Sends =
    [
        new("R1", "manual", "NORTH", 5_000_000m, "M3", "2026-11-01", false, null, 12m, "N", "1d"),
        new("R2", "manual", "NORTH", 250_000m, "M3", "2027-06-30", true, "gusty", 30m, "NE", "1w"),
        new("R3", "manual", "SOUTH", 12_000_000m, "GAL", "2031-12-01", false, null, 5m, "S", "1d"),
        new("R4", "automatic", "SOUTH", 1_000_000m, "GAL", "2026-12-15", false, null, 8m, "SW", "1m"),
        new("R5", "automatic", "EAST", 800_000m, "M3", "2029-01-01", true, null, 22m, "E", "1d"),
        new("X1", "automatic", "EAST", 2_000_000m, "M3", "2026-11-20", false, null, 3m, "E", "1d"),
    ];

    static JsonObject FieldsOf(Sent s) => new()
    {
        ["id"] = s.Id, ["subset"] = s.Subset, ["station"] = s.Station, ["volume"] = s.Volume, ["unit"] = s.Unit, ["taken"] = s.Taken, ["checked"] = s.Checked,
        ["note"] = s.Note, ["windSpeed"] = s.WindSpeed, ["windDirection"] = s.WindDirection, ["window"] = s.Window, ["drift"] = null,
    };

    static readonly IReadOnlyDictionary<string, JsonObject> Entities = Sends.ToDictionary(s => $"reading:{s.Id}", FieldsOf)
        .Concat([
            KeyValuePair.Create("station:N1", new JsonObject { ["id"] = "N1", ["subset"] = "high", ["altitude"] = 120m, ["backup"] = null }),
            KeyValuePair.Create("station:S1", new JsonObject { ["id"] = "S1", ["altitude"] = 15m, ["backup"] = "N1" }),
            // A station that shares its id with a reading, so an id alone cannot say which table it means.
            KeyValuePair.Create("station:X1", new JsonObject { ["id"] = "X1", ["altitude"] = 40m, ["backup"] = null }),
            KeyValuePair.Create("settings:board", new JsonObject { ["id"] = "board", ["alarm"] = 10_000_000m, ["daylight"] = true }),
            // A whole subset's field, set by one edit: it has no value of its own until one is made.
            KeyValuePair.Create("readings:manual", new JsonObject { ["checked"] = null }),
            KeyValuePair.Create("readings:automatic", new JsonObject { ["checked"] = null }),
        ]).ToDictionary();

    public sealed class Source : ISourceVersion
    {
        public string Version => "sent-1";
        public JsonObject Descriptor => new() { ["version"] = Version };
        public SourceValue? Read(string entity, string field) =>
            Entities.TryGetValue(entity, out var fields) && fields.TryGetPropertyValue(field, out var value) ? new SourceValue(value?.DeepClone()) : null;
    }

    public sealed class Rules : IEditRules
    {
        public string KindOf(EditTarget target) => "weather.reading";
        public IReadOnlyList<string> ConflictGroup(EditTarget target) => [target.Field];
        public string TitleOf(WorkspaceState state, EditTarget target) => $"{target.Entity[(target.Entity.IndexOf(':') + 1)..]} {target.Field}";
    }

    public static EditTarget Target(string entity, string field) => new(Scope, entity, field);

    sealed record Row(string Entity, string Key, string? Subset, IReadOnlyDictionary<string, JsonNode?> Values) : ITableRow
    {
        public JsonNode? Value(string field) => Values.TryGetValue(field, out var value) ? value : throw new InvalidOperationException($"{Key} has no {field}.");
    }

    // A row's values as they stand: the source's, or the made row's, under the edits, and a subset's edit where it is later than the row's own.
    static Row RowOf(WorkspaceState state, string entity, JsonObject fields)
    {
        var subset = fields["subset"]?.GetValue<string>();
        var values = fields.Where(f => f.Key != "subset").ToDictionary(f => f.Key, f =>
        {
            var own = state.Overrides.GetValueOrDefault(Target(entity, f.Key));
            var whole = subset is null ? null : state.Overrides.GetValueOrDefault(Target($"readings:{subset}", f.Key));
            var latest = own is null ? whole : whole is null || own.Seq > whole.Seq ? own : whole;
            return latest is null ? f.Value?.DeepClone() : latest.Value?.DeepClone();
        });
        return new Row(entity, values["id"]!.GetValue<string>(), subset, values);
    }

    static IReadOnlyList<ITableRow> RowsOf(WorkspaceState state, string kind) =>
    [
        .. Entities.Where(e => e.Key.StartsWith($"{kind}:", StringComparison.Ordinal)).Select(e => RowOf(state, e.Key, e.Value)),
        .. state.Created.Values.Where(c => c.Entity.StartsWith($"{kind}:", StringComparison.Ordinal)).OrderBy(c => c.Seq).Select(c => RowOf(state, c.Entity, c.Fields)),
    ];

    sealed class Table(TableDescriptor descriptor, string kind) : IWorkspaceTable
    {
        public string TableId => descriptor.TableId;
        public string App => Stations.App;
        public TableDescriptor Describe(WorkspaceState state) => descriptor;
        public IReadOnlyList<ITableRow> Rows(WorkspaceState state) => RowsOf(state, kind);

        // The readings have a reader that pages them in an order, as a grid's does; the other tables have none. Unsorted,
        // the order is the source's and no edit moves it; sorted, any edit may.
        public OrderedRows? InOrder(WorkspaceState state, string? subset, IReadOnlyList<ViewSort> sorts)
        {
            if (kind != "reading") return null;
            var rows = RowsOf(state, kind).Where(r => subset is null || r.Subset == subset);
            foreach (var sort in sorts.Reverse())
            {
                decimal Volume(ITableRow r) => sort.Column == "volume" ? r.Value("volume")!.GetValue<decimal>() : throw new WorkspaceRefused("UnknownColumn", $"Readings cannot be sorted by {sort.Column}.");
                rows = sort.Descending ? rows.OrderByDescending(Volume) : rows.OrderBy(Volume);
            }
            return new([.. rows], sorts.Count == 0 ? "sent" : $"sent/r{state.Revision}");
        }
        public DateOnly? AsOf(WorkspaceState state) => DateOnly.ParseExact(state.Context["day"]!.GetValue<string>(), "yyyy-MM-dd");

        public IReadOnlyList<WorkspaceChange> Set(WorkspaceState state, ITableRow row, TableField field, JsonNode? value)
        {
            // The domain's own rule, which no form says: a tank holds so much.
            if (field.Name == "volume" && value!.GetValue<decimal>() > 50_000_000m)
                throw new WorkspaceRefused("InvalidValue", $"{row.Key} holds at most 50mm.", [Target(((Row)row).Entity, field.Name)]);
            // A rule across two fields of a row, as a domain has them: a drift is explained by a note.
            if (field.Name == "drift" && value is not null && row.Value("note") is null)
                throw new WorkspaceRefused("InvalidValue", $"{row.Key} has no note, and a drift is explained by one.", [Target(((Row)row).Entity, field.Name)]);
            return [new WorkspaceChange.Set(Target(((Row)row).Entity, field.Name), value)];
        }

        public IReadOnlyList<WorkspaceChange>? SetAll(WorkspaceState state, TableSubset subset, TableField field, JsonNode? value) =>
            kind == "reading" && field.Name == "checked" ? [new WorkspaceChange.Set(Target($"readings:{subset.Name}", field.Name), value)] : null;

        public IReadOnlyList<WorkspaceChange> Reset(WorkspaceState state, ITableRow row, TableField? field) =>
            [.. (field is null ? descriptor.Fields : [field]).Select(f => Target(((Row)row).Entity, f.Name)).Where(state.Overrides.ContainsKey).Select(t => new WorkspaceChange.Reset(t))];

        public IReadOnlyList<WorkspaceChange> ResetAll(WorkspaceState state, TableSubset subset, TableField? field) =>
            [.. (field is null ? descriptor.Fields : [field]).Select(f => Target($"readings:{subset.Name}", f.Name)).Where(state.Overrides.ContainsKey).Select(t => new WorkspaceChange.Reset(t))];

        public IReadOnlyList<WorkspaceChange> Remove(WorkspaceState state, ITableRow row) =>
            state.Created.ContainsKey(((Row)row).Entity) ? [new WorkspaceChange.Remove(((Row)row).Entity)]
                : throw new WorkspaceRefused("NotRemovable", $"{row.Key} is a reading a station sent, and stays; set {row.Key} checked off leaves it out.");
    }

    public sealed class Catalog : ITableCatalog
    {
        public IEnumerable<IWorkspaceTable> Tables => [new Table(ReadingsTable, "reading"), new Table(SitesTable, "station"), new Table(SettingsTable, "settings")];
    }

    public static WorkspaceService Service() => new(new Dictionary<string, IEditRules> { [App] = new Rules() }, TimeProvider.System);

    /// <summary>A board on 2026-11-11 with the six readings the stations sent.</summary>
    public static WorkspaceState Board(WorkspaceService service, string owner = "ana") =>
        service.Create(owner, App, new JsonObject { ["day"] = "2026-11-11" }, new Source());

    /// <summary>A reading made on the board itself, which a command may remove.</summary>
    public static WorkspaceState Made(WorkspaceService service, WorkspaceState board, string id, decimal volume = 100_000m) =>
        service.Create(board.WorkspaceId, board.Revision, board.Owner, Scope, $"reading:{id}", FieldsOf(new(id, "manual", "NORTH", volume, "M3", "2026-11-11", false, null, 0m, "N", "1d")));
}
