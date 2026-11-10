using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;
using Serilog.Sinks.OpenTelemetry;
using Qube.Composition.Application;

namespace Qube.Web;

/// <summary>
/// Logs through Serilog; traces and metrics through OpenTelemetry (ADR-0014). Logs go to the console and to a daily
/// file under App_Data/logs always, and with traces and metrics over OTLP when <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> names
/// a collector. A log written inside a request or a Run carries its trace id, so a log line finds its trace. Nothing
/// here knows a domain: the kernel speaks <c>ILogger</c>, <c>ActivitySource</c> and <c>Meter</c>.
/// </summary>
static class Observability
{
    public const string ServiceName = "qube";

    public static void AddObservability(this WebApplicationBuilder builder)
    {
        var otlp = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        var version = typeof(Observability).Assembly.GetName().Version?.ToString() ?? "unknown";
        var environment = builder.Environment.EnvironmentName;
        var logs = LogDirectory(builder.Configuration, builder.Environment);

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

        // Spans are made whether or not they leave the process, so every log line has a trace id to be found by.
        var telemetry = builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(ServiceName, serviceVersion: version)
                .AddAttributes([new("deployment.environment.name", environment)]))
            .WithTracing(t => t
                .AddSource(QubeTelemetry.Name)
                .AddAspNetCoreInstrumentation(o => o.Filter = context => context.Request.Path.StartsWithSegments("/api"))
                .AddHttpClientInstrumentation())
            .WithMetrics(m => m
                .AddMeter(QubeTelemetry.Name)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation());
        if (otlp is not null) telemetry.UseOtlpExporter();
    }

    /// <summary>
    /// One line per request that failed: the panes poll every couple of seconds, so a success is counted by the HTTP
    /// metrics rather than written; a refusal (4xx) is a warning and a server failure an error.
    /// </summary>
    public static void UseRequestLogging(this WebApplication app)
    {
        app.UseSerilogRequestLogging(o => o.GetLevel = (context, _, failure) =>
            failure is not null || context.Response.StatusCode >= 500 ? LogEventLevel.Error
            : context.Response.StatusCode >= 400 ? LogEventLevel.Warning
            : LogEventLevel.Debug);
    }

    /// <summary>Says once, at startup, where telemetry goes, so its absence is never mistaken for silence.</summary>
    public static void LogDestination(this WebApplication app)
    {
        var otlp = app.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        var logs = LogDirectory(app.Configuration, app.Environment);
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Qube.Observability");
        if (otlp is null) logger.LogInformation("Telemetry: logs to the console and {LogDirectory}; traces and metrics are not exported (OTEL_EXPORTER_OTLP_ENDPOINT is not set).", logs);
        else logger.LogInformation("Telemetry: logs, traces and metrics over OTLP to {OtlpEndpoint}; logs also to {LogDirectory}.", otlp, logs);
    }

    static string LogDirectory(IConfiguration configuration, IHostEnvironment environment) =>
        configuration["Qube:LogDirectory"] ?? Path.Combine(environment.ContentRootPath, "App_Data", "logs");
}
