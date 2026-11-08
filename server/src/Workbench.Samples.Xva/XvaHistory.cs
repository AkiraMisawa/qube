using System.Text.Json.Nodes;
using Workbench.Composition.Application;
using Workbench.Composition.Contracts;

using Workbench.Samples.Portfolio;

namespace Workbench.Samples.Xva;

/// <summary>
/// XVA's view of the Pricing DB (docs/05 §7): one row per Run, with its value date, counterparty registration
/// and the CVA and FVA figures of its result. One Run prices one counterparty as one case today; a row per
/// case follows when a Run carries several.
/// </summary>
public sealed class XvaHistoryProjection : IHistoryProjection
{
    public string Domain => PortfolioEditRules.App;
    public string Title => "XVA";
    // A Run priced before the measures register wrote XVA's case result; since, every measure writes the shared one.
    public IReadOnlyList<string> ResultContracts { get; } = [XvaContracts.CaseResult, PortfolioContracts.MeasureResult];

    static readonly string[] Measures = ["CVA", "FVA"];
    static readonly string[] Sides = ["before", "after", "incremental"];

    public IReadOnlyList<DatasetColumn> Columns { get; } =
    [
        new("valueDate", DatasetColumnType.String, true),
        new("registration", DatasetColumnType.String, true),
        new("tradesBefore", DatasetColumnType.Integer, true),
        new("tradesAfter", DatasetColumnType.Integer, true),
        .. Measures.SelectMany(m => Sides.Select(s => new DatasetColumn(Field(m, s), DatasetColumnType.Number, true))),
        new("executionMode", DatasetColumnType.String, true),
    ];

    public JsonObject Project(RunRecord run, IDataReader data)
    {
        var row = new JsonObject
        {
            ["valueDate"] = run.Context["valueDate"]?.DeepClone(),
            ["registration"] = run.Context["registration"]?.DeepClone(),
        };
        foreach (var reference in run.Outputs.Values)
        {
            if (data.Read(reference) is not DataLookup.Found found) continue;
            var rows = found.Content["rows"]!.AsArray().Select(r => r!.AsObject()).ToList();
            var shared = reference.ContractId == PortfolioContracts.MeasureResult;
            foreach (var measure in Measures)
            {
                // The shared result names a metric by id (cva) and calls the incremental Diff; the old one named the measure (CVA).
                var figures = shared
                    ? rows.FirstOrDefault(r => r["metric"]!.GetValue<string>() == measure.ToLowerInvariant() && r["case"]!.GetValue<string>() == "base")
                    : rows.SingleOrDefault(r => r["measure"]!.GetValue<string>() == measure);
                if (figures is null) continue;
                foreach (var side in Sides) row[Field(measure, side)] = figures[shared && side == "incremental" ? "diff" : side]?.DeepClone();
                row["tradesBefore"] = figures["tradesBefore"]?.DeepClone();
                row["tradesAfter"] = figures["tradesAfter"]?.DeepClone();
            }
            row["executionMode"] = found.Content["provenance"]?["executionMode"]?.DeepClone();
        }
        return row;
    }

    static string Field(string measure, string side) => $"{measure.ToLowerInvariant()}{char.ToUpperInvariant(side[0])}{side[1..]}";
}
