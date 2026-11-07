# ADR-0010: The XVA page is a one-screen what-if workbench of layers, cases and scenarios

Status: PROPOSED 2026-11-07. Decided with the owner question by question on 2026-11-06 and 2026-11-07 ("let's grill
every part"); each decision below says whether the owner confirmed it or it is a default for review. Builds on
docs/03 (the XVA domain), ADR-0003 (the server owns the rules; edits in an append-only log) and ADR-0009 (design
guidelines). Resolves the OPEN side-specific editing of docs/03 §4.1 and replaces the page built in slice 02.

## Context

The slice-02 page is one toolbar and one trade grid: no netting-set, credit or CSA grids, no cases, no scenarios,
and a Price that opened its result in another panel.

The layout the owner started from (2026-11-06) is four tabs: Configuration (context,
parameters, CDS, netting set, collateral), Portfolio (new and existing trades, Before/After, iterative), Market
(stress scenarios) and Results (metrics, risks, exposure profiles). The owner does not want it copied ("there is a
better layout"). What it gets in the way of is the what-if loop itself:

- Changing one input and reading its effect crosses three tabs (edit a CSA, Price, Results). The input changed and
  the difference it made are never on one screen.
- What is being priced (value date, counterparty, netting set) is visible only on the Configuration tab.
- Each input section has a "Use as Diff" switch: when on, the section's edits apply to After only. The choice is per
  section, and Before cannot be edited on its own.
- An XVA/FVA switch shows which trades each measure prices, because FVA prices only trades on imperfectly
  collateralised CSAs.
- A second After (the same trade under another threshold) needs a second Price and a manual comparison.

## Decision

### 1. One screen (CONFIRMED, owner 2026-11-06)

From top to bottom:

1. **The context bar**, fixed: value date, counterparty registration (searched as `code | entity | name | flag`;
   a dummy counterparty adds a required Project name beside it), main netting set with its enforceability, display
   currency, the Advanced switch (§2), Refresh, and Price, which says what it will run (`Price 3 cases × 2
   scenarios`). Under it one line of status: source version, revision, `SIMULATED` while a provider is.
2. **The input tabs**, full width: `Credit | Netting | CSA | Settings | Scenarios`, one open at a time, collapsible
   to their tab row, with a drag handle for their height. A tab with changes says how many (`CSA ●2`). They are full
   width because the CDS and CSA grids are wide (tenors from 3M to 20Y; a CSA's general terms plus both parties'
   threshold, MTA and currency). A left pane was proposed first and rejected for that reason (owner, 2026-11-06).
3. **The trades**: New above Existing (§6).
4. **The result dock** (§7), collapsible to one line, drag-resizable.

Below `sm` (768px) the screen is three tabs, Inputs, Trades and Results, with the context bar above them. Everything
follows `docs/design-guidelines.md`.

### 2. Original, Before and After are layers of changes (CONFIRMED, owner 2026-11-06)

- **Original** is what the sources gave for this context: immutable, read-only on screen.
- **Before** and **After** are each Original plus changes. A change belongs to one **layer**:
  - **Common**, applied to both sides. This is where every edit goes by default: in practice every edit other than
    trade membership applies to both sides (owner).
  - **Before only** and **After only**, reached through the Advanced switch. Advanced shows a `Before | After` choice
    of where the next edit goes. A side-only value is marked by its cell's highlight (below) and in the changes
    panel; turning Advanced off keeps such changes and their marks.
- Before and After still differ mainly by the trades' B and A membership.
- "Use as Diff" is not offered. A section with it on is the After-only layer for that section's fields; off is
  Common. Any editable field can now be side-only, not a whole section.

**A value that differs from Original is highlighted in its cell** (owner, 2026-11-07). The cell is filled with the
Before colour when only Before differs, with the After colour when only After differs, and split diagonally, Before
above left and After below right, when both differ. A Common change differs on both sides, so in normal use a changed
cell is split.

- The test is the value, not the edit: a value edited back to its Original is not highlighted, and a value a rule
  changed (a CSA's discount curve carried to the trades that reference it) is.
- The rule reaches every value Original holds: the input tabs' grids and fields and the trade grids. A trade's B and A
  membership is shown by its boxes and is not highlighted, and neither is the context itself (value date,
  counterparty), which chooses Original rather than differing from it (DEFAULT).
- When the two sides hold different values, the cell shows the side the Advanced choice points at, and After while
  Advanced is off (DEFAULT).
- The two colours have no other meaning on the page and are not among those design guidelines §7 already gives (DEFAULT
  violet for Before, cyan for After, light tints that keep the text's contrast in both themes). They are the same
  colours wherever the page shows a side, the result dock's B and A columns included.
- Colour is never the only signal (design guidelines §7): the cell's description gives Original and each side's value,
  and the changes panel lists every change in words.
- ExGrid draws a one-sided highlight already: `cellAppearance` (ex-grid ADR-0050 item 15, ADR-0071) gives a cell a
  solid Fill by (row, column), as `0xRRGGBB`, so the page answers the colour of the scheme in force and hands a new
  function when the scheme changes. The diagonal split is not drawable: a Fill is one solid colour. Until ExGrid can
  draw it, a cell that differs on both sides wears ExGrid's `modified` corner mark instead. Before Advanced
  (Delivery step 3) every changed cell differs on both sides, so the mark tells nothing wrong meanwhile.

Before = Original + Common + Before-only; After = Original + Common + After-only. Price freezes each side as its own
complete input, as Before and After are already independent requests (docs/03 §5.1).

### 3. Cases (CONFIRMED, owner 2026-11-06)

A **case** is one Before/After pair priced together, the unit a row of the results shows. Without
variants the workspace is one case.

- **A case is the workspace plus the case's own changes.** A case's change applies to both of its sides
  (owner, 2026-11-06); After-only within a case is reached through Advanced.
- **Vary from a cell.** On any editable value, *Vary values…* takes a list (`10m, 5m, 0`) and makes one case per
  value, named from what it changed (`CSA-A threshold 5m`). The command line does the same (`vary`).
- **Iterative individual addition** (docs/03 §5) is a generated set of cases, one `add <trade>` per eligible new
  trade with After checked, with docs/03 §5's rules unchanged: not cumulative, a common change to existing trades is
  stated in every case, a candidate in Before is refused rather than unchecked.
- **The changes panel** lists the Common and side-only changes and each case's own, and edits or removes them there,
  so nobody has to find a changed cell in a wide grid.
- One Price is one Run with one case per (case, scenario) (§4). Their number is capped (DEFAULT 50, configured); over
  the cap Price is refused and says by how much.

### 4. Scenarios (CONFIRMED, owner 2026-11-07)

- A **scenario** is a named set of market-data shifts; one scenario may shift several data at once. The Scenarios tab
  is one grid of shifts, `Scenario | Category | Index | Risk factor | Shift type | Size | Description`; rows with the
  same scenario name are one scenario. Beside it, the list of scenarios with their shift counts and an include box.
- Two shifts that touch one risk factor in one scenario are refused at Price, naming the rows.
- Price always prices the unshifted market. Each included scenario is **crossed with every case**: three cases and two
  scenarios are nine Before/After pairs (owner: "crossing is right").
- Scenario sets are saved by name, and the user chooses who sees each (themselves, their desk, everyone), as saved
  workflows are shared (C31). Example: many scenarios built for a cross-gamma simulation.
- **Scenarios are defined by command and by the AI as well as in the grid** (owner, 2026-11-07). Defining, changing,
  including, saving and loading a scenario are command capabilities (ADR-0005), so the command line and the AI pane
  (ADR-0007) reach them as tools: `scenario <name> <shift>; <shift>; …`, `scenario include <name>`,
  `scenario save <set> [desk|all]`, `scenario load <set>`. The AI writes the same shifts the grid does, shown in the
  grid before anything is priced. A request whose index, risk factor or shift type does not resolve is refused by
  name and asked for, never guessed.
- **A scenario of several shifts is defined whole, never one shift per step** (owner's question, 2026-11-07; the
  forms below were proposed the same day and the owner agreed to them for now):
  - *Grid.* Rows are typed or pasted many at a time (a block copied from a spreadsheet is a paste); the rows that share
    a name are one scenario. Duplicate copies a scenario's shifts under a new name, to be edited.
  - *One command, every shift.* `scenario s1 jpy_ois +10bp; usdjpy -5%; jpy_ois 5y +3bp` is one scenario of three
    shifts. A shift's tokens are in free order and recognised by their form, as a trade's are (ADR-0008): the index
    gives the category; an index alone is the whole index, a tenor (`5y`) narrows it to that point; the size's unit
    gives the shift type (`bp` absolute, `%` relative), and a shift type the unit does not settle is written
    (`abs`, `rel`). The example above is refused, because its first and third shifts both touch JPY OIS 5Y.
  - *Generators make a set of scenarios at once.* `scenario ladder jpy_ois -50bp..+50bp step 10bp` makes one scenario
    per size. `scenario cross jpy_ois -50bp, 0, +50bp x usdjpy -10%, 0, +10%` makes one scenario per combination,
    each shifting both factors, as a cross-gamma simulation needs; more than two lists cross the same way. A zero
    size is no shift, so the all-zero combination is the base, which Price prices anyway, and is not made again; a
    combination with one zero is a scenario of the other shift alone. Each generated scenario is named from its
    shifts (`jpy_ois +50bp · usdjpy -10%`). The number made and the cap on cases × scenarios (§3) are shown before
    anything is made; what is made is ordinary rows, edited in the grid like any other.
  - *The AI* uses these same commands, so one request ("cross JPY OIS ±50bp with USDJPY ±10%") is one generator
    call, not a shift at a time.

### 5. FVA prices only imperfectly collateralised CSAs (CONFIRMED, owner 2026-11-06)

A CSA is **imperfectly collateralised** when any of these holds:

1. it is one-way;
2. it margins less often than daily;
3. for either party, MTA + threshold exceeds 1mm GBP, converted at the value date's FX from the workspace's pinned
   market data.

FVA's portfolio is the trades on imperfectly collateralised CSAs. An uncollateralised trade (`nocsa_{ma id}`) is not
in it: the engine gives it FVA 0. The 1mm GBP is configuration and the rule's version is recorded on the Run. The rule
is the XVA module's, on the server, once; the page never decides it.

On screen: a read-only FVA column on both trade grids, with its reason (`CSA-A: daily, MTA+threshold 0.4mm GBP`), an
*FVA only* filter, and in the result dock each measure's trade counts (`FVA 300 → 301`). A side-only CSA change can
move a trade in or out on one side, and the column marks that. There is no XVA/FVA switch.

### 6. The trade grids (CONFIRMED, owner 2026-11-06 and 2026-11-07)

- **New trades**: as high as their rows, at most about eight before the grid scrolls. Above it one input line: trade
  IDs pasted and Enter, or XML dropped or uploaded (also `add trades <ids>`).
- **Existing trades** take the rest of the height and filter from their column headers.
- Both have B and A columns whose header selects or clears every row of the grid, not only those drawn (docs/03 §4).
- **PV01** of a new trade is a result, requested when the trade loads, read-only: pending, its figure, or why it
  failed; never 0. A retry is the failed cell's action and the command `pv01 retry`, not a standing button: it is
  rarely needed.
- **Margin (bps)** is an input. **PV Adj** (the exposure's adjustment at time 0, docs/03 §2.5) is PV01 × Margin, each
  with its own sign, whenever Margin is entered: Margin wins, PV Adj is then read-only and says it came from Margin.
  With Margin empty PV Adj is typed. Price freezes PV Adj with the PV01 and Margin it came from; a later PV01 does not
  change a frozen Run.
- Existing trades show their official PV from the source as the PV that adjustment uses.

### 7. The result dock (CONFIRMED, owner 2026-11-07, unless marked)

- Tabs `Metrics | Risks | Profile` and a scenario selector.
- **Metrics**: a row per case; per measure Before, After and Diff (After − Before) in the display currency. A failed
  case says `unavailable`, never 0 (EX06). The measures shown are chosen per user and kept (DEFAULT).
- **Collapsed**, one line per case with CVA Diff and FVA Diff; MVA joins when it exists.
- A result older than the workspace says so in yellow: `revision 5 · workspace now 7`.
- The inputs it priced (counterparty, netting set, recovery, CDS, cost of capital, value date, currency) come from
  the Run, not from the screen.
- **Download** saves the Run as a zip: its frozen inputs, results, lineage and diagnostic artifacts. It is a Run's
  action, so it is also on the Pricing DB and is the command `download run`.
- **Copy quote** puts on the clipboard what a trader sends sales to charge: the Run's header and each case's CVA
  Charge and PV, as HTML and plain text.

### 8. The input tabs' contents (DEFAULT, for the owner's review)

The input grids, with the layers of §2 instead of a "Use as Diff" switch, and Validate and Reset as commands and
tab actions rather than a button pair per section.

- **Credit**: CDS type (single name, proxy). Single name: the reference searched as `ticker | ccy | seniority | doc
  clause | name`. Proxy: Sector, Region, Rating. The grid: type, ticker, currency, seniority, doc clause, CDS recovery
  and the tenor spreads in basis points; spreads editable.
- **Netting**: main, netting set ID, CVA recovery, enforceability. Enforceability is the source's and read-only
  (docs/03 §2.2 DEFAULT). The CVA recovery is the netting set's; empty stays empty and the
  CDS recovery applies (XV15).
- **CSA**: main, ID, type, master agreement, currency, discount curve, FVA discount curve, frequency, exchange; then
  Counterparty and Self, each threshold, MTA and currency; then the derived *Imperfect* column of §5. *Add CSA* makes
  a hypothetical CSA with a workspace identity (docs/03 §2.4). Eligible collateral and SPV collateral are read-only
  sub-grids, collapsed.
- **Settings**: MPOR override, cost of capital, charge ratio, PV adjustment, risks included, T-1 CRIF, advanced
  configuration. Typed and validated on the server like any other input.

### 9. Every action is a command (ADR-0006)

`price`, `refresh`, `vary`, `add trades`, `pv01 retry`, `advanced on`/`off`, `scenario …` (§4), `download run`,
`copy quote`, and one to open each input tab. Chords are not assigned here.

Those that change the workspace (an edit in any layer, `vary`, `add trades`, the scenario commands) and Price are
registered as command capabilities (ADR-0005), so the AI calls them as it calls the Rates commands; XVA registers
none today. Opening a tab, Download and Copy quote are the page's own and are not the AI's.

## Consequences

- The kernel's edit gains an optional **layer** and **case** key, opaque to it, as scope keys are (ADR-0003 §7): XVA
  declares Common, Before-only, After-only and its cases; the carry check and conflict groups apply per key. Rates is
  unaffected.
- A Run holds cases × scenarios; the Pricing DB lists one row per Run and opens its cases.
- The fake XVA provider follows §5 (no FVA for an uncollateralised trade) and the synthetic CSAs gain frequency,
  one-way, and both parties' threshold, MTA and currency.
- The slice-02 page is replaced. The changes made to it on 2026-11-06 (one Control Height, an empty CVA recovery that
  shows empty, the latest Price on the page) carry into the new one.

### Delivery order (DEFAULT)

1. The screen of §1 with Common edits only; Netting and CSA tabs; the FVA rule; New/Existing grids with PV01, Margin
   and PV Adj; the Metrics dock with Download and Copy quote.
2. Cases: Vary, iterative individual addition, the changes panel.
3. Advanced: Before-only and After-only layers.
4. Scenarios.
5. Credit tab with CDS term structures; Risks and Profile.

## OPEN

- What CVA Charge is computed from (charge ratio, cost of capital) is the engine's; the fake provider will show it
  as simulated until the owner says what it should approximate.
- MVA (with SIMM) and the Risks and Profile contents are not specified here.
- The case cap (DEFAULT 50).
- Whether a coarse pointer gets larger controls (ADR-0009's OPEN) applies here too.
- §2's diagonal split: ex-grid ADR-0490 (proposed 2026-11-07, AkiraMisawa/ex-grid#545) adds a Split Fill to
  ExGrid's `cellAppearance` (`lowerRightFill`), needed by Delivery step 3. Template Columns were rejected:
  a Template cell is not edited or copied as an ordinary cell (ex-grid ADR-0020).
- Whether spot SIMM and spot CCR capital join this workbench as metrics of the same portfolio (owner, 2026-11-07:
  their configuration largely overlaps and all are portfolio calculations). Until that is decided, the input tabs,
  the layers, cases and scenarios, the Run and the dock's metric choice are written for a portfolio and its metrics,
  and only §5 and the FVA column are XVA's own. In production, a KVA option on the XVA analytics request also returns
  CCR capital, spot and profile, and only spot capital needs a drill-down, to trade level (owner, 2026-11-07). So
  the XVA request is kept open to that option: CCR capital may arrive in the same response as CVA and FVA, as a
  measure of its own. Its design is the spot CCR capital thread's.
