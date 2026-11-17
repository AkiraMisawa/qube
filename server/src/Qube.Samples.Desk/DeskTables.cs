using System.Text.Json.Nodes;
using Qube.Composition.Application;
using Qube.Composition.Application.Tables;
using Qube.Composition.Application.Workspaces;

namespace Qube.Samples.Desk;

/// <summary>
/// The book's tables (ADR-0019 §2, ADR-0016 §1): the values and the first-order sensitivities of the base scenario, as
/// the engine wrote them and with its signs (ADR-0019 §9). They are read-only: a write of any kind is refused by name.
/// </summary>
public sealed class DeskTables(IHedgeRisk provider) : ITableCatalog
{
    public const string Hedges = "desk.hedges";
    public const string Moves = "desk.moves";

    public const string Version = "1.0.0";
    public const string Values = "desk.values";
    public const string Risk = "desk.risk";

    public IEnumerable<IWorkspaceTable> Tables { get; } = [new ValueTable(), new RiskTable(), new HedgeTable(provider), new MoveTable()];

    static string Unit(SensitivityPer? per) => per switch
    {
        SensitivityPer.Rate => "rate",
        SensitivityPer.Level => "level",
        null => "",
        _ => throw new InvalidOperationException($"A sensitivity is per {per}, which the Desk does not write."),
    };

    abstract class BookTable(string id, string title) : IWorkspaceTable
    {
        public string TableId => id;
        public string App => DeskEditRules.App;
        public abstract TableDescriptor Describe(WorkspaceState state);
        public abstract IReadOnlyList<ITableRow> Rows(WorkspaceState state);
        public DateOnly? AsOf(WorkspaceState state) => DeskBookSource.Of(state).Set.Manifest.ValuationDate;

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
        static readonly TableDescriptor Descriptor = new(Values, Version, DeskEditRules.App, "Values", ["values", "value"], "id",
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
            var book = DeskBookSource.Of(state);
            var entities = book.Set.Counterparties.ToDictionary(c => c.Code, c => c.LegalEntity);
            return [.. book.Values.Select(v => new Row(DeskBookSource.ValueKey(v), new Dictionary<string, JsonNode?>
            {
                ["id"] = DeskBookSource.ValueKey(v), ["counterparty"] = v.Counterparty, ["legalEntity"] = entities[v.Counterparty],
                ["metric"] = v.Metric, ["value"] = v.Value, ["stdError"] = v.StdError,
            }))];
        }
    }

    sealed class RiskTable() : BookTable(Risk, "Risk")
    {
        static readonly TableDescriptor Descriptor = new(Risk, Version, DeskEditRules.App, "Risk", ["risk", "sensitivities"], "id",
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
            var book = DeskBookSource.Of(state);
            return [.. book.Sensitivities.Select(s => new Row(DeskBookSource.RiskKey(s), new Dictionary<string, JsonNode?>
            {
                ["id"] = DeskBookSource.RiskKey(s), ["counterparty"] = s.Counterparty, ["metric"] = s.Metric, ["kind"] = s.Kind,
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
        static readonly TableDescriptor Descriptor = new(Hedges, Version, DeskEditRules.App, "What-if hedges", ["hedges", "hedge"], "id",
        [
            new("id", "Hedge", ["id"], FieldForm.Id),
            new(DeskHedges.Product, "Product", [DeskHedges.Product], FieldForm.Id),
            new(DeskHedges.End, "End", [DeskHedges.End], FieldForm.PeriodOrDate, Editable: true),
            new(DeskHedges.Currency, "Currency", [DeskHedges.Currency, "ccy"], FieldForm.Choice, Editable: true, Choices: DeskProducts.Currencies),
            new(DeskHedges.Notional, "Notional", [DeskHedges.Notional], FieldForm.Amount, Editable: true, UnitField: DeskHedges.Currency),
            new(DeskHedges.Direction, "Direction", [DeskHedges.Direction], FieldForm.Choice, Editable: true, Choices: DeskProducts.Directions),
            new(DeskHedges.Rate, "Fixed rate", [DeskHedges.Rate], FieldForm.Percent, Editable: true),
        ], [], Actions: [new("Remove hedge", "remove selected")]);

        public string TableId => Hedges;
        public string App => DeskEditRules.App;
        public TableDescriptor Describe(WorkspaceState state) => Descriptor;
        public DateOnly? AsOf(WorkspaceState state) => DeskBookSource.Of(state).Set.Manifest.ValuationDate;

        public IReadOnlyList<ITableRow> Rows(WorkspaceState state) => [.. DeskHedges.In(state).Select(c =>
        {
            var cells = DeskHedges.Fields(state, c.Entity).ToDictionary(f => f.Key, f => f.Value);
            cells["id"] = c.Entity;
            return (ITableRow)new Row(c.Entity, cells);
        })];

        public IReadOnlyList<WorkspaceChange> Set(WorkspaceState state, ITableRow row, TableField field, JsonNode? value)
        {
            var fields = DeskHedges.Fields(state, row.Key).ToDictionary(f => f.Key, f => f.Value);
            fields[field.Name] = value;
            var book = DeskBookSource.Of(state).Set;
            var (swap, issues) = DeskHedges.Read(row.Key, fields, book.Manifest.ValuationDate);
            if (swap is null) throw new WorkspaceRefused("InvalidValue", $"{row.Key}: {string.Join(" ", issues)}");
            try
            {
                provider.Risk(swap, book);
            }
            catch (HedgeRiskRefused refused)
            {
                throw new WorkspaceRefused(refused.Code, $"{row.Key}: {refused.Message}");
            }
            return [new WorkspaceChange.Set(new EditTarget(DeskHedges.Scope, row.Key, field.Name), value)];
        }

        public IReadOnlyList<WorkspaceChange> Reset(WorkspaceState state, ITableRow row, TableField? field) =>
            [.. state.Overrides.Keys.Where(t => t.Scope == DeskHedges.Scope && t.Entity == row.Key && (field is null || t.Field == field.Name))
                .OrderBy(t => t.Field, StringComparer.Ordinal).Select(t => new WorkspaceChange.Reset(t))];

        public IReadOnlyList<WorkspaceChange> Remove(WorkspaceState state, ITableRow row) => [new WorkspaceChange.Remove(row.Key)];
    }

    /// <summary>
    /// The market moves the prediction is made for (ADR-0019 §7): a row per kind and input, its move the user's. A move
    /// of a rate is typed in basis points and one of a quoted level in the quote's own units, as the row's
    /// <c>typedIn</c> says. A kind whose unit the result set does not declare takes no move, and is told so by name.
    /// </summary>
    sealed class MoveTable : IWorkspaceTable
    {
        static readonly TableDescriptor Descriptor = new(Moves, Version, DeskEditRules.App, "Moves", ["moves", "move"], "id",
        [
            new("id", "Row", ["id"], FieldForm.Id),
            new("kind", "Kind", ["kind"], FieldForm.Id),
            new("input", "Input", ["input"], FieldForm.Id),
            new(DeskMoves.Field, "Move", [DeskMoves.Field], FieldForm.Number, Editable: true, Optional: true, UnitField: "typedIn"),
            new("typedIn", "Typed in", ["typed in"], FieldForm.Id, Optional: true),
        ], []);

        public string TableId => Moves;
        public string App => DeskEditRules.App;
        public TableDescriptor Describe(WorkspaceState state) => Descriptor;

        public IReadOnlyList<ITableRow> Rows(WorkspaceState state)
        {
            var book = DeskBookSource.Of(state);
            return [.. book.Inputs.Select(i => (ITableRow)new MoveRow(i.Kind, i.Input, new Dictionary<string, JsonNode?>
            {
                ["id"] = $"{i.Kind}/{i.Input}", ["kind"] = i.Kind, ["input"] = i.Input,
                [DeskMoves.Field] = DeskMoves.Held(state, i.Kind, i.Input),
                ["typedIn"] = DeskMoves.TypedIn(book.Set.UnitOf(i.Kind)),
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
            var set = DeskBookSource.Of(state).Set;
            if (set.UnitOf(move.Kind) is null)
                throw new WorkspaceRefused("NoUnit",
                    $"Result set '{set.Reference}' declares no unit for {move.Kind}, so a move of {move.Key} cannot be read and none is taken.", word: move.Key);
            var target = DeskMoves.Target(move.Kind, move.Input);
            // An empty move is no move: the field follows its empty start again.
            return value is null ? Reset(state, row, field) : [new WorkspaceChange.Set(target, value)];
        }

        public IReadOnlyList<WorkspaceChange> Reset(WorkspaceState state, ITableRow row, TableField? field)
        {
            var move = (MoveRow)row;
            var target = DeskMoves.Target(move.Kind, move.Input);
            return state.Overrides.ContainsKey(target) ? [new WorkspaceChange.Reset(target)] : [];
        }

        public IReadOnlyList<WorkspaceChange> Remove(WorkspaceState state, ITableRow row) =>
            throw new WorkspaceRefused("NotRemovable", "A move is emptied, not removed: reset it.");
    }
}
