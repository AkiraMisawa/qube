using Qube.Samples.Desk;

namespace Qube.Composition.Tests;

/// <summary>
/// ADR-0019 §4, §7 and §8: a result set is read as the engine wrote it or refused by name. The first test is the
/// contract test: the files under Fixtures/desk/alpha-small-128 are the engine's own, copied at the commit the
/// manifest states, so a change of the engine's output shape fails here.
/// </summary>
public class DeskResultFilesTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "qube-desk-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    const string Manifest = """
        { "engine": "alpha", "engineCommit": "abc123", "inputSet": "synthetic/tiny", "valuationDate": "2026-11-13",
          "baseScenario": "S0000",
          "counterparties": { "C0001": { "legalEntity": "SYN-LE-0001" } },
          "units": { "zero_rate": "rate" } }
        """;
    const string Xva = "scenario,counterparty,metric,value,std_error\nS0000,C0001,CVA,-100.5,2\nS0000,C0009,CVA,-1,0\n";
    const string Risk = "scenario,counterparty,metric,kind,input,key,sensitivity\nS0000,C0001,CVA,zero_rate,JPY-OIS,1d,1e-3\nS0000,C0001,CVA,fx_notional_delta,USD,foreign_amount,5\n";
    const string Trades = "scenario,trade,counterparty,netting_set,in_flow_table,by_states,pv0,pv0_std_error,reason\nS0000,T1,C0001,C0001-MA1,true,false,12.5,0,\"a, b\"\n";

    DeskResultSet Read(string? manifest = Manifest, string? xva = Xva, string? risk = Risk, string? trades = Trades)
    {
        var directory = Path.Combine(_root, "set");
        Directory.CreateDirectory(directory);
        void Write(string name, string? text) { if (text is not null) File.WriteAllText(Path.Combine(directory, name), text); }
        Write("manifest.json", manifest);
        Write("xva.csv", xva);
        Write("risk.csv", risk);
        Write("trades.csv", trades);
        return new DeskResultFiles(_root).Read("set");
    }

    DeskResultsRefused Refused(string? manifest = Manifest, string? xva = Xva, string? risk = Risk, string? trades = Trades) =>
        Assert.Throws<DeskResultsRefused>(() => Read(manifest, xva, risk, trades));

    [Fact]
    public void The_engines_small_result_set_reads_through_the_adapter()
    {
        var set = new DeskResultFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures", "desk")).Read("alpha-small-128");

        Assert.Equal("alpha", set.Manifest.Engine);
        Assert.Equal(40, set.Manifest.EngineCommit.Length);
        Assert.Equal(new DateOnly(2026, 11, 13), set.Manifest.ValuationDate);
        Assert.Equal(64, set.ContentHash.Length);

        Assert.Equal(["C0001", "C0002", "C0003", "C0004"], set.Counterparties.Select(c => c.Code));
        Assert.All(set.Counterparties, c => Assert.NotNull(c.LegalEntity));

        var book = set.Values.Where(v => v.Scenario == set.Manifest.BaseScenario).ToList();
        foreach (var counterparty in set.Counterparties)
            Assert.Equal(["CVA", "ColVA", "FVA"],
                book.Where(v => v.Counterparty == counterparty.Code).Select(v => v.Metric).Order(StringComparer.Ordinal));
        // The engine's sign (ADR-0019 §9): a cost is negative, and the adapter does not turn it over.
        Assert.All(book.Where(v => v.Metric == "CVA"), v => Assert.True(v.Value < 0));

        var kinds = set.Sensitivities.Where(s => s.Scenario == set.Manifest.BaseScenario).Select(s => s.Kind).ToHashSet();
        Assert.Superset(new HashSet<string> { "zero_rate", "par_rate", "discount_factor", "fx_spot", "fx_notional_delta",
            "hazard_rate", "survival_probability", "ir_normal_vol", "fx_atm_vol" }, kinds);
        Assert.Contains(set.Sensitivities, s => s is { Kind: "fx_notional_delta", Key: "JPY_amount" });

        Assert.NotEmpty(set.Trades);
        Assert.All(set.Trades, t => Assert.Contains(set.Counterparties, c => c.Code == t.Counterparty));
    }

    [Fact]
    public void Rows_read_with_their_values_and_a_quoted_cell()
    {
        var set = Read();

        Assert.Equal(new DeskValue("S0000", "C0001", "CVA", -100.5, 2), set.Values[0]);
        Assert.Equal(new DeskSensitivity("S0000", "C0001", "CVA", "zero_rate", "JPY-OIS", "1d", 1e-3), set.Sensitivities[0]);
        Assert.Equal(new DeskTrade("S0000", "T1", "C0001", "C0001-MA1", 12.5, 0), Assert.Single(set.Trades));
    }

    [Fact]
    public void A_counterparty_the_mapping_does_not_hold_reads_as_unmapped_with_its_code()
    {
        var set = Read();

        Assert.Equal("SYN-LE-0001", set.Counterparties.Single(c => c.Code == "C0001").LegalEntity);
        Assert.Null(set.Counterparties.Single(c => c.Code == "C0009").LegalEntity);
    }

    [Fact]
    public void A_kind_without_a_declared_unit_reads_with_no_unit()
    {
        var set = Read();

        Assert.Equal(SensitivityPer.Rate, set.UnitOf("zero_rate"));
        Assert.Null(set.UnitOf("fx_notional_delta"));
    }

    [Fact]
    public void A_unit_the_adapter_does_not_know_is_refused()
    {
        var refused = Refused(manifest: Manifest.Replace("\"rate\"", "\"per-bp\""));

        Assert.Equal(DeskResultsRefusal.UnitUnknown, refused.Code);
        Assert.Contains("zero_rate", refused.Message);
    }

    [Theory]
    [InlineData("manifest.json")]
    [InlineData("xva.csv")]
    [InlineData("risk.csv")]
    [InlineData("trades.csv")]
    public void A_missing_file_is_refused_by_name(string file)
    {
        var refused = Refused(
            manifest: file == "manifest.json" ? null : Manifest, xva: file == "xva.csv" ? null : Xva,
            risk: file == "risk.csv" ? null : Risk, trades: file == "trades.csv" ? null : Trades);

        Assert.Equal(DeskResultsRefusal.FileMissing, refused.Code);
        Assert.Contains(file, refused.Message);
    }

    [Fact]
    public void A_missing_column_is_refused_by_name()
    {
        var refused = Refused(xva: "scenario,counterparty,metric,value\nS0000,C0001,CVA,-1\n");

        Assert.Equal(DeskResultsRefusal.ColumnMissing, refused.Code);
        Assert.Contains("std_error", refused.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("n/a")]
    [InlineData("NaN")]
    public void A_number_that_does_not_parse_is_refused_and_no_row_is_skipped(string cell)
    {
        var refused = Refused(risk: Risk + $"S0000,C0001,CVA,zero_rate,JPY-OIS,7d,{cell}\n");

        Assert.Equal(DeskResultsRefusal.NumberUnreadable, refused.Code);
        Assert.Contains("risk.csv line 4", refused.Message);
        Assert.Contains("sensitivity", refused.Message);
    }

    [Fact]
    public void A_row_with_the_wrong_number_of_cells_is_refused()
    {
        var refused = Refused(xva: Xva + "S0000,C0001,FVA\n");

        Assert.Equal(DeskResultsRefusal.RowUnreadable, refused.Code);
        Assert.Contains("xva.csv line 4", refused.Message);
    }

    [Theory]
    [InlineData("engineCommit")]
    [InlineData("inputSet")]
    [InlineData("valuationDate")]
    public void A_manifest_without_an_identifying_field_is_refused(string field)
    {
        var refused = Refused(manifest: Manifest.Replace($"\"{field}\"", $"\"x-{field}\""));

        Assert.Equal(DeskResultsRefusal.ManifestIncomplete, refused.Code);
        Assert.Contains(field, refused.Message);
    }

    [Fact]
    public void A_reference_that_names_no_result_set_is_refused()
    {
        Directory.CreateDirectory(_root);
        var files = new DeskResultFiles(_root);

        Assert.Equal(DeskResultsRefusal.NotFound, Assert.Throws<DeskResultsRefused>(() => files.Read("nothing")).Code);
        Assert.Equal(DeskResultsRefusal.NotFound, Assert.Throws<DeskResultsRefused>(() => files.Read("../set")).Code);
    }

    [Fact]
    public void The_content_hash_moves_with_any_file()
    {
        var before = Read().ContentHash;
        var after = Read(xva: Xva.Replace("-100.5", "-100.6")).ContentHash;

        Assert.NotEqual(before, after);
    }
}
