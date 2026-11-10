using System.Diagnostics.Metrics;
using Workbench.Composition.Application;

namespace Workbench.Agent;

/// <summary>The agent loop's tags and metrics (ADR-0014), on the workbench's own source and meter.</summary>
static class AgentTelemetry
{
    public const string ThreadId = "workbench.agent.thread.id";
    public const string RunId = "workbench.agent.run.id";
    public const string Runtime = "workbench.agent.runtime";
    public const string Tool = "workbench.agent.tool";

    /// <summary>Agent runs, by runtime and outcome (finished, cancelled, runaway, failed).</summary>
    public static readonly Counter<long> Runs = WorkbenchTelemetry.Meter.CreateCounter<long>("workbench.agent.runs",
        description: "Agent runs, by runtime and outcome.");

    /// <summary>Tool calls the loop invoked for the agent, by tool and outcome.</summary>
    public static readonly Counter<long> ToolCalls = WorkbenchTelemetry.Meter.CreateCounter<long>("workbench.agent.tool_calls",
        description: "Tool calls the loop invoked for the agent, by tool and outcome.");
}
