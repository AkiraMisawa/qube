using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Workbench.Composition.Application;
using Workbench.Composition.Application.Workspaces;
using Workbench.Composition.Contracts;
using Workbench.Samples.Ccr;
using Workbench.Samples.Portfolio;

namespace Workbench.Samples.Xva;

public static class XvaContracts
{
    public const string Version = "1.0.0";
    /// <summary>The accepted input of a Run priced before the portfolio module (ADR-0010 §10); read, never written.</summary>
    public const string AcceptedInput = "xva.accepted-input";
    /// <summary>The result of a Run priced before the measures register; read by the Pricing DB, never written.</summary>
    public const string CaseResult = "xva.case-result";
    public const string ResultView = "view.xva.result";
}

/// <summary>
/// XVA's registration into the pricer (ADR-0010 §11): CVA, with its CVA Charge; FVA, which prices only trades on
/// imperfectly collateralised CSAs (§5); KVA, from the capital profile its KVA option returns with spot CCR capital
/// (ADR-0013 §2); and its columns on new trades, PV01, Margin and PV Adj (§6). MVA follows when the engine's option
/// for it is built.
/// </summary>
/// <param name="pv01">Where new trades' PV01 comes from.</param>
/// <param name="fvaLimitGbp">FVA's MTA + threshold limit in GBP, configuration (§5): 1mm unless set.</param>
public sealed class XvaMeasures(Pv01Provider pv01, decimal fvaLimitGbp = 1_000_000m) : IMeasureModule
{
    public const string Module = "xva";
    public const string Cva = "cva";
    public const string Fva = "fva";
    public const string Kva = "kva";
    public const string Pv01 = "pv01";
    public const string Margin = "margin";
    public const string PvAdj = "pvAdj";
    /// <summary>XVA's settings (ADR-0010 §8's note of 2026-11-09): what its request is computed with.</summary>
    public const string Mpor = "mpor";
    public const string CostOfCapital = "costOfCapital";
    public const string ChargeRatio = "chargeRatio";
    public const string PvAdjustment = "pvAdjustment";
    public const string Risks = "risks";
    /// <summary>The model's margin period of risk in days, which an empty MPOR override leaves (simulated).</summary>
    public const int ModelMpor = 10;

    public string Id => Module;

    /// <summary>Its step is the fake engine's (<see cref="FakeXvaPrice"/>), and its PV01 the simulated one's.</summary>
    public bool Simulated => true;

    /// <summary>The Risks tab CVA and FVA register (owner 2026-11-08): one tab for both, their deltas from the Run.</summary>
    public static readonly ResultTab RisksTab = new("risks", "Risks", ResultTab.Risks);

    /// <summary>The Profile tab CVA, FVA and KVA register (owner 2026-11-08): the exposure and capital profiles the Run returned.</summary>
    public static readonly ResultTab ProfileTab = new("profile", "Profile", ResultTab.Profile);

    public static IReadOnlyList<MeasureDefinition> Definitions { get; } =
    [
        // The request that prices CVA also values the portfolio: its PV is the value a case adds, which Copy quote quotes
        // beside the charge (ADR-0010 §7).
        new(Cva, "CVA", [new("cva", "CVA"), new("cvaCharge", "CVA Charge"), new("pv", "PV")], "cva", DeclaresMembership: false, Traced: false, Quoted: true,
            Tabs: [RisksTab, ProfileTab]),
        new(Fva, "FVA", [new("fva", "FVA")], "fva", DeclaresMembership: true, Traced: false, Tabs: [RisksTab, ProfileTab]),
        // The cost of holding spot CCR capital's profile (ADR-0010 §11, ADR-0013 §1); the profile has no trace.
        new(Kva, "KVA", [new("kva", "KVA")], "kva", DeclaresMembership: false, Traced: false, Tabs: [ProfileTab]),
    ];

    public IReadOnlyList<MeasureDefinition> Measures => Definitions;

    public IReadOnlyList<TradeColumn> TradeColumns { get; } =
        [new(Pv01, "PV01", "number", false), new(Margin, "Margin (bp)", "number", true), new(PvAdj, "PV Adj", "number", true)];

    /// <summary>
    /// XVA's settings (ADR-0010 §8's note of 2026-11-09, DEFAULT): the MPOR override the exposure of a collateralised
    /// trade is the move over, empty for the model's; the cost of capital KVA charges a year; the ratio CVA Charge applies
    /// to CVA; whether new trades' PV Adj is priced; and whether the request computes the deltas the Risks tab shows. The
    /// defaults are the simulated engine's, and what each approximates is OPEN with the engine.
    /// </summary>
    public IReadOnlyList<MeasureSetting> Settings { get; } =
    [
        new(Mpor, "MPOR override", CellName.Form.Days, null, [Cva, Fva], Empty: $"the model's {ModelMpor} days", Names: ["mpor"]),
        new(CostOfCapital, "Cost of capital", CellName.Form.Rate, 0.10m, [Kva], Names: ["coc"]),
        new(ChargeRatio, "Charge ratio", CellName.Form.Number, 1.25m, [Cva]),
        new(PvAdjustment, "PV adjustment", CellName.Form.Flag, true, [Cva, Fva, Kva], Names: ["pv adj"]),
        new(Risks, "Risks included", CellName.Form.Flag, false, [Cva, Fva], Names: ["risks"]),
    ];

    public string? CheckSetting(string setting, JsonNode? value) => setting switch
    {
        Mpor => value is null || value is JsonValue m && m.TryGetValue<decimal>(out var days) && days == decimal.Truncate(days) && days is >= 1 and <= 250
            ? null : $"An MPOR override is a whole number of days from 1 to 250, or empty for the model's {ModelMpor}.",
        CostOfCapital => value is JsonValue c && c.TryGetValue<decimal>(out var rate) && rate is >= 0 and < 1 ? null : "A cost of capital is from 0% up to 100%, never empty.",
        ChargeRatio => value is JsonValue r && r.TryGetValue<decimal>(out var ratio) && ratio is > 0 and <= 10 ? null : "A charge ratio is a number above 0 and up to 10.",
        PvAdjustment or Risks => value is JsonValue b && b.TryGetValue<bool>(out _) ? null : $"{Settings.Single(x => x.Id == setting).Title} is on or off.",
        _ => $"XVA has no setting {setting}.",
    };

    /// <summary>CVA's recovery on the Netting tab (ADR-0010 §8, §11): empty, the CDS recovery applies (XV15).</summary>
    public IReadOnlyList<MeasureInput> Inputs { get; } = [new("netting", "cvaRecovery", Cva)];

    /// <summary>A trade's field for one of XVA's columns, as the trade grids and the frozen input name it.</summary>
    public static string Field(string column) => $"{Module}.{column}";

    /// <summary>
    /// PV01, Margin and PV Adj on each new trade (ADR-0010 §6). PV01 is asked the first time the trade is read. Margin is
    /// typed. PV Adj is PV01 × Margin, each with its own sign, whenever Margin is entered, and read-only then, saying so;
    /// with Margin empty PV Adj is typed. An existing trade has none of them: its official PV is the source's.
    /// </summary>
    public IReadOnlyList<IReadOnlyDictionary<string, TradeCell>?> Cells(WorkspaceState state, IReadOnlyList<EffectiveTrade> trades)
    {
        var source = (PortfolioSourceVersion)state.Source;
        var typed = state.Overrides.Values.Where(o => o.Target.Field.StartsWith($"{Module}.", StringComparison.Ordinal))
            .ToDictionary(o => (o.Target.Entity, o.Target.Field), o => o.Value);
        decimal? Typed(EffectiveTrade t, string column) => typed.GetValueOrDefault((Entity.Of(Entity.Trade, t.TradeId), Field(column)))?.GetValue<decimal>();
        return [.. trades.Select(t =>
        {
            if (!t.IsNew) return null;
            var answer = pv01.Of(Pv01Request.Of(t, source.ValueDate));
            var margin = Typed(t, Margin);
            var pv01Cell = answer.State switch
            {
                TradeCell.Shown => new TradeCell(answer.Value, TradeCell.Shown, null, false),
                TradeCell.Pending => new TradeCell(null, TradeCell.Pending, "PV01 is asked of the engine.", false),
                _ => new TradeCell(null, TradeCell.Failed, $"PV01 failed: {answer.Reason}", false),
            };
            TradeCell adjustment = margin is not { } bp ? TradeCell.Input(Typed(t, PvAdj))
                : answer.Value is { } value ? new(Math.Round(value * bp, 2), TradeCell.Shown, $"From Margin: {Amount(bp)} bp × PV01 {Amount(value)}", false)
                : new(null, answer.State, answer.State == TradeCell.Pending ? "From Margin: PV01 is pending." : $"From Margin: PV01 failed: {answer.Reason}", false);
            return (IReadOnlyDictionary<string, TradeCell>)new Dictionary<string, TradeCell>
            {
                [Pv01] = pv01Cell,
                [Margin] = TradeCell.Input(margin),
                [PvAdj] = adjustment,
            };
        })];
    }

    public string? Check(string column, JsonNode? value) => column switch
    {
        Margin => value is null || value is JsonValue m && m.TryGetValue<decimal>(out _) ? null : "A margin is a number of basis points or empty.",
        PvAdj => value is null || value is JsonValue a && a.TryGetValue<decimal>(out _) ? null : "A PV Adj is an amount or empty.",
        _ => $"{column} is a result; it is not typed.",
    };

    public int Retry(WorkspaceState state, IReadOnlyList<EffectiveTrade> trades)
    {
        var source = (PortfolioSourceVersion)state.Source;
        return pv01.Retry(trades.Where(t => t.IsNew).Select(t => Pv01Request.Of(t, source.ValueDate)));
    }

    static string Amount(decimal value) => value.ToString("#,0.##", CultureInfo.InvariantCulture);

    public IReadOnlyList<IMembershipRule> Membership(WorkspaceState state)
    {
        var source = (PortfolioSourceVersion)state.Source;
        return [new ImperfectCsa(PortfolioMaterialiser.Csas(state), source.Agreement.NoCsa, source.ValueDate, fvaLimitGbp)];
    }

    /// <summary>XVA's own inputs: the counterparty's credit. An empty CVA recovery stays empty (docs/03, XV15).</summary>
    public JsonObject Input(WorkspaceState state) => new()
    {
        ["credit"] = JsonSerializer.SerializeToNode(PortfolioMaterialiser.Credit(state), WireJson.Options),
        // Each CSA's FVA discount curve, which a funding shift moves (ADR-0010 §4's note of 2026-11-09).
        ["fundingCurves"] = new JsonObject([.. PortfolioMaterialiser.Csas(state).Select(c => KeyValuePair.Create(c.CsaId, (JsonNode?)c.FvaCurve))]),
        // Its settings as the reading holds them, the defaults among them: the provider reads these, never its own (§8).
        ["settings"] = MeasureSettings.Values(state, this),
    };

    public IReadOnlyList<MeasureRefusal> Validate(WorkspaceState state, IReadOnlyList<string> selected) =>
        throw new InvalidOperationException("XVA validates the trades Price froze, with their cells.");

    /// <summary>
    /// With Margin entered and PV01 pending or failed, the adjustment XVA would price is unknown, so its measures are
    /// refused by name (ADR-0010 §6, DEFAULT). Each refusal names the first such trade and how many there are.
    /// </summary>
    public IReadOnlyList<MeasureRefusal> Validate(WorkspaceState state, IReadOnlyList<string> selected, IReadOnlyList<EffectiveTrade> priced)
    {
        // With PV adjustment off, new trades are priced with none, so a PV Adj not known refuses nothing (ADR-0010 §8).
        var adjusted = MeasureSettings.Value(state, Module, Settings.Single(x => x.Id == PvAdjustment))?.GetValue<bool>()
            ?? throw new InvalidOperationException("PV adjustment is on or off, never empty.");
        List<(string TradeId, TradeCell Cell)> unknown = !adjusted ? [] : priced.SelectMany(t => t.Cells?.GetValueOrDefault(Field(PvAdj)) is { State: not TradeCell.Shown } cell ? [(t.TradeId, Cell: cell)] : Array.Empty<(string TradeId, TradeCell Cell)>()).ToList();
        if (unknown.Count > 0)
        {
            var (tradeId, first) = unknown[0];
            var why = $"PV Adj of {tradeId} is unknown: {first.Note}" + (unknown.Count > 1 ? $" ({unknown.Count - 1} more trade(s) too)" : "");
            return [.. selected.Select(id => new MeasureRefusal(id, "PvAdjUnknown", why))];
        }
        var refusals = new List<MeasureRefusal>();
        // CVA reads the counterparty's CDS curve; without one it is refused, and CVA Charge with it (ADR-0010 §11).
        if (selected.Contains(Cva) && PortfolioMaterialiser.Credit(state) is { Cds: null } credit)
            refusals.Add(new MeasureRefusal(Cva, "NoCds", credit.Missing ?? throw new InvalidOperationException("A credit with no curve says why.")));
        // KVA is the cost of spot CCR capital's profile, so what refuses spot capital refuses KVA (ADR-0013 §6).
        if (selected.Contains(Kva) && CcrMeasures.Problem(state, priced) is { } problem)
            refusals.Add(new MeasureRefusal(Kva, "SpotCapitalRefused", $"KVA reads spot CCR capital: {problem}"));
        return refusals;
    }

    public WorkflowStep Step(TypedDataRef acceptedInput) => new(Module, FakeXvaPrice.CapabilityId, FakeXvaPrice.CapabilityVersion, [],
        new Dictionary<string, Binding> { ["acceptedInput"] = new DataReferenceBinding(acceptedInput) }, FailurePolicy.Stop);
}

/// <summary>
/// FVA's membership (ADR-0010 §5): a CSA is imperfectly collateralised when it is one-way, margins less often than daily,
/// or either party's MTA + threshold exceeds the limit in GBP at the value date's FX. FVA prices the trades on such
/// CSAs; an uncollateralised trade is out, and the engine gives it FVA 0.
/// </summary>
public sealed class ImperfectCsa : IMembershipRule
{
    readonly Dictionary<string, Membership> byCsa;
    readonly string noCsa;

    public ImperfectCsa(IReadOnlyList<EffectiveCsa> csas, string noCsa, DateOnly valueDate, decimal limitGbp)
    {
        this.noCsa = noCsa;
        Version = $"fva-imperfect-csa/1 limit {Mm(limitGbp)}mm GBP";
        byCsa = csas.ToDictionary(c => c.CsaId, c => Decide(c, valueDate, limitGbp));
    }

    public string Measure => XvaMeasures.Fva;
    public string Version { get; }

    public Membership Of(EffectiveTrade trade) =>
        trade.Csa == noCsa ? new(false, $"{noCsa}: uncollateralised, FVA 0")
        : byCsa.TryGetValue(trade.Csa, out var standing) ? standing
        // A trade the rule cannot classify is out with its reason, never dropped (ADR-0010 §10).
        : new(false, $"{trade.Csa} is not a CSA of the agreement, so FVA cannot classify it.");

    public Membership? Of(EffectiveCsa csa) => byCsa[csa.CsaId];

    static Membership Decide(EffectiveCsa c, DateOnly valueDate, decimal limitGbp)
    {
        var counterparty = PortfolioMarket.ToGbp(c.CounterpartyThreshold + c.CounterpartyMta, c.CounterpartyCurrency, valueDate);
        var self = PortfolioMarket.ToGbp(c.SelfThreshold + c.SelfMta, c.SelfCurrency, valueDate);
        var why = new List<string>();
        if (c.OneWay) why.Add("one-way");
        if (c.Frequency != "daily") why.Add(c.Frequency);
        if (counterparty > limitGbp) why.Add($"counterparty MTA+threshold {Mm(counterparty)}mm GBP over {Mm(limitGbp)}mm");
        if (self > limitGbp) why.Add($"self MTA+threshold {Mm(self)}mm GBP over {Mm(limitGbp)}mm");
        return why.Count > 0
            ? new(true, $"{c.CsaId} imperfect: {string.Join(", ", why)}")
            : new(false, $"{c.CsaId} perfect: {c.Frequency}, MTA+threshold {Mm(Math.Max(counterparty, self))}mm GBP");
    }

    static string Mm(decimal gbp) => (gbp / 1_000_000m).ToString("0.0#", CultureInfo.InvariantCulture);
}

/// <summary>
/// The fake analytics provider. Its CVA and FVA are a deterministic toy formula over notional, time to maturity and
/// recovery, and its PV the sum of the PVs it is given, chosen only so that edits visibly move the figures. They are not a model of anything and every result
/// says so (executionMode simulated). Before and After are computed independently; a side with no trade in it is not
/// priced, never priced as zero (ADR-0010 §3). It prices only the selected XVA measures, and a refused one is reported
/// with its reason and not computed. Under a scenario (ADR-0010 §4's note of 2026-11-09) each trade is revalued by the
/// portfolio's toy revaluation, its toy exposure moves with that value and its currency, FVA's toy funding spread moves
/// with its CSA's funding curve, and every amount converts at the scenario's FX; a trade the toy cannot revalue refuses
/// XVA's measures for that scenario, by name.
/// </summary>
public sealed class FakeXvaPrice : ICapabilityHandler
{
    public const string CapabilityId = "xva.price";
    public const string CapabilityVersion = "2.2.0";

    public CapabilityDescriptor Descriptor { get; } = new(CapabilityId, CapabilityVersion, "Price CVA, FVA, KVA and spot CCR capital (simulated)",
        [new("acceptedInput", PortfolioContracts.AcceptedInput, PortfolioContracts.Version, Cardinality.One)],
        [new("result", PortfolioContracts.MeasureResult, PortfolioContracts.Version, Cardinality.One),
         new("traces", TraceSet.ContractId, TraceSet.ContractVersion, Cardinality.One),
         new("risks", PortfolioContracts.Risks, PortfolioContracts.Version, Cardinality.One),
         new("profiles", PortfolioContracts.Profiles, PortfolioContracts.Version, Cardinality.One)],
        ExecutionKind.RequestResponse, [CapabilityEffect.Compute]);

    /// <summary>
    /// One side's toy figures, in the result's currency: CVA (none without a CDS curve, when CVA is refused), FVA and PV,
    /// how many trades it priced and FVA counted, and the exposure profile CVA integrates.
    /// </summary>
    readonly record struct SideFigures(decimal? Cva, decimal Fva, decimal Pv, int Count, int FvaCount, IReadOnlyList<ExposurePoint> Profile);

    /// <summary>A point of the toy exposure profile, in the result's currency: EE and ENE at a time in years.</summary>
    internal readonly record struct ExposurePoint(double Years, decimal Ee, decimal Ene);

    /// <summary>The toy's dates in years, cut at a side's last maturity, which is the last.</summary>
    static readonly double[] Grid = [0, 0.25, 0.5, 1, 2, 3, 5, 7, 10, 15, 20, 30];

    public Task<IReadOnlyDictionary<string, JsonNode?>> InvokeAsync(CapabilityInvocation invocation, CancellationToken cancellationToken)
    {
        var reference = invocation.Inputs["acceptedInput"].Deserialize<TypedDataRef>(WireJson.Options)
            ?? throw new CapabilityFailure("InvalidInput", "The accepted input is required.");
        var provenance = new DatasetProvenance(ExecutionMode.Simulated, DataOrigin.Synthetic, invocation.InvocationId);
        // One request prices every case (ADR-0010 §3's note of 2026-11-08): each row, trace entry and profile point
        // carries its case.
        var rows = new List<JsonObject>();
        var traces = new List<TraceEntry>();
        var profile = new List<JsonObject>();
        var risks = new List<JsonObject>();
        foreach (var @case in PortfolioInput.Cases(invocation.Data.ReadRequired(reference)))
        {
            var priced = Price(@case, invocation.InvocationId, provenance);
            rows.AddRange(priced.Rows);
            traces.AddRange(priced.Traces);
            profile.AddRange(priced.Profile);
            risks.AddRange(priced.Risks);
        }
        return Task.FromResult<IReadOnlyDictionary<string, JsonNode?>>(new Dictionary<string, JsonNode?>
        {
            ["result"] = Datasets.Create("XVA (simulated)", MeasureResult.Columns, rows, provenance),
            ["traces"] = JsonSerializer.SerializeToNode(new TraceSet(traces), WireJson.Options),
            ["risks"] = Datasets.Create("XVA deltas (simulated: each factor bumped and the toy repriced)", MeasureRisks.Columns, risks, provenance),
            ["profiles"] = Datasets.Create("XVA profiles (simulated: the toy's exposure through time, and the spot rules on the trades still alive, today's values held)",
                MeasureProfiles.Columns, profile, provenance),
        });
    }

    /// <summary>
    /// The factors a measure's deltas are taken on (the Risks tab, DEFAULT of 2026-11-09): CVA's credit at each CDS tenor
    /// (+1bp key-rate), IR on each discount curve (+1bp parallel) and FX on each currency against JPY (+1%); FVA's IR on
    /// each discount and funding curve and its FX. Each bump is a shift on top of the pair's market.
    /// </summary>
    static IReadOnlyList<Shift> Bumps(string measure) =>
    [
        .. measure == XvaMeasures.Cva ? CdsCatalog.Tenors.Select(t => new Shift(ScenarioCatalog.Credit, ScenarioCatalog.Cds, t, ScenarioCatalog.Absolute, 1m)) : [],
        .. ScenarioCatalog.Indices.Where(i => i.Category == ScenarioCatalog.Ir && (i.Kind == ScenarioCatalog.Discount || measure == XvaMeasures.Fva))
            .Select(i => new Shift(ScenarioCatalog.Ir, i.Id, null, ScenarioCatalog.Absolute, 1m)),
        .. ScenarioCatalog.Indices.Where(i => i.Category == ScenarioCatalog.Fx).Select(i => new Shift(ScenarioCatalog.Fx, i.Id, null, ScenarioCatalog.Relative, 1m)),
    ];

    /// <summary>One case's rows, its spot capital traces, its capital profile and its deltas, from the case as a one-case input.</summary>
    static (IReadOnlyList<JsonObject> Rows, IReadOnlyList<TraceEntry> Traces, IReadOnlyList<JsonObject> Profile, IReadOnlyList<JsonObject> Risks) Price(PricedCase @case,
        string requestId, DatasetProvenance provenance)
    {
        var input = @case.Input;
        var valueDate = DateOnly.ParseExact(input["context"]!["valueDate"]!.GetValue<string>(), "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var credit = input["modules"]![XvaMeasures.Module]!["credit"]!;
        // The CDS curve CVA reads (ADR-0010 §8's note of 2026-11-09); none where CVA is refused for its want.
        var cds = credit["cds"] as JsonObject;
        var trades = input["trades"]!.AsArray().Select(t => t!).ToList();
        var definitions = XvaMeasures.Definitions.ToDictionary(m => m.Id);
        // The toy figures are made in JPY and reported in the display currency Price froze (ADR-0010 §1, §7), at the
        // value date's synthetic FX.
        var currency = (input["displayCurrency"] ?? throw new CapabilityFailure("InvalidInput", "The accepted input names no display currency.")).GetValue<string>();
        // The market of the pair's scenario: the base market's where it shifts nothing.
        var market = @case.Market;
        decimal Shown(decimal jpy, ScenarioMarket m) => Math.Round(m.ToGbp(jpy, "JPY") * m.UnitsPerGbp(currency), 2);
        var fundingCurves = input["modules"]![XvaMeasures.Module]!["fundingCurves"] as JsonObject;
        // XVA's settings as Price froze them (ADR-0010 §8): read here, never defaulted again by the provider.
        var settings = input["modules"]![XvaMeasures.Module]!["settings"] as JsonObject
            ?? throw new CapabilityFailure("InvalidInput", "The accepted input holds no XVA settings.");
        JsonNode Setting(string id) => settings[id] ?? throw new CapabilityFailure("InvalidInput", $"The accepted input's XVA setting {id} is empty.");
        // The margin period in days a collateralised trade's exposure is the move over: the override, else the model's.
        var mpor = settings[XvaMeasures.Mpor]?.GetValue<double>() ?? XvaMeasures.ModelMpor;
        var chargeRatio = Setting(XvaMeasures.ChargeRatio).GetValue<decimal>();
        var costOfCapital = Setting(XvaMeasures.CostOfCapital).GetValue<decimal>();
        var adjusted = Setting(XvaMeasures.PvAdjustment).GetValue<bool>();
        var risksAsked = Setting(XvaMeasures.Risks).GetValue<bool>();

        // FVA prices only its members (ADR-0010 §5), as the accepted input froze them; a trade without its membership is
        // an input this provider cannot read, never a trade it guesses about.
        bool InFva(JsonNode t) => (t["membership"]?[XvaMeasures.Fva]?["in"] ?? throw new CapabilityFailure("InvalidInput",
            $"Trade {t["tradeId"]} carries no FVA membership.")).GetValue<bool>();

        // A new trade's PV Adj as Price froze it (ADR-0010 §6): none typed is no adjustment; one not known is an input
        // this provider cannot read, since Price refuses XVA's measures then.
        decimal Adjustment(JsonNode t)
        {
            // With PV adjustment off, new trades are priced with none, whatever their cells hold (ADR-0010 §8).
            if (!adjusted) return 0m;
            if (t["cells"]?[XvaMeasures.Field(XvaMeasures.PvAdj)] is not { } cell) return 0m;
            if (cell["state"]!.GetValue<string>() != TradeCell.Shown)
                throw new CapabilityFailure("InvalidInput", $"PV Adj of {t["tradeId"]} is {cell["state"]}; Price refuses XVA's measures then.");
            // An empty PV Adj is one nobody typed: the trade is priced without an adjustment (docs/03 §2.5 OPEN).
            return cell["value"] is { } typed ? typed.GetValue<decimal>() : 0m;
        }

        // A trade's PV as the engine is given it (docs/03 §2.5), in JPY at the value date's FX: an
        // existing trade's official PV, a new trade's PV Adj. A new trade with neither is dealt at market: PV 0 (a toy).
        decimal Value(JsonNode t, ScenarioMarket m)
        {
            var own = t["isNew"]!.GetValue<bool>() ? Adjustment(t)
                : (t["officialPv"] ?? throw new CapabilityFailure("InvalidInput", $"Existing trade {t["tradeId"]} carries no official PV.")).GetValue<decimal>();
            return m.ToGbp(own, t["currency"]!.GetValue<string>()) * m.UnitsPerGbp("JPY") + Change(t, m);
        }

        // A trade's change of value in JPY under the scenario, by the portfolio's toy revaluation; none on the base market.
        decimal Change(JsonNode t, ScenarioMarket m) => m.PvChangeJpy(t["tradeId"]!.GetValue<string>(), t["product"]!.GetValue<string>(), t["currency"]!.GetValue<string>(),
            t["notional"]!.GetValue<decimal>(), DateOnly.ParseExact(t["maturity"]!.GetValue<string>(), "yyyy-MM-dd", CultureInfo.InvariantCulture),
            t["direction"]?.GetValue<string>(), t["curve"]!.GetValue<string>(), t["counterCurrency"]?.GetValue<string>(), t["counterNotional"]?.GetValue<decimal>());

        // The toy funding spread FVA charges at a point: 30bp, moved by the CSA's funding curve under a scenario, below zero
        // too, so a shift past 30bp shows as the benefit it is in the toy, never floored to no FVA.
        decimal FundingSpread(JsonNode t, double years, ScenarioMarket m)
        {
            if (m.IsBase) return 0.003m;
            var csa = t["csa"]!.GetValue<string>();
            var curve = fundingCurves?[csa]?.GetValue<string>()
                ?? throw new CapabilityFailure("InvalidInput", $"Trade {t["tradeId"]} is in FVA on {csa}, and the accepted input names no funding curve of {csa}.");
            return 0.003m + m.RateShiftBp(curve, years) / 10_000m;
        }

        // Each CDS tenor's hazard: its spread, moved by the scenario's credit shifts at that tenor, over 1 − the curve's
        // recovery. A spread the shifts take to 0 or below has no default probability, so the scenario cannot be applied.
        double[] Hazards(JsonObject curve, ScenarioMarket m)
        {
            var cdsRecovery = curve["recovery"]!.GetValue<decimal>();
            var spreads = curve["spreads"]!.AsObject();
            return [.. CdsCatalog.Tenors.Select(tenor =>
            {
                var spread = (spreads[tenor] ?? throw new CapabilityFailure("InvalidInput", $"The CDS curve {curve["curveId"]} has no spread at {tenor}.")).GetValue<decimal>()
                    + m.CreditShiftBp(ScenarioCatalog.YearsOf(tenor)) / 10_000m;
                if (spread <= 0) throw new ScenarioUnapplied($"the CDS spread at {tenor} is {CdsCatalog.Bp(spread)} under the shifts, and a spread of 0 or below has no default probability.");
                return (double)(spread / (1 - cdsRecovery));
            })];
        }

        // Survival to a time: each bucket's hazard from the tenor before to its own, the last one's beyond.
        static double Survival(double years, double[] hazards)
        {
            double integral = 0, from = 0;
            for (var j = 0; j < hazards.Length && from < years; j++)
            {
                var to = j == hazards.Length - 1 ? years : Math.Min(years, ScenarioCatalog.YearsOf(CdsCatalog.Tenors[j]));
                integral += hazards[j] * Math.Max(0, to - from);
                from = to;
            }
            return Math.Exp(-integral);
        }

        // One side on one market (the scenario's, or it with a bump): each trade's toy exposure through time in JPY, its EE
        // the part above zero of its value held and running off, and a diffusion that peaks between today and its
        // maturity, its ENE the part below; a collateralised trade's is what moves over the margin period. CVA integrates
        // EE against the CDS curve's default probabilities; FVA its members' EE against the funding spread; PV is today's.
        SideFigures Side(string side, ScenarioMarket m)
        {
            var members = trades.Where(t => t[side]!.GetValue<bool>()).ToList();
            var lives = members.Select(t => Math.Max(0, (DateOnly.ParseExact(t["maturity"]!.GetValue<string>(), "yyyy-MM-dd", CultureInfo.InvariantCulture).DayNumber - valueDate.DayNumber) / 365.0)).ToList();
            var last = lives.Count == 0 ? 0 : lives.Max();
            var grid = Grid.Where(x => x < last).Append(last).Distinct().ToArray();
            var ee = new decimal[grid.Length];
            var ene = new decimal[grid.Length];
            decimal fva = 0, pv = 0;
            var fvaCount = 0;
            for (var i = 0; i < members.Count; i++)
            {
                var t = members[i];
                var life = lives[i];
                var value = Value(t, m);
                pv += value;
                var collateralised = !t["csa"]!.GetValue<string>().StartsWith("nocsa_", StringComparison.Ordinal);
                var kappa = collateralised ? 0.2m * (decimal)Math.Sqrt(mpor / XvaMeasures.ModelMpor) : 1m;
                var curveFactor = t["curve"]!.GetValue<string>() == "usd_sofr" ? 1.1m : 1m;
                var scale = Math.Abs(t["notional"]!.GetValue<decimal>()) * m.JpyPer(t["currency"]!.GetValue<string>()) * 0.004m * curveFactor;
                var own = new decimal[grid.Length];
                var ownNegative = new decimal[grid.Length];
                for (var k = 0; k < grid.Length; k++)
                {
                    if (life <= 0 || grid[k] >= life) continue;
                    var left = (decimal)(1 - grid[k] / life);
                    var diffusion = scale * (decimal)Math.Sqrt(grid[k]) * left;
                    own[k] = kappa * (Math.Max(value, 0) * left + diffusion);
                    ownNegative[k] = kappa * (Math.Min(value, 0) * left - diffusion);
                    ee[k] += own[k];
                    ene[k] += ownNegative[k];
                }
                if (!InFva(t)) continue;
                fvaCount++;
                for (var k = 1; k < grid.Length; k++)
                    fva -= (own[k - 1] + own[k]) / 2 * FundingSpread(t, grid[k], m) * (decimal)(grid[k] - grid[k - 1]);
            }
            decimal? cva = null;
            if (cds is not null)
            {
                // An empty CVA recovery is not zero: the CDS recovery applies (docs/03, XV15).
                var recovery = credit["cvaRecovery"]?.GetValue<decimal>() ?? cds["recovery"]!.GetValue<decimal>();
                var hazards = Hazards(cds, m);
                decimal loss = 0;
                for (var k = 1; k < grid.Length; k++)
                    loss += (ee[k - 1] + ee[k]) / 2 * (decimal)(Survival(grid[k - 1], hazards) - Survival(grid[k], hazards));
                cva = Shown(-(1 - recovery) * loss, m);
            }
            return new(cva, Shown(fva, m), Shown(pv, m), members.Count, fvaCount,
                [.. grid.Select((x, k) => new ExposurePoint(x, Shown(ee[k], m), Shown(ene[k], m)))]);
        }

        // CVA is priced only where it was not refused, so a curve is there: one missing is an input this provider cannot read.
        decimal CvaOf(SideFigures side) => side.Cva ?? throw new CapabilityFailure("InvalidInput", "CVA is asked for, and the accepted input holds no CDS curve.");

        // Read only when a measure is priced: a measure refused at Price is reported, never computed. A trade the toy cannot
        // revalue under the scenario refuses XVA's measures for this pair, by name (ADR-0010 §4's note of 2026-11-09).
        string? unapplied = null;
        var sides = new Lazy<(SideFigures Before, SideFigures After)?>(() =>
        {
            try
            {
                return (Side("before", market), Side("after", market));
            }
            catch (ScenarioUnapplied e)
            {
                unapplied = $"Under {@case.Scenario.Title}: {e.Message}";
                return null;
            }
        });
        // A side with no trade is not priced; a side whose trades are all outside FVA is priced, and its FVA is 0 (§5).
        MeasureResult.Side Figure(SideFigures side, Func<SideFigures, decimal> value, bool fva) =>
            side.Count == 0 ? MeasureResult.Side.NotPricedWith(0) : MeasureResult.Side.Of(value(side), fva ? side.FvaCount : side.Count);

        var measures = input["measures"]!.AsArray().Select(m => m!.AsObject()).ToList();
        // The KVA option (ADR-0013 §2): on when KVA or spot capital is selected, since it computes spot capital, its
        // trace and the capital profile; the page shows each only where it is selected.
        var kvaOption = measures.Any(m => m["id"]!.GetValue<string>() is XvaMeasures.Kva or CcrMeasures.SpotCapital);
        var capital = kvaOption ? SpotCapitalResults.Answer(@case, requestId, provenance) : null;
        var profiles = new Dictionary<string, IReadOnlyList<CapitalPoint>>();
        if (capital?.Portfolio is { } portfolio)
            foreach (var side in new[] { capital.Before, capital.After })
                if (side.Capital is { } spot) profiles[side.Side] = SpotCapitalPricing.Profile(portfolio, side.Side, spot);

        // KVA = −CoC · Σ K(tᵢ) · (tᵢ₊₁ − tᵢ), the last period running to the last maturity: undiscounted, and simulated.
        MeasureResult.Side Kva(SideCapital side)
        {
            if (side.Trades == 0) return MeasureResult.Side.NotPricedWith(0);
            if (!profiles.TryGetValue(side.Side, out var points))
                return new MeasureResult.Side(MeasureResult.Unavailable, null, side.Trades, $"KVA reads spot CCR capital: {side.Refused}");
            var last = capital!.Portfolio!.Trades.Where(t => side.Side == SpotCapitalPricing.Before ? t.Before : t.After).Max(t => t.Maturity);
            var end = Decompose.Years(capital.Portfolio.ValuationDate, last);
            var cost = points.Select((x, i) => (decimal)x.Capital * (decimal)((i + 1 < points.Count ? points[i + 1].Years : end) - x.Years)).Sum();
            return MeasureResult.Side.Of(Math.Round(-costOfCapital * cost, 2), side.Trades);
        }

        var rows = new List<JsonObject>(capital?.Rows ?? []);
        foreach (var selected in measures.Where(m => m["module"]!.GetValue<string>() == XvaMeasures.Module))
        {
            var definition = definitions[selected["id"]!.GetValue<string>()];
            foreach (var metric in definition.Metrics)
            {
                if (selected["refused"] is JsonObject refused)
                {
                    var side = new MeasureResult.Side(MeasureResult.Refused, null, null, refused["message"]!.GetValue<string>());
                    rows.Add(MeasureResult.Row(@case, definition.Id, metric, currency, side, side));
                    continue;
                }
                if (definition.Id == XvaMeasures.Kva)
                {
                    // KVA is the cost of spot capital's profile, so a scenario spot capital cannot apply refuses it too.
                    if (capital!.Unapplied is { } why)
                    {
                        var side = new MeasureResult.Side(MeasureResult.Refused, null, null, $"KVA reads spot CCR capital: {why}");
                        rows.Add(MeasureResult.Row(@case, definition.Id, metric, currency, side, side));
                        continue;
                    }
                    rows.Add(MeasureResult.Row(@case, definition.Id, metric, currency, Kva(capital.Before), Kva(capital.After)));
                    continue;
                }
                if (sides.Value is not { } figures)
                {
                    var side = new MeasureResult.Side(MeasureResult.Refused, null, null, unapplied);
                    rows.Add(MeasureResult.Row(@case, definition.Id, metric, currency, side, side));
                    continue;
                }
                Func<SideFigures, decimal> value = metric.Id switch
                {
                    "cva" => s => CvaOf(s),
                    "cvaCharge" => s => Math.Round(CvaOf(s) * chargeRatio, 2),
                    "pv" => s => s.Pv,
                    "fva" => s => s.Fva,
                    _ => throw new InvalidOperationException($"XVA has no metric {metric.Id}."),
                };
                var fva = definition.Id == XvaMeasures.Fva;
                rows.Add(MeasureResult.Row(@case, definition.Id, metric, currency, Figure(figures.Before, value, fva), Figure(figures.After, value, fva)));
            }
        }
        // The deltas (the Risks tab): with risks asked, each factor bumped on top of the pair's market and both sides priced
        // again, the delta the bumped figure minus the priced one; a measure with none says why.
        var risks = new List<JsonObject>();
        var bumped = new Dictionary<string, (SideFigures Before, SideFigures After)?>();
        string? bumpUnapplied = null;
        (SideFigures Before, SideFigures After)? On(Shift bump)
        {
            if (bumped.TryGetValue(bump.Text, out var known)) return known;
            var moved = new ScenarioMarket([.. market.Shifts, bump], market.ValueDate);
            try
            {
                return bumped[bump.Text] = (Side("before", moved), Side("after", moved));
            }
            catch (ScenarioUnapplied e)
            {
                bumpUnapplied = e.Message;
                return bumped[bump.Text] = null;
            }
        }
        foreach (var selected in measures.Where(m => m["id"]!.GetValue<string>() is XvaMeasures.Cva or XvaMeasures.Fva))
        {
            var definition = definitions[selected["id"]!.GetValue<string>()];
            var headline = definition.Metrics.Single(m => m.Id == definition.Headline);
            if (selected["refused"] is JsonObject refused)
            {
                risks.Add(MeasureRisks.None(@case, definition.Id, headline, currency, $"{definition.Title} is refused, so it has no deltas: {refused["message"]!.GetValue<string>()}"));
                continue;
            }
            if (!risksAsked)
            {
                risks.Add(MeasureRisks.None(@case, definition.Id, headline, currency,
                    "Risks were not asked: turn Risks included on in Settings, or set xva risks on, and Price again."));
                continue;
            }
            if (sides.Value is not { } figures)
            {
                risks.Add(MeasureRisks.None(@case, definition.Id, headline, currency, unapplied ?? throw new InvalidOperationException("A pair not priced says why.")));
                continue;
            }
            Func<SideFigures, decimal> value = definition.Id == XvaMeasures.Cva ? CvaOf : s => s.Fva;
            MeasureResult.Side Delta(SideFigures priced, SideFigures moved) =>
                priced.Count == 0 ? MeasureResult.Side.NotPricedWith(0) : MeasureResult.Side.Of(value(moved) - value(priced), priced.Count);
            foreach (var bump in Bumps(definition.Id))
            {
                var factor = bump.Tenor is null ? bump.Index : $"{bump.Index} {bump.Tenor}";
                var size = Shift.Signed(bump.Size, bump.Unit);
                if (On(bump) is not { } moved)
                {
                    var side = new MeasureResult.Side(MeasureResult.Refused, null, null, $"Under {bump.Text}: {bumpUnapplied}");
                    risks.Add(MeasureRisks.Row(@case, definition.Id, headline, currency, bump.Category, factor, size, side, side));
                    continue;
                }
                risks.Add(MeasureRisks.Row(@case, definition.Id, headline, currency, bump.Category, factor, size,
                    Delta(figures.Before, moved.Before), Delta(figures.After, moved.After)));
            }
        }
        // The profiles (the Profile tab): the exposure CVA and FVA integrate, where either was priced on this pair, and the
        // capital profile KVA charges for, with the KVA option.
        var profileRows = new List<JsonObject>();
        var exposed = measures.Any(m => m["id"]!.GetValue<string>() is XvaMeasures.Cva or XvaMeasures.Fva && m["refused"] is not JsonObject);
        if (exposed && sides.Value is { } priced)
            foreach (var (side, figures) in new[] { ("before", priced.Before), ("after", priced.After) })
                foreach (var (series, title, value) in new (string, string, Func<ExposurePoint, decimal>)[] { ("ee", "EE", x => x.Ee), ("ene", "ENE", x => x.Ene) })
                    profileRows.AddRange(figures.Count == 0 ? [] : figures.Profile.Select(x => MeasureProfiles.Row(@case, side, MeasureProfiles.Exposure, "Exposure", series, title, x.Years,
                        valueDate.AddDays((int)Math.Round(x.Years * 365)), value(x), currency)));
        foreach (var (side, points) in profiles)
            foreach (var (series, title, value) in new (string, string, Func<CapitalPoint, double>)[] { ("ead", "EAD", x => x.Ead), ("capital", "Capital", x => x.Capital) })
                profileRows.AddRange(points.Select(x => MeasureProfiles.Row(@case, side, MeasureProfiles.Capital, "Capital", series, title, x.Years, x.Date,
                    Math.Round((decimal)value(x), 2), capital!.Portfolio!.ReportingCurrency, x.Trades)));
        return (rows, capital?.Traces.Traces ?? [], profileRows, risks);
    }
}

/// <summary>Registers the XVA sample: its price capability, its view of the Pricing DB and the result view.</summary>
public sealed class XvaModule : IWorkbenchModule
{
    public void Register(CompositionRegistryBuilder builder)
    {
        builder.AddCapability(new FakeXvaPrice());
        builder.AddHistoryProjection(new XvaHistoryProjection());
        builder.AddView(new ViewDefinition("view", CompositionValidator.SchemaVersion, XvaContracts.ResultView, "2.0.0", "CVA and FVA",
            new ViewLayout(1, 1),
            [new ViewComponent("result-grid", ComponentKind.Grid, "CVA and FVA (simulated)", new WorkflowOutputData(XvaMeasures.Module),
                [new("caseTitle", "Case", ColumnFormat.Text), new("metricTitle", "Measure", ColumnFormat.Text), new("before", "Before", ColumnFormat.Number),
                 new("after", "After", ColumnFormat.Number), new("diff", "Diff", ColumnFormat.Number), new("afterStatus", "Status", ColumnFormat.Status)], null)]));
    }
}
