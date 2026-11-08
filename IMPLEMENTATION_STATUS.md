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
- **Runs** record their owner, origin (`pricer`, `ai`, `workflow`, `action`), explicit relations (workspace and revision, parent Run, analysis group, conversation) and the result contracts their workflow declares. Idempotency keys are per owner and survive a restart.
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
- `ccr.spotCapital` registers into the pricer page: membership (a trade SA-CCR can decompose), its inputs, an SA-CCR
  class column on new trades, and validation that refuses it by name while CVA and FVA stand. KVA registers as XVA's
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
  rule leaves it to the bank are ADR-0013 §7's DEFAULTs (netting sets by CSA, remargining days, haircuts, V = 0 for a
  new trade, the profile's dates, KVA's formula and a 10% cost of capital). The counterparties' reference data and
  collateral are synthetic (`PortfolioCredit`).

### Verification performed

| Suite | Result |
|---|---|
| `dotnet test server/Workbench.slnx` | 371 passed, among them the BCBS SA-CCR worked examples, a case per rule, the KVA option and the trace read over HTTP |
| `npm run typecheck`, `npm test` (Vitest) | Clean; 119 passed, among them the formula's typesetting (strict, untrusted commands refused), the tree's rows and `explain` |
| `npx playwright test` (Chromium, headless, Linux) | 29 passed; `trace.spec.ts` with `--repeat-each=10` (30 passed): both sides by pointer, keyboard and `explain`, After alone, and 390px drill-in with 4.5:1 text in both themes |
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
- κ (8%) and which products come first are OPEN with the owner (ADR-0013). Only IRS and FX forwards are decomposed
  (the synthetic portfolio has no other product); any other would be out of the measure's membership with its reason.

## Next

1. ADR-0010 Delivery step 2: cases (`vary`, iterative individual addition, the changes panel).
2. The owner's review of ADR-0003 §7 (scopes, carry and conflict), then a synthetic source change that can produce a conflict, and the browser cases still open above (reload, failed case unavailable).
3. MVA with SIMM (`docs/12-implementation-plan.md` P2), then Rates.
4. ME-ID sign-in through MSAL (C30) and the visibility rules (C31, C32); AI over AG-UI with the company's Copilot behind the adapter port (C34).
5. Slice 01b: Save and Pin (CO06), and the browser parts of CO03 and CO04.
