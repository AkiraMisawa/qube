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
    /// <summary>The capital profile the KVA option returns (ADR-0010 §11, ADR-0013 §1): a row per case, side and date.</summary>
    public const string CapitalProfile = "xva.capital-profile";
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

    public string Id => Module;

    /// <summary>Its step is the fake engine's (<see cref="FakeXvaPrice"/>), and its PV01 the simulated one's.</summary>
    public bool Simulated => true;

    public static IReadOnlyList<MeasureDefinition> Definitions { get; } =
    [
        // The request that prices CVA also values the portfolio: its PV is the value a case adds, which Copy quote quotes
        // beside the charge (ADR-0010 §7).
        new(Cva, "CVA", [new("cva", "CVA"), new("cvaCharge", "CVA Charge"), new("pv", "PV")], "cva", DeclaresMembership: false, Traced: false, Quoted: true),
        new(Fva, "FVA", [new("fva", "FVA")], "fva", DeclaresMembership: true, Traced: false),
        // The cost of holding spot CCR capital's profile (ADR-0010 §11, ADR-0013 §1); the profile has no trace.
        new(Kva, "KVA", [new("kva", "KVA")], "kva", DeclaresMembership: false, Traced: false),
    ];

    public IReadOnlyList<MeasureDefinition> Measures => Definitions;

    public IReadOnlyList<TradeColumn> TradeColumns { get; } =
        [new(Pv01, "PV01", "number", false), new(Margin, "Margin (bp)", "number", true), new(PvAdj, "PV Adj", "number", true)];

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
    };

    public IReadOnlyList<MeasureRefusal> Validate(WorkspaceState state, IReadOnlyList<string> selected) =>
        throw new InvalidOperationException("XVA validates the trades Price froze, with their cells.");

    /// <summary>
    /// With Margin entered and PV01 pending or failed, the adjustment XVA would price is unknown, so its measures are
    /// refused by name (ADR-0010 §6, DEFAULT). Each refusal names the first such trade and how many there are.
    /// </summary>
    public IReadOnlyList<MeasureRefusal> Validate(WorkspaceState state, IReadOnlyList<string> selected, IReadOnlyList<EffectiveTrade> priced)
    {
        var unknown = priced.SelectMany(t => t.Cells?.GetValueOrDefault(Field(PvAdj)) is { State: not TradeCell.Shown } cell ? [(t.TradeId, Cell: cell)] : Array.Empty<(string TradeId, TradeCell Cell)>()).ToList();
        if (unknown.Count > 0)
        {
            var (tradeId, first) = unknown[0];
            var why = $"PV Adj of {tradeId} is unknown: {first.Note}" + (unknown.Count > 1 ? $" ({unknown.Count - 1} more trade(s) too)" : "");
            return [.. selected.Select(id => new MeasureRefusal(id, "PvAdjUnknown", why))];
        }
        // KVA is the cost of spot CCR capital's profile, so what refuses spot capital refuses KVA (ADR-0013 §6).
        return selected.Contains(Kva) && CcrMeasures.Problem(state, priced) is { } problem
            ? [new MeasureRefusal(Kva, "SpotCapitalRefused", $"KVA reads spot CCR capital: {problem}")]
            : [];
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
    public const string CapabilityVersion = "2.1.0";
    /// <summary>The simulated charge ratio CVA Charge applies to CVA (ADR-0010 OPEN: what the charge approximates).</summary>
    public const decimal ChargeRatio = 1.25m;
    /// <summary>The simulated cost of capital KVA charges a year (ADR-0010 §11: a setting, OPEN).</summary>
    public const decimal CostOfCapital = 0.10m;

    public CapabilityDescriptor Descriptor { get; } = new(CapabilityId, CapabilityVersion, "Price CVA, FVA, KVA and spot CCR capital (simulated)",
        [new("acceptedInput", PortfolioContracts.AcceptedInput, PortfolioContracts.Version, Cardinality.One)],
        [new("result", PortfolioContracts.MeasureResult, PortfolioContracts.Version, Cardinality.One),
         new("traces", TraceSet.ContractId, TraceSet.ContractVersion, Cardinality.One),
         new("profile", XvaContracts.CapitalProfile, XvaContracts.Version, Cardinality.One)],
        ExecutionKind.RequestResponse, [CapabilityEffect.Compute]);

    /// <summary>One side's toy figures, in the result's currency, and how many trades it priced and FVA counted.</summary>
    readonly record struct SideFigures(decimal Cva, decimal Fva, decimal Pv, int Count, int FvaCount);

    static readonly IReadOnlyList<DatasetColumn> ProfileColumns =
    [
        new("case", DatasetColumnType.String, false),
        new("scenario", DatasetColumnType.String, false),
        new("side", DatasetColumnType.String, false),
        new("years", DatasetColumnType.Number, false),
        new("date", DatasetColumnType.String, false),
        new("trades", DatasetColumnType.Integer, false),
        new("ead", DatasetColumnType.Number, false),
        new("capital", DatasetColumnType.Number, false),
        new("currency", DatasetColumnType.String, false),
    ];

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
        foreach (var @case in PortfolioInput.Cases(invocation.Data.ReadRequired(reference)))
        {
            var priced = Price(@case, invocation.InvocationId, provenance);
            rows.AddRange(priced.Rows);
            traces.AddRange(priced.Traces);
            profile.AddRange(priced.Profile);
        }
        return Task.FromResult<IReadOnlyDictionary<string, JsonNode?>>(new Dictionary<string, JsonNode?>
        {
            ["result"] = Datasets.Create("XVA (simulated)", MeasureResult.Columns, rows, provenance),
            ["traces"] = JsonSerializer.SerializeToNode(new TraceSet(traces), WireJson.Options),
            ["profile"] = Datasets.Create("Capital profile (simulated: the spot rules on the trades still alive, today's values held)", ProfileColumns, profile, provenance),
        });
    }

    /// <summary>One case's rows, its spot capital traces and its capital profile, from the case as a one-case input.</summary>
    static (IReadOnlyList<JsonObject> Rows, IReadOnlyList<TraceEntry> Traces, IReadOnlyList<JsonObject> Profile) Price(PricedCase @case, string requestId,
        DatasetProvenance provenance)
    {
        var input = @case.Input;
        var valueDate = DateOnly.ParseExact(input["context"]!["valueDate"]!.GetValue<string>(), "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var credit = input["modules"]![XvaMeasures.Module]!["credit"]!;
        // An empty CVA recovery is not zero: the CDS recovery applies (docs/03).
        var recovery = credit["cvaRecovery"]?.GetValue<decimal>() ?? credit["cdsRecovery"]!.GetValue<decimal>();
        var trades = input["trades"]!.AsArray().Select(t => t!).ToList();
        var definitions = XvaMeasures.Definitions.ToDictionary(m => m.Id);
        // The toy figures are made in JPY and reported in the display currency Price froze (ADR-0010 §1, §7), at the
        // value date's synthetic FX.
        var currency = (input["displayCurrency"] ?? throw new CapabilityFailure("InvalidInput", "The accepted input names no display currency.")).GetValue<string>();
        // The market of the pair's scenario: the base market's where it shifts nothing.
        var market = @case.Market;
        decimal Shown(decimal jpy) => Math.Round(market.ToGbp(jpy, "JPY") * market.UnitsPerGbp(currency), 2);
        var fundingCurves = input["modules"]![XvaMeasures.Module]!["fundingCurves"] as JsonObject;

        // FVA prices only its members (ADR-0010 §5), as the accepted input froze them; a trade without its membership is
        // an input this provider cannot read, never a trade it guesses about.
        bool InFva(JsonNode t) => (t["membership"]?[XvaMeasures.Fva]?["in"] ?? throw new CapabilityFailure("InvalidInput",
            $"Trade {t["tradeId"]} carries no FVA membership.")).GetValue<bool>();

        // A new trade's PV Adj as Price froze it (ADR-0010 §6): none typed is no adjustment; one not known is an input
        // this provider cannot read, since Price refuses XVA's measures then.
        decimal Adjustment(JsonNode t)
        {
            if (t["cells"]?[XvaMeasures.Field(XvaMeasures.PvAdj)] is not { } cell) return 0m;
            if (cell["state"]!.GetValue<string>() != TradeCell.Shown)
                throw new CapabilityFailure("InvalidInput", $"PV Adj of {t["tradeId"]} is {cell["state"]}; Price refuses XVA's measures then.");
            // An empty PV Adj is one nobody typed: the trade is priced without an adjustment (docs/03 §2.5 OPEN).
            return cell["value"] is { } typed ? typed.GetValue<decimal>() : 0m;
        }

        // A trade's PV as the engine is given it (docs/03 §2.5), in JPY at the value date's FX: an
        // existing trade's official PV, a new trade's PV Adj. A new trade with neither is dealt at market: PV 0 (a toy).
        decimal Value(JsonNode t)
        {
            var own = t["isNew"]!.GetValue<bool>() ? Adjustment(t)
                : (t["officialPv"] ?? throw new CapabilityFailure("InvalidInput", $"Existing trade {t["tradeId"]} carries no official PV.")).GetValue<decimal>();
            return market.ToGbp(own, t["currency"]!.GetValue<string>()) * market.UnitsPerGbp("JPY") + Change(t);
        }

        // A trade's change of value in JPY under the scenario, by the portfolio's toy revaluation; none on the base market.
        decimal Change(JsonNode t) => market.PvChangeJpy(t["tradeId"]!.GetValue<string>(), t["product"]!.GetValue<string>(), t["currency"]!.GetValue<string>(),
            t["notional"]!.GetValue<decimal>(), DateOnly.ParseExact(t["maturity"]!.GetValue<string>(), "yyyy-MM-dd", CultureInfo.InvariantCulture),
            t["direction"]?.GetValue<string>(), t["curve"]!.GetValue<string>(), t["counterCurrency"]?.GetValue<string>(), t["counterNotional"]?.GetValue<decimal>());

        // The toy funding spread FVA charges at a trade's maturity: 30bp, moved by its CSA's funding curve under a scenario,
        // below zero too, so a shift past 30bp shows as the benefit it is in the toy, never floored to no FVA.
        decimal FundingSpread(JsonNode t, double years)
        {
            if (market.IsBase) return 0.003m;
            var csa = t["csa"]!.GetValue<string>();
            var curve = fundingCurves?[csa]?.GetValue<string>()
                ?? throw new CapabilityFailure("InvalidInput", $"Trade {t["tradeId"]} is in FVA on {csa}, and the accepted input names no funding curve of {csa}.");
            return 0.003m + market.RateShiftBp(curve, years) / 10_000m;
        }

        SideFigures Side(string side)
        {
            var members = trades.Where(t => t[side]!.GetValue<bool>()).ToList();
            decimal cva = 0, fva = 0, pv = 0;
            var fvaCount = 0;
            foreach (var t in members)
            {
                var days = DateOnly.ParseExact(t["maturity"]!.GetValue<string>(), "yyyy-MM-dd", CultureInfo.InvariantCulture).DayNumber - valueDate.DayNumber;
                var years = (decimal)days / 365m;
                var tradeCurrency = t["currency"]!.GetValue<string>();
                // A toy exposure in JPY; one in another currency moves with that currency against JPY under a scenario.
                var exposure = Math.Abs(t["notional"]!.GetValue<decimal>()) * Math.Max(years, 0) * (tradeCurrency == "JPY" ? 0.0001m : 0.015m * market.FxFactor(tradeCurrency));
                // The engine aligns the exposure to the trade's PV (docs/03 §2.5); here, a toy shift by its PV Adj and by its
                // change of value under the scenario.
                exposure = Math.Max(0, exposure + Adjustment(t) * 0.01m + Change(t) * 0.01m);
                var uncollateralised = t["csa"]!.GetValue<string>().StartsWith("nocsa_", StringComparison.Ordinal);
                var curveFactor = t["curve"]!.GetValue<string>() == "usd_sofr" ? 1.1m : 1m;
                cva -= exposure * (1 - recovery) * (uncollateralised ? 0.02m : 0.004m) * curveFactor;
                pv += Value(t);
                if (!InFva(t)) continue;
                fva -= exposure * FundingSpread(t, Math.Max(days, 0) / 365.0) * curveFactor;
                fvaCount++;
            }
            return new(Shown(cva), Shown(fva), Shown(pv), members.Count, fvaCount);
        }

        // Read only when a measure is priced: a measure refused at Price is reported, never computed. A trade the toy cannot
        // revalue under the scenario refuses XVA's measures for this pair, by name (ADR-0010 §4's note of 2026-11-09).
        string? unapplied = null;
        var sides = new Lazy<(SideFigures Before, SideFigures After)?>(() =>
        {
            try
            {
                return (Side("before"), Side("after"));
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
            return MeasureResult.Side.Of(Math.Round(-CostOfCapital * cost, 2), side.Trades);
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
                    "cva" => s => s.Cva,
                    "cvaCharge" => s => Math.Round(s.Cva * ChargeRatio, 2),
                    "pv" => s => s.Pv,
                    "fva" => s => s.Fva,
                    _ => throw new InvalidOperationException($"XVA has no metric {metric.Id}."),
                };
                var fva = definition.Id == XvaMeasures.Fva;
                rows.Add(MeasureResult.Row(@case, definition.Id, metric, currency, Figure(figures.Before, value, fva), Figure(figures.After, value, fva)));
            }
        }
        return (rows, capital?.Traces.Traces ?? [], [.. profiles.SelectMany(p => p.Value.Select(x => new JsonObject
        {
            ["case"] = @case.Id, ["scenario"] = @case.Scenario.Id, ["side"] = p.Key, ["years"] = x.Years,
            ["date"] = x.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ["trades"] = x.Trades,
            ["ead"] = Math.Round(x.Ead, 2), ["capital"] = Math.Round(x.Capital, 2), ["currency"] = capital!.Portfolio!.ReportingCurrency,
        }))]);
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
