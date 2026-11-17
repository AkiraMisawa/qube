using System.Text.Json.Nodes;
using Qube.Composition.Application;
using Qube.Composition.Application.Workspaces;
using Qube.Composition.Contracts;

namespace Qube.Samples.PositionKeeper;

/// <summary>
/// The market moves a Position Keeper workspace holds (ADR-0019 §7): one per kind and input, the user's, in the log. A move can
/// be held only for a kind whose unit the result set declares; nothing assumes a unit.
/// </summary>
public static class PkMoves
{
    public const string Scope = "moves", Field = "move";
    const string Prefix = "move:";

    public static string Entity(string kind, string input) => $"{Prefix}{kind}/{input}";

    public static bool IsMove(string entity) => entity.StartsWith(Prefix, StringComparison.Ordinal);

    public static EditTarget Target(string kind, string input) => new(Scope, Entity(kind, input), Field);

    /// <summary>What a move may say it is in: basis points of a rate, or percent of the input's own level.</summary>
    public const string Bp = "bp", Percent = "%";

    /// <summary>What a move's bare number is in: basis points of a rate, or the input's own quote. Null where the kind has no declared unit.</summary>
    public static string? TypedIn(SensitivityPer? per) => per switch
    {
        SensitivityPer.Rate => Bp,
        SensitivityPer.Level => "quote",
        SensitivityPer.Vol => "vol pt",
        _ => null,
    };

    /// <summary>An absolute move as the sensitivity's unit has it: a basis point is 1e-4 of a rate, a vol point 0.01 of a vol, and a quote's move is itself.</summary>
    public static double InUnits(SensitivityPer per, double typed) => per switch
    {
        SensitivityPer.Rate => typed * 1e-4,
        SensitivityPer.Vol => typed * 0.01,
        _ => typed,
    };

    /// <summary>The move the workspace holds for a kind and input, as typed, and whether it is of the level; null where it holds none.</summary>
    public static (double Amount, bool Relative)? Held(WorkspaceState state, string kind, string input) =>
        state.Overrides.GetValueOrDefault(Target(kind, input))?.Value is JsonObject held
            ? ((double)FieldForms.Held(held["amount"]!), held["unit"]?.GetValue<string>() == Percent)
            : null;

    /// <summary>
    /// A relative move of one key as the sensitivity's unit has it: the percentage of the level the result set gives.
    /// A key it gives no level of is refused by name: none is assumed (ADR-0019 §7).
    /// </summary>
    public static double OfLevel(PkResultSet set, string kind, string input, string key, double percent) =>
        set.Levels.TryGetValue((kind, input, key), out var level) ? level * percent / 100
            : throw new WorkspaceRefused("NoLevel",
                $"Result set '{set.Reference}' gives no level of {kind} {input}{(key.Length > 0 ? " at " + key : "")}, so a move in % of it cannot be read: say the move as it is, without %.",
                word: $"{kind}/{input}");
}

/// <summary>One sensitivity of the position: the book's, the what-if hedges' where any has a row on it, and their sum.</summary>
/// <param name="WhatIf">Null where no hedge has a row on this input and key: there is nothing hypothetical in the line.</param>
/// <param name="Marks">What the What-if and Net figures are (ADR-0019 §2, §4); the Book figure carries none.</param>
public sealed record PositionRisk(string Kind, string Input, string Key, SensitivityPer? Per, double Book, double? WhatIf, double Net, IReadOnlyList<string> Marks)
{
    /// <summary>The key as a tenor's name (<c>5Y</c>) where it is that tenor's day from the valuation date; null where it is not.</summary>
    public string? Tenor { get; init; }
}

/// <summary>
/// The step a trader reads a sensitivity per (ADR-0019 §7): one basis point of a rate, one unit of a quote. A kind
/// with no declared unit has none, and no figure per step.
/// </summary>
public static class PkSteps
{
    public static string? Name(SensitivityPer? per) => per switch
    {
        SensitivityPer.Rate => "1bp",
        SensitivityPer.Level => "quote",
        SensitivityPer.Vol => "1 vol pt",
        _ => null,
    };

    public static double? Of(SensitivityPer? per, double? sensitivity) =>
        per is { } declared && sensitivity is { } value ? PkMoves.InUnits(declared, value) : null;
}

/// <summary>A key as a tenor's name, where it is exactly that tenor's day from the valuation date; no nearest tenor is taken.</summary>
public static class PkTenors
{
    /// <summary>The days a key such as <c>1826d</c> says; null where it is no number of days.</summary>
    public static int? DaysOf(string key) =>
        key.EndsWith('d') && int.TryParse(key.AsSpan(0, key.Length - 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var days) ? days : null;

    /// <summary>The days from the valuation date to a tenor such as <c>1D</c>, <c>2W</c>, <c>6M</c> or <c>5Y</c>; null where the word is no tenor.</summary>
    public static int? Days(DateOnly valuation, string tenor)
    {
        if (tenor.Length < 2 || !int.TryParse(tenor.AsSpan(0, tenor.Length - 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var count) || count <= 0)
            return null;
        return tenor[^1] switch
        {
            'D' => count,
            'W' => 7 * count,
            'M' => valuation.AddMonths(count).DayNumber - valuation.DayNumber,
            'Y' => valuation.AddYears(count).DayNumber - valuation.DayNumber,
            _ => null,
        };
    }

    /// <summary>The day a key stands at: the days it says (<c>1826d</c>), or the day of the tenor it names (<c>5Y</c>); null where it is neither.</summary>
    public static int? DayOf(DateOnly valuation, string key) => DaysOf(key) ?? Days(valuation, key);

    public static string? Name(DateOnly valuation, string key)
    {
        // A key that is a tenor's name already is its own name.
        if (DaysOf(key) is not { } days) return Days(valuation, key) is null ? null : key;
        if (days == 1) return "1D";
        if (days is 7 or 14 or 21) return $"{days / 7}W";
        for (var months = 1; months <= 18; months++)
            if (months % 12 != 0 && valuation.AddMonths(months).DayNumber - valuation.DayNumber == days) return $"{months}M";
        for (var years = 1; years <= 100; years++)
            if (valuation.AddYears(years).DayNumber - valuation.DayNumber == days) return $"{years}Y";
        return null;
    }
}

/// <summary>One input moved by one step at every key: the sum of its sensitivities per step.</summary>
public sealed record PositionParallel(string Kind, string Input, string Step, double Book, double? WhatIf, double Net, IReadOnlyList<string> Marks);

/// <summary>
/// One cell of a kind's ladder: its risk per step at one tenor, or over them all (<c>Total</c>), for the whole kind
/// (<c>total</c>), a currency's curves (<c>group</c>) or one input (<c>input</c>).
/// </summary>
public sealed record PositionLadder(string Kind, string Level, string Name, string Tenor, int Order, string Step, double Book, double? WhatIf, double Net, IReadOnlyList<string> Marks);

/// <summary>The predicted profit and loss of the moves held for one kind of risk: its lines summed.</summary>
/// <param name="Moves">How many inputs of the kind hold a move.</param>
public sealed record PositionPnlKind(string Kind, int Moves, double Book, double? WhatIf, double Net, IReadOnlyList<string> Marks);

/// <summary>A counterparty's adjustments summed over the metrics.</summary>
public sealed record PositionCounterparty(string Counterparty, string? LegalEntity, double Total);

/// <summary>The predicted profit and loss of one input's move: sensitivity times move, summed over the input's keys.</summary>
/// <param name="TypedIn">What <paramref name="Move"/> is in: <c>bp</c> or <c>quote</c> where it is absolute, <c>%</c> where it is of each key's level.</param>
public sealed record PositionPnl(string Kind, string Input, double Move, string TypedIn, double Book, double? WhatIf, double Net, IReadOnlyList<string> Marks);

public sealed record PositionValue(string Counterparty, string? LegalEntity, string Metric, double Value, double StdError);

public sealed record PositionHedge(string Hedge, double Value, string Provider, ExecutionMode Mode);

public sealed record Position(
    PkManifest Book,
    string ResultSet,
    IReadOnlyList<string> Counterparties,
    IReadOnlyList<PositionValue> Values,
    IReadOnlyDictionary<string, double> Totals,
    IReadOnlyList<PositionHedge> Hedges,
    IReadOnlyList<PositionRisk> Risk,
    IReadOnlyList<PositionPnl> Pnl,
    PositionPnl? PnlTotal)
{
    public IReadOnlyList<PositionParallel> Parallel { get; init; } = [];
    public IReadOnlyList<PositionCounterparty> ByCounterparty { get; init; } = [];
    public IReadOnlyList<PositionLadder> Ladder { get; init; } = [];
    public IReadOnlyList<PositionPnlKind> PnlByKind { get; init; } = [];
}

/// <summary>
/// The position as a pure function of the workspace and its acquired book (ADR-0019 §3): Book, What-if and Net for
/// each sensitivity, and the first-order prediction for the moves held. It makes no Run and stores nothing.
/// </summary>
public static class PkPosition
{
    public const string WhatIf = "WHAT-IF", Simulated = "SIMULATED", FirstOrder = "FIRST ORDER";

    /// <param name="counterparties">The counterparties whose book is read; null for all of them.</param>
    public static Position Of(WorkspaceState state, IHedgeRisk provider, IReadOnlyList<string>? counterparties = null)
    {
        var source = PkBookSource.Of(state);
        var set = source.Set;
        var known = set.Counterparties.Select(c => c.Code).ToList();
        if (counterparties?.FirstOrDefault(c => !known.Contains(c)) is { } unknown)
            throw new WorkspaceRefused("UnknownCounterparty", $"The book has no counterparty {unknown}.", word: unknown, candidates: known);
        // Left out, every counterparty is read; a list that names none asks for no book, and is refused as that.
        if (counterparties is { Count: 0 })
            throw new WorkspaceRefused("NoCounterparty", "The counterparties to read are named, or left out for all of them: the list names none.", candidates: known);
        var chosen = counterparties ?? known;
        var entities = set.Counterparties.ToDictionary(c => c.Code, c => c.LegalEntity);

        var values = source.Values.Where(v => chosen.Contains(v.Counterparty))
            .Select(v => new PositionValue(v.Counterparty, entities[v.Counterparty], v.Metric, v.Value, v.StdError)).ToList();
        // A total's value is the sum. Its standard error is not the sum of the parts' (they share paths), so none is given.
        var totals = values.GroupBy(v => v.Metric, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Sum(v => v.Value));

        // Every hedge has its risk, or the position is refused: a Net that left one out would be quietly wrong.
        var hedges = new List<(PositionHedge Hedge, HedgeRiskAnswer Risk)>();
        foreach (var created in PkHedges.In(state))
        {
            var (_, risk, issues) = PkHedges.Risk(state, created.Entity, provider);
            if (risk is null)
                throw new WorkspaceRefused("HedgeRiskUnavailable", $"{created.Entity} has no risk, so no Net is given: {string.Join(" ", issues)}");
            hedges.Add((new(created.Entity, risk.Value, risk.Provider, risk.Mode), risk));
        }

        var lines = new Dictionary<(string Kind, string Input, string Key), (double Book, double? WhatIf, bool Simulated)>();
        foreach (var s in source.Sensitivities.Where(s => chosen.Contains(s.Counterparty)))
        {
            var key = (s.Kind, s.Input, s.Key);
            lines[key] = (lines.GetValueOrDefault(key).Book + s.Sensitivity, null, false);
        }
        foreach (var (_, risk) in hedges)
            foreach (var s in risk.Sensitivities)
            {
                var key = (s.Kind, s.Input, s.Key);
                var line = lines.GetValueOrDefault(key);
                lines[key] = (line.Book, (line.WhatIf ?? 0) + s.Sensitivity, line.Simulated || risk.Mode == ExecutionMode.Simulated);
            }

        static IReadOnlyList<string> Marks(bool whatIf, bool simulated, bool firstOrder = false) =>
            [.. new[] { whatIf ? WhatIf : null, simulated ? Simulated : null, firstOrder ? FirstOrder : null }.OfType<string>()];

        var risks = lines.OrderBy(l => l.Key.Kind, StringComparer.Ordinal).ThenBy(l => l.Key.Input, StringComparer.Ordinal)
            .ThenBy(l => PkTenors.DayOf(set.Manifest.ValuationDate, l.Key.Key) ?? int.MaxValue).ThenBy(l => l.Key.Key, StringComparer.Ordinal)
            .Select(l => new PositionRisk(l.Key.Kind, l.Key.Input, l.Key.Key, set.UnitOf(l.Key.Kind), l.Value.Book, l.Value.WhatIf,
                l.Value.Book + (l.Value.WhatIf ?? 0), Marks(l.Value.WhatIf is not null, l.Value.Simulated))
            { Tenor = PkTenors.Name(set.Manifest.ValuationDate, l.Key.Key) }).ToList();

        var pnl = new List<PositionPnl>();
        var grouped = risks.GroupBy(r => (r.Kind, r.Input)).ToDictionary(g => g.Key, g => g.ToList());
        // Every move held has its line. One for an input the position has no risk line of predicts nothing: the sum
        // over no line, said as a line.
        foreach (var (kind, input) in grouped.Keys.Concat(source.Inputs).Distinct())
        {
            if (PkMoves.Held(state, kind, input) is not var (typed, relative)) continue;
            // A move is held only for a kind with a declared unit (the table refuses another); one found without is the store's defect.
            var per = set.UnitOf(kind)
                ?? throw new InvalidOperationException($"Workspace {state.WorkspaceId} holds a move of {kind}, whose unit the result set does not declare.");
            var ofInput = grouped.GetValueOrDefault((kind, input)) ?? [];
            // The move of each key in the sensitivity's unit: the same for every key where it is absolute, and the
            // percentage of the key's own level where it is relative.
            double Move(PositionRisk r) => relative ? PkMoves.OfLevel(set, kind, input, r.Key, typed) : PkMoves.InUnits(per, typed);
            var hypothetical = ofInput.Any(r => r.WhatIf is not null);
            pnl.Add(new(kind, input, typed, relative ? PkMoves.Percent : PkMoves.TypedIn(per)!, ofInput.Sum(r => r.Book * Move(r)),
                hypothetical ? ofInput.Sum(r => (r.WhatIf ?? 0) * Move(r)) : null, ofInput.Sum(r => r.Net * Move(r)),
                Marks(hypothetical, ofInput.Any(r => r.Marks.Contains(Simulated)), firstOrder: true)));
        }
        var total = pnl.Count == 0 ? null : new PositionPnl("", "", 0, "", pnl.Sum(p => p.Book), pnl.Any(p => p.WhatIf is not null) ? pnl.Sum(p => p.WhatIf ?? 0) : null,
            pnl.Sum(p => p.Net), Marks(pnl.Any(p => p.WhatIf is not null), pnl.Any(p => p.Marks.Contains(Simulated)), firstOrder: true));

        // Each input with a declared unit moved by one step at every key, in the order of the risk lines.
        var parallel = grouped.Where(g => set.UnitOf(g.Key.Kind) is not null).Select(g =>
        {
            var per = set.UnitOf(g.Key.Kind);
            var hypothetical = g.Value.Any(r => r.WhatIf is not null);
            return new PositionParallel(g.Key.Kind, g.Key.Input, PkSteps.Name(per)!, g.Value.Sum(r => PkSteps.Of(per, r.Book)!.Value),
                hypothetical ? g.Value.Sum(r => PkSteps.Of(per, r.WhatIf ?? 0)!.Value) : null, g.Value.Sum(r => PkSteps.Of(per, r.Net)!.Value),
                Marks(hypothetical, g.Value.Any(r => r.Marks.Contains(Simulated))));
        }).ToList();
        // Largest cost first: the order a trader reads counterparties in.
        var byCounterparty = values.GroupBy(v => v.Counterparty, StringComparer.Ordinal)
            .Select(g => new PositionCounterparty(g.Key, entities[g.Key], g.Sum(v => v.Value)))
            .OrderBy(c => c.Total).ThenBy(c => c.Counterparty, StringComparer.Ordinal).ToList();

        return new(set.Manifest, set.Reference, [.. chosen], values, totals, [.. hedges.Select(h => h.Hedge)], risks, pnl, total)
        {
            Parallel = parallel, ByCounterparty = byCounterparty, Ladder = Ladders(set, risks),
            PnlByKind = [.. pnl.GroupBy(l => l.Kind, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g =>
            {
                var hypothetical = g.Any(l => l.WhatIf is not null);
                return new PositionPnlKind(g.Key, g.Count(), g.Sum(l => l.Book), hypothetical ? g.Sum(l => l.WhatIf ?? 0) : null, g.Sum(l => l.Net),
                    Marks(hypothetical, g.Any(l => l.Marks.Contains(Simulated)), firstOrder: true));
            })],
        };
    }

    public const string Total = "Total", All = "All";

    /// <summary>
    /// Each kind with a declared unit as a ladder (ADR-0019 §7): its lines per step, on the tenors the result set
    /// declares for the kind or, where it declares none, on the kind's own nodes. A node between two declared tenors
    /// is shared between them by its distance in days, one before the first or after the last goes to that one, so
    /// a ladder's total is the parallel line. A line whose key is no number of days is in the total alone.
    /// </summary>
    static IReadOnlyList<PositionLadder> Ladders(PkResultSet set, IReadOnlyList<PositionRisk> risks)
    {
        var valuation = set.Manifest.ValuationDate;
        var cells = new List<PositionLadder>();
        foreach (var kind in risks.GroupBy(r => r.Kind))
        {
            if (set.UnitOf(kind.Key) is not { } per) continue;
            var step = PkSteps.Name(per)!;
            // The columns: the declared tenors, or each node's own day by its name.
            List<(string Tenor, int Days)> columns = set.Manifest.Ladders.TryGetValue(kind.Key, out var declared)
                ? [.. declared.Select(t => (t, PkTenors.Days(valuation, t)!.Value))]
                : [.. kind.Where(r => PkTenors.DayOf(valuation, r.Key) is not null).Select(r => (Tenor: r.Tenor ?? r.Key, Days: PkTenors.DayOf(valuation, r.Key)!.Value)).Distinct().OrderBy(c => c.Days)];

            // How a line is shared over the columns: by index, with its weight.
            IEnumerable<(int Column, double Weight)> Shares(PositionRisk line)
            {
                if (PkTenors.DayOf(valuation, line.Key) is not { } days || columns.Count == 0) yield break;
                if (days <= columns[0].Days) { yield return (0, 1); yield break; }
                if (days >= columns[^1].Days) { yield return (columns.Count - 1, 1); yield break; }
                var after = columns.FindIndex(c => c.Days >= days);
                if (columns[after].Days == days) { yield return (after, 1); yield break; }
                var width = (double)(columns[after].Days - columns[after - 1].Days);
                yield return (after - 1, (columns[after].Days - days) / width);
                yield return (after, (days - columns[after - 1].Days) / width);
            }

            void Row(string level, string name, IReadOnlyList<PositionRisk> lines)
            {
                var hypothetical = lines.Any(r => r.WhatIf is not null);
                var marks = (IReadOnlyList<string>)[.. new[] { hypothetical ? WhatIf : null, lines.Any(r => r.Marks.Contains(Simulated)) ? Simulated : null }.OfType<string>()];
                var book = new double[columns.Count];
                var whatIf = new double[columns.Count];
                foreach (var line in lines)
                    foreach (var (column, weight) in Shares(line))
                    {
                        book[column] += PkSteps.Of(per, line.Book)!.Value * weight;
                        whatIf[column] += PkSteps.Of(per, line.WhatIf ?? 0)!.Value * weight;
                    }
                for (var c = 0; c < columns.Count; c++)
                    cells.Add(new(kind.Key, level, name, columns[c].Tenor, c, step, book[c], hypothetical ? whatIf[c] : null, book[c] + whatIf[c], marks));
                var bookTotal = lines.Sum(r => PkSteps.Of(per, r.Book)!.Value);
                var whatIfTotal = lines.Sum(r => PkSteps.Of(per, r.WhatIf ?? 0)!.Value);
                cells.Add(new(kind.Key, level, name, Total, columns.Count, step, bookTotal, hypothetical ? whatIfTotal : null, bookTotal + whatIfTotal, marks));
            }

            Row("total", All, [.. kind]);
            foreach (var group in kind.Where(r => set.Manifest.CurveCurrencies.ContainsKey(r.Input)).GroupBy(r => set.Manifest.CurveCurrencies[r.Input]).OrderBy(g => g.Key, StringComparer.Ordinal))
                Row("group", group.Key, [.. group]);
            foreach (var input in kind.GroupBy(r => r.Input))
                Row("input", input.Key, [.. input]);
        }
        return cells;
    }
}
