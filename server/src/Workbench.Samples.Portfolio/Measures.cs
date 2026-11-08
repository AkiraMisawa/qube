using System.Text.Json.Nodes;
using Workbench.Composition.Application.Workspaces;
using Workbench.Composition.Contracts;

namespace Workbench.Samples.Portfolio;

// The measures register (ADR-0010 §10): a measure is a figure family a Price can be asked for, registered by the domain
// module that owns it. The page and this module know no measure's rules; they read what the owner registered.

/// <summary>One figure a measure reports, such as CVA's CVA Charge.</summary>
public sealed record MetricDefinition(string Id, string Title);

/// <summary>
/// A measure as its module registers it. <paramref name="Headline"/> is the metric the collapsed result line shows.
/// <paramref name="DeclaresMembership"/> says whether it prices only some trades and says per trade why (FVA's rule is the
/// first, ADR-0010 §5); <paramref name="Traced"/> whether a Metrics cell opens its calculation trace (ADR-0011).
/// </summary>
public sealed record MeasureDefinition(string Id, string Title, IReadOnlyList<MetricDefinition> Metrics, string Headline, bool DeclaresMembership, bool Traced);

/// <summary>Whether a measure prices a trade, and why (ADR-0010 §5, §10). Out is never silent: it says why.</summary>
public sealed record Membership(bool In, string Reason);

/// <summary>
/// A measure's membership rule over one state (ADR-0010 §10): which trades it prices and why, decided on the server
/// once. Its version is frozen into the Run. Where the rule reads a CSA's terms (FVA's, §5), it says so per CSA too.
/// </summary>
public interface IMembershipRule
{
    string Measure { get; }
    string Version { get; }
    Membership Of(EffectiveTrade trade);
    /// <summary>The CSA's own standing under the rule, or null where the rule does not read CSAs.</summary>
    Membership? Of(EffectiveCsa csa);
}

/// <summary>Why one measure cannot be priced as the workspace stands. It blocks that measure alone (ADR-0010 §10).</summary>
public sealed record MeasureRefusal(string Measure, string Code, string Message);

/// <summary>A measure in the workspace view: its definition and the module that owns it.</summary>
public sealed record RegisteredMeasure(string Id, string Title, string Module, IReadOnlyList<MetricDefinition> Metrics, string Headline, bool DeclaresMembership, bool Traced);

/// <summary>A domain module's registration into the pricer (ADR-0010 §10). XVA's is the first (§11).</summary>
public interface IMeasureModule
{
    /// <summary>The module's id, such as <c>xva</c>; it names the module's part of the accepted input and its Run output.</summary>
    string Id { get; }

    /// <summary>The measures it owns, in the order the page shows them.</summary>
    IReadOnlyList<MeasureDefinition> Measures { get; }

    /// <summary>The module's own part of the accepted input, read from the frozen state.</summary>
    JsonObject Input(WorkspaceState state);

    /// <summary>The membership rules of its measures that declare one (<see cref="MeasureDefinition.DeclaresMembership"/>).</summary>
    IReadOnlyList<IMembershipRule> Membership(WorkspaceState state);

    /// <summary>What blocks each of this module's selected measures, by name. Shared inputs are the portfolio's to check.</summary>
    IReadOnlyList<MeasureRefusal> Validate(WorkspaceState state, IReadOnlyList<string> selected);

    /// <summary>The step that prices this module's selected measures from the accepted input, writing the measure result.</summary>
    WorkflowStep Step(TypedDataRef acceptedInput);
}

/// <summary>Every registered measure, in module order. Ids are unique across modules, or startup stops.</summary>
public sealed class MeasureRegister
{
    public IReadOnlyList<IMeasureModule> Modules { get; }
    public IReadOnlyList<RegisteredMeasure> Measures { get; }

    public MeasureRegister(IEnumerable<IMeasureModule> modules)
    {
        Modules = [.. modules];
        Measures = [.. Modules.SelectMany(m => m.Measures.Select(d => new RegisteredMeasure(d.Id, d.Title, m.Id, d.Metrics, d.Headline, d.DeclaresMembership, d.Traced)))];
        var duplicate = Measures.GroupBy(m => m.Id).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException($"Measure {duplicate.Key} is registered by {string.Join(" and ", duplicate.Select(m => m.Module))}.");
    }

    public IReadOnlyList<string> Ids => [.. Measures.Select(m => m.Id)];

    /// <summary>Every declared membership rule over a state. A measure that declares membership and gives no rule stops here.</summary>
    public IReadOnlyList<IMembershipRule> Rules(WorkspaceState state)
    {
        var rules = Modules.SelectMany(m => m.Membership(state)).ToList();
        var missing = Measures.Where(m => m.DeclaresMembership && rules.All(r => r.Measure != m.Id)).Select(m => m.Id).ToList();
        if (missing.Count > 0) throw new InvalidOperationException($"Measure {string.Join(", ", missing)} declares membership and gave no rule.");
        return rules;
    }

    /// <summary>The trades with each declared measure's membership on them, as the page shows them and Price freezes them.</summary>
    public IReadOnlyList<EffectiveTrade> WithMembership(WorkspaceState state, IReadOnlyList<EffectiveTrade> trades)
    {
        var rules = Rules(state);
        return rules.Count == 0 ? trades : [.. trades.Select(t => t with { Membership = rules.ToDictionary(r => r.Measure, r => r.Of(t)) })];
    }

    /// <summary>The CSAs with each rule's standing on them, where the rule reads CSAs.</summary>
    public IReadOnlyList<EffectiveCsa> WithMembership(WorkspaceState state, IReadOnlyList<EffectiveCsa> csas)
    {
        var rules = Rules(state);
        return [.. csas.Select(c => c with
        {
            Membership = rules.Select(r => (r.Measure, Standing: r.Of(c))).Where(x => x.Standing is not null).ToDictionary(x => x.Measure, x => x.Standing!),
        })];
    }

    /// <summary>A selection as given, checked and put in register order. Refused by name, never trimmed to what is known.</summary>
    public IReadOnlyList<string> Checked(IReadOnlyList<string> selection)
    {
        var unknown = selection.Where(s => !Ids.Contains(s)).Distinct().ToList();
        if (unknown.Count > 0)
            throw new WorkspaceRefused("UnknownMeasure", $"No measure {string.Join(", ", unknown)} is registered; the measures are {string.Join(", ", Ids)}.");
        if (selection.Count == 0)
            throw new WorkspaceRefused("NoMeasure", "Select at least one measure to price.");
        return [.. Ids.Where(selection.Contains)];
    }
}
