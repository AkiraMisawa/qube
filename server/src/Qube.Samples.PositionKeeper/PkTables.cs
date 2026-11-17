using System.Text.Json.Nodes;
using Qube.Composition.Application;
using Qube.Composition.Application.Tables;
using Qube.Composition.Application.Workspaces;

namespace Qube.Samples.PositionKeeper;

/// <summary>
/// The book's tables (ADR-0019 §2, ADR-0016 §1): the values and the first-order sensitivities of the base scenario, as
/// the engine wrote them and with its signs (ADR-0019 §9). They are read-only: a write of any kind is refused by name.
/// </summary>
public sealed class PkTables(IHedgeRisk provider) : ITableCatalog
{
    public const string Hedges = "pk.hedges";
    public const string Moves = "pk.moves";

    public const string Version = "1.0.0";
    public const string Values = "pk.values";
    public const string Risk = "pk.risk";

    public IEnumerable<IWorkspaceTable> Tables { get; } = [new ValueTable(), new RiskTable(), new HedgeTable(provider), new MoveTable()];

    static string Unit(SensitivityPer per) => per switch
    {
        SensitivityPer.Rate => "rate",
        SensitivityPer.Level => "level",
        SensitivityPer.Vol => "vol",
        _ => throw new InvalidOperationException($"A sensitivity is per {per}, which the Position Keeper does not write."),
    };

    abstract class BookTable(string id, string title) : IWorkspaceTable
    {
        public string TableId => id;
        public string App => PkEditRules.App;
        public abstract TableDescriptor Describe(WorkspaceState state);
        public abstract IReadOnlyList<ITableRow> Rows(WorkspaceState state);
        public DateOnly? AsOf(WorkspaceState state) => PkBookSource.Of(state).Set.Manifest.ValuationDate;

        WorkspaceRefused ReadOnly() => new("ReadOnly",
            $"{title} is the book as the engine computed it: it is read, and no command changes it. A what-if is a hedge the workspace makes.");

        public IReadOnlyList<WorkspaceChange> Set(WorkspaceState state, ITableRow row, TableField field, JsonNode? value) => throw ReadOnly();
        public IReadOnlyList<WorkspaceChange> Reset(WorkspaceState state, ITableRow row, TableField? field) => throw ReadOnly();
        public IReadOnlyList<WorkspaceChange> Remove(WorkspaceState state, ITableRow row) => throw ReadOnly();
    }

    sealed class Row(string key, IReadOnlyDictionary<string, JsonNode?> cells) : ITableRow
    {
        public string Key => key;
        public string? Subset => null;
        public JsonNode? Value(string field) =>
            cells.TryGetValue(field, out var value) ? value?.DeepClone() : throw new InvalidOperationException($"A row of the book has no {field}.");
    }

    sealed class ValueTable() : BookTable(Values, "Values")
    {
        static readonly TableDescriptor Descriptor = new(Values, Version, PkEditRules.App, "Values", ["values", "value"], "id",
        [
            new("id", "Row", ["id"], FieldForm.Id),
            new("counterparty", "Counterparty", ["counterparty", "cpty"], FieldForm.Id),
            new("legalEntity", "Legal entity", ["legal entity", "entity"], FieldForm.Id, Optional: true),
            new("metric", "Metric", ["metric"], FieldForm.Id),
            new("value", "Value", ["value"], FieldForm.Number),
            new("stdError", "Std error", ["std error", "error"], FieldForm.Number),
        ], [], Main: true);

        public override TableDescriptor Describe(WorkspaceState state) => Descriptor;

        public override IReadOnlyList<ITableRow> Rows(WorkspaceState state)
        {
            var book = PkBookSource.Of(state);
            var entities = book.Set.Counterparties.ToDictionary(c => c.Code, c => c.LegalEntity);
            return [.. book.Values.Select(v => new Row(PkBookSource.ValueKey(v), new Dictionary<string, JsonNode?>
            {
                ["id"] = PkBookSource.ValueKey(v), ["counterparty"] = v.Counterparty, ["legalEntity"] = entities[v.Counterparty],
                ["metric"] = v.Metric, ["value"] = v.Value, ["stdError"] = v.StdError,
            }))];
        }
    }

    sealed class RiskTable() : BookTable(Risk, "Risk")
    {
        static readonly TableDescriptor Descriptor = new(Risk, Version, PkEditRules.App, "Risk", ["risk", "sensitivities"], "id",
        [
            new("id", "Row", ["id"], FieldForm.Id),
            new("counterparty", "Counterparty", ["counterparty", "cpty"], FieldForm.Id),
            new("metric", "Metric", ["metric"], FieldForm.Id),
            new("kind", "Kind", ["kind"], FieldForm.Id),
            new("input", "Input", ["input"], FieldForm.Id),
            new("key", "Key", ["key"], FieldForm.Id, Optional: true),
            new("sensitivity", "Sensitivity", ["sensitivity"], FieldForm.Number),
            // What one move of the input is, as the result set declares it; empty where it declares none (ADR-0019 §7).
            new("per", "Per", ["per", "unit"], FieldForm.Id, Optional: true),
        ], []);

        public override TableDescriptor Describe(WorkspaceState state) => Descriptor;

        public override IReadOnlyList<ITableRow> Rows(WorkspaceState state)
        {
            var book = PkBookSource.Of(state);
            return [.. book.Sensitivities.Select(s => new Row(PkBookSource.RiskKey(s), new Dictionary<string, JsonNode?>
            {
                ["id"] = PkBookSource.RiskKey(s), ["counterparty"] = s.Counterparty, ["metric"] = s.Metric, ["kind"] = s.Kind,
                ["input"] = s.Input, ["key"] = s.Key.Length == 0 ? null : s.Key, ["sensitivity"] = s.Sensitivity,
                ["per"] = book.Set.UnitOf(s.Kind) is { } per ? Unit(per) : null,
            }))];
        }
    }

    /// <summary>
    /// The what-if hedges (ADR-0019 §2): rows the workspace made, each marked hypothetical by being here at all. A write
    /// is checked as the making is: the terms after it must describe a swap whose risk the provider answers.
    /// </summary>
    sealed class HedgeTable(IHedgeRisk provider) : IWorkspaceTable
    {
        static readonly TableDescriptor Descriptor = new(Hedges, Version, PkEditRules.App, "What-if hedges", ["hedges", "hedge"], "id",
        [
            new("id", "Hedge", ["id"], FieldForm.Id),
            new(PkHedges.Product, "Product", [PkHedges.Product], FieldForm.Id),
            new(PkHedges.End, "End", [PkHedges.End], FieldForm.PeriodOrDate, Editable: true),
            new(PkHedges.Currency, "Currency", [PkHedges.Currency, "ccy"], FieldForm.Choice, Editable: true, Choices: PkProducts.Currencies),
            new(PkHedges.Notional, "Notional", [PkHedges.Notional], FieldForm.Amount, Editable: true, UnitField: PkHedges.Currency),
            new(PkHedges.Direction, "Direction", [PkHedges.Direction], FieldForm.Choice, Editable: true, Choices: PkProducts.Directions),
            new(PkHedges.Rate, "Fixed rate", [PkHedges.Rate], FieldForm.Percent, Editable: true),
        ], [], Actions: [new("Remove hedge", "remove selected")]);

        public string TableId => Hedges;
        public string App => PkEditRules.App;
        public TableDescriptor Describe(WorkspaceState state) => Descriptor;
        public DateOnly? AsOf(WorkspaceState state) => PkBookSource.Of(state).Set.Manifest.ValuationDate;

        public IReadOnlyList<ITableRow> Rows(WorkspaceState state) => [.. PkHedges.In(state).Select(c =>
        {
            var cells = PkHedges.Fields(state, c.Entity).ToDictionary(f => f.Key, f => f.Value);
            cells["id"] = c.Entity;
            return (ITableRow)new Row(c.Entity, cells);
        })];

        public IReadOnlyList<WorkspaceChange> Set(WorkspaceState state, ITableRow row, TableField field, JsonNode? value)
        {
            var fields = PkHedges.Fields(state, row.Key).ToDictionary(f => f.Key, f => f.Value);
            fields[field.Name] = value;
            var book = PkBookSource.Of(state).Set;
            var (swap, issues) = PkHedges.Read(row.Key, fields, book.Manifest.ValuationDate);
            if (swap is null) throw new WorkspaceRefused("InvalidValue", $"{row.Key}: {string.Join(" ", issues)}");
            try
            {
                provider.Risk(swap, book);
            }
            catch (HedgeRiskRefused refused)
            {
                throw new WorkspaceRefused(refused.Code, $"{row.Key}: {refused.Message}");
            }
            return [new WorkspaceChange.Set(new EditTarget(PkHedges.Scope, row.Key, field.Name), value)];
        }

        public IReadOnlyList<WorkspaceChange> Reset(WorkspaceState state, ITableRow row, TableField? field) =>
            [.. state.Overrides.Keys.Where(t => t.Scope == PkHedges.Scope && t.Entity == row.Key && (field is null || t.Field == field.Name))
                .OrderBy(t => t.Field, StringComparer.Ordinal).Select(t => new WorkspaceChange.Reset(t))];

        public IReadOnlyList<WorkspaceChange> Remove(WorkspaceState state, ITableRow row) => [new WorkspaceChange.Remove(row.Key)];
    }

    /// <summary>
    /// The market moves the prediction is made for (ADR-0019 §7): a row per kind and input, its move the user's. A bare
    /// number is absolute: basis points of a rate (<c>10</c> or <c>10bp</c>) and a quoted level's own units, as the
    /// row's <c>typedIn</c> says. A number in <c>%</c> is relative: that percentage of the level the result set gives
    /// for each key, and refused where it gives none. A kind whose unit the result set does not declare takes no move.
    /// </summary>
    sealed class MoveTable : IWorkspaceTable
    {
        // Before the descriptor, which is made from it.
        static readonly IReadOnlyDictionary<string, string> MoveUnits = new Dictionary<string, string> { [PkMoves.Bp] = PkMoves.Bp, [PkMoves.Percent] = PkMoves.Percent };

        static readonly TableDescriptor Descriptor = new(Moves, Version, PkEditRules.App, "Moves", ["moves", "move"], "id",
        [
            new("id", "Row", ["id"], FieldForm.Id),
            new("kind", "Kind", ["kind"], FieldForm.Id),
            new("input", "Input", ["input"], FieldForm.Id),
            new(PkMoves.Field, "Move", [PkMoves.Field], FieldForm.Money, Editable: true, Optional: true, Choices: MoveUnits),
            new("typedIn", "Bare number in", ["typed in"], FieldForm.Id, Optional: true),
        ], []);

        public string TableId => Moves;
        public string App => PkEditRules.App;
        public TableDescriptor Describe(WorkspaceState state) => Descriptor;

        public IReadOnlyList<ITableRow> Rows(WorkspaceState state)
        {
            var book = PkBookSource.Of(state);
            return [.. book.Inputs.Select(i => (ITableRow)new MoveRow(i.Kind, i.Input, new Dictionary<string, JsonNode?>
            {
                ["id"] = $"{i.Kind}/{i.Input}", ["kind"] = i.Kind, ["input"] = i.Input,
                [PkMoves.Field] = state.Overrides.GetValueOrDefault(PkMoves.Target(i.Kind, i.Input))?.Value?.DeepClone(),
                ["typedIn"] = PkMoves.TypedIn(book.Set.UnitOf(i.Kind)),
            }))];
        }

        sealed class MoveRow(string kind, string input, IReadOnlyDictionary<string, JsonNode?> cells) : ITableRow
        {
            public string Kind => kind;
            public string Input => input;
            public string Key => $"{kind}/{input}";
            public string? Subset => null;
            public JsonNode? Value(string field) =>
                cells.TryGetValue(field, out var value) ? value?.DeepClone() : throw new InvalidOperationException($"A move has no {field}.");
        }

        public IReadOnlyList<WorkspaceChange> Set(WorkspaceState state, ITableRow row, TableField field, JsonNode? value)
        {
            var move = (MoveRow)row;
            var set = PkBookSource.Of(state).Set;
            if (set.UnitOf(move.Kind) is not { } per)
                throw new WorkspaceRefused("NoUnit",
                    $"Result set '{set.Reference}' declares no unit for {move.Kind}, so a move of {move.Key} cannot be read and none is taken.", word: move.Key);
            var target = PkMoves.Target(move.Kind, move.Input);
            // An empty move is no move: the field follows its empty start again.
            if (value is null) return Reset(state, row, field);
            var unit = value["unit"]?.GetValue<string>();
            if (unit == PkMoves.Bp && per != SensitivityPer.Rate)
                throw new WorkspaceRefused("WrongUnit",
                    $"{move.Key} is not a rate, so its move is not in bp: say it in the quote's own units, or in % of its level.", word: move.Key);
            // A move in % is taken of each key's level, so every key the input can have risk on has one, or it is
            // refused here, by the key, before it is held: the book's keys, and a hedge curve's nodes.
            if (unit == PkMoves.Percent)
                foreach (var key in set.Sensitivities.Where(s => s.Scenario == set.Manifest.BaseScenario && s.Kind == move.Kind && s.Input == move.Input).Select(s => s.Key)
                    .Concat(move.Kind == PkBookSource.HedgeKind && set.Manifest.HedgeCurves.Values.Contains(move.Input) ? set.Curves[move.Input].Nodes.Select(n => n.Key) : [])
                    .Distinct())
                    PkMoves.OfLevel(set, move.Kind, move.Input, key, 0);
            return [new WorkspaceChange.Set(target, value)];
        }

        public IReadOnlyList<WorkspaceChange> Reset(WorkspaceState state, ITableRow row, TableField? field)
        {
            var move = (MoveRow)row;
            var target = PkMoves.Target(move.Kind, move.Input);
            return state.Overrides.ContainsKey(target) ? [new WorkspaceChange.Reset(target)] : [];
        }

        public IReadOnlyList<WorkspaceChange> Remove(WorkspaceState state, ITableRow row) =>
            throw new WorkspaceRefused("NotRemovable", "A move is emptied, not removed: reset it.");
    }
}
