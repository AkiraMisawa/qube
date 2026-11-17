using Qube.Samples.Rates;

namespace Qube.Samples.Rates.Alpha;

/// <summary>A curve as the engine defines it (alpha docs/spec/rates-pricer.md §3.1). <c>Quotes</c> is the snapshot's curve key that carries its quotes.</summary>
public sealed record AlphaCurve(string Name, string Currency, string Role, int IndexMonths, string Base, string Interpolation, string Quotes);

/// <summary>
/// Which engine curve projects an index (<c>"JPY/TONA"</c>) and which discounts a currency under a collateral currency
/// (<c>"USD@JPY"</c>). Configuration, never guessed: what is not here is refused on the row.
/// </summary>
public sealed record AlphaCurveMap(IReadOnlyList<AlphaCurve> Curves, IReadOnlyDictionary<string, string> Forward, IReadOnlyDictionary<string, string> Discount);

public sealed record CashflowTable(string Id, string ReportingCurrency, int? ParLeg, IReadOnlyList<TableLeg> Legs);

public sealed record TableLeg(int Sign, string Currency, string DiscountCurve, string Kind, string? Index, string? ForwardCurve,
    IReadOnlyList<TablePeriod> Periods, IReadOnlyList<TableExchange> Exchanges);

/// <summary>One period. Rates are decimals (the grid's percent / 100). <c>Rate</c> is null only on the leg solved at par.</summary>
public sealed record TablePeriod(DateOnly AccrualStart, DateOnly AccrualEnd, DateOnly Payment, double Notional, double YearFraction, double? Rate,
    double Gearing, DateOnly? Fixing = null, DateOnly? IndexStart = null, DateOnly? IndexEnd = null,
    DateOnly? ObservationStart = null, DateOnly? ObservationEnd = null, int? Lookback = null, int? Lockout = null, double? KnownRate = null);

/// <summary>An exchange of notional; positive is received on the leg (the leg's sign then applies).</summary>
public sealed record TableExchange(DateOnly Date, double Amount);

/// <summary>A Rates line as the engine's cashflow table (alpha docs/spec/rates-pricer.md §3.2), or every reason it cannot be.</summary>
public static class CashflowTables
{
    public static (CashflowTable? Table, IReadOnlyList<string> Refusals) From(PricingLine line, AlphaCurveMap map, DateOnly valuationDate,
        Func<string, IBusinessCalendar> calendar)
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
            string? forward = null, kind = "fixed";
            if (terms.Floating)
            {
                var key = MarketSnapshot.CurveKey(terms.Currency, terms.Index);
                if (!map.Forward.TryGetValue(key, out forward)) refusals.Add($"Leg {n}: the engine has no curve for {key}.");
                kind = leg.Profile.Overnight
                    ? (terms.Detail.InArrears == true ? null : terms.Detail.DailyAverage == true ? "average" : "compounded")
                    : terms.Detail.InArrears == true ? "term_in_arrears" : "term";
                if (kind is null) refusals.Add($"Leg {n}: in arrears applies to a term index, not to the overnight {terms.Index}.");
            }
            var indexMonths = forward is null ? 0 : map.Curves.FirstOrDefault(c => c.Name == forward)?.IndexMonths ?? 0;
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
                        IndexEnd = start.AddMonths(indexMonths),
                    };
                }
                if (terms.Floating && terms.Detail.CurrentResetPercent is { } known && p.AccrualStart <= valuationDate && valuationDate < p.AccrualEnd)
                    period = period with { KnownRate = known / 100 };
                periods.Add(period);
            }
            var exchanges = leg.Exchanges.Select(e => new TableExchange(e.PaymentDate, e.Kind == ExchangeKind.Final ? e.Amount : -e.Amount)).ToList();
            legs.Add(new TableLeg(received ? 1 : -1, terms.Currency, discount ?? "", kind ?? "", terms.Floating ? terms.Index : null, forward, periods, exchanges));
        }
        return refusals.Count > 0 ? (null, refusals) : (new CashflowTable(line.RowKey, line.Legs[0].Terms.Currency, line.ParLeg, legs), refusals);
    }
}
