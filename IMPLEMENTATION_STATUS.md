# Implementation status

Last updated 2026-10-27. Commands are in [README.md](README.md).

## Slice 01: the generic workbench

Plan: [docs/plans/slice-01-generic-workbench.md](docs/plans/slice-01-generic-workbench.md).

### What runs

- **Server** (`server/`): the composition kernel.
  - A capability registry, plus strict JSON contracts that refuse unknown members and code-like bindings.
  - Workflow and view validation.
  - A bounded DAG executor with `forEach` maps. Failed items are kept as unavailable, never zero.
  - Runs with idempotency keys, retained typed data references, and registered row actions (`openView`, `runWorkflow`).
  - HTTP API: catalog, runs, data read, action invoke, and definition validation.
- **Client** (`client/`):
  - The command line (Ctrl/⌘+K, or `/` outside a text field or grid; ADR-0006). Commands are English words (`run station summary`, `set region South`); those from the catalog appear without a client change.
  - A context bar that keeps the last valid context in force.
  - A run dialog generated from the input contract.
  - Panels per Run or per opened view.
  - Declarative views rendered through ExGrid, with row actions in an ExGrid Action Column.
  - Light and dark themes.
  - Shell keys only: focus grid, edit context, next/previous panel, close panel (ADR-0006 §8). The theme is the `toggle theme` command.
- **Sample module**: four synthetic capabilities (stations, readings, summary). They are non-financial on purpose, to show that the kernel knows nothing of XVA.

### Verification performed

| Suite | Result |
|---|---|
| `dotnet test server/Workbench.slnx` | 26 passed |
| `npm test` (Vitest) | 16 passed |
| `npx playwright test --repeat-each=3` (Chromium, headless, Linux) | 15 passed (5 tests × 3) |

The Vitest suite covers forms against contracts, view binding, command ranking and refusal, the workspace store, and palette commands built from the catalog.

| Acceptance | Status |
|---|---|
| CO01 | Passed (xUnit: a module added without kernel change; Vitest: its workflow gets a palette command) |
| CO02 | Passed (xUnit) |
| CO03 | Application part passed (xUnit). Browser part not yet: no Playwright test reads an expired reference. |
| CO04 | Application part passed (xUnit). Browser part partial: a context rebound by keyboard reaches the next Run, but the earlier Run is not yet checked as unchanged in the browser. |
| CO05 | Passed (Playwright: Show readings opens a view over retained data; Summarise this station runs a workflow) |
| CO07 | Passed (xUnit) |
| UX01 (slice part) | Passed in Chromium |
| UX03 (slice part) | Passed: dark and light themes; no sideways scroll at 390 px |

Not verified: Edge and Firefox, Windows and macOS, touch (UX02), a real phone or tablet, and performance.

### Providers

Every provider is simulated. All data is synthetic and labelled as such on screen ("synthetic data", "simulated"). No real model, market data source or company service is connected.

### ExGrid

- `@ex-grid/core`, `@ex-grid/data`, `@ex-grid/react` and `@ex-grid/mantine` `0.2.0-beta.3` from npm (since 2026-11-04; before that, tarballs packed from ex-grid commit `3bf523d` in `vendor/ex-grid/`). Since 2026-11-06 (ADR-0009) every grid is `WorkbenchGrid`, ExGrid drawn by its Mantine Wrapper; the stylesheets are `@ex-grid/core/styles.css` and `@ex-grid/mantine/styles.css`.
- A key sent to a newly drawn grid before it carries its Settled Mark is lost, by ExGrid's own contract. Browser tests wait for the mark, as ex-grid's own tests do.
- ExGrid's TypeScript core has no touch support yet.

### Known limits

- Storage was in memory in slice 01; slice 02 moved Runs, retained data and workspaces to SQLite.
- There is no AG-UI or AI path yet. No authentication.
- Palette focus is handled by the shell itself: opening the palette and choosing a command let go of the focused element first. Keys typed before the next surface has the keyboard are dropped, never delivered to the grid behind or to a closing palette.

## Slice 02: the XVA workspace and the Pricing DB

Plan: [docs/plans/slice-02-xva-workspace.md](docs/plans/slice-02-xva-workspace.md). Decisions: ADR-0003 §6 (resolved), §7 (built as proposed, still OPEN for the owner's review), C30 to C36 in `docs/00-decisions.md`.

### What runs

- **Generic workspace kernel** (`Workbench.Composition.Application/Workspaces`): an append-only edit log, overrides with their base, the carry check (carried, conflicted, lost), conflict groups, `DropScope` with the confirmed count, an atomic counterparty change (`ChangeScope`), and `Freeze` refused while a priced entity holds an unresolved edit. Every command names the revision it was taken against.
- **SQLite** (`Workbench.Persistence.Sqlite`, Microsoft.Data.Sqlite 10.0.14 with SQLitePCLRaw 2.1.13): one file holds generic tables for every domain: `Workspace` (current state), `EditLog` (append-only, `domain_kind` per entry), `AnalysisRun`, `SubmissionKey` and `RetainedData`. A workspace is restored after a restart from its source descriptor, and a descriptor that now acquires another version is refused. Deleting a workspace keeps its log. A Run left running by a stopped process is recorded as failed with code `Interrupted`.
- **Runs** record their owner, origin (`pricer`, `ai`, `workflow`, `action`), explicit relations (workspace and revision, parent Run, analysis group, conversation) and the result contracts their workflow declares. Idempotency keys are per owner and survive a restart. A submission under a used key is the same one when its whole content is, or, when the caller names an idempotency basis, when that is: a portfolio Price is named by the workspace revision priced, because its accepted input freezes cells that answer on their own (PV01), so a retry after one answered, or after the workspace moved on, is the first Run, and the key on another revision is refused (XV13).
- **Synthetic XVA module** (`Workbench.Samples.Xva`): three registrations (two share code ABC under different legal entities), agreements with CSAs and one uncollateralised (`nocsa_MA-003`), up to 100,000 generated existing trades per counterparty (matured ones are absent at a later date, and the last fixing moves with the date), three draft trades that belong to no counterparty, an empty CVA recovery on LE-01. The rules: a new trade takes the main CSA, a curve follows its CSA (including a CSA curve edit) unless the user set it, an uncollateralised trade starts on the profile curve, an ineligible CSA is an issue on the row and Price refuses it. A simulated CVA/FVA provider prices Before and After independently and falls back to the CDS recovery when the CVA recovery is empty.
- **HTTP**: `/api/xva/...` creates a workspace, pages effective rows (sorted on the server, Blank last as ExGrid's ADR-0023), edits, resolves held edits, moves the value date, previews and confirms a counterparty change, prices and deletes. `/api/history/...` serves the Pricing DB.
- **Pricing DB**: every Run newest first; a module registers a history projection; the XVA view adds value date, registration, trade counts, CVA and FVA Before, After and incremental. An unknown domain is refused.
- **Client**: the XVA workspace page (ExGrid over a fetching source with a Row Key; user-set, held and invalid cells painted as Cell States with their reasons; a counterparty change that shows what it drops by edit kind and asks first; Price as a button and the `Price XVA workspace` command) and the Pricing DB page (one domain at a time, `Open` opens a Run). `new xva`, `open pricing db` and `price` are commands (their chords were removed by ADR-0006).

### Verification performed

| Suite | Result |
|---|---|
| `dotnet test server/Workbench.slnx` | 58 passed |
| `npm test` (Vitest) | 21 passed |
| `npx playwright test` (Chromium, headless, Linux) | 7 passed; the two new XVA tests also with `--repeat-each=3` (6 passed) |

| Acceptance | Status |
|---|---|
| XV01, XV15, XV16, C24 | Passed (xUnit; Playwright shows the main CSA and the following curve) |
| Before and After independent (CLAUDE.md) | Passed (xUnit) |
| ADR-0003 §6: saved per edit, frozen at Price, survives a restart | Passed (xUnit on SQLite). The browser reload case is not tested yet. |
| ADR-0003 §6: counterparty change asks before dropping | Passed (xUnit and Playwright) |
| ADR-0003 §7: carried, lost, Price refused while unresolved | Passed (xUnit). A conflict cannot arise in the synthetic XVA data yet, since no editable field changes between value dates; it is covered on the kernel only. |
| Pricing DB | Passed (xUnit; Playwright: a Price lands in the list with origin `pricer` and in the XVA view with its figures) |
| EX06, a failed case shown unavailable in the browser | Not yet |

### Measurements

Release build on this Linux container, one request at a time with `curl`, at portfolio sizes of 10,000 and 50,000 requested existing trades (7,000 and 35,137 alive on 2026-10-26). Recorded, not gated; no target is agreed.

| Step | 10,000 | 50,000 |
|---|---|---|
| Create the workspace | 199 ms | 317 ms |
| First page of 120 rows, unsorted | 26 ms | 2 ms |
| A page in the middle under a new sort (first sort of that order) | 44 ms | 133 ms |
| Edit one trade | 24 ms | 11 ms |
| Edit a CSA's curve, reaching every trade on it | 46 ms | 149 ms |
| Move the value date (re-acquire and carry) | 114 ms | 336 ms |
| Price accepted (freeze, build and store the accepted input) | 295 ms | 963 ms |

Rows without an edit of their own are computed once per source version and set of CSA curve edits; a sorted order is kept per state. Scrolling in the browser at these sizes was not measured.

### Providers

Every provider is simulated and every figure synthetic, labelled `simulated` in the result and the Pricing DB.

### Known limits

- The trades grid does not filter: a filter set from a column header is refused with a message, not ignored. Paste is not wired.
- Requests act as one configured demo user (`Workbench:DemoUser`) until ME-ID sign-in (C30); roles (C31) are not enforced, so a user sees only their own Runs.
- Run views are matched to Runs by output name, not by contract.
- Touch waits for ExGrid's touch release (C36).

## Slice 03: the Rates line pricer

Plan: [docs/plans/slice-03-rates-line-pricer.md](docs/plans/slice-03-rates-line-pricer.md). Decisions: C37 to C43 in `docs/00-decisions.md`, ADR-0004 (accepted 2026-10-27).

### What runs

- **Kernel (ADR-0004)**: entities the user creates (`Create`, `Remove`), sources a module derives from the workspace (`IEditRules.Derive`, one scope per line), a Derive log entry with its carry summary whenever a derived source moves, and derivation again on load.
- **Rates module** (`Workbench.Samples.Rates`): a line's flat fields (C40, C43) read into typed terms; a resolver that refuses what it cannot honour on the row; a standard schedule generator; a schedule per line as a derived source whose periods are identified by leg and unadjusted dates, so cashflow edits carry, conflict or are lost when the terms move.
- **Pricing**: `IRatesPricingProvider` with the fake provider (`fake-rates/1`, every figure `simulated`): PV, the par rate or fair spread of the leg left without a quote, and 1bp pillar deltas when the row asks for risk, standing in for the engine's AD sensitivities (C38). A seeded synthetic market whose snapshot n is a function of its configuration.
- **Price** (`rates.price`): freezes the revision the user saw, prices its priceable lines against the snapshot the request names (an unknown one is refused), lists every other line as `notPriced` with its reasons, and refuses when nothing is priceable. One Run per Price.
- **Live**: subscriptions price the newest market with at most one calculation in flight and the newest update waiting; an accepted edit moves a subscription to its revision; a frame names its subscription revision, input revision and snapshot. No Run per tick. Frames stream as server-sent events. A workspace has one subscription on the server; pages attach to it (a new page first hears the frame shown now) and detach when they go. Once nobody has watched it for `Rates:LiveIdleStopSeconds` (300 by default), the next tick stops it and the workspace records why; a page opening on it then shows the last frame and offers Resume, which starts a new subscription on the current revision (owner, 2026-10-27).
- **Capture** (`rates.capture`): saves the frame the user saw as a Run, with its own snapshot, refused (`FrameMoved`) when another frame is shown by then.
- **Commands (ADR-0005, accepted 2026-10-27)**: the kernel's `EditWorkspace` and `Subscribe` effects mark a capability as a command, invoked through `CommandService` for a `Caller` the server sets (user and origin), outside Runs, with an idempotency key (held in memory); the validator refuses a command as a workflow step. Edit log entries record their origin (a nullable `origin` column, added to an older log on open). The commands are the kernel's `entity.create` (one entity of a described product from the command line's short terms, in a workspace of the caller's; ADR-0008 §5), `rates.workspace.create` (an empty Rates workspace at a valuation date), `rates.live.start` (starts or joins the workspace's subscription) and `rates.live.stop`; the live status says whether the page or the AI started and stopped it. They are reached at `POST /api/agent/commands/{capability}/{version}`, the route the agent's tool calls take, with origin `ai`; no agent runtime calls it yet.
- **HTTP**: `/api/rates/market`, `/api/rates/workspaces/...` (create, view, add, edit, remove a line, cashflows and cashflow edits, resolutions, valuation date, price, start live, delete), `/api/rates/live/{id}/frames|capture`.
- **Pricing DB**: the Rates view lists one row per Run: valuation date, price or capture, snapshot, lines priced, failed and not priced, execution mode.
- **Client**: the Rates page (`new rates`). One ExGrid row per line, keyed by line, with Header Groups over Trade, Leg 1, Leg 2, Terms and Result: the inputs first and the result after them (owner, 2026-11-04); the line and its actions (Flows, ✕) are pinned. Each column is at least as wide as its header reads whole with its menu and sort mark (`gridHeaderRequiredPx`). Notionals are typed and shown in millions (`Notional (mm)`, owner 2026-11-04; the server keeps units): `1000` is a billion, `3bn` and `500mm` say their own scale (`m` is months, never millions, ADR-0008 §2), and a bare number of a million or more is refused as a full amount typed out of habit. `new irs` and `new ccs` read a notional the same way. A new IRS is at the money unless a rate or spread is given (C47): spread 0 on the floating leg, the fixed leg at par, for `add line irs`, `new irs` and the AI's `entity.create` alike. Cells the user set, held edits, lines that cannot be priced and figures of an older revision are painted as Cell States with their reasons. Each leg's second level (C43) opens and closes from the toggle ExGrid draws on the leg's Header Group (ExGrid ADR-0400, since `@ex-grid/*` 0.2.0-beta.3); a collapsed leg that hides a value the user set wears ExGrid's hidden mark. Cashflows opens a second ExGrid over the line's periods, whose dates, notional and rate are editable, with its notional exchanges below. Start live and Stop live, Capture, and Price (against the snapshot shown) are buttons and the commands `price`, `live start`, `live stop` and `capture`. The edit queue the XVA page used is now generic and shared.
- **Configuration**: convention profiles and the synthetic market are configuration (`Rates` section). None is configured by default. `appsettings.Development.json` carries the convention table proposed on 2026-10-27, still unconfirmed, on a weekends-only calendar: its profile version says `unconfirmed` and every row names its profiles.

### Verification performed

| Suite | Result |
|---|---|
| `dotnet test server/Workbench.slnx` | 130 passed (the 8 command tests of ADR-0005 included) |
| `dotnet run` (Development) | Started; `/api/rates/market` advanced with the ticker |
| `npm test` (Vitest) | 28 passed |
| `npx playwright test` (Chromium, headless, Linux) | 9 passed; the Rates spec again after the commands (2 passed) |

| Acceptance | Status |
|---|---|
| A line typed by keyboard prices live, captures and prices once (C40, docs/07 §3) | Passed (Playwright) |
| Manual and live agree on the same input and market (docs/07 acceptance 4) | Passed (xUnit) |
| An edit moves the live subscription to its revision; capture refuses a frame no longer shown (spine 6) | Passed (xUnit) |
| Cashflow edits carry, conflict or are lost when terms move (ADR-0004) | Passed (xUnit). Not yet driven in the browser. |
| The AI's commands create a priceable line, refuse missing terms by name, join the page's subscription and are logged as the AI's (ADR-0005) | Passed (xUnit, over HTTP). No agent runtime drives them yet. |
| Touch, tablet and phone layouts | Not verified; waits for ExGrid's touch release (C36) |

### Providers

The provider and the market are simulated. Real holiday calendars, the confirmed convention table and a real worker are not wired.

### Known limits

- The convention table, the risk factors to report against and the meaning of "FX Forward (pts)" are open with the owner.
- The Pricing DB lists Rates Runs, not a row per line.
- Snapshots are kept for the life of the process.
- A collapsed leg's hidden values are priced whether shown or not; its hidden mark says so.
- The result columns stand after the inputs, so on a laptop-width screen they are scrolled to (ExGrid pins columns on the left only); a strip above the grid shows the focused line's status, PV and par or fair spread, or the only line's, at any width (owner, 2026-11-04). Its following the Focus between lines is not yet driven in the browser.
- On a phone the pinned Line and action columns take about half the grid's width.
- Every header carries ExGrid's column menu and sort mark, which set the narrowest a column can be (the Line and action columns included).
- A Price result opens as a Run; it does not fill the line grid's figures, which only live frames do.
- Per-line risk (the deltas) is in the Run's Risk grid only; the line grid shows PV and the solved quote.
- A command's idempotency keys live for the process: a retry after a restart acts again.
- Fixed 2026-11-04: a page's edit queue now follows a revision someone else moved once the page shows it (the AI adding a line, another page), so the next edit is no longer refused as `RevisionMoved` and dropped (Vitest `editQueue.test.ts`; Playwright `ai.spec.ts` "spine 6", which fails without the fix). XVA's page takes the same queue.
- A line the AI creates holds its terms as the line's own values, so the page does not mark them as the user's edits; the log says they came from the AI.

## The command line (ADR-0006, accepted 2026-10-27)

### What runs

- One command line, opened with Ctrl+K anywhere and with `/` where `/` would not be typed (not in a text field, a grid, or the AI chat, whose slash commands are its own). Commands are English words for what they do; typing shows the matching commands, best first: words typed in full, words that start with the query, words whose starts follow the query in order (`op pr` → `open pricing db`), then words or titles that contain it. Among equal matches what the user ran most and lately comes first, so a few letters find it; `?` lists what can run now.
- Each command shows what it acts on (the active panel) and why it cannot run if it cannot. A chosen command shows its arguments as slots: required ones in order, optional ones as `name=value`. The slot being typed lists its valid values; Tab takes one, Enter runs, and Enter with a slot missing names it and runs nothing.
- `new irs <terms…>` and `new ccs <terms…>` (ADR-0008, accepted 2026-11-06, scoped to describing IRS and CCS): one generic create in the kernel (`entity.create`, `ProductService`, `ProductReader`), with each product a description (`ProductDescriptor`) served through a catalog port (`IProductCatalog`; here the Rates module writes `rates.irs` and `rates.ccs`, at the company the engine's catalog would be read in by an adapter). Words in any order, each read by its form (`new irs jpy 10y 3bn rec`, `new irs 5y`, `new ccs 5y usd jpy 100 15bn pay spread2=-30bp`); a word that fits no field or two is refused by name, never guessed; `name=value` sets anything. What is left out comes from, in order, the convention profile's template (`Standard` in settings; the development profiles' templates are an unconfirmed proposal, as their conventions are), the workspace's defaults (`set defaults currency=USD notional=500 direction=pay`), and C47; whatever is still missing is refused by name. Every defaulted field is recorded on the `Create` edit with its source, and its cell says `Default, from …`. The economic check stays the Rates module's (`LineResolver`): a CCS with no basis spread reads, and is still refused. The command line completes each word from the description (`GET /api/products?app=rates`) and shows how each was read; the server reads them again (`POST /api/rates/workspaces/{id}/lines/new`, `POST /api/products/{id}/preview`). `add line <irs|ccs>`, `price` (XVA or Rates, by the active panel), `live start`, `live stop`, `capture`, `new xva`, `new rates`, `open pricing db`, `run <workflow>`, `show <view>`, `set <context field> <value>`, `toggle theme`.
- Pricing is found by product (ADR-0008 §8): a provider declares the products it prices (`IRatesPricingProvider.Products`; the fake declares `rates.irs` and `rates.ccs` 1.0.0), and a line of any other product is not sent to it but listed as not priced with that reason, in Price and in live alike.
- Not done for ADR-0008, by its scope: the adapter that reads the company engine's catalog, and a generic leg-by-leg structure for products no description covers.
- `ai <request>` is a command, and Enter on a line that matches no command asks the AI with what was typed (the AI pane below).
- Domain chords (New XVA, Price XVA, New Rates, Pricing DB, theme) are gone.

### Verification performed

| Suite | Result |
|---|---|
| `npm test` (Vitest) | 36 passed (ranking, slot reading, completion, missing arguments, the AI's rest-of-line) |
| `dotnet test server/Workbench.slnx` | 131 passed (the terms endpoint at the page's revision, logged as the page's) |
| `npx playwright test` (Chromium, headless, Linux) | 10 passed; the new command-line test and the two rewritten ones with `--repeat-each=3` (9 passed) |

### Known limits

- Frequency ranking is kept in the browser until the user settings store arrives with sign-in (C30).
- `new ccs` with terms is not there yet: `add line ccs` adds an empty line to fill in the grid.
- New XVA and New Rates still open their dialogs rather than taking their arguments on the line.

## The AI pane (ADR-0007, proposed 2026-10-27)

### What runs

- **AG-UI** (`Workbench.Agent`, `/api/agent/...`): the pane posts AG-UI's `RunAgentInput` to `POST /api/agent/runs` and reads the run's events as server-sent events, in the shapes of `@ag-ui/core` 1.0.3 (protocol 1.0). The server holds the conversation per thread and owner and takes only new user messages from the client; frontend tools and client-written history are refused. A run goes on when its reader leaves; `GET /api/agent/threads/{t}/runs/{r}/events?after=n` reads the rest, `GET /api/agent/threads/{t}` reads the messages, `POST …/cancel` stops a run before its next tool call. The same run id posted again is read again, never run twice. An agent run is not a workbench Run.
- **Tools**: one per registered command capability (ADR-0005), named by its id with `_` (`entity_create`), whose parameters are its input contracts. A call goes through `CommandService` for `Caller(user, ai)` with the key `agent:{thread}:{toolCall}`; its result is the outputs or every issue of the refusal.
- **Runtime port** `IAgentRuntime`, chosen by `Agent:Runtime`; anything but a configured runtime stops startup. The only runtime is `scripted` (**no model**, labelled `scripted · no model` in the pane and on every card): modules register the requests it understands. Rates registers a new workspace (a valuation date, when none is open or the request says "new workspace"), an IRS (written as the words `new irs` takes and created through `entity.create`, so what the request leaves out comes from the same defaults as on the command line), `start ticking` and `stop ticking`. It passes on only stated terms, stops at a refusal and asks for what it names; the answer is joined to the refused request unless it asks for that thing afresh, so "pay fixed and start ticking" completes the swap, in the workspace the first turn made, and ticks it.
- **Copilot runtime** (`Agent:Runtime=copilot`, C44, C46): `CopilotAgentRuntime` posts each turn to the **agent host** (`Workbench.AgentHost`, a process of its own on `127.0.0.1:5395`, behind a shared key) and reads its answer as JSON lines (words, a tool call, the end, a failure); each call is invoked by the loop for the user and its result posted back, and cancelling a run aborts the host's turn. The host runs `GitHub.Copilot.SDK` 1.0.27 (CLI 1.0.104) in its empty mode: no built-in tool, only the workbench's tools, every other permission rejected, one session per thread. Its token comes from its own environment or a file; the workbench is started without it. `tools/run-with-copilot.sh` starts both, optionally with the host as another OS user; `docs/running-with-copilot.md` says how, and how to reach it over Tailscale.
- **Client** (`client/src/ai/`): the AI pane on the right (a full-screen drawer below 768 px), one card per request with each tool call, its outcome and the reply, a runtime badge, and the chat input with its own `/` commands (`/new`, `/stop`, `/help`, `/close`; the command line's `/` stays out of the chat). `ai <request>` and `open ai pane` / `close ai pane` are commands, and the header has an AI button. The active panel's app gives the context (an open Rates workspace); a result's app effect opens the workspace a new line is in **behind** the active panel, with a Show button on the card, and refreshes what live start and stop changed. Nothing the AI does moves the keyboard.

### Verification performed

| Suite | Result |
|---|---|
| `dotnet test server/Workbench.slnx` | 145 passed (7: commands through AG-UI, asking for missing terms and completing them, read again rather than run twice, client tools and history refused, an invented tool refused, cancel, an unknown runtime stops startup; 7 for Copilot with a fake model behind the real host: a model's call runs as the user's command, the conversation goes with each turn, an invented tool is refused back to the model, cancel aborts the model's turn, a model failure ends the run with its message, the host refuses a caller without the key and will not start without one, `copilot` without its settings stops startup) |
| `tools/run-with-copilot.sh` in the container | The host and the workbench start; the SDK's runtime starts; a run with a made-up token ends in `RUN_ERROR` carrying the SDK's message ("No GitHub OAuth token or Copilot HMAC key provided"). No real model was reached. |
| `npm test` (Vitest) | 42 passed (6 new: cards from events, a failed run, the request sent alone with its context, an effect applied once, busy refusals, chat commands) |
| `npx playwright test` (Chromium, headless, Linux) | 13 passed; the three AI tests with `--repeat-each=3` (9 passed). Every event of a run is parsed with `@ag-ui/core`'s own schemas. |

| Acceptance | Status |
|---|---|
| AI01 (real agent) | Not met yet: the Copilot runtime is built, but no real model has answered through it (needs the owner's token, on the owner's machine). |
| AI04 (reconnect) | Application part passed (xUnit: a run is read again and from a position, never run twice). A browser reconnect is not tested. |
| AI06 (no unregistered path) | Passed for tools (xUnit: client tools refused, an invented tool refused without running). Views are not generated by the agent yet. |

### Known limits

- The Copilot runtime has not met a real model. With a made-up token the SDK reports "No GitHub OAuth token or Copilot HMAC key provided"; whether a real fine-grained token gets past that is the first thing to check on the owner's machine.
- The company's GitHub App (C45) is not wired: its hour-long installation token would need the host's SDK client restarted to refresh.
- The SDK's session lives in the host's memory; a host restart starts a new session, told the conversation so far as text.
- Threads and runs are held in memory; a restart loses them (the commands' effects are in the workspace log as before).
- A tool result card shows the command's outputs as JSON.
- The agent cannot run workflows (Runs) or read data yet; it has the three Rates commands.
- XVA registers no command, so the agent cannot act on an XVA workspace.

## Design guidelines and the header (ADR-0009, proposed 2026-11-06)

### What runs

- `docs/design-guidelines.md`: Control Height, alignment, icons, the shell, colour, touch and keyboard, responsive layout, and how to check a UI change, each rule with its source.
- The header is three zones of 30px controls: title and Commands; Region and As of with their labels beside them; AI and theme, with icons from `@tabler/icons-react` 3.50.0. It is one row of 44px from 992px and two rows of 82px below; it no longer covers the top of a panel on a phone.
- An invalid context field is marked in the header and explained above the panels (`ContextIssues`).
- The focus ring follows `:focus-visible`.
- Every grid is drawn by `@ex-grid/mantine` (`WorkbenchGrid`): the theme's colours in both schemes, Mantine's menus and filter panel, and the cells in the face ExGrid has widths for. The workbench takes the same face (Mantine's default system stack); the hand-written `--ex-` bridge is gone.

### Verification performed

| Suite | Result |
|---|---|
| `npm run typecheck`, `npm test` (Vitest) | passed, 42 tests |
| `npx playwright test e2e/workbench.spec.ts --grep ADR-0009 --repeat-each=3` (Chromium, headless, Linux) | 12 passed (4 widths × 3) |
| `npx playwright test` (Chromium, headless, Linux), after the switch to `@ex-grid/mantine` | 17 passed |

Screenshots were looked at by hand at 390, 768, 992, 1024 and 1440px in both themes, and with an invalid As of; the XVA grid and its column menu in both themes after the switch. Not checked on a real phone or tablet.

### Known limits

The guidelines' §12: the Rates toolbar's rows on a phone, the Action Column's browser-grey buttons, the Wrapper's filled disabled menu items, a context message that repeats its field's name, and touch sizes unchecked on an iPhone (UX02).

## The XVA page, interim fixes and its redesign (ADR-0010, proposed 2026-11-07)

### What runs

- The slice-02 page follows docs/design-guidelines.md: toolbar labels beside their inputs at one 30px Control Height, a field's message said once below the toolbars, neutral metadata in gray, column widths that hold their headers and the largest synthetic notional (it fell to `####`), and a trades grid that fills the window's remaining height.
- An empty CVA recovery shows empty, with the CDS recovery as its placeholder. It showed the literal `undefined`, because the server leaves nulls out and the page compared with `null` only (XV15 on screen).
- The latest Price's CVA and FVA (Before, After, incremental) show on the page, marked simulated, with the revision they priced and whether the workspace has moved since; *Open run* opens the Run panel, which Price no longer opens by itself.
- ADR-0010 records the redesign decided with the owner: one screen (context bar, full-width input tabs, New and Existing trades, a result dock), Before and After as layers over Original with side-only edits in an advanced mode, cases (vary from a cell, iterative), scenarios crossed with cases and defined by command and by the AI, FVA only on imperfectly collateralised CSAs, PV Adj from PV01 × Margin. None of it is built yet.

### Verification performed

| Suite | Result |
|---|---|
| `npm test` (Vitest) | 55 passed |
| `npx playwright test` (Chromium, headless, Linux) | 18 passed; `xva.spec.ts` with `--repeat-each=3` (6 passed) |
| Screenshots at 390, 768, 992 and 1440px, dark and light | No sideways scroll; looked at by hand |

## The pricer page, ADR-0010 Delivery step 1 (tickets 01–08, 2026-11-08)

### What runs

- **A portfolio domain module** (`Workbench.Samples.Portfolio`) holds what XVA, SIMM and SA-CCR will share: the context,
  netting set, CSAs with their terms, trades with B and A, Original, and the **measures register** (§10). XVA registers
  CVA (with CVA Charge and PV) and FVA (§11). The composition kernel gained nothing domain-specific.
- **Measures**: `measures cva fva` (or the page's toggles) selects what Price prices; Price says what it will run
  (`Price CVA FVA · 1 case`) and freezes the selection; a new workspace takes its owner's last one. A refused measure
  blocks only itself. What a measure adds to the page is read from its registration: its trade columns, its membership
  column on the trade and CSA grids, the shared inputs it needs (CVA's recovery shows on Netting only while CVA is
  selected), and whether it has a quote (Copy quote, CVA's). The page names no measure.
- **Netting and CSA tabs**: CSA terms editable (type and Main are the source's), Main and MA columns; FVA's
  imperfect-CSA rule decided on the server with a reason per CSA and per trade (`In · CSA-B imperfect: one-way`), its
  version frozen into the Run; an uncollateralised trade is out with FVA 0. An empty CVA recovery stays empty (XV15).
- **New trades above Existing trades**; B and A for a whole grid (`before all|none`, one edit); After-only Price
  (Before not priced, no Diff, never 0).
- **Original**: a value that differs from it is marked and says what Original held; a value edited back is not. A PV
  Adj that Margin filled is marked like a typed one; PV01, a result, never is.
- **PV01, Margin, PV Adj** on new trades: PV01 pending, its figure, or failed with `pv01 retry`; PV Adj = PV01 × Margin,
  read-only then and saying so; Price refuses XVA's measures by name while a Margin's PV01 is pending or failed.
- **The result dock**: per case Before, After and Diff per priced measure, in the currency the Run froze (named on the
  dock); collapsed to a line a case; `download run` (a zip of the Run's record, input, result and lineage) and `copy
  quote` (HTML and text, read from the Run's retained result on the server).
- **One screen** on every width: context bar, input tabs, New and Existing grids and the dock; part heights set by drag
  or keys and never read back; below `sm` three tabs. A command opens each input tab; `refresh` and `currency` too.
- **Fixed on the way**: a command that carries a revision (Price, Refresh, a value-date move, Rates' Price) now waits
  for the edits already sent (`EditQueue.after`), since Price could overtake an edit and be refused with 409; the light
  theme's coloured and dimmed text now reads at 4.5:1 (`autoContrast` and `cssVariablesResolver`, design-guidelines §7).

### Verification performed

| Suite | Result |
|---|---|
| `dotnet test server/Workbench.slnx` | 210 passed |
| `npm run typecheck`, `npx vitest run` | passed, 83 tests |
| `npx playwright test` (Chromium, headless, Linux) | 26 passed; the three `xva.spec.ts` tests the review changed with `--repeat-each=3` (9 passed) |
| Screenshots at 1440, 992, 768 and 390px, dark and light, before the review's fixes; at 1440 in the dark theme after them | no sideways scroll; at 1440 × 900 no page scroll; looked at by hand |

Code review (`/code-review`, a Standards and a Spec reviewer in parallel) found theme spacing written as literals,
dimmed text the user must read, a made-up `?? ''` and eight `!` in the command list, measure ids written into the page,
a PV Adj from Margin left unmarked, no currency on the dock, and the CSA grid without Main and MA; all fixed. The new
check that a tab is as high as its content found the CSA tab scrolling by 24px since ticket 03: the grid's Selection
Summary sat under its viewport. The CSA grid shows none (a sum of terms in different currencies is no figure).

### Providers

Every figure is simulated and says so: XVA's pricing (`FakeXvaPrice`, a toy formula), PV01 and the FX. No real
provider is wired.

### Known limits

- No column filters, and no *in FVA* filter, on the trade grids yet (§6).
- The Before and After fills wait on ExGrid's Split Fill (ex-grid ADR-0490, PR #545); until step 3 every change
  differs on both sides and wears `modified`.
- No Credit tab or Settings section (step 5); no *Add CSA*; the CSA type is the source's and read-only (DEFAULT); the
  trades show no start date.
- PV is a metric of CVA's (DEFAULT): the request that prices CVA values the portfolio.
- The module columns (PV01, Margin, PV Adj) are on the New grid only; an existing trade shows its official PV.
- docs/03 §2.5 OPEN: a new trade with no PV Adj is priced without an adjustment, and the Run does not yet say so.
- `TAKEN` (260px) is the page's estimate of its fixed parts, and a grid's Selection Summary line is outside its
  viewport with a height ExGrid's Grid Metrics do not give, so the Existing grid's height is approximate (no page
  scroll at 1440 × 900; some room is left under the dock).
- Left from the review as they are: statuses and "no curve" as strings, the field switches repeated across the
  portfolio module, `RegisteredMeasure` and `RegisteredColumn` repeating their definitions, the two-argument `Validate`
  used only by Price, and `Pv01Provider.Settled` used only by tests.

## Spot CCR capital, KVA and the calculation trace (ADR-0011, ADR-0013, proposed 2026-11-07)

### What runs

- The kernel's calculation trace (ADR-0011): kinds with an expression and TeX, the states value, absent, unavailable
  and notReturned, reconciliation of every term against its children, and a set of traces per (case, scenario, side,
  measure) as the `calculation-traces` contract.
- SA-CCR in full (CRE52 as of 2023-01-01) and SA's risk weight resolved at run time from the counterparty (CRE20,
  CRE21), in `Workbench.Samples.Ccr`, writing a trace to trade level in which every term reconciles.
- `ccr.spotCapital` registers into the pricer page: membership (a trade SA-CCR can decompose), its part of the
  request (the counterparty's reference data, the CSAs' margining, the collateral, the FX and the settings, read from
  the synthetic sources), an SA-CCR class column on new trades, and validation that refuses it by name while CVA and FVA stand. KVA registers as XVA's
  measure. Selecting either asks XVA's request with its KVA option on (ADR-0013 §2), which returns spot capital and
  its trace per side, a capital profile, and KVA from that profile, all marked simulated. After alone is priced and
  traced on After.
- `ccr.spotCapital` is also a capability of its own, giving the same figures.
- A Run's trace is read a term at a time (ADR-0011 §5): `GET /api/runs/{run}/cases/{case}/traces/{measure}` answers a
  term on each side the case priced with its Diff, slots, leaf sets, path, parameters and a page of its children;
  `…/leaves` pages and sorts a term's leaf rows; `GET /api/traces/catalogs/{method}/{version}` gives the kinds'
  formulas. `trace.read` (effect `ReadRun`, a command, ADR-0005's note of 2026-11-08) answers the same for the AI.
  Only the Run's owner reads it.
- The trace view (ADR-0011 §6, ticket 07) opens from a traced figure of the result dock, and by `explain <measure>
  [path]` on the XVA page or on a trace. On desktop the tree of terms is a `WorkbenchGrid` with a pinned Template
  Column (indent and open/close), its Before, After, Diff, unit, state, governed and citation columns, beside the
  selected term's formula typeset by KaTeX 0.19.1 (pinned exactly, strict, trust for `\htmlData` alone, fonts bundled
  by Vite): the symbols, then each priced side's values, then the Diff, each operand selecting its term. A decision
  shows its steps and reasons, a parameter its source, a matrix the row the lookup took. A term with leaf rows shows
  them in a grid paged and sorted by the server, a side at a time, with each row's membership and reconciliation; the
  row at the Focus has its column formulas typeset. Alt+→ and Alt+← open and close (ADR-0011 §6's note), and
  `focus grid` gives the tree the keyboard. Below `sm` the view drills in: the breadcrumb, the formula, then the
  children as a list. A case priced on After alone has no Before or Diff column and says so once.

### Providers

- Simulated: the fake XVA provider's KVA option, through the reference SA-CCR and SA-RW. The PoC's choices where the
  rule leaves it to the bank are ADR-0013 §7's DEFAULTs (netting sets by CSA, remargining days, haircuts, JPY as the
  bank's domestic currency, V = 0 for a new trade, the profile's dates, KVA's formula and a 10% cost of capital). The counterparties' reference data and
  collateral are synthetic (`PortfolioCredit`).

### Verification performed

| Suite | Result |
|---|---|
| `dotnet test server/Workbench.slnx` | 378 passed (five runs in a row after the XVA branch's retry fix, XV13), among them the BCBS SA-CCR worked examples, a case per rule, the KVA option and the trace read over HTTP |
| `npm run typecheck`, `npm test` (Vitest) | Clean; 119 passed, among them the formula's typesetting (strict, untrusted commands refused), the tree's rows and `explain` |
| `npx playwright test` (Chromium, headless, Linux) | 29 passed; `trace.spec.ts` with `--repeat-each=10` (30 passed), and again with `--repeat-each=5` after the review fixes (15 passed): both sides by pointer, keyboard and `explain`, After alone, and 390px drill-in with 4.5:1 text in both themes |
| Screenshot of a Price with every measure selected, 1600px, dark | Spot capital, RWA, EAD and KVA in the dock, Trades 5 → 8; looked at by hand |
| Screenshots of the trace at 390, 768, 992 and 1440px, dark and light | Root, risk weight's steps, its table, a netting set's collateral with its rows, PFE; no sideways scroll; looked at by hand |

### Known limits

- The trace view is a panel the page and `explain` open. It is not yet a component a declarative view can name
  (ADR-0011 §6's first sentence), nor opened from the Pricing DB, and a leaf row has no action to its trade's row on
  the page yet (§6's leaf set): the XVA page cannot yet be asked to show a trade.
- In the light theme ExGrid Mantine draws a marked heading's words in the primary, a colour made for a 3:1 line; the
  workbench sets the mark to the primary's text colour (`theme.css`). The Wrapper's default fails 4.5:1 with an
  amber primary, which is ex-grid's to fix.
- The SA-CCR class column is on the New grid only: the Existing grid takes no module columns yet.
- SA-CCR's inputs are not yet sections of the page (ADR-0013 §6): the counterparty's risk weight facts, the CSA terms
  only SA-CCR reads and the collateral come from the synthetic sources and cannot be edited, until the Credit tab
  (ADR-0010 Delivery step 5) takes them.
- A leaf row's `tradeInstanceId` is empty: XVA's request carries trades by their source id alone, though
  `contracts/xva-submission.schema.json` defines an instance id.
- κ (8%) and which products come first are OPEN with the owner (ADR-0013). Only IRS and FX forwards are decomposed
  (the synthetic portfolio has no other product); any other would be out of the measure's membership with its reason.

## Spot SIMM, step 1: the engine, CRIF and the two capabilities (ADR-0012, proposed 2026-11-07)

Spec: [docs/specs/spot-simm/spec.md](docs/specs/spot-simm/spec.md). Decisions: ADR-0012, with the trace of ADR-0011. Nothing in this step touches the page; registering spot IM there is step 2, on ADR-0010's measures register.

### What runs

- **Reference engine** (`Workbench.Samples.Simm`, `SimmEngine`): the ISDA SIMM v2.x aggregation for IR delta (the 12 tenors, sub-curves, cross-currency basis, concentration, `S_b` bounded by `±K_b`, `γ g_bc` across currencies) and FX delta (each currency's own CR, and `f_kl` between them), through the risk class and the Rates/FX product class to each regime, then the largest regime per direction. A regime is computed when the IM CSA serves it and a line it holds names it; a direction with none has no IM, not an IM of 0. Euler allocation down to the CRIF line, with each concentration factor held, so contributions add up exactly at every term and per trade. A currency the table places in no volatility group, or a tenor it lacks, is refused by line.
- **Parameters**: one table, `DEMO-2.x`, made up and labelled DEMO: risk weights, the tenor correlation matrix, the other correlations and the concentration thresholds. ISDA's calibration is not in the repository.
- **IM source** (`IImSource`, a port): `SyntheticImSource` holds one synthetic counterparty `ABC/LE-01` with IM CSAs `IMR-ABC-01` (collect: JFSA, CFTC) and `IMP-ABC-01` (post: JFSA, EMIR), two JPY IRS, a USD IRS, a USD/JPY CCS and a USD/JPY FX forward (exempt), all starting at spot on the value date. A company source replaces it in `Program.cs` and nowhere else.
- **CRIF** (`CrifGenerator`): from the Rates pricing provider at the value date. Pillar deltas are split onto SIMM's tenors linearly in years; only an overnight index maps to the OIS sub-curve, and any other curve is refused by name until a mapping is configured; FX delta is the USD value's change for a 1% stronger currency, with a cross-currency swap's principal exchanges left out (exempt under the BCBS-IOSCO framework, ADR-0012 §5); cross-currency basis is the change for 1bp more spread on the non-USD floating leg. Exempt trades, provider failures, rows the provider did not return and missing FX rates are excluded with their reason. The CRIF's header names its registration and value date.
- **Capabilities**: `im.source.freeze` (registration, value date → the trades frozen with their resolved terms, and the IM CSAs), `risk.crif.generate` (frozen trades, pinned market snapshot → the CRIF and each trade's coverage: its line count or why it has none) and `margin.simm.calculate` (a retained CRIF, the frozen IM CSAs and a loaded table → IM per direction, SIMM per regime, and the trace, whose leaf rows carry each line's contribution). `workflow.simm.crif` (freeze, then CRIF) is in the catalogue and runs from a form, and its Run keeps the frozen trades and IM CSAs; SIMM alone runs through `SimmModule.Calculate(crif, csas)`, a workflow naming what it reads, submitted to `POST /api/runs`. Refused by name: a table that is not loaded (at submission, by its field), a CRIF of another registration (`RegistrationMismatch`) and a line naming a regime the source does not know (`UnknownRegime`).
- **Trace**: the engine writes ADR-0011's trace as it computes, under kinds registered per table (`simm.im`, `simm.regime`, `simm.product`, `simm.riskClass`, `simm.delta`, `simm.bucket`, `simm.bucketSum`, `simm.factor`, and the leaf set `simm.crif`), with its request, value date and subject in the header. One root per direction marks its governing regime; parameters sit in their own scope, each risk weight with why it is what it is. The kernel's `children` gained an optional role so that `S_b` can sum the risk factors under `K_b` (ADR-0011 §2, DEFAULT).

### Verification performed

| Suite | Result |
|---|---|
| `dotnet test server/Workbench.slnx` | 418 passed, after merging the CCR branch at 7d0bf02 (SIMM: 24 engine, 8 sample cases, 10 CRIF, 7 over HTTP, 5 trace; and the kernel's new `children` case) |
| Client | Not touched, not run |

| Acceptance (spec "Done when") | Status |
|---|---|
| Every hand-worked case of ADR-0012 §6's formulas holds, and the sample portfolio gives the sample's figures | Passed (xUnit; the sample's figures come from its separate Python implementation) |
| A term's children's contributions add up to its own, and a regime's lines' to its SIMM | Passed (xUnit, engine and trace) |
| Both workflows run alone over the API and their Runs resolve their inputs and results | Passed (xUnit over HTTP: the CRIF's Run keeps the frozen trades and IM CSAs, and SIMM's names the CRIF and IM CSAs it read) |
| Every term the engine writes reconciles against its kind | Passed (xUnit, on the sample, a mixed portfolio and concentrated FX, and on the Run's trace over HTTP) |

### Providers

The Rates pricing provider is the fake one, so every CRIF and SIMM figure is `simulated`; the parameters are DEMO; the IM source is synthetic. No real SIMM engine, calibration or company source is wired.

### Known limits

- Vega, curvature and base correlation are not computed and have no kinds yet; `simm.addon` is registered when an add-on is first configured (ADR-0012 §6's note of 2026-11-08).
- The command line and the AI cannot run a workflow (their tools are commands only, ADR-0005 and ADR-0007): calling the capabilities from either needs a generic workflow tool in the kernel, a decision of its own. `workflow.simm.calculate` is not in the catalogue, because a form cannot pass it the retained CRIF and IM CSAs.
- Leaf rows carry the trade's source id, not a `tradeInstanceId`: the IM source has no trade instances. Within a Run, the frozen trades are that instance.
- The trace is read over HTTP as the CCR thread's endpoint reads any lone trace, on After of the base case: `GET /api/runs/{run}/cases/base/traces/simm.spotIm` (the measure's ID since step 2; it was `spot-im`), with `path` for a term and `…/leaves` for its CRIF lines (ADR-0011 §5). The page's trace view draws the step 2 Runs' traces; the SIMM tab's own drill-down is step 3.
- Only the synthetic counterparty exists; the regimes a trade counts under come from that source. Phase-in dates (ADR-0012 §2) are not modelled yet.

## Spot SIMM, step 2: spot IM on the pricer page (ADR-0012, 2026-11-08)

Spec: [docs/specs/spot-simm-page/spec.md](docs/specs/spot-simm-page/spec.md), tickets 01–04. Decisions: ADR-0012 §2, §3,
§4 and §7 with their notes of 2026-11-08, and ADR-0010 §7 and §10 with theirs. Seams agreed with the owner
(2026-11-08, "go with your recommendations"): the register in process, HTTP, and the client.

### What runs

- **IM CSAs and IM-R/IM-P** (ticket 01): the synthetic source gives each counterparty at most one IM CSA per direction
  with its regimes and the date each applies from (ABC/LE-01: IMR-ABC-01 under JFSA and CFTC, IMP-ABC-01 under JFSA
  and EMIR; ABC/LE-02 JFSA alone; MA-003 none). An existing trade's IM-R and IM-P are the source's; a new trade takes
  the counterparty's automatically. They are assignment edits, marked against Original and dropped with the
  counterparty as the CSA is. The source classifies each trade per direction and regime by its start date; an FX
  forward is exempt.
- **Spot IM registered** (ticket 02): measure `simm.spotIm` of module `simm` (`measures simm` selects it), metrics IM-R
  (headline, opens the trace at `collect`) and IM-P (at `post`). Membership per scope `{direction}/{regime}`, each in
  or out with why; the compact form `R: JFSA, CFTC · P: JFSA`. Inputs: IM-R and IM-P on both trade grids and the IM
  tab, shown only while spot IM is selected. Price refuses spot IM alone, by name (`SpotImRefused`), for a trade
  naming another IM CSA, a trade in scope that cannot be a Rates line, or IM CSAs naming a table not loaded; CVA is
  priced regardless.
- **SIMM's own request** (ticket 03): `simm.spotIm` reads the accepted input, prices Before and After on their own
  (CRIF from the side's trades in scope at the value date on the pinned market and FX, then the reference engine
  under the frozen IM CSAs), and writes the shared result (IM-R and IM-P in the display currency, each side's
  governing regime as its note, `SIMM PARAMETERS DEMO` as the basis), a trace per priced side and each side's CRIF
  (`crifBefore`, `crifAfter`). A trade in scope without a CRIF line makes its side `unavailable`, naming it; nothing
  in scope is `notPriced`, never 0.
- **The page** (ticket 04): the IM-R and IM-P columns beside the CSA on both grids, editable; the spot IM column with
  each scope's reason in its cell's message; the IM tab (`open im`), read-only, with a sentence for a counterparty
  without IM CSAs; Metrics with the regime before each figure and a `Spot IM · SIMM PARAMETERS DEMO` badge on the
  dock; an IM-R cell opens the trace at `collect`, an IM-P cell at `post`, After alone included.
- **Fixed on the way**: the executor dropped every workflow output after the first unusable one, so a failed XVA
  request would have hidden spot IM's figures; a Run now keeps every usable output and is still `failed` (docs/05,
  "preserve successful cases"; ADR-0010 §10). In the light theme a link (a Metrics figure that opens its trace) read
  at 2.7:1; it now takes the primary colour's ink. On a phone the five measure toggles were cut to `CV.` and
  `Spot I`; they now wrap on a row of their own. A reason under the Metrics for a side with no figure said
  `unavailable`, in red, where nothing had failed (a side with nothing in an IM scope); it now says `not priced`, and
  only a failed request is said in red.
- **Reviewed** (code-review, Standards and Spec, 2026-11-08) and fixed: a missing scope list or measure list in the
  accepted input is refused by name rather than read as empty; a side with nothing in scope says why per direction;
  more than one SIMM version, or one not loaded, is refused at Price and again in the request; a counterparty with
  no IM CSA says so in the tab and in the result; conventions moved after Price fail spot IM's request alone, by
  name, and CVA stands; the browser checks at all four widths, after deselection, and for a counterparty without IM
  CSAs. Left as they are, with the reason: the direction strings (`collect`/`post`) are the trace's paths and the
  CRIF's, one vocabulary; the portfolio's per-field lists for IM-R/IM-P follow the CSA's pattern, and folding them into
  the register waits until another assignment field needs it; `PriceSide` keeps its parameters explicit rather than
  hide them in a bag; the throwing two-argument `Validate` stays for the register's interface, documented.

### Verification performed

| Suite | Result |
|---|---|
| `dotnet test server/Workbench.slnx` | 448 passed: 13 spot IM cases in process (`SimmPricerTests`), 1 over HTTP (`SimmPricerApiTests`), the executor's half-failing workflow, and the registration counts updated |
| `npm run typecheck`, `npm test` (Vitest) | Clean; 127 passed, among them Metrics' notes, basis, term and reasons, and the scope text |
| `npx playwright test` (Chromium, headless, Linux) | 32 passed; `simm.spec.ts` with `--repeat-each=3` (9 passed): columns and IM tab while selected and gone once deselected, a wrong IM-R refused by name, Metrics with regime and DEMO, the trace at `collect` and `post`, After alone, a counterparty with no IM CSA not priced with why, 4.5:1 text in both themes at 1440, 992, 768 and 390px with no sideways scroll |
| Screenshots at 1440, 992, 768 and 390px, dark and light | IM tab, both grids' IM columns, the dock with regimes and the DEMO badge; looked at by hand |

The figures are held to their own consistency, not to hand-worked values (step 1 holds the engine to those): the IM is
the governing regime's SIMM, the display figure is the trace's USD amount at the frozen FX, a side moves only with its
own trades, and every term reconciles.

### Providers

The Rates pricing provider behind CRIF is the fake one, the parameter table is DEMO, and the IM CSAs and
classification are synthetic, so every spot IM figure is simulated and says so (`SIMULATED`, the DEMO badge). No real
SIMM engine, calibration or company source is wired; production takes spot SIMM from the XVA request's MVA option
(ADR-0012 §4), which this PoC does not have.

### Known limits

- The SIMM tab (the two directions' summaries and the drill-down in the owner's layout) was step 3, delivered below; a Metrics cell
  opened the trace view spot CCR capital uses until then.
- The Pricing DB's projection shows no spot IM (ADR-0012 OPEN): a Run that priced spot IM alone lists its date and
  counterparty only.
- The IM tab is read-only; IM CSAs as a what-if is OPEN. The phase-in dates are illustrative.
- Only an IRS becomes a Rates line; an FX forward is exempt, and any other product in scope is refused by name.
- The trade count beside Metrics is IM-R's (the headline's); IM-P's is on its own line of the result.
- Opening the IM tab while spot IM is not selected says so rather than opening; the page cannot name which measure
  would show it, since the server lists only the selected measures' inputs.

## Spot SIMM, step 3: the SIMM tab (ADR-0012 §7, 2026-11-08)

Spec `docs/specs/spot-simm-tab/` (tickets 01–04, done). The DEFAULTs are in the ADRs' notes of 2026-11-08: ADR-0010 §7
and §10, ADR-0011 §5 and §6, ADR-0012 §7 and ADR-0013 §6.

### What runs

- **Result tabs registered by a measure**: spot IM registers `SIMM` and spot CCR capital `CCR`, each drawing its trace;
  the pricer API lists them, and the dock shows Metrics and the tabs of the selected measures the Run priced. An IM-R
  or IM-P cell, a CCR cell, or `explain`, opens the measure's tab at its term; opening a tab widens the dock over the input
  tabs and the grids (owner's choice), and `Restore height` or `dock restore` puts it back. `open result <tab>` and
  `dock full` from the command line. A measure priced on no side (no IM CSA) says why in its tab. A Run opened from
  the Pricing DB keeps the separate trace panel.
- **The SIMM tab**: a card per direction (`IM-R · collect`, `IM-P · post`) with its IM CSA, Before, After, Diff and
  the regime that governs (from the regimes' own `governs`); the chosen card roots the tree beside the formula. Above
  the cards, the rate the Metrics' figures were converted at from USD, said once from a `conversion` column of the
  shared result. CCR's tab has one root and no cards.
- **Trades under a term** (generic, in the kernel): a leaf set names its contribution column, and
  `GET …/traces/{measure}/entities` sums the rows below a term by entity on each side with the Diff, largest change
  first, saying on each side whether they add up to the term's allocation. Where a side cannot be compared (not
  returned, unavailable, or not adding up) no entity has a Diff and the page says why. Refused by name at a term with
  no allocation (a direction, which is a max over regimes) and above a leaf set with no contribution (CCR's, today).
  `…/leaves?below=true` gives the rows at every depth below a term, refused in the same place. The view offers
  `By trade` first and `By CRIF line` beside it, at a regime and every term under it.
- **The trace view**: `Governs` / `Not governing` on the operands of a max, min or clamp (the kernel resolves which
  term took it), the others faint, in the tree and in the phone's list; allocation Before, After and Diff columns,
  and the allocation's change in the formula pane; the term's own parameters first and those of the terms above
  folded, each matrix scrolling in its own box; the `explain` line with Copy, for the case in view. The order, the
  rows below and the fold are kept per view and have commands (`trace order`, `trace rows`, `trace parameters`), and
  `explain simm <path>` names spot IM by its module. The trace tab and its view are loaded on first use, as the trace
  panel is.

### Verification performed

| Suite | Result |
|---|---|
| `dotnet test server/Workbench.slnx` | 458 passed: the entities of a synthetic orchard trace (sums, Diff, order, one side, refusals, no Diff where a side does not add up or is not returned), the rows at every depth below a term, `governs` by position and by slot, SIMM's regimes and labels, the conversion said on each row, the registered tabs over HTTP, and spot IM's trades over HTTP adding up to the regime on both sides and to their own CRIF lines at a risk factor |
| `npm run typecheck`, `npm test` (Vitest) | Clean; 148 passed, among them the dock's tabs (selected and priced), an unpriced tab's reason and the conversion, the cards from the children's `governs`, the views below a term, the entity words, the governs mark, the allocation cells, the parameters split, the `explain` line and how `explain` names a measure |
| `npx playwright test` (Chromium, headless, Linux) | 33 passed; the two changed `simm.spec.ts` tests with `--repeat-each=3` (6 passed): the SIMM tab from a Metrics cell and by command, the FX said once, the cards switching roots, the governing and not-governing regimes, a regime's trades adding up and its CRIF lines at every depth, the copied `explain` line, a risk factor reached by the keyboard with each trade's CRIF lines listed, the `trace` commands, restore, After alone in the cards and at a factor, `explain simm …`, a counterparty with no IM CSA, the regimes' marks in the phone's list, and 4.5:1 text with no sideways scroll at 1440, 992, 768 and 390px in both themes |
| Screenshots at 1440 and 390px, dark | The SIMM tab with the FX caption, a regime's CRIF lines, a bucket with its trades and every parameter unfolded, and the phone's list of regimes; looked at by hand, no console message. A 7×7 ρ matrix scrolled the page sideways at 1440 until it was boxed, found by the browser test |

### Providers

As step 2: the CRIF comes from the fake Rates provider, the SIMM parameters are DEMO, and every figure says
`SIMULATED`. The allocation is the reference engine's Euler allocation.

### Known limits

- CCR's tab has no trades view: SA-CCR's provider writes no contribution per trade, and the kernel refuses to sum
  what was not allocated.
- The entity grid is ordered by the server and takes no sort or filter; it refuses one rather than ignoring it.
- The entities and the rows below a term are read in the one leaf set that names a contribution; a catalogue with two
  such sets has no entity view, rather than columns that fit some rows.
- The view's settings are kept per view for the session and are not saved.
- A trade in the entity grid does not yet go to its row in the trade grids (ADR-0011 §6's leaf action).

## Cases, ADR-0010 Delivery step 2 (tickets 01–08, 2026-11-09)

### What runs

- **The kernel's edit gains a layer** (spec `docs/specs/pricer-cases`): an edit target is a scope, an entity, a field
  and an optional layer, persisted, carried and refused as before; removing a created entity can remove a layer's
  edits in one log entry. The portfolio module, not the kernel, says how layers compose.
- **A case** is a created entity in the counterparty's scope with its changes in its own layer. The Base is Original
  plus the Common edits; a case reads the Base plus its own, its edit winning on the same field of the same entity in
  whatever scope each is kept, and coming after every Common edit (a row's B or A over a later grid edit). The page
  shows the Base. A state with layers is refused by the materialisers unless read through a reading.
- **`vary CSA-A threshold 10mm, 5mm, 0`**, and *Vary values…* on an editable cell's Context Menu (the CSA grid and both
  trade grids): a case per value, named from its change; the workspace's own value and a case already made are
  skipped and said. A comma between two digits is refused rather than split as a guess.
- **`add trades`** (docs/03 §5): a case per New row with A ticked, each that trade alone over the existing trades. A
  candidate in Before is refused by name and nothing is made; making it again replaces the set. Price refuses, naming
  the trades, a set the ticked rows no longer match, and each case's title says a common change to the existing trades.
- **`set <rows> <column> <value>`**: a typed cell's edit in the Base, on each row named, all or none.
- **Price** prices the Base and every case in one Run, one request per module holding every case (XVA, spot IM, spot
  CCR capital); the cap counts the Base (50, `Portfolio:CaseCap`) and Price over it says by how much. The Metrics have
  a row per case; Copy quote a line per case.
- **The Changes tab**: every Common change, then each case with its own, against Original; a value typed again or
  removed, a case removed whole (`case remove`, `cases clear`). A case of `add trades` is changed only whole.
- **The SIMM and CCR tabs** draw the case in view: a case's Metrics cell opens its tab on that case, and the dock's
  case selector moves it.
- **The AI**: `xva.vary`, `xva.trades.add` and `xva.set` are command capabilities with the command line's fields and
  words, logged with origin `ai`; the AI pane names the XVA workspace in view, and the page reads its grids again for a
  revision someone else made.

### Verification performed

| Suite | Result |
|---|---|
| `dotnet test server/Workbench.slnx` | 504 passed: cases against the value typed into the workspace for XVA, spot IM and spot CCR capital, layering and its precedence, the frozen input, the cap, the Changes list, `add trades` against each trade priced alone, `set`, the commands over HTTP and as agent tools, and the review's cases |
| `npm run typecheck`, `npx vitest run` | Clean; 160 passed |
| `npx playwright test` (Chromium, headless, Linux) | 39 passed before the review's fixes; the tests each ticket added with `--repeat-each=3`: `vary`, the Changes tab, *Vary values…* by keyboard, `add trades`, `set`, and the SIMM tab's case selector |

Code review (`/code-review`, high) found a case over a new trade's own Common edit crashing Price (the two were kept in
different scopes and met as two edits of one field), a list split on a thousands separator into two plausible cases,
an amount under 1,000 written bare and read back as millions, a reading cache keyed by the revision, a held change
offered as editable, one equality rule written twice, and this file not updated; all fixed, each with a test.

### Providers

As step 1: every figure is simulated and says so.

### Known limits

- Side-only changes within a case (Advanced, step 3) are not built: a case's change applies to both sides.
- The scripted agent has no XVA requests; the commands are reached by a model through their tools.
- `add trades <ids>` (trades by pasted IDs) is not built.

## Scenarios, ADR-0010 Delivery step 4 (tickets 01–06, 2026-11-09)

### What runs

- **A scenario** (spec `docs/specs/pricer-scenarios`) is a named set of market-data shifts, kept as created rows of the
  workspace in its own scope, so a counterparty change keeps them. The catalogue is the portfolio's and synthetic: IR
  discount and funding curves of JPY, USD and EUR at eight tenors (absolute, in bp) and GBPJPY, USDJPY and EURJPY
  (relative, in %). A tenor shift is a key-rate shift, triangular between the catalogue's tenors; two shifts of one
  scenario that touch one risk factor are refused.
- **`scenario s1 jpy_ois +10bp; usdjpy -5%`** defines a scenario whole, replacing one of its name and keeping whether
  it is included. **Price** prices every case on the base market and on each included scenario, in one Run; the cap
  counts pairs (cases × markets, 50). XVA, spot IM and spot CCR capital each apply the shifts to what they read, and a
  shift a provider cannot apply refuses its measure for that scenario by name. The dock's scenario selector moves the
  Metrics, the collapsed line, the measures' tabs and Copy quote, which names the scenario. A pick holds in its Run.
- **The Scenarios tab**: a grid of every shift (Scenario, Category, Index, Risk factor, Shift type, Size, Description,
  and a read-only Issue) ending in blank rows; a block pasted from a spreadsheet is ExGrid's spilled paste, one batch
  refused whole by row and column; Delete clears; a row with every cell cleared goes. Beside it the list: an include
  box, Duplicate (a dialog), Remove, and Remove all.
- **Generators**: `scenario ladder jpy_ois -50bp..+50bp step 10bp` and `scenario cross jpy_ois -50bp, 0, +50bp x usdjpy
  -10%, 0, +10%` make a set at once, named from their shifts, the zero left out; the command line says how many before
  Enter and the pairs against the cap, and refuses what the server would.
- **Saved sets**: `scenario save <set> [desk|all]` and `scenario load <set>` over the kernel's generic saved-item store
  (C32), in memory and in SQLite beside the workspaces; a user's desk is `Workbench:Desks`. A name two visible sets share
  is refused naming their owners, and `<set>@<owner>` picks one. `scenario load` completes from the sets the user sees.
- **The AI**: the ten scenario commands are command capabilities (define, include, exclude, remove, duplicate, clear,
  ladder, cross, save, load), logged with origin `ai`; saving carries the new effect `SaveItem`.
- **The command line** takes a scenario's name of several words (`scenario exclude jpy_ois +50bp · usdjpy +10%`) and a
  completion of several words whole, and refuses a value typed last once every slot is filled instead of dropping it.

### Verification performed

| Suite | Result |
|---|---|
| `dotnet test server/Workbench.slnx` | 572 passed: the reader and its refusals, each provider under a shift against the base market, the cross of cases and scenarios and the cap, traces and Copy quote per scenario, the grid's batches, include/duplicate/remove/clear, the generators, saved sets' visibility and SQLite across a restart, and the ten commands as agent tools |
| `npm run typecheck`, `npx vitest run` | Clean; 181 passed |
| `npx playwright test` (Chromium, headless, Linux) | 45 passed after the review's fixes; each test the step added or changed with `--repeat-each=3`: the scenario command and selector, the paste and the list by keyboard, the phone layout with the selector, the cross preview, save and load, and the SIMM tab under a scenario |

Code review (`/code-review`, two axes) found duplicate and clear unreachable by the AI, a generated name (with
spaces) unreachable by the command line, an excluded name outliving its rows so a scenario made later started excluded,
a load restoring inclusion against "included when it is made", the generator preview counting unitless sizes the
server refuses, a scenario pick carried to another Run, made-up values in a quote title, a trade count and a desk, a
floored toy funding spread, and spacing literals; all fixed, each with a test where it is behaviour. Kept, with the
reason in ADR-0010 §4's note: Category and Shift type take a value so a seven-column block pastes whole.

### Providers

As before: every figure is simulated and says so. The shifted market is one synthetic helper every provider reads.

### Known limits

- Credit shifts (CDS spreads) come with the Credit tab (step 5).
- The reply sentences are written on the server for the AI and on the client for the page; the REST answers could
  carry the server's.
- The dock's header row mixes 30px selectors with 22px buttons (design-guidelines §3), as the case selector did.
- The scripted agent has no XVA requests; the commands are reached by a model through their tools.

## Next

1. ADR-0010 Delivery step 5: the Credit and Settings tabs, Risks and Profile.
2. MVA with SIMM (`docs/12-implementation-plan.md` P2): spot IM from the XVA request's MVA option.
3. The owner's review of ADR-0003 §7 (scopes, carry and conflict), then a synthetic source change that can produce a conflict, and the browser cases still open above (reload, failed case unavailable).
4. Rates.
6. ME-ID sign-in through MSAL (C30) and the visibility rules (C31, C32); AI over AG-UI with the company's Copilot behind the adapter port (C34).
7. Slice 01b: Save and Pin (CO06), and the browser parts of CO03 and CO04.
