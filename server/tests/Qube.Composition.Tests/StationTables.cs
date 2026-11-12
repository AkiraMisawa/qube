using System.Text.Json.Nodes;
using Qube.Composition.Application;
using Qube.Composition.Application.Products;
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
    public const string Sensors = "weather.sensors";

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
        // A second name that begins with a word of the table's own (`obs`): more words that name a field are the field.
        new("note", "Note", ["note", "obs note"], FieldForm.Id, Editable: true, Optional: true),
        new("windSpeed", "Wind speed", ["wind speed", "wind", "speed"], FieldForm.Number, Editable: true),
        new("windDirection", "Wind direction", ["wind direction", "wind"], FieldForm.Id, Editable: true),
        new("window", "Window", ["window"], FieldForm.Period),
        new("drift", "Drift", ["drift"], FieldForm.Rate, Editable: true, Optional: true),
    ], [Manual, Automatic], Main: true, Actions: [new("Mark checked", "set selected checked on")]);

    public static readonly TableSubset High = new("high", "High", ["high", "upland"]);
    // A subset of the stations with the name the readings' `manual` has, and not all of its words: `by hand` is the
    // readings' alone, whichever table is on screen.
    public static readonly TableSubset Staffed = new("manual", "Staffed", ["manual", "staffed"]);

    // The table answers to a word a reading's field has too (`site`), and the settings' alarm to one the volume has
    // (`limit`): a line that starts with either, with the readings on screen, cannot say which it means.
    public static readonly TableDescriptor SitesTable = new(Sites, "1.0", App, "Stations", ["stations", "site"], "id",
    [
        new("id", "Station", ["id"], FieldForm.Id),
        new("altitude", "Altitude", ["altitude"], FieldForm.Number, Editable: true),
        // A field that points into a table, here its own: the station that stands in for this one.
        new("backup", "Backup", ["backup"], FieldForm.Id, Editable: true, Optional: true, Table: Sites),
    ], [High, Staffed]);

    // A table under the stations (ADR-0016 note 46): a station's sensors, each keyed by its station and its own number,
    // and the station's load, a figure of them written with the unit it is in (note 47). Registered only where a test
    // asks for them (`Catalog(sensors: true)`), so that no other test's sentences list them.
    public static readonly TableDescriptor SensorsTable = new(Sensors, "1.0", App, "Sensors", ["sensors", "sensor"], "id",
    [
        new("id", "Sensor", ["id"], FieldForm.Id),
        new("station", "Station", ["station"], FieldForm.Id, Table: Sites),
        new("kind", "Kind", ["kind"], FieldForm.Id, Editable: true),
        new("load", "Load", ["load"], FieldForm.Number, Editable: true, UnitField: "unit"),
        new("unit", "Unit", ["unit"], FieldForm.Choice, Editable: true, Choices: Units),
    ], [], Under: Sites);

    public static readonly TableDescriptor SitesWithSensors = SitesTable with
    {
        Fields = [.. SitesTable.Fields, new("load", "Load", ["load"], FieldForm.Number, Editable: true, Unit: "M3", Figure: new(Sensors, new(FieldForm.Money, Choices: Units)))],
    };

    public static readonly TableDescriptor SettingsTable = new(Settings, "1.0", App, "Board settings", ["settings", "board"], "id",
    [
        new("id", "Settings", ["id"], FieldForm.Id),
        new("alarm", "Alarm volume", ["alarm volume", "alarm", "limit"], FieldForm.Amount, Editable: true),
        new("daylight", "Daylight", ["daylight"], FieldForm.Flag, Editable: true),
    ], [], SingleRow: true);

    sealed record Sent(string Id, string Subset, string Station, decimal Volume, string Unit, string Taken, bool Checked, string? Note, decimal WindSpeed, string WindDirection, string Window);

    // Two templates of the readings (ADR-0016 note 50), registered only for the tests that ask for them. A reading
    // gauged by hand takes its unit, alone or by name, and may take a drift; it writes the note before the drift, which
    // the board's own rule needs explained by one. A calm takes nothing.
    public static readonly TableTemplate Gauged = new("gauged", "Gauged by hand", ["gauged", "by gauge"],
        [new("unit", "Unit", FieldForm.Choice, Required: true, Unnamed: true, Choices: Units, Aliases: ["in"]), new("drift", "Drift", FieldForm.Rate)],
        ["unit", "checked", "note", "drift"]);

    public static readonly TableTemplate Calm = new("calm", "Calm", ["calm", "no wind"], [], ["windSpeed", "windDirection"]);

    public static readonly TableDescriptor ReadingsWithTemplates = ReadingsTable with { Templates = [Gauged, Calm] };

    // The board's one row of settings has a template too: a night's settings, which take nothing.
    public static readonly TableTemplate Night = new("night", "Night", ["night"], [], ["alarm", "daylight"]);
    public static readonly TableDescriptor SettingsWithTemplates = SettingsTable with { Templates = [Night] };

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
            KeyValuePair.Create("station:S1", new JsonObject { ["id"] = "S1", ["subset"] = "manual", ["altitude"] = 15m, ["backup"] = "N1" }),
            // A station that shares its id with a reading, so an id alone cannot say which table it means.
            KeyValuePair.Create("station:X1", new JsonObject { ["id"] = "X1", ["altitude"] = 40m, ["backup"] = null }),
            KeyValuePair.Create("sensor:N1/1", new JsonObject { ["id"] = "N1/1", ["station"] = "N1", ["kind"] = "rain", ["load"] = 40m, ["unit"] = "M3", ["out"] = false }),
            KeyValuePair.Create("sensor:N1/2", new JsonObject { ["id"] = "N1/2", ["station"] = "N1", ["kind"] = "wind", ["load"] = 25m, ["unit"] = "M3", ["out"] = false }),
            KeyValuePair.Create("sensor:S1/1", new JsonObject { ["id"] = "S1/1", ["station"] = "S1", ["kind"] = "rain", ["load"] = 10m, ["unit"] = "GAL", ["out"] = false }),
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

    sealed record Row(string Entity, string Key, string? Subset, IReadOnlyDictionary<string, JsonNode?> Values, string? Under = null) : ITableRow
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
        // A sensor is under its station.
        return new Row(entity, values["id"]!.GetValue<string>(), subset, values, entity.StartsWith("sensor:", StringComparison.Ordinal) ? values["station"]!.GetValue<string>() : null);
    }

    // A station's sensors as they stand: those sent that no edit took out, and those made on the board.
    static IReadOnlyList<ITableRow> SensorsOf(WorkspaceState state, string station) =>
        [.. RowsOf(state, "sensor").Where(r => r.Under == station && !r.Value("out")!.GetValue<bool>())];

    // A sensor's load in cubic metres: the unit a station's load is said in. A gallon here is four of them.
    static decimal InM3(ITableRow sensor) => sensor.Value("load")!.GetValue<decimal>() * (sensor.Value("unit")!.GetValue<string>() == "GAL" ? 4m : 1m);

    // A station with its load, the figure of its sensors (ADR-0016 note 47).
    static ITableRow WithLoad(WorkspaceState state, Row station) =>
        station with { Values = new Dictionary<string, JsonNode?>(station.Values) { ["load"] = SensorsOf(state, station.Key).Sum(InM3) } };

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
        public IReadOnlyList<ITableRow> Rows(WorkspaceState state) =>
            kind == "sensor" ? [.. RowsOf(state, kind).Where(r => !r.Value("out")!.GetValue<bool>())]
            : descriptor.Fields.Any(f => f.Figure is not null) ? [.. RowsOf(state, kind).Select(r => WithLoad(state, (Row)r))]
            : RowsOf(state, kind);

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

        // A station's load written (ADR-0016 note 47): the station is left one sensor of that load, in the unit written
        // or in cubic metres. A station that holds that one sensor alone is left as it is.
        IReadOnlyList<WorkspaceChange> SetLoad(WorkspaceState state, ITableRow station, JsonNode value)
        {
            var (load, unit) = (value["amount"]!.GetValue<decimal>(), value["unit"]?.GetValue<string>() ?? "M3");
            var held = SensorsOf(state, station.Key);
            if (held is [var one] && one.Value("load")!.GetValue<decimal>() == load && one.Value("unit")!.GetValue<string>() == unit) return [];
            var last = Entities.Keys.Concat(state.Created.Keys).Where(e => e.StartsWith($"sensor:{station.Key}/", StringComparison.Ordinal)).Select(e => int.Parse(e[(e.IndexOf('/') + 1)..])).DefaultIfEmpty(0).Max();
            var id = $"{station.Key}/{last + 1}";
            return [.. held.Select(s => TakenOut(state, s)),
                new WorkspaceChange.Create(Scope, $"sensor:{id}", new JsonObject { ["id"] = id, ["station"] = station.Key, ["kind"] = "all", ["load"] = load, ["unit"] = unit, ["out"] = false })];
        }

        public string? SetSays(WorkspaceState state, ITableRow row, TableField field, JsonNode? value) =>
            field.Figure is null ? null : $"{TableWords.Counted(SensorsOf(state, row.Key).Count, "sensor")} replaced by 1 sensor, {value!["unit"]?.GetValue<string>() ?? "M3"}";

        public IReadOnlyList<WorkspaceChange> Set(WorkspaceState state, ITableRow row, TableField field, JsonNode? value)
        {
            if (field.Figure is not null) return SetLoad(state, row, value!);
            // The domain's own rule, which no form says: a tank holds so much.
            if (field.Name == "volume" && value!.GetValue<decimal>() > 50_000_000m)
                throw new WorkspaceRefused("InvalidValue", $"{row.Key} holds at most 50mm.", [Target(((Row)row).Entity, field.Name)]);
            // A rule across two fields of a row, as a domain has them: a drift is explained by a note.
            if (field.Name == "drift" && value is not null && row.Value("note") is null)
                throw new WorkspaceRefused("InvalidValue", $"{row.Key} has no note, and a drift is explained by one.", [Target(((Row)row).Entity, field.Name)]);
            return [new WorkspaceChange.Set(Target(((Row)row).Entity, field.Name), value)];
        }

        public IReadOnlyDictionary<string, JsonNode?> TemplateValues(WorkspaceState state, TableTemplate template, IReadOnlyDictionary<string, JsonNode?> terms) => template.Name switch
        {
            // A drift not given is written as none: the template says the reading's drift either way.
            "gauged" => new Dictionary<string, JsonNode?> { ["unit"] = terms["unit"], ["checked"] = true, ["note"] = "gauged", ["drift"] = terms.TryGetValue("drift", out var drift) ? drift : null },
            "calm" => new Dictionary<string, JsonNode?> { ["windSpeed"] = 0m, ["windDirection"] = "none" },
            "night" => new Dictionary<string, JsonNode?> { ["alarm"] = 1_000_000m, ["daylight"] = false },
            _ => throw new InvalidOperationException($"The readings have no template {template.Name}."),
        };

        public IReadOnlyList<WorkspaceChange>? SetAll(WorkspaceState state, TableSubset subset, TableField field, JsonNode? value) =>
            kind == "reading" && field.Name == "checked" ? [new WorkspaceChange.Set(Target($"readings:{subset.Name}", field.Name), value)] : null;

        public IReadOnlyList<WorkspaceChange> Reset(WorkspaceState state, ITableRow row, TableField? field) =>
        [
            .. (field is null ? descriptor.Fields : [field]).Where(f => f.Figure is null).Select(f => Target(((Row)row).Entity, f.Name)).Where(state.Overrides.ContainsKey).Select(t => new WorkspaceChange.Reset(t)),
            // A station's load reset is its sensors as they were sent: every edit of theirs gone, and those made on the board with them.
            .. (field is null ? descriptor.Fields : [field]).Where(f => f.Figure is not null).SelectMany(_ => (IEnumerable<WorkspaceChange>)
            [
                .. state.Overrides.Keys.Where(t => t.Entity.StartsWith($"sensor:{row.Key}/", StringComparison.Ordinal)).OrderBy(t => t.Entity, StringComparer.Ordinal).ThenBy(t => t.Field, StringComparer.Ordinal)
                    .Select(t => new WorkspaceChange.Reset(t)),
                .. state.Created.Keys.Where(e => e.StartsWith($"sensor:{row.Key}/", StringComparison.Ordinal)).Order(StringComparer.Ordinal).Select(e => new WorkspaceChange.Remove(e)),
            ]),
        ];

        public IReadOnlyList<WorkspaceChange> ResetAll(WorkspaceState state, TableSubset subset, TableField? field) =>
            [.. (field is null ? descriptor.Fields : [field]).Select(f => Target($"readings:{subset.Name}", f.Name)).Where(state.Overrides.ContainsKey).Select(t => new WorkspaceChange.Reset(t))];

        // A sensor made on the board is removed; one that was sent is taken out by an edit of its own, which a reset or
        // an undo takes back.
        static WorkspaceChange TakenOut(WorkspaceState state, ITableRow sensor) =>
            state.Created.ContainsKey(((Row)sensor).Entity) ? new WorkspaceChange.Remove(((Row)sensor).Entity) : new WorkspaceChange.Set(Target(((Row)sensor).Entity, "out"), true);

        public IReadOnlyList<WorkspaceChange> Remove(WorkspaceState state, ITableRow row) =>
            state.Created.ContainsKey(((Row)row).Entity) ? [new WorkspaceChange.Remove(((Row)row).Entity)]
                : kind == "sensor" ? [TakenOut(state, row)]
                : throw new WorkspaceRefused("NotRemovable", $"{row.Key} is a reading a station sent, and stays; set {row.Key} checked off leaves it out.");
    }

    static readonly IReadOnlyDictionary<string, string> Kinds = new Dictionary<string, string> { ["rain"] = "rain", ["wind"] = "wind", ["all"] = "all" };

    /// <summary>
    /// A sensor as <c>new</c> makes one (ADR-0016 note 48): a described product whose entities are rows of the sensors,
    /// each made under the station its term <c>station</c> names.
    /// </summary>
    public static readonly ProductDescriptor SensorProduct = new("weather.sensor", "1.0", App, Scope, "sensor", "Sensor",
    [
        new("station", "Station", FieldForm.Id, Required: true),
        new("kind", "Kind", FieldForm.Choice, Required: true, Unnamed: true, Choices: Kinds),
        new("load", "Load", FieldForm.Number, Required: true, Unnamed: true),
        new("unit", "Unit", FieldForm.Choice, Required: true, Unnamed: true, Choices: Units),
    ], Table: Sensors, Under: "station");

    /// <summary>The sensor, or as <paramref name="sensor"/> describes it, for a description that is at fault.</summary>
    public sealed class Products(ProductDescriptor? sensor = null) : IProductCatalog
    {
        IEnumerable<ProductDescriptor> IProductCatalog.Products => [sensor ?? SensorProduct];
    }

    /// <summary>What the sensor's description cannot say: its station is one of the board's, and its number follows the station's last.</summary>
    public sealed class SensorRules(WorkspaceService service) : IProductRules
    {
        public string ProductId => SensorProduct.Id;

        public IReadOnlyDictionary<string, ProductDefault> Defaults(WorkspaceState workspace, IReadOnlyDictionary<string, JsonNode?> given) => new Dictionary<string, ProductDefault>();

        public string WouldCreate(WorkspaceState workspace, IReadOnlyDictionary<string, JsonNode?> terms)
        {
            var station = terms["station"]!.GetValue<string>();
            if (!Entities.ContainsKey($"station:{station}")) throw new ProductTermsRefused([new("UnknownStation", "station", $"No station {station}.")]);
            var last = Entities.Keys.Concat(workspace.Created.Keys).Where(e => e.StartsWith($"sensor:{station}/", StringComparison.Ordinal)).Select(e => int.Parse(e[(e.IndexOf('/') + 1)..])).DefaultIfEmpty(0).Max();
            return $"{station}/{last + 1}";
        }

        public (WorkspaceState State, string Entity) Create(string workspaceId, long? revision, string user, IReadOnlyDictionary<string, JsonNode?> terms, IReadOnlyDictionary<string, string> sources, RunOrigin origin)
        {
            var board = service.Find(workspaceId) ?? throw new WorkspaceRefused("UnknownWorkspace", $"No board {workspaceId}.");
            var (id, fields) = Made(board, terms);
            return (service.Create(workspaceId, revision ?? board.Revision, user, Scope, $"sensor:{id}", fields, origin), id);
        }

        // A sensor is made by one change, so a draft may hold its making (ADR-0016 note 54).
        public (IReadOnlyList<WorkspaceChange> Changes, string Entity)? Planned(WorkspaceState workspace, IReadOnlyDictionary<string, JsonNode?> terms, IReadOnlyDictionary<string, string> sources)
        {
            var (id, fields) = Made(workspace, terms);
            return ([new WorkspaceChange.Create(Scope, $"sensor:{id}", fields)], id);
        }

        (string Id, JsonObject Fields) Made(WorkspaceState board, IReadOnlyDictionary<string, JsonNode?> terms)
        {
            var id = WouldCreate(board, terms);
            return (id, new JsonObject { ["id"] = id, ["station"] = terms["station"]!.GetValue<string>(), ["kind"] = terms["kind"]!.GetValue<string>(), ["load"] = FieldForms.Held(terms["load"]!), ["unit"] = terms["unit"]!.GetValue<string>(), ["out"] = false });
        }
    }

    /// <summary>
    /// The three tables; <paramref name="stations"/> describes the stations another way, for a registration that is at
    /// fault; with <paramref name="sensors"/>, the sensors under the stations too, and each station's load, a figure of
    /// them, or the sensors as <paramref name="sensorsAs"/> describes them.
    /// </summary>
    public sealed class Catalog(TableDescriptor? stations = null, bool sensors = false, TableDescriptor? sensorsAs = null, TableDescriptor? readings = null,
        TableDescriptor? settings = null) : ITableCatalog
    {
        public IEnumerable<IWorkspaceTable> Tables =>
        [
            new Table(readings ?? ReadingsTable, "reading"), new Table(stations ?? (sensors ? SitesWithSensors : SitesTable), "station"), new Table(settings ?? SettingsTable, "settings"),
            .. sensors || sensorsAs is not null ? [new Table(sensorsAs ?? SensorsTable, "sensor")] : Array.Empty<IWorkspaceTable>(),
        ];
    }

    // The board's short forms (ADR-0016 note 29): `tick` marks a grid's readings as checked, the grid typed or the one
    // behind the line, and `alarm` is the settings' alarm volume. Each answers the kernel line it stands for.
    sealed class Tick : IShortForm
    {
        static readonly string[] Grids = ["manual", "automatic"];

        public Qube.Composition.Contracts.ShortFormDescriptor Descriptor { get; } =
            new("weather.tick", "Mark a grid's readings as checked", new(["tick"], [new("grid", Qube.Composition.Contracts.CommandArgumentKind.Text, false)], App));

        public string Expand(LineContext line)
        {
            var typed = line.Text.Trim();
            var grid = typed.Length > 0 ? typed : line.View?.Subset ?? throw new TableRefused("NoGrid", "tick takes manual or automatic, or the grid the keyboard is in.", null, Grids);
            return Grids.Contains(grid) ? $"set {grid} checked on" : throw new TableRefused("UnknownGrid", $"No readings {grid}: manual or automatic.", grid, Grids);
        }

        // No space after the form's own word: it is still being typed, and nothing follows yet.
        public LineCompletions Complete(LineContext line) => line.Text.Length == 0 ? LineCompletions.None
            : new([.. Grids.Where(g => g.StartsWith(line.Text.Trim(), StringComparison.OrdinalIgnoreCase)).Select(g => new LineCompletion(g, line.Text.Length - line.Text.TrimStart().Length))]);
    }

    sealed class Alarm : IShortForm
    {
        public Qube.Composition.Contracts.ShortFormDescriptor Descriptor { get; } =
            new("weather.alarm", "Set the volume the alarm sounds at", new(["alarm"], [new("volume", Qube.Composition.Contracts.CommandArgumentKind.Text, true)], App));

        public string Expand(LineContext line) => $"set alarm {line.Text.Trim()}";

        public LineCompletions Complete(LineContext line) => LineCompletions.None;
    }

    /// <summary>The board's two short forms; with <paramref name="more"/>, others to register beside them.</summary>
    public sealed class ShortForms(params IShortForm[] more) : IShortFormCatalog
    {
        IEnumerable<IShortForm> IShortFormCatalog.ShortForms => [new Tick(), new Alarm(), .. more];
    }

    public static WorkspaceService Service() => new(new Dictionary<string, IEditRules> { [App] = new Rules() }, TimeProvider.System);

    /// <summary>A board on 2026-11-11 with the six readings the stations sent.</summary>
    public static WorkspaceState Board(WorkspaceService service, string owner = "ana") =>
        service.Create(owner, App, new JsonObject { ["day"] = "2026-11-11" }, new Source());

    /// <summary>A reading made on the board itself, which a command may remove.</summary>
    public static WorkspaceState Made(WorkspaceService service, WorkspaceState board, string id, decimal volume = 100_000m, string? note = null) =>
        service.Create(board.WorkspaceId, board.Revision, board.Owner, Scope, $"reading:{id}", FieldsOf(new(id, "manual", "NORTH", volume, "M3", "2026-11-11", false, note, 0m, "N", "1d")));
}
