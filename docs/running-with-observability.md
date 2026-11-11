# Logs, traces and metrics

The workbench and the agent host always log to the console and to a daily file. With a collector, they also send
logs, traces and metrics over OTLP, and Grafana shows them together ([ADR-0014](adr/0014-logs-traces-and-metrics-from-client-and-server-in-one-place.md)).

## Without anything else

| Where | What |
|---|---|
| Console | One line per event: `[time level] category: message trace-id` |
| `server/src/Qube.Web/App_Data/logs/qube-YYYYMMDD.clef` | The workbench's log as compact JSON (CLEF), 14 days kept. `Qube:LogDirectory` moves it. |
| `~/Library/Application Support/qube/agent-host/logs/` (macOS) | The agent host's log, under the agent's own home. `AgentHost:LogDirectory` moves it. |

A CLEF file reads with `jq` (`jq -c 'select(."@l" == "Error")' qube-*.clef`) or opens in Seq.

## With the local monitoring stack

Docker is needed. Start the stack once; it keeps its data in a volume across restarts.

```sh
docker compose -f tools/observability/compose.yaml up -d
```

Then start the workbench (and the agent host) with the collector's address:

```sh
export OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317
nix develop -c dotnet run --project server/src/Qube.Web --urls http://localhost:5080
# or, with the AI on Copilot: tools/run-with-copilot.sh --model <id>   (the host gets the same setting)
```

Each process says at startup where its telemetry goes. Open Grafana at http://localhost:3000 (first login `admin` /
`admin`). The home dashboard is **Qube**:

- **At a glance**: Runs finished and failed, server errors (5xx) and browser errors in the selected range.
- **HTTP**: requests and p95 latency by route.
- **Runs, commands and the agent**: Runs by workflow and status, Run duration, commands and agent runs by outcome.
- **Logs and traces**: errors and warnings from both services, the browser's events, recent Runs and failed spans.
- **One Run**: type a RunId in the *Run* box at the top to see its logs and its trace.

Grafana's *Explore* reads any of it directly. Useful queries:

| Source | Query | Finds |
|---|---|---|
| Loki | `{service_name="qube"} \| RunId="run-…"` | Every log line of one Run |
| Loki | `{service_name="qube"} \| scope_name="Qube.Client"` | What the browser reported |
| Loki | `{service_name=~"qube.*"} \| severity_number >= 13` | Warnings and errors |
| Tempo | `{ span.qube.run.id = "run-…" }` | One Run's trace |
| Tempo | `{ span.qube.agent.thread.id = "…" }` | An AI conversation's runs and tool calls |
| Prometheus | `qube_runs_completed_total` | Runs finished, by workflow, origin and status |

A log line's details hold its `trace_id`; Grafana links it to the trace. A trace begun by the browser shows
"root span not yet received": the page makes the trace context but sends no spans of its own.

Stop the stack with `docker compose -f tools/observability/compose.yaml down` (add `-v` to forget what it kept).

## What the browser reports

Uncaught errors, unhandled promise rejections, render errors (with the component stack), calls the server failed
(5xx) or never answered, and the refused notices the user was shown: a result that was done is not reported (ADR-0006's
note 2 of 2026-11-11). Each call to the server carries a `traceparent`, so an event about a failed call is in that
call's trace. A refusal (4xx) is in the server's own log as a warning.

## Not set up

Alert rules and where they would notify; the company's monitoring backend, retention and access (docs/13). Pointing
`OTEL_EXPORTER_OTLP_ENDPOINT` at another collector is all the workbench needs.
