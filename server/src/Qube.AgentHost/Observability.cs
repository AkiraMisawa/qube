using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Formatting.Compact;
using Serilog.Sinks.OpenTelemetry;

namespace Qube.AgentHost;

/// <summary>
/// The agent host's logs, traces and metrics (ADR-0014), as the workbench's: Serilog for logs, OpenTelemetry for
/// traces and metrics, over OTLP when <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set. The host references no workbench project
/// (C44), so this is its own copy. Its file log is kept under the agent's own home, beside the SDK's state. The
/// workbench's calls carry their trace context, so a turn's spans here sit in the workbench's trace.
/// </summary>
static class Observability
{
    public const string ServiceName = "qube-agent-host";

    public static void AddObservability(this WebApplicationBuilder builder)
    {
        var otlp = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        var version = typeof(Observability).Assembly.GetName().Version?.ToString() ?? "unknown";
        var environment = builder.Environment.EnvironmentName;
        var logs = LogDirectory(builder.Configuration);

        builder.Services.AddSerilog(log =>
        {
            log.ReadFrom.Configuration(builder.Configuration)
                .Enrich.FromLogContext()
                .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj} {TraceId}{NewLine}{Exception}")
                .WriteTo.File(new CompactJsonFormatter(), Path.Combine(logs, $"{ServiceName}-.clef"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14);
            if (otlp is not null)
                log.WriteTo.OpenTelemetry(o =>
                {
                    o.Endpoint = otlp;
                    o.Protocol = OtlpProtocol.Grpc;
                    o.ResourceAttributes = new Dictionary<string, object>
                    {
                        ["service.name"] = ServiceName, ["service.version"] = version, ["deployment.environment.name"] = environment,
                    };
                });
        });

        var telemetry = builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(ServiceName, serviceVersion: version)
                .AddAttributes([new("deployment.environment.name", environment)]))
            .WithTracing(t => t.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation())
            .WithMetrics(m => m.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddRuntimeInstrumentation());
        if (otlp is not null) telemetry.UseOtlpExporter();
    }

    /// <summary>Says once, at startup, where telemetry goes.</summary>
    public static void LogDestination(this WebApplication app)
    {
        var otlp = app.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        var logs = LogDirectory(app.Configuration);
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Qube.AgentHost.Observability");
        if (otlp is null) logger.LogInformation("Telemetry: logs to the console and {LogDirectory}; traces and metrics are not exported (OTEL_EXPORTER_OTLP_ENDPOINT is not set).", logs);
        else logger.LogInformation("Telemetry: logs, traces and metrics over OTLP to {OtlpEndpoint}; logs also to {LogDirectory}.", otlp, logs);
    }

    static string LogDirectory(IConfiguration configuration) =>
        configuration["AgentHost:LogDirectory"]
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "qube", "agent-host", "logs");
}
