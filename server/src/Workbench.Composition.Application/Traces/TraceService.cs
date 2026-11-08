using System.Text.Json;
using System.Text.Json.Nodes;
using Workbench.Composition.Contracts;

namespace Workbench.Composition.Application.Traces;

// Reading a retained Run's calculation trace, a term at a time (ADR-0011 §5, §7): the read endpoint and `trace.read`
// both open it here, for a caller who may see the Run, so the page and the AI read the same terms.

/// <summary>Where a side's trace came from: the producer, whether it was simulated, and what it is of.</summary>
public sealed record TraceOrigin(
    string Side,
    TraceProducer? Engine,
    DatasetProvenance? Provenance,
    string? RequestId,
    string? ValuationDate,
    string? ReportingCurrency,
    TraceSubject? Subject);

/// <summary>
/// What a trace is of and how it was written: the Run, case, scenario and measure, the method and the depth its
/// provider declared, the sides the case priced (<c>before</c>, <c>after</c>, in that order), each side's origin, and the
/// roots to open.
/// </summary>
public sealed record TraceHeader(
    string RunId,
    string Case,
    string Scenario,
    string Measure,
    TraceMethod Method,
    string Depth,
    IReadOnlyList<string> Sides,
    IReadOnlyList<TraceOrigin> Origins,
    IReadOnlyList<TermCrumb> Roots);

/// <summary>A term opened on every side its case priced, with what the trace is of.</summary>
public sealed record TraceAnswer(TraceHeader Trace, TermPage Term);

/// <summary>A page of the leaf rows below a term, with what the trace is of.</summary>
public sealed record TraceLeavesAnswer(TraceHeader Trace, string Term, LeafPage Leaves);

/// <summary>Which trace to read: a Run's case, scenario and measure.</summary>
public sealed record TraceAddress(string RunId, string Case, string Scenario, string Measure);

/// <summary>Why a trace could not be read: a stable code, a sentence that says why, and whether asking again may answer.</summary>
public sealed class TraceRefused(string code, string message, bool retryable = false) : Exception(message)
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
}

/// <summary>
/// Opens the calculation traces a Run retained. A trace is found among the outputs of the Run's succeeded invocations:
/// a <see cref="TraceSet"/> holds one per case, scenario, side and measure; a request that wrote one trace alone priced
/// one side of one case, which is read as After of the case <c>base</c> and the scenario <c>base</c> (ADR-0011 §5,
/// DEFAULT 2026-11-08). Only the Run's owner reads it until roles arrive with sign-in (C30, C31).
/// </summary>
public sealed class TraceService(RunService runs, IDataReader data, CompositionRegistry registry)
{
    public const string BaseCase = "base";
    public const string BaseScenario = "base";
    public const string Before = "before";
    public const string After = "after";
    public const int MaxPage = 500;

    const int Retained = 32;
    readonly Lock gate = new();
    readonly Dictionary<TraceAddress, Opened> opened = [];
    readonly Queue<TraceAddress> order = new();

    sealed record Opened(TraceReader Reader, TraceHeader Header);

    /// <summary>Opens a term of a trace, or the first root when no term is named.</summary>
    public TraceAnswer Read(TraceAddress address, string userId, string? path, ChildOrder childOrder = ChildOrder.Formula, int offset = 0, int limit = 200)
    {
        CheckPage(offset, limit);
        var trace = Open(address, userId);
        var id = path ?? trace.Header.Roots[0].Id;
        var page = trace.Reader.Open(id, childOrder, offset, limit)
            ?? throw new TraceRefused("UnknownTerm", $"The trace of {address.Measure} has no term {id}; its roots are {string.Join(", ", trace.Header.Roots.Select(r => r.Id))}.");
        return new TraceAnswer(trace.Header, page);
    }

    /// <summary>A page of the leaf rows below a term, sorted by a column on request.</summary>
    public TraceLeavesAnswer Leaves(TraceAddress address, string userId, string path, int offset = 0, int limit = 200, string? sort = null, bool descending = false)
    {
        CheckPage(offset, limit);
        var trace = Open(address, userId);
        if (trace.Reader.Read(path) is null)
            throw new TraceRefused("UnknownTerm", $"The trace of {address.Measure} has no term {path}.");
        return new TraceLeavesAnswer(trace.Header, path, trace.Reader.Leaves(path, offset, limit, sort, descending));
    }

    static void CheckPage(int offset, int limit)
    {
        if (offset < 0 || limit is < 1 or > MaxPage)
            throw new TraceRefused("InvalidRange", $"A page starts at 0 or later and holds 1 to {MaxPage} rows.");
    }

    Opened Open(TraceAddress address, string userId)
    {
        var run = runs.Find(address.RunId) ?? throw new TraceRefused("UnknownRun", $"Run {address.RunId} does not exist.");
        if (run.Owner != userId) throw new TraceRefused("NotOwner", $"Run {address.RunId} is not yours.");
        if (run.Status != RunStatus.Running)
            lock (gate)
                if (opened.TryGetValue(address, out var known)) return known;

        var found = Find(run, address);
        if (run.Status == RunStatus.Running) return found;
        lock (gate)
        {
            if (opened.TryAdd(address, found))
            {
                order.Enqueue(address);
                while (order.Count > Retained) opened.Remove(order.Dequeue());
            }
            return opened[address];
        }
    }

    Opened Find(RunRecord run, TraceAddress address)
    {
        CalculationTrace? before = null, after = null;
        var written = new List<string>();
        foreach (var invocation in run.Invocations.Where(i => i.Status == InvocationStatus.Succeeded))
            foreach (var (_, reference) in invocation.Outputs.OrderBy(o => o.Key, StringComparer.Ordinal))
                foreach (var entry in Entries(reference))
                {
                    written.Add($"{entry.Measure} ({entry.Case}, {entry.Scenario}, {entry.Side})");
                    if (entry.Case != address.Case || entry.Scenario != address.Scenario || entry.Measure != address.Measure) continue;
                    switch (entry.Side)
                    {
                        case Before when before is null: before = entry.Trace; break;
                        case After when after is null: after = entry.Trace; break;
                        case Before or After:
                            throw new TraceRefused("TraceInvalid", $"Run {run.RunId} has two traces of {address.Measure} for {address.Case}, {address.Scenario}, {entry.Side}.");
                        default:
                            throw new TraceRefused("TraceInvalid", $"A trace of {entry.Measure} in Run {run.RunId} is of the side '{entry.Side}'; a side is before or after.");
                    }
                }

        if (before is null && after is null)
        {
            if (run.Status == RunStatus.Running)
                throw new TraceRefused("RunRunning", $"Run {run.RunId} is still running; its traces are read once its request answers.", retryable: true);
            var has = written.Count == 0 ? "it retained no trace" : $"it has {string.Join("; ", written.Distinct())}";
            throw new TraceRefused("UnknownTrace", $"Run {run.RunId} has no trace of {address.Measure} for case {address.Case}, scenario {address.Scenario}: {has}.");
        }

        var method = (before ?? after)!.Method;
        var catalog = registry.FindTraceCatalog(method.Id, method.Version)
            ?? throw new TraceRefused("UnknownMethod", $"The trace of {address.Measure} follows {method.Id} {method.Version}, and no module registered its kinds.");
        TraceReader reader;
        try
        {
            reader = new TraceReader(catalog, before, after);
        }
        catch (TraceInvalid invalid)
        {
            throw new TraceRefused("TraceInvalid", $"The trace of {address.Measure} in Run {run.RunId} cannot be read: {invalid.Message}");
        }
        var origins = new[] { (Before, before), (After, after) }.Where(s => s.Item2 is not null)
            .Select(s => new TraceOrigin(s.Item1, s.Item2!.Engine, s.Item2.Provenance, s.Item2.RequestId, s.Item2.ValuationDate, s.Item2.ReportingCurrency, s.Item2.Subject))
            .ToList();
        var roots = reader.Roots.Select(id => reader.Read(id)!).Select(t => new TermCrumb(t.Id, t.Label, t.Symbol)).ToList();
        var header = new TraceHeader(run.RunId, address.Case, address.Scenario, address.Measure, method, (after ?? before)!.Depth,
            [.. origins.Select(o => o.Side)], origins, roots);
        return new Opened(reader, header);
    }

    /// <summary>The traces an output holds, or none when it is not a trace.</summary>
    IEnumerable<TraceEntry> Entries(TypedDataRef reference)
    {
        if (reference.ContractId != TraceSet.ContractId && reference.ContractId != CalculationTrace.ContractId) return [];
        var content = data.Read(reference) switch
        {
            DataLookup.Found found => found.Content,
            DataLookup.Expired => throw new TraceRefused("TraceExpired", $"{reference.DataId} has expired after its retention and is not recomputed."),
            _ => throw new TraceRefused("TraceUnavailable", $"{reference.DataId}@{reference.Revision} is not retained."),
        };
        try
        {
            if (reference.ContractId == TraceSet.ContractId)
                return (content.Deserialize<TraceSet>(WireJson.Options) ?? throw new JsonException("The trace set is null.")).Traces;
            var trace = content.Deserialize<CalculationTrace>(WireJson.Options) ?? throw new JsonException("The trace is null.");
            return [new TraceEntry(BaseCase, BaseScenario, After, trace.Measure, trace)];
        }
        catch (JsonException invalid)
        {
            throw new TraceRefused("TraceInvalid", $"{reference.DataId} is not a {reference.ContractId} {reference.ContractVersion}: {invalid.Message}");
        }
    }
}

/// <summary>
/// <c>trace.read</c> (ADR-0011 §7): a term of a retained Run's trace, for the caller, as the read endpoint answers it.
/// It reads and never computes, and acts for a caller who must be allowed to see the Run, so it is a command
/// (effect <c>ReadRun</c>, ADR-0005) and never a workflow step.
/// </summary>
public sealed class TraceRead(Func<TraceService> traces) : ICapabilityHandler
{
    public const string CapabilityId = "trace.read";
    public const string Version = "1.0";
    public const string Request = "trace-read-request";
    public const string Answer = "trace-term";

    public CapabilityDescriptor Descriptor { get; } = new(CapabilityId, Version, "Read a term of a Run's calculation trace",
        [new("request", Request, Version, Cardinality.One)], [new("term", Answer, Version, Cardinality.One)],
        ExecutionKind.RequestResponse, [CapabilityEffect.ReadRun]);

    public Task<IReadOnlyDictionary<string, JsonNode?>> InvokeAsync(CapabilityInvocation invocation, CancellationToken cancellationToken)
    {
        var caller = invocation.Caller ?? throw new CapabilityFailure("NoCaller", "trace.read reads a Run for its caller, and was given none.");
        var request = invocation.Inputs["request"] as JsonObject ?? throw new CapabilityFailure("InvalidInput", "A request is required.");
        string? Text(string name) => request[name]?.GetValue<string>();
        var order = Text("order") switch
        {
            null or "formula" => ChildOrder.Formula,
            "size" => ChildOrder.Size,
            var other => throw new CapabilityFailure("InvalidInput", $"order is formula or size, not {other}."),
        };
        try
        {
            var answer = traces().Read(new TraceAddress(Text("runId")!, Text("case") ?? TraceService.BaseCase, Text("scenario") ?? TraceService.BaseScenario,
                Text("measure")!), caller.UserId, Text("path"), order, request["offset"]?.GetValue<int>() ?? 0, request["limit"]?.GetValue<int>() ?? 50);
            return Task.FromResult<IReadOnlyDictionary<string, JsonNode?>>(new Dictionary<string, JsonNode?>
            {
                ["term"] = JsonSerializer.SerializeToNode(answer, WireJson.Options),
            });
        }
        catch (TraceRefused refused)
        {
            throw new CapabilityFailure(refused.Code, refused.Message);
        }
    }
}

/// <summary>Registers <c>trace.read</c> and its request. The service is reached only when invoked: it is built from the registry.</summary>
public sealed class TracesModule(Func<TraceService> traces) : IWorkbenchModule
{
    public void Register(CompositionRegistryBuilder builder)
    {
        builder.AddCapability(new TraceRead(traces));
        builder.AddContract(new ContractDefinition(TraceRead.Request, TraceRead.Version, "Trace term", new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["runId"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["title"] = "Run" },
                ["case"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["title"] = "Case", ["description"] = "The case priced; base when omitted." },
                ["scenario"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["title"] = "Scenario", ["description"] = "base when omitted." },
                ["measure"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["title"] = "Measure", ["description"] = "A measure that registers a trace, such as ccr.spotCapital." },
                ["path"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["title"] = "Term", ["description"] = "The term's id; the first root when omitted." },
                ["order"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("formula", "size"), ["title"] = "Children's order",
                    ["description"] = "size puts the term that moved most first, or with one side priced the largest." },
                ["offset"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0m, ["title"] = "First child" },
                ["limit"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1m, ["maximum"] = (decimal)TraceService.MaxPage, ["title"] = "Children" },
            },
            ["required"] = new JsonArray("runId", "measure"),
            ["additionalProperties"] = false,
        }));
    }
}
