using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.RegularExpressions;
using Qube.Composition.Application;

namespace Qube.Web;

/// <summary>
/// One event the browser reports (ADR-0014): an uncaught error, a call the server failed, or a notice the user was
/// shown. <c>Traceparent</c> is the W3C trace context the client sent with the call it is about, so the event joins
/// that call's trace.
/// </summary>
public sealed record ClientEvent(string Level, string Kind, string Message, string? At = null, string? Url = null, string? Traceparent = null, string? Stack = null);

/// <summary>A page's events, sent together. <c>Session</c> names one page load.</summary>
public sealed record ClientEventBatch(string Session, IReadOnlyList<ClientEvent> Events);

/// <summary>
/// Takes the browser's events into the server's log, under the category <c>Qube.Client</c>, so client and server
/// are read, kept and alerted on in one place. The page is not trusted to be small: a batch and every text in it are
/// bounded, and what is too long is cut rather than refused, so an error is never lost for its size.
/// </summary>
static partial class ClientTelemetry
{
    public const int MaxEvents = 50;
    public const int MaxText = 4000;

    static readonly Counter<long> Events = QubeTelemetry.Meter.CreateCounter<long>("qube.client.events",
        description: "Events the browser reported, by level and kind.");

    [GeneratedRegex("^[a-z][a-z-]{0,39}$")]
    private static partial Regex KindPattern();

    public static void MapClientTelemetry(this WebApplication app)
    {
        app.MapPost("/api/telemetry/client", (ClientEventBatch batch, HttpContext http, ILoggerFactory loggers) =>
        {
            if (Problem(batch) is { } problem) return Results.Json(new Api.ErrorEnvelope("ValidationFailed", problem, []), statusCode: 400);
            var logger = loggers.CreateLogger("Qube.Client");
            var agent = Clip(http.Request.Headers.UserAgent.ToString(), 300);
            foreach (var e in batch.Events)
            {
                using var joined = Join(e);
                using (logger.BeginScope(new Dictionary<string, object?>
                {
                    ["ClientSession"] = batch.Session, ["ClientUrl"] = Clip(e.Url), ["ClientAt"] = e.At, ["ClientUserAgent"] = agent, ["ClientStack"] = Clip(e.Stack),
                }))
                {
                    logger.Log(LevelOf(e.Level), "Client {ClientKind}: {ClientMessage}", e.Kind, Clip(e.Message));
                }
                Events.Add(1, new("qube.client.level", e.Level), new("qube.client.kind", e.Kind));
            }
            return Results.Accepted();
        });
    }

    /// <summary>Why a batch is refused, or null. Only its shape is refused; long texts are cut.</summary>
    public static string? Problem(ClientEventBatch batch)
    {
        if (string.IsNullOrWhiteSpace(batch.Session) || batch.Session.Length > 64) return "A batch names its session in 1 to 64 characters.";
        if (batch.Events.Count is 0 or > MaxEvents) return $"A batch holds 1 to {MaxEvents} events.";
        foreach (var e in batch.Events)
        {
            if (e.Level is not ("error" or "warning" or "info")) return $"An event's level is error, warning or info, not '{Clip(e.Level, 20)}'.";
            if (!KindPattern().IsMatch(e.Kind ?? "")) return "An event's kind is 1 to 40 lower-case letters or hyphens.";
        }
        return null;
    }

    /// <summary>Puts the event in the trace of the call it is about, so its log line carries that trace id.</summary>
    static Activity? Join(ClientEvent e) =>
        e.Traceparent is not null && ActivityContext.TryParse(e.Traceparent, null, out var context)
            ? QubeTelemetry.Source.StartActivity($"client {e.Kind}", ActivityKind.Internal, context)
            : null;

    static LogLevel LevelOf(string level) => level switch { "error" => LogLevel.Error, "warning" => LogLevel.Warning, _ => LogLevel.Information };

    static string? Clip(string? text, int max = MaxText) => text is null || text.Length <= max ? text : text[..max] + "…";
}
