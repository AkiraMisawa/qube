using System.Globalization;
using System.Text.Json.Nodes;
using Qube.Composition.Application;
using Qube.Composition.Application.Workspaces;

namespace Qube.Samples.PositionKeeper;

/// <summary>
/// The Position Keeper's edits on the generic workspace (ADR-0019 §2). The book is the acquired source and no edit changes it;
/// what the user creates is in the one log, as in every application.
/// </summary>
public sealed class PkEditRules : IEditRules
{
    public const string App = "pk";

    public string KindOf(EditTarget target) => $"pk.{target.Scope}";

    public IReadOnlyList<string> ConflictGroup(EditTarget target) => [target.Field];
}

/// <summary>
/// One result set as a Position Keeper workspace's acquired source (ADR-0019 §2): its version is the result set's reference and
/// content hash, so a set whose files changed is another version and is never read as the one the workspace acquired.
/// </summary>
public sealed class PkBookSource(PkResultSet set) : ISourceVersion
{
    public PkResultSet Set => set;

    /// <summary>The base scenario's values and sensitivities: the book (ADR-0019 §7).</summary>
    public IReadOnlyList<PkValue> Values { get; } = [.. set.Values.Where(v => v.Scenario == set.Manifest.BaseScenario)];

    public IReadOnlyList<PkSensitivity> Sensitivities { get; } = [.. set.Sensitivities.Where(s => s.Scenario == set.Manifest.BaseScenario)];

    public string Version => $"{set.Reference}@{set.ContentHash}";

    public JsonObject Descriptor => new() { ["reference"] = set.Reference, ["contentHash"] = set.ContentHash };

    /// <summary>The kinds and inputs a move can be held for: the book's and the hedge curves', each once.</summary>
    public IReadOnlyList<(string Kind, string Input)> Inputs { get; } =
        [.. set.Sensitivities.Where(s => s.Scenario == set.Manifest.BaseScenario).Select(s => (s.Kind, s.Input))
            .Concat(set.Manifest.HedgeCurves.Values.Select(curve => (HedgeKind, curve))).Distinct()];

    /// <summary>The kind a hedge's risk on its curve is of.</summary>
    public const string HedgeKind = "zero_rate";

    // No edit is read against a book row: its tables refuse every write (ADR-0019 §2). A move starts empty for each
    // kind and input the result set has (ADR-0019 §7), and the user's value is an edit on that.
    public SourceValue? Read(string entity, string field) =>
        field == PkMoves.Field && Inputs.Any(i => PkMoves.Entity(i.Kind, i.Input) == entity) ? new SourceValue(null) : null;

    public static string ValueKey(PkValue v) => $"{v.Counterparty}/{v.Metric}";

    public static string RiskKey(PkSensitivity s) => $"{s.Counterparty}/{s.Metric}/{s.Kind}/{s.Input}/{s.Key}";

    public static PkBookSource Of(WorkspaceState state) => state.Source as PkBookSource
        ?? throw new InvalidOperationException($"Workspace {state.WorkspaceId} is not a Position Keeper workspace on a result set.");

    /// <summary>
    /// Acquires exactly the version a workspace stored, or refuses: a result set that reads differently now is not
    /// the book the workspace was made on, and is not shown as if it were.
    /// </summary>
    public static SourceResolver Resolver(Func<IPkResults> results) => descriptor =>
    {
        var reference = descriptor["reference"]?.GetValue<string>() ?? throw new InvalidOperationException("A Position Keeper workspace's source names no result set.");
        var hash = descriptor["contentHash"]?.GetValue<string>() ?? throw new InvalidOperationException("A Position Keeper workspace's source names no content hash.");
        // Refused as the workspace's own refusal, by the result set's code: every command that loads the workspace
        // answers it as a refusal by name, whichever command it is.
        PkResultSet set;
        try
        {
            set = results().Read(reference);
        }
        catch (PkResultsRefused refused)
        {
            throw new WorkspaceRefused(refused.Code.ToString(), refused.Message);
        }
        return set.ContentHash == hash ? new PkBookSource(set)
            : throw new WorkspaceRefused(nameof(PkResultsRefusal.Changed),
                $"Result set '{reference}' has changed since the workspace was made on it (it was {hash[..12]}, it is {set.ContentHash[..12]}).");
    };
}

/// <summary>Which result set a new Position Keeper workspace is made on when the request names none. Null: none is configured.</summary>
public sealed record PkOptions(string? ResultSet);

/// <summary>The results port where no directory of result sets is configured: every read is refused, by that name.</summary>
public sealed class NoPkResults : IPkResults
{
    public PkResultSet Read(string reference) =>
        throw new PkResultsRefused(PkResultsRefusal.NotConfigured, "No directory of result sets is configured (PositionKeeper:ResultsRoot).");
}

public sealed class PkWorkspaces(WorkspaceService workspaces, IPkResults results, PkOptions options)
{
    /// <summary>
    /// A Position Keeper workspace on a result set (ADR-0019 §2): the one the request names, or the configured one. With neither,
    /// or with a set that cannot be read, nothing is made and the refusal says why; no book stands in (ADR-0019 §4).
    /// </summary>
    public WorkspaceState Create(string user, string? resultSet, RunOrigin origin = RunOrigin.Pricer)
    {
        var reference = resultSet ?? options.ResultSet
            ?? throw new PkResultsRefused(PkResultsRefusal.NotConfigured, "No result set is named and none is configured (PositionKeeper:ResultSet).");
        var source = new PkBookSource(results.Read(reference));
        var manifest = source.Set.Manifest;
        var context = new JsonObject
        {
            ["valuationDate"] = manifest.ValuationDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["resultSet"] = reference,
            ["engine"] = manifest.Engine,
            ["engineCommit"] = manifest.EngineCommit,
            ["inputSet"] = manifest.InputSet,
        };
        return workspaces.Create(user, PkEditRules.App, context, source, origin);
    }
}
