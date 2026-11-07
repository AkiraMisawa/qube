namespace Workbench.Samples.Simm;

/// <summary>The CRIF risk types the PoC's engine reads (ADR-0012 §5). Vega, curvature and base correlation wait for options.</summary>
public enum CrifRiskType { IrCurve, XCcyBasis, Fx }

/// <summary>
/// One CRIF line as the engine receives it. <see cref="Qualifier"/> is the currency; <see cref="Label1"/> the tenor and
/// <see cref="Label2"/> the sub-curve of an IR curve line, and null otherwise. <see cref="AmountUsd"/> is the
/// sensitivity in the calculation currency: per basis point for IR curve and cross-currency basis, per 1% for FX.
/// The regulations are those the line counts under, for each direction (CollectRegulations, PostRegulations).
/// </summary>
public sealed record CrifLine(
    string Line,
    string TradeId,
    CrifRiskType RiskType,
    string Qualifier,
    string? Label1,
    string? Label2,
    double Amount,
    string AmountCurrency,
    double AmountUsd,
    IReadOnlyList<string> CollectRegulations,
    IReadOnlyList<string> PostRegulations);

/// <summary>IM-R is collected from the counterparty; IM-P is posted to it (ADR-0012 §1).</summary>
public enum ImDirection { Collect, Post }

/// <summary>An IM CSA: its direction, the regimes it serves, and the trades it holds (their IM-R or IM-P column, ADR-0012 §2).</summary>
public sealed record ImCsa(string Id, ImDirection Direction, IReadOnlyList<string> Regimes, IReadOnlyList<string> Trades);

/// <summary>A CRIF the engine cannot compute with its parameter table: every line at fault is named, nothing is guessed.</summary>
public sealed class SimmInputException(IReadOnlyList<string> problems) : Exception(string.Join(" ", problems))
{
    public IReadOnlyList<string> Problems { get; } = problems;
}
