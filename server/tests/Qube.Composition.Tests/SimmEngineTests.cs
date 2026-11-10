using Qube.Samples.Simm;

namespace Qube.Composition.Tests;

// The SIMM reference engine at its seam (ADR-0012 §5): CRIF lines, IM CSAs and a parameter table in; IM per
// direction and regime out, with every term's value. Expected values are worked by hand from the DEMO table, or come
// from the drill-down sample's separate implementation; none is recomputed here the way the engine computes it.
public class SimmEngineTests
{
    static readonly SimmParameters Demo = SimmParameters.Demo;

    static CrifLine Ir(string line, string trade, string currency, string tenor, double usd, string[] collect, string[]? post = null) =>
        new(line, trade, CrifRiskType.IrCurve, currency, tenor, "OIS", usd, "USD", usd, collect, post ?? collect);

    static ImCsa Collect(string id, string[] regimes, params string[] trades) => new(id, ImDirection.Collect, regimes, trades);

    [Fact] // ADR-0012 §5: one IR delta line is its risk weight times its sensitivity; JPY is low volatility, 5y weighs 21
    public void One_ir_delta_line_is_its_weighted_sensitivity()
    {
        var crif = new[] { Ir("L1", "T1", "JPY", "5y", 1_000, ["JFSA"]) };

        var result = SimmEngine.Calculate(crif, [Collect("IMR-1", ["JFSA"], "T1")], Demo);

        var collect = result.Direction(ImDirection.Collect);
        Assert.Equal(21_000, collect.Im!.Value, 6);
        Assert.Equal("JFSA", collect.GoverningRegime);
    }

    static double CollectIm(IReadOnlyList<CrifLine> crif) =>
        SimmEngine.Calculate(crif, [Collect("IMR-1", ["JFSA"], [.. crif.Select(l => l.TradeId).Distinct()])], Demo)
            .Direction(ImDirection.Collect).Im!.Value;

    [Fact] // ADR-0012 §6, simm.bucket: two tenors offset through ρ = max(0.30, exp(−0.35 |ln(5/10)|)) = 0.78458
    public void Tenors_in_one_currency_diversify_through_the_tenor_correlation()
    {
        // WS = 21 × 1,000 and 23 × −500; K = √(21,000² + 11,500² − 2 × 0.78458 × 21,000 × 11,500)
        var im = CollectIm([Ir("L1", "T1", "JPY", "5y", 1_000, ["JFSA"]), Ir("L2", "T2", "JPY", "10y", -500, ["JFSA"])]);

        Assert.Equal(13_939.0057, im, 3);
    }

    [Fact] // ADR-0012 §5: cross-currency basis sits in its currency's bucket, weighs 20 and correlates 0.25 with every tenor
    public void Cross_currency_basis_sits_in_its_currency_bucket()
    {
        var crif = new[]
        {
            Ir("L1", "T1", "JPY", "5y", 1_000, ["JFSA"]), Ir("L2", "T2", "JPY", "10y", -500, ["JFSA"]),
            new CrifLine("L3", "T3", CrifRiskType.XCcyBasis, "JPY", null, null, 300, "USD", 300, ["JFSA"], ["JFSA"]),
        };

        Assert.Equal(16_087.1340, CollectIm(crif), 3);
    }

    [Fact] // SIMM v2.x: one tenor on two sub-curves correlates by φ (0.95 in the DEMO table)
    public void One_tenor_on_two_sub_curves_correlates_by_phi()
    {
        var crif = new[]
        {
            Ir("L1", "T1", "JPY", "5y", 1_000, ["JFSA"]),
            Ir("L2", "T2", "JPY", "5y", -800, ["JFSA"]) with { Label2 = "Libor3m" },
        };

        Assert.Equal(7_274.6134, CollectIm(crif), 3);
    }

    [Fact] // ADR-0012 §6, simm.delta: currencies aggregate through γ = 0.30 on the bounded bucket sums S_b
    public void Currencies_aggregate_through_gamma_on_the_bucket_sums()
    {
        // JPY: K 13,939.0057, S = ΣWS = 9,500 (inside ±K). USD: 50 × 200 = 10,000. DM = √(K_JPY² + K_USD² + 2 × 0.30 × S_JPY × S_USD)
        var crif = new[]
        {
            Ir("L1", "T1", "JPY", "5y", 1_000, ["JFSA"]), Ir("L2", "T2", "JPY", "10y", -500, ["JFSA"]),
            Ir("L3", "T3", "USD", "5y", 200, ["JFSA"]),
        };

        Assert.Equal(18_742.8888, CollectIm(crif), 3);
    }

    [Fact] // ADR-0012 §6, simm.bucket: S_b = max(min(ΣWS, K_b), −K_b), and the bound is what crosses currencies
    public void A_bucket_sum_beyond_its_margin_is_bounded_by_it()
    {
        // JPY: WS 21,000 and 11,500, K = 30,857.8048 < ΣWS = 32,500, so S = K. DM = √(K² + 10,000² + 2 × 0.30 × K × 10,000)
        var crif = new[]
        {
            Ir("L1", "T1", "JPY", "5y", 1_000, ["JFSA"]), Ir("L2", "T2", "JPY", "10y", 500, ["JFSA"]),
            Ir("L3", "T3", "USD", "5y", 200, ["JFSA"]),
        };

        Assert.Equal(35_175.9996, CollectIm(crif), 3);
    }

    [Fact] // ADR-0012 §6, simm.bucket: CR_b = max(1, √(|Σs| / T_b)) scales every weighted sensitivity of the currency
    public void A_concentrated_currency_is_scaled_by_its_concentration_factor()
    {
        // JPY is low volatility, T = 120mm per bp: CR = √(200 / 120) = 1.290994, IM = 21 × 200mm × CR
        Assert.Equal(5_422_176_684.6904, CollectIm([Ir("L1", "T1", "JPY", "5y", 200e6, ["JFSA"])]), 2);
    }

    [Fact] // SIMM v2.x: γ_bc is scaled by g_bc = min(CR_b, CR_c) / max(CR_b, CR_c)
    public void Currencies_of_different_concentration_correlate_less()
    {
        // JPY CR 1.290994 (K = S = 5,422,176,684.69); USD CR 1 (K 5bn, S −5bn); g = 1 / 1.290994
        var crif = new[] { Ir("L1", "T1", "JPY", "5y", 200e6, ["JFSA"]), Ir("L2", "T2", "USD", "5y", -100e6, ["JFSA"]) };

        Assert.Equal(6_465_291_950.0978, CollectIm(crif), 2);
    }

    static CrifLine Fx(string line, string trade, string currency, double usd) =>
        new(line, trade, CrifRiskType.Fx, currency, null, null, usd, "USD", usd, ["JFSA"], ["JFSA"]);

    [Fact] // ADR-0012 §5: FX delta per 1% weighs 8 in the DEMO table
    public void One_fx_delta_line_is_its_weighted_sensitivity()
    {
        Assert.Equal(280_000, CollectIm([Fx("L1", "T1", "JPY", 35_000)]), 6);
    }

    [Fact] // SIMM v2.x: FX risk factors correlate by ρ_FX (0.50 in the DEMO table)
    public void Fx_currencies_correlate_by_the_fx_correlation()
    {
        // √(280,000² + 160,000² − 2 × 0.5 × 280,000 × 160,000)
        Assert.Equal(243_310.5012, CollectIm([Fx("L1", "T1", "JPY", 35_000), Fx("L2", "T2", "EUR", -20_000)]), 3);
    }

    [Fact] // SIMM v2.x: an FX factor's CR = max(1, √(|s| / T_FX)), T_FX = 5bn per 1% in the DEMO table
    public void A_concentrated_fx_currency_is_scaled_by_its_concentration_factor()
    {
        Assert.Equal(113_137_084_989.8479, CollectIm([Fx("L1", "T1", "JPY", 10e9)]), 1);
    }

    internal static readonly CrifLine[] ConcentratedFx = [Fx("L1", "T1", "JPY", 20e9), Fx("L2", "T2", "EUR", 5e9)];

    [Fact] // ADR-0012 §5, SIMM v2.x: FX currencies of different concentration correlate by ρ_FX f_kl, f_kl = min(CR_k, CR_l) / max(CR_k, CR_l)
    public void Fx_currencies_of_different_concentration_correlate_less()
    {
        // CR_JPY = √(20bn / 5bn) = 2 and CR_EUR = 1, so f = 0.5; WS = 8 × 20bn × 2 = 320bn and 8 × 5bn = 40bn.
        // K = √(320² + 40² + 2 × 0.5 × 0.5 × 320 × 40) bn; without f it would be 341.76bn.
        var regime = CollectRegime(ConcentratedFx);

        Assert.Equal(332_264_954_516.7230, regime.Value, 1);
        Assert.Equal(regime.Value, regime.Lines().Sum(l => l.Contribution), 1); // Euler, with CR and so f held
    }

    [Fact] // ADR-0012 §6, simm.product: IR and FX diversify through ψ = 0.25 in the Rates/FX product class
    public void Risk_classes_diversify_through_psi_in_their_product_class()
    {
        // IR 13,939.0057 and FX 280,000: √(IR² + FX² + 2 × 0.25 × IR × FX)
        var crif = new[] { Ir("L1", "T1", "JPY", "5y", 1_000, ["JFSA"]), Ir("L2", "T2", "JPY", "10y", -500, ["JFSA"]), Fx("L3", "T3", "JPY", 35_000) };

        Assert.Equal(283_805.8433, CollectIm(crif), 3);
    }

    [Fact] // ADR-0012 §1: SIMM is computed per regime the lines name, and the largest governs
    public void The_largest_regime_governs_the_direction()
    {
        // JFSA counts both lines (13,939.0057, diversified); CFTC counts only the 5y one (21,000)
        var crif = new[] { Ir("L1", "T1", "JPY", "5y", 1_000, ["JFSA", "CFTC"]), Ir("L2", "T2", "JPY", "10y", -500, ["JFSA"]) };

        var collect = SimmEngine.Calculate(crif, [Collect("IMR-1", ["JFSA", "CFTC"], "T1", "T2")], Demo).Direction(ImDirection.Collect);

        Assert.Equal(("CFTC", 21_000.0), (collect.GoverningRegime, Math.Round(collect.Im!.Value, 6)));
        Assert.Equal(13_939.0057, collect.Regimes.Single(r => r.Regime == "JFSA").Total.Value, 3);
    }

    [Fact] // ADR-0012 §1: IM-R counts CollectRegulations and IM-P PostRegulations, each under its own IM CSA
    public void Each_direction_counts_its_own_regulations()
    {
        var crif = new[] { Ir("L1", "T1", "JPY", "5y", 1_000, ["JFSA"], ["EMIR"]), Ir("L2", "T2", "JPY", "10y", -500, [], ["EMIR"]) };

        var result = SimmEngine.Calculate(crif,
            [Collect("IMR-1", ["JFSA"], "T1", "T2"), new ImCsa("IMP-1", ImDirection.Post, ["EMIR"], ["T1", "T2"])], Demo);

        Assert.Equal(("JFSA", 21_000.0), (result.Direction(ImDirection.Collect).GoverningRegime, Math.Round(result.Direction(ImDirection.Collect).Im!.Value, 6)));
        Assert.Equal("EMIR", result.Direction(ImDirection.Post).GoverningRegime);
        Assert.Equal(13_939.0057, result.Direction(ImDirection.Post).Im!.Value, 3);
    }

    [Fact] // ADR-0012 §2: a trade counts only under the IM CSA that holds it
    public void A_trade_the_csa_does_not_hold_is_not_counted()
    {
        var crif = new[] { Ir("L1", "T1", "JPY", "5y", 1_000, ["JFSA"]), Ir("L2", "T2", "JPY", "10y", -500, ["JFSA"]) };

        var collect = SimmEngine.Calculate(crif, [Collect("IMR-1", ["JFSA"], "T1")], Demo).Direction(ImDirection.Collect);

        Assert.Equal(21_000, collect.Im!.Value, 6);
    }

    [Fact] // ADR-0012 §1: only the regimes the IM CSA serves are computed, of those its lines name
    public void A_regime_the_csa_does_not_serve_is_not_computed()
    {
        var crif = new[] { Ir("L1", "T1", "JPY", "5y", 1_000, ["JFSA", "CFTC"]) };

        var collect = SimmEngine.Calculate(crif, [Collect("IMR-1", ["JFSA", "EMIR"], "T1")], Demo).Direction(ImDirection.Collect);

        Assert.Equal(["JFSA"], collect.Regimes.Select(r => r.Regime));
    }

    [Fact] // Spine 1: a direction with nothing in scope has no IM, never an IM of 0
    public void A_direction_with_nothing_in_scope_has_no_im()
    {
        var crif = new[] { Ir("L1", "T1", "JPY", "5y", 1_000, ["JFSA"]) };

        var collect = SimmEngine.Calculate(crif, [Collect("IMR-1", ["CFTC"], "T1")], Demo).Direction(ImDirection.Collect);

        Assert.Equal((null, null, 0), (collect.Im, collect.GoverningRegime, collect.Regimes.Count));
    }

    static SimmTerm CollectRegime(IReadOnlyList<CrifLine> crif) =>
        SimmEngine.Calculate(crif, [Collect("IMR-1", ["JFSA"], [.. crif.Select(l => l.TradeId).Distinct()])], Demo)
            .Direction(ImDirection.Collect).Regimes.Single().Total;

    [Fact] // ADR-0012 §6: the Euler allocation gives a lone line all of the SIMM
    public void A_lone_line_contributes_all_of_the_simm()
    {
        var regime = CollectRegime([Ir("L1", "T1", "JPY", "5y", 1_000, ["JFSA"])]);

        Assert.Equal(21_000, regime.Lines().Single().Contribution, 6);
    }

    [Fact] // ADR-0012 §6: a line's contribution is WS_k ∂K/∂WS_k = WS_k (WS_k + ρ WS_l) / K
    public void A_line_contributes_its_weighted_sensitivity_times_its_marginal_margin()
    {
        // 21,000 × (21,000 − 0.78458 × 11,500) / 13,939.0057 and −11,500 × (−11,500 + 0.78458 × 21,000) / 13,939.0057
        var regime = CollectRegime([Ir("L1", "T1", "JPY", "5y", 1_000, ["JFSA"]), Ir("L2", "T2", "JPY", "10y", -500, ["JFSA"])]);

        var byLine = regime.Lines().ToDictionary(l => l.Line, l => l.Contribution);
        Assert.Equal(18_044.5396, byLine["L1"], 3);
        Assert.Equal(-4_105.5338, byLine["L2"], 3);
    }

    // Two currencies with a bounded bucket sum, a concentrated currency, cross-currency basis and two FX currencies.
    internal static readonly CrifLine[] Mixed =
    [
        Ir("L1", "T1", "JPY", "5y", 150e6, ["JFSA"]), Ir("L2", "T2", "JPY", "10y", 40e6, ["JFSA"]),
        new("L3", "T2", CrifRiskType.XCcyBasis, "JPY", null, null, -3e6, "USD", -3e6, ["JFSA"], ["JFSA"]),
        Ir("L4", "T3", "USD", "2y", -60e6, ["JFSA"]), Ir("L5", "T3", "USD", "30y", 20e6, ["JFSA"]) with { Label2 = "Libor3m" },
        Fx("L6", "T2", "JPY", 2e9), Fx("L7", "T4", "EUR", -1.5e9),
    ];

    [Fact] // ADR-0012 §6: at every term, the children's contributions add up to the term's, and the lines' to the SIMM
    public void Contributions_add_up_at_every_term()
    {
        var regime = CollectRegime(Mixed);

        Assert.Equal(regime.Value, regime.Contribution, 3);
        Assert.Equal(regime.Value, regime.Lines().Sum(l => l.Contribution), 3);
        foreach (var term in Walk(regime).Where(t => t.Children.Count > 0))
            Assert.Equal(term.Contribution, term.Children.Sum(c => c.Contribution), 3);
    }

    [Fact] // ADR-0012 §6: a trade's contribution to a term is the sum of its lines' under that term
    public void A_trades_contribution_is_the_sum_of_its_lines()
    {
        var regime = CollectRegime(Mixed);
        var jpy = Walk(regime).Single(t => t.Kind == "simm.bucket" && t.Name == "JPY");

        var trades = jpy.TradeContributions();

        Assert.Equal(["T1", "T2"], trades.Keys.Order());
        Assert.Equal(jpy.Lines().Where(l => l.TradeId == "T2").Sum(l => l.Contribution), trades["T2"], 6);
        Assert.Equal(jpy.Contribution, trades.Values.Sum(), 3);
    }

    static IEnumerable<SimmTerm> Walk(SimmTerm term) => [term, .. term.Children.SelectMany(Walk)];

    [Fact] // Spine 1: a currency the parameter table does not place is refused by name, never weighted as another
    public void A_currency_without_a_volatility_group_is_refused_by_name()
    {
        var error = Assert.Throws<SimmInputException>(() => CollectRegime([Ir("L1", "T1", "GBP", "5y", 1_000, ["JFSA"])]));

        Assert.Contains("GBP", error.Message);
        Assert.Contains("L1", error.Message);
    }

    [Fact] // Spine 1: an IR line whose tenor is not one of SIMM's twelve is refused by name
    public void A_tenor_outside_the_table_is_refused_by_name()
    {
        var error = Assert.Throws<SimmInputException>(() => CollectRegime([Ir("L1", "T1", "JPY", "7y", 1_000, ["JFSA"])]));

        Assert.Contains("7y", error.Message);
        Assert.Contains("L1", error.Message);
    }
}

// The sample the owner agreed the drill-down on (ADR-0012), computed by its own Python implementation: the engine
// gives its figures for both directions, every regime, Before (without the new trade) and After.
public class SimmSampleTests
{
    static readonly System.Text.Json.Nodes.JsonObject Sample =
        System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "simm-sample.json")))!.AsObject();

    internal static IReadOnlyList<CrifLine> Crif() => [.. Sample["crif"]!.AsArray().Select(n => new CrifLine(
        n!["line"]!.GetValue<string>(), n["trade"]!.GetValue<string>(), Enum.Parse<CrifRiskType>(n["riskType"]!.GetValue<string>()),
        n["qualifier"]!.GetValue<string>(), n["label1"]?.GetValue<string>(), n["label2"]?.GetValue<string>(),
        n["amountUsd"]!.GetValue<double>(), "USD", n["amountUsd"]!.GetValue<double>(),
        [.. n["collect"]!.AsArray().Select(x => x!.GetValue<string>())], [.. n["post"]!.AsArray().Select(x => x!.GetValue<string>())]))];

    public static TheoryData<string, string, string> Cases() => new()
    {
        { "collect", "JFSA", "before" }, { "collect", "JFSA", "after" }, { "collect", "CFTC", "before" }, { "collect", "CFTC", "after" },
        { "post", "JFSA", "before" }, { "post", "JFSA", "after" }, { "post", "EMIR", "before" }, { "post", "EMIR", "after" },
    };

    [Theory] // ADR-0012 §5, §6: the sample's SIMM per regime and every line's contribution
    [MemberData(nameof(Cases))]
    public void The_engine_gives_the_samples_figures(string direction, string regime, string side)
    {
        var trades = Sample["trades"]!.AsArray()
            .Where(t => side == "after" || !t!["isNew"]!.GetValue<bool>()).Select(t => t!["id"]!.GetValue<string>()).ToArray();
        var csa = new ImCsa("IM-1", direction == "collect" ? ImDirection.Collect : ImDirection.Post, [regime], trades);
        var expected = Sample["expected"]![$"{direction}/{regime}/{side}"]!;

        var total = SimmEngine.Calculate(Crif(), [csa], SimmParameters.Demo).Directions.Single().Regimes.Single().Total;

        Assert.Equal(expected["total"]!.GetValue<double>(), total.Value, 0.5);
        var contributions = total.Lines().ToDictionary(l => l.Line, l => l.Contribution);
        var want = expected["contributions"]!.AsObject();
        Assert.Equal(want.Select(p => p.Key).Order(), contributions.Keys.Order());
        foreach (var (line, value) in want)
            Assert.Equal(value!.GetValue<double>(), contributions[line], 0.05);
    }
}
