using System.Text.Json.Nodes;
using Qube.Composition.Application;
using Qube.Composition.Application.Tables;
using Qube.Composition.Application.Workspaces;

namespace Qube.Samples.Desk;

/// <summary>
/// The book's tables (ADR-0019 §2, ADR-0016 §1): the values and the first-order sensitivities of the base scenario, as
/// the engine wrote them and with its signs (ADR-0019 §9). They are read-only: a write of any kind is refused by name.
/// </summary>
public sealed class DeskTables : ITableCatalog
{
    public const string Version = "1.0.0";
    public const string Values = "desk.values";
    public const string Risk = "desk.risk";

    public IEnumerable<IWorkspaceTable> Tables { get; } = [new ValueTable(), new RiskTable()];

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
}
