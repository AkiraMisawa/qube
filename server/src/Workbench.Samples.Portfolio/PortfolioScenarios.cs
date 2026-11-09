using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Workbench.Composition.Application.Workspaces;

namespace Workbench.Samples.Portfolio;

// Scenarios (ADR-0010 §4 and its note of 2026-11-09): named sets of market-data shifts, priced beside the base market
// and crossed with every case. The catalogue, the shifts and the market they make are the portfolio's, shared by the
// measures that price it; every index and level here is synthetic.

/// <summary>
/// One index of the market-data catalogue: a curve the portfolio prices on (<c>IR</c>, a discount or a funding curve, in
/// its currency) or a currency's rate against JPY (<c>FX</c>, its spot).
/// </summary>
public sealed record RiskIndex(string Id, string Category, string Currency, string Kind);

/// <summary>
/// The synthetic market-data catalogue (ADR-0010 §4's note of 2026-11-09): the curves the portfolio prices on, whole or at
/// a tenor, shifted absolutely in bp; each currency against JPY, the bank's domestic currency, shifted relatively in %.
/// Credit joins with the Credit tab (Delivery step 5).
/// </summary>
public static class ScenarioCatalog
{
    public const string Ir = "IR";
    public const string Fx = "FX";
    public const string Absolute = "abs";
    public const string Relative = "rel";
    public const string Bp = "bp";
    public const string Percent = "%";
    public const string Discount = "discount";
    public const string Funding = "funding";
    public const string Spot = "spot";
    /// <summary>The currency every FX factor is quoted against: the bank's domestic currency.</summary>
    public const string Domestic = "JPY";

    /// <summary>The tenors a shift may name, shortest first.</summary>
    public static readonly IReadOnlyList<string> Tenors = ["1m", "3m", "6m", "1y", "2y", "5y", "10y", "30y"];

    static readonly IReadOnlyDictionary<string, string> CurveCurrency = new Dictionary<string, string>
    {
        ["jpy_ois"] = "JPY", ["usd_sofr"] = "USD", ["eur_estr"] = "EUR",
        ["jpy_funding"] = "JPY", ["usd_funding"] = "USD", ["eur_funding"] = "EUR",
    };

    /// <summary>Every index a shift may name: the portfolio's discount and funding curves, then each currency against JPY.</summary>
    public static readonly IReadOnlyList<RiskIndex> Indices =
    [
        .. PortfolioCatalog.Curves.Select(c => new RiskIndex(c, Ir, CurrencyOf(c), Discount)),
        .. PortfolioCatalog.FundingCurves.Select(c => new RiskIndex(c, Ir, CurrencyOf(c), Funding)),
        .. PortfolioMarket.Currencies.Where(c => c != Domestic).Select(c => new RiskIndex($"{c.ToLowerInvariant()}{Domestic.ToLowerInvariant()}", Fx, c, Spot)),
    ];

    static string CurrencyOf(string curve) =>
        CurveCurrency.TryGetValue(curve, out var currency) ? currency : throw new InvalidOperationException($"The catalogue has no currency for the curve {curve}.");

    /// <summary>The index a word names, case ignored, or null.</summary>
    public static RiskIndex? Find(string word) => Indices.SingleOrDefault(i => string.Equals(i.Id, word, StringComparison.OrdinalIgnoreCase));

    /// <summary>The tenor a word names, case ignored, or null.</summary>
    public static string? TenorOf(string word) => Tenors.SingleOrDefault(t => string.Equals(t, word, StringComparison.OrdinalIgnoreCase));

    /// <summary>How a category shifts: IR absolutely, FX relatively.</summary>
    public static string TypeOf(string category) => category == Ir ? Absolute : Relative;

    /// <summary>The unit a shift type's size is written in: bp absolute, % relative.</summary>
    public static string UnitOfType(string type) => type == Absolute ? Bp : Percent;

    public static double YearsOf(string tenor) => tenor[^1] switch
    {
        'm' => int.Parse(tenor[..^1], CultureInfo.InvariantCulture) / 12.0,
        'y' => int.Parse(tenor[..^1], CultureInfo.InvariantCulture),
        _ => throw new ArgumentException($"{tenor} is not a tenor of the catalogue."),
    };

    /// <summary>What the catalogue holds, as a refusal lists it.</summary>
    public static string Listed =>
        $"the indices are {string.Join(", ", Indices.Select(i => i.Id))}; a tenor is {string.Join(", ", Tenors)}; a size is written as +10bp or -5%";
}

/// <summary>
/// One shift, resolved: its category and index, the tenor it is at (none for the whole index), how it shifts and by how
/// much, in its type's unit (bp absolute, % relative).
/// </summary>
public sealed record Shift(string Category, string Index, string? Tenor, string Type, decimal Size)
{
    public string Unit => ScenarioCatalog.UnitOfType(Type);

    /// <summary>The risk factor it moves as the grid shows it: its tenor, or <c>all</c> for the whole index.</summary>
    public string Factor => Tenor ?? "all";

    /// <summary>As written, the way a scenario is named from its shifts (<c>jpy_ois 5y +3bp</c>).</summary>
    public string Text => $"{Index}{(Tenor is null ? "" : $" {Tenor}")} {Signed(Size, Unit)}";

    /// <summary>Whether two shifts touch one risk factor: one index, and a tenor of one inside the other's (ADR-0010 §4).</summary>
    public bool Touches(Shift other) => Index == other.Index && (Tenor is null || other.Tenor is null || Tenor == other.Tenor);

    /// <summary>The risk factor two touching shifts share, as a refusal names it.</summary>
    public string Shared(Shift other) => Tenor is null ? other.Tenor is null ? Index : $"{Index} {other.Tenor}" : $"{Index} {Tenor}";

    public static string Signed(decimal size, string unit) =>
        $"{(size > 0 ? "+" : "")}{size.ToString("0.############", CultureInfo.InvariantCulture)}{unit}";
}

/// <summary>
/// Reads a shift as written (ADR-0010 §4): its words in any order, each recognised by its form, as a trade's are
/// (ADR-0008). The index gives the category; a tenor narrows it to that point; the size's unit gives the shift type, and
/// <c>abs</c> or <c>rel</c> says it where the size has no unit. A word that is none of these, or one given twice, is
/// refused with what it could be, never guessed.
/// </summary>
public static partial class ShiftReader
{
    [GeneratedRegex(@"^([+-]?)(\d+(?:\.\d+)?)(bp|%)?$", RegexOptions.IgnoreCase)]
    private static partial Regex Size();

    /// <summary>One shift; a size of 0 is read, for a generator's list, and is no shift.</summary>
    public static Shift Read(string text)
    {
        var written = text.Trim();
        if (written.Length == 0) throw Refused(written, "is empty: a shift is an index, a tenor if one point, and a size, such as jpy_ois 5y +3bp");
        RiskIndex? index = null;
        string? tenor = null, type = null, unit = null;
        decimal? size = null;
        foreach (var word in written.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (ScenarioCatalog.Find(word) is { } found)
            {
                if (index is not null) throw Refused(written, $"names two indices, {index.Id} and {found.Id}: a shift moves one");
                index = found;
            }
            else if (ScenarioCatalog.TenorOf(word) is { } point)
            {
                if (tenor is not null) throw Refused(written, $"names two tenors, {tenor} and {point}: a shift is at one point or the whole index");
                tenor = point;
            }
            else if (word.ToLowerInvariant() is ScenarioCatalog.Absolute or ScenarioCatalog.Relative)
            {
                if (type is not null) throw Refused(written, "names its shift type twice");
                type = word.ToLowerInvariant();
            }
            else if (Size().Match(word) is { Success: true } match)
            {
                if (size is not null) throw Refused(written, "has two sizes: a shift moves its index by one");
                var magnitude = decimal.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                size = match.Groups[1].Value == "-" ? -magnitude : magnitude;
                unit = match.Groups[3].Success ? match.Groups[3].Value.ToLowerInvariant() : null;
            }
            else throw Refused(written, $"has {word}, which is no index, tenor or size: {ScenarioCatalog.Listed}");
        }
        if (index is null) throw Refused(written, $"names no index: {ScenarioCatalog.Listed}");
        if (size is not { } value) throw Refused(written, $"has no size: write it as {Example(index)}");
        if (index.Category == ScenarioCatalog.Fx && tenor is not null) throw Refused(written, $"{index.Id} is a spot rate and has no tenor");
        var byUnit = unit switch { ScenarioCatalog.Bp => ScenarioCatalog.Absolute, ScenarioCatalog.Percent => ScenarioCatalog.Relative, _ => null };
        if (byUnit is not null && type is not null && byUnit != type)
            throw Refused(written, $"is in {unit}, which is not {type}: leave {type} out, or write the size's unit as {ScenarioCatalog.UnitOfType(type)}");
        var shiftType = byUnit ?? type ?? (value == 0 ? ScenarioCatalog.TypeOf(index.Category)
            : throw Refused(written, $"has a size with no unit, and the unit says how it shifts: write {Example(index)}"));
        if (shiftType != ScenarioCatalog.TypeOf(index.Category))
            throw Refused(written, $"shifts {index.Id} {(shiftType == ScenarioCatalog.Absolute ? "absolutely" : "relatively")}, and an {index.Category} shift is " +
                $"{(index.Category == ScenarioCatalog.Ir ? "absolute, in bp" : "relative, in %")}: write {Example(index)}");
        return new Shift(index.Category, index.Id, tenor, shiftType, value);
    }

    /// <summary>Every shift of a scenario, separated by semicolons (<c>jpy_ois +10bp; usdjpy -5%</c>), none of them 0.</summary>
    public static IReadOnlyList<Shift> ReadAll(string text)
    {
        var pieces = text.Split(';').Select(p => p.Trim()).ToList();
        // A semicolon may end the list; one between two shifts with nothing in it is a shift left out.
        if (pieces.Count > 1 && pieces[^1].Length == 0) pieces.RemoveAt(pieces.Count - 1);
        if (pieces.Count == 0 || pieces.All(p => p.Length == 0))
            throw new WorkspaceRefused("InvalidShift", "A scenario takes one shift or more, separated by semicolons, such as jpy_ois +10bp; usdjpy -5%.");
        if (pieces.Any(p => p.Length == 0))
            throw new WorkspaceRefused("InvalidShift", "A shift between two semicolons is empty.");
        var shifts = pieces.Select(Read).ToList();
        if (shifts.FirstOrDefault(s => s.Size == 0) is { } zero)
            throw new WorkspaceRefused("InvalidShift", $"{zero.Text} shifts nothing: leave it out, since Price prices the base market anyway.");
        return shifts;
    }

    static string Example(RiskIndex index) => index.Category == ScenarioCatalog.Ir ? $"{index.Id} +10bp" : $"{index.Id} -5%";

    static WorkspaceRefused Refused(string text, string why) => new("InvalidShift", $"The shift {(text.Length == 0 ? "" : $"{text} ")}{why}.");
}

/// <summary>
/// A shift row of the Scenarios grid (ADR-0010 §4): the scenario it belongs to, its index with the category it gives, the
/// tenor and the risk factor it names (the tenor, <c>all</c> for a whole curve, <c>spot</c> for an FX rate), the size
/// with the shift type its unit gives, and a description, each as the workspace holds it, and why the row cannot be
/// priced, where it cannot, with the column that says it. <paramref name="Row"/> counts from 1, in the order the rows
/// were made.
/// </summary>
public sealed record ShiftRow(string Id, int Row, string? Scenario, string? Category, string? Index, string? Tenor, string? Factor, string? Type,
    decimal? Size, string? SizeText, string? Description, string? Issue, string? IssueColumn)
{
    /// <summary>The shift the row makes, or null where it has an issue.</summary>
    public Shift? Shift => Issue is null ? new Shift(Category!, Index!, Tenor, Type!, Size!.Value) : null;
}

/// <summary>A scenario as the list beside the grid shows it: its name, its shift count, whether Price prices it, and why
/// it cannot be priced, where it cannot.</summary>
public sealed record ScenarioView(string Name, int Shifts, bool Included, string? Issue);

/// <summary>
/// An edit of the Scenarios grid's row (ADR-0010 §4): its cells as typed, by column, for a row that exists (<c>Id</c>) or a
/// blank one, which the edit makes. <c>Row</c> is where the grid shows it, counted from 1, as a refusal names it.
/// </summary>
public sealed record ShiftEdit(string? Id, int Row, IReadOnlyDictionary<string, string?> Cells);

/// <summary>A scenario as Price freezes it: its id in the Run (<c>base</c>, <c>s1</c>, …), its title and its shifts.</summary>
public sealed record PricedScenario(string Id, string Title, IReadOnlyList<Shift> Shifts);

/// <summary>What a scenario command made: the scenarios it made, and those of the same name it replaced.</summary>
public sealed record ScenarioAnswer(WorkspaceState State, IReadOnlyList<string> Made, IReadOnlyList<string> Replaced);

/// <summary>
/// The workspace's scenarios (ADR-0010 §4's note of 2026-11-09): each shift a created entity, <c>shift:&lt;id&gt;</c>, in
/// the workspace's own scope, so a counterparty change keeps it; the scenarios left out of Price are one created entity,
/// <c>scenarios:workspace</c>, holding their names. Rows sharing a name, case ignored, are one scenario.
/// </summary>
public static partial class PortfolioScenarios
{
    [GeneratedRegex(@"^([+-]?)(\d+(?:\.\d+)?)(bp|%)?$", RegexOptions.IgnoreCase)]
    private static partial Regex GridSize();

    public const string ShiftKind = "shift";
    public const string ScenariosKind = "scenarios";
    public static readonly string Inclusion = Entity.Of(ScenariosKind, Entity.WorkspaceId);
    public const string Excluded = "excluded";

    /// <summary>The base market's id and title in a Run: Price always prices it (ADR-0010 §4).</summary>
    public const string BaseId = "base";
    public const string BaseTitle = "Base market";

    /// <summary>The fields a shift row holds, as its edits name them.</summary>
    public static readonly IReadOnlyList<string> RowFields = ["scenario", "index", "tenor", "size", "unit", "description"];

    /// <summary>The words a scenario command begins with, which no scenario may be named, or the command line could not tell them apart.</summary>
    public static readonly IReadOnlyList<string> Reserved = ["include", "exclude", "remove", "duplicate", "ladder", "cross", "save", "load"];

    /// <summary>The Scenarios grid's columns, as an edit names them: Category and Shift type follow the index and the size.</summary>
    public static readonly IReadOnlyList<string> Columns = ["scenario", "category", "index", "factor", "type", "size", "description"];

    /// <summary>The shift rows, in the order they were made, each read with its edits and checked.</summary>
    public static IReadOnlyList<ShiftRow> Rows(WorkspaceState state) =>
        [.. Raw(state).Select((r, i) => RowOf(Entity.Split(r.Entity).Id, i + 1, r.Fields))];

    /// <summary>Each shift row's entity and its fields with their edits, in the order the rows were made.</summary>
    static IReadOnlyList<(string Entity, JsonObject Fields)> Raw(WorkspaceState state)
    {
        var edits = state.Overrides.Values.Where(o => Entity.Split(o.Target.Entity).Kind == ShiftKind)
            .ToDictionary(o => (o.Target.Entity, o.Target.Field), o => o.Value);
        return [.. state.Created.Values.Where(c => Entity.Split(c.Entity).Kind == ShiftKind).OrderBy(c => c.Seq).Select(c =>
        {
            var fields = new JsonObject();
            foreach (var field in RowFields)
                fields[field] = (edits.TryGetValue((c.Entity, field), out var edited) ? edited : c.Fields[field])?.DeepClone();
            return (c.Entity, fields);
        })];
    }

    static ShiftRow RowOf(string id, int row, JsonObject fields)
    {
        string? Text(string field) => fields[field] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;
        var name = Text("scenario");
        var index = Text("index") is { } named ? ScenarioCatalog.Find(named) : null;
        var tenor = Text("tenor");
        var size = fields["size"] is JsonValue n && n.TryGetValue<decimal>(out var d) ? d : (decimal?)null;
        var unit = Text("unit");
        var type = unit switch { ScenarioCatalog.Bp => ScenarioCatalog.Absolute, ScenarioCatalog.Percent => ScenarioCatalog.Relative, _ => null };
        var (issue, column) = name is null ? ("it names no scenario", "scenario")
            : index is null ? ("it has no index", "index")
            : index.Category == ScenarioCatalog.Fx && tenor is not null ? ($"{index.Id} is a spot rate and has no tenor", "factor")
            : size is null || type is null ? ("it has no size", "size")
            : size == 0 ? ("a size of 0 shifts nothing", "size")
            : type != ScenarioCatalog.TypeOf(index.Category)
                ? ($"an {index.Category} shift is {(index.Category == ScenarioCatalog.Ir ? "in bp" : "in %")}, and its size is in {unit}: type the size again", "size")
            : ((string?)null, (string?)null);
        var factor = index is null ? tenor : tenor ?? (index.Category == ScenarioCatalog.Fx ? ScenarioCatalog.Spot : WholeIndex);
        return new ShiftRow(id, row, name, index?.Category, index?.Id, tenor, factor, type, size,
            size is { } s && unit is not null ? Shift.Signed(s, unit) : null, Text("description"), issue, column);
    }

    /// <summary>The risk factor a shift of a whole curve names in the grid.</summary>
    public const string WholeIndex = "all";

    /// <summary>
    /// The grid's edits as one batch (ADR-0010 §4): a cell typed, a block pasted, or cells cleared. Each cell is read as
    /// its column reads it and refused, naming its row and column, where it does not resolve; one refused refuses the
    /// edit whole. A blank row's edit makes a shift, and a row left with no cell is removed. Category and Shift type
    /// follow the index and the size: a value there is taken only where it is what they give.
    /// </summary>
    public static IReadOnlyList<WorkspaceChange> Edit(WorkspaceState state, IReadOnlyList<ShiftEdit> edits, long revision)
    {
        var raw = Raw(state).ToDictionary(r => Entity.Split(r.Entity).Id, r => r.Fields);
        var changes = new List<WorkspaceChange>();
        foreach (var edit in edits)
        {
            var unknown = edit.Cells.Keys.FirstOrDefault(c => !Columns.Contains(c));
            if (unknown is not null) throw new WorkspaceRefused("InvalidShift", $"The Scenarios grid has no column {unknown}; its columns are {string.Join(", ", Columns)}.");
            var before = edit.Id is null ? Blank()
                : raw.GetValueOrDefault(edit.Id) ?? throw new WorkspaceRefused("UnknownShift", $"Row {edit.Row} is no longer a shift of this workspace. Look at the grid again.");
            var after = Cells(before, edit);
            var empty = RowFields.All(f => after[f] is null);
            if (edit.Id is null)
            {
                if (!empty) changes.Add(new WorkspaceChange.Create(PortfolioEditRules.WorkspaceScope, Entity.Of(ShiftKind, $"s{revision + 1 + changes.Count}"), after));
                continue;
            }
            var entity = Entity.Of(ShiftKind, edit.Id);
            if (empty) { changes.Add(new WorkspaceChange.Remove(entity)); continue; }
            foreach (var field in RowFields.Where(f => !JsonNode.DeepEquals(before[f], after[f])))
                changes.Add(new WorkspaceChange.Set(new EditTarget(PortfolioEditRules.WorkspaceScope, entity, field), after[field]?.DeepClone()));
        }
        return changes;
    }

    static JsonObject Blank() => new() { ["scenario"] = null, ["index"] = null, ["tenor"] = null, ["size"] = null, ["unit"] = null, ["description"] = null };

    // One row's fields once its cells are read: the index first, since Category, the risk factor and the size are read
    // against the index the row will have.
    static JsonObject Cells(JsonObject before, ShiftEdit edit)
    {
        var after = (JsonObject)before.DeepClone();
        string? Typed(string column) => edit.Cells.TryGetValue(column, out var text) && text?.Trim() is { Length: > 0 } t ? t : null;
        WorkspaceRefused Refused(string column, string why) =>
            new("InvalidShift", $"Row {edit.Row}, {Title(column)}: {why}.", [new EditTarget(PortfolioEditRules.WorkspaceScope, edit.Id is null ? $"{ShiftKind}:new" : Entity.Of(ShiftKind, edit.Id), column)]);
        if (edit.Cells.ContainsKey("index"))
            after["index"] = Typed("index") is { } word
                ? (ScenarioCatalog.Find(word) ?? throw Refused("index", $"{word} is no index: the indices are {string.Join(", ", ScenarioCatalog.Indices.Select(i => i.Id))}")).Id
                : null;
        var index = after["index"]?.GetValue<string>() is { } id ? ScenarioCatalog.Find(id) : null;
        if (edit.Cells.ContainsKey("scenario"))
            after["scenario"] = Typed("scenario") is { } name ? NameIssue(name) is { } issue ? throw Refused("scenario", issue.TrimEnd('.')) : name : null;
        if (Typed("category") is { } category && !string.Equals(category, index?.Category, StringComparison.OrdinalIgnoreCase))
            throw Refused("category", index is null
                ? $"{category} is no category of the row: the category follows the index, and the row has none"
                : $"{index.Id} is {index.Category}, not {category}: the category follows the index");
        if (edit.Cells.ContainsKey("factor"))
        {
            var word = Typed("factor");
            after["tenor"] = word is null || string.Equals(word, WholeIndex, StringComparison.OrdinalIgnoreCase)
                    || (string.Equals(word, ScenarioCatalog.Spot, StringComparison.OrdinalIgnoreCase) && index?.Category == ScenarioCatalog.Fx) ? null
                : index?.Category == ScenarioCatalog.Fx ? throw Refused("factor", $"{index.Id} is a spot rate and has no tenor: leave it spot")
                : ScenarioCatalog.TenorOf(word) ?? throw Refused("factor", $"{word} is no tenor: a tenor is {string.Join(", ", ScenarioCatalog.Tenors)}, or {WholeIndex} for the whole curve");
        }
        if (edit.Cells.ContainsKey("size"))
        {
            if (Typed("size") is not { } written) { after["size"] = null; after["unit"] = null; }
            else
            {
                var (size, unit) = SizeOf(written, index) ?? throw Refused("size", index is null
                    ? $"{written} is no size with a unit: write +10bp for a curve or -5% for an FX rate"
                    : $"{written} is no size: write it as {(index.Category == ScenarioCatalog.Ir ? "+10bp" : "-5%")}");
                var takes = ScenarioCatalog.UnitOfType(ScenarioCatalog.TypeOf(index?.Category ?? (unit == ScenarioCatalog.Bp ? ScenarioCatalog.Ir : ScenarioCatalog.Fx)));
                if (index is not null && unit != takes)
                    throw Refused("size", $"an {index.Category} shift is {(index.Category == ScenarioCatalog.Ir ? "absolute, in bp" : "relative, in %")}: write {Shift.Signed(size, takes)}");
                after["size"] = size;
                after["unit"] = unit;
            }
        }
        var unitNow = after["unit"]?.GetValue<string>();
        if (Typed("type") is { } type && !string.Equals(type, unitNow is null ? null : unitNow == ScenarioCatalog.Bp ? ScenarioCatalog.Absolute : ScenarioCatalog.Relative, StringComparison.OrdinalIgnoreCase))
            throw Refused("type", unitNow is null
                ? $"{type} is no shift type of the row: the shift type follows the size's unit, and the row has no size"
                : $"the size is in {unitNow}, so the shift is {(unitNow == ScenarioCatalog.Bp ? ScenarioCatalog.Absolute : ScenarioCatalog.Relative)}, not {type}: the shift type follows the size");
        if (edit.Cells.ContainsKey("description"))
            after["description"] = Typed("description");
        return after;
    }

    // A size as the grid takes it: a number with its unit, or a bare number in the unit the row's index takes.
    static (decimal Size, string Unit)? SizeOf(string written, RiskIndex? index)
    {
        var match = GridSize().Match(written.Replace(" ", ""));
        if (!match.Success) return null;
        var magnitude = decimal.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        var unit = match.Groups[3].Success ? match.Groups[3].Value.ToLowerInvariant()
            : index is null ? null : ScenarioCatalog.UnitOfType(ScenarioCatalog.TypeOf(index.Category));
        return unit is null ? null : (match.Groups[1].Value == "-" ? -magnitude : magnitude, unit);
    }

    static string Title(string column) => column switch
    {
        "scenario" => "Scenario", "category" => "Category", "index" => "Index", "factor" => "Risk factor",
        "type" => "Shift type", "size" => "Size", "description" => "Description", _ => column,
    };

    /// <summary>
    /// <c>scenario include|exclude &lt;name&gt;</c>: whether Price prices the scenario, as its box says; its rows stay. None
    /// where it already stands so.
    /// </summary>
    public static IReadOnlyList<WorkspaceChange> Include(WorkspaceState state, string name, bool included)
    {
        var scenario = Existing(state, name);
        var excluded = ExcludedNames(state);
        var changed = included ? excluded.Remove(scenario) : excluded.Add(scenario);
        return changed ? [Leaving(state, excluded)] : [];
    }

    /// <summary><c>scenario remove &lt;name&gt;</c>: the scenario's rows, in one batch; it is no longer left out of anything.</summary>
    public static IReadOnlyList<WorkspaceChange> Remove(WorkspaceState state, string name)
    {
        var scenario = Existing(state, name);
        var changes = Rows(state).Where(r => Same(r.Scenario, scenario)).Select(r => (WorkspaceChange)new WorkspaceChange.Remove(Entity.Of(ShiftKind, r.Id))).ToList();
        var excluded = ExcludedNames(state);
        if (excluded.Remove(scenario)) changes.Add(Leaving(state, excluded));
        return changes;
    }

    /// <summary><c>scenarios clear</c>: every shift row; none is left out of anything. None where there is no scenario.</summary>
    public static IReadOnlyList<WorkspaceChange> Clear(WorkspaceState state)
    {
        var changes = Rows(state).Select(r => (WorkspaceChange)new WorkspaceChange.Remove(Entity.Of(ShiftKind, r.Id))).ToList();
        if (ExcludedNames(state).Count > 0) changes.Add(Leaving(state, new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
        return changes;
    }

    /// <summary>
    /// <c>scenario duplicate &lt;name&gt; &lt;new name&gt;</c>: the scenario's rows copied under a new name, as they stand, to be
    /// edited; the copy is included. A name a scenario has is refused, so a copy never joins another scenario.
    /// </summary>
    public static IReadOnlyList<WorkspaceChange> Duplicate(WorkspaceState state, string name, string copy, long revision)
    {
        var scenario = Existing(state, name);
        var named = Checked(copy);
        if (All(state).FirstOrDefault(s => Same(s.Name, named)) is { } taken)
            throw new WorkspaceRefused("InvalidScenario", $"A scenario {taken.Name} exists: name the copy otherwise, or remove {taken.Name} first.");
        var changes = new List<WorkspaceChange>();
        foreach (var (_, fields) in Raw(state).Where(r => Same(r.Fields["scenario"]?.GetValue<string>(), scenario)))
        {
            var copied = (JsonObject)fields.DeepClone();
            copied["scenario"] = named;
            changes.Add(new WorkspaceChange.Create(PortfolioEditRules.WorkspaceScope, Entity.Of(ShiftKind, $"s{revision + 1 + changes.Count}"), copied));
        }
        var excluded = ExcludedNames(state);
        if (excluded.Remove(named)) changes.Add(Leaving(state, excluded));
        return changes;
    }

    // A scenario named, by its name as the workspace holds it; one it does not hold is refused with those it does.
    static string Existing(WorkspaceState state, string name)
    {
        var all = All(state);
        return all.FirstOrDefault(s => Same(s.Name, name.Trim()))?.Name ?? throw new WorkspaceRefused("UnknownScenario", all.Count == 0
            ? $"There is no scenario {name.Trim()}: the workspace has none."
            : $"There is no scenario {name.Trim()}: the scenarios are {string.Join(", ", all.Select(s => s.Name))}.");
    }

    // The names left out of Price, as one created entity of the workspace, made with the first.
    static WorkspaceChange Leaving(WorkspaceState state, IEnumerable<string> excluded)
    {
        var names = new JsonArray([.. excluded.Order(StringComparer.OrdinalIgnoreCase).Select(n => (JsonNode?)JsonValue.Create(n))]);
        return state.Created.ContainsKey(Inclusion)
            ? new WorkspaceChange.Set(new EditTarget(PortfolioEditRules.WorkspaceScope, Inclusion, Excluded), names)
            : new WorkspaceChange.Create(PortfolioEditRules.WorkspaceScope, Inclusion, new JsonObject { [Excluded] = names });
    }

    /// <summary>The scenarios, in the order of their first rows, with their shift counts, whether each is included, and the
    /// first reason it cannot be priced: a row's issue, or two shifts touching one risk factor.</summary>
    public static IReadOnlyList<ScenarioView> All(WorkspaceState state)
    {
        var excluded = ExcludedNames(state);
        return [.. Named(Rows(state)).Select(g => new ScenarioView(g.Name, g.Rows.Count, !excluded.Contains(g.Name), Issue(g.Name, g.Rows)))];
    }

    /// <summary>
    /// The scenarios Price prices, the base market first (ADR-0010 §4): every included one, checked. An included scenario
    /// that holds a row it cannot price, or two shifts touching one risk factor, refuses Price by name.
    /// </summary>
    public static IReadOnlyList<PricedScenario> Priced(WorkspaceState state)
    {
        var excluded = ExcludedNames(state);
        var included = Named(Rows(state)).Where(g => !excluded.Contains(g.Name)).ToList();
        foreach (var (name, rows) in included)
        {
            if (rows.FirstOrDefault(r => r.Issue is not null) is { } invalid)
                throw new WorkspaceRefused("ScenarioInvalid", $"Scenario {name} cannot be priced: row {invalid.Row} {invalid.Issue}. Complete the row, or exclude the scenario.");
            if (Twice(rows) is { } twice)
                throw new WorkspaceRefused("RiskFactorShiftedTwice", $"Scenario {name} shifts {twice}. Keep one of them, or exclude the scenario.");
        }
        return [new PricedScenario(BaseId, BaseTitle, []), .. included.Select((g, i) => new PricedScenario($"s{i + 1}", g.Name, [.. g.Rows.Select(r => r.Shift!)]))];
    }

    /// <summary>
    /// <c>scenario &lt;name&gt; &lt;shift&gt;; …</c> (ADR-0010 §4): the scenario whole, replacing the rows of one of that name and
    /// keeping whether it is included. The rows are named by the log entries that make them, so no id is given twice.
    /// </summary>
    public static (IReadOnlyList<WorkspaceChange> Changes, bool Replaced) Define(WorkspaceState state, string name, IReadOnlyList<Shift> shifts, long revision)
    {
        var named = Checked(name);
        var gone = Rows(state).Where(r => Same(r.Scenario, named)).Select(r => (WorkspaceChange)new WorkspaceChange.Remove(Entity.Of(ShiftKind, r.Id))).ToList();
        var changes = new List<WorkspaceChange>(gone);
        foreach (var shift in shifts)
            changes.Add(new WorkspaceChange.Create(PortfolioEditRules.WorkspaceScope, Entity.Of(ShiftKind, $"s{revision + 1 + changes.Count}"), Fields(named, shift)));
        return (changes, gone.Count > 0);
    }

    /// <summary>A shift row's fields as created: the scenario's name, the shift, no description.</summary>
    public static JsonObject Fields(string scenario, Shift shift, string? description = null) => new()
    {
        ["scenario"] = scenario,
        ["index"] = shift.Index,
        ["tenor"] = shift.Tenor,
        ["size"] = shift.Size,
        ["unit"] = shift.Unit,
        ["description"] = description,
    };

    /// <summary>A scenario's name as given, trimmed; an empty one, or one a scenario command begins with, is refused.</summary>
    public static string Checked(string name) =>
        NameIssue(name.Trim()) is { } issue ? throw new WorkspaceRefused("InvalidScenario", issue) : name.Trim();

    // Why a name, trimmed, cannot name a scenario; none where it can.
    static string? NameIssue(string named) =>
        named.Length == 0 ? "A scenario needs a name, such as s1."
        : Reserved.Contains(named.ToLowerInvariant()) ? $"A scenario cannot be named {named}: scenario {named} is a command. Name it otherwise, such as {named}1."
        : null;

    /// <summary>Two names of one scenario: case ignored.</summary>
    public static bool Same(string? a, string? b) => a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    static HashSet<string> ExcludedNames(WorkspaceState state)
    {
        var stored = state.Overrides.TryGetValue(new EditTarget(PortfolioEditRules.WorkspaceScope, Inclusion, Excluded), out var edit) ? edit.Value
            : state.Created.TryGetValue(Inclusion, out var created) ? created.Fields[Excluded] : null;
        return new HashSet<string>(stored is JsonArray names ? names.Select(n => n!.GetValue<string>()) : [], StringComparer.OrdinalIgnoreCase);
    }

    // The rows by scenario, case ignored, in the order of each scenario's first row; a row naming none is no scenario's.
    static IReadOnlyList<(string Name, IReadOnlyList<ShiftRow> Rows)> Named(IReadOnlyList<ShiftRow> rows) =>
        [.. rows.Where(r => r.Scenario is not null).GroupBy(r => r.Scenario!, StringComparer.OrdinalIgnoreCase)
            .Select(g => (g.First().Scenario!, (IReadOnlyList<ShiftRow>)[.. g]))];

    static string? Issue(string name, IReadOnlyList<ShiftRow> rows) =>
        rows.FirstOrDefault(r => r.Issue is not null) is { } invalid ? $"row {invalid.Row}: {invalid.Issue}" : Twice(rows);

    // Two shifts of one scenario that touch one risk factor, said with their rows (ADR-0010 §4).
    static string? Twice(IReadOnlyList<ShiftRow> rows)
    {
        for (var i = 0; i < rows.Count; i++)
            for (var j = i + 1; j < rows.Count; j++)
                if (rows[i].Shift is { } a && rows[j].Shift is { } b && a.Touches(b))
                    return $"{a.Shared(b)} twice, in row {rows[i].Row} ({a.Text}) and row {rows[j].Row} ({b.Text})";
        return null;
    }
}

/// <summary>A shift a provider reads and cannot apply: its measure is refused for that scenario, by name (ADR-0010 §4's
/// note of 2026-11-09), never priced on the base market under the scenario's name.</summary>
public sealed class ScenarioUnapplied(string message) : Exception(message);

/// <summary>
/// The market of one scenario, as every provider reads it (ADR-0010 §4's note of 2026-11-09): the value date's synthetic
/// FX with the FX shifts, and each curve's shift in bp at a point, the key-rate shifts falling linearly to nothing at the
/// catalogue's tenors either side of theirs. The base market is the one with no shift.
/// </summary>
public sealed class ScenarioMarket(IReadOnlyList<Shift> shifts, DateOnly valueDate)
{
    public IReadOnlyList<Shift> Shifts => shifts;
    public DateOnly ValueDate => valueDate;
    public bool IsBase => shifts.Count == 0;

    /// <summary>A currency's move against JPY, as a factor: 1 where no shift moves it.</summary>
    public decimal FxFactor(string currency) =>
        currency == ScenarioCatalog.Domestic ? 1m
        : shifts.SingleOrDefault(s => s.Category == ScenarioCatalog.Fx && ScenarioCatalog.Find(s.Index)!.Currency == currency) is { } shift ? 1m + shift.Size / 100m
        : 1m;

    /// <summary>
    /// Units of the currency one GBP buys on the value date, under the FX shifts: each currency's rate against JPY moved by
    /// its factor, so every cross moves with the two it is made of. With no FX shift, the base market's rate itself.
    /// </summary>
    public decimal UnitsPerGbp(string currency) => PortfolioMarket.UnitsPerGbp(currency, valueDate) * FxFactor("GBP") / FxFactor(currency);

    public decimal ToGbp(decimal amount, string currency) => amount / UnitsPerGbp(currency);

    /// <summary>JPY per unit of the currency under the shifts.</summary>
    public decimal JpyPer(string currency) => UnitsPerGbp(ScenarioCatalog.Domestic) / UnitsPerGbp(currency);

    /// <summary>Whether any shift of this scenario moves the index.</summary>
    public bool Moves(string index) => shifts.Any(s => s.Index == index);

    /// <summary>The shift in bp a curve takes at a point in years: a whole-index shift everywhere, a key-rate shift by its weight.</summary>
    public decimal RateShiftBp(string curve, double years) =>
        shifts.Where(s => s.Category == ScenarioCatalog.Ir && s.Index == curve).Sum(s => s.Size * (decimal)Weight(s.Tenor, years));

    /// <summary>
    /// A key-rate shift's weight at a point: 1 at its tenor, falling linearly to 0 at the tenors either side, and 1 beyond
    /// the first and the last; a shift of the whole index weighs 1 everywhere.
    /// </summary>
    public static double Weight(string? tenor, double years)
    {
        if (tenor is null) return 1;
        var points = ScenarioCatalog.Tenors.Select(ScenarioCatalog.YearsOf).ToList();
        var k = ScenarioCatalog.Tenors.ToList().IndexOf(tenor);
        var at = points[k];
        if (years <= at) return k == 0 ? 1 : years <= points[k - 1] ? 0 : (years - points[k - 1]) / (at - points[k - 1]);
        return k == points.Count - 1 ? 1 : years >= points[k + 1] ? 0 : (points[k + 1] - years) / (points[k + 1] - at);
    }

    /// <summary>
    /// A trade's change of value in JPY under the shifts, by the portfolio's toy revaluation (simulated, as the figures that
    /// read it are): an IRS by a PV01 of notional × years to maturity × 1bp at its maturity on its curve, paying fixed gaining
    /// as rates rise; an FX forward by its two legs' moves against JPY. A trade the toy has no rule for, or one missing what
    /// its rule reads, cannot be revalued under a shift that moves it, and says so.
    /// </summary>
    public decimal PvChangeJpy(string tradeId, string product, string currency, decimal notional, DateOnly maturity, string? direction, string curve,
        string? counterCurrency, decimal? counterNotional)
    {
        if (IsBase) return 0m;
        switch (product)
        {
            case "IRS":
            {
                var years = Math.Max(0, (maturity.DayNumber - valueDate.DayNumber) / 365.0);
                var bp = RateShiftBp(curve, years);
                if (bp == 0) return 0m;
                var sign = direction switch
                {
                    "payFixed" => 1m,
                    "receiveFixed" => -1m,
                    _ => throw new ScenarioUnapplied($"{tradeId} pays or receives fixed, and the source does not say which, so its value under {Moved(curve)} is unknown."),
                };
                return sign * notional * (decimal)years * bp / 10_000m * JpyPer(currency);
            }
            case "FXForward":
            {
                if (counterCurrency is null || counterNotional is not { } counter)
                    throw new ScenarioUnapplied($"{tradeId} is an FX forward with no other leg, so its value under the FX shifts is unknown.");
                var legs = notional * (JpyPer(currency) - PortfolioMarket.UnitsPerGbp(ScenarioCatalog.Domestic, valueDate) / PortfolioMarket.UnitsPerGbp(currency, valueDate))
                    - counter * (JpyPer(counterCurrency) - PortfolioMarket.UnitsPerGbp(ScenarioCatalog.Domestic, valueDate) / PortfolioMarket.UnitsPerGbp(counterCurrency, valueDate));
                if (legs == 0) return 0m;
                return direction switch
                {
                    "buy" => legs,
                    "sell" => -legs,
                    _ => throw new ScenarioUnapplied($"{tradeId} buys or sells, and the source does not say which, so its value under the FX shifts is unknown."),
                };
            }
            default:
                return shifts.Any(s => s.Category == ScenarioCatalog.Fx || s.Index == curve)
                    ? throw new ScenarioUnapplied($"{tradeId} is a {product}, which the toy revaluation has no rule for.")
                    : 0m;
        }
    }

    string Moved(string curve) => string.Join(" and ", shifts.Where(s => s.Index == curve).Select(s => s.Text));
}
