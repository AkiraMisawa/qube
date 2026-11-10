using Qube.Samples.Ccr;

namespace Qube.Composition.Tests;

/// <summary>
/// ADR-0013 §5: the SA risk weight is resolved at run time from the counterparty, and every step of the decision is
/// written down. The expected weights are Basel III's tables (CRE20 as in force from 2023-01-01, CRE21), read off the
/// text, not recomputed here.
/// </summary>
public class CcrRiskWeightTests
{
    static readonly RiskWeightSettings Basel = new(ExternalRatingsAllowed: true, Version: "basel-iii · 2023-01-01");
    static readonly RiskWeightSettings NoRatings = Basel with { ExternalRatingsAllowed = false, Version = "no ratings · 1" };

    static readonly ExposureFacts Long = new(new OriginalMaturity("T-1003", new(2026, 1, 15), new(2037, 1, 15)), InLocalCurrency: true);
    static readonly ExposureFacts Foreign = Long with { InLocalCurrency = false };
    static ExposureFacts ShortUntil(DateOnly end) => new(new OriginalMaturity("T-2001", new(2026, 11, 9), end), InLocalCurrency: true);

    static readonly SovereignFacts SovereignAa = new(Ratings: [new("Agency 1", "AA")]);

    static ExternalRating R(string grade, string agency = "Agency 1") => new(agency, grade);

    static CounterpartyRiskFacts Of(InstitutionType type, params ExternalRating[] ratings) =>
        new("CP-0042", "R-7", type, Ratings: ratings, CountryOfIncorporation: "GB", SovereignOfIncorporation: SovereignAa,
            DueDiligenceBucketsUp: 0);

    static CounterpartyRiskFacts Bank(params ExternalRating[] ratings) => Of(InstitutionType.Bank, ratings);

    static CounterpartyRiskFacts ScraBank(ScraGrade grade, double? cet1 = null, double? leverage = null) =>
        Bank() with { Scra = new ScraAssessment(grade, cet1, leverage) };

    static CounterpartyRiskFacts Corporate(params ExternalRating[] ratings) => Of(InstitutionType.Corporate, ratings) with { Sme = false };

    static RiskWeightDecision Resolve(CounterpartyRiskFacts facts, ExposureFacts? exposure = null, RiskWeightSettings? settings = null) =>
        RiskWeights.Resolve(facts, exposure ?? Long, settings ?? Basel);

    static DecisionStep Step(RiskWeightDecision decision, string key) => decision.Steps.Single(s => s.Key == key);

    static RiskWeightRefused Refused(CounterpartyRiskFacts facts, ExposureFacts? exposure = null, RiskWeightSettings? settings = null) =>
        Assert.Throws<RiskWeightRefused>(() => Resolve(facts, exposure, settings));

    static readonly string[] EightSteps = ["exposureClass", "approach", "ratings", "ratingUsed", "shortTerm", "dueDiligence", "floor", "tableWeight"];

    // Banks, ECRA

    [Fact] // ADR-0013 §5, CRE20.18, CRE21.9-11: of three ratings, the higher of the two lowest weights applies
    public void A_bank_with_three_ratings_takes_the_higher_of_the_two_lowest_weights()
    {
        var decision = Resolve(Bank(R("AA-", "Agency 1"), R("A+", "Agency 2"), R("BBB+", "Agency 3")));
        Assert.Equal(0.30, decision.RiskWeight);
        Assert.Equal(EightSteps, decision.Steps.Select(s => s.Key));
        Assert.Equal("Bank", Step(decision, "exposureClass").Outcome);
        Assert.Equal("ECRA", Step(decision, "approach").Outcome);
        Assert.Equal("A+ (Agency 2)", Step(decision, "ratingUsed").Outcome);
        Assert.Equal("CRE20.18", Step(decision, "tableWeight").Citation);
        Assert.Equal(("A+ to A-", "Base"), (decision.Lookup.Row, decision.Lookup.Column));
        Assert.False(Step(decision, "floor").Applies);
        Assert.Null(decision.Floor);
    }

    [Theory] // ADR-0013 §5, CRE20.18 Table 6: one rating is used as it is, on either agency scale
    [InlineData("AA-", 0.20)]
    [InlineData("A-", 0.30)]
    [InlineData("BBB", 0.50)]
    [InlineData("B-", 1.00)]
    [InlineData("CCC+", 1.50)]
    [InlineData("Baa3", 0.50)]
    [InlineData("Aa1", 0.20)]
    public void A_bank_with_one_rating_takes_its_band_of_table_6(string grade, double weight)
    {
        var decision = Resolve(Bank(R(grade)));
        Assert.Equal(weight, decision.RiskWeight);
        Assert.Equal($"{grade} (Agency 1)", Step(decision, "ratingUsed").Outcome);
    }

    [Fact] // ADR-0013 §5, CRE21.9-11: of two ratings with different weights, the higher weight applies
    public void A_bank_with_two_ratings_takes_the_higher_weight()
    {
        var decision = Resolve(Bank(R("A1", "Agency 1"), R("BBB+", "Agency 2")));
        Assert.Equal(0.50, decision.RiskWeight);
        Assert.Equal("BBB+ (Agency 2)", Step(decision, "ratingUsed").Outcome);
    }

    [Fact] // ADR-0013 §5, CRE20.19: an original maturity of three months or less takes Table 6's short-term weights
    public void A_bank_exposure_of_three_months_takes_the_short_term_weight_and_one_day_more_does_not()
    {
        var inside = Resolve(Bank(R("BB")), ShortUntil(new(2027, 2, 9)));
        Assert.Equal(0.50, inside.RiskWeight);
        Assert.True(Step(inside, "shortTerm").Applies);
        Assert.Equal("Short-term", inside.Lookup.Column);

        var over = Resolve(Bank(R("BB")), ShortUntil(new(2027, 2, 10)));
        Assert.Equal(1.00, over.RiskWeight);
        Assert.False(Step(over, "shortTerm").Applies);
        Assert.Contains("T-2001", Step(over, "shortTerm").Because);
    }

    [Fact] // ADR-0013 §5, CRE20.20: due diligence takes the weight at least one bucket higher, up to the last band
    public void Due_diligence_moves_a_bank_up_by_the_buckets_it_finds()
    {
        var one = Resolve(Bank(R("A+")) with { DueDiligenceBucketsUp = 1 });
        Assert.Equal(0.50, one.RiskWeight);
        Assert.Equal("BBB+ to BBB-", one.Lookup.Row);
        Assert.True(Step(one, "dueDiligence").Applies);

        Assert.Equal(1.00, Resolve(Bank(R("A+")) with { DueDiligenceBucketsUp = 2 }).RiskWeight);
        Assert.Equal(1.50, Resolve(Bank(R("B-")) with { DueDiligenceBucketsUp = 2 }).RiskWeight);
        Assert.False(Step(Resolve(Bank(R("B-"))), "dueDiligence").Applies);
    }

    [Fact] // ADR-0013 §5, CRE20.20: a bucket is a weight, so a short-term bank rated AA- or A goes up to 50%, not to the next 20% row
    public void Due_diligence_on_a_short_term_bank_takes_the_next_higher_weight()
    {
        foreach (var rating in new[] { "AA-", "A" })
        {
            var decision = Resolve(Bank(R(rating)) with { DueDiligenceBucketsUp = 1 }, ShortUntil(new(2027, 2, 9)));
            Assert.Equal("Short-term", decision.Lookup.Column);
            Assert.Equal(0.50, decision.RiskWeight);
            Assert.Equal("BB+ to B-", decision.Lookup.Row);
            Assert.Contains("to BB+ to B- at 50%", Step(decision, "dueDiligence").Because);
        }
        var worst = Resolve(Bank(R("CCC")) with { DueDiligenceBucketsUp = 1 }, ShortUntil(new(2027, 2, 9)));
        Assert.Equal(1.50, worst.RiskWeight);
        Assert.Contains("already the highest weight", Step(worst, "dueDiligence").Because);
    }

    [Fact] // ADR-0013 §5, CRE20.20: due diligence never lowers a weight, and a rated weight is not given without it
    public void A_rated_bank_without_a_due_diligence_outcome_or_with_a_lowering_one_is_refused()
    {
        Assert.Contains("due diligence", Refused(Bank(R("A")) with { DueDiligenceBucketsUp = null }).Message);
        Assert.Contains("never lower", Refused(Bank(R("A")) with { DueDiligenceBucketsUp = -1 }).Message);
    }

    // Banks, SCRA

    [Theory] // ADR-0013 §5, CRE20.17, CRE20.21 Table 7: an unrated bank is weighted by its SCRA grade
    [InlineData(ScraGrade.A, 0.40)]
    [InlineData(ScraGrade.B, 0.75)]
    [InlineData(ScraGrade.C, 1.50)]
    public void An_unrated_bank_takes_its_scra_grade(ScraGrade grade, double weight)
    {
        var decision = Resolve(ScraBank(grade));
        Assert.Equal(weight, decision.RiskWeight);
        Assert.Equal($"SCRA, grade {grade}", Step(decision, "approach").Outcome);
        Assert.False(Step(decision, "ratingUsed").Applies);
        Assert.False(Step(decision, "dueDiligence").Applies);
        Assert.Equal("CRE20.21", Step(decision, "tableWeight").Citation);
    }

    [Fact] // ADR-0013 §5, CRE20.17: where external ratings are not allowed, a rated bank is weighted under SCRA
    public void A_rated_bank_in_a_jurisdiction_without_ratings_takes_its_scra_grade()
    {
        var decision = Resolve(Bank(R("AA")) with { Scra = new(ScraGrade.B) }, settings: NoRatings);
        Assert.Equal(0.75, decision.RiskWeight);
        Assert.False(Step(decision, "ratings").Applies);
        Assert.Contains("does not allow", Step(decision, "approach").Because);
    }

    [Fact] // ADR-0013 §5, CRE20.21 footnote 15: grade A with CET1 of 14% and leverage of 5% or more takes 30%
    public void A_grade_a_bank_with_strong_capital_takes_thirty_percent()
    {
        Assert.Equal(0.30, Resolve(ScraBank(ScraGrade.A, cet1: 0.14, leverage: 0.05)).RiskWeight);
        Assert.Equal(0.40, Resolve(ScraBank(ScraGrade.A, cet1: 0.139, leverage: 0.06)).RiskWeight);
        Assert.Equal(0.40, Resolve(ScraBank(ScraGrade.A, cet1: 0.15, leverage: 0.049)).RiskWeight);
        Assert.Equal(0.20, Resolve(ScraBank(ScraGrade.A, cet1: 0.14, leverage: 0.05), ShortUntil(new(2027, 2, 9))).RiskWeight);
        Assert.Contains("not given", Step(Resolve(ScraBank(ScraGrade.A)), "tableWeight").Because);
    }

    [Theory] // ADR-0013 §5, CRE20.31: Table 7's short-term weights
    [InlineData(ScraGrade.A, 0.20)]
    [InlineData(ScraGrade.B, 0.50)]
    [InlineData(ScraGrade.C, 1.50)]
    public void A_short_term_scra_exposure_takes_the_short_term_weight(ScraGrade grade, double weight)
    {
        var decision = Resolve(ScraBank(grade), ShortUntil(new(2027, 2, 9)));
        Assert.Equal(weight, decision.RiskWeight);
        Assert.Equal("CRE20.31", Step(decision, "shortTerm").Citation);
    }

    [Fact] // ADR-0013 §5, CRE20.32: under SCRA, a foreign-currency exposure is not below the sovereign of incorporation
    public void A_foreign_currency_scra_exposure_is_floored_at_its_sovereign()
    {
        var weak = ScraBank(ScraGrade.A) with { SovereignOfIncorporation = new(Ratings: [R("B+")]) };
        var floored = Resolve(weak, Foreign);
        Assert.Equal(1.00, floored.RiskWeight);
        Assert.Equal(0.40, floored.Lookup.Weight);
        Assert.Equal(1.00, floored.Floor);
        Assert.True(Step(floored, "floor").Applies);
        Assert.Equal("100%", Step(floored, "floor").Outcome);
        Assert.Contains("GB", Step(floored, "floor").Because);

        var local = Resolve(weak, Long);
        Assert.Equal(0.40, local.RiskWeight);
        Assert.Null(local.Floor);
        Assert.False(Step(local, "floor").Applies);

        Assert.Equal(1.00, Resolve(weak with { SovereignOfIncorporation = new() }, Foreign).RiskWeight);

        var strong = Resolve(ScraBank(ScraGrade.A), Foreign);
        Assert.Equal(0.40, strong.RiskWeight);
        Assert.Equal(0.0, strong.Floor);
    }

    [Fact] // ADR-0013 §5, §6: an SCRA bank without its grade, or a floor without its sovereign, refuses the measure
    public void An_scra_bank_without_its_grade_or_its_sovereign_is_refused()
    {
        Assert.Contains("SCRA grade", Refused(Bank()).Message);
        Assert.Contains("sovereign", Refused(ScraBank(ScraGrade.A) with { SovereignOfIncorporation = null }, Foreign).Message);
    }

    // Securities firms and other financial institutions

    [Fact] // ADR-0013 §5, CRE20.40: supervised as a bank, weighted as a bank; otherwise as a corporate
    public void A_securities_firm_is_a_bank_only_when_it_is_supervised_as_one()
    {
        var supervised = Resolve(Of(InstitutionType.SecuritiesFirm, R("A")) with { SupervisedAsBank = true });
        Assert.Equal(0.30, supervised.RiskWeight);
        Assert.Equal("Bank", Step(supervised, "exposureClass").Outcome);
        Assert.Equal("CRE20.40", Step(supervised, "exposureClass").Citation);

        var other = Resolve(Of(InstitutionType.OtherFinancialInstitution, R("A")) with { SupervisedAsBank = false });
        Assert.Equal(0.50, other.RiskWeight);
        Assert.Equal("Corporate", Step(other, "exposureClass").Outcome);

        Assert.Contains("supervised", Refused(Of(InstitutionType.SecuritiesFirm, R("A"))).Message);
    }

    // Corporates

    [Theory] // ADR-0013 §5, CRE20.42 Table 10
    [InlineData("AA-", 0.20)]
    [InlineData("A", 0.50)]
    [InlineData("BBB-", 0.75)]
    [InlineData("BB-", 1.00)]
    [InlineData("B+", 1.50)]
    public void A_rated_corporate_takes_its_band_of_table_10(string grade, double weight)
    {
        var decision = Resolve(Corporate(R(grade)));
        Assert.Equal(weight, decision.RiskWeight);
        Assert.Equal("CRE20.42", Step(decision, "tableWeight").Citation);
        Assert.False(Step(decision, "shortTerm").Applies);
        Assert.False(Step(decision, "floor").Applies);
    }

    [Fact] // ADR-0013 §5, CRE20.42: due diligence applies to a rated corporate too
    public void Due_diligence_moves_a_corporate_up()
    {
        Assert.Equal(0.75, Resolve(Corporate(R("A")) with { DueDiligenceBucketsUp = 1 }).RiskWeight);
    }

    [Fact] // ADR-0013 §5, CRE20.43, CRE20.47: an unrated corporate is 100%, an unrated SME 85%
    public void An_unrated_corporate_takes_one_hundred_percent_and_an_unrated_sme_eighty_five()
    {
        var unrated = Resolve(Corporate());
        Assert.Equal(1.00, unrated.RiskWeight);
        Assert.Equal("CRE20.43", Step(unrated, "tableWeight").Citation);

        var sme = Resolve(Corporate() with { Sme = true });
        Assert.Equal(0.85, sme.RiskWeight);
        Assert.Equal("CRE20.47", Step(sme, "tableWeight").Citation);

        Assert.Contains("SME", Refused(Corporate() with { Sme = null }).Message);
    }

    [Fact] // ADR-0013 §5, CRE20.44-47: without ratings, investment grade is 65%, an SME 85%, any other corporate 100%
    public void A_corporate_in_a_jurisdiction_without_ratings_is_weighted_by_investment_grade_and_size()
    {
        Assert.Equal(0.65, Resolve(Corporate(R("AAA")) with { InvestmentGrade = true }, settings: NoRatings).RiskWeight);
        Assert.Equal(0.85, Resolve(Corporate() with { InvestmentGrade = false, Sme = true }, settings: NoRatings).RiskWeight);
        var other = Resolve(Corporate(R("AAA")) with { InvestmentGrade = false }, settings: NoRatings);
        Assert.Equal(1.00, other.RiskWeight);
        Assert.Equal("CRE20.44", Step(other, "tableWeight").Citation);

        Assert.Contains("investment grade", Refused(Corporate(), settings: NoRatings).Message);
    }

    // Sovereigns, public sector entities, multilateral development banks

    [Theory] // ADR-0013 §5, CRE20.7 Table 1
    [InlineData("AA-", 0.00)]
    [InlineData("A", 0.20)]
    [InlineData("BBB-", 0.50)]
    [InlineData("B", 1.00)]
    [InlineData("CCC", 1.50)]
    [InlineData(null, 1.00)]
    public void A_sovereign_takes_its_band_of_table_1(string? grade, double weight)
    {
        var decision = Resolve(Of(InstitutionType.Sovereign, grade is null ? [] : [R(grade)]));
        Assert.Equal(weight, decision.RiskWeight);
        Assert.Equal("CRE20.7", Step(decision, "tableWeight").Citation);
        Assert.Equal(EightSteps, decision.Steps.Select(s => s.Key));
    }

    [Fact] // ADR-0013 §5, CRE20.9: where the jurisdiction recognises ECA scores, the score weighs a sovereign
    public void A_sovereign_with_a_recognised_eca_score_takes_table_2()
    {
        var eca = Basel with { EcaScoresRecognised = true };
        var decision = Resolve(Of(InstitutionType.Sovereign, R("AA")) with { EcaScore = 3 }, settings: eca);
        Assert.Equal(0.50, decision.RiskWeight);
        Assert.Equal("CRE20.9", Step(decision, "tableWeight").Citation);
        Assert.Equal(0.00, Resolve(Of(InstitutionType.Sovereign, R("AA")) with { EcaScore = 3 }).RiskWeight);
        Assert.Contains("ECA", Refused(Of(InstitutionType.Sovereign) with { EcaScore = 8 }, settings: eca).Message);
    }

    [Fact] // ADR-0013 §5, CRE20.8: the domestic-currency discretion is configuration, for the sovereign it names
    public void The_domestic_sovereign_in_its_own_currency_takes_the_configured_weight()
    {
        var domestic = Basel with { DomesticSovereign = new DomesticSovereignTreatment("GB", 0.0) };
        var sovereign = Of(InstitutionType.Sovereign, R("A"));
        var local = Resolve(sovereign, Long, domestic);
        Assert.Equal(0.00, local.RiskWeight);
        Assert.Equal("CRE20.8", Step(local, "tableWeight").Citation);
        Assert.Equal(0.20, Resolve(sovereign, Foreign, domestic).RiskWeight);
        Assert.Equal(0.20, Resolve(sovereign with { CountryOfIncorporation = "FR" }, Long, domestic).RiskWeight);
    }

    [Fact] // ADR-0013 §5, CRE20.10: the BIS, the IMF, the ECB, the EU, the ESM and the EFSF take 0%
    public void An_international_organisation_takes_zero()
    {
        var decision = Resolve(Of(InstitutionType.InternationalOrganisation, R("AAA")));
        Assert.Equal(0.00, decision.RiskWeight);
        Assert.Equal("CRE20.10", Step(decision, "tableWeight").Citation);
    }

    [Fact] // ADR-0013 §5, CRE20.11 option 1: a PSE is weighted by its sovereign's rating, on Table 3
    public void A_pse_under_option_1_takes_its_sovereigns_band()
    {
        var pse = Of(InstitutionType.PublicSectorEntity, R("AAA")) with { SovereignOfIncorporation = new(Ratings: [R("A")]) };
        Assert.Equal(0.50, Resolve(pse).RiskWeight);
        Assert.Equal(1.00, Resolve(pse with { SovereignOfIncorporation = new() }).RiskWeight);
        Assert.Contains("sovereign", Refused(pse with { SovereignOfIncorporation = null }).Message);
    }

    [Fact] // ADR-0013 §5, CRE20.11 option 2: a PSE is weighted by its own rating, on Table 4
    public void A_pse_under_option_2_takes_its_own_band()
    {
        var option2 = Basel with { PseOption = PseOption.OwnRating };
        Assert.Equal(0.50, Resolve(Of(InstitutionType.PublicSectorEntity, R("BBB")), settings: option2).RiskWeight);
        Assert.Equal(1.00, Resolve(Of(InstitutionType.PublicSectorEntity, R("BB")), settings: option2).RiskWeight);
        Assert.Equal(0.50, Resolve(Of(InstitutionType.PublicSectorEntity), settings: option2).RiskWeight);
    }

    [Fact] // ADR-0013 §5, CRE20.12: a PSE the supervisor treats as its sovereign takes the sovereign's weight
    public void A_pse_treated_as_its_sovereign_takes_table_1()
    {
        var pse = Of(InstitutionType.PublicSectorEntity) with { PseTreatedAsSovereign = true, SovereignOfIncorporation = new(Ratings: [R("A")]) };
        var decision = Resolve(pse);
        Assert.Equal(0.20, decision.RiskWeight);
        Assert.Equal("CRE20.12", Step(decision, "exposureClass").Citation);
    }

    [Fact] // ADR-0013 §5, CRE20.14-15: an eligible MDB takes 0%; another takes Table 5, unrated 50%
    public void A_multilateral_development_bank_takes_zero_or_table_5()
    {
        Assert.Equal(0.00, Resolve(Of(InstitutionType.MultilateralDevelopmentBank, R("AAA")) with { EligibleZeroWeightMdb = true }).RiskWeight);
        Assert.Equal(0.30, Resolve(Of(InstitutionType.MultilateralDevelopmentBank, R("A"))).RiskWeight);
        Assert.Equal(0.50, Resolve(Of(InstitutionType.MultilateralDevelopmentBank)).RiskWeight);
        Assert.Equal(0.50, Resolve(Of(InstitutionType.MultilateralDevelopmentBank, R("AAA")), settings: NoRatings).RiskWeight);
    }

    // Retail

    [Fact] // ADR-0013 §5, CRE20 retail exposures: regulatory retail 75%, other retail 100%
    public void An_individual_is_regulatory_retail_or_other_retail()
    {
        Assert.Equal(0.75, Resolve(Of(InstitutionType.Individual) with { RegulatoryRetail = true }).RiskWeight);
        Assert.Equal(1.00, Resolve(Of(InstitutionType.Individual) with { RegulatoryRetail = false }).RiskWeight);
        Assert.Contains("retail", Refused(Of(InstitutionType.Individual)).Message);
    }

    // Refusal

    [Fact] // ADR-0013 §6: a rating on no known scale refuses the measure by name rather than guess a band
    public void A_rating_on_no_known_scale_is_refused()
    {
        var refused = Refused(Bank(R("A-1+")));
        Assert.Contains("A-1+", refused.Message);
        Assert.Contains("CP-0042", refused.Message);
    }
}
