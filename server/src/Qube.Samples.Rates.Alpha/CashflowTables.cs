using Qube.Samples.Rates;

namespace Qube.Samples.Rates.Alpha;

/// <summary>
/// Which of the market's curves projects an index (<c>"JPY/TONA"</c>) and which discounts a currency under a collateral
/// currency (<c>"USD@JPY"</c>). Configuration, never guessed: what is not here, or names a curve the market lacks, is
/// refused on the row.
/// </summary>
public sealed record AlphaCurveMap(IReadOnlyDictionary<string, string> Forward, IReadOnlyDictionary<string, string> Discount);

public sealed record CashflowTable(string Id, string ReportingCurrency, int? ParLeg, IReadOnlyList<TableLeg> Legs);

public sealed record TableLeg(int Sign, string Currency, string DiscountCurve, string Kind, string? Index, string? ForwardCurve,
    IReadOnlyList<TablePeriod> Periods, IReadOnlyList<TableExchange> Exchanges, double? DayBasis = null);

/// <summary>One period. Rates are decimals (the grid's percent / 100). <c>Rate</c> is null only on the leg solved at par.</summary>
public sealed record TablePeriod(DateOnly AccrualStart, DateOnly AccrualEnd, DateOnly Payment, double Notional, double YearFraction, double? Rate,
    double Gearing, DateOnly? Fixing = null, DateOnly? IndexStart = null, DateOnly? IndexEnd = null,
    DateOnly? ObservationStart = null, DateOnly? ObservationEnd = null, int? Lookback = null, int? Lockout = null, double? KnownRate = null,
    double? IndexYearFraction = null);

/// <summary>An exchange of notional; positive is received on the leg (the leg's sign then applies).</summary>
public sealed record TableExchange(DateOnly Date, double Amount);

/// <summary>A Rates line as the engine's cashflow table (alpha docs/spec/rates-pricer.md §3.2), or every reason it cannot be.</summary>
public static class CashflowTables
{
    public static (CashflowTable? Table, IReadOnlyList<string> Refusals) From(PricingLine line, AlphaCurveMap map, MarketSnapshot market,
        DateOnly valuationDate, Func<string, IBusinessCalendar> calendar)
    {
        var refusals = new List<string>();
        if (string.IsNullOrWhiteSpace(line.Collateral))
            refusals.Add("Collateral is empty; the engine discounts each leg on its collateral's curve.");
        var collateral = line.Collateral?.Trim().ToUpperInvariant();
        var legs = new List<TableLeg>();
        for (var n = 1; n <= line.Legs.Count; n++)
        {
            var leg = line.Legs[n - 1];
            var terms = leg.Terms;
            var received = (line.Direction == Direction.Pay) == (n == 2);
            string? discount = null;
            if (collateral is not null && !map.Discount.TryGetValue($"{terms.Currency}@{collateral}", out discount))
                refusals.Add($"Leg {n}: the engine has no curve for {terms.Currency} collateralised in {collateral}.");
            else if (discount is not null && !market.Curves.ContainsKey(discount))
                refusals.Add($"Leg {n}: the market {market.Id} has no curve {discount}.");
            string? forward = null, kind = "fixed";
            if (terms.Floating)
            {
                var key = MarketSnapshot.CurveKey(terms.Currency, terms.Index);
                if (!map.Forward.TryGetValue(key, out forward)) refusals.Add($"Leg {n}: the engine has no curve for {key}.");
                else if (!market.Curves.ContainsKey(forward)) refusals.Add($"Leg {n}: the market {market.Id} has no curve {forward}.");
                kind = leg.Profile.Overnight
                    ? (terms.Detail.InArrears == true ? null : terms.Detail.DailyAverage == true ? "average" : "compounded")
                    : terms.Detail.InArrears == true ? "term_in_arrears" : "term";
                if (kind is null) refusals.Add($"Leg {n}: in arrears applies to a term index, not to the overnight {terms.Index}.");
            }
            // A term index's tenor is its forward curve's; a curve that does not say it is refused, never taken as some tenor.
            int? indexMonths = forward is not null && market.Curves.TryGetValue(forward, out var fc) ? fc.IndexMonths : null;
            if (kind is "term" or "term_in_arrears" && forward is not null && market.Curves.ContainsKey(forward) && indexMonths is null)
                refusals.Add($"Leg {n}: the curve {forward} does not say the tenor of its index.");
            var cal = calendar(leg.Profile.CalendarId);
            var periods = new List<TablePeriod>();
            foreach (var p in leg.Periods)
            {
                double? rate = p.Rate / 100;
                if (rate is null && line.ParLeg != n) refusals.Add($"Leg {n}: {p.Entity} has no rate.");
                var period = new TablePeriod(p.AccrualStart, p.AccrualEnd, p.PaymentDate, p.Notional, p.YearFraction, rate, terms.Detail.Gearing ?? 1.0);
                if (terms.Floating && leg.Profile.Overnight)
                {
                    var shift = terms.Detail.ObservationShiftDays ?? 0;
                    period = period with
                    {
                        ObservationStart = DateRules.AddBusinessDays(p.AccrualStart, -shift, cal),
                        ObservationEnd = DateRules.AddBusinessDays(p.AccrualEnd, -shift, cal),
                        Lookback = terms.Detail.LookbackDays ?? 0,
                        Lockout = 0,
                    };
                }
                else if (terms.Floating)
                {
                    var start = kind == "term_in_arrears" ? p.AccrualEnd : p.AccrualStart;
                    period = period with
                    {
                        Fixing = DateRules.AddBusinessDays(start, -leg.Profile.SpotLagDays, cal),
                        IndexStart = start,
                        IndexEnd = indexMonths is { } months ? start.AddMonths(months) : null, // absent only on a row refused above
                        // the index period on the leg's day count (a definition of this sample, unconfirmed like the others)
                        IndexYearFraction = indexMonths is { } m ? DateRules.YearFraction(start, start.AddMonths(m), leg.Profile.DayCount) : null,
                    };
                }
                if (terms.Floating && terms.Detail.CurrentResetPercent is { } known && p.AccrualStart <= valuationDate && valuationDate < p.AccrualEnd)
                    period = period with { KnownRate = known / 100 };
                periods.Add(period);
            }
            var exchanges = leg.Exchanges.Select(e => new TableExchange(e.PaymentDate, e.Kind == ExchangeKind.Final ? e.Amount : -e.Amount)).ToList();
            // An overnight leg's compounding counts days on its day count's basis.
            double? dayBasis = terms.Floating && leg.Profile.Overnight ? DayBasis(leg.Profile.DayCount) : null;
            legs.Add(new TableLeg(received ? 1 : -1, terms.Currency, discount ?? "", kind ?? "", terms.Floating ? terms.Index : null, forward, periods, exchanges, dayBasis));
        }
        return refusals.Count > 0 ? (null, refusals) : (new CashflowTable(line.RowKey, line.Legs[0].Terms.Currency, line.ParLeg, legs), refusals);
    }

    static double DayBasis(DayCountBasis basis) => basis switch
    {
        DayCountBasis.Act360 => 360.0,
        DayCountBasis.Act365Fixed => 365.0,
        _ => throw new NotSupportedException($"The day count {basis} has no basis the engine knows."),
    };
}
