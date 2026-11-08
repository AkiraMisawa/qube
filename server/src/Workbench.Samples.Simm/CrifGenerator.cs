using Workbench.Samples.Rates;

namespace Workbench.Samples.Simm;

/// <summary>
/// A trade as CRIF generation takes it: its frozen pricing input, or why it has none (an exemption), and the regimes
/// it counts under in each direction. Which regimes a trade is in is the source's to say, never the page's (ADR-0012 §2).
/// </summary>
public sealed record SimmTrade(string Id, PricingLine? Pricing, string? Exemption, IReadOnlyList<string> CollectRegulations, IReadOnlyList<string> PostRegulations,
    string? Description = null);

/// <summary>A trade that gave no CRIF line, and why.</summary>
public sealed record CrifExclusion(string TradeId, string Reason);

/// <summary>The CRIF lines, the trades that gave none and why, and the calculation mode the provider answered in.</summary>
public sealed record CrifResult(IReadOnlyList<CrifLine> Lines, IReadOnlyList<CrifExclusion> Excluded, string CalculationMode);

/// <summary>
/// CRIF from the Rates pricing at the value date (ADR-0012 §5). IR curve deltas are the provider's, per curve pillar,
/// split onto SIMM's twelve tenors linearly in years. FX delta is the USD value's change for a 1% rise of each other
/// currency, with a cross-currency swap's principal exchanges left out: the margin framework exempts those fixed,
/// physically settled FX transactions from IM (ADR-0012 §5). Cross-currency basis is the change for 1bp more spread on
/// a non-USD floating leg. A trade the provider fails or does not answer, or whose curve has no SIMM sub-curve, is
/// excluded with that reason; nothing stands in for it.
/// </summary>
public sealed class CrifGenerator(IRatesPricingProvider provider)
{
    public const string CalculationCurrency = "USD";

    public async Task<CrifResult> GenerateAsync(IReadOnlyList<SimmTrade> trades, DateOnly valueDate, MarketSnapshot market, CancellationToken cancellationToken)
    {
        if (!market.Fx.ContainsKey(CalculationCurrency))
            throw new SimmInputException([$"The market {market.Id} has no FX rate for the calculation currency {CalculationCurrency}."]);
        var excluded = new List<CrifExclusion>();
        var priced = new List<(SimmTrade Trade, PricingLine Pricing)>();
        foreach (var trade in trades)
        {
            if (trade.Exemption is { } exemption) excluded.Add(new(trade.Id, exemption));
            else if (trade.Pricing is not { } pricing) excluded.Add(new(trade.Id, "The trade has no pricing input."));
            else if (Missing(pricing, market) is { } currency) excluded.Add(new(trade.Id, $"The market {market.Id} has no FX rate for {currency}."));
            else priced.Add((trade, pricing));
        }

        var modes = new HashSet<string>(StringComparer.Ordinal);
        async Task<IReadOnlyDictionary<string, RowResult>> Price(MarketSnapshot m, IEnumerable<PricingLine> lines)
        {
            var batch = await provider.PriceBatchAsync(new RatesBatchInput(0, valueDate, m, [.. lines]), cancellationToken);
            modes.Add(batch.CalculationMode);
            return batch.Rows.ToDictionary(r => r.RowKey);
        }

        var risk = await Price(market, priced.Select(t => t.Pricing with { Risk = true }));
        var bare = priced.ToDictionary(t => t.Trade.Id, t => t.Pricing with { Risk = false, Legs = [.. t.Pricing.Legs.Select(l => l with { Exchanges = [] })] });
        var bareBase = await Price(market, bare.Values);
        var fxCurrencies = priced.SelectMany(t => Currencies(t.Pricing)).Where(c => c != CalculationCurrency).Distinct().Order(StringComparer.Ordinal).ToList();
        var bumpedFx = new Dictionary<string, IReadOnlyDictionary<string, RowResult>>();
        foreach (var currency in fxCurrencies)
            bumpedFx[currency] = await Price(Strengthened(market, currency), bare.Values.Where(l => Currencies(l).Contains(currency)));
        var basisLegs = priced.ToDictionary(t => t.Trade.Id, t => BasisLeg(t.Pricing));
        var full = priced.Where(t => basisLegs[t.Trade.Id] is not null).ToDictionary(t => t.Trade.Id, t => t.Pricing with { Risk = false });
        var fullBase = await Price(market, full.Values);
        var basisBumped = await Price(market, full.Select(p => basisLegs[p.Key] is { } leg ? Widened(p.Value, leg)
            : throw new InvalidOperationException($"{p.Key} was taken for its basis leg and has none.")));

        // A row the provider did not answer is a defect of the provider's, said for the trade, never a line made up.
        RowResult? Answer(IReadOnlyDictionary<string, RowResult> rows, string rowKey) => rows.GetValueOrDefault(rowKey);
        string Unanswered() => $"The provider {provider.Name} returned no row for the trade.";

        var lines = new List<CrifLine>();
        foreach (var (trade, pricing) in priced)
        {
            if (Answer(risk, pricing.RowKey) is not { } row)
            {
                excluded.Add(new(trade.Id, Unanswered()));
                continue;
            }
            if (row.Failure is not null)
            {
                excluded.Add(new(trade.Id, row.Failure));
                continue;
            }
            var own = new List<(CrifRiskType Type, string Qualifier, string? Tenor, string? SubCurve, double Amount, string Currency)>();
            string? refusal = null;
            foreach (var (key, measure) in row.Measures.Where(m => m.Key.StartsWith("delta:", StringComparison.Ordinal)).OrderBy(m => m.Key, StringComparer.Ordinal))
            {
                var parts = key.Split(':');
                var (currency, index) = (parts[1].Split('/')[0], parts[1].Split('/')[1]);
                if (SubCurve(pricing, currency, index) is not { } subCurve)
                {
                    refusal = $"The curve {parts[1]} has no SIMM sub-curve: only an overnight index maps to OIS until a mapping for {index} is configured.";
                    break;
                }
                foreach (var (tenor, weight) in OntoSimmTenors(SyntheticMarket.YearsOf(Tenor.Parse(parts[2]))))
                    own.Add((CrifRiskType.IrCurve, currency, tenor, subCurve, measure.Value * weight, Unit(measure)));
            }
            if (refusal is null && basisLegs[trade.Id] is { } basisLeg)
            {
                if ((Answer(fullBase, pricing.RowKey), Answer(basisBumped, pricing.RowKey)) is not ({ } b, { } a)) refusal = Unanswered();
                else if ((b.Failure ?? a.Failure) is { } failure) refusal = failure;
                else own.Add((CrifRiskType.XCcyBasis, pricing.Legs[basisLeg].Terms.Currency, null, null,
                    a.Measures[Measures.Pv].Value - b.Measures[Measures.Pv].Value, b.Measures[Measures.Pv].Unit));
            }
            foreach (var currency in Currencies(pricing).Where(c => c != CalculationCurrency).Distinct().Order(StringComparer.Ordinal))
            {
                if (refusal is not null) break;
                if ((Answer(bareBase, pricing.RowKey), Answer(bumpedFx[currency], pricing.RowKey)) is not ({ } b, { } a)) refusal = Unanswered();
                else if ((b.Failure ?? a.Failure) is { } failure) refusal = failure;
                else own.Add((CrifRiskType.Fx, currency, null, null,
                    Usd(a.Measures[Measures.Pv], Strengthened(market, currency)) - Usd(b.Measures[Measures.Pv], market), CalculationCurrency));
            }
            if (refusal is not null)
            {
                excluded.Add(new(trade.Id, refusal));
                continue;
            }

            var n = 0;
            foreach (var g in own.GroupBy(o => (o.Type, o.Qualifier, o.Tenor, o.SubCurve, o.Currency)).OrderBy(g => g.Key.Type).ThenBy(g => g.Key.Qualifier, StringComparer.Ordinal)
                .ThenBy(g => g.Key.Tenor is null ? 0 : SimmParameters.TenorYears[g.Key.Tenor]))
            {
                var amount = g.Sum(o => o.Amount);
                if (amount == 0) continue; // a sensitivity of exactly nothing is no risk factor's line
                lines.Add(new CrifLine($"{trade.Id}-{++n:00}", trade.Id, g.Key.Type, g.Key.Qualifier, g.Key.Tenor, g.Key.SubCurve, amount,
                    g.Key.Currency, amount * UsdPer(g.Key.Currency, market), trade.CollectRegulations, trade.PostRegulations));
            }
        }
        // One CRIF is made in one mode: a provider that answers its batches in different ones is a defect, not a mixture to label.
        if (modes.Count != 1)
            throw new InvalidOperationException($"The provider {provider.Name} answered in the calculation modes {string.Join(", ", modes.Order(StringComparer.Ordinal))}.");
        return new CrifResult(lines, excluded, modes.Single());
    }

    static IEnumerable<string> Currencies(PricingLine line) => line.Legs.Select(l => l.Terms.Currency).Distinct();

    static string? Missing(PricingLine line, MarketSnapshot market) => Currencies(line).FirstOrDefault(c => !market.Fx.ContainsKey(c));

    static string Unit(Measure measure) => measure.Unit.Split('/')[0];

    static double UsdPer(string currency, MarketSnapshot market) => market.Fx[CalculationCurrency] / market.Fx[currency];

    static double Usd(Measure pv, MarketSnapshot market) => pv.Value * UsdPer(pv.Unit, market);

    /// <summary>The market with one currency 1% stronger against USD; FX is quoted as units per a common base.</summary>
    static MarketSnapshot Strengthened(MarketSnapshot market, string currency) =>
        market with { Fx = market.Fx.ToDictionary(p => p.Key, p => p.Key == currency ? p.Value / 1.01 : p.Value) };

    /// <summary>The leg whose spread is the cross-currency basis: a floating leg quoted as a spread, not in USD, of a two-currency trade.</summary>
    static int? BasisLeg(PricingLine line)
    {
        if (Currencies(line).Count() < 2) return null;
        for (var i = 0; i < line.Legs.Count; i++)
            if (line.Legs[i].Terms is { Floating: true, Quote.Kind: QuoteKind.FloatSpreadPercent } terms && terms.Currency != CalculationCurrency) return i;
        return null;
    }

    /// <summary>The line with 1bp (0.01%) more spread on one leg.</summary>
    static PricingLine Widened(PricingLine line, int leg) => line with
    {
        Legs = [.. line.Legs.Select((l, i) => i != leg ? l : l with
        {
            Terms = l.Terms with { Quote = l.Terms.Quote! with { Value = l.Terms.Quote.Value + 0.01 } },
            Periods = [.. l.Periods.Select(p => p with { Rate = p.Rate + 0.01 })],
        })],
    };

    /// <summary>OIS for a curve whose index is overnight; null for any other, which has no sub-curve until one is configured.</summary>
    static string? SubCurve(PricingLine line, string currency, string index) =>
        line.Legs.FirstOrDefault(l => l.Terms.Currency == currency && l.Terms.Index == index)?.Profile.Overnight == true ? "OIS" : null;

    /// <summary>A pillar's share on SIMM's tenors: itself, or its two neighbours linearly in years; the ends take what lies beyond them.</summary>
    static IEnumerable<(string Tenor, double Weight)> OntoSimmTenors(double years)
    {
        var tenors = SimmParameters.TenorYears.OrderBy(t => t.Value).ToList();
        if (years <= tenors[0].Value) return [(tenors[0].Key, 1)];
        if (years >= tenors[^1].Value) return [(tenors[^1].Key, 1)];
        for (var i = 1; i < tenors.Count; i++)
        {
            if (Math.Abs(years - tenors[i].Value) < 1e-12) return [(tenors[i].Key, 1)];
            if (years < tenors[i].Value)
            {
                var w = (tenors[i].Value - years) / (tenors[i].Value - tenors[i - 1].Value);
                return [(tenors[i - 1].Key, w), (tenors[i].Key, 1 - w)];
            }
        }
        throw new InvalidOperationException($"{years} years lies on no SIMM tenor."); // the ends above take every other case
    }
}
