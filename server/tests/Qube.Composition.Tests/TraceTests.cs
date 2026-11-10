using System.Text.Json;
using System.Text.Json.Nodes;
using Qube.Composition.Application;
using Qube.Composition.Application.Traces;
using Qube.Composition.Contracts;

namespace Qube.Composition.Tests;

/// <summary>
/// The calculation trace of ADR-0011 over a method that is not a regulation: a garden's water budget. It uses every
/// operation of the closed set, so the kernel is shown to evaluate, reconcile and pair traces without knowing a rule.
/// </summary>
public partial class TraceTests
{
    static readonly TraceMethod Method = new("garden-water", "2026-01");
    static readonly TraceUnit Litres = new("volume");

    static TermKind Kind(string kind, string tex, TraceExpr? expression, TermValueType type = TermValueType.Number) =>
        new(kind, kind.Replace("garden.", "", StringComparison.Ordinal) + " {bed}", kind, Litres, "Garden rules §1", tex, type, expression);

    static readonly SlotExpr Area = new("area"), Depth = new("depth"), Best = new("best"), Other = new("other");

    static TraceCatalog Catalog() => new(Method,
    [
        Kind("garden.total", @"W = \sum_b W_b", new SumExpr([new ChildrenExpr("garden.bed")])),
        Kind("garden.bed", @"W_b = \slot{area} \cdot \slot{depth} \cdot \param{density}", new ProductExpr([Area, Depth, new ParamExpr("density")])),
        Kind("garden.area", @"A = \sum_i a_i", new SumLeavesExpr("area")),
        Kind("garden.depth", "d", null),
        Kind("garden.best", @"\max(\slot{best}, \slot{other})", new MaxExpr([Best, Other])),
        Kind("garden.least", @"\min(\slot{best}, \slot{other})", new MinExpr([Best, Other])),
        Kind("garden.net", @"\slot{best} - \slot{other}", new DifferenceExpr([Best, Other])),
        Kind("garden.ratio", @"\slot{best} / \slot{other}", new QuotientExpr([Best, Other])),
        Kind("garden.bounded", @"\mathrm{clamp}(\slot{best}, 0, \param{cap})", new ClampExpr(Best, new ConstExpr(0), new ParamExpr("cap"))),
        Kind("garden.shape", @"\sqrt{|\ln \exp(-\slot{best})|}", new SqrtExpr(new AbsExpr(new LnExpr(new ExpExpr(new NegateExpr(Best)))))),
        Kind("garden.chance", @"\Phi(\param{z})", new NormalCdfExpr(new ParamExpr("z"))),
        Kind("garden.spread", @"\sqrt{\sum_k x_k^2 + \sum_{k \ne l} c_{kl} x_k x_l}", new QuadraticFormExpr(new ChildrenExpr("garden.part"), ["corr"], "part")),
        Kind("garden.pool", @"\sqrt{(\sum_k \rho_k x_k)^2 + \sum_k (1 - \rho_k^2) x_k^2}", new SingleFactorExpr(new ChildrenExpr("garden.part"), "rho")),
        Kind("garden.part", "x_k", null),
        Kind("garden.soil", "s", null, TermValueType.Text),
        Kind("garden.rate", @"\mathrm{lookup}(\text{rates}, \slot{soil})", new LookupExpr("rates", new SlotExpr("soil"))),
        Kind("garden.tally", @"\sum_k x_k \text{ of } \slot{parts}", new SumExpr([new ChildrenExpr("garden.part", "parts")])),
    ],
    [
        new LeafSetKind("garden.plants", "plant", [
            new LeafColumn("plant", "Plant"),
            new LeafColumn("count", "Count"),
            new LeafColumn("each", "Area each"),
            new LeafColumn("area", "Area", Litres, new ProductExpr([new FieldExpr("count"), new FieldExpr("each")]), @"\field{count} \cdot \field{each}"),
        ], "row"),
    ]);

    static TraceTerm T(string id, string kind, double? value, string? parent, int children = 0, Dictionary<string, string>? key = null) =>
        new(id, kind, value is null ? TermState.Absent : TermState.Value, children, parent, key, value is null ? null : JsonValue.Create(value.Value));

    static JsonObject Plant(string parent, string plant, double count, double each, double? area = null) =>
        new() { ["leafSet"] = "garden.plants", ["parent"] = parent, ["plant"] = plant, ["count"] = count, ["each"] = each, ["area"] = area ?? count * each };

    /// <summary>
    /// Two beds: north with two plants, south with one (or, on After, with a third plant), and terms of every operation
    /// over the parts of a spread.
    /// </summary>
    static CalculationTrace Garden(bool after = false, Func<TraceTerm, TraceTerm>? edit = null, bool dropSouthDepth = false)
    {
        var density = 0.5;
        var northArea = 2 * 1.5 + 3 * 0.5;
        var southArea = after ? 4 * 1.0 + 2 * 0.25 : 4 * 1.0;
        var north = northArea * 2.0 * density;
        var south = southArea * 3.0 * density;
        var parts = new[] { 3.0, -1.0, 2.0 };
        var corr = new[] { new[] { 1, 0.5, 0.2 }, new[] { 0.5, 1, 0.4 }, new[] { 0.2, 0.4, 1 } };
        var spread = Math.Sqrt(Enumerable.Range(0, 3).Sum(k => Enumerable.Range(0, 3).Sum(l => corr[k][l] * parts[k] * parts[l])));
        var rhos = new[] { 0.5, 0.3, 0.8 };
        var pool = Math.Sqrt(Math.Pow(Enumerable.Range(0, 3).Sum(k => rhos[k] * parts[k]), 2) + Enumerable.Range(0, 3).Sum(k => (1 - rhos[k] * rhos[k]) * parts[k] * parts[k]));
        var terms = new List<TraceTerm>
        {
            T("total", "garden.total", north + south, null, 3),
            T("north", "garden.bed", north, "total", 2, new() { ["bed"] = "north" }),
            T("north/area", "garden.area", northArea, "north") with { Leaves = new("garden.plants", 2) },
            T("north/depth", "garden.depth", 2.0, "north"),
            T("south", "garden.bed", south, "total", dropSouthDepth ? 3 : 2, new() { ["bed"] = "south" }),
            T("south/area", "garden.area", southArea, "south") with { Leaves = new("garden.plants", after ? 2 : 1) },
            T("east", "garden.bed", null, "total"),
            T("ops", "garden.spread", spread, null, 3),
            T("ops/p1", "garden.part", parts[0], "ops", key: new() { ["part"] = "a" }),
            T("ops/p2", "garden.part", parts[1], "ops", key: new() { ["part"] = "b" }),
            T("ops/p3", "garden.part", parts[2], "ops", key: new() { ["part"] = "c" }),
            T("pool", "garden.pool", pool, null),
            T("best", "garden.best", 3.0, null, 2) with { Governed = JsonValue.Create(0) },
            T("best/best", "garden.part", 3.0, "best"),
            T("best/other", "garden.part", null, "best"),
            T("least", "garden.least", 3.0, null) with { Slots = new Dictionary<string, string> { ["best"] = "best/best", ["other"] = "best/other" } },
            T("net", "garden.net", 3.0, null) with { Slots = new Dictionary<string, string> { ["best"] = "best/best", ["other"] = "best/other" } },
            T("ratio", "garden.ratio", 1.5, null) with { Slots = new Dictionary<string, string> { ["best"] = "best/best", ["other"] = "ops/p3" } },
            T("bounded", "garden.bounded", 2.5, null) with { Slots = new Dictionary<string, string> { ["best"] = "best/best" } },
            T("shape", "garden.shape", Math.Sqrt(3), null) with { Slots = new Dictionary<string, string> { ["best"] = "best/best" } },
            T("chance", "garden.chance", NormalDistribution.Cdf(0.4), null),
            T("rate", "garden.rate", 0.7, null, 1),
            new("rate/soil", "garden.soil", TermState.Value, 0, "rate", Value: JsonValue.Create("loam")),
        };
        if (!dropSouthDepth) terms.Insert(6, T("south/depth", "garden.depth", 3.0, "south"));
        if (edit is not null) terms = terms.Select(edit).ToList();
        var leaves = new List<JsonObject> { Plant("north/area", "rose", 2, 1.5), Plant("north/area", "fern", 3, 0.5), Plant("south/area", "bean", 4, 1.0) };
        if (after) leaves.Add(Plant("south/area", "mint", 2, 0.25));
        var parameters = new List<TraceParameter>
        {
            new("density", ParameterSource.Configuration, Value: density, Config: new("garden.density", "1")),
            new("cap", ParameterSource.Regulation, Value: 2.5, Citation: "Garden rules §2"),
            new("z", ParameterSource.Input, Value: 0.4, Input: new("garden", "z")),
            new("corr", ParameterSource.Regulation, Matrix: new(["a", "b", "c"], ["a", "b", "c"], corr.Select(r => (IReadOnlyList<double>)r).ToList())),
            new("rho", ParameterSource.Regulation, Value: 0.5, Scope: "ops/p1"),
            new("rho", ParameterSource.Regulation, Value: 0.3, Scope: "ops/p2"),
            new("rho", ParameterSource.Regulation, Value: 0.8, Scope: "ops/p3"),
            new("rates", ParameterSource.Regulation, Matrix: new(["sand", "loam", "clay"], ["rate"], [[0.9], [0.7], [0.4]])),
        };
        return new CalculationTrace(CalculationTrace.DocumentTypeName, CalculationTrace.ContractVersion, "garden.water", Method, "plant", 1e-9,
            ["total", "ops", "pool", "best", "least", "net", "ratio", "bounded", "shape", "chance", "rate"], parameters, terms, leaves);
    }

    static TraceEvaluator Evaluator(CalculationTrace trace) => new(new TraceIndex(trace), Catalog());

    static TermCheck Check(CalculationTrace trace, string id) => Evaluator(trace).Check(new TraceIndex(trace).Find(id)!);

    [Fact] // ADR-0011 §2: every operation of the closed set evaluates, and a well-formed trace reconciles throughout
    public void Every_term_of_a_well_formed_trace_reconciles()
    {
        var trace = Garden();
        var evaluator = Evaluator(trace);
        var checks = trace.Terms.ToDictionary(t => t.Id, t => evaluator.Check(t));
        foreach (var id in new[] { "total", "north", "north/area", "south", "ops", "best", "least", "net", "ratio", "bounded", "shape", "chance", "rate" })
            Assert.True(checks[id].Status == CheckStatus.Matches, $"{id}: {checks[id].Status} {checks[id].Reason}");
        Assert.Equal(CheckStatus.NotApplicable, checks["north/depth"].Status); // a kind without a formula
        Assert.Equal(CheckStatus.NotApplicable, checks["east"].Status);        // absent: nothing to check
    }

    [Fact] // ADR-0011 §2: children may be read from the term playing a role, as SIMM's S_b sums the risk factors under K_b
    public void Children_are_read_from_the_term_playing_a_role()
    {
        static CalculationTrace With(double value, string of) => Garden() with
        {
            Roots = [.. Garden().Roots, "tally"],
            Terms = [.. Garden().Terms, T("tally", "garden.tally", value, null) with { Slots = new Dictionary<string, string> { ["parts"] = of } }],
        };

        Assert.Equal((CheckStatus.Matches, 4.0), (Check(With(4, "ops"), "tally").Status, Check(With(4, "ops"), "tally").Computed!.Value));
        Assert.Equal(CheckStatus.Inconsistent, Check(With(5, "ops"), "tally").Status);
        Assert.Equal(CheckStatus.NotChecked, Check(With(4, "nowhere"), "tally").Status);
        var unmarked = Catalog() with { Kinds = [.. Catalog().Kinds.Select(k => k.Kind == "garden.tally" ? k with { Tex = @"\sum_k x_k" } : k)] };
        Assert.Contains(TraceCatalogs.Problems(unmarked), p => p.Contains("does not mark the slot parts", StringComparison.Ordinal));
    }

    [Fact] // ADR-0011 §2: a single-factor aggregation takes each term's correlation from its own scope
    public void Single_factor_reads_each_terms_correlation_in_its_own_scope()
    {
        // The parts sit under the spread, each with its own rho; read the pool's formula over them.
        var index = new TraceIndex(Garden());
        var asPool = index.Find("ops")! with { Kind = "garden.pool", Value = JsonValue.Create(3.4) };
        var check = new TraceEvaluator(index, Catalog()).Check(asPool);
        // (0.5·3 − 0.3·1 + 0.8·2)² + 0.75·9 + 0.91·1 + 0.36·4 = 7.84 + 9.10 = 16.94
        Assert.Equal(Math.Sqrt(16.94), check.Computed!.Value, 12);
    }

    [Fact] // ADR-0011 §2: a value that does not follow from its operands is inconsistent, with both figures, and never replaced
    public void A_term_that_does_not_add_up_is_inconsistent_and_keeps_its_value()
    {
        var trace = Garden(edit: t => t.Id == "total" ? t with { Value = JsonValue.Create(99.0) } : t);
        var check = Check(trace, "total");
        Assert.Equal(CheckStatus.Inconsistent, check.Status);
        Assert.Equal(10.5, check.Computed!.Value, 12); // north 4.5 + south 6, east absent
        Assert.Contains("99 returned", check.Reason);
        var read = new TraceReader(Catalog(), null, trace).Read("total")!;
        Assert.Equal(99.0, read.After!.Value!.GetValue<double>());
    }

    [Fact] // ADR-0011 §2: an absent operand contributes nothing to a sum, a max, a min or a difference
    public void Absent_operands_drop_out_of_sums_maxima_minima_and_differences()
    {
        var trace = Garden();
        Assert.Equal(CheckStatus.Matches, Check(trace, "total").Status); // east is absent
        Assert.Equal(3.0, Check(trace, "best").Computed);
        Assert.Equal(3.0, Check(trace, "least").Computed);
        Assert.Equal(3.0, Check(trace, "net").Computed);
    }

    [Fact] // ADR-0011 §1, §2: a slot the provider did not return leaves its term not checked, and is never read as 0
    public void A_term_below_the_returned_depth_leaves_its_parent_not_checked()
    {
        var trace = Garden(dropSouthDepth: true); // south says it has 3 terms below it and 1 is written
        var check = Check(trace, "south");
        Assert.Equal(CheckStatus.NotChecked, check.Status);
        Assert.Contains("not returned", check.Reason);
    }

    [Fact] // ADR-0011 §2: a computed leaf column is checked against its row's own fields
    public void A_leaf_row_whose_computed_column_is_wrong_is_reported()
    {
        var trace = Garden() with { Leaves = [Plant("north/area", "rose", 2, 1.5, area: 3.5), Plant("north/area", "fern", 3, 0.5), Plant("south/area", "bean", 4, 1.0)] };
        var evaluator = Evaluator(trace);
        var mismatch = Assert.Single(evaluator.CheckRow(trace.Leaves[0]));
        Assert.Equal(("area", 3.5, 3.0), (mismatch.Column, mismatch.Returned, mismatch.Computed));
        Assert.Empty(evaluator.CheckRow(trace.Leaves[1]));
    }

    [Fact] // ADR-0011 §2: a kind's TeX marks exactly the slots and parameters its expression reads, or registration refuses it
    public void A_kind_whose_tex_does_not_match_its_expression_is_refused_at_registration()
    {
        Assert.Empty(TraceCatalogs.Problems(Catalog()));
        var bad = Catalog() with
        {
            Kinds = [.. Catalog().Kinds.Select(k => k.Kind == "garden.bed" ? k with { Tex = @"W_b = \slot{area} \cdot \slot{width} \cdot \rho" } : k)],
            LeafSets = [new LeafSetKind("garden.plants", "plant", [new LeafColumn("count", "Count"), new LeafColumn("area", "Area", Expression: new FieldExpr("each"), Tex: @"\field{each}")])],
        };
        var problems = TraceCatalogs.Problems(bad).ToList();
        Assert.Contains(problems, p => p.Contains("does not mark the slot depth", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("marks the slot width", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("does not mark the param density", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("reads each, which garden.plants does not declare", StringComparison.Ordinal));
        var refused = Assert.Throws<InvalidOperationException>(() => new CompositionRegistry([new CatalogModule(bad)]));
        Assert.Contains("garden.bed", refused.Message);
        var registry = new CompositionRegistry([new CatalogModule(Catalog())]);
        Assert.NotNull(registry.FindTraceCatalog("garden-water", "2026-01"));
    }

    sealed class CatalogModule(TraceCatalog catalog) : IQubeModule
    {
        public void Register(CompositionRegistryBuilder builder) => builder.AddTraceCatalog(catalog);
    }

    [Fact] // ADR-0011 §4: Before and After pair by id; an absent side contributes nothing, so a new term's Diff is its value
    public void Sides_pair_by_id_and_a_term_on_one_side_has_the_other_sides_value_as_its_diff()
    {
        var reader = new TraceReader(Catalog(), Garden(), Garden(after: true));
        var south = reader.Read("south/area")!;
        Assert.Equal(DiffState.Value, south.Diff!.State);
        Assert.Equal(0.5, south.Diff.Value!.Value, 12);
        var east = reader.Read("east")!;
        Assert.Equal(DiffState.Absent, east.Diff!.State);
        TraceTerm Planted(TraceTerm t) => t.Id == "east" ? t with { State = TermState.Value, Value = JsonValue.Create(2.0) } : t;
        var added = new TraceReader(Catalog(), Garden(), Garden(after: true, edit: Planted)).Read("east")!.Diff!;
        Assert.Equal((DiffState.Value, 2.0), (added.State, added.Value));
        var removed = new TraceReader(Catalog(), Garden(edit: Planted), Garden(after: true)).Read("east")!.Diff!;
        Assert.Equal((DiffState.Value, -2.0), (removed.State, removed.Value));
        var rows = reader.Leaves("south/area");
        Assert.Equal(2, rows.Total);
        var mint = rows.Rows.Single(r => r.Entity == "mint");
        Assert.Null(mint.Before);
        Assert.NotNull(mint.After);
    }

    [Fact] // ADR-0011 §2, §6: the operand a max took governs and its other operands do not; a term that is no operand of one has no mark
    public void The_operand_a_max_took_governs_and_its_other_operands_do_not()
    {
        var reader = new TraceReader(Catalog(), null, Garden());
        Assert.Equal((true, false), (reader.Read("best/best")!.After!.Governs, reader.Read("best/other")!.After!.Governs));
        Assert.Null(reader.Read("north")!.After!.Governs);

        // Named by its slot rather than its position.
        TraceTerm BySlot(TraceTerm t) => t.Id == "best" ? t with { Governed = JsonValue.Create("other") } : t;
        var bySlot = new TraceReader(Catalog(), null, Garden(edit: BySlot));
        Assert.Equal((false, true), (bySlot.Read("best/best")!.After!.Governs, bySlot.Read("best/other")!.After!.Governs));
    }

    [Fact] // ADR-0011 §4: a side that is unavailable, not returned or inconsistent makes the Diff unavailable, never 0
    public void A_diff_against_a_side_without_a_value_it_can_stand_behind_is_unavailable()
    {
        var failed = Garden(after: true, edit: t => t.Id == "north" ? t with { State = TermState.Unavailable, Value = null, Reason = "the engine timed out" } : t);
        var diff = new TraceReader(Catalog(), Garden(), failed).Read("north")!.Diff!;
        Assert.Equal(DiffState.Unavailable, diff.State);
        Assert.Contains("timed out", diff.Reason);
        var wrong = Garden(after: true, edit: t => t.Id == "total" ? t with { Value = JsonValue.Create(1.0) } : t);
        Assert.Contains("does not reconcile", new TraceReader(Catalog(), Garden(), wrong).Read("total")!.Diff!.Reason);
        var shallow = new TraceReader(Catalog(), Garden(), Garden(after: true, dropSouthDepth: true));
        var depth = shallow.Read("south/depth")!;
        Assert.Equal(TermState.NotReturned, depth.After!.State);
        Assert.False(depth.After.Written);
        Assert.Equal(DiffState.Unavailable, depth.Diff!.State);
    }

    [Fact] // ADR-0011 §4: a decision's text compares as changed or not; it has no numeric Diff
    public void A_text_term_has_no_numeric_diff()
    {
        var clay = Garden(after: true, edit: t => t.Id == "rate/soil" ? t with { Value = JsonValue.Create("clay") } : t.Id == "rate" ? t with { Value = JsonValue.Create(0.4) } : t);
        var reader = new TraceReader(Catalog(), Garden(), clay);
        var soil = reader.Read("rate/soil")!.Diff!;
        Assert.Equal((DiffState.NotNumeric, true), (soil.State, soil.Changed));
        Assert.Equal(-0.3, reader.Read("rate")!.Diff!.Value!.Value, 12);
        Assert.Equal(CheckStatus.Matches, reader.Read("rate")!.After!.Check.Status);
    }

    [Fact] // ADR-0011 §1: a decision carries, on each side, the inputs it read, its reasons and the rule it applied
    public void A_decision_is_read_with_its_inputs_reasons_and_rule_on_each_side()
    {
        TraceTerm Decided(TraceTerm t, string soil, string rule) => t.Id == "rate/soil"
            ? t with
            {
                Value = JsonValue.Create(soil),
                Inputs = new Dictionary<string, JsonNode?> { ["survey"] = JsonValue.Create(soil) },
                Because = [$"The survey found {soil}."],
                Citation = rule,
            }
            : t.Id == "rate" ? t with { Value = JsonValue.Create(soil == "clay" ? 0.4 : 0.7) } : t;
        var reader = new TraceReader(Catalog(), Garden(edit: t => Decided(t, "loam", "Garden rules §3")), Garden(after: true, edit: t => Decided(t, "clay", "Garden rules §4")));
        var soil = reader.Read("rate/soil")!;
        Assert.Equal("Garden rules §1", soil.Citation);
        Assert.Equal(("Garden rules §3", "Garden rules §4"), (soil.Before!.Citation, soil.After!.Citation));
        Assert.Equal(["The survey found clay."], soil.After.Because);
        Assert.Equal("clay", soil.After.Inputs!["survey"]!.GetValue<string>());
    }

    [Fact] // ADR-0011 §4 (owner 2026-11-07): a case priced without Before is read on After alone, and every term still opens
    public void A_case_priced_on_one_side_is_read_on_that_side_with_no_diff()
    {
        var reader = new TraceReader(Catalog(), null, Garden(after: true));
        Assert.False(reader.PricedBothSides);
        Assert.Equal([TraceSide.After], reader.Sides);
        var page = reader.Open("total", ChildOrder.Size)!;
        Assert.Null(page.Term.Before);
        Assert.Null(page.Term.Diff);
        Assert.Null(page.BeforeParameters);
        Assert.Equal(CheckStatus.Matches, page.Term.After!.Check.Status);
        Assert.Equal(["south", "north", "east"], page.Children.Select(c => c.Id)); // by value; absent last
        Assert.Equal(TermState.Absent, page.Children[2].After!.State);
        var leaves = reader.Leaves("south/area");
        Assert.All(leaves.Rows, r => Assert.Null(r.Before));
        Assert.Equal(["total", "south", "south/area"], reader.Open("south/area")!.Path.Select(p => p.Id));
    }

    [Fact] // ADR-0011 §6: children open in the formula's order, or by the size of their change
    public void Children_order_by_formula_or_by_the_size_of_their_change()
    {
        var reader = new TraceReader(Catalog(), Garden(), Garden(after: true));
        Assert.Equal(["north", "south", "east"], reader.Open("total")!.Children.Select(c => c.Id));
        Assert.Equal(["south", "north", "east"], reader.Open("total", ChildOrder.Size)!.Children.Select(c => c.Id));
        var page = reader.Open("total", offset: 1, limit: 1)!;
        Assert.Equal((3, "south"), (page.ChildTotal, page.Children.Single().Id));
        Assert.Equal("bed north", reader.Read("north")!.Label);
        Assert.Contains(reader.Open("north")!.AfterParameters!, p => p.Name == "density");
    }

    [Fact] // ADR-0011 §1: a trace the kernel cannot read is refused, not guessed at
    public void A_malformed_trace_is_refused()
    {
        Assert.Throws<TraceInvalid>(() => new TraceIndex(Garden() with { Terms = [.. Garden().Terms, Garden().Terms[0]] }));
        Assert.Throws<TraceInvalid>(() => new TraceIndex(Garden(edit: t => t.Id == "north" ? t with { Parent = "nowhere" } : t)));
        Assert.Throws<TraceInvalid>(() => new TraceIndex(Garden(edit: t => t.Id == "north" ? t with { State = TermState.NotReturned, Value = null } : t)));
        Assert.Throws<TraceInvalid>(() => new TraceIndex(Garden() with { Parameters = [new("x", ParameterSource.Input, Value: 1, Text: "one")] }));
        Assert.Throws<TraceInvalid>(() => new TraceReader(Catalog(), null, null));
        Assert.Throws<TraceInvalid>(() => new TraceReader(Catalog() with { Method = new("other", "1") }, null, Garden()));
    }

    [Theory] // ADR-0011 §2: normalCdf, which an option's supervisory delta reads, is accurate in the body and the tails
    [InlineData(0.0, 0.5)]
    [InlineData(1.96, 0.97500210485177952)]
    [InlineData(-0.6146431135879091, 0.26939521771053270)]
    [InlineData(-3.5, 2.3262907903552504e-4)]
    [InlineData(-8.0, 6.220960574271819e-16)]
    [InlineData(5.0, 0.9999997133484281)]
    public void Normal_cdf_is_accurate(double z, double expected) =>
        Assert.True(Math.Abs(NormalDistribution.Cdf(z) - expected) <= 1e-15 + 1e-13 * expected, $"Φ({z}) = {NormalDistribution.Cdf(z):R}, expected {expected:R}");

    [Fact] // ADR-0011 §1: the trace and the catalogue's expressions cross the wire as written, with the operation named by op
    public void A_trace_and_an_expression_round_trip_through_the_wire_options()
    {
        var trace = Garden();
        var json = JsonSerializer.Serialize(trace, WireJson.Options);
        var back = JsonSerializer.Deserialize<CalculationTrace>(json, WireJson.Options)!;
        Assert.Equal(trace.Terms.Count, back.Terms.Count);
        Assert.Contains("\"state\":\"absent\"", json);
        TraceExpr expr = new QuadraticFormExpr(new ChildrenExpr("garden.part"), ["corr"], "part");
        var exprJson = JsonSerializer.Serialize(expr, WireJson.Options);
        Assert.StartsWith("{\"op\":\"quadraticForm\"", exprJson);
        Assert.IsType<QuadraticFormExpr>(JsonSerializer.Deserialize<TraceExpr>(exprJson, WireJson.Options));
        Assert.Equal(CheckStatus.Matches, Evaluator(back).Check(new TraceIndex(back).Find("ops")!).Status);
    }
}
