# ADR-0014: Logs, traces and metrics from the client and the server, collected and monitored in one place

Status: PROPOSED 2026-10-28 (numbered ADR-0008 until 2026-11-09, when it reached main after ADR-0008 had gone to the generic create command). Raised by the owner: "make the app log properly on both the client and the server,
collected so it can be monitored" (2026-10-28), and "Serilog for the server, maybe" (C49).

## Context

The server wrote ASP.NET Core's console log and nothing of its own: no service used `ILogger`, a Run's execution left
no trace, and an exception other than a capability's refusal left a Run `running` forever with nothing said. The
browser reported nothing at all. The company's monitoring product is not known (OPEN), so whatever is built must reach
it without a rewrite, and the workbench must run at home without it.

## Decision

1. **OpenTelemetry's wire (OTLP) is the way out.** The workbench and the agent host export logs, traces and metrics
   over OTLP when `OTEL_EXPORTER_OTLP_ENDPOINT` names a collector, and say at startup where their telemetry goes. Most
   monitoring products take OTLP or sit behind a collector that does, so replacing the backend is configuration.
2. **Logs go through Serilog** (C49): to the console, to a daily compact-JSON file (`App_Data/logs` for the workbench;
   the agent host's own home for the host, beside its SDK state, C44), and over OTLP. Serilog stamps each event with
   the current trace and span, so a log line finds its trace. A successful request is not written (the pages poll);
   a refusal is a warning and a server failure an error, and the HTTP metrics count all of them.
3. **Traces and metrics go through the OpenTelemetry SDK**: ASP.NET Core and HttpClient instrumentation, .NET runtime
   metrics, and the workbench's own source and meter, named `Qube`. The kernel speaks only the BCL's
   `ActivitySource` and `Meter` and the `ILogger` abstraction; the hosts choose Serilog and OpenTelemetry. Spans:
   `run {workflow}`, `invoke {capability}`, `command {capability}`, `agent run`, `agent tool {name}`. Metrics:
   `qube.runs.completed`, `qube.run.duration`, `qube.commands`, `qube.agent.runs`,
   `qube.agent.tool_calls`, `qube.client.events`. Business identifiers ride as tags and log properties
   (`qube.run.id` / `RunId`, `qube.workspace.id`, `qube.agent.thread.id`, …), so a RunId finds its
   logs and trace. Telemetry explains; the Run's record stays the authority (docs/05).
4. **The browser reports to its own server, not to a collector.** No SDK: every call carries a fresh W3C
   `traceparent`, and the page sends its events in batches to `POST /api/telemetry/client`, which writes them into the
   server's log as `Qube.Client` in the trace of the call they are about. Reported: uncaught errors, unhandled
   rejections, render errors with their component stack, calls the server failed (5xx) or never answered, and the
   notices the user was shown (since 2026-11-11 the refused ones only, ADR-0006's note 2 of that day). A refusal
   (4xx) is not reported by the page; the server's log already has it. The
   endpoint bounds a batch (50 events) and cuts long texts rather than refusing them; the page bounds what it holds
   (200 events, the first kept, the dropped counted) and never reports its own reporting's failure. The browser needs
   no route to the collector and no credential of its own.
5. **Words of a conversation are not logged.** An agent run's span and logs carry its ids, runtime, tools and outcome,
   never the user's or the model's text.
6. **A Run that fails for a reason of the workbench's own ends failed** (`InternalError`), logged with its RunId, instead
   of being left running.
7. **Locally, one container collects and shows it all** (DEFAULT): `grafana/otel-lgtm` 0.36.0 (an OpenTelemetry
   Collector, Loki, Tempo, Prometheus and Grafana), started by `tools/observability/compose.yaml` on ports bound to
   this machine only, with a provisioned *Qube* dashboard: Runs finished and failed, server and browser
   errors, requests and latency by route, Runs by workflow and status, commands and agent runs by outcome, errors and
   warnings from both services, the browser's events, recent and failed spans, and one Run's logs and trace by its id.

## Consequences

- Package versions: OpenTelemetry.Extensions.Hosting and Exporter.OpenTelemetryProtocol 1.19.1; Instrumentation
  .AspNetCore, .Http and .Runtime 1.19.0; Serilog.AspNetCore 10.0.0; Serilog.Sinks.OpenTelemetry 4.2.0; the kernel
  takes Microsoft.Extensions.Logging.Abstractions 10.0.0 only.
- Logging levels move from `Logging` to `Serilog:MinimumLevel` in appsettings.
- A trace that starts in the browser has no root span in Tempo ("root span not yet received"): the browser makes a
  trace context but exports no spans. Its server spans are all there.
- Alert rules and their notification channel are not set up; the dashboard shows the state. The company's backend,
  retention and alerting are OPEN (docs/13).
- The client endpoint takes events from any page that reaches the server; with sign-in (C30) it takes the user's.

## Rejected

- **The OpenTelemetry JavaScript SDK in the browser, exporting to the collector.** It would put the collector on the
  browser's network path with CORS and a credential, for page spans nobody has asked for yet; errors and failed calls
  are what monitoring needs first. It can be added later behind the same `traceparent`.
- **A hosted error service (Sentry and the like).** A third party receiving company pages' errors is a policy
  decision, and it would be a second place to look.
- **OpenTelemetry's own logging provider instead of Serilog.** Equivalent on the wire; the owner prefers Serilog, which
  also writes the local file without a collector.
