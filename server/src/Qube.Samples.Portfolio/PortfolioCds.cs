using System.Globalization;
using System.Text.Json.Nodes;
using Qube.Composition.Application.Workspaces;

namespace Qube.Samples.Portfolio;

// The counterparty's CDS (ADR-0010 §8 and its note of 2026-11-09): a synthetic catalogue of single names and proxies,
// the portfolio's as its market data is, and the choice a workspace holds. Every ticker, name, recovery and spread here
// is made up.

/// <summary>
/// A CDS curve of the catalogue: a single name, keyed by its ticker, currency, seniority and doc clause, or a proxy, one
/// per sector, region and rating; its recovery; and its spread at each CDS tenor, as a rate (52bp is 0.0052).
/// </summary>
public sealed record CdsCurve(string Id, string Type, string Ticker, string Currency, string Seniority, string DocClause, string Name, decimal Recovery,
    IReadOnlyDictionary<string, decimal> Spreads, string? Sector = null, string? Region = null, string? Rating = null)
{
    /// <summary>The reference as the search writes it: <c>ticker | ccy | seniority | doc clause | name</c>.</summary>
    public string Reference => $"{Ticker} | {Currency} | {Seniority} | {DocClause} | {Name}";

    /// <summary>The words <c>cds</c> recognises it by: its ticker, currency, seniority and doc clause, case ignored.</summary>
    public IReadOnlyList<string> Words => [Ticker, Currency, Seniority, DocClause];
}

/// <summary>
/// The counterparty's CDS as the workspace holds it: a single name by its reference, a proxy by its sector, region and
/// rating, or none (a null type). A key of the type not chosen is kept, so choosing that type again finds it.
/// </summary>
public sealed record CdsChoice(string? Type, string? Reference, string? Sector, string? Region, string? Rating);

/// <summary>
/// The synthetic CDS catalogue (ADR-0010 §8's note of 2026-11-09): the single names the reference search finds and a
/// proxy for every sector, region and rating. A proxy's spreads are a rating's level, scaled by its sector and region,
/// on one term structure.
/// </summary>
public static class CdsCatalog
{
    public const string SingleName = "singleName";
    public const string Proxy = "proxy";

    /// <summary>The CDS tenors, shortest first: a curve has a spread at each.</summary>
    public static readonly IReadOnlyList<string> Tenors = ["6m", "1y", "2y", "3y", "5y", "7y", "10y"];

    public static readonly IReadOnlyList<string> Sectors = ["financials", "industrials", "utilities", "consumer"];
    public static readonly IReadOnlyList<string> Regions = ["asia", "europe", "americas"];
    public static readonly IReadOnlyList<string> Ratings = ["AAA", "AA", "A", "BBB", "BB", "B"];

    /// <summary>The fields of a CDS choice on <c>credit:&lt;registration&gt;</c>, as an edit names them.</summary>
    public static readonly IReadOnlyList<string> ChoiceFields = ["cdsType", "cdsReference", "proxySector", "proxyRegion", "proxyRating"];

    // The 5y spread of each rating in bp, and each tenor's share of it.
    static readonly IReadOnlyDictionary<string, decimal> RatingLevel = new Dictionary<string, decimal>
    {
        ["AAA"] = 12m, ["AA"] = 25m, ["A"] = 48m, ["BBB"] = 95m, ["BB"] = 230m, ["B"] = 420m,
    };
    static readonly IReadOnlyList<decimal> Shape = [0.45m, 0.55m, 0.70m, 0.82m, 1m, 1.08m, 1.15m];
    static readonly IReadOnlyDictionary<string, decimal> SectorFactor = new Dictionary<string, decimal>
    {
        ["financials"] = 1.10m, ["industrials"] = 1m, ["utilities"] = 0.80m, ["consumer"] = 1.05m,
    };
    static readonly IReadOnlyDictionary<string, decimal> RegionFactor = new Dictionary<string, decimal>
    {
        ["asia"] = 0.95m, ["europe"] = 1m, ["americas"] = 1.05m,
    };

    /// <summary>The single names, in the order the search lists them.</summary>
    public static readonly IReadOnlyList<CdsCurve> SingleNames =
    [
        Name("ABCH", "USD", "SNRFOR", "XR14", "ABC Holdings (synthetic)", 0.40m, [18, 22, 30, 38, 52, 61, 70]),
        Name("ABCH", "USD", "SUBLT2", "XR14", "ABC Holdings (synthetic)", 0.20m, [32, 40, 54, 68, 94, 110, 126]),
        Name("ABCH", "JPY", "SNRFOR", "CR14", "ABC Holdings (synthetic)", 0.35m, [15, 19, 26, 33, 45, 53, 62]),
        Name("ABCS", "USD", "SNRFOR", "XR14", "ABC Securities (synthetic)", 0.40m, [25, 30, 41, 52, 71, 83, 95]),
        Name("DEFG", "EUR", "SNRFOR", "MM14", "DEFG Trading (synthetic)", 0.40m, [40, 48, 66, 84, 115, 128, 140]),
        Name("HIJK", "USD", "SNRFOR", "XR14", "HIJK Utilities (synthetic)", 0.40m, [14, 17, 23, 29, 40, 46, 52]),
        Name("LMNO", "JPY", "SNRFOR", "CR14", "LMNO Motors (synthetic)", 0.35m, [20, 25, 34, 43, 59, 68, 78]),
    ];

    static CdsCurve Name(string ticker, string currency, string seniority, string doc, string name, decimal recovery, int[] bp) =>
        new($"{ticker}.{seniority}.{currency}.{doc}", SingleName, ticker, currency, seniority, doc, name, recovery,
            Tenors.Select((t, i) => (t, bp[i])).ToDictionary(x => x.t, x => x.Item2 / 10_000m));

    /// <summary>A curve by its id, a single name's or a proxy's, or null where the catalogue has none.</summary>
    public static CdsCurve? Find(string id)
    {
        if (SingleNames.SingleOrDefault(c => c.Id == id) is { } name) return name;
        var parts = id.Split('.');
        return parts is ["PROXY", var sector, var region, var rating] ? ProxyOf(sector.ToLowerInvariant(), region.ToLowerInvariant(), rating) : null;
    }

    /// <summary>The proxy of a sector, region and rating, or null where one of them is not the catalogue's.</summary>
    public static CdsCurve? ProxyOf(string sector, string region, string rating)
    {
        if (!SectorFactor.TryGetValue(sector, out var s) || !RegionFactor.TryGetValue(region, out var r) || !RatingLevel.TryGetValue(rating, out var level)) return null;
        var id = $"PROXY.{sector.ToUpperInvariant()}.{region.ToUpperInvariant()}.{rating}";
        return new(id, Proxy, id, "USD", "SNRFOR", "XR14", $"Proxy {Title(sector)} · {Title(region)} · {rating} (synthetic)", 0.40m,
            Tenors.Select((t, i) => (t, Math.Round(level * Shape[i] * s * r, 1))).ToDictionary(x => x.t, x => x.Item2 / 10_000m),
            sector, region, rating);
    }

    static string Title(string word) => char.ToUpperInvariant(word[0]) + word[1..];

    /// <summary>The curve a choice names, or why it names none, in the words a refusal of CVA says (ADR-0010 §8's note).</summary>
    public static (CdsCurve? Curve, string? Missing) Resolve(CdsChoice choice, string registration)
    {
        switch (choice.Type)
        {
            case SingleName:
                return choice.Reference is { } reference && Find(reference) is { Type: SingleName } curve ? (curve, null)
                    : (null, $"No CDS for {registration}: the single name {choice.Reference ?? "(none)"} is not in the catalogue. Choose one on the Credit tab, or with cds.");
            case Proxy:
                var missing = new[] { ("sector", choice.Sector), ("region", choice.Region), ("rating", choice.Rating) }.Where(k => k.Item2 is null).Select(k => k.Item1).ToList();
                if (missing.Count > 0)
                    return (null, $"No CDS for {registration}: its proxy has no {string.Join(", ", missing)}. Choose {(missing.Count == 1 ? "it" : "them")} on the Credit tab, or with cds proxy.");
                return ProxyOf(choice.Sector!, choice.Region!, choice.Rating!) is { } proxy ? (proxy, null)
                    : (null, $"No CDS for {registration}: the catalogue has no proxy {choice.Sector} {choice.Region} {choice.Rating}.");
            default:
                return (null, $"No CDS for {registration}: choose a single name or a proxy on the Credit tab, or with cds.");
        }
    }

    /// <summary>
    /// What <c>cds</c> reads (ADR-0010 §8's note): <c>none</c>; <c>proxy</c> and a sector, a region and a rating in any
    /// order; or the words of one single name in any order. Words that name no curve, or several, are refused with what
    /// they could be, never guessed.
    /// </summary>
    public static CdsChoice Read(string text, CdsChoice current)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) throw new WorkspaceRefused("InvalidCds", "cds takes a single name's words, proxy with a sector, a region and a rating, or none.");
        if (words is [var only] && only.Equals("none", StringComparison.OrdinalIgnoreCase)) return current with { Type = null };
        if (words[0].Equals(Proxy, StringComparison.OrdinalIgnoreCase))
        {
            string? sector = null, region = null, rating = null;
            foreach (var word in words.Skip(1))
            {
                void Take(ref string? slot, string value, string what)
                {
                    if (slot is not null) throw new WorkspaceRefused("InvalidCds", $"cds proxy names two {what}s, {slot} and {value}.", word: word);
                    slot = value;
                }
                if (Sectors.FirstOrDefault(s => s.Equals(word, StringComparison.OrdinalIgnoreCase)) is { } s) Take(ref sector, s, "sector");
                else if (Regions.FirstOrDefault(r => r.Equals(word, StringComparison.OrdinalIgnoreCase)) is { } r) Take(ref region, r, "region");
                else if (Ratings.FirstOrDefault(g => g.Equals(word, StringComparison.OrdinalIgnoreCase)) is { } g) Take(ref rating, g, "rating");
                else throw new WorkspaceRefused("InvalidCds",
                    $"{word} is no sector, region or rating: the sectors are {string.Join(", ", Sectors)}; the regions {string.Join(", ", Regions)}; the ratings {string.Join(", ", Ratings)}.",
                    // What it could have been is of a kind the line has not given yet (ADR-0016 note 45).
                    word: word, candidates: [.. sector is null ? Sectors : [], .. region is null ? Regions : [], .. rating is null ? Ratings : []]);
            }
            var lacking = new[] { ("sector", sector), ("region", region), ("rating", rating) }.Where(k => k.Item2 is null).Select(k => k.Item1).ToList();
            if (lacking.Count > 0) throw new WorkspaceRefused("InvalidCds", $"cds proxy takes a sector, a region and a rating; it has no {string.Join(", ", lacking)}.");
            return new CdsChoice(Proxy, current.Reference, sector, region, rating);
        }
        // A single name's words are named as typed, so that the line can mark them where they stand (ADR-0016 note 45).
        var matching = SingleNames.Where(c => words.All(w => c.Words.Any(x => x.Equals(w, StringComparison.OrdinalIgnoreCase)) || c.Id.Equals(w, StringComparison.OrdinalIgnoreCase))).ToList();
        return matching.Count switch
        {
            1 => current with { Type = SingleName, Reference = matching[0].Id },
            0 => throw new WorkspaceRefused("UnknownCds",
                $"No single name is {string.Join(" ", words)}: the catalogue holds {string.Join("; ", SingleNames.Select(c => string.Join(" ", c.Words)))}, or write cds proxy <sector> <region> <rating>.",
                word: text.Trim(), candidates: [.. SingleNames.Select(c => string.Join(" ", c.Words))]),
            _ => throw new WorkspaceRefused("AmbiguousCds",
                $"{string.Join(" ", words)} names {matching.Count} single names, {string.Join("; ", matching.Select(c => string.Join(" ", c.Words)))}: add a word to name one.",
                word: text.Trim(), candidates: [.. matching.Select(c => string.Join(" ", c.Words))]),
        };
    }

    /// <summary>A spread as the grid and the Changes tab write it, in bp.</summary>
    public static string Bp(decimal spread) => $"{(spread * 10_000m).ToString("0.##########", CultureInfo.InvariantCulture)}bp";
}

/// <summary>
/// The counterparty's CDS curve as the workspace shows it and Price would submit it (ADR-0010 §8): the catalogue's curve
/// with the user's spreads, each tenor's Original where it differs.
/// </summary>
public sealed record EffectiveCds(string CurveId, string Type, string Ticker, string Currency, string Seniority, string DocClause, string Name, string Reference,
    decimal Recovery, IReadOnlyDictionary<string, decimal> Spreads, IReadOnlyList<string> Overridden, string? Sector = null, string? Region = null, string? Rating = null,
    IReadOnlyDictionary<string, JsonNode?>? Original = null);
