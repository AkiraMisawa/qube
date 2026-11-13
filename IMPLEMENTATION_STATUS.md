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
- Column widths (ExGrid ADR-0016): in the grids that take them (below), a declared width is raised to what its header needs (`gridHeaderRequiredPx`, as the grid charges an Auto column's, with no menu button on the Rates line grid, which hides its column menu, ADR-0015 §4), so a header is never cut at the default; a longer value may be. The grids of 2026-10-28 take `onColumnWidthChanged` (a view's grid, the Pricing DB, the Rates lines and cashflows, and the XVA page's New and Existing trades): a drag on a header's right edge (or a double click, which fits the column) moves it. Since ADR-0017 (2026-11-11) a grid's widths are the user's setting `grid.widths.<grid id>`, the same in every workspace and panel, after a reload and in another browser; they are not saved with a workspace (before, they were kept per panel until it closed). Since 2026-11-10 (owner: every grid gets the grip) the XVA CSA, Credit and Scenarios grids and the calculation trace's tree, entities and rows take it too, each under its own grid id; the grids that hide their column menu (the Rates lines, Scenarios, the trace's three) are charged no menu button.
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

- Frequency ranking was kept in the browser until the user settings store (ADR-0017, 2026-11-11), which now keeps it for the user; sign-in (C30) is still the demo user.
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

## Generic commands, delivery step 3a: the pricer's verbs that edit, and short forms (ADR-0016 §3, §4, 2026-11-11)

The first pull request of step 3 (`docs/specs/commands-3-domain-verbs-rates`, tickets 01 to 07). Six of the pricer's
commands are declared once on the server and read there, three more are short forms of the kernel's `set`, and the
commands for a page's tabs are built by the shell. Nine hand-written client commands, and the seven `open <tab>`,
are gone with the six page endpoints that wrote for them. What it settled is ADR-0016's notes 27 to 30.

### What runs

- **A command says its application.** A command's form names the workspace application it is for, or none (the
  kernel's verbs). The server reads a line with the commands of the workspace's application, and the line lists a
  served command on a page of that application: a Rates page is offered no `vary`.
- **Six verbs read their own line**: `vary`, `add each`, `cds`, `measures`, `case remove` and `cases clear`. Each is a
  capability that says how it is typed and reads the words after its own into the request the AI already writes. The
  reading is a trial: the command plans its changes for the workspace as it stands and the kernel checks them with
  nothing written. The line says what would happen before Enter (`Makes 2 case(s): CSA-A threshold 10mm, CSA-A
  threshold 5mm.`, `Chooses …`, `Price would price KVA.`, `Removes the case …, with its 1 change.`, `Removes 2 cases:
  … and ….`) or the command's own refusal, and Enter invokes the capability with the inputs read, at the revision
  read.
- **What could follow is the pricer's to say**: the measures not typed yet, the CDS catalogue's words and a proxy's
  keys, the cases' titles.
- **`add trades` is `add each`** (the owner's name for it), with no argument until step 3d. `add trades` is no second
  spelling.
- **Short forms.** `before all|none [new|existing]`, `after all|none [new|existing]` and `currency <ccy>` are
  registered by the pricer as short forms. The server expands one to a kernel line and reads that line; the line
  shows it before Enter (`Runs as set existing before off`), with the kernel's own preview under it (`trades, 2
  rows: …`, `B: 2 on to off`). What runs, what the log names and what `undo` takes back is the kernel's command, by
  the expanded line. With no grid named the grid is the one the keyboard is in; with none, the line is refused and
  names `new` and `existing`.
- **The page's controls call the same commands**, through the page's door. A measure's button invokes `measures` with
  the selection it would leave; the captions' `B all`, `B none`, `A all` and `A none` run the short form (`before all
  existing`); *Vary values…* invokes `vary` with its values as typed and shows the command's sentence in its dialog;
  the Changes tab's removals invoke `case remove` and `cases clear`. `undo` names a press as it names the line.
- **`open <tab>` is built from the page's tabs.** A place of a page says whether it is a tab; the shell builds one
  command for each (`open csa`, `open netting`, `open im`, …) and `go` takes the grids, the dock and a trace. `go
  csa` is no longer read. `open result` and `open pricing db` keep their words.
- **Gone:** the client's readers of `measures`, `vary`, `add trades`, `cds`, `case remove`, `cases clear`, `currency`,
  `before` and `after`, and the page's `/vary`, `/cds` (the write), `/add-trades`, `/cases/remove`, `/cases/clear` and
  `/measures` endpoints. The catalogue's read (`GET /api/xva/cds`) stays.
- **The AI** calls the same capabilities, three of them new to it (`xva.measures`, `xva.case.remove`,
  `xva.cases.clear`), and the page reads the workspace again after each.

### Verification performed

| What | Result |
|---|---|
| `dotnet test server/Qube.slnx` | 1021 passed |
| Client: `npm run typecheck`, `npm test`, `npm run build` | Clean; 297 passed in 33 files; built |
| `npx playwright test` (Chromium, headless, Linux) | 81 passed |
| The four spec files this step changes (`xva`, `simm`, `trace`, `credit`), with `--repeat-each=3`, before the review's changes | 186 passed |
| The three browser tests the review's changes bear on (the short forms, `vary`, *Vary values…*), with `--repeat-each=3` | 9 passed |
| The line's `Runs as` row in Chromium at 390, 768, 992 and 1440 px, dark and light, by a script and by eye | One text line inside the line's box at each width; no text of the line under 4.5:1; nothing scrolls sideways; no console message |

### Providers

Unchanged: every figure on these pages is simulated, and says so.

### Known limits

- The line still lists the commands written in the client before the served ones, so the pricer's placeholder reads
  `price, scenario, scenario include…`. The order becomes the module's, `price` first, when `price` is served (3b).
- Still written in the client: `new xva`, `price`, the scenario verbs, `refresh`, `pv01 retry`, `explain`, the trace
  options, `open result`, `dock full|restore` (3b), `download run` and `copy quote` (they act in the browser, note
  31), and every command of Rates (3c).
- `add each` takes no list yet: it makes a case for each New row ticked in After, as `add trades` did (3d).
- The currency select and the Credit tab's selects are field edits of the page's queue, as a typed cell is, and not
  commands (ADR-0016 note 28): a proxy is chosen a key at a time, and `cds` says a whole choice in one line.
- A served line stays open until the server has answered, and a key typed meanwhile is the line's. The browser tests
  wait for the line to close before they send keys to the page (`taken`).
- A request to `xva.vary` with no values is refused by the contract (`422 ValidationFailed`), not by the command's
  own sentence; a line with none is refused by the command, with an example.
- `vary` offers nothing to complete, and a refusal of one of the six verbs is a sentence alone: it marks no word of
  the line and lists nothing it could have been, as `set` and the short forms do (ADR-0016 note 28).
- A caption button pressed where every row already holds the value writes nothing and says `Nothing changed`.
- Rates has no door and no registered table (3c): none of the kernel's verbs work there.

## Generic commands, delivery step 3b: scenarios and the verbs that execute (ADR-0016 notes 32 and 33, 2026-11-11)

The second pull request of step 3 (`docs/specs/commands-3-domain-verbs-rates`, tickets 08 to 11). The pricer's ten
scenario commands, `price`, `refresh` and `pv01 retry` are declared once on the server and read there. The page's
Price and Refresh buttons, a failed cell's *Ask again* and the Scenarios tab's gestures on a scenario invoke the
same capabilities. Twelve hand-written client commands are gone with twelve page endpoints.

### What runs

- **The scenario verbs read their own line**: `scenario`, `scenario include`, `scenario exclude`, `scenario remove`,
  `scenario duplicate`, `scenarios clear`, `scenario ladder`, `scenario cross`, `scenario load` and `scenario save`.
  The line says what each would do before Enter: `Makes the scenario up with 3 shifts: …`, `Makes 8 scenario(s): …
  and 5 more. Price would then price 9 pairs of a case and a market, of 50 at most.`, `Price would leave the
  scenario up out; its shifts stay.`, `Removes the scenario up, with its 3 shifts.`, `Loads 2 scenario(s) from
  rates: up, down.`, `Saves 2 scenario(s) as the set rates, for every user.` The nine that write are a trial, as
  3a's verbs are; `scenario save` writes no workspace, and its reading keeps nothing.
- **What could follow**: the scenarios a verb would change, with their counts of shifts; the saved sets the user can
  see, with whom each is for and who saved it; `desk` and `all` after a set's name.
- **The scenarios are not a registered table.** The spec planned one; a name of several words, inclusion held as one
  list of the workspace and a scenario being its shifts are step 5's to hold (ADR-0016 note 32). `set s1 included
  off` and `remove s1` are therefore not read.
- **`price` is read without being tried.** The reading freezes the workspace and builds the Run's input, keeps and
  submits nothing, and says `Prices CVA and FVA for 3 cases on 2 markets (the base market and 1 scenario), as one
  Run.`, or refuses as Price refuses: held edits by name, too many pairs, an `add each` set that no longer matches.
  Enter submits one Run under the key the command was invoked with, so the same invocation sent twice is one Run.
- **`refresh`** says what it would read again, and its answer what carried; the notice stays while an edit is held.
  The log names it `refresh`, and `undo` takes it back. The grids start over from their first row only where what
  the trades were read of has moved (the counterparty, the value date or the source's version); a `refresh` that
  read the same version re-reads the rows in view where they are, where the Refresh button started the grids over
  every time.
- **`pv01 retry [trade]`** says the trades whose results failed before it asks, and that nothing would be asked
  where none has. It names the revision it was read at and is refused where the workspace has moved since.
- **A new effect, `execute`**, for a command that starts something `undo` does not take back (`price`, `pv01
  retry`). The gateway hands a handler the idempotency key it was invoked with.
- **The page's controls are the commands.** Price, Refresh, *Ask again*, and the Scenarios tab's include box,
  Remove, Duplicate… and Remove all. The Scenarios grid's own edits (typed, pasted, cleared) stay the page's.
  The dock shows the workspace's latest Run, whoever priced it.
- **The line lists the pricer's own commands first**, in the order its module registers them, `price` first; then
  the commands still written in the client, then the kernel's. The placeholder reads `price, vary, add each…`.
- **A command whose arguments may all be left out is read by its words alone**, and Enter runs what that reading
  showed: `pv01 retry`, and `reset` on the focused cell, without a space typed after them.
- **The AI** calls the same capabilities; `xva.workspace.price`, `xva.refresh`, `xva.pv01.retry`,
  `xva.scenario.duplicate` and `xva.scenarios.clear` are new to it, and the page reads the workspace again after
  each.
- **Gone:** the page's `/scenarios` (define), `/scenarios/generate`, `/include`, `/remove`, `/duplicate`, `/clear`,
  `/save` and `/load`, `GET /api/xva/scenario-sets`, `/price`, `/refresh` and `/retry`; the client's readers of a
  `scenario` line and of the generators, and its copies of the sentences; `caseCap` on the workspace's view, which
  only those readers read (the cap is said in the sentences that count against it).

### Verification performed

| What | Result |
|---|---|
| `dotnet test server/Qube.slnx` | 1041 passed |
| Client: `npm run typecheck`, `npm test`, `npm run build` | Clean; 290 passed in 33 files; built |
| `npx playwright test` (Chromium, headless, Linux) | 82 passed |
| The six browser tests this step adds or rewrites, with `--repeat-each=3` | 18 passed |

Seen once and not explained: one earlier run of the whole browser suite failed one test this step does not change,
"in the dark and the light theme every text on the XVA page reads at 4.5:1". `measures cva`, entered right after
`price`, was not answered within the five seconds the test waits, and the line stayed busy. The same test then
passed 37 times alone (25 of them with the processor and the disk kept busy) and in the whole run above, and the
server's logs of the failed run were not kept. That run was the only one made while other work used the same
machine. It is not known whether the server or the page was late; a recurrence should be run with a trace.

### Providers

Unchanged: every figure on these pages is simulated, and says so.

### Known limits

- What reading `price` costs on a workspace of 10,000 trades is not measured: the reading builds the Run's whole
  input each time the line is read. To be measured before the line is used at that size (ADR-0016 note 33).
- A scenario is named by its name alone: the kernel's `set`, `remove` and the row selector do not reach scenarios
  until they are a table (step 5).
- What the line showed of a generator while it was typed, part by part, is now the server's sentence, which is
  read once the line holds a generator whole; before that the line shows the server's reason.
- The client's `price` remains for the Rates page until Rates has its own (3c); on the pricer's page the word is
  the server's.
- A failed result cannot be produced in the browser with the simulated provider, so *Ask again* on a failed cell
  is held by the server's tests of `pv01 retry` and the door's unit tests, not by a browser test.
- The synthetic source holds no value that both moves with the value date and can be edited, so no edit of a
  priced trade can be held in the sample: that `price` names the held edits in its refusal is held by a test of
  the sentence alone, and the `refresh` that says it stays by an edit lost on a matured trade.
- In the sample, `refresh` never reads another version of the source, so the grids starting over after one is
  not exercised; a change of the value date or the counterparty, which starts them over itself, is.
- Still written in the client: `new xva`, `explain`, the trace options, `open result`, `dock full|restore`,
  `download run` and `copy quote` (note 31), and every command of Rates (3c).
- `add each` takes no list yet (3d). Rates has no door and no registered table (3c).

## Generic commands, delivery step 3c: Rates registers (ADR-0016 notes 34 to 36, 2026-11-11)

The third pull request of step 3 (`docs/specs/commands-3-domain-verbs-rates`, tickets 12 to 15). A Rates workspace
is reached as the pricer's is: its lines and its defaults are registered tables, its page has a door, a screen and
places, and its verbs are declared once on the server and read there. Every hand-written Rates command of the
client but `new rates` is gone, with eight page endpoints and the two product endpoints.

### What runs

- **`set`, `reset` and `remove` on a line** (`rates.lines`): `set L3 notional 5mm`, `set L3 rate 1.25%`, `set L3
  freq1 3m`, `set L3 fixed 6m`, `reset L3 end`, `remove L3`, and `set notional 5mm` on the lines selected in the
  grid. An IRS's terms are fields as `new irs` names them (`notional` writes both legs, `fixed` and `float` the
  fixed and the floating leg's frequency); a term that does not apply to a line is refused by the line's name, with
  the fields that do.
- **The defaults are set one at a time** (`rates.workspace`): `set default currency jpy`, `set default notional=500
  default direction=rec`, `reset default currency`. A new line takes them, and says so before Enter.
- **`undo` and `redo`** take back and put back each of these, a change of defaults included, from the line and from
  the grid's keys.
- **A live subscription follows a kernel verb's write**: the workspace service tells Rates' observer after each
  accepted command, so a `set` or an `undo` from the line reprices a ticking workspace.
- **`new <product> <terms>` is the kernel's `entity.create`, read on the server.** `new ` offers the products; after
  one, each field not yet typed (choices, an end's tenors, `name=`). The line shows how each word was read and
  what Enter would make (`Makes L3: Interest rate swap. Not typed: index TONA from …`), and says what is missing by
  name while it is typed. Nothing is sent to be refused.
- **`price`** names the market snapshot it read and how many lines it would price (`Prices 1 of 2 lines on market
  …, as one Run. Left out: L4.`); Enter prices on that snapshot, as one Run under the invocation's key. **`capture`**
  names the frame the subscription showed when the line was read, with its time, and saves that one. **`live
  start`** and **`live stop`** say what they would start or stop. **`add line <irs|ccs>`** adds an empty line.
- **The page's controls are the commands.** Add IRS, Add CCS, Price, the live toggle and Capture run them through
  the door; Price names the market the page shows and Capture the frame the page drew, where the typed `price`
  reads the latest and says which. A Run that `price` or `capture` made opens in its own panel, from the command's
  answer. The page follows the workspace's live subscription whoever started or stopped it.
- **The line says the Rates screen** (`On screen: lines, 2 selected, L3 · line`), `find L5` puts the Focus on a
  line, `go lines` and `go flows` put the keyboard in the lines and in an open line's cashflows, and the grid's
  Context Menu holds `Reset this cell · reset` and `Remove line · remove selected`.
- **`new rates` still opens its dialog**, beside the served `new`: a line that is exactly a client command's words
  is that command's.
- **The AI** calls the same capabilities; `rates.workspace.price`, `rates.frame.capture` and `rates.line.add` are
  new to it.
- **Gone:** `POST /api/rates/workspaces/{id}/lines`, `/lines/new`, `/lines/{line}/remove`, `/defaults`, `/price`
  and `/live`, `POST /api/rates/live/{id}/capture`, `DELETE /api/rates/live/{id}`, `GET /api/products` and `POST
  /api/products/{id}/preview`; the client's `set defaults`, `add line`, `live start`, `live stop`, `capture`,
  `price` on a Rates page and `new irs`/`new ccs`, with its own reader of a product's terms.

### Verification performed

| What | Result |
|---|---|
| `dotnet test server/Qube.slnx` | 1065 passed |
| Client: `npm run typecheck`, `npm test`, `npm run build` | Clean; 292 passed in 33 files; built |
| `npx playwright test` (Chromium, headless, Linux) | 85 passed |
| The six Rates browser tests this step adds or rewrites, with `--repeat-each=3`, before the review | 18 passed |
| The three Rates browser tests the review's changes add or extend, with `--repeat-each=3` | 9 passed |
| `credit.spec.ts`'s first test, whose `price` helper this step corrects, 14 times | 14 passed |
| The Price button's test against the first build's button, and the host's live test with no observer wired | Each failed, as it should |

A test of no part of this step failed in about one run of four: `credit.spec.ts`'s `price` helper read the result's
revision as painted, in capitals, and compared it with its text, so it never waited for the new Run and read the
figures of the Run before. It now waits for the line to close and for the revision of the workspace as it stands.

The code review (two readers: the repository's standards, and the spec) found two defects of the first build,
both corrected with a test that failed first: the Price button ran the typed `price` and so priced on the latest
snapshot, not the one the page shows; and a leg whose `Float` cell the grid had left empty made `set L3 rate 1%` a
server error, where it is now refused by name. It also found the two frequencies of `new irs` missing as fields, a
disabled Capture that did not say why, and each module's own copy of what a command reads of its request, now the
kernel's (`CommandRequests`).

Found while the pull request's pictures were taken, and corrected with a unit test: the Run panel's header wrote a
context value that is an object as `[object Object]` (a Rates Run's `defaults`). It now writes each value under
its path (`defaults.currency JPY`), and a list or an empty object as its JSON (`runFacts.ts`).

### Providers

Unchanged: the Rates market, its conventions' development profiles and every figure are simulated, and say so.

### Known limits

- The line grid's Context Menu is bounded by the grid's own box, as ExGrid places it (its ADR-0040). The grid shows
  three rows at least, so on a workspace of up to three lines the menu shows three of its four items at once and
  scrolls to `Remove line`. A taller grid would show all four and move the cashflows down (ADR-0015's layout).
- A leg whose `Float` cell is empty is priced as fixed, and its summary says `Fixed`: the resolver's reading from
  before this step, and a default of the kind CLAUDE.md now asks the owner to approve. A command does not use it (a
  term that depends on the flag is refused until the flag is said); whether the row should say the flag is missing
  is the owner's to decide.
- `new irs 5y` on a workspace with no defaults says six terms are missing, the index and the frequencies among
  them, though a currency alone would bring those from its profile: the reading does not know which missing terms
  a profile would supply once another is typed (ADR-0016 note 36).
- A line is named by the revision that makes it, so a workspace's first line is `L3` and its next `L5`.
- The valuation date is not a field: it stays the page's control, and `undo` takes its change back.
- The cashflows are a grid of no table: `set` and `find` do not reach a period, and with the keyboard there the
  line is told of no screen. A cashflow's edit and a held edit's resolution stay the page's own writes.
- `capture` from the line saves the frame the server showed when the line was read; the button saves the frame the
  page drew. The two are the same frame unless a tick fell between the drawing and the reading.
- What reading `price` costs is not measured here either; a Rates workspace holds few lines.
- Still written in the client: `new xva`, `new rates`, `explain`, the trace options, `open result`, `dock
  full|restore`, `download run` and `copy quote` (note 31).
- `add each` takes no list yet (3d). *Built since: the 3d section below.*

## Generic commands, delivery step 3d: `add each` takes lists (ADR-0016 §3, note 37, 2026-11-11)

The fourth and last pull request of step 3 (`docs/specs/commands-3-domain-verbs-rates`, tickets 16 to 19). A case
of `add each` may add several new trades together and take existing trades out of After, given as lists; the
pricer's page makes one from the two grids' selections. Step 3 of ADR-0016's Delivery order is built with it.

### What runs

- **A contract may hold a list** (`ContractChecks`): a field of `type: array` whose items are values, or records
  whose fields are values or lists of values, and nothing deeper. A list's problem is said at its path
  (`/inputs/request/iterations/0/add`). A workflow whose input contract holds a list is refused when it is
  registered (`ListInForm`), since its input is filled in a form; the client's form says a list it is handed and
  never fills or defaults one.
- **`xva.trades.add` takes `iterations`**: a list of `{ add, without }`, the new trades a case adds together and
  the existing trades it takes out of After. It is the whole set: the cases made before are replaced, the given
  iterations are made in their order, and a New row with A ticked that none names is one more case alone. Left
  out, the command is `add each` as typed, and the line still takes no argument.
- **`keep: true` makes the iterations given beside the cases made** (note 37, corrected in review): the cases of
  `add each` made before stay the cases they are and are not read again, so a stale one refuses no new case and
  stays Price's to refuse. One made already is refused by its case's title, a ticked New row no case names is one
  more case alone, and a candidate that joins the set is turned off in the cases kept.
- **Refused by name, with nothing made:** an unknown id, an existing trade named to be added or a New row named to
  be taken out, a candidate with A clear or B ticked, a trade taken out that the Base has not in After, an
  iteration that adds none, a trade twice in one list and the same iteration twice.
- **A case names its iteration** (`add` and `without`) in the workspace's view and in the input Price freezes. Its
  title is `add DRAFT-1`, or `add DRAFT-1 + DRAFT-2 without E00003`. A case stored before this step names one
  trade and is read as that trade alone.
- **Price's check of the set** reads every iteration's candidates together, and refuses a set that takes out a
  trade After no longer holds. Each case's After must hold its own candidates of the New rows and no other, and
  none of the trades the case takes out.
- **A view's selection read for a page** (`POST /api/workspaces/{id}/selection`): the keys a selection names, read
  as the preview reads `selected`, for a page whose grids the server pages. The view is checked whole first, so
  one that cannot be read is refused (400) whether or not it selects a row.
- **The page.** `Add each` in the New grid's caption runs the command with no value. `Make case…` beside it, and
  *Make case from the selection…* on both trade grids' Context Menus, open a dialog that lists what the New grid's
  selection adds and what the Existing grid's takes out; a trade pressed is left out, and *Make case* makes the
  case beside those made (`keep`). Refused, the dialog stays with the server's reason.
- **The Changes tab** lists a case's trades under it, each a button: one the case adds is left out, one it takes
  out is kept in After. The one trade a case adds has no button: the case is removed whole.
- **A request made in the command's turn** (`CommandDoor.invoke`): the Changes tab says the made iterations again
  with one change, read from the cases the page shows at the revision the command is sent at, so an edit still on
  its way when the button was pressed is in it.

### Verification performed

| What | Result |
|---|---|
| `dotnet test server/Qube.slnx` | 1078 passed |
| Client: `npm run typecheck`, `npm test`, `npm run build` | Clean; 297 passed in 34 files; built |
| `npx playwright test` (Chromium, headless, Linux) | 86 passed |
| The browser test this step adds, and the menu test it touches, with `--repeat-each=3` | 6 passed |

The browser test's first run failed, and found a defect of the first build: the wire leaves an empty field out,
and the page read a case's `iteration` as `null` where it has none, so a case that is no iteration was sent as an
empty item and the contract refused the list. A case's `iteration` and `note` are typed as left out now.

The figures above are of the branch after the code review (two reviewers, the repository's standards and the
spec). What the review changed: `keep` and *Make case* beside the cases made, the log's name, a view's selection
checked whole, Price's check of the trades a case takes out, the buttons' names, and the request's lists read
without a default (ADR-0016 note 37's corrections).

### Providers

Unchanged: every figure of the pricer is simulated, and says so.

### Known limits

- The line takes `add each` with no argument: how it spells an iteration's lists is open (ADR-0016 §3). The lists
  are given by the page's dialog, or by a caller of the command, such as the AI.
- `load trades <ids>` is not built: the pricer makes no trade of its own until step 5 (note 37).
- The dialog's case and the Changes tab's buttons have no line and no palette entry: how the line spells a list
  is open, so they are the page's gestures over a command the AI invokes with the same value.
- A trade left out, or kept in After, on the Changes tab says the whole set again: its notice names the whole set
  (`Made 4 case(s): …; replaced …`), `undo` takes the whole set back, and a stale case of the set refuses it by
  that case's trade, so the stale case is removed first. *Make case* has none of this: it names the case it made.
- A trade left out of what a case adds is still ticked, so it becomes a case alone; where what is left of the case
  is a case already made, the server refuses it and the case is removed whole instead. That refusal says
  `The iteration DRAFT-1 is given twice.`: the command's word, where the page says "case".
- No test reaches Price's refusal of a case whose After holds a trade it takes out: a case of the set is not
  edited, so no command leaves such a state. The check is there so that one is refused if a later change does.
- At a phone's width the dialog was opened from the caption's button and looked at; the Context Menu's item was
  not exercised there, and no browser test holds the phone's path.
- ExGrid bounds a Context Menu by its grid's box (its ADR-0040), and the New grid is as high as its rows: with few
  new trades its menu scrolls to *Make case from the selection…*, its last item. The caption's button is in view.
- Still written in the client: `new xva`, `new rates`, `explain`, the trace options, `open result`, `dock
  full|restore`, `download run` and `copy quote` (note 31).

## Generic commands, delivery step 4a: the AI describes, reads and previews (ADR-0016 §7, note 38, 2026-11-11)

The first of step 4's three pull requests (`docs/specs/commands-4-view-verbs-ai-reads`, tickets 01 to 05). An
agent, or any caller with no screen, can find out what a workspace holds, read its rows and ask what a command
would change, through three capabilities of the kernel that write nothing.

### What runs

- **A reading of a workspace is a command** (`CapabilityEffect.ReadWorkspace`): invoked through the gateway for
  the caller the server authenticated, offered to the agent as a tool, refused as a workflow step. The three
  declare no command form, so the line and the palette do not list them.
- **`tables.describe`**: the tables a workspace's application registered (id, title, words, key, how many rows,
  subsets, whether it is of one row, whether it is the main one) and their fields (form, how a value is written,
  whether a command sets it, whether it may be empty, its choices to fifty with the rest counted, the field that
  carries its unit, the table an id points into). One table alone where it is named.
- **`tables.read`**: the rows a selector names, as `entity.set` takes rows (`all`, a subset, ids, `where`), with
  the fields asked by their words. Each cell is the text `set` reads back (`5mm`, `empty`). The answer says how
  many rows matched, where the page starts and how many follow. With no field the rows are their keys.
- **The page is bounded**: 50 rows where no limit is given, at most 200, at most 2,000 cells (a page of twenty
  fields holds 100 rows). A limit over 200 is refused by the contract.
- **`commands.preview`**: a line as typed, read as the command line reads it: the command, the inputs that run it
  (JSON text, handed to the command's own tool as they are), each argument, the rows meant and what would change.
  Nothing runs. A line that cannot be read refuses the call with the word at fault and what it could have been.
- **Refused by name:** an unknown table, field, subset or row, a field asked twice, no rows, another user's
  workspace (`NotOwner`), a revision the workspace has left, and `selected`, `shown` and the verbs of a screen
  (`NoView`).
- **The agent's instructions** name the three by what they answer, where they are among its tools: read rather
  than guess, say a count as the tool gave it, preview when asked and before a command over many rows.
- **The stand-in agent** (no model configured) takes `describe the tables`, `read <rows> [fields <a>, <b>]` and
  `preview <line>` for the workspace the pane's context names, and says the count and the sentence the tool
  answered. What is said after a reading that was refused is a request of its own.
- **The pane** keeps a step's answer inside its card: a long one scrolls within 30% of the window's height and
  takes the keyboard.
- **How a value is written is said in one place** (`FieldForms.Takes`): the description's sentence and a refused
  value's. A choice's description says "one of its choices", with the values listed beside it.

### Verification performed

| What | Result |
|---|---|
| `dotnet test server/Qube.slnx` | 1106 passed |
| Client: `npm run typecheck`, `npm test`, `npm run build` | Clean; 297 passed in 34 files; built |
| `npx playwright test` (Chromium, headless, Linux) | 87 passed |
| The browser test this step adds, with `--repeat-each=3` | 3 passed |
| `python3 tools/validate_handoff.py` | Passed |
| The pane with a page of rows at 390, 768, 992 and 1440px, dark and light | Looked at; no sideways scroll, the block inside the pane |

The server's tests read the kernel's three capabilities over a registration with no financial word, then the
pricer's tables through the agent's endpoint, compared with what the grid's own page endpoint answers for the
same rows. The browser test reads a workspace of more than fifty trades through the stand-in agent.

Not verified: a real model using the three tools. No model is configured here, so what the agent does with a
description or a page of rows was exercised through the stand-in alone, which is scripted.

The figures above are of the branch after the code review (two reviewers, the repository's standards and the
spec). What the review changed: what follows a refused reading is no longer joined to it by the stand-in; `would`
is in every preview and a start no position holds is refused by the contract; a reading that names a command with
no inputs is a defect, not `NoView`; ADR-0005 carries the sixth effect; the documents no longer say that a key in
capitals is not found, or that `rows` is required; and tests hold the schema file's effects, an unknown workspace,
a condition no row meets and `shown` in a preview.

### Providers

Unchanged: every figure of the pricer is simulated, and says so. The agent is the scripted stand-in.

### Known limits

- A reading does not sort, and is not retained as a dataset a view binds: the pane shows its answer as JSON, as
  it shows any tool's (note 38, "left for later").
- The stand-in lower-cases what it is told. Rows, fields and choices read the same in either case; a value whose
  capitals matter (an identifier a `set` would write) reaches the tool in lower case, and its preview shows it
  so. The real agent passes words as they are.
- A choice's values beyond fifty are counted, not listed: the agent reads the rows to see them.
- The gateway remembers an answer by its idempotency key in memory, a page of rows included, as it does every
  command's answer.
- `sort`, `filter`, `select`, `copy` and `export` are step 4b's and 4c's.

## Generic commands, delivery step 4b: `sort` and `filter` (ADR-0016 notes 39 to 41, 2026-11-11)

The second of step 4's three pull requests (`docs/specs/commands-4-view-verbs-ai-reads`, tickets 06 to 10). With
the keyboard in a grid of a table, the line sorts that grid and shows the rows conditions leave; `shown` is then
those rows.

### What runs

- **A verb of the screen asks its page for one thing**: a place (`find`), a sort or a filter. The server reads the
  line and answers the ask with its sentence; the page's door gives it to the grid the line's view was read of. No
  command runs, nothing is logged, and the agent's tools hold none of the three.
- **`sort by <field> [desc] [, <field> [desc]]`** and **`sort off`** set the grid's own sort, which its header
  shows. The pricer's trade grids ask their reader, which refuses a field it does not sort by before the page is
  asked; the CSA grid sorts the rows it holds. The Rates line grid shows a term as typed and sorts by none, so a
  sort of it is refused on the line by the column's name.
- **`filter where <conditions>`** and **`filter off`**. The conditions are the selector's (`ccy=USD and
  notional>50mm`, `maturity<1y`), kept as typed in the grid and read again by the server at each reading: a row
  edited out of them leaves the grid. The reading says how many rows they leave before Enter ("17 of 43").
- **A filtered grid says so** beside its title: the conditions, how many rows of how many, and a Filter off button.
- **`shown` is what the filter leaves**: `set shown a off` writes those rows alone, a position selected in the grid
  is one among them, and the line's first sentence says "filtered to 17".
- **A grid the server pages** (the trade grids): the page endpoint takes the conditions and answers the rows left,
  their count, the count with none and a token of that order. A column's own filter panel is still refused there.
- **A grid whose page holds its rows** (the CSAs, the Rates lines): the page asks the server which rows the
  conditions leave at each revision it shows (`POST /api/workspaces/{id}/shown`); the client reads no condition.
  Its view says the conditions and names no row for them, so a line's `shown` is what the server reads them to
  leave at the revision the line is read at, whatever the grid has drawn. While it waits for an answer the grid
  keeps what the server last said.
- **Refused by name:** no grid behind the line (`NoView`), another table's or subset's grid (`OtherGrid`), a field
  the table has not, a field its reader does not sort by (`NotSortable`), conditions `where` would refuse, and
  conditions that can no longer be read (`FilterUnread`), which are never read as none.

### Measurements

Release build on this Linux container, the SQLite store, one request at a time, a workspace of 10,009 existing
trades (14,286 requested), the Existing grid sorted by notional. Recorded, not gated.

| What | Time |
|---|---|
| A page with no conditions, sorted | 7 to 8 ms |
| A page under `ccy=USD` (4,011 rows left): the first, then again | 84 ms, then 15 to 53 ms |
| A page under `maturity<2y and product=IRS` (4,428 left) | 15 to 57 ms |
| The reading of `filter where ccy=USD` | 9 to 62 ms |
| The reading of `set shown a off` under `ccy=USD` (71 KB), then Enter | 46 to 129 ms, 190 ms |
| The first page after that edit, then again | 273 ms, then 24 to 25 ms |
| `POST /api/workspaces/{id}/shown` for the CSAs | 6 ms |

The conditions are read again at each page and nothing is kept between pages.

### Verification performed

| What | Result |
|---|---|
| `dotnet test server/Qube.slnx` | 1158 passed |
| Client: `npm run typecheck`, `npm test`, `npm run build` | Clean; 317 passed in 35 files; built |
| `npx playwright test` (Chromium, headless, Linux) | 90 passed |
| The three browser tests this step adds, with `--repeat-each=3` | 9 passed |
| `python3 tools/validate_handoff.py` | Passed |
| A filtered trade grid, CSA grid and Rates line grid at 390, 768, 992 and 1440px, dark and light | Looked at; no sideways scroll, the filter's line inside the page |

The server's tests read the two verbs and a view's conditions over a registration with no financial word, then the
pricer's page endpoint and `/shown` through the host. The browser tests sort and filter a trade grid from the line,
write `shown` under the filter, edit a row out of it and take the filter off by the line and by the button; filter
the CSA grid with a line chained on it; and filter the Rates line grid. One step holds the page's own question
back, so that the CSA grid still draws the rows it last heard of while the line reads the rows the conditions now
leave: what a line reads is the server's, not the grid's progress.

The code review (two readers, the standards and the spec) found that last case as built: a row an edit had just
brought into a held grid's conditions was missed by a chained `set shown` until the page had heard of it. It was
corrected before the pull request, with the token of a filtered order, which moved with rows the filter leaves out
(ADR-0016 note 41, "Corrected in review"). A test of the line's list (`workbench.spec.ts`) failed once in a full
run: it read the list before the Rates page had drawn its grid, and now waits for the grid.

### Providers

Unchanged: every figure of the pricer and of Rates is simulated, and says so.

### Known limits

- The Rates line grid is not sorted from the line (see above). Sorting it by what a term means is left for later.
- A trade grid whose read fails under its conditions keeps the rows it last read, with why in the page's alert, as
  when any read fails. A grid whose page holds its rows shows no row then.
- The caption says the conditions as typed, and the notice as the server read them (`ccy=USD`, `ccy = USD`).
- A column's own filter panel is refused on the trade grids; on the CSA grid it works beside the line's filter.
  The grid then names its rows to the line, and after each edit it is given every row until the server has
  answered for the revision: it is drawn twice at each edit. What that does to its Selection was not looked at.
- Conditions are joined by `and` alone (ADR-0016, Rejected, "A full query language").
- A sort and a filter last as long as the panel (ADR-0017 §1): neither is kept with the workspace or for the user.
- `select`, `copy` and `export` are step 4c's.

## Generic commands, delivery step 4c: `select`, `copy` and `export` (ADR-0016 notes 42 to 44, 2026-11-11)

The third of step 4's pull requests (`docs/specs/commands-4-view-verbs-ai-reads`, tickets 11 to 14). The line places
a grid's Selection on rows it names, puts rows on the clipboard as cells a spreadsheet reads, and saves them as a
CSV file.

### What runs

- **One thing asked of the page, said by its kind.** A view verb's reading holds `asks` with `kind`: `place`,
  `sort`, `filter`, `select`, `copy` or `export` (note 42), in place of three members of which one was there.
- **`select <rows>`** places the Selection of the grid behind the line: whole rows, with the Focus on the first of
  them in the column it was on. The rows are the selector's (`select where ccy=USD`, `select CSA-A, CSA-B`);
  conditions said alone are asked of the rows the grid shows. A chained line that names no rows then acts on the
  rows selected (`set a off`, `copy`).
- **One run of rows.** ExGrid places one range (ex-grid issue #561 asks for several). Rows that are not next to
  one another are refused with how many runs they are in (`SeveralRuns`); rows of another table or subset, and rows
  the grid's filter leaves out, are refused by name (`OtherGrid`, `NotOnScreen`). Where the server pages the grid
  (the trade grids) it answers the run's first position in the view's order; where the page holds the rows (the
  CSAs, the Rates lines) it answers their keys, and the page says whether its grid shows them next to one another.
- **`copy [<rows>] [<fields>] [with headers]`** puts cells on the clipboard: tabs between cells, CRLF between
  lines, no header line unless asked for. Rows left out are the Selection, or the row the keyboard is on.
- **`export [<table>] [<rows>] [<fields>]`** saves a CSV file (RFC 4180) named by the table's word (`trades.csv`),
  always with a header line, in UTF-8 with a byte order mark. Rows left out are the rows the grid behind the line
  shows; a table named alone is every row of it, and needs no grid.
- **A cell is written as a spreadsheet reads the value held**: an amount, a number and a count of days in full with
  no grouping (`50000000`), a rate and a spread with the percent sign (`1.25%`), a flag `true` or `false`, a date
  `yyyy-mm-dd`, no value empty. A header is the word a command names the field by (`trade id`, `notional`). A line
  of one empty cell is written `""`, so that no row is a blank line.
- **The cells are asked for at Enter** (`POST /api/commands/content`): the server reads the line again at the
  revision of the reading, refused where the workspace has moved, and the page's door writes the clipboard or saves
  the file, then says the server's sentence. Nothing is logged or undone, and the agent's tools hold none of the
  three (it has `tables.read`).
- **Refused by name:** more than 1,000,000 cells (`TooManyCells`, never cut), no row to write (`NoRowMatches`), a
  field named twice, a field the table has not, no rows and no grid, and a clipboard the browser does not give.
- **More words that name a field are that field**: `trade id` is the trades' field although `trade` is their
  table's word. This holds for every verb that reads rows (note 44, "Changed as built").

### Verification performed

| What | Result |
|---|---|
| `dotnet test server/Qube.slnx` | 1223 passed |
| Client: `npm run typecheck`, `npm test`, `npm run build` | Clean; 328 passed in 35 files; built |
| `npx playwright test` (Chromium, headless, Linux) | 93 passed |
| The three browser tests this step adds, with `--repeat-each=3` | 9 passed |
| `python3 tools/validate_handoff.py` | Passed |
| The line's readings and a Selection placed by the line at 390, 768, 992 and 1440px, dark and light | Looked at; no sideways scroll, the line's reading wraps inside the page |

The server's tests read the three verbs over a registration with no financial word (a paged view, a held view, the
refusals, the cells and the file's text), then the preview and the content endpoint through the host with the
pricer's trades. The client's tests give the door a clipboard and a file of their own. The browser tests select a
run of trades from the line, write the rows selected with a line that names none, read the clipboard back after
`copy`, and read the file `export` saves (its byte order mark, header line, line ends and the grid's order); select
and copy CSAs, whose page holds every row; and select and export Rates lines.

### Providers

Unchanged: every figure of the pricer and of Rates is simulated, and says so.

### Known limits

- A Selection of several ranges is not placed (ex-grid #561), and the line does not take the Selection off
  (`select off`): ExGrid has no call that leaves none.
- `select all` and `select <subset>` say rows of the table, as they do for every verb: behind a grid that shows
  fewer they are refused, naming the rows it does not show, and `select shown` is the way.
- Rows said as the screen's are written in the grid's order where the server pages the grid. Where the page holds
  its rows and sorts them itself (the CSAs, the Rates lines), the Selection is written in the grid's order, and so
  is `shown` where the grid's own column filters narrow it; with no such filter, `shown` and the rows `export` takes
  when none are said come in the table's order, whatever the grid is sorted by.
- The door cannot hear whether the browser saved a file: it says the server's sentence once the file is handed
  over. The clipboard does answer.
- A text is written as it is: a spreadsheet reads one that begins with `=`, `+`, `-` or `@` as a formula, and may
  drop the leading zeros of an id made of digits. What to do about it is put to the owner (note 44).
- An amount copied does not read back as an amount typed here: a bare number is millions (ADR-0008).
- The clipboard was exercised in Chromium alone. A browser gives it only over https or on localhost, and one that
  wants it written inside the key press itself may refuse it after the server's answer: `copy` then says the
  browser's reason, and `export` still works.
- The most cells is the kernel's constant; no host setting names it.
- The grid scrolls as far as shows the Focus, which is on the first row selected: a run that begins below the rows
  drawn is seen from its first row at the foot of the grid.
- A misspelt field after a table's word (`export stations colour`) is refused as a row that is not there.

### What the review changed

The two reviews of this step (standards and spec) found, and this pull request corrects: `select where …` refused
ADR-0016 §9's own chain, since conditions said alone meant the whole table (they are now asked of the grid's rows,
for `select` alone); a line of one empty cell was a blank line, and `copy` of one empty cell an empty clipboard said
as copied (now `""`); a short form that stood for a line of `export` was read and then refused at Enter; the list
offered a field for `export` as if it took the Selection, offered none after a comma with the rows left out, and
offered fields after `copy <table>`, which is always refused. Each is said in notes 43 and 44 as "Corrected in
review", with a test.

## Generic commands, delivery step 4d: what note 28 left (ADR-0016 note 45, 2026-11-11)

The fourth and last of step 4's pull requests (`docs/specs/commands-4-view-verbs-ai-reads`, tickets 15 to 18). A
domain verb's refusal names the word it refuses and what it could have been, and `vary` offers what could follow.
Step 4 is whole with this.

### What runs

- **A workspace's refusal may name its word** (`WorkspaceRefused.Word`, `Candidates`). A reading marks the word
  where it stands once after the command's own words, with its place in the line, and answers the candidates, as
  it does for a table's refusal. The line selects the word after Enter, or leaves the caret after it where the word
  ends the line, as it already did for the kernel's verbs.
- **The pricer's refusals name theirs**, in the one place the line and the capability plan from:
  - `vary CSA-Z threshold 5mm` names `CSA-Z`, with the rows a cell is named on (the CSAs, the netting set,
    `credit`, `cds` where a curve is chosen, the modules with settings; a trade's id is a row and is not listed).
  - `vary CSA-A colour 5mm` names `colour`, with the CSA's columns; a column of several words is named whole.
  - `vary CSA-A currency XXX, USD` names `XXX`, with the currencies; a value of an amount is named with nothing to
    choose from, and `10,000k` is named as the text that could be one value or two.
  - `measures cva dva2` names `dva2`, with the measures registered.
  - `cds` names a single name the catalogue has not, with each name's words, words that name several, with those,
    and a word that is no sector, region or rating, with the values of the kinds not given yet.
  - `case remove` names a case that is not there, with the cases' titles; a scenario's name that is not there is
    named with the scenarios the workspace has.
- **The AI is told the same.** `commands.preview` refuses such a line with the word and the candidates on its
  issue. A capability invoked with the same words fails by its code and sentence, as before.
- **`vary` completes.** After `vary`: the rows that are no trade, then trades, found by how an id starts or ends,
  twenty at a time with how many more, each with what it is. After a row: its columns, found by any of their names as far as typed and offered by
  the word that names each, a column of several words written from its first. After a flag or a choice: its values,
  and again after each comma without those already listed; `empty` where the column may be empty. An amount, a
  rate and an id are typed freely.
- **One function says what a field has to choose from** (`FieldForms.ChoicesWritten`): `set`'s completion and
  `vary`'s both ask it, and a value `vary` refuses is told it. A choice registered with nothing to choose from fails
  when its table is read, naming the field.

### Verification performed

| What | Result |
|---|---|
| `dotnet test server/Qube.slnx` | 1266 passed |
| Client: `npm run typecheck`, `npm test`, `npm run build` | Clean; 328 passed in 35 files; built (the client is unchanged) |
| `npx playwright test` (Chromium, headless, Linux) | 93 passed |
| `python3 tools/validate_handoff.py` | Passed |
| A refused word selected after Enter and `vary`'s lists at 1440px, dark and light, and the list at 390px | Looked at; the list and the sentence stay inside the line's panel |

The server's tests read a command over a registration with no financial word whose reader refuses with a word
(marked, with candidates; not marked where it stands twice), then each of the pricer's refusals with its word, its
place and its candidates, what `vary` offers word by word, and the AI's preview through the agent's door. No browser
test is added: that the line selects a marked word and lists what a reading offers is held by the tests of steps 1
and 3.

### Providers

Unchanged: every figure of the pricer and of Rates is simulated, and says so.

### Known limits

- The line does not draw a refusal's candidates: it selects the word, and its list is the completion's. The
  candidates are in the preview for the AI.
- A word that stands twice after the command's words is not marked, as for the kernel's verbs.
- `vary` offers no value for a trade's CSA or a curve, which are ids typed freely; `set` offers a trade's CSAs,
  since its field says the table it points into.
- Rates' verbs and the scenario verbs' own words already refused with their word; nothing of theirs changed.
- No test holds a row, a column or a case that names two things (`AmbiguousCell`, `AmbiguousCase`): nothing in the
  sample's own data makes one.
- A refusal names one word: of several measures that are not registered, the first.

### What the review changed

The two reviews of this step (standards and spec) found, and this pull request corrects: a value `vary` cannot read
named no word, though it is one of the line's; `cds`'s single name was named with one space between its words, so
words typed with two were not marked; `case remove` with a name and no case named none; rows were offered by how an
id starts alone, where `set` offers them by how it starts or ends; a value listed was left out only as it was
written (`yes` still offered `on`); the sentence for a row that is not there listed `cds` with no curve chosen; and
a choice registered with nothing to choose from would have thrown at each key typed, where it now fails when its
table is read. Each is said in note 45 as "Corrected in review", with a test.

## Dates read yyyy-MM-dd (design guidelines §10, ADR-0009, 2026-11-11)

### What runs

- Every date field draws and takes its date as `yyyy-MM-dd`, whatever the browser's locale: the header's As of, a
  workflow's date in the run dialog, the XVA toolbar's Value date, the Rates toolbar's Valuation date, and the New XVA
  and New Rates workspace dialogs. Each was a native `<input type="date">`, which drew `11/11/2026` in en-US and
  `2026/11/11` in ja-JP.
- They are `IsoDateInput`: a text field, with Mantine's `DatePicker` in a popover that a click or a tap opens on the
  month the text names. The field is its text: text that is not a date that exists (`2026-02-30`) stays as typed,
  is marked invalid, and is said where its holder says things: the header's context issues, the dialog's field, and
  for the toolbars' dates (`CommittedDateInput`, moved when the field is left or Enter is pressed) a refused notice
  that names the date that stays. Escape with the calendar open closes the calendar only; Enter closes it too.
- New dependencies, pinned exactly: `@mantine/dates` 9.7.4 (Mantine's own version) and `dayjs` 1.11.23, its peer.

### Verification performed

| Suite | Result |
|---|---|
| `npm run typecheck`, `npm test` (Vitest) | Clean; 328 passed |
| `npx playwright test --grep "As of field\|Escape shuts" --repeat-each=3` (Chromium, headless, Linux) | 6 passed: the two new tests |
| `npx playwright test` (Chromium, headless, Linux) | 95 passed |

The header was looked at in screenshots at 390, 768, 992 and 1440px in both themes, with the calendar open, and the
New Rates dialog and the Rates toolbar at 1440px.

### Known limits

- The calendar's days are reached by pointer or touch; the keyboard types the date. The native input's ArrowUp and
  ArrowDown, which stepped a part of the date, are gone.

## A user's settings: the store, preferences, defaults and the AI's model (ADR-0017, 2026-11-11)

One pull request in three steps (`docs/specs/user-settings`, tickets 01 to 09). What a user prefers, and the values a
workspace they make starts with, are kept by the server for that user and follow them to any browser. They are
changed by the line, the settings dialog and the AI through the same three commands.

### What runs

- **One store that knows no setting.** A module registers its settings (`ISettingCatalog`) with a key or a family of
  keys (`grid.widths.*`), words, a kind (preference or default), a form and a registered value. The kernel keeps a
  user's values in SQLite's `UserSetting` beside `SavedItem`, checks each against its form, and refuses an
  unregistered key or a value the form does not take by name. A value stored that the form no longer takes is never
  replaced: it is read as unreadable, a preference then applies its registered value and the dialog says that theirs
  is unreadable, and a default refuses the making that needs it, naming the setting and how to reset it.
- **Three commands through the gateway**: `settings.read`, `settings.set` and `settings.reset`, with their own effect
  (`UserSetting`) and permissions (`readSettings`, `editSettings`). The caller is the server's, never an input. A set
  or a reset answers what applies now, what it replaced and the line that takes it back. The page reads them at start
  with `GET /api/settings`, which answers as `settings.read` does. The agent is offered them as it is every command,
  and a setting it changes applies at once on the page.
- **The line and the account menu**: `settings` opens the settings dialog; `settings <name> <value>` and
  `settings reset <name>` set and reset, with names and values completed. The header's account menu, which took the
  place of its theme button at the owner's request, opens as the mouse rests on the user's initials, leaving the
  keyboard where it was, and is held open by a click, a tap or Enter, with the keyboard in it; a second click closes
  it. It names the user and their desk (`GET /api/me`), opens Settings, chooses the theme among the three, and says
  on an item the keyboard and a screen reader reach that signing out waits for sign-in. The dialog lists each setting with its value, whether the user set it or
  the registered value applies, and a reset; on a phone it keeps two columns and says the source and the reset under
  the setting's name.
- **Preferences** (step 1, step 3): the theme (`dark`, `light`, `auto`, which follows the device), a grid's column
  widths, the command ranking and, where models are configured, the AI's default model. The page's own writes (a
  theme toggled, a column dragged, a command run) are ordered per setting with the line's and the dialog's, and only
  the last one made settles what applies. The theme the browser last drew is kept there for the first frame only.
- **Defaults** (step 2): the pricer's display currency (registered as the company profile's, JPY) and a new Rates
  line's currency, notional and direction (registered as none). The making of a workspace copies the ones the user
  set: the pricer as the making's own edit, which `undo` never takes, and Rates into the workspace's defaults with a
  record of which came from the settings. The page says where each came from, where it is read on every width: the
  pricer's status line (`Display currency USD, from your settings as this workspace was made.`), the Rates status
  line (`New lines default to JPY · 500mm · receive from your settings.`), and a new line's preview (`currency JPY from
  settings`). A workspace made before a setting moved keeps what it was made with; Price reads only the workspace.
- **The AI's model** (step 3): configuration lists the models the AI may use (`Agent:Copilot:Models`); the first is
  the default model's registered value. A conversation holds its model from its first turn: one chosen in the pane
  (its Model menu, or `/model <id>`), else the user's default. A chosen model not configured is refused with the list;
  a default no longer configured gives way to the registered one. A choice changes neither the default nor another
  conversation. Each run says its model in `RUN_STARTED`, its trace (`qube.agent.model`), its failure and its card
  (`Copilot on gpt-5.1`). The scripted stand-in runs on no model, registers no default model and refuses one chosen.

### Verification performed

| What | Result |
|---|---|
| `dotnet test server/Qube.slnx` | 1296 passed |
| Client: `npm run typecheck`, `npm test`, `npm run build` | Clean; 347 passed in 36 files; built |
| `npx playwright test` (Chromium, headless, Linux), after main's yyyy-MM-dd dates were merged in | 106 passed |
| `e2e/settings.spec.ts`, which this pull request adds, with `--repeat-each=3` | 33 passed |
| `python3 tools/validate_handoff.py` | Passed |
| The settings dialog and the account menu at 390, 768, 992 and 1440px, dark and light; the AI pane at 390 and 1440px | Looked at; no sideways scroll, nothing cut off |
| The AI pane against a stand-in Copilot host with two models | Looked at: a conversation on the default model, `/model gpt-5.1`, and the next run on it |

The server's tests read the store over a registration with no domain (`UserSettingsTests`), the commands through the
gateway and the host, the defaults through the pricer's and Rates' makings (`PricerDefaultsTests`,
`RatesLineCommandTests`) and the models through the agent's endpoint with a runtime that has models
(`CopilotRuntimeTests`). The client's tests order the page's writes, the theme's double toggle, a setting the AI
changes, an unreadable ranking and theme, and the conversation's model. The browser tests carry the theme and the
ranking to another browser, follow the device's scheme, set and reset from the line and the dialog, keep a dragged
width across a reload and another workspace, make a pricer and a Rates workspace from the user's defaults, and fit
the dialog on a phone.

### Providers

Unchanged: every figure of the pricer and of Rates is simulated, and says so. The browser tests run the scripted
agent, which has no model; the model choice was looked at against a stand-in host that answers as Copilot's does,
not against Copilot.

### Known limits

- The user is `Qube:DemoUser` until sign-in (C30): every browser of one host is one user's.
- Sort and filter are not remembered (ADR-0017 §6.4).
- Column widths a browser dragged before were kept per panel and are not carried over.
- A conflict kept by its holder re-writes the display currency's edit, so a workspace whose currency was copied from
  the settings then says it was chosen in the workspace.
- Model ids are compared exactly; a value typed on the line is matched to one ignoring case.
- `Agent:Copilot:Model`, the one model of before, stops startup and says to list it (README, "Coming from before the
  user's settings").
- The account menu's avatar shows the initials of the user's id. A name and a photo come with sign-in, from MSAL's
  ID token and Microsoft Graph in the browser; `GET /api/me` stays the server's word on the id and the desk.

### What the review changed

Each step was reviewed on its two axes (standards and spec) and every finding was corrected. Step 1: the page's
writes are ordered per setting, so a drag made while an earlier width was being kept is no longer lost; the AI's
changes apply at once; a ranking no longer taken is neither applied nor written over; the workbench opens on the
registered values when settings cannot be read, and says so. Step 2: the page says each default's source where it
is read on every width, not in a title only; the display currency registers the profile's; the services take the
settings as a required dependency; a default no longer taken refuses the making in the kernel; a Rates default's
rule lives once; `undo` of a Rates default set in the workspace gives the settings' back. Step 3: an unreadable
preference gives way to its registered value on the page as on the server; the default model is registered from the
runtime's own models; a conversation's model is held by its thread; the pane offers `/model <id>`; the dialog fits a
phone. The account menu: the menu opens on its own delays instead of Mantine's `click-hover`, whose open delay
outlived an item's close and opened the menu again over the settings a tap on a phone had just opened; the theme is
chosen among radio items that say which is checked; Sign out is reached by the arrows and describes why it waits;
the user who cannot be read is said, never stood in for; a second click closes the menu.

## Generic commands, delivery step 5a: rows under a row (ADR-0016 notes 46 to 49, 2026-11-11)

The first of step 5's four pull requests (`docs/specs/commands-5-drafts-rows-under-a-row`, tickets 01 to 08). A
table may be under another's rows, a field may be a figure of the rows under its row, and a number may be written
with what it is in. The pricer registers a trade's PV legs and independent amounts, and a trade's PV as its legs'
in the display currency.

### What runs

- **A table under another's rows** (`TableDescriptor.Under`, `ITableRow.Under`). A row's key is whole in its
  table (`E00003/1`). The selector reads an id first among the table's own rows, then among the rows above, where
  it means every row under that one: `legs E00003/1` is a leg, `legs E00003` the trade's legs.
- **A figure of the rows under a row** (`TableField.Figure`, `Unit`). It reads as a number, sorts and is compared
  as one, and is written as the table decides: `set` reads the value as the figure says it is written, asks the
  table for each row's changes, and counts a row with none as holding it. For a line that writes one row, the
  preview says what the write does to the rows under it (`IWorkspaceTable.SetSays`); for several it says how many.
- **A number with what it is in** (`FieldForm.Money`): `-350,000 USD`, or the number alone. A unit that is not one
  of the field's is refused with the units it takes.
- **`new` makes a row under a row** (`ProductDescriptor.Table`, `Under`): the first word after the product's is
  the row above where it names one (`new leg E00003 eur 250,000`), and typed by its name (`trade=E00003`) it is
  read the same way. `remove` takes one away.
- **The pricer's legs and independent amounts** (`xva.legs`, `xva.amounts`), read from the synthetic source: a
  swap has two legs in its currency, an FX forward one in each currency, and about a third of the trades hold an
  independent amount or two. A leg's currency, PV and notional are edited as any field is, and `reset` and `undo`
  take an edit back. A leg of the source is taken out by an edit of its own, so `reset` and `undo` bring it back;
  a leg made in the workspace is removed. A new trade has no leg until one is made.
- **A trade's PV** (`pv`) is its legs' PVs in the display currency at the workspace's value date, absent where the
  trade has no leg. `set E00003 pv -350,000 USD` leaves the trade one leg of that amount, in the display currency
  where none is written; the line says `2 legs replaced by 1 leg, USD` before Enter. `reset E00003 pv` and `undo`
  bring the legs back. `independent amount` is a figure too, read-only.
- **Both trade grids show PV**, after B and A, the header naming the currency the page of rows says its PVs are
  in (`PV (JPY)`). A PV typed in a cell is the same `set`, and a cell committed as it was shown writes nothing.
  The server sorts by it, and a PV that differs from what the source's legs give is marked against Original, its
  cell saying what Original held, that no measure reads the legs, and what Price reads instead. The Changes
  tab lists each leg edited, taken out and made, and resets each; a leg made counts among the workspace's edits.
- **The AI reads the same**: `tables_describe` says which table is under which and which field is a figure of
  which, with its unit and how it is written; `tables_read` reads `legs E00003`.

### Verification performed

| What | Result |
|---|---|
| `dotnet test server/Qube.slnx` | 1383 passed |
| Client: `npm run typecheck`, `npm test`, `npm run build` | Clean; 351 passed in 36 files; built |
| `npx playwright test` (Chromium, headless, Linux), after main's dates and settings were merged in | 107 passed |
| `python3 tools/validate_handoff.py` | Passed |
| The PV column, a PV overwritten and the Changes tab at 390, 768, 992 and 1440px, dark and light | Looked at; the column comes into view with the Focus at every width, and the Changes tab's rows wrap at 390px |

The kernel's tests read a table under the stations and a figure of it, registered only for the tests that ask for
them: the selector by a row's own id and by the row above, a figure read and compared, a figure written
with and without its unit, the preview's sentence, `new` under a row and `remove`. The pricer's read the source's
legs, each write of a leg and of a PV with its `undo` and `reset`, the display currency's figure worked by hand
from the synthetic market, a value date and a counterparty changed under made legs, the Changes tab and the page's
rows, and one flow over HTTP. One browser test types a PV in a cell, one the server refuses and one left as it was
shown, overwrites another from the line and takes it back.

The tests of the pricer's write, reset and remove were written from the spec in the same step as their code, so
no run showed them red first. They were checked afterwards by breaking the code three ways (a leg not taken out,
a PV not converted, a made leg not removed): seven tests failed, and passed again with the code restored.

### What the review changed

The two reviews of this step (standards and spec) found, and this pull request corrects:

- A PV cell committed as it was shown (F2, Enter) wrote the rounded figure back and left the trade one leg. It
  writes nothing now.
- `ia` was a word of the amounts' table and of the trade's field: `remove ia <amount>` was refused as ambiguous
  with a trade grid behind the line. It is the table's alone.
- `new leg trade=E00004 …` was refused where `new leg E00004 …` was read. The term typed by its name now names the
  row above as the first word does.
- The cell of an overwritten PV named SA-CCR alone as what reads the official PV; the simulated XVA figures read it
  too, and a new trade's PV Adj. The cell says what Price reads.
- The PV header took its currency from the workspace's view while the rows came from another answer, so the two
  could disagree between the answers. Each page of rows says its currency (`pvCurrency`), and the header takes it.
- A money field registered with nothing it may be in, and a product whose table its application does not
  register, failed at the first use; they fail when the tables and the products are read.
- `set all pv` read the workspace's made legs once for each trade; they are read once for a state.
- The kernel's comments named the pricer's things; the conversion and the reset's rule were each written twice;
  three helpers had no caller. The independent amount's test recomputed what the code computes, and now holds
  figures worked by hand; the kernel had no test that a condition compares a figure.

Each behaviour corrected is said in notes 47 to 49 as "Corrected in review", with a test. Left as they are: a
table under a trade throws for a row it cannot remove, where the kernel has refused already; the spec's `Status`
line is free text, as the specs of steps 3 and 4 are; and a row read for a measure carries no PV, as it carries no
cells.

### Providers

Unchanged: every figure of the pricer and of Rates is simulated, and says so. The legs, the independent amounts
and the market that converts them are synthetic.

### Known limits

- **No measure reads the legs.** The simulated XVA figures and SA-CCR's replacement cost read an existing trade's
  official PV; a new trade is taken at market, or at its PV Adj where XVA's PV adjustment is on; spot IM reads
  sensitivities. A PV overwritten changes the grid's PV and nothing Price answers, and the cell of such a PV says
  so. What Price freezes carried neither the legs nor the PV in this step; since 5b it carries the legs (below).
  The owner put the measures off until the XVA analytics is connected (2026-11-12, note 49).
- A leg's and an independent amount's fields are typed in a command; no grid shows them until the trade's dialog
  (5c). An independent amount is read and removed, never made or edited.
- A PV is converted at the synthetic market's rates of the value date, and a leg's currency is one of the four
  that market has: no other rate, day or source stands in for one.
- An FX forward's first leg is worked from the official PV at the value date's rates, so it moves with the value
  date, and an edit of it is then a conflict to resolve, as any edit of a value the source moved.
- A leg made under a trade that a later value date no longer has stays in the Changes tab until it is reset.
- A leg made is numbered after the last of the source's and of those the workspace holds, so each overwrite's
  leg takes the next number: after `set pv` twice on a swap, its leg is the fourth. A made leg removed by `remove`
  leaves its number to the next leg made, which holds none of its edits.
- A PV cell is compared with what it showed as text: the same figure typed again is no write, and the same figure
  with its currency is one.
- A figure is no cell of `vary`: a case of another PV is not made.

## Generic commands, delivery step 5b: templates and the fields beyond the grids (ADR-0016 notes 50 to 53, 2026-11-12)

The second of step 5's four pull requests (`docs/specs/commands-5-drafts-rows-under-a-row`, tickets 09 to 12). A
table may register templates, which `set <rows> as <template>` writes over rows; the pricer's trade and CSA carry
the fields the owner's examples show beyond the grids, each under a group; and what Price freezes carries what the
user set.

### What runs

- **A template** (`TableDescriptor.Templates`, `IWorkspaceTable.TemplateValues`): a name, the words it answers to, the
  terms it takes and the fields it writes. `set CSA-B as 2way usd` writes them as several values are written: in
  the template's order, each checked, all or none, one command and one `undo`. Its terms are read as a product's
  are, by name or by form in any order (`ccy=USD`, `usd`). Before Enter the line says each cell that would change
  and how many hold their value already; after, `Set CSA-B as 2way currency=USD.` The line completes `as`, the
  templates and their terms; `tables_describe` lists them; a template that writes a field its table has not, one
  no command sets or a figure fails when the tables are read.
- **The CSA's fields beyond the grid**, read from the synthetic source: its general terms (counterparty, netting
  set, agreement name, enforceable, main, user-defined), which no command sets, and for each side whether it
  posts, its VM cap and the cap's currency, its independent amount, its rounding and how a delivery and a return
  are rounded, set as any field and said with the side (`set CSA-B self vm cap 5mm`, `cpty posts=off`). A word
  the sides share is refused alone with the two it could be.
- **The CSA's four templates**: `2way`, `1way-self`, `1way-cpty` and `none`, each taking a currency. They write
  the CSA's currency and each side's, `exchange`, `one-way`, who posts, and both sides' threshold and MTA as 0;
  curves, frequency, VM caps, independent amounts and rounding stay.
- **The trade's fields beyond the grids**: its identity (requested id, trade type, the source's product type, SIMM
  product class, user-defined) and its agreement (master agreement, netting set), which no command sets, and its
  break (date, mandatory) and model (model, pricer and interpolation ids, domestic currency, IR model and basis,
  equity, credit and inflation models, the method for what is not modelled), set and checked by their forms.
- **They are fields of the row and no column of a grid**: a condition reads them (`trades where ir model=HW2F`),
  `set`, `reset`, `undo`, `vary` and `tables_read` reach them, the Changes tab lists an edit of one with the
  source's value and takes a value typed over it, and an edit is carried when the source is acquired again. A new
  trade's break and model stay with it through a counterparty change. Every field of both tables has a group; B
  and A have none.
- **The CSA grid's Main column is the field `main` now**, so the line's context names it with the row when the
  Focus stands there (`CSA-A · main`), as it names any column that is a field.
- **What Price freezes** (note 53, the owner's of 2026-11-12): each trade of a case with its legs, its independent
  amounts and its fields beyond the grids, and each CSA the case's trades use with its terms and its fields beyond
  the grid, a case's own values in its own input. A leg and an independent amount of a priced trade are priced
  entities, so an unresolved edit on one refuses Price. `docs/10-api-contracts.md` says the shape.

### Verification performed

| What | Result |
|---|---|
| `dotnet test server/Qube.slnx` | 1467 passed |
| Client: `npm run typecheck`, `npm test`, `npm run build` | Clean; 351 passed in 36 files; built |
| `npx playwright test` (Chromium, headless, Linux), on the host rebuilt after the review's changes | 107 passed |
| `python3 tools/validate_handoff.py` | Passed |
| The line with a template typed (what follows `as`, the preview, the refusal of a word both sides share) and the Changes tab | Looked at, at 1440px dark; the preview at 992px light, 768px dark and 390px in both themes, where it wraps inside the line |

The kernel's tests read two templates registered on the stations' readings and one on the board's one row of
settings, only for the tests that ask for them: a template written over one row and over several, its terms by
name and by form, what the line says before and after Enter, the rows that already hold its values, `undo`, the
grid's selection, completion, the description, and each defect of a registration by name. The pricer's read the
source's value of every field beyond the grids, each write of one with its `reset` and `undo`, the refusal of a
word both sides share and of a value its form does not hold, the four templates cell by cell, a value date and a
counterparty changed under an edit, `vary`, the Changes tab, and the accepted input: a trade's legs, independent
amounts and details, a CSA's terms and details, a case other than the Base, a CSA no priced trade is under, and an
uncollateralised counterparty. No browser test was added: the line draws a template with what it had, and the one
browser expectation the step changed is the context's name for the Main column.

The tests of the fields beyond the grids were written from the spec in the same step as their code, and several
passed at their first run. They were checked by breaking the code: the source's read of a detail field taken out,
the Changes tab's read not routed, and the form's check answering nothing failed 23 tests; after the review, six
more breaks (the view writing the source's values, a CSA no trade is under carried, a figure let into a template,
an undeclared field let through, a leg's notional left out, the count of rows already held dropped) failed the six
tests written for them. Each passed again with the code restored.

### What the review changed

The two reviews of this step (standards and spec) found, and this pull request corrects:

- The workspace view wrote the source's values of a CSA's fields beyond the grid inside its agreement, beside rows
  a user may since have changed. They are no part of the view; a test holds the view's CSA to the names it had.
- A trade under no CSA has no entry in the input's `csas`, which was so and was said nowhere; and an id that was no
  CSA of the agreement was passed over in the same way. The sentinel is said in note 53 and `docs/10`, and any
  other id throws.
- Three tests of the input recomputed what the code computes (the CSAs carried, an independent amount's dates,
  the rows a condition names). They hold literals now.
- No test held a template that writes a figure, an answer with an undeclared field, the rows that already hold a
  template's values in what is said, the log's name for a template with its terms, a leg's `notional: null`, that
  the PV is not carried, or a case other than the Base. Each has one.
- A field beyond the grids was declared in one list and its source value in a switch by name, with a side's name
  cut out of the field's; each field is declared with what the source holds in it, and a side's terms once for
  both sides. A template's cells were listed twice (what it writes, and its values); they are declared once.
- The table's answer for a template was `IWorkspaceTable.Template`; it is `TemplateValues`. A template's words
  were matched in three places and the sentence for an amount written twice; each is in one place.
- The CSA grid's Main column being a field now, and so named in the line's context, was not said in note 51.

Each is said in notes 50, 51 and 53 as "Corrected in review" or "Added as built". Left as they are: the reader of
a template's terms keeps the product reader's names (`ProductReader`, `ProductField`), since `new` and `as` share
it and a rename reaches every product; `set` branches on a template inside the one plan that counts and words
every `set`; and the pricer's tests keep one reader of a preview in their rig while each older test class keeps its
own. The "Later, perhaps" section below was added on this branch at the owner's request of 2026-11-12.

### Providers

Unchanged: every figure of the pricer and of Rates is simulated, and says so. The fields beyond the grids are
worked by the synthetic source from what it holds; every name and value is made up.

### Known limits

- **No simulated measure reads the new fields or the legs**, by the owner's word: the measures are connected with
  the XVA analytics. FVA's rule and SA-CCR keep reading `one-way`, `exchange`, the thresholds and the MTAs.
- **Who posts and `one-way` are two things a user can set apart.** A template writes them together; an edit by
  hand can leave `one-way` on with both sides posting. Whether `one-way` should be what the two flags say is the
  owner's to decide.
- The input holds an existing trade's official PV and its legs. Which the real request reads is not decided.
- No dialog shows the new fields until 5c, and no grid but the CSA grid's Main column, which was there: they
  are typed in a command and read by `tables_read`.
- No test drives Price to its refusal for an unresolved edit on a leg. The synthetic source gives a leg the same
  PV on every value date, so no carry leaves a leg's edit in conflict or lost; the rule is held by the kernel's
  test of it and by a test that a priced trade's legs are priced entities ("Next" names the source change).
- A refusal that lists a table's fields is long now (a trade has more than thirty). The groups are told to a
  reader of the description, and the order they are drawn in is 5c's.
- `new` makes no row from a template: no table of the samples makes its rows from one.
- A template's currency does not choose curves: the market has no curve for every currency, and none is guessed.

## The panels' tabs: many panels, and every way of managing them a command (ADR-0006 note 7, 2026-11-12)

The owner asked what happens when many panels are open, what good practice for tabs is, and for the good operations
as commands too. Measured first at 1440, 1280, 992, 768 and 390px with twelve panels (note 7 says what was found);
the practices taken are the ones editors and browsers share, each a verb of the shell.

### What runs

- **The strip** keeps the active tab in sight (opened, chosen elsewhere, moved, or narrowed past), scrolling only
  itself, sideways. A long title is cut with an ellipsis and said whole by the tab's title and the list. A tab is
  one Control Height.
- **The list** at the strip's end says how many panels are open and lists them; choosing one shows it. Under it are
  the active panel's verbs, which is how a phone reaches them. Its tooltip names `show panel`.
- **A tab's menu** opens by a right click, Shift+F10 or the menu key, names its panel, and shows each verb with the
  words the line takes. A key opens it on its first item and gives the keyboard back to the tab, or to the tab shown
  next where it closed its own; the menu key's own contextmenu, which Windows sends as it is let go, leaves that menu
  as it is. An item that cannot act is dimmed with why under it, where a phone reads it too, and stays in the arrows'
  reach; choosing it does nothing and leaves the menu open (`UnavailableMenuItem`, which the account menu's Sign out
  now uses too).
- **Keys and the pointer**: Delete closes the focused tab and the keyboard follows to the tab shown next; the middle
  button closes a tab; a tab dragged onto another takes its place.
- **Where panels open and what closing shows**: a new panel opens beside the active one; closing the active panel
  shows the one shown before it. A Run or a page open already is shown, not opened twice, by `reopen panel` too.
- **Numbering**: panels of one title are `Title`, `Title · 2`, …; a panel keeps its number while open and has it
  back when reopened where it is free. The strip, the list, `show panel`, every command's target and the AI pane's
  card for a panel the AI opened use it; the card says a panel closed since is closed, where its Show did nothing.
- **Pinned panels** stand first, show a pin and no close button, and stay open through the closes of several.
- **Closed panels**: the last twenty are kept with where they stood, whether pinned, and their number. A close of
  several comes back from its left, each where it stood.
- **The verbs**, declared once in `client/src/shell/panelVerbs.ts` and run by the line, the menus and the keys:
  `close panel` (Ctrl/⌘+Shift+X), `close other panels`, `close panels to the right`, `close all panels`, `pin panel`,
  `unpin panel`, `move panel <left|right|first|last>`, `reopen panel`, beside `next panel`, `previous panel` and
  `show panel`. A close of several says how many it closed and kept. They are the client's: not logged, not undone,
  not the AI's (ADR-0016 §3).

### Verification performed

| What | Result |
|---|---|
| Client: `npm run typecheck`, `npm test`, `npm run build`, after the third review's fixes | Clean; 369 passed in 37 files; built |
| `npx playwright test e2e/panels.spec.ts --repeat-each=3` (Chromium, headless, Linux) | 15 passed |
| `npx playwright test` (Chromium, headless, Linux), after the third review's fixes and the rebase on #28 | 112 passed |
| The strip, a tab's menu, the list and the line at 1440px dark and light, 992, 768 and 390px dark and light | Looked at; no sideways scroll of the page at 768 or 390 |
| Elements and the switch back to a page, twelve panels open (before the change, which does not alter what is mounted) | About 700 elements whatever the number; about 150 ms back to an XVA page of 10,000 trades. One run of a Playwright script against the built client in this container's headless Chromium: elements by `querySelectorAll('*')`, the switch from the tab's click to its grid settled. An indication, not a target |

The store's tests read where a panel opens and what closing shows, numbering, the closes of several with pinned
panels, reopening (and its refusal to open a page twice), the closed panels kept, and moves within the pinned or
the others. The verbs' tests read that the line has each verb, `show panel`'s numbered names, what each says when it
cannot act, a refusal from a key as a notice, and a move without its where as a defect. The browser tests read what
only a browser shows: a tab in sight at 768 and 390px, the list's names, an item that cannot act reached by the arrows
with its reason, the menu by right click, Shift+F10 and the menu key and where the keyboard goes as it closes, Delete
and the middle button, a drag, and the AI card's word for a panel closed since.

### Providers

Unchanged; nothing on the server changed.

### Known limits

- A page's own state (its inner tab, a grid's sort, filter and selection) still goes when its panel is hidden. The
  owner chose that each page keep it by panel and show it again (note 7's last bullet): its own pull request, after
  ADR-0016's step 5 changes the pricer's page. (Built on 2026-11-12: ADR-0006 note 10, below.)
- The panels, their order and the closed ones are kept while the page is loaded, as before; a reload starts empty.
  (Since ADR-0018 step 1, below, the browser tab keeps them across a reload.)
- On a Mac laptop Delete is Fn+Delete, and Shift+F10 may need Fn: the menu is reached by a right click (or
  Ctrl+click) and the list, and `close panel` has its chord.
- On a phone a refused notice (from the line or a key) lies over the strip's end, the list's button with it, until it
  is closed: a notice is drawn over the top of the main area (ADR-0006 note 2), and at 390px it takes the width. The
  menus raise none.
- The menu key is checked on Linux only: its contextmenu as Windows sends it is played in the browser test, not
  pressed on Windows.
- A drag is checked with a mouse only; on a touch screen it is not checked, and the list's Move left and Move right
  reach the same move. (Since ADR-0018 step 1, a finger dragging a tab to an area's edge is checked in Chromium with
  touch emulated; it has not been tried on a touch device, which ADR-0018 §3 asks before saying it works.)

### What the review changed

The two reviews (standards and spec) found, and this pull request corrects: `reopen panel` opened a second panel of
a page opened again since it closed; after a menu opened from the keyboard closed its own tab, the keyboard was lost,
and after Delete closed the last panel the next panel opened took it; Move left and Move right stayed enabled at the
strip's ends; keeping the active tab in sight could scroll the page under the strip; other commands' targets named
two panels of one title alike; a tab was 28px beside the list's 30px button; a menu's name hung on an empty mark; a
close of several beside a panel not open answered "Closed 0 panels"; and the empty strip told a phone to press
Ctrl+K. Each is said in note 7 or the design guidelines, with a test where a test can hold it.

A second pair of reviews, of the corrected and rebased branch, found, and this pull request corrects: a close of
several came back in the wrong order (A B E C D after `close other panels` on A and four `reopen panel`), since the
rightmost was reopened first where the strip was too short for its place; an item that cannot act was skipped by the
arrows and said why only to a hovering pointer; the AI pane's card named a numbered panel by its bare title; the line
could list `reopen panel` as available after a reopen that only showed a page; `move panel` with no where was a
refusal, where a required argument missing is the line's defect; a move, a pin or a close of a panel not open did
nothing, where it now throws; the menu key, let go on Windows, could open the browser's own menu over the tab's; the
pin was 14px in a 30px control, and the close button had no tooltip naming `close panel`; and the notice opened a
sentence in lower case.

A third pair, of those fixes alone, found, and this pull request corrects: choosing an item that cannot act raised a
refused notice, which on a phone lay over the list it was chosen from, so the reason is now under the item; the item
was marked by writing to the page from a ref, where the account menu already drew such an item through React, so both
use one component; the list's tick was 14px beside a 16px pin; showing a panel, or setting a Run panel's view, did
nothing for a panel not open, and the AI card's Show did nothing for a panel closed since; the Windows menu key's
contextmenu could reopen the menu as a pointer's, losing the keyboard, when it came before the keyboard reached the
menu; the AI's effects each named their panel, where the pane now names what they open; and the line's check for a
required argument was written three times.

## Generic commands, delivery step 5c: drafts and a row's dialog (ADR-0016 notes 54 to 57, 2026-11-12)

The third of step 5's four pull requests (`docs/specs/commands-5-drafts-rows-under-a-row`, tickets 13 to 17). A row
has a dialog, the same for every table, drawn from what the server says of the row. What is changed in it is a
draft: lines the page holds and the server reads, written by `apply` as one command or discarded.

### What runs

- **A draft** (`LineDraft`: the table, the row, the revision it was opened at, and its lines) is view state of the
  page. A line of it is a whole line of the workbench that names the row (`set trades E00004 notional 5bn`, `new leg
  trade=E00004 eur 250,000`). The server stores none: it reads the lines in order, each on what the lines before it
  left, and writes nothing.
- **The form** (`POST /api/workspaces/{id}/draft`, `DraftForms`): the row's fields with how each is typed, its value
  as a line writes it and whether the draft changed it; each table under the row with its rows as the draft leaves
  them (made, changed, removed), without the field that names the row above, and the products that make a row
  there; the table's templates; each line with its sentence or its refusal; and Original, the row as the source
  alone gives it. A draft the workspace has left is refused (`RevisionMoved`), saying what moved it.
- **The line acts on a draft.** A preview's view carries the draft, and the server reads the line for the draft's
  row: `set notional 5bn`, `new leg eur 250,000`, `remove leg 1` and `set as 1way-cpty ccy=JPY` name no row and
  answer the whole line the draft gains. `apply` is the command `draft.apply`, one batch that one `undo` takes back
  (`Applied 2 lines to E00004.`); `cancel` discards. Every other command, and the verbs of a grid, are refused while
  a draft is open, and the catalog says which are read (`draftCommands`), so the line lists the rest as
  unavailable.
- **Nothing else runs on the page while a dialog is open.** The shell's own commands (the theme, the panels, a
  workflow) are held with the page's, in the palette and at their chords, and say why: `The dialog of E00004 is
  open: apply writes its draft, and cancel discards it.` A command run by its id (the grid's undo key) is refused
  the same way, and so is a line typed for a dialog that has since closed or is another row's. A second press of
  the row's button opens nothing new.
- **`open <row>`** is read by the server and answers the page the one row whose dialog to open; the shell's `open
  <tab>` commands stay, and the line lists them while what is typed could still be one.
- **The dialog** (`client/src/platform/drafts`: `DraftDialog`, `useDraft`, `draftLines`): a modal, the whole screen
  on a phone, with tabs Live and Original. A group is a section; a field is an input by its form (a list for a
  choice, a switch for a flag, text otherwise); a table under the row is a grid whose cells are typed into, with a
  Remove button and a button per product that makes a row; a template is a button that asks for its terms. Every
  change is a line sent through the page's door and read on the server; a refusal is said where it was typed. The
  pending lines are listed and can be taken back one by one. Save is `apply`; Cancel, Escape and the close button
  discard, asking first where lines are pending. The line is typed over the dialog (Ctrl/⌘+K).
- **What the dialog holds to.** A control sends a text once, and what is typed while a line is read is kept. Save
  waits for the lines still being read and writes nothing where one of them was refused; while Save is read the form
  takes nothing. The question before discarding is of one draft: it drops when the draft changes, and it is asked
  in every state of the dialog, the form that failed to load included, which offers "Try again". A line is taken
  back only from the list of the draft as it is. The form says which fields hold numbers (`number`), and a table
  under the row draws those against the right edge, as every grid does; a width dragged there is kept by the table.
- **The keyboard.** It stays in the dialog while it is open. Escape in a grid of the dialog, with nothing of the
  grid's left to dismiss, is the dialog's (ex-grid's `onLeave`). Closed, the dialog gives the keyboard back to the
  grid the page has on screen (`returnKeyboard`), at the Focus it had.
- **A row's button**: the pricer's trade grids and its CSA grid have one button, Edit, beside the row's key. It runs
  `open <key>` through the page's door, as Space on its cell does.
- **The pricer's page** holds the draft (`useDraft`) and says one sentence above the form: the simulated measures
  do not read a trade's legs or the fields outside the grids yet, and Price keeps every value set in the input it
  freezes (note 53).

### Verification performed

| What | Result |
|---|---|
| `dotnet test server/Qube.slnx` | 1509 passed |
| Client: `npm run typecheck`, `npm test`, `npm run build` | Clean; 424 passed in 40 files; built |
| `npx playwright test` (Chromium, headless, Linux), on the host rebuilt after the review's changes | 115 passed |
| `python3 tools/validate_handoff.py` | Passed |
| The dialog of a trade and of a CSA, the line typed over it, and the Edit column | Looked at, at 390, 768, 992 and 1440px, light and dark |
| The Edit button by mouse, by touch (390px, a touch context) and by the keyboard (Space on its cell, Escape back to the grid) | Each opened the dialog |
| After the review: the button pressed twice, Keep editing and Escape, the grid's undo key and a chord of the shell under the dialog, a leg's cell and Remove, a line taken back, the keyboard after the dialog closed | Looked at in the browser, each as the notes say |

The kernel's tests read sensors under a station of the stations' readings (`SensorRig`): the form of a row with no
line, each line read on what the lines before it left, a refused line, the marks of a row made, changed or removed
under the row, Original, the products and the templates the form carries, a draft the workspace has left, each
command that is no line of a draft, `open`, a line read for the draft's row, `apply` as one command that one `undo`
takes back and its refusals, and `cancel` in a draft that no longer reads. The pricer's read a trade's draft that
makes and removes legs, a CSA's draft with a template, a line that names no row, and `apply` of a trade's draft. The
host's read the catalog's `draftCommands`, a draft in a preview's view with `apply` invoked as any command, and the
form through its door. The client's read the lines a change makes (`draftLines`), the door with a draft open
(`served`: a line for a dialog that has closed, a command by its id, a second `open` of the same row, the form read
again after the dialog closed), the dialog with the door stood in (`DraftDialog.test.tsx`: a field by its form, a
refusal where it was typed, a line sent once and the same text from another press, text typed while a line is
read, a product's and a template's terms, the pending lines taken back and where the keyboard goes then, Save
waiting for a line in flight, the form disabled while Save is read, Cancel, Escape and the close button asking
first, the question dropped with its draft, the form the workspace has left, "Try again", Original read-only), the
palette with a dialog open (`CommandPalette.test.tsx`: a shell command held in the list and at its chord), and the
row's button.

Three browser flows (`e2e/drafts.spec.ts`) go through the built client and the host. The first: the Edit button, a
field changed, a value refused, a leg typed on the line over the dialog, Cancel asking, Save, and one `undo`. The
second, by the keyboard: Space on the row's button, a leg's cell typed into and a leg removed in the dialog's grid
(a table under the row is ExGrid, which jsdom does not draw, so these two are held here and not in the client's
tests), the server's refusal of a second Remove, the grid's undo key and a chord of the shell held, Escape asking,
Save, and the keyboard back in the grid behind, also after a dialog the line opened. The third, on a touch screen
at 390px: a tap opens the dialog over the whole screen with Cancel and Save in view, and Cancel asks. The three ran
five times in a row before they went in.

The Edit column moved every later column of the trade grids and of the CSA grid one to the right. The pricer's
browser tests that count columns were moved with it (`e2e/xva.spec.ts`), and one of them reads the CSA grid's
threshold once the Focus has brought it into view, since at the tests' 1280px it is past the first view now.

The test that a line is sent once passed without its guard at first, since the door stood in answered at once;
the rig holds an answer back now, and the test fails with the guard taken out (two lines sent).

### What the review changed

The two reviews of the step (Standards and Spec, each with a fresh context) found defects that every test had
passed over. Each is fixed with a test that fails without the fix, and ADR-0016 notes 54 to 57 say what was
decided ("in review").

- **A line taken back was taken from the draft as it then was**, not the one the list was drawn from, so a press
  on a list a line had just changed could take back another line. `drop` takes the draft the list was drawn from
  and refuses where it is no longer the page's.
- **The question before discarding outlived its draft**, the form that failed to load discarded without asking,
  and it offered nothing to try again with.
- **A control could send its text twice** (Enter, then the blur), text typed while a line was read was written
  over by the answer, and the fields stayed live while Save was read, so a line could be sent into a draft being
  written.
- **The shell's own commands and their chords ran under an open dialog**, and so did a command run by its id: the
  grid's undo key took back a command of the workspace under the draft.
- **`apply` of a draft whose line no longer read** answered the line's refusal as its own; it says `DraftUnread`
  now, and a draft of no line is `NothingToApply`.
- **The keyboard fell to the page** when a line was taken back or the dialog closed, and Escape in a grid of the
  dialog was swallowed by the grid.
- Smaller: a flag read by two readers, one of which took any text for off; a value outside its choices drawn as
  blank; the fields that hold numbers named a second time on the client; the column widths of a table under the
  row not kept; a test of the row's button that compared a constant with itself.

Left as they are, with the reason:

- **Comment lines longer than 120 characters** (59 of the lines the step adds). No document of the repository
  limits a comment's width, and one comment line in eight on `main` is as long; a one-line doc comment is the form
  the code around them uses.
- **The door's two `catch` blocks that say an error alike**: they catch two kinds of error with no base of their
  own.
- **The dialog's own reading of the phone width** beside the shell's: the shell's is in `App.tsx`, which the
  panels' work is changing.
- **The older browser tests that find a cell by its number**; the new ones find it by the column's header.
- **The Edit column's place** is the owner's (ticket 16, `ready-for-human`).

### Providers

Unchanged: every figure of the pricer and of Rates is simulated, and says so. A row's form is read from the
synthetic source and the workspace's edits; every name and value is made up.

### Known limits

- **The Edit column costs a column of the first view.** It stands second, beside the row's key, 64px wide. At
  390px the trade grids' first view is Trade, Edit and Product, and `Ccy` is past it; at 1280px the CSA grid's
  counterparty threshold is. Ticket 16's criterion "costs no needed column at 390px" is not met as written and the
  ticket is left for the owner (`ready-for-human`). The other place is last, which costs nothing and is out of
  view until the grid is scrolled.
- **Only the pricer's trade grids and its CSA grid have the button.** A leg and an independent amount are reached
  in their trade's dialog.
- **A draft is the page's.** A reload discards it without asking, as any view state. While its dialog is open the
  shell's commands are held too, so the theme or the panel is changed after Save or Cancel.
- **The keyboard goes back to the grid, not always to the row.** A dialog opened from the row's button with the
  keyboard in the grid closes with the Focus on that row. One the line opened for another row closes with the Focus
  where it was: moving it to the row asks the server for a place, and is not built. A dialog opened by a press with
  the keyboard on nothing leaves it on nothing.
- **Dates are typed as text** (`yyyy-MM-dd`); the dialog has no date picker.
- **A removed row keeps its Remove button** under the row; pressing it again shows the server's refusal.
- **Cancel, Escape and the close button discard on the page** and ask the server nothing; `cancel` typed is read
  by the server, and both end the same.
- **The verbs of a grid (`find`, `sort`, `filter`, `select`, `copy`, `export`) are refused while a draft is
  open**, where §10 first let them stay; and `new x` does not list the shell's `new xva` (note 56).
- At 390px a table under the row shows its key and one more column in its first view (a leg's key is its trade's
  with a number); the rest are reached by scrolling the grid.
- **No simulated measure reads the legs or the fields beyond the grids** (5b's limit); the dialog says so above
  the form, and that Price keeps every value in the input it freezes.

## Generic commands, delivery step 5d: scenarios as tables, and `load trades` (ADR-0016 notes 58 and 59, 2026-11-12)

The last of step 5's four pull requests (`docs/specs/commands-5-drafts-rows-under-a-row`, tickets 18 to 20). The
pricer's scenarios and their shifts are tables the kernel's verbs read and write, and `load trades <ids>` brings
booked trades of a synthetic store into the New grid. With it step 5 is built.

### What runs

- **`xva.scenarios` and, under it, `xva.shifts`.** A scenario's row says its name, how many shifts it holds, whether
  Price prices it and why it cannot be priced; `set scenarios up included off` leaves it out of Price and `remove
  scenarios up` removes it. A shift's row is the Scenarios grid's row: `set shifts up/3 size 15bp`, `set shifts
  where size > 5bp size 20bp`, `remove shifts up/4`. A write is the grid's own edit of that cell, so a cell has one
  reader. `copy`, `export`, the AI's `tables.read` and a `where` read them as any table.
- **A scenario's key** is its name with what a typed id does not hold written as `_` (`parallel up` is
  `parallel_up`); a shift's is its scenario's key and the number of its row (`up/3`). `shifts up` is a scenario's
  shifts: a scenario's key as written is read before the end of a shift's key.
- **A scenario's dialog** (`open up`): its fields, and its shifts as rows under it. A size typed in a shift's cell
  is a line of the draft; a unit the index does not shift by is refused at the cell by the shift's key.
- **The kernel**, for any table: a number written with its unit against it (`10bp`, `-5%`, `+10bp`); a condition
  on such a field, with its unit said; rows written together, and removed together, where they share what holds
  the value (`SetRows`, `RemoveRows`); and the units of such a field in `tables.describe`.
- **`load trades B00012, B00002`** (`xva.trades.load`). The line offers the store's ids as it is typed, each with
  its product, currency and notional, and says what it would load. The trades join the New grid after the drafts,
  out of Before and in After, under the main CSA. One command, one `undo`.
- **A loaded trade is a new trade.** Its fields, legs and independent amounts are set, reset, varied and opened in
  its dialog as any trade's, where Original shows the store's terms. `remove trades B00012` takes it out with its
  edits, and so does Remove on its row of the Changes tab, which lists it as `loaded`.
- **The workspace holds that it loaded the trade; the terms stay the store's**, read on the workspace's value date.
  A counterparty change keeps a loaded trade and its edits. A value date past its maturity leaves it in no grid,
  and the Changes tab says so.
- **Price freezes it**: the accepted input names the loaded trades (`loadedTrades`) and the version of the store
  their terms were read from (`loadedTradesVersion`), and carries each among the case's trades with everything set
  on it.

### Verification performed

| What | Result |
|---|---|
| `dotnet test server/Qube.slnx` | 1568 passed |
| Client: `npm run typecheck`, `npm test`, `npm run build` | Clean; 424 passed in 40 files; built |
| `npx playwright test` (Chromium, headless, Linux) | 117 passed |
| `python3 tools/validate_handoff.py` | Passed |
| `load trades` typed, its completions and reading, the New grid and the Changes tab after it, a loaded trade's dialog, `set`, `remove trades`, the tab's Remove and `undo` | Looked at in the browser at 1440px, light and dark; the completions and the page also at 390 and 768px |
| A scenario's and a shift's rows read, set and removed by the line; a scenario's dialog, a shift's cell typed and refused there, its Remove by the keyboard, Save | Looked at in the browser at 1440px, light and dark; the dialog also at 390px |

The pricer's tests drive the line as a user does (`PricerRig`). For the scenarios: a scenario's row and its
one-word key, a shift's row under it and a condition on its size, `included` set on one scenario and on several at
once, as the edit `scenario exclude` makes, a size with its unit, without it and in the wrong one, a shift's cell
written as the grid writes it, a reset, the removal of a scenario's last shifts and of several scenarios, and a
scenario's draft with its shifts and `open`. Note 32's verbs are held by the tests they had. For `load trades`: the
load and its `undo`, the reading, each refusal with nothing loaded, a trade past its maturity, the completions, a
loaded trade's fields, legs and amounts edited, its removal and what refuses it, the Changes tab's row and its
reset, a counterparty change, a value date past its maturity, an edit of a booked trade that was not loaded, the
grid's order moving with a load, and the accepted input of a Price with a loaded trade edited in every place. After
the review: `redo` of a load and of a removal, a trade past its maturity left out of `loadedTrades` and removed
from the Changes tab with the edit it lost its place for, the Original of a loaded trade's dialog, the Existing
grid's order left alone by a load, a scenario named as the end of a shift's key, and `all` refused as an FX shift's
risk factor. The
kernel's read a number with its unit against it, a condition on it, and rows written and removed together over the
stations' sensors. Two browser tests hold what only a browser shows: the New grid drawn again after a load, a
removal and an `undo`; and a scenario's dialog with a shift's cell typed, refused and saved.

### Providers

Unchanged: every figure is simulated and says so. The store of booked trades is synthetic (`BookedTrades`, forty
trades made from their ids); no trade, name or figure of it is a real one.

### Known limits

- **The store is a sample's.** It is of no counterparty, holds forty trades, and is searched by an id's first
  characters only. Loading from a file is not built.
- **A loaded trade's own CSA is not carried**: it joins the main CSA, as a draft does.
- **No simulated measure reads a loaded trade's official PV or its legs**; it is priced as a draft is. The input
  Price freezes carries them.
- **A shift's number is its row's own** and does not begin at 1 in each scenario (`up/3`, `up/4`): the line offers
  a scenario's shifts as it is typed.
- **No product makes a shift** (`new shift`), and a scenario is not renamed, nor a shift moved, by a field: the
  `scenario` verbs and the Scenarios grid do those.
- **The Scenarios tab is as it was.** Its grid is no view of the table: `find`, `sort`, `filter` and `select` do
  not act on it, and it has no Edit button; a scenario's dialog is opened by `open <key>`.
- **In a scenario's dialog the grid of shifts is wider than the dialog** (nine fields): its Draft mark and Remove
  are reached by scrolling the grid or by the keyboard. Where a row's buttons stand is ticket 16's question.
- **No test reaches `ConflictUnresolved`**, the refusal to remove a loaded trade while an edit of it waits as a
  conflict: no editable value of the synthetic store, or of the synthetic source, moves with the value date, so
  neither makes a conflict yet ("Next", item 5). The refusal is written and read, not run.
- **A scenario's key with `_2` follows the scenarios' order**: once the earlier scenario of that key is removed or
  renamed, the later one takes the key without the number. Left for the owner with the key's spelling.

### What the review changed

Two reviews with fresh context, of the standards and of the spec (ADR-0016 notes 58 and 59 say each outcome).

- **An id as written is read before the end of one** (kernel, any table under another): `shifts 4` is the shifts
  of a scenario named `4`, not the shift whose key ends with `4`.
- **`all` is refused as an FX shift's risk factor**, at the grid's cell as at the command's. The cell took it and
  wrote nothing, so a `set` over curves and FX rates counted a row it had not written.
- **A loaded trade's dialog has its Original.** The state with no edit dropped the entity that says a trade was
  loaded; a module's rules now say which entities bring in a row a source has (`IEditRules.Brings`).
- **The Existing grid's order does not move with a load**: its token names the acquired version alone.
- **One refusal names every id the store has not**, the unknown and the matured together.
- **The accepted input names the store's version** (`loadedTradesVersion`).
- **From the standards review**: the remembered views of a source with loaded trades are held under a lock; the
  reading of a loaded trade's terms and of the entities that say a trade was loaded are each in one place;
  `RowsVersion` names a grid's rows beside `Version`; `RefuseNotLoaded` says what it refuses.
- **Checked by breaking the code on purpose** (in a second working tree): of seventeen breakages the tests caught
  fourteen. Two tests were made to catch two more (a size's unit left out of a reset, and shifts removed one by
  one); the last is the refusal no test reaches, above. One more breakage, lost edits left behind by the Changes
  tab's removal, is caught by a test the review added.
- **Left as they are**: the key with `_2` (above); the tables of scenarios and shifts overriding most of the
  pricer's `Table`; a reader that cast a workspace's source to the pricer's type would miss its loaded trades, and
  only review holds that every reader goes through `PortfolioSourceVersion.Of`.

## Panels in areas, step 1: split, float and maximize in the window, and the AI's proposal (ADR-0018, 2026-11-12)

The owner asked for panels that float and dock, as Bloomberg's terminal has them, and decided that the AI only
proposes an arrangement, which he applies. ADR-0018 step 1, one pull request (`docs/specs/panels-1-areas`, tickets
01 to 08; 07 in part).

### What runs

- **Areas**, drawn by dockview 8.9.0 (`dockview-react`, exact) from a panels' space of 768px, the window less the AI
  pane: one area at first, holding every panel. Each area's strip is note 7's (its tabs, its list of the area's
  panels with the shown panel's verbs, a tab's menu, pinned first, Delete, the middle button, a drag along it) with the
  shell's tabs inside dockview's. The active panel's area is framed in `signal` from two areas on. Only the page each
  area shows is mounted; a panel moved, floated or docked keeps its page mounted.
- **The strip where there is no room**: below 768px, or where the arrangement needs more than the panels' space (three
  areas side by side need 960px; the AI pane takes its 340 to 400px), note 7's one strip is drawn, and the
  arrangement comes back with the room. Nothing is merged into fewer areas by itself.
- **The verbs** (`client/src/shell/panelVerbs.ts`), for the line, a tab's menu and the area's list: `split panel
  <side>`, `send panel to <title>`, `float panel <where>`, `dock panel`, `size panel <percent>`, `maximize panel`,
  `unmaximize panel`, `gather panels`, each refused by name where it cannot act, and in the strip saying when the
  arrangement comes back. Note 7's verbs act within the panel's area. `next panel` goes through every panel in
  reading order. They reach dockview only through a port (`Arrangement`), which the strip implements by refusing.
- **The pointer**: a tab dragged onto an area's edge splits it there, onto another strip joins that area, and with
  Shift floats; a finger drags as a mouse does. A floating area moves by its title bar and is resized by its edges.
- **The keyboard**: a splitter is a focusable separator the arrows move by 24px, held at an area's 320 by 240px; a
  floating area's title bar is a focusable handle the arrows move by 24px, and Shift with the arrows resizes it.
- **Kept by the browser tab** (`keptPanels.ts`): the panels, what each shows, the active one, those closed and the
  arrangement come back on a reload of that tab; another tab starts with its own. A value of another version, or one
  that does not read whole, is not applied, and the shell says so.
- **The AI proposes** (`layout.propose`, a kernel capability with the new effect `Propose`: offered to the agent, it
  writes nothing). The pane's context `qube.panels` names the open panels, their areas, the room and the pages a
  proposal may open. The card draws the proposal as a plan of boxes with the panels' titles, an Apply button and
  `arrange <n>`; nothing moves before. Applying checks the proposal again and refuses, by name and moving nothing, a
  panel closed or retitled since or a space too small; `arrange back` puts back what the last applied replaced and
  closes the pages it opened. The scripted stand-in reads "put … and … side by side" and "put … beside …".
- **A page reads its panel's width** (`PanelPage`, a CSS container): the trace view drills in an area under 768px and
  is a tree in a wider one. The pricer's own sections, its full-height button and `xva.css` still read the window:
  that is the pull request after this one, now that ADR-0016's step 5d has merged (ADR-0018 §11).

- **Fixed on the way**: a Rates workspace with no default for new lines, or only some, said "New lines default to ·
  NaNmm ·": the server's view leaves a default that is not set out (`WireJson` writes no null), and the page read it
  as a null (since #26). The page now reads the view as it is sent, and `rates.spec.ts` checks a workspace with no
  default and one with a currency alone.

### Verification performed

| What | Result |
|---|---|
| `dotnet test server/Qube.slnx`, after main (step 5d, #33) was merged in | 1594 passed |
| Client: `npm run typecheck`, `npm test`, `npm run build` | typecheck clean; 462 tests in 44 files passed; built |
| `npx playwright test` (Chromium, headless, Linux), against the built client, after main's step 5c was rebased in and after the four reviews' changes | 124 passed; after main's step 5d (#33) was merged in, 126 passed (an earlier run: 122 of 123, `xva.spec.ts:2459`, below) |
| `e2e/areas.spec.ts`, which this pull request adds, with `--repeat-each=3` | 24 passed |
| `e2e/scrollbars.spec.ts`, which this pull request adds (Chromium with its scrollbars shown), with `--repeat-each=3` | 3 passed |
| ADR-0018 §6's measurement: three XVA pages of 10,000 trades at 1440px, in three areas and in one | Passes: the pages shown and one tab per panel; a tab's switch 92ms in one area, 89ms in three (below) |
| The areas, a tab's menu, a floating area, the AI's card and the strip at 1440 dark and light and 390px | Looked at, in the pull request's screens |

The verbs' tests (`areaVerbs.test.ts`) read each verb and each refusal of the spec's table over a fake port, note 7's
verbs within an area, `next panel` across areas and `reopen panel` back into its area. The arrangement's tests read
what fits, the reading order, a kept arrangement drawn again with the panels opened and closed since, and `size
panel`'s row. The proposal's tests read a proposal made into a layout and every refusal at apply, and the kept value's
tests a reload, another user, and a value of another version. The server's tests take a well-formed proposal, refuse
each malformed one by path, and run the stand-in's two requests. The browser tests read what only a browser shows:
where dockview draws each verb, a drag with the mouse, with Shift and with a finger, the keys of a splitter and a title
bar, the panels' space at 390, 768, 992 and 1440px with the AI pane shut and open in both schemes, a reload and a
second tab, the AI's proposal applied and put back, and the trace by its area's width.

### Measurements (ADR-0018 §6)

Four panels open (the Pricing DB and three XVA workspaces of 10,000 synthetic trades each) at 1440 by 900px, dark,
the AI pane shut. A tab's switch is from the click to the grid of the page it shows saying it has settled, in the
page's own clock, ten times over two tabs of one area.

| | Elements in the document | Of the pages shown | Outside them | Tabs in the areas' strips | A tab's switch, median (range) |
|---|---|---|---|---|---|
| One area, showing one XVA page | 491 | 280 | 211 (the area's strip 45) | 4 | 92ms (84 to 107) |
| Three areas, each showing an XVA page | 1367 | 317, 317 and 389 | 344 (the strips 31, 24 and 24) | 4 | 89ms (78 to 124) |

§6 passes: the elements are the pages shown and one tab per open panel, in both; a panel not shown mounts nothing,
and what the areas add (about 65 elements an area) grows with the areas in view, not with the panels open or the
trades. A switch takes as long in three areas as in one. No console message or exception in either.

### Providers

The AI's proposals in the tests and the screens come from the scripted stand-in, not a model: `layout.propose` is
offered to the agent, and no model has been asked to arrange panels. The XVA pages measured are the synthetic pricer's.

### Known limits

- *(Its width half is done by the next pull request, "the pricer by its area" below; a short area's trade grids still
  scroll under the result dock, as that section's Known limits say.)* The pricer's page reads the window, not its area,
  until its own pull request, the next one now that ADR-0016's step
  5d has merged (ticket 07): an XVA page in a narrow area is laid out as the window's width says, and in a short one
  (split down at 1440 by 900px) its trade grids scroll under the result dock and their headers draw over it, as they
  do in a window that short.
- A change between the strip and the areas, at 768px or as the AI pane opens beside many areas, draws the pages anew:
  a page's own state (an open trace, an input tab, a grid's sort) starts again, as it does when its tab is hidden,
  until pages keep their state by panel (note 7's pull request).
- Other browser windows (step 2) and parts of a page with links (step 3) are not built.
- The finger drag is checked in Chromium with touch emulated, not on a phone or a tablet.
- dockview 8.9.0 is pinned exactly; its own context menu, overflow list and theme are not used.
- One of seven full browser runs on this branch failed `xva.spec.ts:2459` (ADR-0016 notes 43 and 44, from main): after
  `select where ccy=USD` and Enter, the line was still open five seconds later. The test passed in the other six
  runs and six times of six alone. This branch changes when the line builds its list of commands again (as the
  arrangement changes), not what Enter does. Not explained yet; the host had by then priced three workspaces of 10,000
  trades for the measurement.

### What the review changed

Two-axis review (standards and spec) of the whole branch:

- **`dock panel` after any float.** Where a floating panel came from was noted only by `float panel`, so a panel
  floated by Shift-drag docked into the last area. It is now read from dockview's own move, kept by the browser tab
  with the arrangement, and the browser test docks a Shift-dragged panel and one floated before a reload.
- **The menus over a floating area.** dockview's floating areas stood at 999 (a CSS variable that names itself), above
  the shell's menus: a submenu opened from a floating area's tab was drawn under its grid's header. They stand at 99,
  and the browser test now floats, sends to, docks and places an area through the menus.
- **Float ▸ in a floating panel's menu** places its area, as the ADR's table says, with Dock beside it.
- **A Shift-press on a strip's empty end**, which floats the whole area in dockview, is refused by name where that area
  is the only one docked.
- **What the tab keeps is read whole**: every part of dockview's layout, not only its version and panels.
- **`reopen panel` says where the panel went**, and `next panel` with no active panel goes to the first.
- **A splitter says its place** (`aria-valuenow`); the page inside an area is no second tab panel.
- **A proposal's place** names exactly one of a panel, a Run or a page, and a label never stands in for one it could
  not read; `arrange back` says so where none of the earlier arrangement's panels is still open.
- **The records**: ADR-0018's note and ADR-0010's say the pricer reads its area only from a pull request of its own
  after ADR-0016's step 5d; the spec's "As built" says what the build settled; the touch claim says Chromium's
  emulation.

A second review, of those fixes alone:

- **`arrange back` puts back where each floating panel came from**, with the arrangement it puts back; `arrange`
  clears it, since a proposal's floating area came from no area (`proposals.test.ts`).
- **A kept floating area is read with where it is placed from**: exactly one of left and right, and of top and bottom.
- **The Shift-press on a strip's empty end** finds the pressed area through dockview, not by reading the DOM's active
  tab, so nothing unread lets it through.
- **A floating title bar's grip** takes the same colour under the keyboard's focus as under the pointer (design
  guidelines §8).
- **ADR-0018's note** records the whole `dock panel` row and two readings of §4 (no frame with one area; `reopen
  panel` says where only where there are several areas).

Found by the full browser run that followed: **a page read its width inside the shell's gutter**, 16px less than its
panel, so in one area of a 768px window the trace drilled in where it had been a tree. `simm.spec.ts` had passed by
reading the trace before the browser reported the width; it now waits for the page to be told its width
(`data-panel-width`). The panel's width is now its whole width, the gutter included (`PanelPage`), as the window's was.

A third review, of that fix, found with scrollbars that take room (Windows, Linux; checked in Chromium with its
scrollbars shown): an area's scrollbar took 15px from the width its page read, and a window from 768 to 782px stayed
areas or strip by whichever it was drawn in first, since the strip's document scrollbar narrowed the panels' space.
An area's page now scrolls inside the panel's page, and the panels' space counts the document's scrollbar, read with
the panels' width in one callback (read apart, the two added up for a render to a width neither layout had, and at
760px the strip and the areas were drawn by turns). `e2e/scrollbars.spec.ts` launches Chromium with its scrollbars
shown and crosses 768px both ways: each width is the same mode whichever it came from, and the page reads its panel's
width. The same review: `reopen panel`'s notices are as ADR-0018's note now says, the Shift-press refusal says "the
only area docked", and the kept floating area's check is named and typed as the others. A fourth review, of those:
the panels' space is read only in the observer's callback, after the browser's layout (a window's change of width
changes the document's box); the test waits for each scrollbar before the next width, and fails without the fix;
`float panel`'s refusal says "the only panel docked", true with a floating area open; and `reopen panel` in one area
is checked to say nothing.

## Generic commands, delivery step 6: saved sequences of edits (ADR-0016 §8, notes 60 to 64, 2026-11-12)

The last step of ADR-0016's delivery order, one pull request (`docs/specs/commands-6-saved-sequences`, tickets 01
to 07). A user keeps an ordered list of command lines under a name and runs it on any workspace of the application;
each line runs as its own command. With it the generic commands are built.

### What runs

- **`sequence save <name> [desk|all] <line> then <line> …`** (`sequence.save`), on every application. The lines are
  read apart at the word `then`; the reading says each with its number, whom the sequence is for (the user alone,
  their desk, every user) and whether it replaces the user's own of that name. It is a saved item (C32) under
  `sequence:<app>`, kept across a restart, and changes no workspace.
- **What a line may be** is one rule, read at the save and again as each line is reached: a command of the
  application that edits the workspace, or as the last line one that starts work on it (`price`). A verb of a page,
  `selected`, `shown`, `undo`, `redo`, `apply` and a `sequence` verb are refused with the line's number. A line
  refused only as the workspace stands (a row not there yet) is kept, and the reading says which and why.
- **`sequence run <name>`** (`sequence.run`). The line offers the sequences the user can see, each with its lines
  and its owner; its reading says every line and what the first would do on the workspace as it stands. Each line
  is read by the server's reader at the revision the line before it left and invoked through the command gateway,
  so each is a command of its own in the log and `undo` takes them back one at a time, the last first.
- **A run stops at the first refusal.** The first line's refuses the command, and nothing ran. A later one ends the
  run: the lines before it stay written, and the notice says the line, why, the revisions the run began and ended
  at, and what `undo` reaches. That notice stays until it is closed. So does a run's whose line said that what it
  did waits for the user, with that line's sentence.
- **What was read is what runs.** A sequence saved again since its reading is refused (`SequenceMoved`), a line
  whose first words name another command than when it was saved stops the run (`LineMoved`), and a workspace
  another hand moved between two lines stops it (`RevisionMoved`).
- **Origin.** A run's lines are in the log with origin `workflow`, and `undo` says "a saved sequence for you" of
  one; lines of a sequence the AI ran keep origin `ai`.
- **The line as it is typed** offers `desk` and `all` after the name, the commands a sequence may hold where a line
  begins, what that line's own command offers within it, and `then` after a line that reads.
- **The page** is told what each line's command answered, in order, as if it had run it: Rates opens the Run a
  sequence's last line `price` made. Nothing else of the client knows a sequence.
- **The AI** has both commands among its tools, as every command: it saves a sequence from a list of lines and
  runs one by name, and the answer holds each line that ran with its command's own answer.

### Verification performed

| What | Result |
|---|---|
| `dotnet test server/Qube.slnx` | 1642 passed |
| Client: `npm run typecheck`, `npm test`, `npm run build` | Clean; 465 passed in 44 files; built |
| `npx playwright test` (Chromium, headless, Linux) | 127 passed |
| `python3 tools/validate_handoff.py` | Passed |
| A save typed with its reading, the offer of a saved sequence, the run's reading, the run with its notice and the Run it priced, `undo` after it, a save that keeps a refused line, a run that stops at its second line, a save that is refused | Looked at in the browser at 1440px, light and dark; the two readings also at 390px in both themes, 768px and 992px |

The kernel's tests drive the two commands over the stations' sensors (`SequenceRig`: an in-memory store of saved
items, and one command registered for the tests that starts work and edits nothing): what a line's first words
name, the save and each refusal of it with nothing kept, a line kept though refused where it stands, a run with
each line at the revision the line before left, `undo` after it, a first line refused, a later line refused and
what the answer then says, a workspace moved between two lines, a sequence saved again since it was read, a line
whose words name another command now, a kept line the rule no longer allows, a run asked again with its key, a
shared sequence run by a colleague and a private one not seen, the AI's origin kept, a command that answers no
revision thrown as a defect, both lines' readings and completions, and `undo`'s reading. The pricer's
(`PricerSequenceTests`): a sequence saved on one counterparty and run on another, ending with `price`; the Run has
origin `workflow`, stands at the revision the second line left, and its accepted input holds After exactly on the
USD existing trades, as the lines set it. Through the host (`SequenceApiTests`): a Rates sequence that makes a line
and prices it, a pricer's sequence not seen on a Rates workspace, a sequence kept across a restart, and the AI's
door. The client's door has three tests of what it tells its page, and one browser test holds what only a browser
shows: on Rates a sequence is saved from the line and offered by name, its run makes a line and opens the Run, and
`undo` takes the run's line back.

### Providers

Unchanged: every figure is simulated and says so. Saved sequences are kept by the kernel's store of saved items,
in the host's SQLite database.

### Known limits

- **For the owner to judge** (ADR-0016 notes 61 to 63): a command that starts work is a sequence's last line or
  none, so `price` between two edits is refused; a sequence the AI runs keeps origin `ai` for its lines; a run that
  stopped is drawn as a done notice that stays; the words `sequence` and `then`; both commands are among the AI's
  tools.
- **A saved sequence is not taken away.** Its owner saves the name again, for themselves alone to withdraw it from
  a desk. No command lists the sequences: `sequence run` offers them as it is typed.
- **No values are given when it runs**, none is made from the log or recorded, and there is no dialog to write one
  in ("Later, perhaps").
- **A run is not all or nothing.** The lines before a refused one stay written, and `undo` takes back those it
  reaches: a line whose command no undo takes back (a `refresh` that took another source version) keeps it from
  that line and the ones before, and the stopped run's notice says so.
- **The AI has no list of the saved sequences.** `sequence run` with no name names three and counts the rest.
- **The run's reading reads the first line alone**: it claims nothing of what a later line would change (§9).
- **A typed line cannot hold the word `then` standing alone**, and a typed sequence's first line cannot begin with
  `desk` or `all`; the capability takes its lines as a list.
- **A line with a mistyped field or row is kept**, with the save's reading saying it is refused where it stands:
  the server cannot tell it from a line that names what an earlier line makes.
- **The save's reading reads every line at each keystroke**, fifty at most. Its cost is not measured.
- **A sequence's lines are not run under a lock of the workspace**: a hand that moves it between two lines stops
  the run with the lines before written, as any refusal does.
- **A run asked again with its key after it broke** (a command's defect in the middle) is refused at its first
  line (`IdempotencyConflict`) and writes nothing; the lines that ran stay, for `undo`.

### What the review changed

Two reviews with fresh context, of the standards and of the spec (ADR-0016 notes 61 to 64 say each outcome).

- **A line a sequence may not hold is not read for what it offers.** `sequence save a sequence save a …` read
  itself once for each nesting, and a line long enough would have exhausted the server's stack.
- **A stopped run says what `undo` reaches.** It said undo takes every line back, which is false after a line
  whose command no undo takes back; it now names that line and the lines after it that undo reaches.
- **A line's `stays` is carried by the run**: the notice stays and holds that line's sentence with its number.
- **A run whose answer breaks the door's contract is done all the same**: the page chains on the revision it
  left, reads the workspace again, and says the defect beside what the run said.
- **Each line refused where the workspace stands is said with its reason**, not the first alone.
- **The rule of note 61 is one function** (`Held`), asked by the save, its reading, the offer of `then` and a run;
  what a short form stands for is named as the reader reads it; the reader's refusal of what reads a screen has
  one name (`TableRefused.NoView`); a line of a request that is no text is refused by name.
- **Documents**: the contracts document listed `InvalidShare` and `NoLines` as the command's refusals, which the
  request's contract refuses first; the spec said the AI reads the line's completions, which it does not.
- **Checked by breaking the code on purpose** (in a second working tree): of forty-seven breakages the tests
  caught forty-three. Tests were added that catch three more (a line a sequence may not hold read for what it
  offers, a short form not held to the rule, and the earlier of two lines no undo takes back taken for the later).
  The last is caught by none: what a short form's line names, read among the short forms too, differs only where
  such a line begins with a short form's words, and none does.
- **Left as they are**: whom a saved item is for is put into words in the kernel's sequences and in the pricer's
  scenario sets, each in its own sentences, and belongs on the store of saved items once a third kind is saved;
  `sequence.run` answers through its own few lines beside `TableContracts.Answered`, which is not asynchronous;
  a refusal's code and sentence travel as a pair; a spec's `Status: built` is not one of the tickets' roles, as
  the specs before it.

## A panel's name: `rename panel` (ADR-0006 note 9, 2026-11-12)

The owner asked whether a tab's name can be changed, then for renaming. A panel takes a name of the user's, typed on
the line.

### What runs

- **`rename panel <name>`** names the active panel. The name is shown in place of its page's title wherever the panel
  is named: the strip, the list, the tab's menu, `show panel`, every command's target, and the AI pane's card, whose
  Show now names an open panel as it is called now. `rename panel` with the page's title, in any case, which the line
  completes, gives that back. A name ending in `·` and a number, or longer than 60 characters, is refused on the line
  before it runs. One reading of a name (`readName`) serves the store, the line and the kept panels.
- **Numbering** goes by what a panel is called, regardless of case: a panel renamed takes the lowest number free among
  its new name, and the others keep theirs.
- **Rename** in a tab's menu and in the list, **F2** on a focused tab and a **double click** on a tab each show the
  panel and open the line with `rename panel` and its name, selected; typing replaces it. The keyboard goes back to
  the tab as the line closes, from a menu a key opened too (`lineIsOpen` keeps the menu from giving it to the tab as
  the line opens).
- **Kept**: a closed panel keeps its name and is reopened with it; the browser tab keeps the names across a reload,
  and refuses a kept name this version could not have given (spaced, the title, empty, numbered or too long).
- **A fix from step 1**: a tab's menu opened by Shift+F10 on the first tab of an area at the window's left edge was
  drawn hidden (Mantine took its mark, 0px wide on the edge, for a detached target). The menu no longer makes that
  check; the browser test below opens it there.

### Verification performed

| What | Result |
|---|---|
| Client: `npm run typecheck`, `npx vitest run`, `npm run build`, after the rebase on #35 | Clean; 470 passed in 44 files; built |
| `npx playwright test e2e/panels.spec.ts --grep "note 9" --repeat-each=3` (Chromium, headless, Linux), after the second review's fixes | 6 passed |
| `npx playwright test` (Chromium, headless, Linux), after the rebase on #35 | 129 passed |
| The same, once before, after the first review's fixes | 127 passed, 1 failed: `settings.spec.ts:132` (the account menu's End did not reach Sign out), which this change does not touch; it passed 5 of 5 run again alone, and in the runs before and after |

The store's tests read a name given, numbered among its name regardless of case, the title given back in any case, a
refused name changing nothing, and a reopened panel with its name and number. The verbs' test reads `rename panel` on
the line: its target, its completion, the rest of the line as the name, and a refused name. The kept panels' test
reads names across a reload, open and closed, and the refusal of a name the store could not give. The proposals' test
reads an arrangement the AI proposed refused after a rename (ADR-0018 §8). The browser test reads, at 1280px in an
area and at 390px in the phone's strip, the menu's Rename on a tab not shown, F2 and Escape, Rename reached by the
arrows in the menu a key opened on the window's first tab, the list's Rename, and a double click, each with the name
selected on the line and the keyboard back on the tab. The AI card's Show by the name now is not read by a test.

### Providers

Unchanged; nothing on the server changed.

### Known limits

- The name is the browser tab's, as its panels are (ADR-0018 §7): another tab or another browser does not see it.
- An arrangement the AI proposed before a rename is refused by its Apply, saying what the panel is called now
  (ADR-0018 §8, as designed).
- Not checked on a real phone: the list's Rename was clicked in a narrow desktop Chromium, not tapped. The line takes
  the keyboard on the next turn after a tap, as it does from the header's button, so a phone's own keyboard may not
  rise until the field is tapped.
- `settings.spec.ts:132` failed once in a full run and passed when run again: a red that passes on a re-run, still open.

## Panels in areas, step 1's rest: the pricer by its area (ADR-0018 ticket 07, 2026-11-12)

The pull request after step 1's (`docs/specs/panels-1-areas`, ticket 07's rest), once ADR-0016's step 5d had merged
on the pricer's files (ADR-0018 §11).

### What runs

- **The pricer reads its panel's width.** Below 768px of its area it is the three tabs under the context bar, as on a
  phone, and its dock has no Full height; from 768px it is the one screen. The profile's chart and table, and two
  measures' deltas, stand side by side from 1200px of its area. `xva.css` reads the panel by container queries; the
  section switch and Full height are shown by them, written in the range syntax (`width < 768px`, `width >= 768px`),
  so a switch and the layout never disagree at a fractional width: `max-width: 767.98px` and `min-width: 768px` both
  missed an area 767.99px wide, a third of a row (argued from the queries; no test reads a width that near 768px).
  The shell's toolbar rules, which read the window, now also read the panel: inside a panel the pricer's parts use
  them (its context bar, the Credit and Settings tabs' fields, the profile's choice) and the trace's toolbar, and the
  pricer's counterparty takes its own row in a narrow area. The header's context bar and a dialog's fields use them
  too, by the window, as before.
- **The pricer reads its panel's height.** The Existing grid takes what the page's other parts leave of the height the
  shell gives a page (`--qube-panel-room`): in an area the area's height less the gutter, in the strip the window's
  less the header, the strip and the gutter. The page's own number (`TAKEN`) counts its parts alone, 187px where the
  context bar is one row, read once at 1440px, so in one area of 1440 by 900 the page fits and does not scroll (it
  overflowed by 18px). Maximized, the area's room is the panels' whole space (the grid's height back, tested). Floated
  to the right half, under 768px, the pricer is the three tabs and the Trades tab's grid stands above its 20rem floor
  (tested at a width between 600 and 768px); that floating area is the window's height, so the test does not tell its room from the
  window's.
- **The shell's own numbers say themselves once**: the header's offset is Mantine's, read without a default (Mantine
  sets it whenever the shell has a header); the strip page's top gutter is a class the room's formula names; and an
  area's strip and a floating area's title bar take their height from the tab's.
- **Container queries are in pixels.** A container query's `em` is the container's font size, 13px in this theme:
  step 1's trace put its formula beside the tree from 806px, not 992px. Fixed with the pricer's queries, and the
  trace's browser test reads 950px.
- **What else read the window inside a page now reads the panel**: a trace's roots (spot IM's cards, side by side from
  the panel's 768px, in the pricer's dock) and a declarative view's columns (the Run panel's and a view panel's, the
  wide count from the panel's 992px). Both were Mantine's `SimpleGrid`, whose columns read the window.
- A dialog (a row's draft) still reads the window: it is drawn over the window, not in the area.

### Verification performed

| What | Result |
|---|---|
| `dotnet test server/Qube.slnx` | 1594 passed (no server change) |
| Client: `npm run typecheck`, `npm test`, `npm run build` | Clean; 462 passed in 44 files (465 with main's step 6 merged in); built |
| `npx playwright test` (Chromium, headless, Linux), against the built client | 127 passed after the first review's fixes; 129 after the second's (two new tests); 130 with main's step 6 merged in, after the fourth's |
| `areas.spec.ts`'s two tests of a page by its area, `--repeat-each=3` | 6 passed; the pricer's again after the review's fixes, 3 passed |
| `scrollbars.spec.ts` with a 600px window, `--repeat-each=3` | 3 passed |
| The trace's test with the trace's query in `em` again | Fails at 950px (two columns where one is right) |
| The pricer at 390px (the strip) and at 768, 992 and 1440px (one area), 900px high, dark and light, in Chromium | Looked at: the three tabs at 390px, the one screen from 768px, nothing sideways; the pull request's screens |
| The SIMM tab's two cards and a Rates Run's two grids, side by side in a wide panel and one above the other in a 720px area of a 1440px window (`simm.spec.ts`, `rates.spec.ts`), `--repeat-each=3` | 6 passed |
| `areas.spec.ts` whole, three times in a row, after the second review's fixes | 27 passed; its mouse-drag test (step 1's) failed once before that, its drop on an area's edge not taken, and passed 5 of 5 alone |
| After the third review's fixes: the pricer's, the trace's, the panels' space, the SIMM cards' and the Rates Run's tests, `--repeat-each=3`; `areas.spec.ts` whole | 15 passed; 9 passed |

`areas.spec.ts` holds the pricer in one area at 1440 by 900 (it fits, the grid above the dock), in the top half (the
grid at its six rows, the area scrolling and not the document), maximized (the grid's height back), in the strip at
1440 by 480 (too short for the two areas: no switch, the grid at its six rows, the document scrolling), gathered again
(the grid's height back), floated to the right half (the three tabs, the Trades tab's grid above its 20rem floor) and
docked back, beside the Pricing DB at 605px (the three tabs, no Full height, nothing sideways) and at
1080px (the one screen, in the same page), and in the strip at 1440px with the AI pane open beside four areas in a row
(no switch, the grid above the dock and the document not scrolling: the strip's room). `scrollbars.spec.ts` needs a
pricer taller than its room, so that its page scrolls beside a scrollbar: at 992 by 900 it overflowed its area by the
18px `TAKEN` left out, and now fits, so the test's window is 600px high.

### Providers

Unchanged: every figure is simulated and says so.

### Known limits

- In an area shorter than the page's parts need (split down at 1440 by 900), the grid keeps its six rows and the page
  scrolls inside its area; its trade grids scroll under the result dock and their headers draw over it (ExGrid's
  header stacks at 4, the dock at 3), as in a window that short.
- Where the context bar wraps (an area under about 1,000 to 1,100px, by the measures chosen and the Price button's
  words), the page scrolls by the rows it adds: `TAKEN` is the one-row figure.

## A page keeps what it shows by its panel (ADR-0006 note 10, 2026-11-12)

The owner's choice of 2026-11-11: each page keeps its screen state by panel and shows it again when its panel comes
back, without keeping hidden pages mounted, and without the scroll. Built after ADR-0016's steps 5 and 6.

### What runs

- **What is kept, by the panel, while it is open**: the pricer's layout (its part on a phone, the inputs open or folded
  and their height, the dock open or folded, its height and whether it is widened), its input tab and dock tab, the
  case and the market the dock shows, the profile chosen and its series hidden; a trace's term selected, terms open,
  pages of children read and card shown, by the trace it is of; the Rates page's levels opened and the line whose
  cashflows are open; the Pricing DB's domain; and every grid's sort, conditions from the line and Selection. A part of
  a page drawn again while its panel is shown (an input tab or a domain chosen again) takes back what it showed too.
- **A grid's Selection comes back only onto the rows it was read among**: kept with the order its positions were read
  in, as the page names it (a paged grid by its reader's order token, once every row in hand came in one answer under
  the conditions it holds; a grid whose page holds every row by its rows' keys, once the server said which rows its
  conditions leave; a Run's datasets and trace by the sort), and with the grid's columns. Placed once the rows are in
  hand where both are the same, nowhere otherwise, and never over a Selection the user made meanwhile.
- **How long**: in memory, for the page load. A panel closed takes it with it, so `reopen panel` and a reload bring
  back pages opened anew.
- **How**: `usePanelState` (`client/src/platform/panelState.ts`) is React's `useState` kept by the panel under a key
  declared once (`panelKey`); `useKeptGrid` (`client/src/platform/grid/keptGrid.ts`) keeps a grid's sort, conditions
  and Selection. `PanelScreens` holds them, and the workspace's store tells it which panels are open.

### Verification performed

| What | Result |
|---|---|
| Client: `npm run typecheck`, `npx vitest run`, `npm run build`, after the review's fixes | Clean; 484 passed in 47 files; built |
| `npx playwright test e2e/pageState.spec.ts` and the SIMM test #37 added, `--repeat-each=3` (Chromium, headless, Linux), after the review's fixes | 18 passed |
| `npx playwright test` (Chromium, headless, Linux), with #37 merged in, before the review's fixes | 135 passed, 1 failed: #37's SIMM test reopened the trace by its button after the page was drawn anew, where the trace is now kept; corrected to read the kept trace |
| The same, after the review's fixes | 137 passed |

The unit tests read a value kept by panel, key and instance; nothing kept for a closed panel, whatever its page writes
as it goes; a component given another instance starting from what that one kept; a Selection kept as one range with
its order and columns, and none for several ranges or another sequence; placed again on the same rows, nowhere where
the rows moved or the columns differ; a paged grid's order only where its rows came in one answer under the conditions
held; a held grid's order only once the server answered for its conditions; a Selection given back and not yet decided
kept while the page is hidden again before its rows came, under React's Strict Mode; and none placed over the user's.
The browser tests read, after a panel hidden and shown again: the pricer's CSA tab and its trade grid's sort,
conditions and two rows selected, and after a close and `reopen panel` none of them; the pricer's inputs folded, its
dock's height, and on a phone its part; a Rates leg open, its cashflows open and the Focus back; the Pricing DB's
domain and Focus, through the window crossing 768px too (the strip and back), and no Selection where a Run was priced
while it was hidden; and a trace's term, terms open and Focus.

### Providers

Unchanged; nothing on the server changed.

### Known limits

- A Selection of several ranges is not kept, nor a grid's scroll: ExGrid places one range from outside a grid, and
  hands a view it read back only to the grid that read it (its ADR-0331). The Focus placed is revealed.
- Nothing is kept across a reload, or by a panel closed and reopened (as decided).
- A Rates grid filtered from the line keeps its Selection only where the server's answer for its conditions comes
  before the rows are decided on; until it comes the Selection waits, and a page hidden again keeps it.
- A trace priced again in a panel on screen starts as a trace opened anew, on the term last asked for, as before; no
  browser test tells that from a trace that kept the last one's term.
- Not checked on a real phone or tablet; the phone width is Chromium's.

### What the review changed

The two reviews (standards and spec) found, and this pull request corrects: a trace's state was kept by its tab alone,
so a Run priced since started on the last trace's terms; a Rates or CSA grid filtered from the line decided its
Selection on every row before the server's answer, and dropped it; a paged grid's order was read from its first row
alone, where the line's own view asks that every row came in one answer under the conditions held, and both now read
one function; a dataset grid kept its Selection by the view alone, so another dataset's rows could take it; a closed
panel's page wrote its state back as it went, after the close; a Selection the user made before the kept one was
decided could be overwritten by it; a component given another instance kept the last one's value; a key declared again
by Vite's hot reload threw; the request a trace answered once was written twice; and the tests' names did not carry
the ADR, and a browser read of the Focus could mix two grids as a page was drawn anew. ADR-0018 §5 said the pages are
drawn anew between the strip and the areas until this pull request; it now says they take back their state.

## Panels in areas, step 2: a panel in another window of the browser (ADR-0018 §9, 2026-11-13)

The owner said to start step 2 on 2026-11-13 ("前者は始めていいよ"). One pull request
(`docs/specs/panels-2-windows`, tickets 01 to 06); what it settles beyond §9 is DEFAULT, in ADR-0018 §9's note of
2026-11-13.

### What runs

- **`pop out panel`** (the line, a tab's menu, the area's list) moves the panel into an area of its own in a new
  browser window, opened by dockview 8.9.0 (`addPopoutGroup`) on `client/public/popout.html` over where its area
  stood and at its size. An area's only panel takes the whole area out, so that its place is kept. Refused in the
  strip, for a panel alone in a window already, and for the only panel docked in the main window.
- **The window is the main one's** (`client/src/shell/WindowShell.tsx`): it follows the main `<html>`'s attributes
  (the colour scheme among them) and the stylesheets the main head gains after it opened; it is titled
  `<panel> · Qube`, following the tab chosen there; a menu, a select, a tooltip or a popover opened from its page
  opens in it (Mantine's `Portal` is pointed at the window's own node); a press outside its popovers closes them, and
  Escape closes a dialog there. A panel's menus keep inside the window and scroll there, in the main window too (a
  short window cut off the menu of every verb, found while the screens were taken).
- **The window in use** (`client/src/shell/windows.ts`): the window last pressed in, focused, or typed a shell key in,
  or the one just popped out into, changed only while no dialog is open (in the main window a press or the window's own
  focus, since the shell moves the keyboard there itself). The command line, the Run dialog, the
  applications' dialogs and the settings open there; the shell's notices are said there; the active panel is the one
  it shows. Ctrl+K, `/` and the shell's chords are attached to every window's document. `ai` from another window asks
  with its panel, and says that the answer is in the AI pane of the main window.
- **A page drawn anew in its window**: an area's page is keyed by its window, so a move into a window or back mounts
  it there, and it takes back what its panel kept (ADR-0006 note 10: a Rates leg open, a grid's Focus). ExGrid 0.2.0
  works there (presses, arrows, menus), but reads the main window's Device Pixel (below).
- **Back where it was**: `dock panel` in a window puts its panel back into the area it came from, or where that area
  stood, or, where it has gone, a new area on the right where there is room and else the last area; `send panel to
  <title>` moves a panel either way, and `close panel` on a window's last panel closes the window. A window the user
  closes puts its panels back (into the area they came from, floating where they floated, or an area of their own) and
  the shell says which. A window the browser blocks is said as a refusal, and the panel stays. A reload brings the windows'
  panels into the main window (`withWindowsDocked`) and says where each went; so does the main window showing the one
  strip, which closes the other windows. The verbs that act within the main window's areas (`split`, `float`, `size`,
  `maximize`) are refused for a panel in a window, saying to dock it first (`size`: the window sizes it); `gather
  panels` is refused in a window.

### Verification performed

| What | Result |
|---|---|
| Client: `npm run typecheck`, `npx vitest run`, `npm run build`, after the ninth round's fixes | Clean; 494 passed in 48 files; built |
| `npx playwright test e2e/windows.spec.ts`, which this pull request adds, `--repeat-each=3` (Chromium, headless, Linux), after the second review's fixes, and again after each later round that changed the client's product code (to the fifth) | 33 passed: eleven tests three times, the Device Pixel test among them as failing, which Playwright counts as passing (below) |
| The Device Pixel test (a window at 2x beside a main window at 1x) | Fails on ExGrid 0.2.0, as it should: the grid in the window is told 1px where its window's device pixel is 0.5px. Marked `test.fail` while the workbench takes 0.2.0, so it must pass once it takes `0.3.0-rc.1` (ex-grid #565, fixed by #566; ADR-0090 there) |
| `npx playwright test` (Chromium, headless, Linux), before the review | 145 passed, 1 failed: `panels.spec.ts`'s list of the active panel's verbs, which now holds Pop out; corrected |
| The same, after the first review's fixes | 147 passed |
| The same, after the second review's fixes | 147 passed, 1 failed: `closingWith` in `windows.spec.ts` read the answer to a key sent to a window that the key had closed already; the helper now waits for the window closing, which is what it checks |
| The same, after the third review's fixes | 148 passed |
| The same, after the fourth review's fixes | 148 passed |
| `e2e/windows.spec.ts` and `e2e/areas.spec.ts`, `--repeat-each=3`, after the fifth review's fixes | 60 passed |
| `npx playwright test` (Chromium, headless, Linux), on the client's product code as it stands (unchanged since the fifth review's fixes) | 147 passed, 1 failed: `areas.spec.ts`'s tab dragged with the mouse (step 1's test) stayed in one area. Run alone it then passed 8 times, and in all 64 runs of `panels.spec.ts` and `areas.spec.ts` (16 tests, four times each). It failed once before this step's code too, in #37's runs ("step 1's rest: the pricer by its area" above, its `areas.spec.ts` row: once before the three runs in a row, its drop on an area's edge not taken). Not fixed here: a red that passes on a re-run, open (Known limits) |
| The same, after the eighth review's fixes (the product code unchanged) | 148 passed |
| `arrange back` with a panel in another window, its unit test without the fix and with it | Without it, the panel was in no area (drawn nowhere, still open); with it, back where its area stood |
| A tab's menu in a window 480px high, without the fix and with it | 700px tall and cut off without it; inside the window and scrolling to its last item with it |
| The window, its menus, the command line in it and the notices, in both schemes | Looked at, in the pull request's screens |

The unit tests read `pop out panel` and its refusals, the verbs refused in a window, `send panel to` into a window and
back and its new refusal, and `dock panel` from a window, over the fake port; the arrangement's reading order and room
with windows and the hidden place a whole area left; `withWindowsDocked`; a kept value with `popoutGroups`, read
and refused; and `arrange back` bringing a window's panel back into the main window. The browser tests read what only a browser shows: the window opened by the line and by the menu, its
title, its page's state and a grid driven in it; a whole area popped out and its window closed by the user; the scheme
followed, a tooltip, a menu closed by a press outside it and a select in the window; Ctrl+K and `/` there, a verb and a
dialog run from there with Tab and Escape, and a tab's Rename there; the main window taken back by a press; `send panel
to` both ways and the window's title following its tab; `close panel` closing a window; `dock panel` into the area a
panel came from and into the place its whole area left, as wide as it stood; `ai` from the window; a blocked window; a
reload; a tab's menu in a short window; the strip closing the windows; and `arrange` closing them, with `arrange back`
drawing their panels in the main window.

### Providers

Unchanged; nothing on the server changed. The AI's answer in the test is the scripted stand-in's.

### Known limits

- **The Device Pixel in another window** waits for ExGrid `v0.3.0-rc.1` on npm (the owner's tag, ex-grid #569): until
  then a grid in a window on a display of another scale than the main window's draws its lines at the main window's
  device pixel. The bump to `0.3.0-rc.1`, all seven packages at one version, and the test going green are this pull
  request's last commit, or the next one's; if the bump goes to the next pull request, this spec and its tickets 04
  and 06 stay in progress until it lands.
- A reload does not open the windows again (a browser opens a window only on a key or a press, §9), nor does `arrange
  back`, which follows the reload; the panels come back into the main window, said so.
- Checked in Chromium only: Safari's and Firefox's pop-up rules, touch in a window and a real second monitor are not.
- An area is not split inside another window (one area per window, as a floating area is).
- `edit context` (the chord that focuses the header's context bar) typed in another window focuses the field in the main
  window, which the browser does not bring to the front.
- Step 1's test of a tab dragged with the mouse (`areas.spec.ts`) failed once in the run with 147 passed, the first of
  the two full runs on the product code unchanged since the fifth review's fixes, its tab left in one area, and passed
  in every run after it (above): a red that passes on a re-run, open. It is step 1's test, and failed once before this step's code too (#37's runs, under
  "step 1's rest: the pricer by its area" above); the cause is not found.

### What the review changed

The two reviews (standards and spec) found, and this pull request corrects: a press in the main window did not make it
the window in use again, so after another window had been used the header's Commands, or a dialog opened by a press,
opened in that window; four `instanceof Element` checks the first pass missed (the pricer's and the Rates page's
screen state, a row's draft dialog's Escape, and the strip's tab, which was harmless), and the draft dialog's focus read
the main document, all false in another window; a tab's Rename looked for its tab in the main document alone and threw for a panel in a window; the window's
title did not follow a tab chosen in it; a window that closed before it had loaded was read as one with no panels, and
the window of a tab's menu and the area a reload folds into had defaults standing in for what was not found; the focus
listener of the line could be taken from another document than it was added to; the areas kept a copy of the windows'
registry by hand; a copied mouse press could not prevent the original (a touch is heard passively, and the code and
the spec now say so); and an area was `floating` and `window` at once in its type. The spec's and the ADR note's words
for `size panel` in a window, and tickets 03 and 05, were corrected in the documents to what the code says, with the
reason in the note. The tests the spec asked for and the first pass left out, written now, found that `dock panel` did not put a
window's last panel back where its whole area stood (dockview removed the place before the panel arrived, and the
places were tidied while a window was still opening): the window's area now comes in beside its place. `send panel to`
from a window, `close panel` closing one, the window's title and the main window taken back by a press are tested too.
A tab's menu cut off in a short window, found while the screens were taken, now keeps inside it and scrolls.

A second round of both reviews found, and this pull request corrects: `arrange back` drew nothing of a panel the
arrangement before had in another window (`reconciled` dropped the windows; it now refuses an arrangement with them,
and `arrange back` docks their panels first, as a reload does, and says so); the AI's context described an area in a
window as docked (it now says `where` each area stands); the window's title heard the active panel of the whole page,
so a panel sent to a window or closed there without becoming active left it stale (it now hears the window's own
area); the menus' margin was a literal (now the theme's `sm`); and the line's focus listener stayed in the first
document a chained line held it in. The spec now says `Area`'s `where`, the menus' placement and what `arrange` does
with windows open, and test 2 checks that a window's area comes back as wide as it stood.

A third round found, and this pull request corrects: `arrange back` said only that a panel was back, with a reason that
does not hold for a command run from a key (it now says where each went in the reload's words, from one function,
`whereReturned`, and that the proposal closed the windows); a touch pressed in another window could still try to
prevent the original's default (the touch is heard passively, declared so, and its copy cannot be prevented); the AI's
context had no test with areas (one now holds a docked, a floating and a window's area); a window shell with no area
of dockview's under it would have drawn the main window's title (it was made a defect, thrown); and a theme length
that Mantine reads as NaN passed as pixels.

A fourth round found, and this pull request corrects: `arrange` did not say that it closed the other windows (it now
does, naming their panels); `arrange back` named the proposal as what closed the windows, which the strip may have
done instead (it now says where the panels are, with no reason); the sentence was still written in two places (it is
now one, `backInThisWindow`, tested on each of its branches); and the comment on a window drawn with no area of
dockview's had dockview's order of letting go backwards. That round first made such a window draw nothing; a fifth
round found that this hid a defect, and it is thrown again, its comment now true (dockview lets go of the area a step
before the window, within one call that React does not draw in the middle of). The fifth round also asked for a test
of `arrange` closing two windows and for ticket 05's check to fit all its cases, and a sixth for that test to check
where `arrange` places those windows' panels; all are in. A seventh, of the sixth's fixes, found four things, all
corrected: that test checked only part of the notice, its fixture marked values non-null, its name did not say what it
checks, and this paragraph's account of the rounds was not exact. An eighth, of the seventh's, found that test's guard
saying the opposite of what failed and its fixture holding no panels, the last full run's row not naming its browser
nor its round, step 1's flake left out of the Known limits, and ticket 06 ticking a full run that had failed; all are
corrected, and the windows' entries in `proposals.test.ts` come from one helper. A ninth, of the eighth's, found ticket 06
calling the code final, the spec's and ticket 06's status lines leaving out the flake, the flake's earlier record
missed, one window's entry still written by hand, and this paragraph leaving out what the eighth changed; all are
corrected. A full run after the eighth's fixes passed, 148, so ticket 06's box is ticked again, the failure before it
noted. A tenth, of the ninth's, found the Known limits calling the failed run the last, #37's record cited so that it
could not be found and as the wrong run, the one helper claimed for every test file, and ticket 06 left with no open
box for the run after the bump; all are corrected. The eleventh and twelfth found only words of this section to
correct (which full run failed, #37's section named two ways, one test file claimed as the unit tests, what stays in
progress if the bump goes to the next pull request); they are corrected. The client's product code has not changed
since the fifth round's fixes: the sixth to twelfth changed `proposals.test.ts`, the spec's status, ticket 06 and this
file.

## Next

1. Panels in areas, ADR-0018 step 3 (parts of a page with links), once the owner has seen step 2.
2. MVA with SIMM (`docs/12-implementation-plan.md` P2): spot IM from the XVA request's MVA option.
3. ADR-0010 Delivery step 3 (Advanced: Before-only and After-only edits, the split fill), when the owner asks for it.
4. The owner's review of ADR-0003 §7 (scopes, carry and conflict), then a synthetic source change that can produce a conflict, and the browser cases still open above (reload, failed case unavailable).
5. Rates.
6. ME-ID sign-in through MSAL (C30) and the visibility rules (C31, C32); AI over AG-UI with the company's Copilot behind the adapter port (C34).
7. Slice 01b: Save and Pin (CO06), and the browser parts of CO03 and CO04.

The generic commands (ADR-0016) are built through step 6, and a page keeps its state by panel (ADR-0006 note 10); what
each left for the owner is in its section's "Known limits".

## Later, perhaps

What the owner asked to keep in mind and has not asked to build. Nothing here is planned; an item moves to "Next"
when he asks for it.

- **Bundling many changes on the server** (owner, 2026-11-12). A cell edited by hand is one request and one revision
  today, and a command over many rows is already one. Where a user makes many changes in a row, by hand or by
  command, the server may gather them and apply them together, or work out only the part they touch, as long as the
  outcome is the one applying each in turn gives. The lines it must hold: nothing half applied is shown or priced
  (Price freezes one whole revision); every change stays in the log, for undo, the Changes tab and the account of
  it; changes whose order matters are bundled only where the result is that of their order. Measure the slow
  operation first.
- **More of saved sequences** (ADR-0016 note 60; left out of step 6's build, not asked for by the owner). Taking a
  saved sequence away; values given when it runs (`sequence run usd-only ccy=EUR`); a sequence made from the
  commands the log holds, or recorded while they are typed; a dialog to write one in; a command that lists them;
  a run that writes all of its lines or none.
