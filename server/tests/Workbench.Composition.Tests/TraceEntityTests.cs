using System.Text.Json.Nodes;
using Workbench.Composition.Application.Traces;
using Workbench.Composition.Contracts;

namespace Workbench.Composition.Tests;

/// <summary>
/// ADR-0011 §1, §6 and ADR-0012 §6 ("at any term, a trade's contribution is the sum of its lines' contributions"): the
/// leaf rows under a term, at any depth, summed per entity on each side, as the kernel reads them for any measure. A
/// synthetic, non-financial trace: an orchard whose water is allocated to rows of trees, and each tree to its keeper.
/// </summary>
public sealed class TraceEntityTests
{
    static readonly TraceMethod Method = new("orchard-water", "1");

    static TraceCatalog Catalog(string? contribution = "share") => new(Method,
    [
        new("orchard.total", "Orchard", "W", new("volume"), "Orchard rules", @"W = \sum_r W_r", TermValueType.Number, new SumExpr([new ChildrenExpr()])),
        new("orchard.row", "Row {row}", "W_r", new("volume"), "Orchard rules", @"W_r = \sum_i w_i", TermValueType.Number, new SumLeavesExpr("water")),
    ],
    [new LeafSetKind("orchard.trees", "keeper", [new LeafColumn("keeper", "Keeper"), new LeafColumn("water", "Water"), new LeafColumn("share", "Share")], "tree", contribution)]);

    static JsonObject Tree(string row, string keeper, string tree, double water, double share) =>
        new() { ["leafSet"] = "orchard.trees", ["parent"] = row, ["keeper"] = keeper, ["tree"] = tree, ["water"] = water, ["share"] = share };

    /// <summary>Two rows; Ann keeps a tree in each, Bo one in the north. After, Cy plants one in the south and Bo's is gone.</summary>
    static CalculationTrace Orchard(bool after, double? totalShare = null, bool allocated = true)
    {
        var trees = after
            ? new List<JsonObject> { Tree("north", "ann", "n1", 3, 2.5), Tree("south", "ann", "s1", 2, 1.5), Tree("south", "cy", "s2", 4, 3.0) }
            : [Tree("north", "ann", "n1", 3, 2.0), Tree("north", "bo", "n2", 1, 0.5), Tree("south", "ann", "s1", 2, 1.0)];
        double Water(string row) => trees.Where(t => t["parent"]!.GetValue<string>() == row).Sum(t => t["water"]!.GetValue<double>());
        double Share(string row) => trees.Where(t => t["parent"]!.GetValue<string>() == row).Sum(t => t["share"]!.GetValue<double>());
        TraceTerm Row(string id) => new(id, "orchard.row", TermState.Value, 0, "total", new Dictionary<string, string> { ["row"] = id }, JsonValue.Create(Water(id)),
            Leaves: new("orchard.trees", trees.Count(t => t["parent"]!.GetValue<string>() == id)), Allocation: new("euler", Share(id)));
        var total = new TraceTerm("total", "orchard.total", TermState.Value, 2, null, null, JsonValue.Create(Water("north") + Water("south")),
            Allocation: allocated ? new("euler", totalShare ?? Share("north") + Share("south")) : null);
        return new CalculationTrace(CalculationTrace.DocumentTypeName, CalculationTrace.ContractVersion, "orchard.water", Method, "tree", 1e-9,
            ["total"], [], [total, Row("north"), Row("south")], trees);
    }

    [Fact] // ADR-0012 §6: at any term, each entity's contribution is the sum of its rows' at every depth below it, per side, with the Diff
    public void An_entitys_contribution_is_the_sum_of_its_rows_below_the_term_on_each_side()
    {
        var reader = new TraceReader(Catalog(), Orchard(after: false), Orchard(after: true));

        var page = reader.Entities("total");

        // Largest change first: Cy is new (+3.0), Ann's moves +1.0, Bo's is gone (-0.5); a side without the entity has nothing.
        Assert.Equal(["cy", "ann", "bo"], page.Rows.Select(r => r.Entity));
        var ann = page.Rows.Single(r => r.Entity == "ann");
        Assert.Equal((3.0, 4.0, 1.0, 2, 2), (ann.Before!.Contribution, ann.After!.Contribution, ann.Diff, ann.Before.Rows, ann.After.Rows));
        var bo = page.Rows.Single(r => r.Entity == "bo");
        Assert.Equal((0.5, null, -0.5), (bo.Before!.Contribution, bo.After, bo.Diff));
        // The entities add up to the term's own contribution on each side.
        Assert.All(page.Checks, c => Assert.Equal(CheckStatus.Matches, c.Status));
        Assert.Equal([(TraceSide.Before, 3.5, 3.5), (TraceSide.After, 7.0, 7.0)], page.Checks.Select(c => (c.Side, c.Sum, c.Allocation!.Value)));

        // Below a row, only that row's rows.
        Assert.Equal(["cy", "ann"], reader.Entities("south").Rows.Select(r => r.Entity));
    }

    [Fact] // ADR-0011 §1, §4: entities that do not add up to the term's contribution say so with both figures; one side alone has no Diff
    public void Entities_that_do_not_add_up_say_so_and_one_side_alone_has_no_diff()
    {
        var reader = new TraceReader(Catalog(), null, Orchard(after: true, totalShare: 9.0));

        var page = reader.Entities("total");

        // One side alone: ordered by the share itself, largest first.
        Assert.All(page.Rows, r => Assert.Null(r.Diff));
        Assert.Equal(["ann", "cy"], page.Rows.Select(r => r.Entity));
        var check = Assert.Single(page.Checks);
        Assert.Equal((TraceSide.After, CheckStatus.Inconsistent, 7.0, 9.0), (check.Side, check.Status, check.Sum, check.Allocation));
    }

    [Fact] // ADR-0011 §4: a side whose entities do not add up has no change to show, so no entity has a Diff, and the page says why
    public void Entities_have_no_diff_where_a_side_does_not_add_up()
    {
        var reader = new TraceReader(Catalog(), Orchard(after: false, totalShare: 9.0), Orchard(after: true));

        var page = reader.Entities("total");

        Assert.All(page.Rows, r => Assert.Null(r.Diff));
        Assert.StartsWith("Before's entities do not add up to its allocation", page.DiffUnavailable);
        // Ordered by the share each has, After's where it has one.
        Assert.Equal(["ann", "cy", "bo"], page.Rows.Select(r => r.Entity));
        Assert.Null(reader.Entities("north").DiffUnavailable);
    }

    [Fact] // ADR-0011 §1, §4: a term not returned on a side gives its entities no Diff, never one with that side taken as 0
    public void A_term_not_returned_on_a_side_gives_its_entities_no_diff()
    {
        var after = Orchard(after: true);
        after = after with
        {
            Terms = [.. after.Terms.Where(t => t.Id != "south")],
            Leaves = [.. after.Leaves.Where(l => l["parent"]!.GetValue<string>() != "south")],
        };
        var reader = new TraceReader(Catalog(), Orchard(after: false), after);

        var south = reader.Entities("south");
        Assert.All(south.Rows, r => Assert.Null(r.Diff));
        Assert.Contains("After is not returned", south.DiffUnavailable);
        Assert.Equal([TraceSide.Before], south.Checks.Select(c => c.Side));
        // Above it, After's entities fall short of its allocation by what was not returned.
        var total = reader.Entities("total");
        Assert.All(total.Rows, r => Assert.Null(r.Diff));
        Assert.NotNull(total.DiffUnavailable);
    }

    [Fact] // ADR-0012 §6's note of 2026-11-08: at any term with an allocation, the leaf rows at every depth below it, paired by side
    public void The_leaf_rows_below_a_term_are_read_at_every_depth_where_it_has_an_allocation()
    {
        var reader = new TraceReader(Catalog(), Orchard(after: false), Orchard(after: true));

        Assert.Empty(reader.Leaves("total").Rows);
        var below = reader.Leaves("total", below: true);
        Assert.Equal([("ann", "n1", true, true), ("bo", "n2", true, false), ("ann", "s1", true, true), ("cy", "s2", false, true)],
            below.Rows.Select(r => (r.Entity, r.Component, r.Before is not null, r.After is not null)));
        Assert.Equal(4, below.Total);

        var unallocated = new TraceReader(Catalog(), Orchard(after: false, allocated: false), Orchard(after: true, allocated: false));
        Assert.Equal("NoAllocation", Assert.Throws<TraceRefused>(() => unallocated.Leaves("total", below: true)).Code);
        Assert.Equal(2, unallocated.Leaves("south", below: true).Total);
    }

    [Fact] // ADR-0011 §1: a leaf set that names no contribution has no entity contributions to sum, and says so rather than 0
    public void A_leaf_set_without_a_contribution_field_is_refused_by_name()
    {
        var reader = new TraceReader(Catalog(contribution: null), Orchard(after: false), Orchard(after: true));

        var refused = Assert.Throws<TraceRefused>(() => reader.Entities("total"));
        Assert.Equal("NoContributions", refused.Code);
        Assert.Contains("orchard.trees", refused.Message);
    }

    [Fact] // ADR-0012 §6: a term with no allocation (a max over regimes, say) has no share to split, and summing below it would count twice
    public void A_term_without_an_allocation_is_refused_rather_than_summed()
    {
        var reader = new TraceReader(Catalog(), Orchard(after: false, allocated: false), Orchard(after: true, allocated: false));

        var refused = Assert.Throws<TraceRefused>(() => reader.Entities("total"));
        Assert.Equal("NoAllocation", refused.Code);
        Assert.Equal(["cy", "ann"], reader.Entities("south").Rows.Select(r => r.Entity));
    }
}
