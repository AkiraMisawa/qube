using Workbench.Samples.Rates;

namespace Workbench.Samples.Simm;

/// <summary>
/// A booked trade as the IM source holds it: its Rates terms, or why it has nothing to price (an exemption), and the
/// regimes it counts under in each direction. The source says which regimes, never the page (ADR-0012 §2).
/// </summary>
public sealed record ImTrade(string Id, string Description, RatesLine? Line, string? Exemption,
    IReadOnlyList<string> CollectRegulations, IReadOnlyList<string> PostRegulations);

/// <summary>A counterparty's IM CSAs and booked trades at one version of the source, and the regimes the source classifies by.</summary>
public sealed record ImPortfolio(string Registration, string SourceVersion, IReadOnlyList<string> Regimes, IReadOnlyList<ImCsa> Csas, IReadOnlyList<ImTrade> Trades)
{
    /// <summary>
    /// The trades frozen for CRIF at a value date: each one's Rates line resolved with the confirmed conventions and its
    /// schedules generated. A trade whose line does not resolve is not frozen, and says why; nothing stands in for it.
    /// </summary>
    public (IReadOnlyList<SimmTrade> Frozen, IReadOnlyList<CrifExclusion> NotFrozen) Freeze(DateOnly valueDate, ConventionRegistry conventions)
    {
        var frozen = new List<SimmTrade>();
        var notFrozen = new List<CrifExclusion>();
        foreach (var trade in Trades)
        {
            if (trade.Line is null)
            {
                frozen.Add(new(trade.Id, null, trade.Exemption ?? throw new InvalidOperationException($"{trade.Id} has neither terms nor an exemption."),
                    trade.CollectRegulations, trade.PostRegulations));
                continue;
            }
            var resolution = LineResolver.Resolve(trade.Line, valueDate, conventions);
            if (!resolution.Priceable)
            {
                notFrozen.Add(new(trade.Id, string.Join(" ", resolution.Issues)));
                continue;
            }
            frozen.Add(new(trade.Id, new PricingLine(trade.Id, 0, trade.Line.Direction, RatesMaterialiser.Unedited(resolution), resolution.ParLeg, false),
                null, trade.CollectRegulations, trade.PostRegulations));
        }
        return (frozen, notFrozen);
    }
}

/// <summary>
/// The PoC's IM source: one wholly synthetic counterparty with two IM CSAs and the trades of the drill-down sample. Its
/// trades start at spot on the value date, so they never mature under a moving date. Nothing here is a real position.
/// </summary>
public static class SyntheticImSource
{
    public const string Version = "synthetic-im-1";

    /// <summary>The regimes this source classifies trades by. A CRIF naming any other is refused, not left out of scope.</summary>
    public static readonly IReadOnlyList<string> Regimes = ["CFTC", "EMIR", "JFSA"];

    static readonly IReadOnlyList<string> JfsaCftc = ["JFSA", "CFTC"];
    static readonly IReadOnlyList<string> JfsaEmir = ["JFSA", "EMIR"];

    static LegTerms Fixed(string currency, string index, double notional, double ratePercent) =>
        new(currency, index, notional, new LegQuote(QuoteKind.FixedRatePercent, ratePercent), Tenor.Parse("12m"), null, false);

    static LegTerms Floating(string currency, string index, double notional, double spreadPercent, string frequency) =>
        new(currency, index, notional, new LegQuote(QuoteKind.FloatSpreadPercent, spreadPercent), Tenor.Parse(frequency), Tenor.Parse(frequency), true);

    static RatesLine Irs(string id, Direction direction, string tenor, string currency, string index, double notional, double fixedPercent) =>
        new(id, RatesProduct.Irs, null, DateOrTenor.After(Tenor.Parse(tenor)), direction,
            Fixed(currency, index, notional, fixedPercent), Floating(currency, index, notional, 0, "12m"), null, null, false);

    static readonly IReadOnlyList<ImTrade> Trades =
    [
        new("T-1001", "JPY IRS pay fixed 5y 20bn", Irs("T-1001", Direction.Pay, "5y", "JPY", "TONA", 20e9, 0.95), null, JfsaCftc, ["JFSA"]),
        new("T-1002", "JPY IRS receive fixed 10y 15bn", Irs("T-1002", Direction.Receive, "10y", "JPY", "TONA", 15e9, 1.10), null, JfsaCftc, JfsaEmir),
        new("T-1003", "USD IRS pay fixed 7y 100mm", Irs("T-1003", Direction.Pay, "7y", "USD", "SOFR", 100e6, 4.40), null, ["JFSA"], JfsaEmir),
        new("T-1004", "USD/JPY CCS 5y 50mm, receive JPY TONA -25bp against USD SOFR",
            new RatesLine("T-1004", RatesProduct.Ccs, null, DateOrTenor.After(Tenor.Parse("5y")), Direction.Receive,
                Floating("JPY", "TONA", 7.5e9, -0.25, "3m"), Floating("USD", "SOFR", 50e6, 0, "3m"), null, null, false),
            null, JfsaCftc, JfsaEmir),
        new("T-1005", "USD/JPY FX forward 6m, buy USD 30mm", null, "Physically settled FX forward: exempt from IM in every regime.", [], []),
    ];

    static readonly IReadOnlyList<string> Held = [.. Trades.Select(t => t.Id)];

    static readonly IReadOnlyDictionary<string, ImPortfolio> Portfolios = new Dictionary<string, ImPortfolio>(StringComparer.Ordinal)
    {
        ["ABC/LE-01"] = new("ABC/LE-01", Version, Regimes,
            [new("IMR-ABC-01", ImDirection.Collect, JfsaCftc, Held), new("IMP-ABC-01", ImDirection.Post, JfsaEmir, Held)], Trades),
    };

    /// <summary>The registrations the source holds IM CSAs for: a counterparty with its legal entity (docs/03).</summary>
    public static IReadOnlyList<string> Registrations { get; } = [.. Portfolios.Keys.Order(StringComparer.Ordinal)];

    public static ImPortfolio? Find(string registration) => Portfolios.GetValueOrDefault(registration);
}
