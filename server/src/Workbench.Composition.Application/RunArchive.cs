using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Workbench.Composition.Contracts;

namespace Workbench.Composition.Application;

/// <summary>
/// A finished Run as one zip (ADR-0010 §7, docs/05): <c>run.json</c>, its record with the pinned workflow and every
/// invocation, which is its lineage and its diagnostics; each value the Run references, its frozen inputs, each
/// invocation's outputs and its results, under <c>data/</c>; and <c>manifest.json</c>, which says what each value is to
/// the Run. A value no longer retained is listed as expired or missing, never left out and never recomputed.
/// </summary>
public static class RunArchive
{
    public const string Run = "run.json";
    public const string Manifest = "manifest.json";

    /// <summary>The roles each referenced value plays in the Run, in the order the Run names them.</summary>
    public static IReadOnlyList<(TypedDataRef Reference, IReadOnlyList<string> Roles)> References(RunRecord run)
    {
        var roles = new Dictionary<TypedDataRef, List<string>>();
        void Add(TypedDataRef reference, string role)
        {
            if (!roles.TryGetValue(reference, out var list)) roles[reference] = list = [];
            list.Add(role);
        }
        foreach (var step in run.Workflow.Steps)
            foreach (var (name, binding) in step.Bindings)
                if (binding is DataReferenceBinding pinned) Add(pinned.Reference, $"input:{step.StepId}/{name}");
        foreach (var invocation in run.Invocations)
            foreach (var (name, reference) in invocation.Outputs) Add(reference, $"invocation:{invocation.InvocationKey}/{name}");
        foreach (var (name, reference) in run.Outputs) Add(reference, $"output:{name}");
        return [.. roles.Select(r => (r.Key, (IReadOnlyList<string>)r.Value))];
    }

    /// <summary>Writes the archive of a finished Run. A Run still running has no archive yet: it is refused.</summary>
    public static void Write(RunRecord run, IDataReader data, Stream to)
    {
        if (run.Status == RunStatus.Running) throw new InvalidOperationException($"Run {run.RunId} is still running; its archive is written once it finishes.");
        using var zip = new ZipArchive(to, ZipArchiveMode.Create, leaveOpen: true);
        Entry(zip, Run, JsonSerializer.SerializeToNode(run, WireJson.Options));
        var entries = new JsonArray();
        foreach (var (reference, roles) in References(run))
        {
            var path = $"data/{reference.DataId}@{reference.Revision}.json";
            var state = data.Read(reference) switch
            {
                DataLookup.Found found => Retained(zip, path, found.Content),
                DataLookup.Expired => "expired",
                DataLookup.Unknown => "missing",
                var other => throw new InvalidOperationException($"Unhandled data lookup {other.GetType().Name}."),
            };
            entries.Add(new JsonObject
            {
                ["path"] = state == "retained" ? path : null,
                ["state"] = state,
                ["dataId"] = reference.DataId,
                ["revision"] = reference.Revision,
                ["contractId"] = reference.ContractId,
                ["contractVersion"] = reference.ContractVersion,
                ["roles"] = new JsonArray([.. roles.Select(r => JsonValue.Create(r))]),
            });
        }
        Entry(zip, Manifest, new JsonObject
        {
            ["runId"] = run.RunId,
            ["status"] = JsonSerializer.SerializeToNode(run.Status, WireJson.Options),
            ["owner"] = run.Owner,
            ["acceptedAt"] = run.AcceptedAt,
            ["completedAt"] = run.CompletedAt,
            ["entries"] = entries,
        });
    }

    static string Retained(ZipArchive zip, string path, JsonNode content)
    {
        Entry(zip, path, content);
        return "retained";
    }

    static void Entry(ZipArchive zip, string path, JsonNode? content)
    {
        using var stream = zip.CreateEntry(path, CompressionLevel.Optimal).Open();
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        (content ?? throw new InvalidOperationException($"{path} has no content.")).WriteTo(writer);
    }
}
