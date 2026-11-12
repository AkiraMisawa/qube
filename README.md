# Qube

A keyboard-first workbench in which capabilities, workflows, views and context are composed rather
than hard-wired into pages ([ADR-0002](docs/adr/0002-the-product-is-a-keyboard-first-workbench-and-the-generic-slice-comes-first.md)).
Start with `START_HERE.md` and `docs/00-decisions.md`; what runs today is in
[`IMPLEMENTATION_STATUS.md`](IMPLEMENTATION_STATUS.md).

## Layout

| Path | Contents |
|---|---|
| `server/` | ASP.NET Core host and the composition kernel (`Qube.slnx`, .NET 10) |
| `client/` | React + TypeScript client (Vite, Mantine, TanStack Query, Zustand, ExGrid React) |
| `docs/` | Requirements, ADRs (`docs/adr/`), plans and reviews |

## Commands

The toolchain (.NET SDK 10.0.203, Node 26.12.1) comes from the Nix flake. Run each command from the
repository root, or prefix it with `nix develop <root> -c`.

```sh
# Server: build and the xUnit suite (composition, validation, API)
nix develop -c dotnet test server/Qube.slnx

# Client: install, typecheck, unit tests (Vitest)
nix develop -c bash -c 'cd client && npm ci && npm run typecheck && npm test'

# Client build: written into server/src/Qube.Web/wwwroot, which the host serves
nix develop -c bash -c 'cd client && npm run build'

# Run the workbench on http://localhost:5080 (after the client build). Workspaces, Runs and results are
# kept in server/src/Qube.Web/App_Data/qube.db unless Qube__Database names another file.
nix develop -c dotnet run --project server/src/Qube.Web --urls http://localhost:5080

# Development, both at once: the host on :5080 and Vite on http://localhost:5173 (open this one; it reloads on
# save and forwards /api to the host). Ctrl-C stops both. --port moves the host.
tools/dev.sh

# Development: Vite on http://localhost:5173, forwarding /api to the host above
nix develop -c bash -c 'cd client && npm run dev'

# Browser tests (Playwright); start the host themselves unless one is already on the port
nix develop -c bash -c 'cd client && npm run build && npx playwright test'
```

Logs go to the console and to `server/src/Qube.Web/App_Data/logs`. To collect logs, traces and metrics from the
client and the server in Grafana, start `docker compose -f tools/observability/compose.yaml up -d` and run the
workbench with `OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317`; see
[`docs/running-with-observability.md`](docs/running-with-observability.md).

The AI pane runs on the scripted agent (no model) by default. To run it on Copilot, with the agent in a process of its
own, and to open the workbench from a phone or another Mac over Tailscale, see
[`docs/running-with-copilot.md`](docs/running-with-copilot.md) (`tools/run-with-copilot.sh --model <id>`, once for
each model the AI may use; the first is a new conversation's until the user sets a default model).

A user's settings (ADR-0017) are kept by the host in the same database, for the user `Qube:DemoUser` names until
sign-in: the theme, column widths by grid, the command ranking, the AI's default model where models are configured,
and the defaults a new workspace starts with (the pricer's display currency, a Rates line's currency, notional and
direction). `settings` on the command line, or Settings in the header's account menu, lists them with where each
value comes from, `settings <name> <value>` sets one, and `settings reset <name>` puts the registered value back; the
AI has the same three commands.

Playwright uses its own Chromium (`npx playwright install chromium`). Where a Chromium is already
installed for another Playwright version, name it with `PLAYWRIGHT_CHROMIUM=/path/to/chrome`.
`QUBE_PORT` moves the host to another port. Each browser run starts the host on a database file of its own in the
temporary directory (`QUBE_DB` names one instead).

ExGrid comes from npm at one exact version, 0.2.0 (`@ex-grid/core`, `data`, `react`, `mantine`, `chart`, `chart-react` and `chart-mantine` in `client/package.json`). To move it, set all seven to the same new version and run `npm install` in `client/`; the packages must move together.

## Coming from before the rename

Until 2026-11-10 the application was called Quant Workbench and its identifiers said so (C50). On a machine that ran
it then, these carry its local state over:

- **Database.** `App_Data/workbench.db` is `App_Data/qube.db` now. Rename the file (and its `-wal` and `-shm` files,
  if it has them). The host refuses to start beside the earlier file alone, so it never starts empty next to your data.
- **Configuration.** `Workbench:*` keys are `Qube:*` (`Qube__Database` in the environment), and the host refuses to
  start while an earlier key is set. `WORKBENCH_PORT`, `WORKBENCH_DB` and `WORKBENCH_API` are `QUBE_PORT`, `QUBE_DB`
  and `QUBE_API`; these three are not checked, so an earlier one is ignored.
- **Copilot token.** The Keychain item is `qube-copilot`. `tools/run-with-copilot.sh` still reads the earlier item,
  `quant-workbench-copilot`, and says so each time, until `--store-token` has stored the new one.
- **Agent host.** Its folder is `qube/agent-host` under the user's local application data (it was
  `quant-workbench/agent-host`). Move the folder to keep the SDK's state and the earlier logs, or leave it: a new one
  is made.
- **Observability.** The compose project is `qube-observability`, the services report as `qube` and
  `qube-agent-host`, and the instruments are `qube.*`. `docker compose -p quant-workbench-observability down -v`
  removes the earlier containers and what they collected.
- **Browser.** The command line's usage ranking starts again (its storage key changed).

## Coming from before the user's settings (2026-11-11)

- **Copilot models.** `Agent:Copilot:Model` is now `Agent:Copilot:Models`, a list (`Agent__Copilot__Models__0`, `__1`
  …); the workbench stops at startup while only the earlier key is set, and says so. `tools/run-with-copilot.sh`
  takes `--model` once for each.
- **Browser.** The theme and the command ranking a browser kept are the user's now. The ranking is added to the
  user's once, the first time that browser meets the server, and its copy removed; the theme the browser kept draws
  the first frame only. Column widths dragged before were kept per panel and are not carried over.

## Rules

`CLAUDE.md` holds the non-negotiable requirements. Everything committed is in English.
