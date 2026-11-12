using System.Diagnostics.Metrics;
using Qube.Composition.Application;

namespace Qube.Agent;

/// <summary>The agent loop's tags and metrics (ADR-0014), on the workbench's own source and meter.</summary>
static class AgentTelemetry
{
    public const string ThreadId = "qube.agent.thread.id";
    public const string RunId = "qube.agent.run.id";
    public const string Runtime = "qube.agent.runtime";
    public const string Model = "qube.agent.model";
    public const string Tool = "qube.agent.tool";

    /// <summary>Agent runs, by runtime and outcome (finished, cancelled, runaway, failed).</summary>
    public static readonly Counter<long> Runs = QubeTelemetry.Meter.CreateCounter<long>("qube.agent.runs",
        description: "Agent runs, by runtime and outcome.");

    /// <summary>Tool calls the loop invoked for the agent, by tool and outcome.</summary>
    public static readonly Counter<long> ToolCalls = QubeTelemetry.Meter.CreateCounter<long>("qube.agent.tool_calls",
        description: "Tool calls the loop invoked for the agent, by tool and outcome.");
}
