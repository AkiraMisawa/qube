using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Qube.Composition.Application;

/// <summary>
/// The workbench's own traces and metrics (ADR-0014), in the BCL's types, so the kernel takes no telemetry package: the
/// host decides whether they are collected and where they go. Business identifiers ride as tags, so a RunId, a
/// workspace or an agent thread finds its trace. Telemetry explains; the Run's record stays the authority (docs/05).
/// </summary>
public static class QubeTelemetry
{
    /// <summary>The name of the activity source and the meter, which a host subscribes to.</summary>
    public const string Name = "Qube";

    public static readonly ActivitySource Source = new(Name);
    public static readonly Meter Meter = new(Name);

    /// <summary>Runs finished, by workflow, origin and status.</summary>
    public static readonly Counter<long> RunsCompleted = Meter.CreateCounter<long>("qube.runs.completed", description: "Runs finished, by workflow, origin and status.");

    /// <summary>From a Run's acceptance to its completion.</summary>
    public static readonly Histogram<double> RunDuration = Meter.CreateHistogram<double>("qube.run.duration", unit: "s", description: "From a Run's acceptance to its completion.");

    /// <summary>Command invocations (ADR-0005), by capability, origin and outcome.</summary>
    public static readonly Counter<long> Commands = Meter.CreateCounter<long>("qube.commands", description: "Command invocations, by capability, origin and outcome.");

    /// <summary>Tag names, one vocabulary for spans, metrics and log scopes.</summary>
    public static class Tags
    {
        public const string RunId = "qube.run.id";
        public const string RunStatus = "qube.run.status";
        public const string Origin = "qube.origin";
        public const string WorkflowId = "qube.workflow.id";
        public const string WorkspaceId = "qube.workspace.id";
        public const string CapabilityId = "qube.capability.id";
        public const string StepId = "qube.step.id";
        public const string Outcome = "qube.outcome";
        public const string ErrorCode = "qube.error.code";
    }
}
