using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Workbench.Composition.Application;
using Workbench.Composition.Application.Workspaces;
using Workbench.Composition.Contracts;
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
/// XVA's registration into the pricer (ADR-0010 §11): CVA, with its CVA Charge, and FVA, which prices only trades on
/// imperfectly collateralised CSAs (§5). MVA and KVA follow when the engine's options for them are built.
/// </summary>
/// <param name="fvaLimitGbp">FVA's MTA + threshold limit in GBP, configuration (§5): 1mm unless set.</param>
public sealed class XvaMeasures(decimal fvaLimitGbp = 1_000_000m) : IMeasureModule
{
    public const string Module = "xva";
    public const string Cva = "cva";
    public const string Fva = "fva";

    public string Id => Module;

    public IReadOnlyList<MeasureDefinition> Measures { get; } =
    [
        new(Cva, "CVA", [new("cva", "CVA"), new("cvaCharge", "CVA Charge")], "cva", DeclaresMembership: false, Traced: false),
        new(Fva, "FVA", [new("fva", "FVA")], "fva", DeclaresMembership: true, Traced: false),
    ];

    public IReadOnlyList<IMembershipRule> Membership(WorkspaceState state)
    {
        var source = (PortfolioSourceVersion)state.Source;
        return [new ImperfectCsa(PortfolioMaterialiser.Csas(state), source.Agreement.NoCsa, source.ValueDate, fvaLimitGbp)];
    }

    /// <summary>XVA's own inputs: the counterparty's credit. An empty CVA recovery stays empty (docs/03, XV15).</summary>
    public JsonObject Input(WorkspaceState state) => new()
    {
        ["credit"] = JsonSerializer.SerializeToNode(PortfolioMaterialiser.Credit(state), WireJson.Options),
    };

    public IReadOnlyList<MeasureRefusal> Validate(WorkspaceState state, IReadOnlyList<string> selected) => [];

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
            ? new(true, $"{c.CsaId}: {string.Join(", ", why)}")
            : new(false, $"{c.CsaId}: {c.Frequency}, MTA+threshold {Mm(Math.Max(counterparty, self))}mm GBP");
    }

    static string Mm(decimal gbp) => (gbp / 1_000_000m).ToString("0.0#", CultureInfo.InvariantCulture);
}

/// <summary>
/// The fake analytics provider. Its CVA and FVA are a deterministic toy formula over notional, time to maturity and
/// recovery, chosen only so that edits visibly move the figures. They are not a model of anything and every result
/// says so (executionMode simulated). Before and After are computed independently; a side with no trade in it is not
/// priced, never priced as zero (ADR-0010 §3). It prices only the selected XVA measures, and a refused one is reported
/// with its reason and not computed.
/// </summary>
public sealed class FakeXvaPrice : ICapabilityHandler
{
    public const string CapabilityId = "xva.price";
    public const string CapabilityVersion = "2.0.0";
    /// <summary>The simulated charge ratio CVA Charge applies to CVA (ADR-0010 OPEN: what the charge approximates).</summary>
    public const decimal ChargeRatio = 1.25m;

    public CapabilityDescriptor Descriptor { get; } = new(CapabilityId, CapabilityVersion, "Price CVA and FVA (simulated)",
        [new("acceptedInput", PortfolioContracts.AcceptedInput, PortfolioContracts.Version, Cardinality.One)],
        [new("result", PortfolioContracts.MeasureResult, PortfolioContracts.Version, Cardinality.One)],
        ExecutionKind.RequestResponse, [CapabilityEffect.Compute]);

    public Task<IReadOnlyDictionary<string, JsonNode?>> InvokeAsync(CapabilityInvocation invocation, CancellationToken cancellationToken)
    {
        var reference = invocation.Inputs["acceptedInput"].Deserialize<TypedDataRef>(WireJson.Options)
            ?? throw new CapabilityFailure("InvalidInput", "The accepted input is required.");
        var input = invocation.Data.ReadRequired(reference);
        var valueDate = DateOnly.ParseExact(input["context"]!["valueDate"]!.GetValue<string>(), "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var credit = input["modules"]![XvaMeasures.Module]!["credit"]!;
        // An empty CVA recovery is not zero: the CDS recovery applies (docs/03).
        var recovery = credit["cvaRecovery"]?.GetValue<decimal>() ?? credit["cdsRecovery"]!.GetValue<decimal>();
        var trades = input["trades"]!.AsArray().Select(t => t!).ToList();
        var definitions = new XvaMeasures().Measures.ToDictionary(m => m.Id);
        const string currency = "JPY";

        // FVA prices only its members (ADR-0010 §5), as the accepted input froze them; a trade without its membership is
        // an input this provider cannot read, never a trade it guesses about.
        bool InFva(JsonNode t) => (t["membership"]?[XvaMeasures.Fva]?["in"] ?? throw new CapabilityFailure("InvalidInput",
            $"Trade {t["tradeId"]} carries no FVA membership.")).GetValue<bool>();

        (decimal Cva, decimal Fva, int Count, int FvaCount) Side(string side)
        {
            var members = trades.Where(t => t[side]!.GetValue<bool>()).ToList();
            decimal cva = 0, fva = 0;
            var fvaCount = 0;
            foreach (var t in members)
            {
                var years = (decimal)(DateOnly.ParseExact(t["maturity"]!.GetValue<string>(), "yyyy-MM-dd", CultureInfo.InvariantCulture).DayNumber - valueDate.DayNumber) / 365m;
                var exposure = Math.Abs(t["notional"]!.GetValue<decimal>()) * Math.Max(years, 0) * (t["currency"]!.GetValue<string>() == "JPY" ? 0.0001m : 0.015m);
                var uncollateralised = t["csa"]!.GetValue<string>().StartsWith("nocsa_", StringComparison.Ordinal);
                var curveFactor = t["curve"]!.GetValue<string>() == "usd_sofr" ? 1.1m : 1m;
                cva -= exposure * (1 - recovery) * (uncollateralised ? 0.02m : 0.004m) * curveFactor;
                if (!InFva(t)) continue;
                fva -= exposure * 0.003m * curveFactor;
                fvaCount++;
            }
            return (Math.Round(cva, 2), Math.Round(fva, 2), members.Count, fvaCount);
        }

        var before = Side("before");
        var after = Side("after");
        // A side with no trade is not priced; a side whose trades are all outside FVA is priced, and its FVA is 0 (§5).
        MeasureResult.Side Figure((decimal Cva, decimal Fva, int Count, int FvaCount) side, Func<(decimal Cva, decimal Fva, int Count, int FvaCount), decimal> value, bool fva) =>
            side.Count == 0 ? MeasureResult.Side.NotPricedWith(0) : MeasureResult.Side.Of(value(side), fva ? side.FvaCount : side.Count);

        var rows = new List<JsonObject>();
        foreach (var selected in input["measures"]!.AsArray().Select(m => m!.AsObject()).Where(m => m["module"]!.GetValue<string>() == XvaMeasures.Module))
        {
            var definition = definitions[selected["id"]!.GetValue<string>()];
            foreach (var metric in definition.Metrics)
            {
                if (selected["refused"] is JsonObject refused)
                {
                    var side = new MeasureResult.Side(MeasureResult.Refused, null, null, refused["message"]!.GetValue<string>());
                    rows.Add(MeasureResult.Row("base", "Workspace", definition.Id, metric, currency, side, side));
                    continue;
                }
                Func<(decimal Cva, decimal Fva, int Count, int FvaCount), decimal> value = metric.Id switch
                {
                    "cva" => s => s.Cva,
                    "cvaCharge" => s => Math.Round(s.Cva * ChargeRatio, 2),
                    "fva" => s => s.Fva,
                    _ => throw new InvalidOperationException($"XVA has no metric {metric.Id}."),
                };
                var fva = definition.Id == XvaMeasures.Fva;
                rows.Add(MeasureResult.Row("base", "Workspace", definition.Id, metric, currency, Figure(before, value, fva), Figure(after, value, fva)));
            }
        }
        return Task.FromResult<IReadOnlyDictionary<string, JsonNode?>>(new Dictionary<string, JsonNode?>
        {
            ["result"] = Datasets.Create("CVA and FVA (simulated)", MeasureResult.Columns, rows,
                new(ExecutionMode.Simulated, DataOrigin.Synthetic, invocation.InvocationId)),
        });
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
