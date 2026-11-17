using System.Globalization;
using System.Text.Json.Nodes;
using Qube.Composition.Application;
using Qube.Composition.Application.Workspaces;

namespace Qube.Samples.Desk;

/// <summary>
/// The Desk's edits on the generic workspace (ADR-0019 §2). The book is the acquired source and no edit changes it;
/// what the user creates is in the one log, as in every application.
/// </summary>
public sealed class DeskEditRules : IEditRules
{
    public const string App = "desk";

    public string KindOf(EditTarget target) => $"desk.{target.Scope}";

    public IReadOnlyList<string> ConflictGroup(EditTarget target) => [target.Field];
}

/// <summary>
/// One result set as a Desk workspace's acquired source (ADR-0019 §2): its version is the result set's reference and
/// content hash, so a set whose files changed is another version and is never read as the one the workspace acquired.
/// </summary>
public sealed class DeskBookSource(DeskResultSet set) : ISourceVersion
{
    public DeskResultSet Set => set;

    /// <summary>The base scenario's values and sensitivities: the book (ADR-0019 §7).</summary>
    public IReadOnlyList<DeskValue> Values { get; } = [.. set.Values.Where(v => v.Scenario == set.Manifest.BaseScenario)];

    public IReadOnlyList<DeskSensitivity> Sensitivities { get; } = [.. set.Sensitivities.Where(s => s.Scenario == set.Manifest.BaseScenario)];

    public string Version => $"{set.Reference}@{set.ContentHash}";

    public JsonObject Descriptor => new() { ["reference"] = set.Reference, ["contentHash"] = set.ContentHash };

    /// <summary>The kinds and inputs a move can be held for: the book's and the hedge curves', each once.</summary>
    public IReadOnlyList<(string Kind, string Input)> Inputs { get; } =
        [.. set.Sensitivities.Where(s => s.Scenario == set.Manifest.BaseScenario).Select(s => (s.Kind, s.Input))
            .Concat(set.Manifest.HedgeCurves.Values.Select(curve => ("zero_rate", curve))).Distinct()];

    // No edit is read against a book row: its tables refuse every write (ADR-0019 §2). A move starts empty for each
    // kind and input the result set has (ADR-0019 §7), and the user's value is an edit on that.
    public SourceValue? Read(string entity, string field) =>
        field == DeskMoves.Field && Inputs.Any(i => DeskMoves.Entity(i.Kind, i.Input) == entity) ? new SourceValue(null) : null;

    public static string ValueKey(DeskValue v) => $"{v.Counterparty}/{v.Metric}";

    public static string RiskKey(DeskSensitivity s) => $"{s.Counterparty}/{s.Metric}/{s.Kind}/{s.Input}/{s.Key}";

    public static DeskBookSource Of(WorkspaceState state) => state.Source as DeskBookSource
        ?? throw new InvalidOperationException($"Workspace {state.WorkspaceId} is not a Desk workspace on a result set.");

    /// <summary>
    /// Acquires exactly the version a workspace stored, or refuses: a result set that reads differently now is not
    /// the book the workspace was made on, and is not shown as if it were.
    /// </summary>
    public static SourceResolver Resolver(Func<IDeskResults> results) => descriptor =>
    {
        var reference = descriptor["reference"]?.GetValue<string>() ?? throw new InvalidOperationException("A Desk workspace's source names no result set.");
        var hash = descriptor["contentHash"]?.GetValue<string>() ?? throw new InvalidOperationException("A Desk workspace's source names no content hash.");
        var set = results().Read(reference);
        return set.ContentHash == hash ? new DeskBookSource(set)
            : throw new DeskResultsRefused(DeskResultsRefusal.Changed,
                $"Result set '{reference}' has changed since the workspace was made on it (it was {hash[..12]}, it is {set.ContentHash[..12]}).");
    };
}

/// <summary>Which result set a new Desk workspace is made on when the request names none. Null: none is configured.</summary>
public sealed record DeskOptions(string? ResultSet);

/// <summary>The results port where no directory of result sets is configured: every read is refused, by that name.</summary>
public sealed class NoDeskResults : IDeskResults
{
    public DeskResultSet Read(string reference) =>
        throw new DeskResultsRefused(DeskResultsRefusal.NotConfigured, "No directory of result sets is configured (Desk:ResultsRoot).");
}

public sealed class DeskWorkspaces(WorkspaceService workspaces, IDeskResults results, DeskOptions options)
{
    /// <summary>
    /// A Desk workspace on a result set (ADR-0019 §2): the one the request names, or the configured one. With neither,
    /// or with a set that cannot be read, nothing is made and the refusal says why; no book stands in (ADR-0019 §4).
    /// </summary>
    public WorkspaceState Create(string user, string? resultSet, RunOrigin origin = RunOrigin.Pricer)
    {
        var reference = resultSet ?? options.ResultSet
            ?? throw new DeskResultsRefused(DeskResultsRefusal.NotConfigured, "No result set is named and none is configured (Desk:ResultSet).");
        var source = new DeskBookSource(results.Read(reference));
        var manifest = source.Set.Manifest;
        var context = new JsonObject
        {
            ["valuationDate"] = manifest.ValuationDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["resultSet"] = reference,
            ["engine"] = manifest.Engine,
            ["engineCommit"] = manifest.EngineCommit,
            ["inputSet"] = manifest.InputSet,
        };
        return workspaces.Create(user, DeskEditRules.App, context, source, origin);
    }
}
