using System.Globalization;
using System.Text.Json.Nodes;
using Workbench.Composition.Contracts;

namespace Workbench.Samples.Ccr;

// Spot CCR capital over a frozen portfolio (ADR-0013 §2, §3, §6): what the reference implementation reads from the
// accepted input, how a portfolio trade becomes SA-CCR's exposures and netting sets, and the capital profile KVA reads.
// The names and amounts it reads are the portfolio's; the rules are SaCcr's and RiskWeights'.

/// <summary>
/// A trade as spot capital reads it. Amounts are in the trade's currency. <c>Direction</c> is <c>payFixed</c> or
/// <c>receiveFixed</c> for an IRS, and <c>buy</c> or <c>sell</c> of <c>Currency</c> against <c>CounterCurrency</c> for
/// an FX forward. A field the source did not give is null, and the trade cannot be decomposed without it.
/// </summary>
public sealed record CapitalTrade(
    string TradeId, bool IsNew, string Product, string Currency, double Notional, DateOnly Maturity, string Csa,
    bool Before, bool After, double? OfficialPv = null, DateOnly? Start = null, string? Direction = null,
    string? CounterCurrency = null, double? CounterNotional = null);

/// <summary>A CSA's terms as SA-CCR reads them: how often it margins and the counterparty's threshold and MTA.</summary>
public sealed record CapitalCsa(
    string CsaId, string Currency, string Frequency, bool Exchange, bool OneWay, double Threshold, double Mta, string ThresholdCurrency,
    int Disputes = 0, bool IlliquidCollateral = false);

/// <summary>
/// Collateral held (<c>received</c>) or posted under a CSA, in its currency. A bond names its issuer's grade and its
/// residual maturity, which its haircut reads (CRE22.49).
/// </summary>
public sealed record CapitalCollateral(
    string Id, string CsaId, CollateralDirection Direction, CollateralPurpose Purpose, string Type, string Currency, double Amount,
    bool Segregated = false, string? IssuerGrade = null, double? ResidualYears = null);

/// <summary>
/// What a trade's decomposition reads beside the trade: the valuation date, the reporting currency, and the FX the
/// request froze, in units of the reporting currency per unit of each currency.
/// </summary>
public sealed record CapitalMarket(DateOnly ValuationDate, string ReportingCurrency, IReadOnlyDictionary<string, double> ToReporting);

/// <summary>κ, SA-CCR's choices and the jurisdiction's risk weight choices, as configured and frozen into the Run.</summary>
public sealed record CapitalSettings(CapitalRatio Kappa, SaCcrSettings SaCcr, RiskWeightSettings RiskWeight);

/// <summary>
/// One counterparty's portfolio as spot capital reads it: its netting set, CSAs, collateral and trades, the FX the
/// request froze (units of the reporting currency per unit of each currency) and the settings.
/// </summary>
public sealed record CapitalPortfolio(
    DateOnly ValuationDate,
    string ReportingCurrency,
    IReadOnlyDictionary<string, double> ToReporting,
    TraceSubject Counterparty,
    CounterpartyRiskFacts Facts,
    string NettingSetId,
    bool Enforceable,
    IReadOnlyList<CapitalCsa> Csas,
    IReadOnlyList<CapitalCollateral> Collateral,
    IReadOnlyList<CapitalTrade> Trades,
    CapitalSettings Settings)
{
    public CapitalMarket Market => new(ValuationDate, ReportingCurrency, ToReporting);
}

/// <summary>Spot capital on one side: the figure and its trace, or why it could not be computed. Never 0 for either.</summary>
public sealed record SideCapital(string Side, int Trades, SpotCapital? Capital = null, CalculationTrace? Trace = null, string? Refused = null);

/// <summary>One point of the capital profile: the trades still alive at that date and the capital they hold.</summary>
public sealed record CapitalPoint(double Years, DateOnly Date, int Trades, double Ead, double Capital);

public static class SpotCapitalPricing
{
    public const string Before = "before";
    public const string After = "after";

    /// <summary>The engine every trace and figure of the reference implementation names.</summary>
    public static readonly TraceProducer Engine = new("Workbench reference SA-CCR and SA risk weight (simulated)", "1.0");

    /// <summary>
    /// The profile's dates, in years from the valuation date, up to the last maturity (DEFAULT). A date past every
    /// maturity holds no capital and is not written.
    /// </summary>
    public static readonly IReadOnlyList<double> ProfileYears = [0, 0.25, 0.5, 1, 2, 3, 5, 7, 10, 15, 20, 30];

    static readonly IReadOnlyDictionary<string, string> LocalCurrencies = new Dictionary<string, string>
    {
        ["JP"] = "JPY", ["US"] = "USD", ["GB"] = "GBP", ["DE"] = "EUR", ["FR"] = "EUR", ["NL"] = "EUR",
    };

    /// <summary>
    /// The exposures SA-CCR reads for a trade (ADR-0013 §3). A product SA-CCR's decomposition does not know, or a trade
    /// without the fields its decomposition needs, is refused by name.
    /// </summary>
    public static IReadOnlyList<SaCcrExposure> Exposures(CapitalTrade t, CapitalMarket p)
    {
        DateOnly Start() => t.Start ?? throw new SaCcrRefused($"{t.TradeId}: the source gives no start date, which its decomposition needs.");
        string Direction(params string[] known) => t.Direction is { } d && known.Contains(d)
            ? d
            : throw new SaCcrRefused($"{t.TradeId}: the direction is {(t.Direction is null ? "not given" : $"'{t.Direction}'")}; a {t.Product} is {string.Join(" or ", known)}.");
        if (t.Maturity <= p.ValuationDate)
            throw new SaCcrRefused($"{t.TradeId}: it matured on {Date(t.Maturity)}, on or before the valuation date {Date(p.ValuationDate)}.");
        switch (t.Product)
        {
            case "IRS":
                return [Decompose.InterestRateSwap(t.TradeId, t.Currency, t.Notional * Rate(t.TradeId, t.Currency, p), p.ValuationDate, Start(), t.Maturity,
                    Direction("payFixed", "receiveFixed") == "payFixed")];
            case "FXForward":
            {
                var buys = Direction("buy", "sell") == "buy";
                var counter = t.CounterCurrency ?? throw new SaCcrRefused($"{t.TradeId}: the source gives no counter currency, so the pair is not known.");
                var counterAmount = t.CounterNotional ?? throw new SaCcrRefused($"{t.TradeId}: the source gives no counter amount, so the other leg is not known.");
                return buys
                    ? [Decompose.FxForward(t.TradeId, t.Currency, t.Notional, counter, counterAmount, p.ValuationDate, t.Maturity, p.ReportingCurrency, p.ToReporting)]
                    : [Decompose.FxForward(t.TradeId, counter, counterAmount, t.Currency, t.Notional, p.ValuationDate, t.Maturity, p.ReportingCurrency, p.ToReporting)];
            }
            default:
                throw new SaCcrRefused($"{t.TradeId}: a {t.Product} is not a product SA-CCR's decomposition knows (IRS, FXForward).");
        }
    }

    /// <summary>
    /// What SA-CCR decided a trade is, for its row in the trade grid (ADR-0013 §6): asset class, hedging set, bucket
    /// and delta sign, one exposure after another.
    /// </summary>
    public static string Classification(IReadOnlyList<SaCcrExposure> exposures) =>
        string.Join("; ", exposures.Select(e =>
        {
            var sign = e.Delta is LinearTerms linear ? (linear.Long ? "long" : "short") : "option";
            return e.AssetClass switch
            {
                AssetClass.InterestRate => $"Interest rate · {e.HedgingSet} · {SaCcr.BucketNames[SaCcr.BucketOf(e.EndYears ?? e.MaturityYears) - 1]} · {sign}",
                AssetClass.ForeignExchange => $"FX · {e.HedgingSet} · {sign}",
                _ => $"{e.AssetClass} · {e.HedgingSet} · {sign}",
            };
        }));

    /// <summary>
    /// The netting sets of one side's trades. The trades of a netting set are calculated by the CSA they are on, each
    /// group with its CSA's margining and the uncollateralised ones without (DEFAULT, 2026-11-08). A group is named
    /// <c>{netting set} {CSA}</c>, except the one group an agreement can have alone: its only CSA's, or, with no CSA,
    /// the uncollateralised one, which keeps the netting set's name. A name depends on the agreement and the trade's
    /// CSA, never on which other trades the side holds, so Before and After pair.
    /// </summary>
    public static IReadOnlyList<SaCcrNettingSet> NettingSets(CapitalPortfolio p, IReadOnlyList<CapitalTrade> trades, double horizonYears = 0)
    {
        return [.. trades.GroupBy(t => t.Csa).OrderBy(g => g.Key, StringComparer.Ordinal).Select(group =>
        {
            var csa = p.Csas.SingleOrDefault(c => c.CsaId == group.Key);
            var alone = p.Csas.Count == 0 || (p.Csas.Count == 1 && csa is not null);
            var id = alone ? p.NettingSetId : $"{p.NettingSetId} {group.Key}";
            var saCcrTrades = group.Select(t => Trade(t, p, horizonYears)).ToList();
            if (csa is null) return new SaCcrNettingSet(id, p.ReportingCurrency, saCcrTrades, LegallyEnforceable: p.Enforceable);
            return new SaCcrNettingSet(id, csa.Currency, saCcrTrades, Margining(csa, p),
                [.. p.Collateral.Where(c => c.CsaId == csa.CsaId).Select(c => Balance(c, p))], p.Enforceable);
        })];
    }

    /// <summary>
    /// Why a CSA's margining cannot be read as SA-CCR's, or null when it can. One-way, or without collateral exchanged,
    /// the CSA's terms do not say which party's threshold governs, and the figure is refused rather than assumed.
    /// </summary>
    public static string? MarginingProblem(CapitalCsa csa) =>
        csa.OneWay ? $"{csa.CsaId} is one-way, and its terms do not say which party posts, so its margined exposure cannot be decided."
        : !csa.Exchange ? $"{csa.CsaId} exchanges no collateral, so it is not a margin agreement SA-CCR can read."
        : RemarginingDays(csa.Frequency) is null ? $"{csa.CsaId} margins {csa.Frequency}, which is not a remargining period SA-CCR knows (daily, weekly, monthly)."
        : null;

    /// <summary>Business days between margin calls (CRE52.50): a week is 5, a month 20 (DEFAULT).</summary>
    static int? RemarginingDays(string frequency) => frequency switch { "daily" => 1, "weekly" => 5, "monthly" => 20, _ => null };

    static Margining Margining(CapitalCsa csa, CapitalPortfolio p)
    {
        if (MarginingProblem(csa) is { } problem) throw new SaCcrRefused(problem);
        var rate = Rate(csa.CsaId, csa.ThresholdCurrency, p.Market);
        return new Margining(csa.CsaId, RemarginingDays(csa.Frequency)!.Value, csa.Threshold * rate, csa.Mta * rate, csa.IlliquidCollateral, csa.Disputes);
    }

    static CollateralBalance Balance(CapitalCollateral c, CapitalPortfolio p) =>
        new(c.Id, c.Direction, c.Purpose, c.Type, c.Currency, c.Amount * Rate(c.Id, c.Currency, p.Market), Haircut(c), c.Segregated);

    /// <summary>
    /// The supervisory haircut at ten business days (CRE22.49): 0 for cash, and for a sovereign bond by its issuer's
    /// grade and residual maturity. Any other collateral is refused by name, never given a haircut it might not have.
    /// </summary>
    public static double Haircut(CapitalCollateral c)
    {
        if (c.Type == "cash") return 0;
        if (c.Type != "sovereignBond")
            throw new SaCcrRefused($"{c.Id}: the haircut of {c.Type} collateral is not one the reference implementation holds (cash, sovereign bonds).");
        var years = c.ResidualYears ?? throw new SaCcrRefused($"{c.Id}: a bond's residual maturity is not given, and its haircut reads it.");
        var notch = c.IssuerGrade is { } grade ? RiskWeights.NotchOf(grade) : -1;
        double[] row = notch switch
        {
            >= 0 and <= 3 => [0.005, 0.02, 0.02, 0.04, 0.04],
            >= 4 and <= 9 => [0.01, 0.03, 0.03, 0.06, 0.06],
            >= 10 and <= 12 => [0.15, 0.15, 0.15, 0.15, 0.15],
            _ => throw new SaCcrRefused($"{c.Id}: the issuer grade {(c.IssuerGrade ?? "is not given and")} is not eligible as collateral (CRE22.49)."),
        };
        return row[years <= 1 ? 0 : years <= 3 ? 1 : years <= 5 ? 2 : years <= 10 ? 3 : 4];
    }

    static SaCcrTrade Trade(CapitalTrade t, CapitalPortfolio p, double horizonYears)
    {
        var exposures = Exposures(t, p.Market);
        if (horizonYears > 0) exposures = [.. exposures.Select(e => Rolled(e, horizonYears))];
        // V (CRE52.10): the official PV for an existing trade (ADR-0010 §6); a new one is at market when it is traded.
        var (value, source) = t.IsNew
            ? (0.0, "new trade, at market (simulated)")
            : (t.OfficialPv ?? throw new SaCcrRefused($"{t.TradeId}: an existing trade without its official PV."), "official PV");
        return new SaCcrTrade(t.TradeId, value * Rate(t.TradeId, t.Currency, p.Market), exposures, source);
    }

    static SaCcrExposure Rolled(SaCcrExposure e, double years) => e with
    {
        MaturityYears = e.MaturityYears - years,
        StartYears = e.StartYears is { } s ? Math.Max(0, s - years) : null,
        EndYears = e.EndYears - years,
        Delta = e.Delta is OptionTerms option ? option with { ExerciseYears = Math.Max(0, option.ExerciseYears - years) } : e.Delta,
    };

    /// <summary>
    /// What the risk weight reads about the side's trades: the one with the longest original maturity, and whether
    /// every currency they are in is the local currency of the counterparty's country of incorporation.
    /// </summary>
    public static ExposureFacts ExposureFacts(CapitalPortfolio p, IReadOnlyList<CapitalTrade> trades)
    {
        var longest = trades
            .Select(t => new OriginalMaturity(t.TradeId, t.Start ?? throw new SaCcrRefused($"{t.TradeId}: the source gives no start date, and the risk weight reads the original maturity."), t.Maturity))
            .OrderByDescending(m => m.End.DayNumber - m.Start.DayNumber).ThenBy(m => m.TradeId, StringComparer.Ordinal).First();
        var country = p.Facts.CountryOfIncorporation;
        var local = country is not null && LocalCurrencies.TryGetValue(country, out var currency)
            ? currency
            : throw new RiskWeightRefused($"{p.Counterparty.Id}: no local currency is known for its country of incorporation {(country ?? "(not given)")}.");
        var currencies = trades.SelectMany(t => t.CounterCurrency is { } c ? new[] { t.Currency, c } : [t.Currency]);
        return new ExposureFacts(longest, currencies.All(c => c == local));
    }

    /// <summary>The trades on one side, in the accepted input's order.</summary>
    public static IReadOnlyList<CapitalTrade> On(CapitalPortfolio p, string side) => side switch
    {
        Before => [.. p.Trades.Where(t => t.Before)],
        After => [.. p.Trades.Where(t => t.After)],
        _ => throw new ArgumentException($"No side {side}.", nameof(side)),
    };

    /// <summary>
    /// Why spot capital cannot be computed on one side, or null when it can: the same reading as <see cref="Price"/>,
    /// without writing the trace, so that what Price refuses and what the provider computes never differ.
    /// </summary>
    public static string? Problem(CapitalPortfolio p, string side)
    {
        var trades = On(p, side);
        if (trades.Count == 0) return null;
        try
        {
            SaCcr.Calculate(NettingSets(p, trades), p.Settings.SaCcr);
            RiskWeights.Resolve(p.Facts, ExposureFacts(p, trades), p.Settings.RiskWeight);
            return null;
        }
        catch (Exception e) when (e is SaCcrRefused or RiskWeightRefused)
        {
            return e.Message;
        }
    }

    /// <summary>
    /// Spot capital on one side and its trace. A side with no trade is not priced; a refusal says why, by name.
    /// </summary>
    public static SideCapital Price(CapitalPortfolio p, string side, string requestId, DatasetProvenance provenance)
    {
        var trades = On(p, side);
        if (trades.Count == 0) return new SideCapital(side, 0);
        try
        {
            var exposure = SaCcr.Calculate(NettingSets(p, trades), p.Settings.SaCcr);
            var riskWeight = RiskWeights.Resolve(p.Facts, ExposureFacts(p, trades), p.Settings.RiskWeight);
            var capital = SpotCapital.Of(exposure, riskWeight, p.Settings.Kappa);
            var header = new SpotCapitalTraceHeader(p.Counterparty, p.ReportingCurrency, Engine, Date(p.ValuationDate), requestId, provenance);
            return new SideCapital(side, trades.Count, capital, SpotCapitalTrace.Write(capital, header));
        }
        catch (Exception e) when (e is SaCcrRefused or RiskWeightRefused)
        {
            return new SideCapital(side, trades.Count, Refused: e.Message);
        }
    }

    /// <summary>
    /// The capital profile of one side (ADR-0013 §1, without a trace): at each date, the spot rules applied to the
    /// trades still alive, their exposures rolled to that date, with today's market values, collateral and risk
    /// weight held. Nothing is simulated, and the profile says so where it is shown.
    /// </summary>
    public static IReadOnlyList<CapitalPoint> Profile(CapitalPortfolio p, string side, SpotCapital spot)
    {
        var trades = On(p, side);
        var points = new List<CapitalPoint>();
        foreach (var years in ProfileYears)
        {
            var date = p.ValuationDate.AddDays((int)Math.Round(years * 365));
            var alive = trades.Where(t => t.Maturity > date).ToList();
            if (alive.Count == 0) break;
            var ead = years == 0 ? spot.Exposure.Ead : SaCcr.Calculate(NettingSets(p, alive, years), p.Settings.SaCcr).Ead;
            points.Add(new CapitalPoint(years, date, alive.Count, ead, spot.Kappa.Value * spot.RiskWeight.RiskWeight * ead));
        }
        return points;
    }

    static double Rate(string what, string currency, CapitalMarket p) =>
        p.ToReporting.TryGetValue(currency, out var rate) && rate > 0
            ? rate
            : throw new SaCcrRefused($"{what}: the request froze no FX rate from {currency} to {p.ReportingCurrency}.");

    static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

/// <summary>
/// Reads spot capital's portfolio from a portfolio accepted input: the shared trades and context, and the ccr module's
/// own part (its counterparty facts, CSA terms, collateral, FX and settings). A part that is missing or not of its type
/// is refused by name, never defaulted.
/// </summary>
public static class CapitalInput
{
    public static CapitalPortfolio Read(JsonObject acceptedInput, string module)
    {
        var ccr = acceptedInput["modules"]?[module] as JsonObject
            ?? throw new SaCcrRefused($"The accepted input has no {module} part, which spot capital reads.");
        var valuation = DateOnly.ParseExact(Text(acceptedInput["context"]?["valueDate"], "context.valueDate"), "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var counterparty = Obj(ccr["counterparty"], "counterparty");
        var netting = Obj(ccr["nettingSet"], "nettingSet");
        var settings = Obj(ccr["settings"], "settings");
        var kappa = Obj(settings["kappa"], "settings.kappa");
        var saCcr = Obj(settings["saCcr"], "settings.saCcr");
        var riskWeight = Obj(settings["riskWeight"], "settings.riskWeight");
        var id = Text(counterparty["id"], "counterparty.id");
        var type = Text(counterparty["type"], "counterparty.type");
        if (!Enum.TryParse<InstitutionType>(type, ignoreCase: true, out var institution) || int.TryParse(type, out _))
            throw new RiskWeightRefused($"{id}: '{type}' is not an institution type the exposure class can be decided from.");
        var facts = new CounterpartyRiskFacts(
            Text(counterparty["code"], "counterparty.code"), Text(counterparty["legalEntity"], "counterparty.legalEntity"), institution,
            Ratings(counterparty["ratings"], "counterparty.ratings"),
            OptionalText(counterparty["country"]),
            new SovereignFacts(Ratings(counterparty["sovereignRatings"], "counterparty.sovereignRatings")),
            OptionalBool(counterparty["supervisedAsBank"]),
            OptionalText(counterparty["scraGrade"]) is { } grade
                ? new ScraAssessment(Enum.TryParse<ScraGrade>(grade, out var g) ? g : throw new RiskWeightRefused($"{id}: '{grade}' is not an SCRA grade (A, B, C)."))
                : null,
            counterparty["dueDiligenceBucketsUp"]?.GetValue<int>(),
            OptionalBool(counterparty["sme"]),
            OptionalBool(counterparty["investmentGrade"]));

        return new CapitalPortfolio(
            valuation,
            Text(ccr["reportingCurrency"], "reportingCurrency"),
            Obj(ccr["fx"], "fx").ToDictionary(p => p.Key, p => Number(p.Value, $"fx.{p.Key}")),
            new TraceSubject("counterparty", id, OptionalText(counterparty["name"])),
            facts,
            Text(netting["id"], "nettingSet.id"),
            Bool(netting["enforceable"], "nettingSet.enforceable"),
            [.. Arr(ccr["csas"], "csas").Select(c => Obj(c, "csas[]")).Select(c => new CapitalCsa(
                Text(c["csaId"], "csa.csaId"), Text(c["currency"], "csa.currency"), Text(c["frequency"], "csa.frequency"),
                Bool(c["exchange"], "csa.exchange"), Bool(c["oneWay"], "csa.oneWay"),
                Number(c["threshold"], "csa.threshold"), Number(c["mta"], "csa.mta"), Text(c["thresholdCurrency"], "csa.thresholdCurrency"),
                c["disputes"]?.GetValue<int>() ?? throw Missing("csa.disputes"), Bool(c["illiquidCollateral"], "csa.illiquidCollateral")))],
            [.. Arr(ccr["collateral"], "collateral").Select(c => Obj(c, "collateral[]")).Select(c => new CapitalCollateral(
                Text(c["id"], "collateral.id"), Text(c["csaId"], "collateral.csaId"),
                Text(c["direction"], "collateral.direction") switch
                {
                    "received" => CollateralDirection.Received,
                    "posted" => CollateralDirection.Posted,
                    var other => throw new SaCcrRefused($"Collateral {c["id"]}: direction '{other}' is not received or posted."),
                },
                Text(c["purpose"], "collateral.purpose") switch
                {
                    "variationMargin" => CollateralPurpose.VariationMargin,
                    "independentAmount" => CollateralPurpose.IndependentAmount,
                    var other => throw new SaCcrRefused($"Collateral {c["id"]}: purpose '{other}' is not variationMargin or independentAmount."),
                },
                Text(c["type"], "collateral.type"), Text(c["currency"], "collateral.currency"), Number(c["amount"], "collateral.amount"),
                Bool(c["segregated"], "collateral.segregated"), OptionalText(c["issuerGrade"]),
                c["residualYears"] is { } residual ? Number(residual, "collateral.residualYears") : null))],
            [.. Arr(acceptedInput["trades"], "trades").Select(t => Obj(t, "trades[]")).Select(t => new CapitalTrade(
                Text(t["tradeId"], "trade.tradeId"), Bool(t["isNew"], "trade.isNew"), Text(t["product"], "trade.product"),
                Text(t["currency"], "trade.currency"), Number(t["notional"], "trade.notional"),
                DateOnly.ParseExact(Text(t["maturity"], "trade.maturity"), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                Text(t["csa"], "trade.csa"), Bool(t["before"], "trade.before"), Bool(t["after"], "trade.after"),
                t["officialPv"] is { } pv ? Number(pv, "trade.officialPv") : null,
                OptionalText(t["start"]) is { } start ? DateOnly.ParseExact(start, "yyyy-MM-dd", CultureInfo.InvariantCulture) : null,
                OptionalText(t["direction"]), OptionalText(t["counterCurrency"]),
                t["counterNotional"] is { } counter ? Number(counter, "trade.counterNotional") : null))],
            new CapitalSettings(
                new CapitalRatio(Number(kappa["value"], "settings.kappa.value"), Text(kappa["key"], "settings.kappa.key"), Text(kappa["version"], "settings.kappa.version")),
                new SaCcrSettings(Text(saCcr["version"], "settings.saCcr.version"), Bool(saCcr["irOffsetAcrossBuckets"], "settings.saCcr.irOffsetAcrossBuckets")),
                new RiskWeightSettings(Bool(riskWeight["externalRatingsAllowed"], "settings.riskWeight.externalRatingsAllowed"), Text(riskWeight["version"], "settings.riskWeight.version"))));
    }

    static IReadOnlyList<ExternalRating> Ratings(JsonNode? node, string what) =>
        [.. Arr(node, what).Select(r => Obj(r, what)).Select(r => new ExternalRating(Text(r["agency"], $"{what}.agency"), Text(r["grade"], $"{what}.grade")))];

    static SaCcrRefused Missing(string what) => new($"The accepted input's spot capital part has no {what}, or it is not of its type.");

    static JsonObject Obj(JsonNode? node, string what) => node as JsonObject ?? throw Missing(what);

    static JsonArray Arr(JsonNode? node, string what) => node as JsonArray ?? throw Missing(what);

    static string Text(JsonNode? node, string what) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : throw Missing(what);

    static string? OptionalText(JsonNode? node) => node is null ? null : Text(node, "text");

    static bool Bool(JsonNode? node, string what) => node is JsonValue v && v.TryGetValue<bool>(out var b) ? b : throw Missing(what);

    static bool? OptionalBool(JsonNode? node) => node is null ? null : Bool(node, "flag");

    static double Number(JsonNode? node, string what) => node is JsonValue v && v.TryGetValue<double>(out var d) ? d : throw Missing(what);
}
