using System.Text.Json.Nodes;
using Qube.Composition.Application;
using Qube.Composition.Application.Workspaces;
using Qube.Composition.Contracts;

namespace Qube.Samples.Desk;

/// <summary>
/// The market moves a Desk workspace holds (ADR-0019 §7): one per kind and input, the user's, in the log. A move can
/// be held only for a kind whose unit the result set declares; nothing assumes a unit.
/// </summary>
public static class DeskMoves
{
    public const string Scope = "moves", Field = "move";
    const string Prefix = "move:";

    public static string Entity(string kind, string input) => $"{Prefix}{kind}/{input}";

    public static bool IsMove(string entity) => entity.StartsWith(Prefix, StringComparison.Ordinal);

    public static EditTarget Target(string kind, string input) => new(Scope, Entity(kind, input), Field);

    /// <summary>What a move is typed in: basis points of a rate, or the input's own quote. Null where the kind has no declared unit.</summary>
    public static string? TypedIn(SensitivityPer? per) => per switch
    {
        SensitivityPer.Rate => "bp",
        SensitivityPer.Level => "quote",
        _ => null,
    };

    /// <summary>The move as the sensitivity's unit has it: a basis point is 1e-4 of a rate, and a quote's move is itself.</summary>
    public static double InUnits(SensitivityPer per, double typed) => per == SensitivityPer.Rate ? typed * 1e-4 : typed;

    /// <summary>The move the workspace holds for a kind and input, as typed; null where it holds none.</summary>
    public static double? Held(WorkspaceState state, string kind, string input) =>
        state.Overrides.GetValueOrDefault(Target(kind, input))?.Value is JsonValue value ? (double)FieldForms.Held(value) : null;
}

/// <summary>One sensitivity of the position: the book's, the what-if hedges' where any has a row on it, and their sum.</summary>
/// <param name="WhatIf">Null where no hedge has a row on this input and key: there is nothing hypothetical in the line.</param>
/// <param name="Marks">What the What-if and Net figures are (ADR-0019 §2, §4); the Book figure carries none.</param>
public sealed record PositionRisk(string Kind, string Input, string Key, SensitivityPer? Per, double Book, double? WhatIf, double Net, IReadOnlyList<string> Marks);

/// <summary>The predicted profit and loss of one input's move: sensitivity times move, summed over the input's keys.</summary>
public sealed record PositionPnl(string Kind, string Input, double Move, string TypedIn, double Book, double? WhatIf, double Net, IReadOnlyList<string> Marks);

public sealed record PositionValue(string Counterparty, string? LegalEntity, string Metric, double Value, double StdError);

public sealed record PositionHedge(string Hedge, double Value, string Provider, ExecutionMode Mode);

public sealed record Position(
    DeskManifest Book,
    string ResultSet,
    IReadOnlyList<string> Counterparties,
    IReadOnlyList<PositionValue> Values,
    IReadOnlyDictionary<string, double> Totals,
    IReadOnlyList<PositionHedge> Hedges,
    IReadOnlyList<PositionRisk> Risk,
    IReadOnlyList<PositionPnl> Pnl,
    PositionPnl? PnlTotal);

/// <summary>
/// The position as a pure function of the workspace and its acquired book (ADR-0019 §3): Book, What-if and Net for
/// each sensitivity, and the first-order prediction for the moves held. It makes no Run and stores nothing.
/// </summary>
public static class DeskPosition
{
    public const string WhatIf = "WHAT-IF", Simulated = "SIMULATED", FirstOrder = "FIRST ORDER";

    /// <param name="counterparties">The counterparties whose book is read; null for all of them.</param>
    public static Position Of(WorkspaceState state, IHedgeRisk provider, IReadOnlyList<string>? counterparties = null)
    {
        var source = DeskBookSource.Of(state);
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
        foreach (var created in DeskHedges.In(state))
        {
            var (_, risk, issues) = DeskHedges.Risk(state, created.Entity, provider);
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
            .ThenBy(l => Days(l.Key.Key)).ThenBy(l => l.Key.Key, StringComparer.Ordinal)
            .Select(l => new PositionRisk(l.Key.Kind, l.Key.Input, l.Key.Key, set.UnitOf(l.Key.Kind), l.Value.Book, l.Value.WhatIf,
                l.Value.Book + (l.Value.WhatIf ?? 0), Marks(l.Value.WhatIf is not null, l.Value.Simulated))).ToList();

        var pnl = new List<PositionPnl>();
        foreach (var group in risks.GroupBy(r => (r.Kind, r.Input)))
        {
            if (DeskMoves.Held(state, group.Key.Kind, group.Key.Input) is not { } typed) continue;
            // A move is held only for a kind with a declared unit (the table refuses another); one found without is the store's defect.
            var per = set.UnitOf(group.Key.Kind)
                ?? throw new InvalidOperationException($"Workspace {state.WorkspaceId} holds a move of {group.Key.Kind}, whose unit the result set does not declare.");
            var move = DeskMoves.InUnits(per, typed);
            var hypothetical = group.Any(r => r.WhatIf is not null);
            pnl.Add(new(group.Key.Kind, group.Key.Input, typed, DeskMoves.TypedIn(per)!, group.Sum(r => r.Book) * move,
                hypothetical ? group.Sum(r => r.WhatIf ?? 0) * move : null, group.Sum(r => r.Net) * move,
                Marks(hypothetical, group.Any(r => r.Marks.Contains(Simulated)), firstOrder: true)));
        }
        // A move held for an input the position has no risk line of predicts nothing: the sum over no line. It is
        // said as a line, so that every move held has one.
        foreach (var (kind, input) in source.Inputs.Where(i => !pnl.Any(p => p.Kind == i.Kind && p.Input == i.Input)))
        {
            if (DeskMoves.Held(state, kind, input) is not { } typed) continue;
            var per = set.UnitOf(kind)
                ?? throw new InvalidOperationException($"Workspace {state.WorkspaceId} holds a move of {kind}, whose unit the result set does not declare.");
            pnl.Add(new(kind, input, typed, DeskMoves.TypedIn(per)!, 0, null, 0, Marks(false, false, firstOrder: true)));
        }
        var total = pnl.Count == 0 ? null : new PositionPnl("", "", 0, "", pnl.Sum(p => p.Book), pnl.Any(p => p.WhatIf is not null) ? pnl.Sum(p => p.WhatIf ?? 0) : null,
            pnl.Sum(p => p.Net), Marks(pnl.Any(p => p.WhatIf is not null), pnl.Any(p => p.Marks.Contains(Simulated)), firstOrder: true));

        return new(set.Manifest, set.Reference, [.. chosen], values, totals, [.. hedges.Select(h => h.Hedge)], risks, pnl, total);
    }

    // Keys that are days (1826d) sort by their number; others after them, by name.
    static int Days(string key) =>
        key.EndsWith('d') && int.TryParse(key.AsSpan(0, key.Length - 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var days)
            ? days : int.MaxValue;
}
