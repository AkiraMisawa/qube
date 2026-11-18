using Qube.Samples.Rates;
using Qube.Samples.Rates.Alpha;

namespace Qube.Composition.Tests;

// alpha docs/spec/rates-pricer.md §3.2: a line goes to the engine as its cashflow table, as the grid edited it.
public class AlphaCashflowTableTests
{
    static readonly IBusinessCalendar Calendar = new WeekendsOnlyCalendar();
    static readonly DateOnly ValuationDate = new(2026, 11, 17);

    static ConventionProfile Profile(string ccy, string index, bool overnight) =>
        new($"t.{ccy}.{index}", "1", ccy, index, DayCountBasis.Act360, Calendar.Id, BusinessDayAdjustment.ModifiedFollowing, 2, 0, StubRule.ShortFront, false, overnight);

    static readonly AlphaCurveMap Map = new(
        new Dictionary<string, string> { ["JPY/TONA"] = "JPY-OIS", ["USD/SOFR"] = "USD-OIS", ["JPY/TIBOR6M"] = "JPY-FLT-6M" },
        new Dictionary<string, string> { ["JPY@JPY"] = "JPY-OIS", ["USD@USD"] = "USD-OIS", ["USD@JPY"] = "USD-XCCY-JPY" });

    static MarketCurve Curve(string name, string ccy, string index, string role, int? indexMonths = null) =>
        new(name, ccy, index, role, "base", CurveMath.LogLinearDf, [new("1y", 365, 0.99), new("10y", 3650, 0.9)], indexMonths);

    static readonly MarketSnapshot Market = new("m:1", 1, DateTimeOffset.UnixEpoch, "synthetic", 1, "t",
        new[] { Curve("JPY-OIS", "JPY", "TONA", "discount"), Curve("USD-OIS", "USD", "SOFR", "discount"),
                Curve("JPY-FLT-6M", "JPY", "TIBOR6M", "forward", 6), Curve("USD-XCCY-JPY", "USD", "XCCY-JPY", "xccy_discount") }.ToDictionary(c => c.Name),
        new Dictionary<string, QuoteSet>(), [], new Dictionary<string, double> { ["JPY"] = 150, ["USD"] = 1 });

    static EffectivePeriod Period(int n, double? rate, double notional = 1e9) =>
        new($"p{n}", ValuationDate.AddMonths(12 * n), ValuationDate.AddMonths(12 * (n + 1)), ValuationDate.AddMonths(12 * (n + 1)), notional, rate, 1.0, []);

    static EffectiveLeg Leg(string ccy, string index, bool floating, bool overnight, double? rate, LegDetail? detail = null,
        IReadOnlyList<NotionalExchange>? exchanges = null) =>
        new(new LegTerms(ccy, index, 1e9, null, Tenor.Parse("12m"), null, floating) { Detail = detail ?? LegDetail.None },
            Profile(ccy, index, overnight), [Period(0, rate), Period(1, rate)], exchanges ?? []);

    static PricingLine Irs(Direction direction = Direction.Pay, string? collateral = "JPY", LegDetail? floatDetail = null, string floatIndex = "TONA", bool overnight = true) =>
        new("L1", 3, direction, [Leg("JPY", "FIXED", false, false, 1.25), Leg("JPY", floatIndex, true, overnight, 0.10, floatDetail)], null, true, collateral);

    [Fact]
    public void A_paid_fixed_received_overnight_swap_becomes_two_signed_legs_on_the_collateral_curve()
    {
        var (table, refusals) = CashflowTables.From(Irs(), Map, Market, ValuationDate, _ => Calendar);

        Assert.Empty(refusals);
        Assert.Equal(("L1", "JPY"), (table!.Id, table.ReportingCurrency));
        var (fixedLeg, floatLeg) = (table.Legs[0], table.Legs[1]);
        Assert.Equal((-1, "fixed", "JPY-OIS"), (fixedLeg.Sign, fixedLeg.Kind, fixedLeg.DiscountCurve));
        Assert.Equal((1, "compounded", "JPY-OIS", "JPY-OIS"), (floatLeg.Sign, floatLeg.Kind, floatLeg.DiscountCurve, floatLeg.ForwardCurve));
        Assert.Equal(0.0125, fixedLeg.Periods[0].Rate!.Value, 15);   // percent on the grid, a decimal to the engine
        Assert.Equal(0.0010, floatLeg.Periods[0].Rate!.Value, 15);
        Assert.Equal(1.0, floatLeg.Periods[0].Gearing);
        Assert.Equal((floatLeg.Periods[0].AccrualStart, floatLeg.Periods[0].AccrualEnd),
            (floatLeg.Periods[0].ObservationStart, floatLeg.Periods[0].ObservationEnd));
    }

    [Fact]
    public void Receiving_flips_both_signs()
    {
        var (table, _) = CashflowTables.From(Irs(Direction.Receive), Map, Market, ValuationDate, _ => Calendar);
        Assert.Equal([1, -1], table!.Legs.Select(l => l.Sign));
    }

    [Fact] // C43: daily average, observation shift, lookback and gearing reach the engine as they were set
    public void Second_level_terms_choose_the_kind_and_shift_the_observation()
    {
        var (table, _) = CashflowTables.From(Irs(floatDetail: new LegDetail(DailyAverage: true, ObservationShiftDays: 2, LookbackDays: 0, Gearing: 0.5)),
            Map, Market, ValuationDate, _ => Calendar);

        var p = table!.Legs[1].Periods[0];
        Assert.Equal("average", table.Legs[1].Kind);
        Assert.Equal(DateRules.AddBusinessDays(p.AccrualStart, -2, Calendar), p.ObservationStart);
        Assert.Equal(DateRules.AddBusinessDays(p.AccrualEnd, -2, Calendar), p.ObservationEnd);
        Assert.Equal((0, 0.5), (p.Lookback, p.Gearing));
    }

    [Fact]
    public void A_term_index_fixes_two_business_days_before_its_period_and_in_arrears_at_its_end()
    {
        var (advance, _) = CashflowTables.From(Irs(floatIndex: "TIBOR6M", overnight: false), Map, Market, ValuationDate, _ => Calendar);
        var a = advance!.Legs[1].Periods[1];
        Assert.Equal(("term", "JPY-FLT-6M"), (advance.Legs[1].Kind, advance.Legs[1].ForwardCurve));
        Assert.Equal((DateRules.AddBusinessDays(a.AccrualStart, -2, Calendar), a.AccrualStart, a.AccrualStart.AddMonths(6)), (a.Fixing, a.IndexStart, a.IndexEnd));

        var (arrears, _) = CashflowTables.From(Irs(floatIndex: "TIBOR6M", overnight: false, floatDetail: new LegDetail(InArrears: true)), Map, Market, ValuationDate, _ => Calendar);
        var r = arrears!.Legs[1].Periods[1];
        Assert.Equal("term_in_arrears", arrears.Legs[1].Kind);
        Assert.Equal((DateRules.AddBusinessDays(r.AccrualEnd, -2, Calendar), r.AccrualEnd, r.AccrualEnd.AddMonths(6)), (r.Fixing, r.IndexStart, r.IndexEnd));
    }

    [Fact] // alpha rates-pricer.md §7: the worker makes no year fraction; an overnight leg says its day basis, a term period its index's
    public void An_overnight_leg_carries_its_day_basis_and_a_term_period_its_index_year_fraction()
    {
        var (overnight, _) = CashflowTables.From(Irs(), Map, Market, ValuationDate, _ => Calendar);
        Assert.Equal(360.0, overnight!.Legs[1].DayBasis);   // the leg's ACT/360
        Assert.Null(overnight.Legs[0].DayBasis);

        var (term, _) = CashflowTables.From(Irs(floatIndex: "TIBOR6M", overnight: false), Map, Market, ValuationDate, _ => Calendar);
        var p = term!.Legs[1].Periods[0];
        Assert.Equal((p.IndexEnd!.Value.DayNumber - p.IndexStart!.Value.DayNumber) / 360.0, p.IndexYearFraction!.Value, 15);
    }

    [Fact] // Akira 2026-11-17 (shared pricing layer): an empty Collateral discounts each leg on its own currency's RFR curve
    public void A_line_without_collateral_discounts_each_leg_on_its_own_currencys_curve()
    {
        var (table, refusals) = CashflowTables.From(Irs(collateral: null), Map, Market, ValuationDate, _ => Calendar);
        Assert.Empty(refusals);
        Assert.All(table!.Legs, l => Assert.Equal("JPY-OIS", l.DiscountCurve));   // JPY@JPY
    }

    [Fact]
    public void A_curve_the_engine_has_no_definition_for_is_refused_by_name()
    {
        var line = new PricingLine("L1", 1, Direction.Pay, [Leg("EUR", "FIXED", false, false, 1.0), Leg("EUR", "ESTR", true, true, 0)], null, false, "EUR");
        var (_, refusals) = CashflowTables.From(line, Map, Market, ValuationDate, _ => Calendar);
        Assert.Contains(refusals, r => r.Contains("EUR/ESTR"));
        Assert.Contains(refusals, r => r.Contains("EUR collateralised in EUR"));
    }

    [Fact] // C42: the cross-currency leg is discounted on its curve collateralised in the line's collateral currency
    public void A_cross_currency_swap_discounts_the_foreign_leg_on_its_collateral_curve_and_signs_its_exchanges()
    {
        var exchanges = new[] { new NotionalExchange(ExchangeKind.Initial, ValuationDate, 1e7), new NotionalExchange(ExchangeKind.Final, ValuationDate.AddYears(2), 1e7) };
        var line = new PricingLine("X1", 1, Direction.Pay,
            [Leg("USD", "SOFR", true, true, 0, exchanges: exchanges), Leg("JPY", "TONA", true, true, 0, exchanges: exchanges)], 2, true, "JPY");
        var (table, refusals) = CashflowTables.From(line, Map, Market, ValuationDate, _ => Calendar);

        Assert.Empty(refusals);
        Assert.Equal(("USD-XCCY-JPY", "USD-OIS"), (table!.Legs[0].DiscountCurve, table.Legs[0].ForwardCurve));
        Assert.Equal([-1e7, 1e7], table.Legs[0].Exchanges.Select(e => e.Amount));
        Assert.Equal(2, table.ParLeg);
    }

    [Fact] // CLAUDE.md: a rate that is missing is never zero; only the leg solved at par may leave it out
    public void A_period_without_a_rate_is_refused_unless_its_leg_is_solved()
    {
        var line = new PricingLine("L1", 1, Direction.Pay, [Leg("JPY", "FIXED", false, false, null), Leg("JPY", "TONA", true, true, 0)], null, false, "JPY");
        Assert.Contains(CashflowTables.From(line, Map, Market, ValuationDate, _ => Calendar).Refusals, r => r.Contains("no rate"));

        var solved = line with { ParLeg = 1 };
        var (table, refusals) = CashflowTables.From(solved, Map, Market, ValuationDate, _ => Calendar);
        Assert.Empty(refusals);
        Assert.Null(table!.Legs[0].Periods[0].Rate);
    }
}
