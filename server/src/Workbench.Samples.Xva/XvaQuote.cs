using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Workbench.Composition.Application;
using Workbench.Composition.Application.Workspaces;
using Workbench.Composition.Contracts;
using Workbench.Samples.Portfolio;

namespace Workbench.Samples.Xva;

/// <summary>A quote as the clipboard takes it: the same content as plain text and as HTML.</summary>
public sealed record Quote(string Text, string Html);

/// <summary>
/// Copy quote (ADR-0010 §7, §11): what a trader sends sales to charge, read from the Run's retained input and result
/// alone, never from a screen. The header names the Run, the counterparty, the netting set, the value date, the
/// revision and the currency, and says when the figures are simulated. Each case gives its CVA Charge and its PV as its
/// Diff (After − Before, DEFAULT note of 2026-11-07); a one-sided case quotes its priced side and names it. A figure the
/// Run does not have is quoted as what it is (unavailable, refused), never as 0.
/// </summary>
public static class XvaQuote
{
    public const string CvaCharge = "cvaCharge";
    public const string Pv = "pv";

    public static Quote Of(RunRecord run, IDataReader data)
    {
        if (run.Status == RunStatus.Running) throw new WorkspaceRefused("RunRunning", $"Run {run.RunId} is still running; quote it once it finishes.");
        if (!run.Outputs.TryGetValue(XvaMeasures.Module, out var resultRef))
            throw new WorkspaceRefused("NothingToQuote", $"Run {run.RunId} has no XVA result to quote.");
        var step = run.Workflow.Steps.SingleOrDefault(s => s.StepId == XvaMeasures.Module)
            ?? throw new WorkspaceRefused("NothingToQuote", $"Run {run.RunId} has no XVA step.");
        if (step.Bindings.GetValueOrDefault("acceptedInput") is not DataReferenceBinding pinned)
            throw new InvalidOperationException($"Run {run.RunId}'s XVA step pins no accepted input.");
        var input = data.ReadRequired(pinned.Reference);
        var result = data.ReadRequired(resultRef);
        var rows = result["rows"]!.AsArray().Select(r => r!).ToList();
        if (!rows.Any(r => r["metric"]!.GetValue<string>() == CvaCharge))
            throw new WorkspaceRefused("NothingToQuote", $"Run {run.RunId} did not price CVA, so it has no CVA Charge to quote.");

        var simulated = result["provenance"]?["executionMode"]?.GetValue<string>() == "simulated";
        var currency = rows.Select(r => r["currency"]!.GetValue<string>()).Distinct().Single();
        var header = string.Join(" · ",
            $"Counterparty {input["registration"]!.GetValue<string>()}",
            $"Netting set {input["nettingSet"]!.GetValue<string>()}",
            $"Value date {run.Context["valueDate"]!.GetValue<string>()}",
            $"Revision {input["revision"]!.GetValue<long>()}",
            currency);
        var title = $"XVA quote{(simulated ? " (simulated)" : "")} · {run.RunId}";
        var cases = rows.GroupBy(r => (Case: r["case"]!.GetValue<string>(), Title: r["caseTitle"]!.GetValue<string>()))
            .Select(g => (g.Key.Title, Charge: Figure(g.SingleOrDefault(r => r["metric"]!.GetValue<string>() == CvaCharge)),
                Pv: Figure(g.SingleOrDefault(r => r["metric"]!.GetValue<string>() == Pv))))
            .ToList();

        var text = new StringBuilder().AppendLine(title).AppendLine(header);
        foreach (var (caseTitle, charge, pv) in cases)
            text.AppendLine($"{caseTitle}{Sided(charge, pv)}: CVA Charge {charge.Text} · PV {pv.Text}");

        var html = new StringBuilder()
            .Append($"<p><b>{Html(title)}</b></p><p>{Html(header)}</p>")
            .Append("<table><thead><tr><th>Case</th><th>Quotes</th><th>CVA Charge</th><th>PV</th></tr></thead><tbody>");
        foreach (var (caseTitle, charge, pv) in cases)
            html.Append($"<tr><td>{Html(caseTitle)}</td><td>{Html(charge.Side)}</td><td style=\"text-align:right\">{Html(charge.Text)}</td><td style=\"text-align:right\">{Html(pv.Text)}</td></tr>");
        html.Append("</tbody></table>");
        return new(text.ToString().TrimEnd(), html.ToString());
    }

    /// <summary>A case's quoted figure, the side it is (Diff, After only or Before only), and how it reads.</summary>
    readonly record struct Quoted(string Side, string Text);

    static Quoted Figure(JsonNode? row)
    {
        if (row is null) return new("Diff", "not priced");
        var before = row["beforeStatus"]!.GetValue<string>();
        var after = row["afterStatus"]!.GetValue<string>();
        if (before == MeasureResult.Priced && after == MeasureResult.Priced) return new("Diff", Amount(row["diff"]!));
        // A one-sided case (ADR-0010 §3, §7): the priced side, named.
        if (after == MeasureResult.Priced && before == MeasureResult.NotPriced) return new("After only", Amount(row["after"]!));
        if (before == MeasureResult.Priced && after == MeasureResult.NotPriced) return new("Before only", Amount(row["before"]!));
        var failed = after == MeasureResult.Priced ? before : after;
        return new("Diff", row["reason"] is { } reason ? $"{failed}: {reason.GetValue<string>()}" : failed);
    }

    // The side a case quotes is said once, on the case, when both figures quote the same one.
    static string Sided(Quoted charge, Quoted pv) => charge.Side == "Diff" ? "" : charge.Side == pv.Side ? $" ({charge.Side})" : $" (CVA Charge {charge.Side}, PV {pv.Side})";

    static string Amount(JsonNode value) => value.GetValue<decimal>().ToString("#,0.00", CultureInfo.InvariantCulture);

    static string Html(string text) => WebUtility.HtmlEncode(text);
}
