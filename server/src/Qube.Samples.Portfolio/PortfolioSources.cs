using System.Globalization;
using System.Text.Json.Nodes;
using Qube.Composition.Application.Workspaces;

namespace Qube.Samples.Portfolio;

// Synthetic portfolio sources (slice 02; shared by the measures since ADR-0010 §10). Every name, identifier and number here is made up; nothing is a real
// counterparty, agreement, curve or convention.

/// <summary>A counterparty registration. The code alone is not an identity: the legal entity is part of it.</summary>
public sealed record Registration(string Code, string LegalEntity, string Name)
{
    public string Key => $"{Code}/{LegalEntity}";
}

/// <summary>A master agreement with its netting set and CSAs. An uncollateralised one has none, only its sentinel.
/// Enforceability is the source's and read-only (docs/03 §2.2).</summary>
public sealed record Agreement(string MaId, string NettingSetId, string RegistrationKey, IReadOnlyList<Csa> Csas, string? MainCsaId, bool Enforceable = true)
{
    public bool Collateralised => Csas.Count > 0;
    public string NoCsa => $"nocsa_{MaId}";

    /// <summary>The counterparty's IM CSAs (ADR-0012 §2), at most one per direction here; none where it exchanges no IM.</summary>
    public IReadOnlyList<ImCsaTerms> ImCsas { get; init; } = [];

    /// <summary>The IM CSA of a direction (<see cref="ImCsaTerms.Collect"/> or <see cref="ImCsaTerms.Post"/>), or null where there is none.</summary>
    public ImCsaTerms? ImCsaOf(string direction) => ImCsas.SingleOrDefault(c => c.Direction == direction);
}

/// <summary>A regulatory regime an IM CSA serves, and the date the counterparty came into its scope (ADR-0012 §2).</summary>
public sealed record ImRegime(string Regime, DateOnly Since);

/// <summary>
/// An IM CSA as the source holds it (ADR-0012 §2, §3): its direction, IM-R collected from the counterparty or IM-P posted
/// to it; the regimes it serves; the currency SIMM is calculated in; and the SIMM version, which names the parameter
/// table it computes with.
/// </summary>
public sealed record ImCsaTerms(string ImCsaId, string Direction, IReadOnlyList<ImRegime> Regimes, string CalculationCurrency, string SimmVersion)
{
    public const string Collect = "collect";
    public const string Post = "post";
    public static readonly IReadOnlyList<string> Directions = [Collect, Post];
}

/// <summary>A trade's standing under one regime as the source classifies it (ADR-0012 §2): in, or out with why.</summary>
public sealed record RegimeStanding(string Regime, bool In, string Reason);

/// <summary>
/// How the source classifies a trade for IM (ADR-0012 §2): exempt everywhere, with why, or its standing under each regime
/// its direction's IM CSA serves. Which regimes a trade counts under is the source's to say, never the page's.
/// </summary>
public sealed record ImClassification(string? Exemption, IReadOnlyList<RegimeStanding> Collect, IReadOnlyList<RegimeStanding> Post)
{
    public IReadOnlyList<RegimeStanding> Of(string direction) => direction == ImCsaTerms.Collect ? Collect : Post;
}

/// <summary>
/// A CSA's terms (ADR-0010 §8): its type, currency, discount and FVA discount curves, how often it margins, whether
/// collateral is exchanged, whether it is one-way, and each party's threshold and minimum transfer amount in its
/// currency. The collateral desk's record adds what SA-CCR's margin period of risk reads (ADR-0013 §3): the margin call
/// disputes of the last two quarters that lasted longer than the margin period, and whether the collateral held is
/// illiquid or a trade under it cannot easily be replaced.
/// </summary>
public sealed record Csa(
    string CsaId, string Type, string Currency, string Curve, string FvaCurve, string Frequency, bool Exchange, bool OneWay,
    decimal CounterpartyThreshold, decimal CounterpartyMta, string CounterpartyCurrency,
    decimal SelfThreshold, decimal SelfMta, string SelfCurrency,
    int Disputes = 0, bool IlliquidCollateral = false);

/// <summary>
/// One acquired trade, as the source reports it for one value date. Its start and direction are what SA-CCR's
/// decomposition reads (ADR-0013 §3): an IRS pays or receives fixed (<c>payFixed</c>, <c>receiveFixed</c>), and an FX
/// forward buys or sells (<c>buy</c>, <c>sell</c>) its notional in <c>Currency</c> against <c>CounterNotional</c> in
/// <c>CounterCurrency</c>. A source that does not give one leaves it null, and a measure that needs it refuses.
/// </summary>
public sealed record SourceTrade(
    string TradeId,
    bool IsNew,
    string Product,
    string Currency,
    decimal Notional,
    DateOnly Maturity,
    decimal? FixedRate,
    string? Schedule,
    decimal? LastFixing,
    string? Csa,
    // The official PV the books hold for an existing trade (ADR-0010 §6), in its currency; a new trade has none.
    decimal? OfficialPv = null,
    DateOnly? Start = null,
    string? Direction = null,
    string? CounterCurrency = null,
    decimal? CounterNotional = null,
    // The IM CSAs the trade is held under (ADR-0012 §2): IM-R and IM-P. A new trade has none of its own: it takes the
    // counterparty's.
    string? ImR = null,
    string? ImP = null)
{
    /// <summary>The trade's PV legs as the books hold them (ADR-0016 note 49); a new trade has none, since no book holds it.</summary>
    public IReadOnlyList<SourceLeg> Legs { get; init; } = [];

    /// <summary>The independent amounts held on the trade (ADR-0016 note 49).</summary>
    public IReadOnlyList<SourceAmount> Amounts { get; init; } = [];
}

/// <summary>
/// One PV leg of a trade as the books hold it (ADR-0016 note 49): its PV and notional in its own currency. Its id is
/// its trade's, a slash, and its number: <c>T/1</c>.
/// </summary>
public sealed record SourceLeg(string LegId, string TradeId, string Currency, decimal Pv, decimal Notional);

/// <summary>
/// An independent amount held on a trade (ADR-0016 note 49), in its currency, from its start to its end. Its id is its
/// trade's, a slash, <c>IA</c> and its number, <c>T/IA1</c>, so that no leg and amount of a trade share an id.
/// </summary>
public sealed record SourceAmount(string AmountId, string TradeId, string Currency, DateOnly Start, DateOnly End, decimal Value);

/// <summary>The configured baseline profile: the curve an uncollateralised trade starts on (docs/03 §2.4), and the
/// currency figures are shown in until the user chooses another (ADR-0010 §1).</summary>
public sealed record BaselineProfile(string ProfileVersion, string UncollateralisedCurve, string DisplayCurrency);

public static class PortfolioCatalog
{
    public static readonly IReadOnlyList<Registration> Registrations =
    [
        new("ABC", "LE-01", "ABC Holdings (synthetic)"),
        new("ABC", "LE-02", "ABC Securities (synthetic)"),
        new("XYZ", "LE-03", "XYZ Industries (synthetic)"),
    ];

    public static readonly IReadOnlyList<string> Curves = ["jpy_ois", "usd_sofr", "eur_estr"];
    public static readonly IReadOnlyList<string> FundingCurves = ["jpy_funding", "usd_funding", "eur_funding"];
    public static readonly IReadOnlyList<string> Frequencies = ["daily", "weekly", "monthly"];

    // CSA-A is perfectly collateralised; CSA-B's counterparty threshold puts it past FVA's 1mm GBP; CSA-C margins daily
    // with small amounts, until a threshold is raised (ADR-0010 §5). ABC/LE-01 exchanges IM under JFSA and, from dates
    // that leave some of its trades out, CFTC and EMIR; ABC/LE-02 came into JFSA's scope after most of its trades; XYZ
    // exchanges none (ADR-0012 §2). The dates are illustrative.
    public static readonly IReadOnlyList<Agreement> Agreements =
    [
        new("MA-001", "NS-001", "ABC/LE-01",
            [new("CSA-A", "VM", "JPY", "jpy_ois", "jpy_funding", "daily", true, false, 0m, 50_000_000m, "JPY", 0m, 50_000_000m, "JPY"),
             new("CSA-B", "VM", "USD", "usd_sofr", "usd_funding", "daily", true, false, 1_000_000m, 500_000m, "USD", 0m, 250_000m, "USD", Disputes: 3)], "CSA-A")
        {
            ImCsas =
            [
                new("IMR-ABC-01", ImCsaTerms.Collect, [new("JFSA", new(2022, 9, 1)), new("CFTC", new(2025, 9, 1))], "USD", "DEMO-2.x"),
                new("IMP-ABC-01", ImCsaTerms.Post, [new("JFSA", new(2022, 9, 1)), new("EMIR", new(2024, 9, 1))], "USD", "DEMO-2.x"),
            ],
        },
        new("MA-002", "NS-002", "ABC/LE-02",
            [new("CSA-C", "VM", "USD", "usd_sofr", "usd_funding", "daily", true, false, 0m, 250_000m, "USD", 0m, 250_000m, "USD")], "CSA-C")
        {
            ImCsas =
            [
                new("IMR-ABC-02", ImCsaTerms.Collect, [new("JFSA", new(2026, 9, 1))], "USD", "DEMO-2.x"),
                new("IMP-ABC-02", ImCsaTerms.Post, [new("JFSA", new(2026, 9, 1))], "USD", "DEMO-2.x"),
            ],
        },
        new("MA-003", "NS-003", "XYZ/LE-03", [], null, Enforceable: false),
    ];

    public static readonly BaselineProfile Profile = new("profile-2026-10", "jpy_ois", "JPY");

    public static Registration Find(string key) =>
        Registrations.SingleOrDefault(r => r.Key == key) ?? throw new ArgumentException($"No registration {key}.");

    public static Agreement AgreementOf(string registrationKey) =>
        Agreements.Single(a => a.RegistrationKey == registrationKey);
}

/// <summary>
/// What the synthetic source answers for one registration, value date and portfolio size. Deterministic: the
/// same request gives the same trades. A trade past its maturity is absent, and a trade's last fixing moves
/// when a fixing date has passed, so another value date is another version of the same trades.
/// </summary>
public sealed class PortfolioSourceVersion : ISourceVersion
{
    public Registration Registration { get; }
    public Agreement Agreement { get; }
    public DateOnly ValueDate { get; }
    public IReadOnlyList<SourceTrade> Existing { get; }
    public IReadOnlyList<SourceTrade> New { get; }
    public decimal? CvaRecovery { get; }
    /// <summary>The counterparty's CDS as the source names it (ADR-0010 §8's note of 2026-11-09).</summary>
    public CdsChoice Cds { get; }
    public string Version { get; }
    public JsonObject Descriptor => new() { ["registration"] = Registration.Key, ["valueDate"] = ValueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ["existingCount"] = existingCount };

    readonly int existingCount;
    readonly Dictionary<string, SourceTrade> trades;
    readonly Dictionary<string, SourceLeg> legs;
    readonly Dictionary<string, SourceAmount> amounts;

    public PortfolioSourceVersion(string registrationKey, DateOnly valueDate, int existingCount)
    {
        Registration = PortfolioCatalog.Find(registrationKey);
        Agreement = PortfolioCatalog.AgreementOf(registrationKey);
        ValueDate = valueDate;
        Existing = [.. Enumerable.Range(1, existingCount).Select(i => Generate(Registration, $"{Registration.Code}-{Registration.LegalEntity}-E{i:D5}", false, valueDate)).OfType<SourceTrade>()];
        this.existingCount = existingCount;
        // Drafts the user loaded belong to no counterparty, so they stay through a counterparty change (D07).
        New = [.. Enumerable.Range(1, 3).Select(i => Generate(Registration, $"DRAFT-{i}", true, valueDate)).OfType<SourceTrade>()];
        trades = Existing.Concat(New).ToDictionary(t => t.TradeId);
        legs = trades.Values.SelectMany(t => t.Legs).ToDictionary(l => l.LegId);
        amounts = trades.Values.SelectMany(t => t.Amounts).ToDictionary(a => a.AmountId);
        // Each ABC entity has a single name; XYZ Industries, an unrated corporate, is proxied.
        Cds = Registration.Key switch
        {
            "ABC/LE-01" => new CdsChoice(CdsCatalog.SingleName, "ABCH.SNRFOR.USD.XR14", null, null, null),
            "ABC/LE-02" => new CdsChoice(CdsCatalog.SingleName, "ABCS.SNRFOR.USD.XR14", null, null, null),
            _ => new CdsChoice(CdsCatalog.Proxy, null, "industrials", "asia", "BBB"),
        };
        // One registration has an explicit empty CVA recovery: analytics then uses the CDS recovery (docs/03).
        CvaRecovery = Registration.LegalEntity == "LE-01" ? null : 0.35m;
        Version = $"{registrationKey}@{valueDate:yyyy-MM-dd}#{existingCount}";
    }

    /// <summary>Acquires the version a descriptor names again. The synthetic source is deterministic, so it is the same.</summary>
    public static PortfolioSourceVersion Resolve(JsonObject descriptor) => new(
        descriptor["registration"]!.GetValue<string>(),
        DateOnly.ParseExact(descriptor["valueDate"]!.GetValue<string>(), "yyyy-MM-dd", CultureInfo.InvariantCulture),
        descriptor["existingCount"]!.GetValue<int>());

    public SourceTrade? Trade(string tradeId) => trades.GetValueOrDefault(tradeId);

    /// <summary>A PV leg by its id, <c>T/1</c>; null where the source holds none.</summary>
    public SourceLeg? Leg(string legId) => legs.GetValueOrDefault(legId);

    /// <summary>An independent amount by its id; null where the source holds none.</summary>
    public SourceAmount? Amount(string amountId) => amounts.GetValueOrDefault(amountId);

    public Csa? CsaOf(string csaId) => Agreement.Csas.SingleOrDefault(c => c.CsaId == csaId);

    /// <summary>
    /// The synthetic classification (ADR-0012 §2's note of 2026-11-08): a trade counts under a regime its direction's IM
    /// CSA serves when it started on or after the counterparty came into that regime's scope, and a physically settled
    /// FX forward is exempt everywhere. A new trade starts on the value date. Null for a trade the source does not hold.
    /// </summary>
    public ImClassification? ImClassificationOf(string tradeId)
    {
        if (Trade(tradeId) is not { } trade) return null;
        if (trade.Product == "FXForward")
            return new("A physically settled FX forward is exempt from IM in every regime.", [], []);
        var start = trade.Start ?? throw new InvalidOperationException($"Trade {tradeId} has no start, which its IM classification reads.");
        IReadOnlyList<RegimeStanding> Under(string direction) => [.. (Agreement.ImCsaOf(direction)?.Regimes ?? []).Select(r => start >= r.Since
            ? new RegimeStanding(r.Regime, true, $"Started {Day(start)}, on or after {Registration.Key} came into {r.Regime}'s scope on {Day(r.Since)}.")
            : new RegimeStanding(r.Regime, false, $"Started {Day(start)}, before {Registration.Key} came into {r.Regime}'s scope on {Day(r.Since)}."))];
        return new(null, Under(ImCsaTerms.Collect), Under(ImCsaTerms.Post));
    }

    static string Day(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public SourceValue? Read(string entity, string field)
    {
        var (kind, id) = Entity.Split(entity);
        switch (kind)
        {
            case Entity.Trade when trades.GetValueOrDefault(id) is { } t:
                return field switch
                {
                    "notional" => new(t.Notional),
                    "maturity" => new(t.Maturity.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                    "fixedRate" => new(t.FixedRate),
                    "schedule" => new(t.Schedule),
                    "lastFixing" => new(t.LastFixing),
                    "officialPv" => new(t.OfficialPv),
                    // A new trade has no CSA of its own: it takes the main CSA automatically.
                    "csa" => new(t.Csa),
                    // Nor IM CSAs: it takes the counterparty's (ADR-0012 §2).
                    "imR" => new(t.ImR),
                    "imP" => new(t.ImP),
                    // A trade's curve follows its CSA unless the user sets one.
                    "curve" => new((JsonNode?)null),
                    "before" => new(!t.IsNew),
                    "after" => new(true),
                    // A module's own input on a trade, such as XVA's Margin (ADR-0010 §10): the source holds none.
                    _ when field.Contains('.') => new((JsonNode?)null),
                    _ => null,
                };
            // A PV leg and an independent amount (ADR-0016 note 49). Each is in the trade until an edit takes it out.
            case Entity.Leg when legs.GetValueOrDefault(id) is { } leg:
                return field switch
                {
                    "currency" => new(leg.Currency),
                    "pv" => new(leg.Pv),
                    "notional" => new(leg.Notional),
                    Entity.TakenOut => new(false),
                    _ => null,
                };
            case Entity.Amount when amounts.ContainsKey(id):
                return field == Entity.TakenOut ? new(false) : null;
            case Entity.Csa when CsaOf(id) is { } csa:
                return field switch
                {
                    "currency" => new(csa.Currency),
                    "curve" => new(csa.Curve),
                    "fvaCurve" => new(csa.FvaCurve),
                    "frequency" => new(csa.Frequency),
                    "exchange" => new(csa.Exchange),
                    "oneWay" => new(csa.OneWay),
                    "counterpartyThreshold" => new(csa.CounterpartyThreshold),
                    "counterpartyMta" => new(csa.CounterpartyMta),
                    "counterpartyCurrency" => new(csa.CounterpartyCurrency),
                    "selfThreshold" => new(csa.SelfThreshold),
                    "selfMta" => new(csa.SelfMta),
                    "selfCurrency" => new(csa.SelfCurrency),
                    _ => null,
                };
            // B or A set for every row of a grid (ADR-0010 §6): the source sets no grid at once.
            case Entity.Grid when id is Entity.NewGrid or Entity.ExistingGrid:
                return field is "before" or "after" ? new((JsonNode?)null) : null;
            // The CVA recovery is the netting set's (ADR-0010 §8); empty stays empty and the CDS recovery applies.
            case Entity.Netting when id == Agreement.NettingSetId:
                return field == "cvaRecovery" ? new(CvaRecovery) : null;
            // The measures Price runs (ADR-0010 §10). The source has no choice of its own: empty means every measure registered.
            case Entity.Selection when id == Entity.MeasuresId:
                return field == Entity.MeasuresField ? new((JsonNode?)null) : null;
            // The workspace's own settings (ADR-0010 §1): the display currency starts as the profile's.
            case Entity.Settings when id == Entity.WorkspaceId:
                return field == Entity.DisplayCurrency ? new(PortfolioCatalog.Profile.DisplayCurrency) : null;
            // A module's settings (ADR-0010 §8): the source holds none, so each starts at the module's default.
            case Entity.Settings when id.Length > 0:
                return new((JsonNode?)null);
            case Entity.Credit when id == Registration.Key:
                return field switch
                {
                    "cvaRecovery" => new(CvaRecovery),
                    "cdsType" => new(Cds.Type),
                    "cdsReference" => new(Cds.Reference),
                    "proxySector" => new(Cds.Sector),
                    "proxyRegion" => new(Cds.Region),
                    "proxyRating" => new(Cds.Rating),
                    _ => null,
                };
            // A curve of the CDS catalogue, one field a tenor (ADR-0010 §8's note of 2026-11-09).
            case Entity.Cds when CdsCatalog.Find(id) is { } curve:
                return curve.Spreads.TryGetValue(field, out var spread) ? new(spread) : null;
            default:
                return null;
        }
    }

    static SourceTrade? Generate(Registration registration, string tradeId, bool isNew, DateOnly valueDate)
    {
        var seed = Seed(tradeId);
        var random = new Random(seed);
        var start = new DateOnly(2024, 1, 2).AddDays(random.Next(0, 900));
        var maturity = start.AddMonths(6 + random.Next(0, 9) * 6);
        if (isNew) maturity = valueDate.AddYears(5);
        else if (maturity <= valueDate) return null;
        var swap = random.NextDouble() < 0.7;
        var currency = random.NextDouble() < 0.6 ? "JPY" : "USD";
        var notional = Math.Round((decimal)(1 + random.Next(1, 100)) * (currency == "JPY" ? 100_000_000m : 1_000_000m), 0);
        var agreement = PortfolioCatalog.AgreementOf(registration.Key);
        string? csa = isNew ? null : agreement.Collateralised ? agreement.Csas[random.Next(agreement.Csas.Count)].CsaId : agreement.NoCsa;
        // Every existing trade is held under the counterparty's IM CSAs, where it has them; which regimes it counts under
        // is the classification's, not the holding's (ADR-0012 §2).
        var (imR, imP) = isNew ? (null, null) : (agreement.ImCsaOf(ImCsaTerms.Collect)?.ImCsaId, agreement.ImCsaOf(ImCsaTerms.Post)?.ImCsaId);
        // Drawn from a generator of its own, so the trades' other values stay as they were before it existed.
        decimal? officialPv = isNew ? null : Math.Round((decimal)(new Random(seed ^ 0x5bd1e995).NextDouble() - 0.5) * notional * 0.02m, 0);
        // A new trade starts on the value date. Its direction is drawn from a generator of its own, as the PV is.
        if (isNew) start = valueDate;
        var buysOrPays = new Random(seed ^ 0x2545f491).NextDouble() < 0.5;
        if (!swap)
        {
            // Against JPY, or JPY against USD, at the synthetic FX of the day it was traded.
            var counter = currency == "JPY" ? "USD" : "JPY";
            var counterNotional = Math.Round(notional * PortfolioMarket.UnitsPerGbp(counter, start) / PortfolioMarket.UnitsPerGbp(currency, start), 0);
            return new(tradeId, isNew, "FXForward", currency, notional, maturity, null, null, null, csa, officialPv,
                start, buysOrPays ? "buy" : "sell", counter, counterNotional, imR, imP)
            {
                Legs = officialPv is { } held ? ForwardLegs(tradeId, seed, currency, notional, counter, counterNotional, held, valueDate) : [],
                Amounts = isNew ? [] : AmountsOf(tradeId, seed, currency, notional, start, maturity),
            };
        }
        var schedule = random.NextDouble() < 0.5 ? "6M" : "3M";
        var fixedRate = Math.Round((decimal)(0.001 + random.NextDouble() * 0.04), 5);
        // The last fixing is set on the latest fixing date not after the value date.
        var period = schedule == "6M" ? 182 : 91;
        var since = valueDate.DayNumber - start.DayNumber;
        var fixings = since < 0 ? 0 : since / period + 1;
        decimal? lastFixing = isNew ? null : Math.Round((decimal)(0.001 + new Random(seed ^ fixings).NextDouble() * 0.04), 5);
        return new(tradeId, isNew, "IRS", currency, notional, maturity, fixedRate, schedule, lastFixing, csa, officialPv,
            start, buysOrPays ? "payFixed" : "receiveFixed", ImR: imR, ImP: imP)
        {
            Legs = officialPv is { } pv ? SwapLegs(tradeId, seed, currency, notional, pv) : [],
            Amounts = isNew ? [] : AmountsOf(tradeId, seed, currency, notional, start, maturity),
        };
    }

    // The PV legs (ADR-0016 note 49), each drawn from a generator of its own so the trades' other values stay as they
    // were before the legs existed. A swap's two legs are in its currency and add up to its official PV.
    static IReadOnlyList<SourceLeg> SwapLegs(string tradeId, int seed, string currency, decimal notional, decimal officialPv)
    {
        var first = Math.Round((decimal)(new Random(seed ^ 0x1b873593).NextDouble() - 0.5) * notional * 0.2m, 0);
        return [new($"{tradeId}/1", tradeId, currency, first, notional), new($"{tradeId}/2", tradeId, currency, officialPv - first, notional)];
    }

    // An FX forward has a leg in each currency. The second is a whole amount of its own currency; the first is what is
    // left of the official PV at the value date's rate, to the cent, so the two add up to it to the unit.
    static IReadOnlyList<SourceLeg> ForwardLegs(string tradeId, int seed, string currency, decimal notional, string counter, decimal counterNotional, decimal officialPv,
        DateOnly valueDate)
    {
        var second = Math.Round((decimal)(new Random(seed ^ 0x1b873593).NextDouble() - 0.5) * counterNotional * 0.2m, 0);
        var first = Math.Round(officialPv - second * PortfolioMarket.UnitsPerGbp(currency, valueDate) / PortfolioMarket.UnitsPerGbp(counter, valueDate), 2);
        return [new($"{tradeId}/1", tradeId, currency, first, notional), new($"{tradeId}/2", tradeId, counter, second, counterNotional)];
    }

    // About one existing trade in three holds an independent amount, a few of them two, in the trade's currency over
    // its life. Illustrative, as every figure here.
    static IReadOnlyList<SourceAmount> AmountsOf(string tradeId, int seed, string currency, decimal notional, DateOnly start, DateOnly maturity)
    {
        var random = new Random(seed ^ 0x3c6ef372);
        var count = random.NextDouble() < 0.35 ? random.NextDouble() < 0.3 ? 2 : 1 : 0;
        return [.. Enumerable.Range(1, count).Select(n =>
            new SourceAmount($"{tradeId}/IA{n}", tradeId, currency, start, maturity, Math.Round(notional * (decimal)(0.005 + random.NextDouble() * 0.02), 0)))];
    }

    static int Seed(string text)
    {
        var hash = 17;
        foreach (var c in text) hash = unchecked(hash * 31 + c);
        return hash;
    }
}

/// <summary>Entity names in portfolio edit targets: <c>trade:ID</c>, <c>csa:ID</c>, <c>netting:ID</c>,
/// <c>credit:CODE/LE</c> (a CVA recovery set before it moved to the netting set) and <c>selection:measures</c>.</summary>
public static class Entity
{
    public const string Trade = "trade";
    public const string Csa = "csa";
    /// <summary>A PV leg of a trade, <c>leg:&lt;trade id&gt;/&lt;n&gt;</c> (ADR-0016 note 49): the source's, or one the workspace made.</summary>
    public const string Leg = "leg";
    /// <summary>An independent amount held on a trade, <c>ia:&lt;trade id&gt;/IA&lt;n&gt;</c> (ADR-0016 note 49).</summary>
    public const string Amount = "ia";
    /// <summary>The field whose edit takes a leg or an independent amount the source holds out of its trade, so that a
    /// reset or an undo brings it back.</summary>
    public const string TakenOut = "out";
    public const string Netting = "netting";
    /// <summary>A trade grid as a whole, <c>grid:new</c> or <c>grid:existing</c>: B or A set for every row by one edit.</summary>
    public const string Grid = "grid";
    public const string NewGrid = "new";
    public const string ExistingGrid = "existing";
    public const string Credit = "credit";
    /// <summary>A curve of the CDS catalogue, <c>cds:&lt;curve id&gt;</c>, its fields its tenors' spreads.</summary>
    public const string Cds = "cds";
    public const string Selection = "selection";
    public const string Settings = "settings";
    /// <summary>A case of the workspace (ADR-0010 §3), <c>case:&lt;id&gt;</c>: created by <c>vary</c> or <c>add each</c>, and the
    /// layer its changes are edits in.</summary>
    public const string Case = "case";

    /// <summary>The selection of measures Price runs: <c>selection:measures</c>, field <c>measures</c> (ADR-0010 §10).</summary>
    public const string MeasuresId = "measures";
    public const string MeasuresField = "measures";
    public static readonly string Measures = Of(Selection, MeasuresId);

    /// <summary>The workspace's settings, <c>settings:workspace</c>: its display currency (ADR-0010 §1).</summary>
    public const string WorkspaceId = "workspace";
    public const string DisplayCurrency = "displayCurrency";
    public static readonly string WorkspaceSettings = Of(Settings, WorkspaceId);

    public static string Of(string kind, string id) => $"{kind}:{id}";

    /// <summary>The trade a leg or an independent amount is under: its id up to the last slash.</summary>
    public static string TradeOf(string id)
    {
        var at = id.LastIndexOf('/');
        return at > 0 ? id[..at] : throw new ArgumentException($"'{id}' names no row under a trade.");
    }

    public static (string Kind, string Id) Split(string entity)
    {
        var at = entity.IndexOf(':');
        if (at <= 0) throw new ArgumentException($"Entity '{entity}' has no kind.");
        return (entity[..at], entity[(at + 1)..]);
    }
}

/// <summary>
/// The workspace's pinned synthetic market data for the portfolio's own rules: FX to GBP as of the value date. The
/// rates are made up, and move a little with the date so that another value date is another fixing.
/// </summary>
public static class PortfolioMarket
{
    public static readonly IReadOnlyList<string> Currencies = ["GBP", "JPY", "USD", "EUR"];

    // Units of each currency per GBP, before the date's move.
    static readonly IReadOnlyDictionary<string, decimal> PerGbp = new Dictionary<string, decimal>
    {
        ["GBP"] = 1m, ["JPY"] = 190m, ["USD"] = 1.27m, ["EUR"] = 1.17m,
    };

    /// <summary>Units of the currency one GBP buys on the value date.</summary>
    public static decimal UnitsPerGbp(string currency, DateOnly valueDate)
    {
        if (!PerGbp.TryGetValue(currency, out var rate)) throw new ArgumentException($"No synthetic FX for {currency}.");
        return currency == "GBP" ? 1m : Math.Round(rate * (1m + (valueDate.DayNumber % 21 - 10) * 0.001m), 6);
    }

    public static decimal ToGbp(decimal amount, string currency, DateOnly valueDate) => amount / UnitsPerGbp(currency, valueDate);
}
