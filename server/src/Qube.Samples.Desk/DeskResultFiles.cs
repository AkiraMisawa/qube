using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Qube.Samples.Desk;

/// <summary>
/// Reads a result set from a directory of the engine's files (<c>xva.csv</c>, <c>risk.csv</c>, <c>trades.csv</c>)
/// with a <c>manifest.json</c> beside them (ADR-0019 §4, §7). A reference is a directory name under the root.
/// </summary>
public sealed class DeskResultFiles(string root) : IDeskResults
{
    const string ManifestFile = "manifest.json";
    static readonly string[] Files = [ManifestFile, "xva.csv", "risk.csv", "trades.csv"];

    public DeskResultSet Read(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference) || reference.Contains("..") || Path.IsPathRooted(reference))
            throw new DeskResultsRefused(DeskResultsRefusal.NotFound, $"'{reference}' is not a result set's reference.");
        var directory = Path.Combine(root, reference);
        if (!Directory.Exists(directory))
            throw new DeskResultsRefused(DeskResultsRefusal.NotFound, $"There is no result set '{reference}'.");

        var bytes = new Dictionary<string, byte[]>();
        foreach (var file in Files)
        {
            var path = Path.Combine(directory, file);
            if (!File.Exists(path))
                throw new DeskResultsRefused(DeskResultsRefusal.FileMissing, $"Result set '{reference}' has no {file}.");
            bytes[file] = File.ReadAllBytes(path);
        }

        var manifest = Manifest(reference, bytes[ManifestFile]);

        var values = Table.Parse("xva.csv", bytes["xva.csv"], ["scenario", "counterparty", "metric", "value", "std_error"])
            .Select(r => new DeskValue(r.Text(0), r.Text(1), r.Text(2), r.Number(3), r.Number(4))).ToList();
        var sensitivities = Table.Parse("risk.csv", bytes["risk.csv"],
                ["scenario", "counterparty", "metric", "kind", "input", "key", "sensitivity"])
            .Select(r => new DeskSensitivity(r.Text(0), r.Text(1), r.Text(2), r.Text(3), r.Text(4), r.Text(5), r.Number(6))).ToList();
        var trades = Table.Parse("trades.csv", bytes["trades.csv"],
                ["scenario", "trade", "counterparty", "netting_set", "pv0", "pv0_std_error"])
            .Select(r => new DeskTrade(r.Text(0), r.Text(1), r.Text(2), r.Text(3), r.Number(4), r.Number(5))).ToList();

        var counterparties = values.Select(v => v.Counterparty)
            .Concat(sensitivities.Select(s => s.Counterparty))
            .Concat(trades.Select(t => t.Counterparty))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(code => new DeskCounterparty(code, manifest.LegalEntities.TryGetValue(code, out var entity) ? entity : null))
            .ToList();

        // The input set's curves, where the result set carries them: curves/<name>.csv, discount factors by days.
        var curves = new Dictionary<string, DeskCurve>(StringComparer.Ordinal);
        var curveDirectory = Path.Combine(directory, CurveDirectory);
        if (Directory.Exists(curveDirectory))
            foreach (var path in Directory.GetFiles(curveDirectory, "*.csv").Order(StringComparer.Ordinal))
            {
                var file = $"{CurveDirectory}/{Path.GetFileName(path)}";
                bytes[file] = File.ReadAllBytes(path);
                var name = Path.GetFileNameWithoutExtension(path);
                curves[name] = new DeskCurve(name, [.. Table.Parse(file, bytes[file], ["tenor", "discount_factor"]).Select(r => Node(file, r))]);
            }
        foreach (var (currency, curve) in manifest.HedgeCurves)
            if (!curves.ContainsKey(curve))
                throw new DeskResultsRefused(DeskResultsRefusal.FileMissing,
                    $"Result set '{reference}' declares {curve} for hedges in {currency} and has no {CurveDirectory}/{curve}.csv.");

        return new DeskResultSet(reference, Hash(bytes), manifest, counterparties, values, sensitivities, trades) { Curves = curves };
    }

    const string CurveDirectory = "curves";

    static DeskCurveNode Node(string file, Table.Row row)
    {
        var key = row.Text(0);
        if (!key.EndsWith('d') || !int.TryParse(key.AsSpan(0, key.Length - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var days) || days <= 0)
            throw new DeskResultsRefused(DeskResultsRefusal.RowUnreadable, $"{file}: the tenor '{key}' is not a number of days such as 1826d.");
        var df = row.Number(1);
        return df > 0 ? new DeskCurveNode(key, days, df)
            : throw new DeskResultsRefused(DeskResultsRefusal.NumberUnreadable, $"{file}: the discount factor at {key} is {df}, which is not more than 0.");
    }

    static string Hash(Dictionary<string, byte[]> bytes)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in bytes.Keys.Order(StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(file + "\n"));
            hash.AppendData(SHA256.HashData(bytes[file]));
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    static DeskManifest Manifest(string reference, byte[] json)
    {
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(json);
            root = document.RootElement.Clone();
        }
        catch (JsonException e)
        {
            throw new DeskResultsRefused(DeskResultsRefusal.ManifestUnreadable,
                $"Result set '{reference}': {ManifestFile} is not readable JSON ({e.Message}).");
        }
        if (root.ValueKind != JsonValueKind.Object)
            throw new DeskResultsRefused(DeskResultsRefusal.ManifestUnreadable,
                $"Result set '{reference}': {ManifestFile} is not an object.");

        string Required(string name)
        {
            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(value.GetString()))
                return value.GetString()!;
            throw new DeskResultsRefused(DeskResultsRefusal.ManifestIncomplete,
                $"Result set '{reference}': {ManifestFile} does not state '{name}'.");
        }

        var engine = Required("engine");
        var commit = Required("engineCommit");
        var inputSet = Required("inputSet");
        var dateText = Required("valuationDate");
        if (!DateOnly.TryParseExact(dateText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            throw new DeskResultsRefused(DeskResultsRefusal.ManifestIncomplete,
                $"Result set '{reference}': {ManifestFile} states valuationDate '{dateText}', which is not a date (yyyy-MM-dd).");
        var baseScenario = Required("baseScenario");

        var entities = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root.TryGetProperty("counterparties", out var counterparties))
        {
            if (counterparties.ValueKind != JsonValueKind.Object)
                throw new DeskResultsRefused(DeskResultsRefusal.ManifestUnreadable,
                    $"Result set '{reference}': {ManifestFile}'s 'counterparties' is not an object.");
            foreach (var entry in counterparties.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Object
                    || !entry.Value.TryGetProperty("legalEntity", out var entity)
                    || entity.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(entity.GetString()))
                    throw new DeskResultsRefused(DeskResultsRefusal.ManifestUnreadable,
                        $"Result set '{reference}': {ManifestFile} names counterparty '{entry.Name}' without a legalEntity.");
                entities[entry.Name] = entity.GetString()!;
            }
        }

        var units = new Dictionary<string, SensitivityPer>(StringComparer.Ordinal);
        if (root.TryGetProperty("units", out var declared))
        {
            if (declared.ValueKind != JsonValueKind.Object)
                throw new DeskResultsRefused(DeskResultsRefusal.ManifestUnreadable,
                    $"Result set '{reference}': {ManifestFile}'s 'units' is not an object.");
            foreach (var entry in declared.EnumerateObject())
            {
                units[entry.Name] = entry.Value.ValueKind == JsonValueKind.String ? entry.Value.GetString() switch
                {
                    "rate" => SensitivityPer.Rate,
                    "level" => SensitivityPer.Level,
                    _ => throw UnknownUnit(reference, entry),
                } : throw UnknownUnit(reference, entry);
            }
        }

        var hedgeCurves = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root.TryGetProperty("hedgeCurves", out var hedges))
        {
            if (hedges.ValueKind != JsonValueKind.Object)
                throw new DeskResultsRefused(DeskResultsRefusal.ManifestUnreadable,
                    $"Result set '{reference}': {ManifestFile}'s 'hedgeCurves' is not an object.");
            foreach (var entry in hedges.EnumerateObject())
                hedgeCurves[entry.Name] = entry.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(entry.Value.GetString())
                    ? entry.Value.GetString()!
                    : throw new DeskResultsRefused(DeskResultsRefusal.ManifestUnreadable,
                        $"Result set '{reference}': {ManifestFile} names no curve for hedges in {entry.Name}.");
        }

        return new DeskManifest(engine, commit, inputSet, date, baseScenario, entities, units) { HedgeCurves = hedgeCurves };
    }

    static DeskResultsRefused UnknownUnit(string reference, JsonProperty entry) =>
        new(DeskResultsRefusal.UnitUnknown,
            $"Result set '{reference}': {ManifestFile} declares the unit of '{entry.Name}' as {entry.Value.GetRawText()}, which is neither \"rate\" nor \"level\".");

    /// <summary>A CSV file's rows, with the named columns found by the header and in the order asked for.</summary>
    sealed class Table
    {
        public sealed class Row(string file, int line, string[] names, string[] cells)
        {
            public string Text(int i) => cells[i];

            public double Number(int i) =>
                double.TryParse(cells[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
                    ? value
                    : throw new DeskResultsRefused(DeskResultsRefusal.NumberUnreadable,
                        $"{file} line {line}: '{names[i]}' is '{cells[i]}', which is not a number.");
        }

        public static IEnumerable<Row> Parse(string file, byte[] bytes, string[] columns)
        {
            var lines = Encoding.UTF8.GetString(bytes).Split('\n');
            var header = lines.Length > 0 ? Cells(file, 1, lines[0].TrimEnd('\r')) : [];
            var at = new int[columns.Length];
            for (var c = 0; c < columns.Length; c++)
            {
                at[c] = Array.IndexOf(header, columns[c]);
                if (at[c] < 0)
                    throw new DeskResultsRefused(DeskResultsRefusal.ColumnMissing, $"{file} has no column '{columns[c]}'.");
            }
            var rows = new List<Row>(lines.Length);
            for (var l = 1; l < lines.Length; l++)
            {
                var text = lines[l].TrimEnd('\r');
                if (text.Length == 0 && l == lines.Length - 1) break;
                var cells = Cells(file, l + 1, text);
                if (cells.Length != header.Length)
                    throw new DeskResultsRefused(DeskResultsRefusal.RowUnreadable,
                        $"{file} line {l + 1} has {cells.Length} cells where the header has {header.Length}.");
                rows.Add(new Row(file, l + 1, columns, [.. at.Select(i => cells[i])]));
            }
            return rows;
        }

        static string[] Cells(string file, int line, string text)
        {
            var cells = new List<string>();
            var cell = new StringBuilder();
            var quoted = false;
            for (var i = 0; i < text.Length; i++)
            {
                var ch = text[i];
                if (quoted)
                {
                    if (ch != '"') cell.Append(ch);
                    else if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                    else quoted = false;
                }
                else if (ch == '"' && cell.Length == 0) quoted = true;
                else if (ch == ',') { cells.Add(cell.ToString()); cell.Clear(); }
                else cell.Append(ch);
            }
            if (quoted)
                throw new DeskResultsRefused(DeskResultsRefusal.RowUnreadable, $"{file} line {line} ends inside a quoted cell.");
            cells.Add(cell.ToString());
            return [.. cells];
        }
    }
}
