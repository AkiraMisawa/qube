using System.Globalization;
using Workbench.Composition.Contracts;

namespace Workbench.Samples.Ccr;

/// <summary>What the counterparty is registered as. The exposure class is decided from it (CRE20).</summary>
public enum InstitutionType
{
    Sovereign,
    /// <summary>The BIS, the IMF, the ECB, the EU, the ESM or the EFSF (CRE20.10).</summary>
    InternationalOrganisation,
    PublicSectorEntity,
    MultilateralDevelopmentBank,
    Bank,
    SecuritiesFirm,
    OtherFinancialInstitution,
    Corporate,
    Individual,
}

public enum ScraGrade { A, B, C }

/// <summary>The bank's own SCRA assessment of the counterparty (CRE20.22-30), and the ratios footnote 15 reads.</summary>
public sealed record ScraAssessment(ScraGrade Grade, double? Cet1Ratio = null, double? Tier1LeverageRatio = null);

/// <summary>A long-term issuer rating from an eligible agency, on the S&amp;P/Fitch scale or Moody's.</summary>
public sealed record ExternalRating(string Agency, string Grade);

/// <summary>The sovereign of the country of incorporation, as the ratings feed has it.</summary>
public sealed record SovereignFacts(IReadOnlyList<ExternalRating>? Ratings = null, int? EcaScore = null);

/// <summary>
/// What the risk weight reads about the counterparty (ADR-0013 §5, §6). No ratings, null or empty, means unrated. An
/// attribute left null is not known, and a step that needs it refuses the measure rather than assume it. The two
/// designations at the end are lists the supervisor keeps: a counterparty not on them is not designated.
/// </summary>
public sealed record CounterpartyRiskFacts(
    string CounterpartyId,
    string LegalEntity,
    InstitutionType Type,
    IReadOnlyList<ExternalRating>? Ratings = null,
    string? CountryOfIncorporation = null,
    SovereignFacts? SovereignOfIncorporation = null,
    bool? SupervisedAsBank = null,
    ScraAssessment? Scra = null,
    int? DueDiligenceBucketsUp = null,
    bool? Sme = null,
    bool? InvestmentGrade = null,
    bool? RegulatoryRetail = null,
    int? EcaScore = null,
    bool EligibleZeroWeightMdb = false,
    bool PseTreatedAsSovereign = false);

/// <summary>A trade's original maturity, from its start to its end.</summary>
public sealed record OriginalMaturity(string TradeId, DateOnly Start, DateOnly End)
{
    /// <summary>Three months or less, by the calendar (CRE20.19).</summary>
    public bool IsShortTerm => End <= Start.AddMonths(3);

    internal string Span => $"{Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} to {End.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";
}

/// <summary>
/// What the risk weight reads about the exposure: the trade with the longest original maturity (short-term treatment
/// applies only when every trade is short-term), and whether the exposure is in the local currency of the
/// counterparty's country of incorporation.
/// </summary>
public sealed record ExposureFacts(OriginalMaturity Longest, bool InLocalCurrency);

/// <summary>CRE20.11's two options for public sector entities.</summary>
public enum PseOption { SovereignRating, OwnRating }

/// <summary>CRE20.8: a lower weight for the jurisdiction's own sovereign in its own currency.</summary>
public sealed record DomesticSovereignTreatment(string Country, double Weight);

/// <summary>The jurisdiction's choices, as configuration with a version. A national rule's differences go here.</summary>
public sealed record RiskWeightSettings(
    bool ExternalRatingsAllowed,
    string Version,
    PseOption PseOption = PseOption.SovereignRating,
    bool EcaScoresRecognised = false,
    DomesticSovereignTreatment? DomesticSovereign = null);

public sealed record DecisionInput(string Name, string Value, ParameterSource Source, string Where);

/// <summary>One step of the decision, written as a term of the trace: its outcome, why, what it read and its rule.</summary>
public sealed record DecisionStep(string Key, string Title, string Outcome, string Because, string Citation, bool Applies, IReadOnlyList<DecisionInput> Inputs);

/// <summary>A table of weights, by row and column (<c>Weights[row][column]</c>).</summary>
public sealed record RiskWeightTable(
    string Name,
    string Citation,
    ParameterSource Source,
    IReadOnlyList<string> Rows,
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<double>> Weights);

/// <summary>The cell of the table the weight was taken from.</summary>
public sealed record TableLookup(RiskWeightTable Table, string Row, string Column, double Weight);

/// <summary>
/// The decision: the table's weight, the floor where one applies, and the risk weight, the greater of the two. The
/// steps are always the same eight, in order; a step that does not apply says why.
/// </summary>
public sealed record RiskWeightDecision(double RiskWeight, TableLookup Lookup, double? Floor, IReadOnlyList<DecisionStep> Steps);

/// <summary>The resolver cannot weigh this counterparty, and says why (ADR-0013 §6: the measure is refused by name).</summary>
public sealed class RiskWeightRefused(string message) : Exception(message);

/// <summary>
/// The SA risk weight of a counterparty, resolved at run time (ADR-0013 §5), with Basel III's values: CRE20 as in force
/// from 2023-01-01 and CRE21.9-11 for the use of ratings. Every step is recorded, including those that do not apply.
/// </summary>
public static class RiskWeights
{
    public static readonly IReadOnlyList<string> StepKeys = ["exposureClass", "approach", "ratings", "ratingUsed", "shortTerm", "dueDiligence", "floor", "tableWeight"];

    public static RiskWeightDecision Resolve(CounterpartyRiskFacts facts, ExposureFacts exposure, RiskWeightSettings settings) =>
        new Resolver(facts, exposure, settings).Resolve();

    // Notches of the S&P/Fitch scale, best first; Moody's grades map onto the same notches.
    static readonly string[] Scale = ["AAA", "AA+", "AA", "AA-", "A+", "A", "A-", "BBB+", "BBB", "BBB-", "BB+", "BB", "BB-", "B+", "B", "B-", "CCC+", "CCC", "CCC-", "CC", "C", "D"];
    static readonly string[] Moodys = ["Aaa", "Aa1", "Aa2", "Aa3", "A1", "A2", "A3", "Baa1", "Baa2", "Baa3", "Ba1", "Ba2", "Ba3", "B1", "B2", "B3", "Caa1", "Caa2", "Caa3", "Ca", "C", "D"];

    internal static int NotchOf(string grade)
    {
        var g = grade.Trim().Replace('−', '-');
        var i = Array.IndexOf(Scale, g);
        return i >= 0 ? i : Array.IndexOf(Moodys, g);
    }

    const int RatedBands = 5;
    static readonly string[] Bands = ["AAA to AA-", "A+ to A-", "BBB+ to BBB-", "BB+ to B-", "Below B-"];
    static readonly string[] CorporateBands = ["AAA to AA-", "A+ to A-", "BBB+ to BBB-", "BB+ to BB-", "Below BB-"];
    internal static int Band(int notch) => notch <= 3 ? 0 : notch <= 6 ? 1 : notch <= 9 ? 2 : notch <= 15 ? 3 : 4;
    internal static int CorporateBand(int notch) => notch <= 3 ? 0 : notch <= 6 ? 1 : notch <= 9 ? 2 : notch <= 12 ? 3 : 4;

    static readonly string[] Base = ["Base"];
    static readonly string[] BaseAndShortTerm = ["Base", "Short-term"];

    static RiskWeightTable Table(string name, string citation, string[] rows, string[] columns, params double[][] weights) =>
        new(name, citation, ParameterSource.Regulation, rows, columns, weights);

    internal static readonly RiskWeightTable Table1 = Table("sovereigns, Table 1", "CRE20.7", [.. Bands, "Unrated"], Base,
        [0.00], [0.20], [0.50], [1.00], [1.50], [1.00]);
    internal static readonly RiskWeightTable Table2 = Table("sovereigns by ECA score, Table 2", "CRE20.9", ["0 to 1", "2", "3", "4 to 6", "7"], Base,
        [0.00], [0.20], [0.50], [1.00], [1.50]);
    internal static readonly RiskWeightTable InternationalOrganisations = Table("international organisations", "CRE20.10", ["BIS, IMF, ECB, EU, ESM, EFSF"], Base,
        [0.00]);
    internal static readonly RiskWeightTable Table3 = Table("public sector entities, option 1, Table 3", "CRE20.11", [.. Bands, "Unrated"], Base,
        [0.20], [0.50], [1.00], [1.00], [1.50], [1.00]);
    internal static readonly RiskWeightTable Table4 = Table("public sector entities, option 2, Table 4", "CRE20.11", [.. Bands, "Unrated"], Base,
        [0.20], [0.50], [0.50], [1.00], [1.50], [0.50]);
    internal static readonly RiskWeightTable EligibleMdbs = Table("eligible multilateral development banks", "CRE20.14", ["Eligible MDB"], Base,
        [0.00]);
    internal static readonly RiskWeightTable Table5 = Table("multilateral development banks, Table 5", "CRE20.15", [.. Bands, "Unrated"], Base,
        [0.20], [0.30], [0.50], [1.00], [1.50], [0.50]);
    internal static readonly RiskWeightTable Table6 = Table("banks, ECRA, Table 6", "CRE20.18", Bands, BaseAndShortTerm,
        [0.20, 0.20], [0.30, 0.20], [0.50, 0.20], [1.00, 0.50], [1.50, 1.50]);
    internal static readonly RiskWeightTable Table7 = Table("banks, SCRA, Table 7", "CRE20.21",
        ["A", "A, CET1 at least 14% and leverage at least 5%", "B", "C"], BaseAndShortTerm,
        [0.40, 0.20], [0.30, 0.20], [0.75, 0.50], [1.50, 1.50]);
    internal static readonly RiskWeightTable Table10 = Table("corporates, Table 10", "CRE20.42", [.. CorporateBands, "Unrated", "Unrated SME"], Base,
        [0.20], [0.50], [0.75], [1.00], [1.50], [1.00], [0.85]);
    internal static readonly RiskWeightTable CorporatesWithoutRatings = Table("corporates without external ratings", "CRE20.44",
        ["Investment grade", "SME", "Other"], Base, [0.65], [0.85], [1.00]);
    internal static readonly RiskWeightTable Retail = Table("retail", "CRE20, retail exposures", ["Regulatory retail", "Other retail"], Base,
        [0.75], [1.00]);

    internal static string Pct(double w) => (w * 100).ToString("0.##", CultureInfo.InvariantCulture) + "%";

    sealed record Weighed(ExternalRating Rating, int Band, double Weight);

    /// <summary>CRE21.9-11: one rating is used; of two, the higher weight; of three or more, the higher of the two lowest.</summary>
    static (Weighed Used, string Why) Pick(IReadOnlyList<Weighed> weighed)
    {
        // By weight, then by band, so that the choice among equal weights is the same every time.
        var ordered = weighed.OrderBy(w => w.Weight).ThenBy(w => w.Band).ToList();
        return ordered.Count switch
        {
            1 => (ordered[0], "One assessment, used as it is."),
            2 => (ordered[1], $"Two assessments, with weights {Pct(ordered[0].Weight)} and {Pct(ordered[1].Weight)}: the higher applies."),
            _ => (ordered[1], $"{ordered.Count} assessments: the two lowest weights are {Pct(ordered[0].Weight)} and {Pct(ordered[1].Weight)}, and the higher of those applies."),
        };
    }

    sealed class Resolver(CounterpartyRiskFacts facts, ExposureFacts exposure, RiskWeightSettings settings)
    {
        static readonly Dictionary<string, string> Titles = new()
        {
            ["exposureClass"] = "Exposure class",
            ["approach"] = "Approach",
            ["ratings"] = "Eligible ratings",
            ["ratingUsed"] = "Rating used",
            ["shortTerm"] = "Short-term treatment",
            ["dueDiligence"] = "Due diligence",
            ["floor"] = "Sovereign floor",
            ["tableWeight"] = "Table weight",
        };

        readonly Dictionary<string, DecisionStep> steps = [];
        double? floor;

        IReadOnlyList<ExternalRating> Ratings => facts.Ratings ?? [];
        string Counterparty => $"counterparty/{facts.CounterpartyId} · {facts.LegalEntity}";
        string Config => $"risk weight settings · {settings.Version}";

        public RiskWeightDecision Resolve()
        {
            var lookup = facts.Type switch
            {
                InstitutionType.Sovereign => Sovereign(),
                InstitutionType.InternationalOrganisation => InternationalOrganisation(),
                InstitutionType.PublicSectorEntity => PublicSectorEntity(),
                InstitutionType.MultilateralDevelopmentBank => MultilateralDevelopmentBank(),
                InstitutionType.Bank => Bank("The counterparty is registered as a bank.", "CRE20.16"),
                InstitutionType.SecuritiesFirm or InstitutionType.OtherFinancialInstitution => FinancialInstitution(),
                InstitutionType.Corporate => Corporate("The counterparty is registered as a corporate.", "CRE20.41"),
                InstitutionType.Individual => Individual(),
                _ => throw Refuse($"the institution type {facts.Type} has no exposure class."),
            };
            var weight = floor is { } f ? Math.Max(lookup.Weight, f) : lookup.Weight;
            var ordered = StepKeys.Select(k => steps.TryGetValue(k, out var s) ? s : throw new InvalidOperationException($"The step {k} was not written.")).ToList();
            return new RiskWeightDecision(weight, lookup, floor, ordered);
        }

        RiskWeightRefused Refuse(string why) => new($"{facts.CounterpartyId} · {facts.LegalEntity}: {why}");

        void Step(string key, string outcome, string because, string citation, bool applies, params DecisionInput[] inputs) =>
            steps.Add(key, new DecisionStep(key, Titles[key], outcome, because, citation, applies, inputs));

        DecisionInput Input(string name, string value) => new(name, value, ParameterSource.Input, Counterparty);
        DecisionInput Configured(string name, string value) => new(name, value, ParameterSource.Configuration, Config);
        DecisionInput ExternalRatingsSetting => Configured("externalRatings", settings.ExternalRatingsAllowed ? "allowed" : "not allowed");
        DecisionInput TypeInput => Input("institutionType", facts.Type.ToString());
        DecisionInput CurrencyInput => new("localCurrency", exposure.InLocalCurrency ? "yes" : "no", ParameterSource.Input, "trades");
        DecisionInput MaturityInput => new("originalMaturity", exposure.Longest.Span, ParameterSource.Input, $"trades/{exposure.Longest.TradeId}");

        static DecisionInput Feed(string name, int count, string whose) =>
            new(name, count == 1 ? "1 assessment" : $"{count} assessments", ParameterSource.Data, $"ratings feed · {whose}");

        void ExposureClass(string outcome, string because, string citation, params DecisionInput[] more) =>
            Step("exposureClass", outcome, because, citation, true, [TypeInput, .. more]);

        // The steps that apply to some exposure classes only, written where they do not apply.

        void NoShortTerm() =>
            Step("shortTerm", "Not applicable", "Short-term treatment is for exposures to banks.", "CRE20.19", false, MaturityInput);

        void NoDueDiligence(string why, string citation = "CRE20.20") => Step("dueDiligence", "Not applicable", why, citation, false);

        void NoFloor(string why) => Step("floor", "Not applicable", why, "CRE20.32", false);

        void NoRating(string why, string citation)
        {
            var listed = Ratings.Count == 0 ? "None." : string.Join(", ", Ratings.Select(r => $"{r.Agency} {r.Grade}")) + $": not used, {why}";
            Step("ratings", Ratings.Count == 0 ? "None" : $"{Ratings.Count} found, not used", listed, "CRE21", false, Feed("ratings", Ratings.Count, "counterparty"));
            Step("ratingUsed", "Not applicable", $"No rating is used: {why}", citation, false);
        }

        // Ratings: weighed on the table and column that apply, listed, and one used (CRE21.9-11).

        List<Weighed> Weigh(IReadOnlyList<ExternalRating> ratings, Func<int, int> bandOf, RiskWeightTable table, int column) =>
            ratings.Select(r =>
            {
                var notch = NotchOf(r.Grade);
                if (notch < 0) throw Refuse($"the rating {r.Grade} from {r.Agency} is on no scale the resolver knows.");
                var band = bandOf(notch);
                return new Weighed(r, band, table.Weights[band][column]);
            }).ToList();

        Weighed UseRating(IReadOnlyList<Weighed> weighed, string whose)
        {
            Step("ratings", weighed.Count == 1 ? "1 found" : $"{weighed.Count} found",
                string.Join(", ", weighed.Select(w => $"{w.Rating.Agency} {w.Rating.Grade} ({Pct(w.Weight)})")) + ".", "CRE21", true,
                Feed("ratings", weighed.Count, whose));
            var (used, why) = Pick(weighed);
            Step("ratingUsed", $"{used.Rating.Grade} ({used.Rating.Agency})", why, "CRE21.9-11", true);
            return used;
        }

        TableLookup Lookup(RiskWeightTable table, int row, int column, string citation, string because)
        {
            var weight = table.Weights[row][column];
            var where = table.Source == ParameterSource.Regulation ? table.Citation : Config;
            Step("tableWeight", Pct(weight), because, citation, true, new DecisionInput("table", table.Name, table.Source, where));
            return new TableLookup(table, table.Rows[row], table.Columns[column], weight);
        }

        /// <summary>A rated row when ratings are allowed and there are some, else the table's unrated row.</summary>
        TableLookup RatedOrUnrated(IReadOnlyList<ExternalRating> ratings, string whose, RiskWeightTable table, string citation, string what)
        {
            if (settings.ExternalRatingsAllowed && ratings.Count > 0)
            {
                var used = UseRating(Weigh(ratings, Band, table, 0), whose);
                return Lookup(table, used.Band, 0, citation, $"{what}, rating band {table.Rows[used.Band]}.");
            }
            NoRating(settings.ExternalRatingsAllowed ? $"the {whose} has no eligible rating." : "the jurisdiction does not allow external ratings.", citation);
            return Lookup(table, RatedBands, 0, citation, $"{what}, unrated.");
        }

        int DueDiligence(int band, RiskWeightTable table, int column, string citation)
        {
            var rows = table.Rows;
            double WeightOf(int b) => table.Weights[b][column];
            var up = facts.DueDiligenceBucketsUp
                ?? throw Refuse($"the due diligence outcome is missing, and a rating-based weight is given only with it ({citation}).");
            if (up < 0) throw Refuse($"a due diligence outcome of {up} buckets: due diligence can raise a weight and never lower it ({citation}).");
            var input = Input("dueDiligence", up == 0 ? "no more risk than the rating" : up == 1 ? "1 bucket higher" : $"{up} buckets higher");
            if (up == 0)
            {
                Step("dueDiligence", "No uplift", "The bank's own assessment finds no more risk than the rating implies.", citation, false, input);
                return band;
            }
            // A bucket is a weight, not a row (CRE20.20, "a risk weight at least one bucket higher"): in Table 6's
            // short-term column the three best rows share 20%, so a step up from any of them goes to the first row
            // weighted more. The last rated row is as high as the uplift goes.
            var moved = band;
            for (var i = 0; i < up; i++)
            {
                var from = moved;
                var next = Enumerable.Range(from + 1, RatedBands - 1 - from).FirstOrDefault(b => WeightOf(b) > WeightOf(from), -1);
                if (next < 0) break;
                moved = next;
            }
            var outcome = up == 1 ? "1 bucket higher" : $"{up} buckets higher";
            if (moved == band)
            {
                Step("dueDiligence", outcome,
                    $"The bank's own assessment finds more risk than the rating implies, and {rows[band]} at {Pct(WeightOf(band))} is already the highest weight of its column.",
                    citation, true, input);
                return band;
            }
            Step("dueDiligence", outcome,
                $"The bank's own assessment finds more risk than the rating implies: from {rows[band]} at {Pct(WeightOf(band))} to {rows[moved]} at {Pct(WeightOf(moved))}.",
                citation, true, input);
            return moved;
        }

        // Sovereigns (CRE20.7-10).

        TableLookup Sovereign()
        {
            ExposureClass("Sovereign", "The counterparty is registered as a sovereign or its central bank.", "CRE20.7");
            return SovereignWeight(Ratings, facts.EcaScore, "counterparty");
        }

        TableLookup SovereignWeight(IReadOnlyList<ExternalRating> ratings, int? ecaScore, string whose)
        {
            NoShortTerm();
            NoDueDiligence("The one-bucket uplift is written for bank and corporate exposures (CRE20.20, CRE20.42).", "CRE20.4");
            NoFloor("The floor at the sovereign of incorporation is for bank exposures under SCRA.");

            if (settings.DomesticSovereign is { } domestic && exposure.InLocalCurrency)
            {
                var country = facts.CountryOfIncorporation
                    ?? throw Refuse("the country is missing, and the domestic-currency weight of CRE20.8 depends on it.");
                if (country == domestic.Country)
                {
                    Step("approach", "Domestic currency",
                        $"The jurisdiction weighs its own sovereign, {domestic.Country}, at {Pct(domestic.Weight)} in its own currency, and the exposure is in that currency.",
                        "CRE20.8", true, Configured("domesticSovereign", $"{domestic.Country} at {Pct(domestic.Weight)}"), Input("countryOfIncorporation", country), CurrencyInput);
                    NoRating("the domestic-currency weight applies.", "CRE20.8");
                    var table = new RiskWeightTable("sovereign, domestic currency", "CRE20.8", ParameterSource.Configuration, ["Domestic currency"], Base, [[domestic.Weight]]);
                    return Lookup(table, 0, 0, "CRE20.8", $"The configured weight for {domestic.Country} in its own currency.");
                }
            }

            if (settings.EcaScoresRecognised && ecaScore is { } score)
            {
                Step("approach", "ECA score", "The jurisdiction recognises export credit agencies' country risk scores.", "CRE20.9", true,
                    Configured("ecaScores", "recognised"), new DecisionInput("ecaScore", score.ToString(CultureInfo.InvariantCulture), ParameterSource.Data, $"ECA scores · {whose}"));
                NoRating("the ECA score is used.", "CRE20.9");
                return Lookup(Table2, EcaRow(score), 0, "CRE20.9", $"Sovereign, ECA score {score}.");
            }

            var rated = settings.ExternalRatingsAllowed && ratings.Count > 0;
            Step("approach", rated ? "ECRA" : "Unrated",
                rated ? "The jurisdiction allows external ratings for risk weights and the sovereign has an eligible rating."
                    : settings.ExternalRatingsAllowed ? "The sovereign has no eligible rating." : "The jurisdiction does not allow external ratings for risk weights.",
                "CRE20.7", true, ExternalRatingsSetting);
            return RatedOrUnrated(ratings, whose, Table1, "CRE20.7", "Sovereign");
        }

        int EcaRow(int score) => score switch
        {
            0 or 1 => 0,
            2 => 1,
            3 => 2,
            >= 4 and <= 6 => 3,
            7 => 4,
            _ => throw Refuse($"the ECA score {score} is outside 0 to 7 (CRE20.9)."),
        };

        /// <summary>The weight of the sovereign of incorporation, for the SCRA floor.</summary>
        (double Weight, string How) WeightOfSovereign(SovereignFacts sovereign)
        {
            if (settings.EcaScoresRecognised && sovereign.EcaScore is { } score)
                return (Table2.Weights[EcaRow(score)][0], $"ECA score {score}, Table 2");
            var ratings = sovereign.Ratings ?? [];
            if (settings.ExternalRatingsAllowed && ratings.Count > 0)
            {
                var (used, _) = Pick(Weigh(ratings, Band, Table1, 0));
                return (used.Weight, $"rated {used.Rating.Grade} by {used.Rating.Agency}, Table 1");
            }
            return (Table1.Weights[RatedBands][0], "unrated, Table 1");
        }

        TableLookup InternationalOrganisation()
        {
            ExposureClass("Sovereign", "The counterparty is one of the international organisations CRE20.10 names.", "CRE20.10");
            Step("approach", "Named in CRE20.10", "The BIS, the IMF, the ECB, the EU, the ESM and the EFSF take 0%.", "CRE20.10", true);
            NoRating("the organisation's weight is fixed by CRE20.10.", "CRE20.10");
            NoShortTerm();
            NoDueDiligence("The weight is fixed by CRE20.10.", "CRE20.10");
            NoFloor("The floor at the sovereign of incorporation is for bank exposures under SCRA.");
            return Lookup(InternationalOrganisations, 0, 0, "CRE20.10", "An international organisation of CRE20.10.");
        }

        // Public sector entities (CRE20.11-12) and multilateral development banks (CRE20.14-15).

        SovereignFacts SovereignFor(string why) =>
            facts.SovereignOfIncorporation ?? throw Refuse($"the sovereign of incorporation is missing from the ratings feed, and {why}");

        TableLookup PublicSectorEntity()
        {
            if (facts.PseTreatedAsSovereign)
            {
                ExposureClass("Sovereign", "The counterparty is a public sector entity the supervisor treats as its sovereign.", "CRE20.12",
                    Input("treatedAsSovereign", "yes"));
                var sovereign = SovereignFor("a PSE treated as its sovereign takes the sovereign's weight (CRE20.12).");
                return SovereignWeight(sovereign.Ratings ?? [], sovereign.EcaScore, "sovereign of incorporation");
            }

            ExposureClass("Public sector entity", "The counterparty is registered as a public sector entity.", "CRE20.11");
            NoShortTerm();
            NoDueDiligence("The one-bucket uplift is written for bank and corporate exposures (CRE20.20, CRE20.42).", "CRE20.4");
            NoFloor("The floor at the sovereign of incorporation is for bank exposures under SCRA.");
            if (settings.PseOption == PseOption.SovereignRating)
            {
                var sovereign = SovereignFor("under option 1 a PSE is weighted by its sovereign's rating (CRE20.11).");
                Step("approach", "Option 1", "The jurisdiction weighs public sector entities by the rating of their sovereign.", "CRE20.11", true,
                    Configured("pseOption", "1, the sovereign's rating"), ExternalRatingsSetting);
                return RatedOrUnrated(sovereign.Ratings ?? [], "sovereign of incorporation", Table3, "CRE20.11", "Public sector entity, option 1, by its sovereign");
            }
            Step("approach", "Option 2", "The jurisdiction weighs public sector entities by their own rating.", "CRE20.11", true,
                Configured("pseOption", "2, the entity's own rating"), ExternalRatingsSetting);
            return RatedOrUnrated(Ratings, "counterparty", Table4, "CRE20.11", "Public sector entity, option 2");
        }

        TableLookup MultilateralDevelopmentBank()
        {
            NoShortTerm();
            NoDueDiligence("The one-bucket uplift is written for bank and corporate exposures (CRE20.20, CRE20.42).", "CRE20.4");
            NoFloor("The floor at the sovereign of incorporation is for bank exposures under SCRA.");
            if (facts.EligibleZeroWeightMdb)
            {
                ExposureClass("Multilateral development bank", "The counterparty is a multilateral development bank eligible for 0%.", "CRE20.14",
                    Input("eligibleForZero", "yes"));
                Step("approach", "Eligible for 0%", "The MDB meets the Committee's eligibility criteria.", "CRE20.14", true);
                NoRating("an eligible MDB takes 0%.", "CRE20.14");
                return Lookup(EligibleMdbs, 0, 0, "CRE20.14", "An MDB eligible for 0%.");
            }
            ExposureClass("Multilateral development bank", "The counterparty is a multilateral development bank not eligible for 0%.", "CRE20.15",
                Input("eligibleForZero", "no"));
            var rated = settings.ExternalRatingsAllowed && Ratings.Count > 0;
            Step("approach", rated ? "ECRA" : "Unrated",
                rated ? "The jurisdiction allows external ratings and the MDB has an eligible rating."
                    : settings.ExternalRatingsAllowed ? "The MDB has no eligible rating." : "The jurisdiction does not allow external ratings, so the unrated weight applies.",
                "CRE20.15", true, ExternalRatingsSetting);
            return RatedOrUnrated(Ratings, "counterparty", Table5, "CRE20.15", "Multilateral development bank");
        }

        // Banks (CRE20.16-32), and securities firms and other financial institutions (CRE20.40).

        TableLookup FinancialInstitution()
        {
            var supervised = facts.SupervisedAsBank
                ?? throw Refuse("whether the counterparty is supervised as a bank is missing, and its exposure class depends on it (CRE20.40).");
            var what = facts.Type == InstitutionType.SecuritiesFirm ? "a securities firm" : "a financial institution";
            return supervised
                ? Bank($"The counterparty is {what} subject to prudential standards and supervision equivalent to a bank's.", "CRE20.40")
                : Corporate($"The counterparty is {what} not supervised as a bank.", "CRE20.40");
        }

        TableLookup Bank(string because, string citation)
        {
            ExposureClass("Bank", because, citation, facts.SupervisedAsBank is { } s ? [Input("supervisedAsBank", s ? "yes" : "no")] : []);
            var ecra = settings.ExternalRatingsAllowed && Ratings.Count > 0;
            var shortTerm = ShortTerm(ecra ? "CRE20.19" : "CRE20.31");
            var column = shortTerm ? 1 : 0;
            var when = shortTerm ? ", short-term" : "";

            if (ecra)
            {
                Step("approach", "ECRA", "The jurisdiction allows external ratings for risk weights and the counterparty has an eligible rating.",
                    "CRE20.17", true, ExternalRatingsSetting);
                var used = UseRating(Weigh(Ratings, Band, Table6, column), "counterparty");
                var band = DueDiligence(used.Band, Table6, column, "CRE20.20");
                NoFloor("The floor at the sovereign of incorporation applies under SCRA only.");
                return Lookup(Table6, band, column, "CRE20.18", $"Bank exposure under ECRA, rating band {Table6.Rows[band]}{when}.");
            }

            var scra = facts.Scra
                ?? throw Refuse("the SCRA grade is missing, and an unrated bank, or any bank where external ratings are not allowed, is weighted by it (CRE20.17, CRE20.21).");
            var gradeInputs = new List<DecisionInput> { ExternalRatingsSetting, Input("scraGrade", scra.Grade.ToString()) };
            if (scra.Cet1Ratio is { } c) gradeInputs.Add(Input("cet1Ratio", Pct(c)));
            if (scra.Tier1LeverageRatio is { } l) gradeInputs.Add(Input("leverageRatio", Pct(l)));
            Step("approach", $"SCRA, grade {scra.Grade}",
                settings.ExternalRatingsAllowed
                    ? "The counterparty has no eligible external rating, so the standardised credit risk assessment applies."
                    : "The jurisdiction does not allow external ratings for risk weights, so the standardised credit risk assessment applies.",
                "CRE20.17", true, [.. gradeInputs]);
            NoRating(settings.ExternalRatingsAllowed ? "the counterparty has no eligible rating." : "the jurisdiction does not allow external ratings.", "CRE20.17");
            NoDueDiligence("The one-bucket uplift is for rating-based weights; under SCRA the grade is the bank's own assessment (CRE20.22-30).");
            Floor();

            var (row, why) = scra.Grade switch
            {
                ScraGrade.A => GradeA(scra),
                ScraGrade.B => (2, "Grade B."),
                ScraGrade.C => (3, "Grade C."),
                _ => throw Refuse($"the SCRA grade {scra.Grade} is not A, B or C."),
            };
            return Lookup(Table7, row, column, "CRE20.21", $"Bank exposure under SCRA. {why}{(shortTerm ? " Short-term." : "")}");
        }

        /// <summary>CRE20.21 footnote 15: grade A with CET1 of 14% or more and a Tier 1 leverage ratio of 5% or more.</summary>
        static (int Row, string Why) GradeA(ScraAssessment scra)
        {
            if (scra.Cet1Ratio is not { } cet1 || scra.Tier1LeverageRatio is not { } leverage)
                return (0, "Grade A. The CET1 and leverage ratios are not given, so footnote 15's 30% is not claimed.");
            return cet1 >= 0.14 && leverage >= 0.05
                ? (1, $"Grade A with a CET1 ratio of {Pct(cet1)} and a leverage ratio of {Pct(leverage)}: footnote 15's 30%.")
                : (0, $"Grade A. A CET1 ratio of {Pct(cet1)} and a leverage ratio of {Pct(leverage)} do not reach 14% and 5%, so not footnote 15's 30%.");
        }

        bool ShortTerm(string citation)
        {
            var longest = exposure.Longest;
            if (longest.IsShortTerm)
                Step("shortTerm", "Applied",
                    $"Every trade's original maturity is three months or less; the longest, {longest.TradeId}, runs {longest.Span}.", citation, true, MaturityInput);
            else
                Step("shortTerm", "Not applied",
                    $"The longest original maturity, {longest.TradeId} from {longest.Span}, is over three months.", citation, false, MaturityInput);
            return longest.IsShortTerm;
        }

        /// <summary>CRE20.32: under SCRA, an exposure not in the local currency is not weighted below the sovereign of incorporation.</summary>
        void Floor()
        {
            if (exposure.InLocalCurrency)
            {
                Step("floor", "Not applied", "The exposure is in the local currency of the country of incorporation.", "CRE20.32", false, CurrencyInput);
                return;
            }
            var country = facts.CountryOfIncorporation
                ?? throw Refuse("the country of incorporation is missing, and a foreign-currency exposure under SCRA is floored at its sovereign (CRE20.32).");
            var sovereign = SovereignFor($"a foreign-currency exposure under SCRA is floored at the weight of {country} (CRE20.32).");
            var (weight, how) = WeightOfSovereign(sovereign);
            floor = weight;
            Step("floor", Pct(weight),
                $"The exposure is not in the local currency of {country}, so its weight is not below that of {country}'s sovereign: {Pct(weight)}, {how}.",
                "CRE20.32", true, CurrencyInput, Input("countryOfIncorporation", country), Feed("sovereignRatings", (sovereign.Ratings ?? []).Count, $"sovereign of {country}"));
        }

        // Corporates (CRE20.41-47).

        TableLookup Corporate(string because, string citation)
        {
            var more = new List<DecisionInput>();
            if (facts.SupervisedAsBank is { } s) more.Add(Input("supervisedAsBank", s ? "yes" : "no"));
            if (facts.Sme is { } sme) more.Add(Input("sme", sme ? "yes" : "no"));
            ExposureClass("Corporate", because, citation, [.. more]);
            NoShortTerm();
            NoFloor("The floor at the sovereign of incorporation is for bank exposures under SCRA.");

            if (settings.ExternalRatingsAllowed && Ratings.Count > 0)
            {
                Step("approach", "ECRA", "The jurisdiction allows external ratings for risk weights and the counterparty has an eligible rating.",
                    "CRE20.42", true, ExternalRatingsSetting);
                var used = UseRating(Weigh(Ratings, CorporateBand, Table10, 0), "counterparty");
                var band = DueDiligence(used.Band, Table10, 0, "CRE20.42");
                return Lookup(Table10, band, 0, "CRE20.42", $"Corporate exposure, rating band {Table10.Rows[band]}.");
            }

            if (settings.ExternalRatingsAllowed)
            {
                var unratedSme = facts.Sme
                    ?? throw Refuse("whether the counterparty is an SME is missing, and an unrated corporate is weighted by it (CRE20.43, CRE20.47).");
                Step("approach", "Unrated", "The counterparty has no eligible external rating.", "CRE20.43", true, ExternalRatingsSetting);
                NoRating("the counterparty has no eligible rating.", "CRE20.43");
                NoDueDiligence("The one-bucket uplift applies to a rating-based weight, and the counterparty is unrated.", "CRE20.42");
                return unratedSme
                    ? Lookup(Table10, RatedBands + 1, 0, "CRE20.47", "An unrated SME: the consolidated group's annual sales are EUR 50m or less.")
                    : Lookup(Table10, RatedBands, 0, "CRE20.43", "An unrated corporate that is not an SME.");
            }

            var investmentGrade = facts.InvestmentGrade
                ?? throw Refuse("whether the counterparty is investment grade is missing, and a corporate is weighted by it where external ratings are not allowed (CRE20.44, CRE20.46).");
            Step("approach", "Without external ratings", "The jurisdiction does not allow external ratings for risk weights.", "CRE20.44", true,
                ExternalRatingsSetting, Input("investmentGrade", investmentGrade ? "yes" : "no"));
            NoRating("the jurisdiction does not allow external ratings.", "CRE20.44");
            NoDueDiligence("The one-bucket uplift applies to a rating-based weight, and ratings are not allowed.", "CRE20.42");
            if (investmentGrade)
                return Lookup(CorporatesWithoutRatings, 0, 0, "CRE20.46", "An investment-grade corporate.");
            var isSme = facts.Sme
                ?? throw Refuse("whether the counterparty is an SME is missing, and a corporate that is not investment grade is weighted by it (CRE20.44, CRE20.47).");
            return isSme
                ? Lookup(CorporatesWithoutRatings, 1, 0, "CRE20.47", "An SME: the consolidated group's annual sales are EUR 50m or less.")
                : Lookup(CorporatesWithoutRatings, 2, 0, "CRE20.44", "A corporate neither investment grade nor an SME.");
        }

        // Retail.

        TableLookup Individual()
        {
            var regulatory = facts.RegulatoryRetail
                ?? throw Refuse("whether the exposure meets the regulatory retail criteria is missing (CRE20, retail exposures).");
            ExposureClass("Retail", "The counterparty is an individual.", "CRE20, retail exposures", Input("regulatoryRetail", regulatory ? "yes" : "no"));
            Step("approach", regulatory ? "Regulatory retail" : "Other retail",
                regulatory ? "The exposure meets the regulatory retail criteria." : "The exposure does not meet the regulatory retail criteria.",
                "CRE20, retail exposures", true);
            NoRating("retail exposures are not weighted by ratings.", "CRE20, retail exposures");
            NoShortTerm();
            NoDueDiligence("The one-bucket uplift is written for bank and corporate exposures (CRE20.20, CRE20.42).", "CRE20.4");
            NoFloor("The floor at the sovereign of incorporation is for bank exposures under SCRA.");
            return Lookup(Retail, regulatory ? 0 : 1, 0, "CRE20, retail exposures", regulatory ? "Regulatory retail." : "Other retail.");
        }
    }
}
