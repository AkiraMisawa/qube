using System.Text.Json;
using System.Text.Json.Nodes;
using Workbench.Composition.Application;
using Workbench.Composition.Application.Traces;
using Workbench.Composition.Application.Workspaces;
using Workbench.Composition.Contracts;
using Workbench.Samples.Portfolio;
using Workbench.Samples.Rates;
using Workbench.Samples.Simm;
using Workbench.Samples.Xva;

namespace Workbench.Composition.Tests;

/// <summary>
/// ADR-0012 §2, §3, §4 and §7 with their notes of 2026-11-08: spot IM on the pricer page. The portfolio holds the IM
/// CSAs and each trade's IM-R and IM-P; SIMM registers the measure, its scopes, its inputs and its validation, and its
/// own request prices each side. Names and figures are synthetic.
/// </summary>
public class SimmPricerTests
{
    static readonly DateOnly Day1 = new(2026, 10, 26);

    // The confirmed conventions and a synthetic market of the test's own: made up, as every figure here is.
    static ConventionProfile Profile(string ccy, string index) =>
        new($"test.{ccy}.{index}", "1", ccy, index, DayCountBasis.Act360, "weekends-only", BusinessDayAdjustment.ModifiedFollowing, 2, 2, StubRule.ShortFront, false, true);

    static readonly ConventionRegistry Conventions = new([Profile("JPY", "TONA"), Profile("USD", "SOFR")], [new WeekendsOnlyCalendar()]);

    static readonly SyntheticMarket Market = new(new("simm-page-1", 7, new DateTimeOffset(2026, 10, 26, 0, 0, 0, TimeSpan.Zero), TimeSpan.FromSeconds(1),
        [new("JPY", "TONA", [(Tenor.Parse("1y"), 0.4), (Tenor.Parse("5y"), 0.9), (Tenor.Parse("10y"), 1.3)]),
         new("USD", "SOFR", [(Tenor.Parse("1y"), 4.1), (Tenor.Parse("5y"), 3.8), (Tenor.Parse("10y"), 3.9)])],
        new Dictionary<string, double> { ["USD"] = 1, ["JPY"] = 150 }, 0.5, 0.2, 0.0005));

    static (PortfolioWorkspaces Service, RunService Runs, IDataStore Store) Priced(IRatesPricingProvider? provider = null, IReadOnlyList<SimmParameters>? loaded = null,
        ConventionRegistry? requestConventions = null)
    {
        var workspaces = new WorkspaceService(new Dictionary<string, IEditRules> { [PortfolioEditRules.App] = new PortfolioEditRules() }, TimeProvider.System);
        var store = new InMemoryDataStore();
        // The request may be given another registry than Price read, as one moved between them would be.
        var simm = new SimmModule(new SyntheticImSource(), provider ?? new FakeRatesPricing(), Market.Resolve, requestConventions ?? Conventions, [SimmParameters.Demo]);
        var runs = new RunService(new CompositionRegistry([new PortfolioModule(), new XvaModule(), simm]), store, TimeProvider.System);
        var register = new MeasureRegister([new XvaMeasures(Pv01Provider.Simulated(TimeSpan.Zero)), new SimmMeasures(Conventions, _ => Market.Snapshot(0), loaded ?? [SimmParameters.Demo])]);
        return (new PortfolioWorkspaces(workspaces, store, runs, register), runs, store);
    }

    sealed record Priced_(RunRecord Run, JsonObject Input, IReadOnlyList<JsonNode> Rows, TraceSet Traces, CrifDocument? Before, CrifDocument? After);

    static async Task<Priced_> Price(RunService runs, IDataStore store, PortfolioWorkspaces service, WorkspaceState ws)
    {
        var accepted = Assert.IsType<SubmissionResult.Accepted>(service.Price(ws.WorkspaceId, ws.Revision, "akira", Guid.NewGuid().ToString()));
        await runs.Completion(accepted.Run.RunId);
        var run = runs.Find(accepted.Run.RunId)!;
        Assert.Equal(RunStatus.Succeeded, run.Status);
        var input = store.ReadRequired(((DataReferenceBinding)run.Workflow.Steps[0].Bindings["acceptedInput"]).Reference).AsObject();
        var outputs = run.Invocations.Single(i => i.StepId == SimmMeasures.Module).Outputs;
        CrifDocument? Crif(string name) => outputs.TryGetValue(name, out var crif) ? SimmDatasets.ReadCrif(store.ReadRequired(crif)) : null;
        return new(run, input, [.. store.ReadRequired(run.Outputs[SimmMeasures.Module])["rows"]!.AsArray().Select(r => r!)],
            store.ReadRequired(outputs["traces"]).Deserialize<TraceSet>(WireJson.Options)!, Crif("crifBefore"), Crif("crifAfter"));
    }

    static JsonNode Row(IReadOnlyList<JsonNode> rows, string metric) =>
        rows.Single(r => r["measure"]!.GetValue<string>() == SimmMeasures.SpotIm && r["metric"]!.GetValue<string>() == metric);

    static void Reconciles(CalculationTrace trace)
    {
        var evaluator = new TraceEvaluator(new TraceIndex(trace), SimmTraceCatalog.For(SimmParameters.Demo));
        Assert.All(trace.Terms, t => Assert.True(evaluator.Check(t).Status is CheckStatus.Matches or CheckStatus.NotApplicable, $"{t.Id}: {evaluator.Check(t).Reason}"));
        Assert.All(trace.Leaves, row => Assert.Empty(evaluator.CheckRow(row)));
    }

    static IReadOnlyDictionary<string, EffectiveTrade> Rows(PortfolioWorkspaces service, WorkspaceState ws) =>
        service.Page(ws.WorkspaceId, "akira", 0, 100, [], null).Rows.ToDictionary(t => t.TradeId);

    [Fact] // ADR-0012 §2: an existing trade's IM-R and IM-P are the source's; a new trade takes the counterparty's IM CSAs, as it takes the main CSA
    public void A_trades_im_csas_come_from_the_source_or_for_a_new_trade_from_the_counterparty()
    {
        var (service, _, _) = Priced();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 20);

        var agreement = service.View(ws.WorkspaceId, "akira").Agreement;
        Assert.Equal([("IMR-ABC-01", "collect"), ("IMP-ABC-01", "post")], agreement.ImCsas.Select(c => (c.ImCsaId, c.Direction)));
        var rows = Rows(service, ws);
        Assert.All(rows.Values.Where(t => !t.IsNew), t =>
            Assert.Equal(("IMR-ABC-01", Ownership.Source, "IMP-ABC-01", Ownership.Source), (t.ImR, t.ImROwner, t.ImP, t.ImPOwner)));
        Assert.All(rows.Values.Where(t => t.IsNew), t =>
            Assert.Equal(("IMR-ABC-01", Ownership.Automatic, "IMP-ABC-01", Ownership.Automatic), (t.ImR, t.ImROwner, t.ImP, t.ImPOwner)));

        // A counterparty without IM CSAs has none to give: its trades hold none, which is not an IM CSA of its own.
        var none = service.Create("akira", "XYZ/LE-03", Day1, 20);
        Assert.Empty(service.View(none.WorkspaceId, "akira").Agreement.ImCsas);
        Assert.All(Rows(service, none).Values, t => Assert.Equal((null, null), (t.ImR, t.ImP)));
    }

    [Fact] // ADR-0012 §2, ADR-0010 §2: IM-R and IM-P are edited as a CSA is, marked against Original and kept with the counterparty
    public void An_im_csa_is_an_assignment_edit_marked_against_original()
    {
        var (service, _, _) = Priced();
        var ws = service.Create("akira", "ABC/LE-01", Day1, 20);
        var existing = Rows(service, ws).Values.First(t => !t.IsNew).TradeId;

        // Emptied, a trade holds no IM CSA of that direction.
        ws = service.Edit(ws.WorkspaceId, ws.Revision, "akira", $"trade:{existing}", "imR", null);
        ws = service.Edit(ws.WorkspaceId, ws.Revision, "akira", "trade:DRAFT-1", "imP", "IMP-ABC-01");

        var rows = Rows(service, ws);
        Assert.Equal((null, Ownership.User), (rows[existing].ImR, rows[existing].ImROwner));
        Assert.Equal("IMR-ABC-01", rows[existing].Original!["imR"]!.GetValue<string>());
        // Set to the value it already had, the new trade's IM-P is the user's and is not marked.
        Assert.Equal(("IMP-ABC-01", Ownership.User), (rows["DRAFT-1"].ImP, rows["DRAFT-1"].ImPOwner));
        Assert.Null(rows["DRAFT-1"].Original);
        // An assignment belongs to the counterparty, a new trade's too, as a CSA does: a counterparty change drops both.
        Assert.Equal(2, service.PreviewCounterpartyChange(ws.WorkspaceId, "akira").ByKind["xva.assignment"]);

        // An IM CSA is an identifier, or empty.
        Assert.Equal("InvalidValue", Assert.Throws<WorkspaceRefused>(() => service.Edit(ws.WorkspaceId, ws.Revision, "akira", "trade:DRAFT-1", "imR", 5)).Code);
    }

    static WorkspaceState Select(PortfolioWorkspaces service, WorkspaceState ws, params string[] measures) =>
        service.SetMeasures(ws.WorkspaceId, ws.Revision, "akira", measures);

    static IReadOnlyDictionary<string, MembershipScope> Scopes(EffectiveTrade trade) =>
        trade.Membership![SimmMeasures.SpotIm].Scopes!.ToDictionary(s => s.Id);

    [Fact] // ADR-0012 §2, ADR-0010 §10: spot IM's scopes are per direction and regime, each in or out with why, shown compactly
    public void Spot_im_names_each_trades_scopes_per_direction_and_regime()
    {
        var (service, _, _) = Priced();
        var ws = Select(service, service.Create("akira", "ABC/LE-01", Day1, 60), "simm");
        var existing = Rows(service, ws).Values.Where(t => !t.IsNew).ToList();
        string Started(EffectiveTrade t) => t.Start!;

        // Traded after every regime it counts under had come in: in each of them.
        var recent = existing.First(t => t.Product == "IRS" && string.CompareOrdinal(Started(t), "2025-09-01") >= 0);
        Assert.Equal((true, "R: JFSA, CFTC · P: JFSA, EMIR"), (recent.Membership![SimmMeasures.SpotIm].In, recent.Membership[SimmMeasures.SpotIm].Reason));
        Assert.Equal(["collect/JFSA", "collect/CFTC", "post/JFSA", "post/EMIR"], recent.Membership[SimmMeasures.SpotIm].Scopes!.Select(s => s.Id));
        Assert.All(Scopes(recent).Values, s => Assert.True(s.In));

        // Traded before CFTC and EMIR came in: out of those two alone, saying when they did.
        var early = existing.First(t => t.Product == "IRS" && string.CompareOrdinal(Started(t), "2024-09-01") < 0);
        Assert.Equal((true, "R: JFSA · P: JFSA"), (early.Membership![SimmMeasures.SpotIm].In, early.Membership[SimmMeasures.SpotIm].Reason));
        var cftc = Scopes(early)["collect/CFTC"];
        Assert.False(cftc.In);
        Assert.Contains("2025-09-01", cftc.Reason);
        Assert.Contains(Started(early), cftc.Reason);
        Assert.False(Scopes(early)["post/EMIR"].In);

        // A physically settled FX forward is exempt in every scope.
        var forward = existing.First(t => t.Product == "FXForward");
        Assert.False(forward.Membership![SimmMeasures.SpotIm].In);
        Assert.All(Scopes(forward).Values, s => Assert.Equal((false, forward.Membership[SimmMeasures.SpotIm].Reason), (s.In, s.Reason)));
        Assert.Contains("exempt", forward.Membership[SimmMeasures.SpotIm].Reason);

        // A new trade starts on the value date, after every regime came in.
        Assert.Equal("R: JFSA, CFTC · P: JFSA, EMIR", Rows(service, ws)["DRAFT-1"].Membership![SimmMeasures.SpotIm].Reason);
    }

    [Fact] // ADR-0012 §2: a trade with no IM CSA in a direction is out of its scopes, and a counterparty with none has no scope at all
    public void A_trade_without_an_im_csa_is_out_of_that_directions_scopes()
    {
        var (service, _, _) = Priced();
        var ws = Select(service, service.Create("akira", "ABC/LE-01", Day1, 60), "simm");
        var recent = Rows(service, ws).Values.First(t => !t.IsNew && t.Product == "IRS" && string.CompareOrdinal(t.Start!, "2025-09-01") >= 0).TradeId;
        ws = service.Edit(ws.WorkspaceId, ws.Revision, "akira", $"trade:{recent}", "imR", null);

        var trade = Rows(service, ws)[recent];
        Assert.Equal((true, "R: none · P: JFSA, EMIR"), (trade.Membership![SimmMeasures.SpotIm].In, trade.Membership[SimmMeasures.SpotIm].Reason));
        Assert.All(trade.Membership[SimmMeasures.SpotIm].Scopes!.Where(s => s.Id.StartsWith("collect/", StringComparison.Ordinal)), s =>
            Assert.False(s.In));
        Assert.Contains("IM-R", Scopes(trade)["collect/JFSA"].Reason);

        var none = Select(service, service.Create("akira", "XYZ/LE-03", Day1, 20), "simm");
        Assert.All(Rows(service, none).Values, t =>
        {
            var membership = t.Membership![SimmMeasures.SpotIm];
            Assert.Equal((false, 0), (membership.In, membership.Scopes!.Count));
            Assert.Contains("XYZ/LE-03 has no IM CSA", membership.Reason);
        });
    }

    [Fact] // ADR-0012 §3, ADR-0010 §10: IM-R, IM-P and the IM tab are spot IM's inputs, shown only while it is selected
    public void Spot_ims_inputs_show_only_while_it_is_selected()
    {
        var (service, _, _) = Priced();
        var ws = Select(service, service.Create("akira", "ABC/LE-01", Day1, 20), "cva", "simm");

        Assert.Equal([("trades", "imR"), ("trades", "imP"), ("im", "imCsas")],
            service.View(ws.WorkspaceId, "akira").Inputs.Where(i => i.Measure == SimmMeasures.SpotIm).Select(i => (i.Tab, i.Field)));
        var measure = service.View(ws.WorkspaceId, "akira").Measures.Single(m => m.Id == SimmMeasures.SpotIm);
        Assert.Equal([("imR", "collect"), ("imP", "post")], measure.Metrics.Select(m => (m.Id, m.Term)));

        ws = Select(service, ws, "cva");
        Assert.DoesNotContain(service.View(ws.WorkspaceId, "akira").Inputs, i => i.Measure == SimmMeasures.SpotIm);
    }

    [Fact] // ADR-0012 §4, §7: SIMM's own request prices each side from that side's CRIF, under its governing regime, in the display currency
    public async Task Spot_im_is_priced_on_each_side_from_its_own_crif_under_the_governing_regime()
    {
        var (service, runs, store) = Priced();
        var ws = Select(service, service.Create("akira", "ABC/LE-01", Day1, 30), "simm");
        var trades = Rows(service, ws).Values.ToList();

        var priced = await Price(runs, store, service, ws);

        // `measures simm` is spot IM, priced by SIMM's request alone, which reads SIMM's part of the accepted input.
        Assert.Equal(SimmMeasures.SpotIm, priced.Run.Input["measures"]!.GetValue<string>());
        Assert.Equal([("simm", "simm.spotIm")], priced.Run.Workflow.Steps.Select(s => (s.StepId, s.CapabilityId)));
        Assert.Equal(["simm"], priced.Input["modules"]!.AsObject().Select(p => p.Key));
        var fx = priced.Input["modules"]!["simm"]!["market"]!["fx"]!;
        // The display currency is JPY, converted from SIMM's USD at the portfolio's FX of the value date (ADR-0012 §7).
        var usdToJpy = fx["JPY"]!.GetValue<double>() / fx["USD"]!.GetValue<double>();
        Assert.Equal((double)(PortfolioMarket.UnitsPerGbp("JPY", Day1) / PortfolioMarket.UnitsPerGbp("USD", Day1)), usdToJpy, 9);

        Assert.Equal(["before", "after"], priced.Traces.Traces.Select(t => t.Side));
        foreach (var (metric, direction, regimes) in new[] { ("imR", "collect", new[] { "JFSA", "CFTC" }), ("imP", "post", new[] { "JFSA", "EMIR" }) })
        {
            var row = Row(priced.Rows, metric);
            Assert.Equal(("priced", "priced", "JPY", "SIMM PARAMETERS DEMO"),
                (row["beforeStatus"]!.GetValue<string>(), row["afterStatus"]!.GetValue<string>(), row["currency"]!.GetValue<string>(), row["basis"]!.GetValue<string>()));
            Assert.Equal(row["after"]!.GetValue<decimal>() - row["before"]!.GetValue<decimal>(), row["diff"]!.GetValue<decimal>());
            foreach (var entry in priced.Traces.Traces)
            {
                Assert.Equal(("base", "base", SimmMeasures.SpotIm), (entry.Case, entry.Scenario, entry.Measure));
                // The figure is its direction's IM in the trace, the governing regime's SIMM, converted; and the note names it.
                var im = entry.Trace.Terms.Single(t => t.Id == direction);
                var governing = entry.Trace.Terms.Where(t => t.Parent == direction).ElementAt(im.Governed!.GetValue<int>());
                Assert.Equal(im.Value!.GetValue<double>(), governing.Value!.GetValue<double>());
                Assert.Equal(Math.Round((decimal)(im.Value.GetValue<double>() * usdToJpy), 2), row[entry.Side]!.GetValue<decimal>());
                Assert.Equal(governing.Key!["regime"], row[$"{entry.Side}Note"]!.GetValue<string>());
                Assert.Contains(governing.Key["regime"], regimes);
                // Its count is the side's trades in a scope of that direction.
                Assert.Equal(trades.Count(t => (entry.Side == "before" ? t.Before : t.After)
                    && t.Membership![SimmMeasures.SpotIm].Scopes!.Any(s => s.In && s.Id.StartsWith($"{direction}/", StringComparison.Ordinal))),
                    row[entry.Side == "before" ? "tradesBefore" : "tradesAfter"]!.GetValue<int>());
            }
        }
        foreach (var entry in priced.Traces.Traces)
        {
            Reconciles(entry.Trace);
            Assert.Equal((ExecutionMode.Simulated, "ABC/LE-01", "2026-10-26"), (entry.Trace.Provenance!.ExecutionMode, entry.Trace.Subject!.Id, entry.Trace.ValuationDate));
        }

        // Each side's CRIF is retained: of that side's trades in scope alone, and none of the exempt FX forwards.
        foreach (var (crif, side) in new[] { (priced.Before!, "before"), (priced.After!, "after") })
        {
            Assert.Equal(("ABC/LE-01", Day1), (crif.Registration, crif.ValueDate));
            var inScope = trades.Where(t => (side == "before" ? t.Before : t.After) && t.Membership![SimmMeasures.SpotIm].In).Select(t => t.TradeId).ToHashSet();
            Assert.Equal(inScope, crif.Lines.Select(l => l.TradeId).ToHashSet());
        }
        Assert.Contains(priced.After!.Lines, l => l.TradeId == "DRAFT-1" || l.TradeId == "DRAFT-2" || l.TradeId == "DRAFT-3");
        Assert.DoesNotContain(priced.Before!.Lines, l => l.TradeId.StartsWith("DRAFT-", StringComparison.Ordinal));
    }

    [Fact] // ADR-0012 §1, §6 (owner 2026-11-07): priced without Before, spot IM is After alone, and its trace is After's alone
    public async Task A_price_without_before_has_spot_im_on_after_alone()
    {
        var (service, runs, store) = Priced();
        var ws = Select(service, service.Create("akira", "ABC/LE-01", Day1, 30), "simm");
        ws = service.Edit(ws.WorkspaceId, ws.Revision, "akira", "grid:existing", "before", false);

        var priced = await Price(runs, store, service, ws);

        foreach (var metric in new[] { "imR", "imP" })
        {
            var row = Row(priced.Rows, metric);
            Assert.Equal(("notPriced", "priced"), (row["beforeStatus"]!.GetValue<string>(), row["afterStatus"]!.GetValue<string>()));
            Assert.Null(row["before"]);
            Assert.Null(row["diff"]);
            Assert.Null(row["beforeNote"]);
        }
        var entry = Assert.Single(priced.Traces.Traces);
        Assert.Equal("after", entry.Side);
        Reconciles(entry.Trace);
        Assert.Empty(priced.Before!.Lines);
    }

    [Fact] // ADR-0012 §1: a counterparty with no IM CSA has no IM in either direction, which is not an IM of 0
    public async Task A_counterparty_without_im_csas_has_no_im_rather_than_zero()
    {
        var (service, runs, store) = Priced();
        var ws = Select(service, service.Create("akira", "XYZ/LE-03", Day1, 20), "simm");

        var priced = await Price(runs, store, service, ws);

        foreach (var metric in new[] { "imR", "imP" })
        {
            var row = Row(priced.Rows, metric);
            Assert.Equal(("notPriced", "notPriced"), (row["beforeStatus"]!.GetValue<string>(), row["afterStatus"]!.GetValue<string>()));
            Assert.Null(row["after"]);
            Assert.Contains("XYZ/LE-03 has no IM CSA", row["reason"]!.GetValue<string>());
        }
        Assert.Empty(priced.Traces.Traces);
    }

    [Fact] // ADR-0012 §2's note, ADR-0010 §10: a trade naming an IM CSA that is not the counterparty's refuses spot IM alone, by name; CVA stands
    public async Task A_trade_naming_another_im_csa_refuses_spot_im_alone()
    {
        var (service, runs, store) = Priced();
        var ws = Select(service, service.Create("akira", "ABC/LE-01", Day1, 30), "cva", "simm");
        var trade = Rows(service, ws).Values.First(t => !t.IsNew && t.Product == "IRS").TradeId;
        ws = service.Edit(ws.WorkspaceId, ws.Revision, "akira", $"trade:{trade}", "imR", "IMP-ABC-01");

        // The row says why the trade is out of IM-R, before Price is asked.
        Assert.Contains("not ABC/LE-01's IM-R CSA", Scopes(Rows(service, ws)[trade])["collect/JFSA"].Reason);
        var priced = await Price(runs, store, service, ws);

        var refusal = priced.Input["measures"]!.AsArray().Single(m => m!["id"]!.GetValue<string>() == SimmMeasures.SpotIm)!["refused"]!;
        Assert.Equal("SpotImRefused", refusal["code"]!.GetValue<string>());
        Assert.Contains(trade, refusal["message"]!.GetValue<string>());
        Assert.Contains("IMP-ABC-01", refusal["message"]!.GetValue<string>());
        foreach (var metric in new[] { "imR", "imP" })
            Assert.Equal(("refused", "refused"), (Row(priced.Rows, metric)["beforeStatus"]!.GetValue<string>(), Row(priced.Rows, metric)["afterStatus"]!.GetValue<string>()));
        Assert.Empty(priced.Traces.Traces);
        var cva = store.ReadRequired(priced.Run.Outputs[XvaMeasures.Module])["rows"]!.AsArray().Single(r => r!["metric"]!.GetValue<string>() == "cva")!;
        Assert.Equal(("priced", "priced"), (cva["beforeStatus"]!.GetValue<string>(), cva["afterStatus"]!.GetValue<string>()));
    }

    [Fact] // ADR-0012 §4's note: a trade in scope that cannot be a Rates line, and IM CSAs naming a table not loaded, refuse spot IM by name
    public async Task Spot_im_is_refused_by_name_where_it_cannot_be_computed()
    {
        var (service, runs, store) = Priced();
        var ws = Select(service, service.Create("akira", "ABC/LE-01", Day1, 30), "simm");
        var trade = Rows(service, ws).Values.First(t => !t.IsNew && t.Product == "IRS" && t.Membership![SimmMeasures.SpotIm].In).TradeId;
        ws = service.Edit(ws.WorkspaceId, ws.Revision, "akira", $"trade:{trade}", "fixedRate", null);

        var refusal = (await Price(runs, store, service, ws)).Input["measures"]!.AsArray().Single()!["refused"]!;
        Assert.Equal("SpotImRefused", refusal["code"]!.GetValue<string>());
        Assert.Contains($"{trade} has no fixed rate", refusal["message"]!.GetValue<string>());

        var (unloaded, unloadedRuns, unloadedStore) = Priced(loaded: []);
        var other = Select(unloaded, unloaded.Create("akira", "ABC/LE-01", Day1, 30), "simm");
        var none = (await Price(unloadedRuns, unloadedStore, unloaded, other)).Input["measures"]!.AsArray().Single()!["refused"]!;
        Assert.Contains("No SIMM parameter table DEMO-2.x is loaded", none["message"]!.GetValue<string>());
    }

    /// <summary>The fake Rates pricer, except that one trade's rows fail, as an engine's would.</summary>
    sealed class FailsOne(string tradeId) : IRatesPricingProvider
    {
        readonly FakeRatesPricing inner = new();
        public string Name => "fails-one";
        public IReadOnlyList<PricedProduct> Products => inner.Products;

        public async Task<RatesBatchResult> PriceBatchAsync(RatesBatchInput input, CancellationToken cancellationToken)
        {
            var batch = await inner.PriceBatchAsync(input, cancellationToken);
            return batch with { Provider = Name, Rows = [.. batch.Rows.Select(r => r.RowKey == tradeId ? r with { Measures = new Dictionary<string, Measure>(), Failure = "The engine did not converge." } : r)] };
        }
    }

    [Fact] // ADR-0012 §4's note: a trade in scope that gives no CRIF makes its side unavailable, naming it, and the other side is priced
    public async Task A_trade_in_scope_without_crif_makes_its_side_unavailable()
    {
        var (probe, _, _) = Priced();
        var probing = Select(probe, probe.Create("akira", "ABC/LE-01", Day1, 30), "simm");
        // An existing trade in scope that is in Before alone, so After is priced without it.
        var trade = Rows(probe, probing).Values.First(t => !t.IsNew && t.Product == "IRS" && t.Membership![SimmMeasures.SpotIm].In).TradeId;

        var (service, runs, store) = Priced(new FailsOne(trade));
        var ws = Select(service, service.Create("akira", "ABC/LE-01", Day1, 30), "simm");
        ws = service.Edit(ws.WorkspaceId, ws.Revision, "akira", $"trade:{trade}", "after", false);
        var priced = await Price(runs, store, service, ws);

        var imR = Row(priced.Rows, "imR");
        Assert.Equal("unavailable", imR["beforeStatus"]!.GetValue<string>());
        Assert.Contains(trade, imR["reason"]!.GetValue<string>());
        Assert.Contains("The engine did not converge.", imR["reason"]!.GetValue<string>());
        Assert.Equal("priced", imR["afterStatus"]!.GetValue<string>());
        // The unavailable side keeps no trace and no CRIF line of its own; After's are whole.
        Assert.Equal(["after"], priced.Traces.Traces.Select(t => t.Side));
        Assert.Empty(priced.Before!.Lines);
        Assert.NotEmpty(priced.After!.Lines);
        Assert.DoesNotContain(priced.After.Lines, l => l.TradeId == trade);
    }

    [Fact] // ADR-0012 §4's note: a direction, then a whole side, with nothing in scope has no IM, says why, and keeps no trace; never 0
    public async Task A_direction_or_side_with_nothing_in_scope_is_not_priced_and_says_why()
    {
        var (service, runs, store) = Priced();
        var ws = Select(service, service.Create("akira", "ABC/LE-01", Day1, 30), "simm");
        var existing = Rows(service, ws).Values.Where(t => !t.IsNew).Select(t => t.TradeId).ToList();
        // Before holds the existing trades alone; with no IM-P CSA on any of them, Before has IM-R and no IM-P.
        foreach (var trade in existing) ws = service.Edit(ws.WorkspaceId, ws.Revision, "akira", $"trade:{trade}", "imP", null);
        Assume(Rows(service, ws).Values.Where(t => !t.IsNew).Any(t => Scopes(t).Values.Any(s => s.In && s.Id.StartsWith("collect/", StringComparison.Ordinal))));

        var oneDirection = await Price(runs, store, service, ws);
        Assert.Equal("priced", Row(oneDirection.Rows, "imR")["beforeStatus"]!.GetValue<string>());
        var imP = Row(oneDirection.Rows, "imP");
        Assert.Equal(("notPriced", "No trade in Before is in an IM-P scope."), (imP["beforeStatus"]!.GetValue<string>(), imP["reason"]!.GetValue<string>()));
        Assert.Null(imP["before"]);
        Assert.Equal("priced", imP["afterStatus"]!.GetValue<string>());

        // With no IM-R CSA either, nothing of Before is in a scope: no figure, no CRIF line and no trace on that side.
        foreach (var trade in existing) ws = service.Edit(ws.WorkspaceId, ws.Revision, "akira", $"trade:{trade}", "imR", null);
        var noSide = await Price(runs, store, service, ws);
        foreach (var metric in new[] { "imR", "imP" })
        {
            var row = Row(noSide.Rows, metric);
            Assert.Equal(("notPriced", "priced"), (row["beforeStatus"]!.GetValue<string>(), row["afterStatus"]!.GetValue<string>()));
            Assert.Null(row["before"]);
        }
        Assert.Equal("No trade in Before is in an IM-R scope.", Row(noSide.Rows, "imR")["reason"]!.GetValue<string>());
        Assert.Equal(["after"], noSide.Traces.Traces.Select(t => t.Side));
        Assert.Empty(noSide.Before!.Lines);
    }

    static void Assume(bool holds) => Assert.True(holds, "The synthetic portfolio no longer has the trades this test needs.");

    [Fact] // ADR-0012 §4's note, ADR-0010 §10: conventions moved between Price and the request fail SIMM's request alone, by name, and CVA's figures stay
    public async Task Conventions_moved_after_price_fail_spot_im_alone_and_cva_stands()
    {
        var moved = new ConventionRegistry([Profile("JPY", "TONA") with { Version = "2" }, Profile("USD", "SOFR")], [new WeekendsOnlyCalendar()]);
        var (service, runs, store) = Priced(requestConventions: moved);
        var ws = Select(service, service.Create("akira", "ABC/LE-01", Day1, 10), "cva", "simm");

        var accepted = Assert.IsType<SubmissionResult.Accepted>(service.Price(ws.WorkspaceId, ws.Revision, "akira", Guid.NewGuid().ToString()));
        await runs.Completion(accepted.Run.RunId);
        var run = runs.Find(accepted.Run.RunId)!;

        Assert.Equal(RunStatus.Failed, run.Status);
        var failed = run.Invocations.Single(i => i.StepId == SimmMeasures.Module);
        Assert.Equal(("ConventionsMoved", true), (failed.Error!.Code, failed.Error.Message.Contains("test.JPY.TONA 1", StringComparison.Ordinal)));
        // The Run keeps what XVA's request computed: only spot IM is unavailable.
        Assert.False(run.Outputs.ContainsKey(SimmMeasures.Module));
        var cva = store.ReadRequired(run.Outputs[XvaMeasures.Module])["rows"]!.AsArray().Single(r => r!["metric"]!.GetValue<string>() == "cva")!;
        Assert.Equal("priced", cva["afterStatus"]!.GetValue<string>());
    }
}
