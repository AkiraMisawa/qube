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
| `dotnet test server/Qube.slnx` | 26 passed |
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

- `@ex-grid/core`, `@ex-grid/data`, `@ex-grid/react` and `@ex-grid/mantine`, with `@ex-grid/chart`, `@ex-grid/chart-react` and `@ex-grid/chart-mantine` since step 5, at `0.2.0` from npm (since 2026-11-10; `0.2.0-beta.3` from 2026-11-04; before that, tarballs packed from ex-grid commit `3bf523d` in `vendor/ex-grid/`). Since 2026-11-06 (ADR-0009) every grid is `QubeGrid`, ExGrid drawn by its Mantine Wrapper; the stylesheets are `@ex-grid/core/styles.css` and `@ex-grid/mantine/styles.css`.
- A key sent to a newly drawn grid before it carries its Settled Mark is lost, by ExGrid's own contract. Browser tests wait for the mark, as ex-grid's own tests do.
- ExGrid's TypeScript core has no touch support yet.
- Column widths (ExGrid ADR-0016): in the grids that take them (below), a declared width is raised to what its header needs (`gridHeaderRequiredPx`, as the grid charges an Auto column's, with no menu button on the Rates line grid, which hides its column menu, ADR-0015 §4), so a header is never cut at the default; a longer value may be. The grids of 2026-10-28 take `onColumnWidthChanged` (a view's grid, the Pricing DB, the Rates lines and cashflows, and the XVA page's New and Existing trades): a drag on a header's right edge (or a double click, which fits the column) moves it, and the width is kept per panel in the workspace store until the panel closes. Widths are not saved with a workspace. Since 2026-11-10 (owner: every grid gets the grip) the XVA CSA, Credit and Scenarios grids and the calculation trace's tree, entities and rows take it too, each kept per panel under its own key; the grids that hide their column menu (the Rates lines, Scenarios, the trace's three) are charged no menu button.
- Off-screen columns are not in the DOM, so a browser test moves the grid's Focus to a column before reading its cells.

### Known limits

- Storage was in memory in slice 01; slice 02 moved Runs, retained data and workspaces to SQLite.
- There is no AG-UI or AI path yet. No authentication.
- Palette focus is handled by the shell itself: opening the palette and choosing a command let go of the focused element first. Keys typed before the next surface has the keyboard are dropped, never delivered to the grid behind or to a closing palette.

## Slice 02: the XVA workspace and the Pricing DB

Plan: [docs/plans/slice-02-xva-workspace.md](docs/plans/slice-02-xva-workspace.md). Decisions: ADR-0003 §6 (resolved), §7 (built as proposed, still OPEN for the owner's review), C30 to C36 in `docs/00-decisions.md`.

### What runs

- **Generic workspace kernel** (`Qube.Composition.Application/Workspaces`): an append-only edit log, overrides with their base, the carry check (carried, conflicted, lost), conflict groups, `DropScope` with the confirmed count, an atomic counterparty change (`ChangeScope`), and `Freeze` refused while a priced entity holds an unresolved edit. Every command names the revision it was taken against.
- **SQLite** (`Qube.Persistence.Sqlite`, Microsoft.Data.Sqlite 10.0.14 with SQLitePCLRaw 2.1.13): one file holds generic tables for every domain: `Workspace` (current state), `EditLog` (append-only, `domain_kind` per entry), `AnalysisRun`, `SubmissionKey` and `RetainedData`. A workspace is restored after a restart from its source descriptor, and a descriptor that now acquires another version is refused. Deleting a workspace keeps its log. A Run left running by a stopped process is recorded as failed with code `Interrupted`.
- **Runs** record their owner, origin (`pricer`, `ai`, `workflow`, `action`), explicit relations (workspace and revision, parent Run, analysis group, conversation) and the result contracts their workflow declares. Idempotency keys are per owner and survive a restart. A submission under a used key is the same one when its whole content is, or, when the caller names an idempotency basis, when that is: a portfolio Price is named by the workspace revision priced, because its accepted input freezes cells that answer on their own (PV01), so a retry after one answered, or after the workspace moved on, is the first Run, and the key on another revision is refused (XV13).
- **Synthetic XVA module** (`Qube.Samples.Xva`): three registrations (two share code ABC under different legal entities), agreements with CSAs and one uncollateralised (`nocsa_MA-003`), up to 100,000 generated existing trades per counterparty (matured ones are absent at a later date, and the last fixing moves with the date), three draft trades that belong to no counterparty, an empty CVA recovery on LE-01. The rules: a new trade takes the main CSA, a curve follows its CSA (including a CSA curve edit) unless the user set it, an uncollateralised trade starts on the profile curve, an ineligible CSA is an issue on the row and Price refuses it. A simulated CVA/FVA provider prices Before and After independently and falls back to the CDS recovery when the CVA recovery is empty.
- **HTTP**: `/api/xva/...` creates a workspace, pages effective rows (sorted on the server, Blank last as ExGrid's ADR-0023), edits, resolves held edits, moves the value date, previews and confirms a counterparty change, prices and deletes. `/api/history/...` serves the Pricing DB.
- **Pricing DB**: every Run newest first; a module registers a history projection; the XVA view adds value date, registration, trade counts, CVA and FVA Before, After and incremental. An unknown domain is refused.
- **Client**: the XVA workspace page (ExGrid over a fetching source with a Row Key; user-set, held and invalid cells painted as Cell States with their reasons; a counterparty change that shows what it drops by edit kind and asks first; Price as a button and the `Price XVA workspace` command) and the Pricing DB page (one domain at a time, `Open` opens a Run). `new xva`, `open pricing db` and `price` are commands (their chords were removed by ADR-0006).

### Verification performed

| Suite | Result |
|---|---|
| `dotnet test server/Qube.slnx` | 58 passed |
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
- Requests act as one configured demo user (`Qube:DemoUser`) until ME-ID sign-in (C30); roles (C31) are not enforced, so a user sees only their own Runs.
- Run views are matched to Runs by output name, not by contract.
- Touch waits for ExGrid's touch release (C36).

## Slice 03: the Rates line pricer

Plan: [docs/plans/slice-03-rates-line-pricer.md](docs/plans/slice-03-rates-line-pricer.md). Decisions: C37 to C43 in `docs/00-decisions.md`, ADR-0004 (accepted 2026-10-27).

### What runs

- **Kernel (ADR-0004)**: entities the user creates (`Create`, `Remove`), sources a module derives from the workspace (`IEditRules.Derive`, one scope per line), a Derive log entry with its carry summary whenever a derived source moves, and derivation again on load.
- **Rates module** (`Qube.Samples.Rates`): a line's flat fields (C40, C43) read into typed terms; a resolver that refuses what it cannot honour on the row; a standard schedule generator; a schedule per line as a derived source whose periods are identified by leg and unadjusted dates, so cashflow edits carry, conflict or are lost when the terms move.
- **Pricing**: `IRatesPricingProvider` with the fake provider (`fake-rates/1`, every figure `simulated`): PV, the par rate or fair spread of the leg left without a quote, and 1bp pillar deltas when the row asks for risk, standing in for the engine's AD sensitivities (C38). A seeded synthetic market whose snapshot n is a function of its configuration.
- **Price** (`rates.price`): freezes the revision the user saw, prices its priceable lines against the snapshot the request names (an unknown one is refused), lists every other line as `notPriced` with its reasons, and refuses when nothing is priceable. One Run per Price.
- **Live**: subscriptions price the newest market with at most one calculation in flight and the newest update waiting; an accepted edit moves a subscription to its revision; a frame names its subscription revision, input revision and snapshot. No Run per tick. Frames stream as server-sent events. A workspace has one subscription on the server; pages attach to it (a new page first hears the frame shown now) and detach when they go. Once nobody has watched it for `Rates:LiveIdleStopSeconds` (300 by default), the next tick stops it and the workspace records why; a page opening on it then shows the last frame and offers Resume, which starts a new subscription on the current revision (owner, 2026-10-27).
- **Capture** (`rates.capture`): saves the frame the user saw as a Run, with its own snapshot. The capture names its frame, and the subscription keeps its last 32 frames (`RatesLiveService.KeptFrames`), so a newer frame shown before the capture arrives changes nothing; a frame no longer kept is refused (`FrameNotKept`, 409). Until 2026-11-10 a capture was refused whenever another frame had been shown by then, so a tick landing between the user's Capture and its arrival refused it: the C40 browser test failed about one run in twenty.
- **Commands (ADR-0005, accepted 2026-10-27)**: the kernel's `EditWorkspace` and `Subscribe` effects mark a capability as a command, invoked through `CommandService` for a `Caller` the server sets (user and origin), outside Runs, with an idempotency key (held in memory); the validator refuses a command as a workflow step. Edit log entries record their origin (a nullable `origin` column, added to an older log on open). The commands are the kernel's `entity.create` (one entity of a described product from the command line's short terms, in a workspace of the caller's; ADR-0008 §5), `rates.workspace.create` (an empty Rates workspace at a valuation date), `rates.live.start` (starts or joins the workspace's subscription) and `rates.live.stop`; the live status says whether the page or the AI started and stopped it. They are reached at `POST /api/agent/commands/{capability}/{version}`, the route the agent's tool calls take, with origin `ai`; no agent runtime calls it yet.
- **HTTP**: `/api/rates/market`, `/api/rates/workspaces/...` (create, view, add, edit, remove a line, cashflows and cashflow edits, resolutions, valuation date, price, start live, delete), `/api/rates/live/{id}/frames|capture`.
- **Pricing DB**: the Rates view lists one row per Run: valuation date, price or capture, snapshot, lines priced, failed and not priced, execution mode.
- **Client**: the Rates page (`new rates`). One ExGrid row per line, keyed by line, with Header Groups over Trade, Leg 1, Leg 2 and Result: the inputs first and the result after them (owner, 2026-11-04); the line and Flows are pinned, and `Remove line` is in the line's context menu (right click or the context-menu key). The grid reads at laptop width (ADR-0015, owner 2026-11-10): a collapsed leg is one read-only `Summary` column (`USD 500mm Fixed 4.10% 12m`; leg 2 leaves out a currency and notional equal to leg 1's), its Header Group's toggle (or a double click on the summary) opens its basic terms, and a `Conventions` toggle inside the opened leg opens the second level (C43), which reads `Overrides` (or `Profile`) while closed; the Trade group's toggle adds Collateral, Break and Risk. The levels opened are kept for the page while the application runs. The headers carry no column menu and no sort mark (`hideColumnMenu`, `queryable: false`), so a column is as wide as its values or its header (`gridHeaderRequiredPx` without either reserve). Folded with five lines the grid is 1,644px wide, and at 1280px every column up to `Par %` is in view. Notionals are typed and shown in millions (`Notional (mm)`, owner 2026-11-04; the server keeps units): `1000` is a billion, `3bn` and `500mm` say their own scale (`m` is months, never millions, ADR-0008 §2), and a bare number of a million or more is refused as a full amount typed out of habit. `new irs` and `new ccs` read a notional the same way. A new IRS is at the money unless a rate or spread is given (C47): spread 0 on the floating leg, the fixed leg at par, for `add line irs`, `new irs` and the AI's `entity.create` alike. Cells the user set, held edits, lines that cannot be priced and figures of an older revision are painted as Cell States with their reasons. The toggles are the ones ExGrid draws on a Header Group (ExGrid ADR-0400, since `@ex-grid/*` 0.2.0-beta.3), with Alt+Shift+↓ and ↑ on the group over the Focus; a closed group that hides a value the user set wears ExGrid's hidden mark. Cashflows opens a second ExGrid over the line's periods, whose dates, notional and rate are editable, with its notional exchanges below. Start live and Stop live, Capture, and Price (against the snapshot shown) are buttons and the commands `price`, `live start`, `live stop` and `capture`. The edit queue the XVA page used is now generic and shared.
- **Configuration**: convention profiles and the synthetic market are configuration (`Rates` section). None is configured by default. `appsettings.Development.json` carries the convention table proposed on 2026-10-27, still unconfirmed, on a weekends-only calendar: its profile version says `unconfirmed` and every row names its profiles.

### Verification performed

| Suite | Result |
|---|---|
| `dotnet test server/Qube.slnx` | 130 passed (the 8 command tests of ADR-0005 included) |
| `dotnet run` (Development) | Started; `/api/rates/market` advanced with the ticker |
| `npm test` (Vitest) | 28 passed |
| `npx playwright test` (Chromium, headless, Linux) | 9 passed; the Rates spec again after the commands (2 passed) |

| Acceptance | Status |
|---|---|
| A line typed by keyboard prices live, captures and prices once (C40, docs/07 §3) | Passed (Playwright) |
| Manual and live agree on the same input and market (docs/07 acceptance 4) | Passed (xUnit) |
| An edit moves the live subscription to its revision; capture saves the frame it names even after a newer one, and refuses one no longer kept (spine 6) | Passed (xUnit); the C40 browser test 40 runs in a row (2026-11-10) |
| Cashflow edits carry, conflict or are lost when terms move (ADR-0004) | Passed (xUnit). Not yet driven in the browser. |
| The AI's commands create a priceable line, refuse missing terms by name, join the page's subscription and are logged as the AI's (ADR-0005) | Passed (xUnit, over HTTP). No agent runtime drives them yet. |
| At 1280px the line, the trade, both legs folded and the result up to `Par %` read without scrolling (ADR-0015) | Passed (Playwright, Chromium headless Linux, five lines). Looked at by hand at 390, 768, 992, 1280 and 1440px in both themes, with a leg opened to each level and the context menu open (2026-11-10) |
| Touch, tablet and phone layouts | Not verified; waits for ExGrid's touch release (C36) |

### Providers

The provider and the market are simulated. Real holiday calendars, the confirmed convention table and a real worker are not wired.

### Known limits

- The convention table, the risk factors to report against and the meaning of "FX Forward (pts)" are open with the owner.
- The Pricing DB lists Rates Runs, not a row per line.
- Snapshots are kept for the life of the process.
- A collapsed leg's hidden values are priced whether shown or not; its hidden mark says so.
- The result columns stand after the inputs (ExGrid pins columns on the left only). With the legs folded they are in view at 1280px (ADR-0015), but opening a leg, or the AI pane at 1440px, scrolls them away again; a strip above the grid shows the focused line's status, PV and par or fair spread, or the only line's, at any width (owner, 2026-11-04). Its following the Focus between lines is not yet driven in the browser.
- On a phone the pinned Line and action columns take about half the grid's width.
- The line grid does not sort or filter (ADR-0015 §4): it holds the few lines being priced, in the order they were made.
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
| `dotnet test server/Qube.slnx` | 131 passed (the terms endpoint at the page's revision, logged as the page's) |
| `npx playwright test` (Chromium, headless, Linux) | 10 passed; the new command-line test and the two rewritten ones with `--repeat-each=3` (9 passed) |

### Known limits

- Frequency ranking is kept in the browser until the user settings store arrives with sign-in (C30).
- `new ccs` with terms is not there yet: `add line ccs` adds an empty line to fill in the grid.
- New XVA and New Rates still open their dialogs rather than taking their arguments on the line.

## The AI pane (ADR-0007, proposed 2026-10-27)

### What runs

- **AG-UI** (`Qube.Agent`, `/api/agent/...`): the pane posts AG-UI's `RunAgentInput` to `POST /api/agent/runs` and reads the run's events as server-sent events, in the shapes of `@ag-ui/core` 1.0.3 (protocol 1.0). The server holds the conversation per thread and owner and takes only new user messages from the client; frontend tools and client-written history are refused. A run goes on when its reader leaves; `GET /api/agent/threads/{t}/runs/{r}/events?after=n` reads the rest, `GET /api/agent/threads/{t}` reads the messages, `POST …/cancel` stops a run before its next tool call. The same run id posted again is read again, never run twice. An agent run is not a workbench Run.
- **Tools**: one per registered command capability (ADR-0005), named by its id with `_` (`entity_create`), whose parameters are its input contracts. A call goes through `CommandService` for `Caller(user, ai)` with the key `agent:{thread}:{toolCall}`; its result is the outputs or every issue of the refusal.
- **Runtime port** `IAgentRuntime`, chosen by `Agent:Runtime`; anything but a configured runtime stops startup. The only runtime is `scripted` (**no model**, labelled `scripted · no model` in the pane and on every card): modules register the requests it understands. Rates registers a new workspace (a valuation date, when none is open or the request says "new workspace"), an IRS (written as the words `new irs` takes and created through `entity.create`, so what the request leaves out comes from the same defaults as on the command line), `start ticking` and `stop ticking`. It passes on only stated terms, stops at a refusal and asks for what it names; the answer is joined to the refused request unless it asks for that thing afresh, so "pay fixed and start ticking" completes the swap, in the workspace the first turn made, and ticks it.
- **Copilot runtime** (`Agent:Runtime=copilot`, C44, C46): `CopilotAgentRuntime` posts each turn to the **agent host** (`Qube.AgentHost`, a process of its own on `127.0.0.1:5395`, behind a shared key) and reads its answer as JSON lines (words, a tool call, the end, a failure); each call is invoked by the loop for the user and its result posted back, and cancelling a run aborts the host's turn. The host runs `GitHub.Copilot.SDK` 1.0.27 (CLI 1.0.104) in its empty mode: no built-in tool, only the workbench's tools, every other permission rejected, one session per thread. Its token comes from its own environment or a file; the workbench is started without it. `tools/run-with-copilot.sh` starts both, optionally with the host as another OS user; `docs/running-with-copilot.md` says how, and how to reach it over Tailscale.
- **Client** (`client/src/ai/`): the AI pane on the right (a full-screen drawer below 768 px), one card per request with each tool call, its outcome and the reply, a runtime badge, and the chat input with its own `/` commands (`/new`, `/stop`, `/help`, `/close`; the command line's `/` stays out of the chat). `ai <request>` and `open ai pane` / `close ai pane` are commands, and the header has an AI button. The active panel's app gives the context (an open Rates workspace); a result's app effect opens the workspace a new line is in **behind** the active panel, with a Show button on the card, and refreshes what live start and stop changed. Nothing the AI does moves the keyboard.

### Verification performed

| Suite | Result |
|---|---|
| `dotnet test server/Qube.slnx` | 145 passed (7: commands through AG-UI, asking for missing terms and completing them, read again rather than run twice, client tools and history refused, an invented tool refused, cancel, an unknown runtime stops startup; 7 for Copilot with a fake model behind the real host: a model's call runs as the user's command, the conversation goes with each turn, an invented tool is refused back to the model, cancel aborts the model's turn, a model failure ends the run with its message, the host refuses a caller without the key and will not start without one, `copilot` without its settings stops startup) |
| `tools/run-with-copilot.sh` in the container | The host and the workbench start; the SDK's runtime starts; a run with a made-up token ends in `RUN_ERROR` carrying the SDK's message ("No GitHub OAuth token or Copilot HMAC key provided"). No real model was reached. |
| `npm test` (Vitest) | 42 passed (6 new: cards from events, a failed run, the request sent alone with its context, an effect applied once, busy refusals, chat commands) |
| `npx playwright test` (Chromium, headless, Linux) | 13 passed; the three AI tests with `--repeat-each=3` (9 passed). Every event of a run is parsed with `@ag-ui/core`'s own schemas. |

| Acceptance | Status |
|---|---|
| AI01 (real agent) | Not met yet. On 2026-10-28 a real model answered through the Copilot runtime on the owner's machine (`claude-sonnet-5.5`, the owner's fine-grained token): it asked for missing terms and created a line with `rates.line.create`, a command `entity.create` has replaced since (ADR-0008), so no real model has met the current tools. AI01's SIMM request needs tools the agent does not have (below). |
| AI04 (reconnect) | Application part passed (xUnit: a run is read again and from a position, never run twice). A browser reconnect is not tested. |
| AI06 (no unregistered path) | Passed for tools (xUnit: client tools refused, an invented tool refused without running). Views are not generated by the agent yet. |

### Known limits

- A fine-grained token with the Copilot Requests permission works (2026-10-28). The host's first call failed on a fresh machine because the SDK's working directory did not exist yet; the host now creates it before starting the client.
- The company's GitHub App (C45) is not wired: its hour-long installation token would need the host's SDK client restarted to refresh.
- The SDK's session lives in the host's memory; a host restart starts a new session, told the conversation so far as text.
- Threads and runs are held in memory; a restart loses them (the commands' effects are in the workspace log as before).
- A tool result card shows the command's outputs as JSON.
- The agent cannot run workflows (Runs) or read data yet; it has the three Rates commands.
- XVA registers no command, so the agent cannot act on an XVA workspace.

## Display and keyboard (2026-10-28)

- Amounts (C48): PV on the line grid, a view's `currency` columns (the XVA result's Before, After and Diff; the Rates result's PV and risk deltas) and the Pricing DB's figures are grouped in threes, in whole units, and red when negative (ExGrid's `tone` rule, in red's text colour, which reads at 4.5:1 in both themes; the Wrapper's own tone colour, the theme's error red, read at 3.86:1 on the dark grid). Par rates, quotes and counts are unchanged. Widened 2026-11-10 (owner: "basically an amount is grouped in threes and red when negative; a percentage and the like are different"): the XVA trade grids' Official PV, and PV01 and PV Adj, which XVA now declares `amount` (Margin in bp stays a `number`, to its digits); the Metrics dock, its Risks tab and spot IM, whose figures are all amounts; and the Profile table. Outside a grid a negative figure's cell carries `data-negative` and takes red's text colour, its note does not. The calculation trace keeps an amount to the cent so its sums still add up as written, and paints a negative one red in the entities and rows grids, where a column is all amounts; its tree mixes amounts and rates in one column, and ExGrid asks a tone of the value alone, so the tree's figures stay uncoloured. A typed term (a notional, a CSA threshold or MTA) reads back as typed. The owner settled the colour too: red means an error, an action that stops something, or a negative amount (docs/design-guidelines.md §7).
- Column widths: see the ExGrid section above.
- The command line: Ctrl+N and Ctrl+P move through its list as the arrows do (literal Ctrl; Cmd is not taken on a Mac, and an IME's composition keeps them). On Windows and Linux, Chrome keeps Ctrl+N for a new window. `show panel <title>` shows an open panel by its title or a part of it, and names the candidates back when the text fits several. Moving between panels was already Ctrl+Alt+←/→ (`next panel`, `previous panel`); Mantine's `mod` takes Ctrl or Cmd, so the Ctrl shown works on a Mac.

## Observability (ADR-0014, proposed 2026-10-28; numbered ADR-0008 until 2026-11-09)

### What runs

- **Server logs** through Serilog (C49): the console, a daily CLEF file (`App_Data/logs`, 14 kept), and OTLP when `OTEL_EXPORTER_OTLP_ENDPOINT` is set. Each process says at startup where its telemetry goes. A request is logged only when it was refused (warning) or failed (error).
- **Traces and metrics** through OpenTelemetry: ASP.NET Core, HttpClient and runtime instrumentation, and the kernel's `Qube` source and meter (BCL types only): spans `run {workflow}`, `invoke {capability}`, `command {capability}`, `agent run`, `agent tool {name}`; metrics for Runs (by workflow, origin, status; duration), commands, agent runs and tool calls, and the browser's events. RunId, workspace, step, capability, origin, outcome and agent thread ride as tags and log properties. The agent host exports the same way, and the workbench's calls carry their trace context to it.
- **The browser** sends a `traceparent` with every call and reports uncaught errors, unhandled rejections, render errors, failed or unanswered calls (5xx, network) and the notices it showed (since 2026-11-11 the refused ones only, ADR-0006's note 2 of that day), in batches to `POST /api/telemetry/client`; the server logs them as `Qube.Client` in the call's trace.
- A Run that throws something other than a capability's refusal now ends `Failed` with `InternalError`, logged with its RunId; it used to stay `running`.
- **Local stack**: `tools/observability/compose.yaml` runs `grafana/otel-lgtm` 0.36.0 on ports bound to localhost, with the provisioned *Qube* dashboard (`docs/running-with-observability.md`).
- Versions: OpenTelemetry.Extensions.Hosting and Exporter.OpenTelemetryProtocol 1.19.1; Instrumentation.AspNetCore, .Http and .Runtime 1.19.0; Serilog.AspNetCore 10.0.0; Serilog.Sinks.OpenTelemetry 4.2.0; Microsoft.Extensions.Logging.Abstractions 10.0.0 in the kernel.

### Verification performed

| Command | Result |
|---|---|
| `dotnet test server/Qube.slnx` | 148 passed (3 new: a page's events are taken and long texts cut, an oversized or unknown-level batch is refused, a Run whose capability throws ends `InternalError`) |
| `npm test` (Vitest) | 53 passed (new: trace context format, batching under one session, the first events kept with the dropped counted, a refused batch never throws; amounts; column widths; `show panel`) |
| `npx playwright test` (Chromium, headless, macOS, against a host already running) | 15 passed (new: a column dragged wider keeps its width across tabs; Ctrl+N/P and `show panel`) |
| The local stack, driven in a browser (2026-10-28, macOS, Docker Desktop 27.3.1) | An XVA price, a scripted AI request, a page error and a client event posted with a `traceparent` all reached Grafana: Loki held the logs with RunId and the client's properties, the client event in the trace it named; Prometheus held `qube_*` and `http_server_*`; Tempo held the request → `run workflow.xva.price` → `invoke xva.price` trace. The agent host's logs reached Loki as `qube-agent-host`. Every dashboard panel queried without error. |

### Known limits

- No alert rules or notification channel; the company's backend, retention and alerting are OPEN (docs/13).
- A trace begun in the browser has no root span in Tempo; its server spans are complete.
- The dashboard's totals take each server process's count and sum them: exact for a local workbench, approximate over a range that starts after a long-lived process did.
- `/api/telemetry/client` takes events from any page that reaches the server until sign-in (C30) exists.
- The Rates live frames are read through `EventSource`, which cannot send a `traceparent`; every `fetch` goes through `send` (`platform/api.ts`) and carries one, the AI pane's stream and cancel and the Rates unsubscribe included (since 2026-11-09).
- The agent host's spans of a real Copilot turn have not been looked at; its startup logs have.

### Merged onto main (2026-11-09)

This work and the display changes above sat on a local branch from 2026-10-28 and reached main after 112 commits of
its own. ADR-0008 had gone to the generic create command and C47 to the at-the-money IRS meanwhile, so they are
ADR-0014, C48 (amounts) and C49 (logs) here. On the pages main rebuilt, amounts and dragged widths were carried onto
the new structure: the XVA result view's Before, After and Diff are `currency`, and the XVA page's New and Existing
trades each keep their widths. Two browser tests read cells that a header read whole pushes out of view (Before on
a new trade, Trades After in the Pricing DB); they now move the Focus there and name the cell by its column, and the
width test drags Product, which is in view, since the result moved after the inputs.

The merge's review (`/code-review`, a Standards and a Spec reviewer) found, and this fixed: a negative amount read at
3.86:1 on the dark grid in the Wrapper's error red, now red's text colour (a browser test measures it in both themes);
the AI pane's stream and cancel and the Rates unsubscribe neither reported a failed call nor, for the last two, sent a
`traceparent`, now all through one `send` (ADR-0014 §4, a unit test each way); the ported tests did not name what they
hold; and three stale lines here. Left as found: the review's smells in the 2026-10-28 code (inline telemetry in
`RunService`, strings for outcomes and event kinds, amount parts repeated per grid) and the Rates lines' header width
judged twice (`fitted` in `lines.ts`, then `headerFitted`, which agree).

| Suite | Result |
|---|---|
| `dotnet test server/Qube.slnx` | 599 passed |
| `npm run typecheck`, `npm test` (Vitest) | passed, 212 tests |
| `npx playwright test` (Chromium headless shell 156.0.8078.4, macOS, a host of its own on port 5187) | 53 passed; the width, the CSA and the Ctrl+N tests with `--repeat-each=3` (9 passed) |

Of the three full browser runs after the tests were adapted, one failed `credit.spec.ts` (`cds none`, then Price: the Metrics cell read empty and
`notPriced`, not `refused`); it passed alone five times out of five and in the run after. The merge does not touch that
path; it is noted as flaky, not fixed.

The local monitoring stack was not run again after the merge.

## Design guidelines and the header (ADR-0009, accepted 2026-11-09)

### What runs

- `docs/design-guidelines.md`: Control Height, alignment, icons, the shell, colour, touch and keyboard, responsive layout, and how to check a UI change, each rule with its source.
- The header is three zones of 30px controls: title and Commands; Region and As of with their labels beside them; AI and theme, with icons from `@tabler/icons-react` 3.50.0. It is one row of 44px from 992px and two rows of 82px below; it no longer covers the top of a panel on a phone.
- An invalid context field is marked in the header and explained above the panels (`ContextIssues`).
- The focus ring follows `:focus-visible`.
- Every grid is drawn by `@ex-grid/mantine` (`QubeGrid`): the theme's colours in both schemes, Mantine's menus and filter panel, and the cells in the face ExGrid has widths for. The workbench takes the same face (Mantine's default system stack); the hand-written `--ex-` bridge is gone.

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

- **A portfolio domain module** (`Qube.Samples.Portfolio`) holds what XVA, SIMM and SA-CCR will share: the context,
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
| `dotnet test server/Qube.slnx` | 210 passed |
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
- The Before and After fills wait on step 3, which uses ExGrid's Split Fill (ex-grid ADR-0490, PR #545, released in
  `0.2.0`); until then every change differs on both sides and wears `modified`.
- No *Add CSA*; the CSA type is the source's and read-only (DEFAULT); the trades show no start date. (The Credit tab
  and the Settings section came with step 5.)
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
  CRE21), in `Qube.Samples.Ccr`, writing a trace to trade level in which every term reconciles.
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
  [path]` on the XVA page or on a trace. On desktop the tree of terms is a `QubeGrid` with a pinned Template
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
| `dotnet test server/Qube.slnx` | 378 passed (five runs in a row after the XVA branch's retry fix, XV13), among them the BCBS SA-CCR worked examples, a case per rule, the KVA option and the trace read over HTTP |
| `npm run typecheck`, `npm test` (Vitest) | Clean; 119 passed, among them the formula's typesetting (strict, untrusted commands refused), the tree's rows and `explain` |
| `npx playwright test` (Chromium, headless, Linux) | 29 passed; `trace.spec.ts` with `--repeat-each=10` (30 passed), and again with `--repeat-each=5` after the review fixes (15 passed): both sides by pointer, keyboard and `explain`, After alone, and 390px drill-in with 4.5:1 text in both themes |
| Screenshot of a Price with every measure selected, 1600px, dark | Spot capital, RWA, EAD and KVA in the dock, Trades 5 → 8; looked at by hand |
| Screenshots of the trace at 390, 768, 992 and 1440px, dark and light | Root, risk weight's steps, its table, a netting set's collateral with its rows, PFE; no sideways scroll; looked at by hand |

### Known limits

- The trace view is a panel the page and `explain` open. It is not yet a component a declarative view can name
  (ADR-0011 §6's first sentence), nor opened from the Pricing DB, and a leaf row has no action to its trade's row on
  the page yet (§6's leaf set): the XVA page cannot yet be asked to show a trade.
- In the light theme ExGrid Mantine draws a marked heading's line in the primary's filled amber, 2.04:1 on the marked
  heading's grey; the workbench sets the mark to the primary's text colour (`theme.css`), 5.78:1. Its words read
  6.62:1 by the Wrapper's own token since ExGrid 0.2.0 (ex-grid#550). The line is ex-grid's to fix (ex-grid#556).
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

- **Reference engine** (`Qube.Samples.Simm`, `SimmEngine`): the ISDA SIMM v2.x aggregation for IR delta (the 12 tenors, sub-curves, cross-currency basis, concentration, `S_b` bounded by `±K_b`, `γ g_bc` across currencies) and FX delta (each currency's own CR, and `f_kl` between them), through the risk class and the Rates/FX product class to each regime, then the largest regime per direction. A regime is computed when the IM CSA serves it and a line it holds names it; a direction with none has no IM, not an IM of 0. Euler allocation down to the CRIF line, with each concentration factor held, so contributions add up exactly at every term and per trade. A currency the table places in no volatility group, or a tenor it lacks, is refused by line.
- **Parameters**: one table, `DEMO-2.x`, made up and labelled DEMO: risk weights, the tenor correlation matrix, the other correlations and the concentration thresholds. ISDA's calibration is not in the repository.
- **IM source** (`IImSource`, a port): `SyntheticImSource` holds one synthetic counterparty `ABC/LE-01` with IM CSAs `IMR-ABC-01` (collect: JFSA, CFTC) and `IMP-ABC-01` (post: JFSA, EMIR), two JPY IRS, a USD IRS, a USD/JPY CCS and a USD/JPY FX forward (exempt), all starting at spot on the value date. A company source replaces it in `Program.cs` and nowhere else.
- **CRIF** (`CrifGenerator`): from the Rates pricing provider at the value date. Pillar deltas are split onto SIMM's tenors linearly in years; only an overnight index maps to the OIS sub-curve, and any other curve is refused by name until a mapping is configured; FX delta is the USD value's change for a 1% stronger currency, with a cross-currency swap's principal exchanges left out (exempt under the BCBS-IOSCO framework, ADR-0012 §5); cross-currency basis is the change for 1bp more spread on the non-USD floating leg. Exempt trades, provider failures, rows the provider did not return and missing FX rates are excluded with their reason. The CRIF's header names its registration and value date.
- **Capabilities**: `im.source.freeze` (registration, value date → the trades frozen with their resolved terms, and the IM CSAs), `risk.crif.generate` (frozen trades, pinned market snapshot → the CRIF and each trade's coverage: its line count or why it has none) and `margin.simm.calculate` (a retained CRIF, the frozen IM CSAs and a loaded table → IM per direction, SIMM per regime, and the trace, whose leaf rows carry each line's contribution). `workflow.simm.crif` (freeze, then CRIF) is in the catalogue and runs from a form, and its Run keeps the frozen trades and IM CSAs; SIMM alone runs through `SimmModule.Calculate(crif, csas)`, a workflow naming what it reads, submitted to `POST /api/runs`. Refused by name: a table that is not loaded (at submission, by its field), a CRIF of another registration (`RegistrationMismatch`) and a line naming a regime the source does not know (`UnknownRegime`).
- **Trace**: the engine writes ADR-0011's trace as it computes, under kinds registered per table (`simm.im`, `simm.regime`, `simm.product`, `simm.riskClass`, `simm.delta`, `simm.bucket`, `simm.bucketSum`, `simm.factor`, and the leaf set `simm.crif`), with its request, value date and subject in the header. One root per direction marks its governing regime; parameters sit in their own scope, each risk weight with why it is what it is. The kernel's `children` gained an optional role so that `S_b` can sum the risk factors under `K_b` (ADR-0011 §2, DEFAULT).

### Verification performed

| Suite | Result |
|---|---|
| `dotnet test server/Qube.slnx` | 418 passed, after merging the CCR branch at 7d0bf02 (SIMM: 24 engine, 8 sample cases, 10 CRIF, 7 over HTTP, 5 trace; and the kernel's new `children` case) |
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
| `dotnet test server/Qube.slnx` | 448 passed: 13 spot IM cases in process (`SimmPricerTests`), 1 over HTTP (`SimmPricerApiTests`), the executor's half-failing workflow, and the registration counts updated |
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
| `dotnet test server/Qube.slnx` | 458 passed: the entities of a synthetic orchard trace (sums, Diff, order, one side, refusals, no Diff where a side does not add up or is not returned), the rows at every depth below a term, `governs` by position and by slot, SIMM's regimes and labels, the conversion said on each row, the registered tabs over HTTP, and spot IM's trades over HTTP adding up to the regime on both sides and to their own CRIF lines at a risk factor |
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
| `dotnet test server/Qube.slnx` | 504 passed: cases against the value typed into the workspace for XVA, spot IM and spot CCR capital, layering and its precedence, the frozen input, the cap, the Changes list, `add trades` against each trade priced alone, `set`, the commands over HTTP and as agent tools, and the review's cases |
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
  (C32), in memory and in SQLite beside the workspaces; a user's desk is `Qube:Desks`. A name two visible sets share
  is refused naming their owners, and `<set>@<owner>` picks one. `scenario load` completes from the sets the user sees.
- **The AI**: the ten scenario commands are command capabilities (define, include, exclude, remove, duplicate, clear,
  ladder, cross, save, load), logged with origin `ai`; saving carries the new effect `SaveItem`.
- **The command line** takes a scenario's name of several words (`scenario exclude jpy_ois +50bp · usdjpy +10%`) and a
  completion of several words whole, and refuses a value typed last once every slot is filled instead of dropping it.

### Verification performed

| Suite | Result |
|---|---|
| `dotnet test server/Qube.slnx` | 572 passed: the reader and its refusals, each provider under a shift against the base market, the cross of cases and scenarios and the cap, traces and Copy quote per scenario, the grid's batches, include/duplicate/remove/clear, the generators, saved sets' visibility and SQLite across a restart, and the ten commands as agent tools |
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

- Credit shifts (CDS spreads) came with the Credit tab in step 5.
- The reply sentences are written on the server for the AI and on the client for the page; the REST answers could
  carry the server's.
- The dock's header row mixes 30px selectors with 22px buttons (design-guidelines §3), as the case selector did.
- The scripted agent has no XVA requests; the commands are reached by a model through their tools.

## Credit, Settings, Risks and Profile, ADR-0010 Delivery step 5 (tickets 01–06, 2026-11-09)

### What runs

- **The counterparty's CDS** (spec `docs/specs/pricer-credit-risks-profile`): a synthetic catalogue of single names
  (`ticker | ccy | seniority | doc clause | name`) and proxies (sector × region × rating); each registration's CDS
  from the source, a single name or a proxy. The **Credit tab** (while CVA is selected) chooses the type, a single
  name by a searchable select or a proxy by its three keys, or none; the chosen curve is one ExGrid row with its
  spreads editable in bp, marked against the catalogue, with *Vary values…*; a spread the server refuses keeps the
  editor open with its reason. None refuses CVA (and its charge and PV) by name, and the tab says why in words. `cds
  ABCH USD SNRFOR`, `cds proxy financials asia A` and `cds none` choose from the command line, completed from the
  catalogue; the AI reaches it as `xva.cds`. An empty CVA recovery takes the curve's.
- **Credit shifts**: the Scenarios catalogue's Credit category, the index `cds`, whole or at a CDS tenor, absolute in
  bp, key-rate on the CDS tenors. A shift CVA cannot take (a spread at or below 0) refuses CVA alone for that market,
  and FVA, PV and the exposure profile are priced as on the base market.
- **Settings** are registered by a module (form, default, the measures each serves) and are inputs of the workspace,
  frozen by Price and read by the provider. XVA's (DEFAULT): MPOR override (empty is the model's 10 days), cost of
  capital (10%, KVA's), charge ratio (1.25, CVA Charge's), PV adjustment (on) and risks included (off). The **Settings
  tab** is a table a module (value, default, the measures it is for); `set xva charge ratio 1.5` and `vary xva mpor
  20d, 40d` reach them; the server checks each form and bound and refuses by name.
- **Risks** (with risks included): per measure, CVA's credit delta at each CDS tenor (+1bp key-rate), IR per discount
  curve (+1bp) and FX per currency against JPY (+1%); FVA's IR on discount and funding curves and its FX; each the
  bumped figure minus the priced one, Before, After and Diff. A measure with none says why. One tab for CVA and FVA,
  following the case and scenario selectors.
- **Profile**: the exposure profile (EE and ENE, when CVA or FVA is priced) and the capital profile (EAD and capital,
  with KVA), one at a time, as a table of dates × series and an ExChart line chart (`@ex-grid/chart-react`
  0.2.0-beta.3 in Mantine's Chrome), After solid and Before dashed, each series hidden or shown from the legend. KVA
  is the cost of that capital profile.

### Verification performed

| Suite | Result |
|---|---|
| `dotnet test server/Qube.slnx` | 596 passed: the catalogue and `cds`, a spread edit kept on its curve, CVA reading the curve and refused without one, Credit shifts and their refusals, a Credit shift CVA cannot take refusing CVA alone, the settings' forms, bounds, freezing and effect on each figure, the deltas and their reasons, the exposure profile with and without collateral and MPOR, and KVA as the cost of the capital profile's rows |
| `npm run typecheck`, `npx vitest run` | Clean; 197 passed |
| `npx playwright test` (Chromium, headless, Linux) | 50 passed; the five tests of `e2e/credit.spec.ts` with `--repeat-each=3`: the Credit tab by its own controls and by `cds`, a refused spread at the cell, the Settings tab and `set`, a refused setting sent once, the Risks tab before and after risks are included, the Profile table and chart with a series toggled from the legend, and the phone (390px) with no sideways scroll |

Looked at by screenshot at 390 (dark), 768 and 1440 (light): the Credit row, the Settings table, the Risks tables and
the Profile chart and table.

Code review (`/code-review`, two axes) found a Credit shift refusing FVA too, a cached bump giving another bump's
reason, Enter then a blur sending a setting twice, the catalogue's read error shown as an empty search, the `cds`
example in the command line naming no curve, a bound's message saying 100% where 100% is refused, rows with no market
filed under the base market and titles made up from ids, colour alone marking a missing CDS, a switch a size larger than
the guidelines', duplicated parse and row writers, and thin tests (the tab's own selects, the legend, Risks on a phone,
the Changes tab for a `set`, KVA from the profile's rows, a collateralised exposure); all fixed. Kept as they are: the
five CDS choice keys mapped field by field at each site (each maps them to a different thing: cells, edits, the view),
and the CDS type as strings on the wire.

### Providers

As before: every figure is simulated and says so. FVA's toy now integrates the same EE profile the Profile tab draws,
so a collateralised trade's exposure is what moves over its margin period, and every FVA figure differs from step 4's.

### Known limits

- A command that opens an input tab while the dock is full now brings the inputs back (`openTab`), so the keyboard
  lands on what it opened.
- Advanced configuration is the engine's (OPEN); the risks' bumps are fixed (DEFAULT); a delta table is a
  Mantine table, as the Metrics are, not a grid.
- What CVA Charge is computed from beside the charge ratio is the engine's (ADR-0010 OPEN).

## ExGrid 0.2.0 (2026-11-10)

### What runs

- The seven `@ex-grid/*` packages move from `0.2.0-beta.3` to `0.2.0`, the first release that is not a prerelease
  (ADR-0001 says "one exact version" now). 0.2.0 is 0.2.0-rc.4's code; from beta.3 its public surface only adds
  (Find All, `highlightFocusColumn`, `moveReference`, the Split Fill's `lowerRightFill`), so no call changed.
- What 0.2.0 fixed is no longer worked around: ExChart's legend keeps the keyboard across a toggle (ex-grid PR #548),
  so `e2e/credit.spec.ts` no longer focuses the entry again and reads that it kept the keyboard; and a marked
  heading's words take the Wrapper's own token (ex-grid#550), so the light scheme's override in `theme.css` now
  colours only the line, which the Wrapper still leaves in the primary's filled amber (ex-grid#556).
- 0.2.0-rc.3's behaviour changes (a Window holding a null row or a Row Key twice is refused by name; a header band
  past the browser's scroll limit is refused) meet nothing here: no test or page shows either refusal.
- What a user sees changes only by the fixes 0.2.0 carries (ExChart's legend and its open table keep the keyboard);
  no page takes up what 0.2.0 added yet (the Split Fill is ADR-0010 Delivery step 3's).

### Verification performed

| Suite | Result |
|---|---|
| `npm run typecheck`, `npx vitest run` | Clean; 197 passed |
| `npx playwright test` (Chromium, headless, Linux) | 50 passed; the Profile test of `e2e/credit.spec.ts` (the legend toggled twice by keyboard alone) with `--repeat-each=3` |
| Contrast in Chrome, the trace tree's marked Term at 1440 × 900 | light: words 6.62:1, the line 5.78:1 (2.04:1 without `theme.css`'s override), the Focus outline 2.70:1; dark: words 8.74:1, the line 6.57:1 |

`dotnet test` was not run for this change: nothing under `server/` moved (the browser run builds and starts the host).

### Known limits

- In the light theme the grid's Focus outline is the primary's filled amber (`#e38800`), 2.70:1 on the white body,
  where docs/design-guidelines.md §7 asks 3:1 of a focus indicator. Measured for the first time here; it is the
  Wrapper's mapping (ex-grid#556) and was the same on 0.2.0-beta.3. Not patched in `theme.css`, because the Focus's
  colour is a design choice of the owner's.
- ExGrid's Mantine Wrapper still draws a disabled column-menu item as a grey block (ex-grid#552) and the Action
  Column's buttons in the browser's colours (ex-grid#553).

## The name Qube and its identifiers (C50, 2026-11-10)

### What runs

- The product's displayed name is Qube: the header reads `QUBE` and the browser's title `Qube` (read in Chromium at
  1440 × 900 with an XVA workspace open: every class on the page is `qube-*`, and the console is clean).
- Every identifier follows it. Projects, namespaces and assemblies are `Qube.*` (`server/Qube.slnx`), configuration
  keys `Qube:*`, environment variables `QUBE_*`, the kernel's source and meter `Qube` with instruments and tags
  `qube.*`, the services `qube` and `qube-agent-host`, CSS classes and variables `qube-*`, the handoff's contract ids
  and schema ids `qube.*`, and the names of local state `qube` (database file, agent host's folder, Keychain item,
  compose project, dashboard). Prose still says "the workbench" for the kind of thing the product is.
- An earlier name is not passed over in silence where data or a credential hangs on it (`LocalState`, README "Coming
  from before the rename"): the host refuses to start on `Workbench:*` keys, or beside an `App_Data/workbench.db` with
  no `qube.db`; `tools/run-with-copilot.sh` reads the earlier Keychain item while the new one is absent, and says so.
- Nothing stored is rewritten. A Run kept from before still carries the contract ids and producer names it was
  written with (`qwb.mapped-outcomes`, `workbench-simm-reference`); nothing matches on them.

### Verification performed

| What | Result |
|---|---|
| `dotnet build server/Qube.slnx` | 0 warnings, 0 errors |
| `dotnet test server/Qube.slnx` | 604 passed, 4 of them `LocalStateTests` |
| Client: `npm run typecheck`, `npm test`, `npm run build` | 228 passed in 30 files |
| `npx playwright test` (Chromium, headless, Linux) | 54 passed |
| The built host started beside an `App_Data/workbench.db` alone, then with `Workbench__Database` set | Refused each time, naming the file or the key and what to do |
| `tools/run-with-copilot.sh` with a stand-in for `security`, in the container | Only the earlier item: both hosts start and the script says which item it read. Both items: the new one is read, nothing is said. Neither: refused, naming `qube-copilot` |
| `python3 tools/validate_handoff.py` | Passes |

### Known limits

- Not run: the observability stack. The dashboard's queries and the compose project were renamed with the
  instruments they read and the file parses as JSON, but no Grafana was started here.
- Not run: the macOS Keychain itself. The script's choice between the two items was exercised with a stand-in.
- `WORKBENCH_PORT`, `WORKBENCH_DB` and `WORKBENCH_API` are ignored without a word if still set; only the host's own
  keys are checked.

## Generic commands, delivery step 0: the review's fixes (ADR-0016, ADR-0006's notes of 2026-11-11)

ADR-0016 was accepted on 2026-11-11, when the owner asked for every step of its Delivery order to be built. Step 0 is
the four fixes of the command review of 2026-11-10 that need no decision of the design
(`docs/specs/commands-0-review-fixes`). What a command does on the server is unchanged; the one server change is the
reader of a flag's words, which a product's term now shares with a cell.

### What runs

- **A line that cannot be read stays open** (note 1). A command reads its arguments before it does anything and
  throws its reason as a `CommandRefused`; `execute` answers it, and the line keeps its text and shows the reason
  where it shows a missing argument. Anything else a command throws is a defect and is thrown on. `set <trade> a`,
  `vary CSA-A threshold` and `scenario duplicate s1` closed without a word. A chord that cannot run says why in a
  notice (`focus grid` with no panel open: `focus grid: No panel is open.`).
- **A notice has a kind** (note 2, ADR-0016 §6), stated by what raises it and never read from its text: `done` is
  teal with a tick and `role="status"`, and leaves after six seconds unless the pointer or the keyboard is on it, each
  by a hold of its own; `refused` is red with a cross and `role="alert"`, and stays until it is closed or replaced. A
  done notice that counts edits in conflict or lost stays too. It is drawn over the top of the main area and takes no
  room, so no grid moves when a command answers. Only refused notices are reported to the server's log (ADR-0014 §4).
- **Commands that edit say what they did**, in titles: `before none` says `Cleared Before on 2 existing trades.`, from
  the line or from the caption's buttons; Price on the XVA page says the Run it was accepted as. A refused edit names
  its row and its column as the screen does (`CSA-A Ccy was not changed: …`, `The measures were not changed: …`), on
  XVA and on Rates, never an entity's key or a field's name in the code.
- **A flag is read one way** (note 4): `on`, `off`, `yes`, `no`, `true`, `false`, `y`, `n`, in any case, in a trade
  grid's B and A cells, a CSA's flags, Rates' flags, `set`, and a product's `flag` term on the page and on the server
  (`FlagWords.Read`). The hints of `set` and `vary` show a trade and a CSA of the open workspace; they showed an id no
  workspace has.
- **The empty line and `?` list the page's commands first** (note 3), under the active panel's title, then each group
  of the other commands that can run, the applications' before the workflows' and the shell's, and on the empty line
  what cannot run now, last, each with its reason. Nothing is highlighted in these two lists, so Enter alone runs
  nothing and says to choose or to type: Ctrl+K and Enter would have priced. A list is cut at 200 commands or 50
  values and then says how many more there are. The placeholder names the first three commands of that list, so it
  follows the page.

### Verification performed

| What | Result |
|---|---|
| `dotnet test server/Qube.slnx` | 605 passed |
| Client: `npm run typecheck`, `npm test`, `npm run build` | 253 passed in 31 files |
| `npx playwright test` (Chromium, headless, Linux) | 62 passed |
| The eight browser tests of ADR-0006's notes 1 to 4 (four of them one per width), each with `--repeat-each=3` | 24 passed |
| Looked at in Chromium: a done and a refused notice, the empty line's list and the line after Enter alone, in both schemes at 1440, 992, 768 and 390 px | A notice lies under the header and inside the page at each width, and wraps to three lines on a phone; a refused one lies over the panel's tabs until it is closed; a heading reads as one, and no command is highlighted until an arrow chooses |
| Code review (`code-review`, two axes, since `78415af`) | Its findings are fixed in this step, but those the Known limits name |

### Providers

Unchanged: every figure on these pages is simulated, and says so.

### Known limits

- The reason is shown at Enter. While a line is typed nothing reads it yet: that is the server's preview, step 1.
- `set` and `vary` are still read on the client (`setTyped`, `varyTyped`), and a result does not yet say how a value
  was read or how many rows it changed (the review's F4): both move with step 1's `entity.set` and its result.
- When the server refuses a command the line has already closed, and the reason is a refused notice: keeping the line
  open then needs the preview (step 1) and the chain key (step 2).
- No list of the sample modules passes a cap, so the line `N more: type to narrow.` is held by a unit test of the
  count and was not seen in a browser.
- `measures` and `currency` say nothing when they are done: the toolbar's controls show what they set.
- `Cleared Before on N existing trades.` counts the grid's rows, not the rows whose value changed: rows changed is
  step 1's result.
- A carry that leaves edits in conflict or lost was not made in a browser: that its notice stays is held by the unit
  test of `leavesByItself`.
- A refused notice lies over the panel's tabs until it is closed or replaced, at every width.
- Not done here: history in the line, a run button on a phone, search words a command declares (the review's F10,
  F12, F15), and Rates' three command defects of 2026-11-10 (with Rates' registration, step 3).

## Generic commands, delivery step 1a: the log, `undo` and `redo` (ADR-0016 §11, 2026-11-11)

Step 1 of ADR-0016's Delivery order is three pull requests (`docs/specs/commands-1-tables-selector-undo`, fourteen
tickets; the ADR's notes of 2026-11-11, "what step 1 settles"). This is the first, tickets 01 to 04: what the log
records, `undo` and `redo` in the kernel, as two commands, on the pricer's line and on its grids' keys. The tables, the
selector and the edit verbs are 1b; the preview and the line built from the catalog are 1c.

### What runs

- **A log entry says its command and what it replaced** (ADR-0003's entry, extended by ADR-0016 §11). One accepted call
  is one command, `c` and the first sequence number it wrote (`c12`), on every entry the call writes, the derivation's
  included; a batch may carry the words it was typed as. A `Set` or a `Reset` records the override or the lost edit it
  found, a `Remove` the entity with the edits that ended with it, a change of context the context before. The SQLite log
  has five new columns (`command`, `said`, `undoes`, `redoes`, `replaced_json`), added to an older log when it opens;
  entries written before them have no command and are not taken back.
- **`undo` and `redo` are linear and read from the log alone** (`CommandHistory`, `WorkspaceService.Undo`, `Redo`).
  `undo` is one all-or-none command that puts back what each entry of the latest command replaced, as it stood, its
  sequence number included, so "a row's later edit wins" (ADR-0010 §6) reads as before the command; a set, a reset, a
  batch, a created entity, a removed one with its edits, and a change of context that kept the source version. `redo` is
  the same act on the `undo`, so `undo, undo, redo, redo` works and survives a restart; any other command ends what can
  be put back.
- **What is refused, by name**: nothing to take back (`NothingToUndo`, the workspace as it was made) or to put back
  (`NothingToRedo`); a command that took another source version, left a scope with its edits or decided a held edit, and
  one written before commands had ids (`CannotUndo`); another user's command (`NotYourCommand`); a named command that is
  not the latest (`NotLatestCommand`); a request that says neither the revision it read nor the command it means
  (`CommandNotNamed`). Nothing older than a refused command is reached.
- **A workspace's making is one command.** The pricer made a workspace and then set its owner's last selection of
  measures as a second command, which the first `undo` would have taken back. `WorkspaceService.Create` takes the values
  a workspace is made with and writes them in the making's own command; a selection taken back is no longer the owner's
  last (ADR-0016's note 2).
- **Two kernel commands and the page's door.** `workspace.undo` and `workspace.redo` 1.0.0 (`EditWorkspace`; the catalog
  now lists 33 capabilities) take the workspace and what the request means: the revision its caller read, as the page
  gives, or the id of the command, which the AI's request always names (ADR-0016 §11). They answer the new revision,
  their own command id, the command taken back, whose it was, from where, when, how many edits and one sentence (`Took
  back: set CSA-A threshold. redo puts it back.`). The owner of the workspace runs them, and the AI has them as tools
  (`workspace_undo`), which no agent runtime has called. `POST /api/commands/{id}/{version}` is the page's door to the
  same gateway as the agent's, with origin `pricer` and the same statuses (ADR-0016 §5).
- **A command is named as its screen names it** (`IEditRules.TitleOf`): by its words where it was given them, else by
  its edits' titles, the first three and a count. The pricer's rules answer `CSA-A threshold`, `DRAFT-1 A`, `Before on
  the existing trades`, `the measures`, `the display currency`, a case by its title; never an entity's key.
- **On the pricer's page**: `undo` and `redo` on the line, after `set` in the page's list, and Ctrl+Z and Ctrl+Y (or
  Ctrl+Shift+Z) in every grid of the page, the input tabs' grids included, through ExGrid's `onUndo` and `onRedo`. While
  a cell is being typed in the keys stay the editor's, by ExGrid's contract. The page runs them in its edit queue's
  turn, after the edits it has already sent, reads the workspace again in that same turn, and the notice says what was
  taken back or why not.

### Verification performed

| What | Result |
|---|---|
| `dotnet test server/Qube.slnx` | 630 passed |
| Client: `npm run typecheck`, `npm test`, `npm run build` | 253 passed in 31 files |
| `npx playwright test` (Chromium, headless, Linux) | 63 passed |
| The browser test of `undo` and `redo` (line and keys), with `--repeat-each=3` | 3 passed; its first repeat found the making's second command, fixed above |
| Looked at in Chromium: `undo` on the line, its notice and the list for `re`, in both schemes at 1440, 992, 768 and 390 px | The notice wraps to two lines on a phone; `undo` and `redo` list under the page's title |
| Code review (`code-review`, two axes, since `492969f`) | Three defects fixed (a `redo` that left a removed case's changes behind, the page's read beside its queue, a refusal that misnamed a command), the request's rule of §11 enforced, six tests added; what was left is in the Known limits |

### Providers

Unchanged: every figure on these pages is simulated, and says so.

### Known limits

- Only the pricer's page has the two commands and the keys. The kernel takes back a Rates workspace's commands, a change
  of valuation date and of defaults with the cashflow edits it moved among them (`RatesApplicationTests`, through the
  kernel's service); Rates' page gets the line's words and the keys with its registration, step 3, and until then such a
  change is named `changed the context`.
- A command is not yet recorded with its words: `set CSA-A threshold 5mm` reaches the server as the page's edit and is
  named by its cell (`set CSA-A threshold`). The words come with `entity.set` and the line's preview (1b, 1c).
- Not measured at 10,000 rows: an `undo` writes an entry for each entry it takes back, as the batch did. The by-row
  batch and its `undo` are measured when 1b builds them.
- A refresh, a change of counterparty or of value date, and a held edit once kept or discarded are not taken back, nor
  anything before them: the notice says which and why.
- The derivation runs after an `undo` as after any command. A lost edit that was acknowledged asks to be seen again when
  its scope moves, which was so before this step for every command and is not changed here.
- The answer names the command by an id (`c12`) the page does not show yet; the Changes tab lists edits, not commands,
  and words a whole grid's B or A its own way (`Existing B every row`, where the sentence says `Before on the existing
  trades`): one title for both comes with the tables' registration, 1b.
- What an entry replaced is one record with a field per case (an edit, an entity, a context), not a type per case; the
  code review named it and it was left, the log's JSON being simpler for it.
- No real model has called `workspace_undo`; the rule that the AI names the command is held over HTTP at the agent's
  door.
- The command's idempotency keys are held in memory, as every command's are (ADR-0005).

## Generic commands, delivery step 1b: tables, the selector and the edit verbs (ADR-0016 §1 to §3, §6, 2026-11-11)

The second pull request of step 1 (`docs/specs/commands-1-tables-selector-undo`, tickets 05 to 10): one form
vocabulary, a domain's tables registered through a port, one selector, and `entity.set`, `entity.reset` and
`entity.remove` over any registered table, with the pricer registered and `xva.set` gone. The preview and the line
built from the catalog are 1c.

### What runs

- **One form vocabulary** (`FieldForms`, the kernel's): a period, a date, an amount, a signed amount, a percent, a rate,
  a spread, a number, days, a choice, a flag and an id, each read from text and written back as it reads. A product's
  term, a pricer cell, a condition and a command's value are read by it. A choice's refusal lists its choices, and no
  value is said `empty` or `none`.
- **A domain registers its tables** (`ITableCatalog`, `IWorkspaceTable`, `TableRegister`): an id, words, a key, fields
  with their forms and words, subsets, a row reader, and what a write is, answered as workspace changes, so the domain
  keeps its scopes and its checks and the kernel knows no financial word. A refusal carries the word at fault and what
  it could have been (`TableRefused`), and the command gateway, the HTTP envelope and the AI's tool result answer both
  on the refusal's issue (`word`, `candidates`). A table's subsets share no word, checked when the table is read.
- **One selector** (`RowSelector`, `SelectorReader`, `Selectors.Resolve`): rows by id, where the end of an id is enough
  when it names one row; `all`; a subset; and `where` with `=`, `!=`, `<`, `<=`, `>`, `>=`, `~` and `in`, joined by
  `and`. With no table named, ids find the one table that has them and the rest mean the application's main table. A
  period on a date counts from the table's date. An amount is compared only where its unit is one. Ids named by their
  ends are found in an index of the ids reversed, so thousands of them stay linear. After `in`, a comma before three
  digits is refused as one value or two (`1,500k`); two signs are no operator; an order against `empty` is refused.
- **`entity.set`, `entity.reset` and `entity.remove` 1.0.0** (`EditWorkspace`; the catalog lists 35 capabilities): one
  all-or-none batch, written by row or as one whole-set edit where the rows are a subset or `all` and the field has
  one. The answer counts (`matched`, `changed`, `held`), names the first rows, and says one sentence (`Set A to off on
  9 trades; 3 already held it.`). A command that would change nothing writes nothing and is done. Several values are
  written in the order given, each checked against the rows as the ones before it left them, and refused together. Each
  is one command in the log under its own words, which `undo` then says (`Took back: set CSA-A threshold 5mm. redo puts
  it back.`).
- **The pricer is registered** (`PortfolioTables`): `xva.trades` (main; `existing` and `new`, with B and A as whole-set
  edits), `xva.csas`, and of one row each `xva.netting`, `xva.credit`, `xva.cds`, `xva.workspace` and
  `xva.settings.<module>`. `PortfolioCells` names a cell from the same fields, so `vary`, the Changes tab and the
  commands read one list, and one check (`PortfolioChecks`) serves a typed cell and a command. A module's typed column
  (Margin, PV Adj) is a field while one of its measures is selected, and PV Adj is compared within one currency. No row
  of the pricer is of the workspace's making until step 3 creates trades by command, so `remove` refuses each by name
  and says what to do instead.
- **`xva.set` is gone**, with `PortfolioWorkspaces.SetCells` and `POST /api/xva/workspaces/{id}/set`. The AI's tool is
  `entity_set`. The page's `set` runs `entity.set` through the page's door, in its edit queue's turn, and its notice is
  the server's sentence. The line still reads `set` with the client's reader, so `where` is typed by the AI and at the
  gateway until 1c.

### Measurements

Release build on this Linux container, the SQLite store, one request at a time with `curl`, a workspace of 10,009
existing trades (14,286 requested). Recorded, not gated; the ranges are of four runs.

| Step | Time |
|---|---|
| `set existing csa CSA-B`: 10,009 rows matched, 4,988 edits written by row | 0.20 to 0.25 s (0.30 s the first on a host) |
| `set where ccy=JPY after off`: 5,999 edits written by row | 0.11 to 0.22 s |
| `undo` of either, and `redo` | 0.10 to 0.20 s |
| `set existing before off`: one whole-set edit over 10,009 rows, and its `undo` | 0.02 s, 0.01 s |
| `set` of one row in a workspace with no other edit, and its `undo` | 0.02 s, 0.01 s |
| With 15,000 edits standing: `set` of one trade | 0.04 to 0.07 s |
| With 15,000 edits standing: `set` of one CSA's threshold, no table named | 0.09 to 0.16 s |
| With 15,000 edits standing: the workspace's view | 0.15 to 0.26 s |
| With 15,000 edits standing: `undo` of one edit | 0.03 to 0.06 s |
| In a test, the in-memory store: `set` of 7,000 rows named by the ends of their ids | 0.2 to 0.4 s |
| In a test, the in-memory store: two values over 5,999 rows (`after=off before=off`) | 0.5 to 0.65 s |

The last four were 0.30 to 0.36 s, 0.27 to 0.36 s, 0.42 s and 0.03 s when first measured: every reader of a state
worked its trades, CSAs and credit out again, and walked every edit of the workspace to find an entity's. The pricer
now reads a state once (`PortfolioMaterialiser`), finds an entity's edits by an index, works a touched row out again
only when what it reads has changed, and no longer works every row out again for a CSA term a row does not read. On
the kernel's own fixture `where` on two fields resolves in 6 ms at 10,000 rows and 51 ms at 100,000 (ticket 07).
Finding 7,000 rows by the ends of their ids took 1.1 s in the review's measurement, each end a scan of every id, before
the ends were indexed. A second value costs a reading of the rows as the first left them, which the last row shows.

### Verification performed

| What | Result |
|---|---|
| `dotnet test server/Qube.slnx` | 797 passed |
| Client: `npm run typecheck`, `npm test`, `npm run build` | 253 passed in 31 files |
| `npx playwright test` (Chromium, headless, Linux) | 63 passed |
| The browser tests of `set`, of `undo` after it and of a setting by `set xva`, with `--repeat-each=3` | 33 passed |
| Code review (`code-review`, two axes, since 4f3446f) | Fixed before the merge: see below |

The review's findings that changed behaviour, each with a test: a table said beside the rows now reads its own subsets;
several values are checked in order (a value was checked against the rows as they stood before the command, so a pair
the grid would refuse was written); a misspelt first field and a field given twice were refused under another name;
`==` was read as `=` and a value `=x`; ends of ids cost a scan each; `in` split a grouped number; `=empty` on an amount
was refused across units and `<empty` matched nothing in silence; a module's amount was compared across currencies; a
module's column was a field while its module was not selected; the refusal's word and candidates were dropped at the
gateway. The kernel's contract titles and comments no longer use a pricer's words as examples. Left as they are, and
said in the pull request: three handlers of one shape, the operator of a condition as text, and the arguments a verb
passes on together.

### Providers

Unchanged: every figure on these pages is simulated, and says so.

### Known limits

- The line does not read a selector yet: `set where ccy=USD after off` is the AI's tool and the gateway's, and the
  page's `set` names rows by id, `all`, `new` or `existing`. The preview, the count before Enter and the completions
  are 1c.
- A revision counts edits, so a batch of 5,000 rows moves it by 5,000, and `undo` writes as many entries again: the
  log of a workspace grows by the rows a command touched each time it is taken back or put back.
- With many thousands of edits standing, each new state still costs a walk of them (0.05 s at 15,000) before the first
  read, and the view more. No target is agreed.
- Only the pricer registers tables. Rates' lines and defaults come with step 3; until then its line is as it was.
- `reset` and `remove` have no words on the pricer's line and no button: they are commands of the gateway and tools
  of the AI until 1c builds the line from the catalog.
- No selector contract is registered: the edit verbs take the rows as the line spells them (ADR-0016's note 12).
- Only a subset has a whole-set edit (B and A of a grid): no table registers one for `all` of its rows, so `set all`
  on a table without subsets writes a row at a time.
- A trade's `csa` set to an id that is no CSA is written and flagged on the row, as the grid does. A field says which
  table its ids point into (`TableField.Table`), and nothing reads that until the preview completes ids in 1c.
- A module's column that does not show is no field, so its edit cannot be reset by command until the module is
  selected again.
- No real model has called `entity_set`; that the AI's tools hold it and not `xva_set` is held over HTTP.

## Generic commands, delivery step 1c: the preview and the line (ADR-0016 §4, §5, 2026-11-11)

The third pull request of step 1 (`docs/specs/commands-1-tables-selector-undo`, tickets 11 to 14): a command says how
it is typed, the server reads a line and answers what it would do with nothing executed, and the command line is built
from the catalog for those commands. Step 1 is whole with it: 1a (the log, `undo`, `redo`), 1b (tables, the selector,
the edit verbs, the pricer registered) and this one. What it settled is ADR-0016's note 15.

### What runs

- **A command says how it is typed** (`CommandForm` on `CapabilityDescriptor`, served by `GET /api/catalog`): its words
  and its arguments in the order typed, each with a name, a kind (rows, assignments, a field, text) and whether it is
  required. Five capabilities have one: `entity.set` (`set [rows] <set>`), `entity.reset` (`reset [rows] [field]`),
  `entity.remove` (`remove <rows>`), `workspace.undo` and `workspace.redo`. A capability that has a form and cannot
  read its line, and two that answer to the same words, stop the host at start.
- **`POST /api/commands/preview`** (`CommandLines`) reads `{ line, workspaceId, revision? }` and writes nothing. It
  answers the command, its inputs ready for the gateway, each argument as read, the rows (`trades, 2 rows` and their
  first ids), and what would change in one sentence (`A: 2 on to off`, `csa: CSA-A to CSA-B`, `Nothing would change:
  …`); or why the line cannot be read, with the word at fault and its place in the line (no place where the word
  stands twice). For `undo` and `redo` it says what would be taken back, whose it was (`you`, or `the AI for you`) and
  when. The verbs plan before they write, and the sentence is made from that plan.
- **What could follow** comes with every answer: after the command's words `all`, the subsets, `where`, the tables,
  the fields of the tables of one row and rows by id (twenty at a time, the rest counted); after rows, their table's
  editable fields; after a field, its values where they are a closed set (a flag, a choice, the ids of the table it
  points into); in a condition, the fields, the field's operators, its values and `and`.
- **One value follows its field with no sign** in `entity.set` itself (`set CSA-A threshold 5mm`); several are
  `name=value`. The client's own reader of `set` is gone.
- **The line** builds `set`, `reset`, `remove`, `undo` and `redo` from the catalog, as commands of the page in the
  active panel, and reads nothing after their words. While one is typed it shows the server's reading under the line
  and lists what could follow; Tab takes the chosen one at its place. A reading of an earlier text is dimmed, said to
  be read again, and nothing is taken from it; a request the server did not answer with a reading is said in red.
  `set where ccy=USD a off`, `reset existing csa` and `undo` are typed on the pricer's page as they are sent by the AI.
- **Enter runs the reading the line shows**, through the page's door and in the page's own turn after its edits: the
  inputs that reading answered are invoked, with the revision it was read at, so a workspace someone else moved since
  refuses it (`The workspace is at revision 9, not 8.`), the page reads the workspace again, and the line stays and is
  read again. With no reading for the text yet, the server reads the line at the page's revision and that is invoked.
  Taken, the line closes and the notice is the server's sentence (`Set A to off on DRAFT-2 and DRAFT-3.`); refused,
  by the reading or by the write, the line stays with its text, the reason and the word at fault selected.
- **The door is generic** (`commandDoor`, `client/src/platform/commands/served.ts`): a page gives it its workspace,
  its edit queue and how it reads its view again. The pricer's page opens it in a dozen lines; no other page does yet.
- **A grid's Ctrl+Z and Ctrl+Y** go through the same door, `workspace.undo` and `workspace.redo` by their ids; with
  nothing to take back the reason is a notice, since a key has no line.

### Measurements

Release build on this Linux container, the SQLite store, one request at a time with `curl`, a workspace of 10,009
existing trades (14,286 requested), five requests a line. Recorded, not gated.

| Preview of | Time |
|---|---|
| `set ` (what could follow: 50 listed, 9,994 more counted) | 0.01 to 0.02 s (0.09 s the first on a host) |
| `set where ccy=JPY after off` (5,999 rows, an edit a row) | 0.04 to 0.17 s |
| `set existing csa CSA-B` (10,009 rows; 4,988 would change) | 0.04 to 0.05 s |
| `set all after off` (10,012 rows, one edit a grid) | 0.01 to 0.04 s |
| `set ABC-LE-01-E0` (names no row; 20 offered, 6,995 more) | 0.02 to 0.05 s |
| `set CSA-A threshold 5mm` | 0.01 s |
| `reset existing` (nothing to reset) | 0.14 to 0.18 s |
| `undo` | under 0.01 s |
| With 4,988 edits standing: `set existing csa CSA-A` (10,009 would change) | 0.07 to 0.12 s |
| With 4,988 edits standing: `reset existing csa` | 0.02 to 0.03 s |

A preview of a whole line costs the command's plan, which is the write without the store; a line half typed is refused
before the rows are read and costs a few milliseconds. The client asks at each key and drops the answers it no longer
waits for; the server still finishes each one it started.

### Verification performed

| What | Result |
|---|---|
| `dotnet test server/Qube.slnx` | 886 passed |
| Client: `npm run typecheck`, `npm test`, `npm run build` | 272 passed in 32 files |
| `npx playwright test` (Chromium, headless, Linux) | 67 passed |
| The browser tests of the line this step added or rewrote and their neighbours (14 tests), with `--repeat-each=3` | 42 passed |
| Code review on two axes (standards, specification) before the merge | Findings fixed in the same pull request; what was left is under Known limits |
| The line in Chromium at 1440, 992, 768 and 390 px, light and dark, by a script and by eye | `set`, a `where` with its rows, a refused line, `undo`; nothing scrolls sideways |

### Providers

Unchanged: every figure on these pages is simulated, and says so.

### Known limits

- Only the five commands are read by the server. Every other command of the line is still read by the client (`vary`,
  `measures`, the scenario commands, Rates' line) until it moves to the catalog (steps 2 and 3).
- Only the pricer's page gives the line a door, so on a Rates page and on a Run the five are listed as not available.
- The server finishes a preview the client has stopped waiting for: the endpoint takes no cancellation. At 10,000 rows
  the dearest costs 0.2 s.
- An argument as read does not say where it came from (typed, default, context), and the line has no context yet:
  `selected`, `shown` and the focused cell are step 2.
- A completion that the line already holds whole is still listed (`off` after `a off`).
- A word at fault that stands twice in the line is named in the reason and not marked: the reader names the word, not
  its place.
- `undo`'s reading counts the command's edits, not its rows (ADR-0016 §11 speaks of rows): the log holds no row count.
- The shell's `set region` is read before a table's field named `region` would be; no table has one today.
- The pricer takes any text as a trade's CSA (`set DRAFT-2 csa nowhere` is written, as the grid's cell writes it), and
  Price then refuses the measure that needs it. A field that points into a table is not yet held to that table's rows.
- The moment in `undo`'s reading is written in the browser's zone and locale; no setting chooses another.
- `remove` refuses every row of the pricer by name, as in 1b, until a command creates a trade (step 3).
- No real model has called the preview or read a command form; the AI's tools are generated from the same descriptors
  and were not changed by this step.

## Generic commands, delivery step 2a: the selection and the context (ADR-0016 §2, §9, 2026-11-11)

The first pull request of step 2 (`docs/specs/commands-2-selection-context-places`, tickets 01 to 05): the rows
selected in a grid are rows a command can name, a line that names none acts on them, and the line says what it was
opened on. 2b (places, `go`, `find`) and 2c (the chain key, menu items from declarations) follow. What it settled is
ADR-0016's notes 16 to 19.

### What runs

- **A view.** When the line opens, the page's door answers what the user was looking at (`CommandDoor.view()`): the
  table of the grid the keyboard was last in, its subset, its selection and its focused cell. A grid whose page holds
  every row names rows by key; a grid the server pages names them by position, with the sort and the order token its
  reader answered for the rows on screen. The line sends the view with every reading, and the server keeps none of it.
- **`selected` and `shown`** are rows where rows are said, alone or narrowed (`set selected where ccy=USD a off`).
  The preview resolves them to row keys at the revision read, so the command that runs is rows by id: nothing in a
  contract, the log or a result knows a screen. A command invoked with either word is refused (`NoView`).
- **Rows left out** are the selection, or the focused row where nothing is selected: with three trades selected,
  `set csa CSA-B` sets their CSA. `reset` with nothing after it resets the focused cell. Each argument says whether it
  was typed or came from the view, and the line says so in words (`rows from the screen: DRAFT-1, DRAFT-2, DRAFT-3`).
- **The line says its context** under the input, in the server's words: `On screen: trades (new), 3 selected,
  DRAFT-1 · csa`. With no grid of a table behind it, it says nothing and `selected` is refused by name.
- **Positions are never guessed.** A table gives the order its grid is paged in (`IWorkspaceTable.InOrder`) with that
  order's token; positions sent with another token are refused (`ViewMoved`), and the sentence says `no longer in the
  order it was shown in`. A view that names what the workspace has not, or is no view (a range of no row, a selection
  said two ways), is the page's fault (HTTP 400, `InvalidView`), checked whole whatever the line.
- **Rows of a screen run as those rows or not at all.** The preview writes their keys as a line lists ids and reads
  that text again as Enter will: keys a command would read as other rows (a single key that is a subset's word) are
  refused before Enter (`UnsayableRow`). Found by the review: such a key previewed one row and wrote a whole subset.
- **`shown` is what the user sees.** The CSA grid can be filtered from its column's menu, and its view then names the
  rows it shows; the sentence says `csas, filtered to 1, nothing selected`. Found by the review: `shown` took the
  whole table, hidden rows included.
- **The pricer's page** gives its New and Existing grids by position and its CSA grid by key. Its reader of pages and
  its trades table answer one order, kept per state (`TradeOrder`). The CSA table now answers to `csas` only, since
  `csa` is the trade's field.
- **What could follow** offers `selected (3 rows)` and `shown (43 rows)` after `all`, and, where rows may be left out,
  the fields of the screen's table with what they would act on.

### Measurements

Release build on this Linux container, the SQLite store, one request at a time, a workspace of 10,009 existing trades
(14,286 requested), every one selected by one range, five requests a line, measured again after the review's checks
were added. Recorded, not gated.

| Preview of | Unsorted | Sorted by notional |
|---|---|---|
| The opening's sentence (no text) | 0.01 to 0.05 s | 0.01 s |
| `set selected a off` (10,009 rows by key; the answer is 177 KB) | 0.09 to 0.25 s | 0.09 to 0.14 s |
| `set csa CSA-B`, the rows left out | 0.05 to 0.08 s | 0.06 to 0.09 s |
| `set selected where ccy=USD a off` (4,011 rows) | 0.04 to 0.08 s | 0.05 to 0.08 s |
| `reset` on the focused cell | 0.02 to 0.06 s | 0.03 s |

Enter on `set selected a off` took 0.52 s, its request 176 KB. Before the order was kept per state these previews took
0.2 to 0.7 s: one reading asked the table for the grid's order five times, each 50 ms at this size.

### Verification performed

| What | Result |
|---|---|
| `dotnet test server/Qube.slnx` | 938 passed |
| Client: `npm run typecheck`, `npm test`, `npm run build` | Clean; 285 passed in 33 files; built |
| `npx playwright test` (Chromium, headless, Linux) | 70 passed |
| The three browser tests this step added, with `--repeat-each=3` | 9 passed |
| The line in Chromium at 1440, 992, 768 and 390 px, light and dark, by a script and by eye | The sentence, the mark and a reading of five rows; nothing scrolls sideways |
| The CSA grid filtered from its column's menu in Chromium, then `set shown threshold 7mm` | One row, CSA-B; `On screen: csas, filtered to 1, nothing selected` |
| Code review on two axes (standards, specification) before the merge | Two defects that wrote other rows than shown, both fixed with a test first: a screen key read again as a subset's word, and `shown` under the CSA grid's filter. Also fixed: views that are no view were read (a range of no row, an unchecked sort); the focused row's key by case; a stale order drawn as a normal sentence; names and repeated code. Left, and written down: a table left out (ticket 12, 2b); Enter before the reading arrives (ADR-0016 note 18); small repetitions between the selector's and the verbs' readers of a leading field |

### Providers

Unchanged: every figure on these pages is simulated, and says so.

### Known limits

- After a command on a **sorted** trade grid the order token has moved (it holds the revision), so the grid reads
  again in a new sequence and drops its selection, as it does after a cell's edit: a second command needs the rows
  selected again. An unsorted grid keeps its selection. A token that moves only when the order does was built in 2c
  (ticket 13, below).
- The keyboard reached the Existing grid by a press or by Tab, not by a command: `go existing` is 2b's, below.
- Only the pricer's trade grids and CSA grid are screens. The scenarios' shifts, the credit grid, a result and a trace
  are grids of no table, and the line has no screen once the keyboard has been in one; Rates has no door (step 3).
- `shown` is the grid's subset on the trade grids, which refuse a filter until the server pages one (`filter` is
  step 4). The CSA grid's own filter is read (above).
- A table left out was not yet the screen's (ADR-0016 §9): `set where ccy=USD …` typed on the CSA grid still read the
  trades, as in step 1, and the preview said so before Enter. Built in 2b (ticket 12, below).
- Enter before the reading has arrived runs the line with the view read as the line opened (ADR-0016 note 18): the
  count is then said by the notice and not before.
- A column menu opened in a short grid (the CSA grid with two rows) shows two items at a time and scrolls: a popover
  stays inside its grid's box (ExGrid ADR-0040). Its filter form is below the items, reached by Tab or by scrolling.
- An answer that names 10,000 rows is 177 KB at each key typed. No target is agreed; the median workspace is 100 rows.
- `set csa CSA-A threshold 5mm` no longer reads (`csa` is the trade's field): `set CSA-A threshold 5mm` does.
- With trades and CSAs both able to take a word (`set ccy USD` on the CSA grid), the line is refused with both
  spellings. Nothing remembers which one the user meant last time.
- The selection is not given to the AI as context (ADR-0007 §8; ADR-0016's first open question).
- This file held step 1c's section twice, the first with 1b's measurements under it; the copy was removed here.

## Generic commands, delivery step 2b: places, `go`, `find` and the screen's table (ADR-0016 §9, 2026-11-11)

The second pull request of step 2 (`docs/specs/commands-2-selection-context-places`, tickets 06 to 08 and 12): the
keyboard is put somewhere by name, a row is found in the grid on screen though its page was never loaded, and rows
said with no table are the screen's. 2c (the chain key, menu items from declarations) follows. What it settled is
ADR-0016's notes 20 to 23.

### What runs

- **Places.** A page registers its places with the shell: a word, what the screen calls it, whether it is a grid,
  and how the keyboard is put there. `go <place>` puts it there and says so (`The keyboard is in Existing trades.`),
  offers the page's places, and keeps the line with the reason where a place cannot take the keyboard now (`go
  results` before anything is priced). It is the client's alone: nothing is read, written or logged.
- **A place always takes the keyboard.** A page offers its places only while it draws them, and each has something
  to take the keyboard: a tab with nothing to work in (Netting while no selected measure reads the recovery) takes
  it on its own tab, and a dock that draws no tab and no control takes it itself.
- **`open <tab>` is `go` to that tab's place**: the same act, the same notice (`The keyboard is in the CSA tab.`).
- **The pricer's places** are `new`, `existing`, its input tabs by the words `open` takes, `results`, and `trace`
  while a trace is widened over the grids. Below `sm` the part a place is in is shown first. Rates, the Pricing DB
  and a Run have the one place `grid`.
- **`focus grid`** and its chord are `go` to the page's first grid, with the same notice.
- **`find <row> [<field>]`** puts the Focus of the grid on screen on a row, or on one of its cells. The server reads
  the line with the view and answers a place: the row's key, its position in the order the view names, the column,
  and the sentence to say. Enter invokes nothing; the page puts the Focus there with ExGrid's `placeSelection`, in
  the Row Sequence Version the view was read in, and the keyboard is in the grid afterwards. A view that names no
  order is answered the key alone: a page that holds every row finds it, and one that holds part says it cannot.
- **`find` is a view verb**: declared by the server apart from the capabilities (`GET /api/catalog`, `viewVerbs`),
  built on the line like a capability's command, never invoked, never logged, and no tool of the agent's.
- **What `find` refuses, by name:** a row the table has that this grid does not show, with where it is; an end of an
  id that names several, with them; a field the table has not; an order that has moved; no grid behind the line.
  Where the grid itself refuses the place, the line says which it was: the grid has moved since the line opened, or
  it keeps an edit open that it could not commit.
- **A table left out is the screen's.** On the CSA grid `set where ccy=USD threshold 0` is the CSAs in USD, and the
  request the preview answers names `csas`, so Enter and the log say the same rows (`set csas where ccy=USD
  threshold 0`). A subset's word the screen's table has not is still the main table's: `set existing a off` on the
  CSA grid is the existing trades. With no view, the main table, as in step 1. The line's reader decides the table
  once, and the preview, the completions and the request read what it decided.
- **The keyboard afterwards.** After `set`, `reset`, `undo` and Price from the line it is where it was before the
  line opened; `go`, `find` and `open <tab>` leave it where they put it, however late the page draws the place.

### Verification performed

| What | Result |
|---|---|
| `dotnet test server/Qube.slnx` | 968 passed |
| Client: `npm run typecheck`, `npm test`, `npm run build` | Clean; 300 passed in 33 files; built |
| `npx playwright test` (Chromium, headless, Linux) | 75 passed |
| The five browser tests this step added and the trace test it changed, with `--repeat-each=3` | 18 passed |
| `go`, `focus grid` and `find` in Chromium at 1440 and 390 px, by a script and by eye | The keyboard in each place, the part shown first at 390 px, the notices; nothing scrolls sideways |
| `find 0300 csa` in a workspace of 600 synthetic trades, the Existing grid sorted by notional | The row was in no page the grid had been answered with; the grid asked for its page, drew it, and the Focus is on its CSA |
| Code review on two axes (standards, specification) before the merge | Fixed, each with a test: the table of rows said with none was worked out in two places, which a subset both tables have told apart (now the reader's alone); `find` answered a position by the table's reader, not by whether the view named an order; `go` said the keyboard had moved where nothing took it, and the line gave it back before the page's late focus (places only while drawn, a fallback on the tab and the dock, `keepKeyboard`); `open <tab>` did not say where the keyboard went; one refusal sentence for a grid that had moved and one that keeps an edit open. Left as judgement calls: the page asks its own dock element what takes the keyboard; the reading carries the screen's table through two returns |

### Providers

Unchanged: every figure on these pages is simulated, and says so.

### Known limits

- `find` looks only in the grid on screen. A row of another grid is refused with where it is; it does not go there.
- `find` takes a row's id or its end, never a value of another field (`find where …` is `select`'s and `filter`'s,
  step 4).
- Only the pricer's trade grids and CSA grid take `find`: they are the grids of a table (step 2a's limit). On its
  other grids `find` says to go to a grid of a table. Rates, the Pricing DB and a Run have no door for the server's
  commands yet (step 3), and list `find` as unable to run there.
- `open <tab>` and `go <tab>` are two words for one act on the pricer, and now one code path. Which goes is step 3's.
- A paged grid whose rows on screen were answered in more than one order when the line opened takes no `find`: the
  line says so and asks to be opened again. The order token that moves only when the order does is 2c's.
- Nothing in a browser test holds an edit open under `find`: the sentence for it is held by a unit test alone.
- A place has no chord of its own: only `focus grid` has one.
- `go` and `focus grid` say where the keyboard went in a notice each time, the chord included.
- After a command on a **sorted** trade grid the grid still read again and dropped its selection (2a's first limit).
  Built in 2c (ticket 13, below).

## Generic commands, delivery step 2c: the chain key and menus from declarations (ADR-0016 §4, §9, 2026-11-11)

The third pull request of step 2 (`docs/specs/commands-2-selection-context-places`, tickets 09 to 11 and 13), which
ends the step: a chain of commands is typed with no mouse and no reopening between them, a grid's Context Menu is
built from what its table declares, and a sorted grid keeps its selection across a command that moves no row. What
it settled is ADR-0016's notes 24 to 26.

### What runs

- **The chain key.** Ctrl+Enter in the line, ⌘+Enter on a Mac, runs the line as Enter does and keeps the line open:
  the text is cleared, the list is an opening's, and the line says under its input what its chain did, in the order
  it happened: `› set selected a off`, then `Set A to off on DRAFT-1, DRAFT-2 and DRAFT-3.` with a tick or a cross
  and the notice's colour. A refused line stays with its text and the reason, as with Enter.
- **The next line is typed while the chained one runs.** It is kept whole. Enter and the chain key run nothing until
  the chained line has ended, and say why (`set selected a off has not ended yet. Press Enter again once it has.`);
  the server is asked to read nothing meanwhile. A chained line that is refused comes back on the line as typed,
  with why, and what was typed after it goes with it.
- **Each chained command is a new opening.** The screen is read again and the commands are built again: after `go
  existing` the next line's context is the Existing grid, and after `find E00004 csa` a field alone is that cell's
  row.
- **The line hears notices.** What the application says once a chain has begun is said in the line too, when it
  arrives: an edit's sentence under its line, and Price's result (`Price was accepted as Run …`) whenever it comes.
  No notice is said to be any line's. The line draws its last four rows and counts the rest.
- **The keyboard in a chain.** It stays the line's. A place a chained `go`, `find` or `open <tab>` puts it in has it
  taken back at once, and is where the keyboard goes when the line closes; with none, it goes back where the line
  was opened from. Such a command has ended when its place has taken the keyboard, so the next line is read with
  that place's screen. A command that opens a dialog ends the chain: the line closes and the dialog has the
  keyboard.
- **While an IME composes** every key of the line is the IME's: Enter, Ctrl+Enter, Tab and the arrows. This also
  ends a defect: the Enter that committed a composition used to run the line.
- **A table's actions.** A table's description lists its actions, each a title and the line that does it. `GET
  /api/workspaces/{id}/tables` answers a workspace's tables with them, the kernel's `Reset this cell` (`reset`)
  first. An action whose line begins with no command's words fails the read, and so does a host that registers no
  `entity.reset`: each is the registration's defect.
- **The Context Menu.** The pricer's trade grids show `Reset this cell`, `Take out of After`, `Put into After`, `Take
  out of Before` and `Put into Before`, each with its line (`Take out of After · set selected after off`), after the
  grid's own commands and before the page's `Vary values…`. The CSA grid shows `Reset this cell`. An item runs its
  line through the page's door, as a line typed: read by the server with the menu's Selection and the cell the menu
  was opened on, in the page's turn, logged, and taken back by `undo`. What it did, that nothing changed, or why it
  was refused is a notice.
- **The order's token.** A sorted trade grid's token is its source's version and a digest of the row keys in that
  order. An edit that moves no row leaves it: the grid keeps its sequence and its selection, and a second command
  says the same rows with nothing selected again. An edit that moves a row moves it, as before.
- **One context value.** The workspace's context value is one for the application's life. It was made anew at every
  notice, which made every page's command door anew in the middle of the command that said it: a `find` after a
  notice in one opening had no grid to put the keyboard on. Found by the chain, which is the first to do that.

### Measurements

Release build on this Linux container, the SQLite store, one request at a time, a workspace of 10,009 existing trades
(14,286 requested), the Existing grid sorted by notional and every row selected by one range. Recorded, not gated.

| What | Time |
|---|---|
| The grid's first page of a new workspace (sort and digest), then again | 116 ms, then 3 to 6 ms |
| `set selected a off`: the preview (177 KB), then Enter | 343 ms, 580 ms |
| The grid's first page after it (the state's first sort and digest), then again | 358 ms, then 9 ms |
| `set selected a on` with the same view: the preview, then Enter | 131 ms, 588 ms |
| The grid's first page after it, then again | 300 ms, then 8 to 9 ms |
| `GET /api/workspaces/{id}/tables` (seven tables) | 9 ms |

The token was the same after both commands, so neither was refused and the view was not read again between them.

### Verification performed

| What | Result |
|---|---|
| `dotnet test server/Qube.slnx` | 972 passed |
| Client: `npm run typecheck`, `npm test`, `npm run build` | Clean; 308 passed in 34 files; built |
| `npx playwright test` (Chromium, headless, Linux) | 80 passed |
| The five browser tests of this step, with `--repeat-each=3` | 15 passed |
| The line's log in Chromium at 390, 768, 992 and 1440 px, dark and light, by a script and by eye: a line running, Enter refused meanwhile, notices of both colours' text measured | Rows of one text line each, the icon centred on its row, inside the line's box; text contrast 4.9:1 or more (dimmed rows in dark), nothing scrolls sideways, no console message |
| The chain and the Context Menu in Chromium, dark and light at 1440 px and dark at 390 px, by a script and by eye (before the review's changes to what the line says) | The context after `go` and `find`, the dialog taking the keyboard, the menu's items |
| An IME's composition through the browser's own input protocol (`Input.imeSetComposition`) | Enter, Ctrl+Enter, Tab and an arrow did nothing of the line's while it composed; committed, Ctrl+Enter ran the line once |
| A server slowed by the test (`page.route`) | The next line typed whole while the chained one ran; Enter and the chain key refused with the reason; run once after it ended |

### Providers

Unchanged: every figure on these pages is simulated, and says so.

### Known limits

- After a chained edit that **does** move a row of a sorted grid, the line's view names the order the grid is leaving:
  the next line that says `selected`, or leaves its rows out, is refused by name, and the context is drawn stale.
  The line is closed and opened again. The grid has dropped its selection by then, as ExGrid does when its order
  moves.
- The line says no notice is any line's: Price's result in a chain reads `› price` and, whenever it arrives, `Price
  was accepted as Run …`, after whatever ran meanwhile.
- Enter pressed while a chained line runs is refused, not queued: it is pressed again once that line has ended.
- A dialog a page opens some time after its command ends the chain when it takes the keyboard, and what was typed
  into the line before that is lost with the line. No command of the line opens such a dialog today, so this is
  held by a unit test alone, and where the keyboard is after that dialog closes is the dialog's to say.
- A chained `new xva` gives the keyboard back to where the line was opened from until its dialog has drawn, as Enter
  does: a key typed in that moment goes there. The Run dialog does not have this gap.
- `Ask again` beside a table's actions, on a failed result, is held by no browser test.
- A row added to or removed from a sorted grid moves its token by what is digested; no test holds it, since the
  sample has no command that adds or removes a row of a grid.
- Every action is offered on every cell, enabled. One that changes nothing there says so when run; none is greyed
  out by the cell's state, which the client does not know generically.
- A declarative view's row action has no `command` target yet (ADR-0016 §4): no view asks for one.
- The IME test drives Chromium's composition through its protocol. A real IME on Windows or macOS was not driven.
- The footer says Ctrl+Enter on every platform; ⌘+Enter also works on a Mac and is not said there.
- Step 2a's and 2b's other limits stand: `shown` is the grid's subset until `filter` (step 4), only the pricer's
  trade grids and CSA grid are screens, and Rates has no door (step 3).

## Next

1. Generic commands, delivery step 3 (ADR-0016 §3, §4): the pricer's remaining commands as capabilities through the
   gateway, Rates registers its tables and its door, and one of `open <tab>` and `go <tab>` goes.
2. MVA with SIMM (`docs/12-implementation-plan.md` P2): spot IM from the XVA request's MVA option.
3. ADR-0010 Delivery step 3 (Advanced: Before-only and After-only edits, the split fill), when the owner asks for it.
4. The owner's review of ADR-0003 §7 (scopes, carry and conflict), then a synthetic source change that can produce a conflict, and the browser cases still open above (reload, failed case unavailable).
5. Rates.
6. ME-ID sign-in through MSAL (C30) and the visibility rules (C31, C32); AI over AG-UI with the company's Copilot behind the adapter port (C34).
7. Slice 01b: Save and Pin (CO06), and the browser parts of CO03 and CO04.
