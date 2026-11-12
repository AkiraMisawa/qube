# ADR-0010: The pricer is a one-screen what-if workbench, shared by the measures that register into it

Status: ACCEPTED 2026-11-07 by the owner. Decided with the owner question by question on 2026-11-06 and 2026-11-07
("let's grill every part"); each decision below says whether the owner confirmed it or it was a default. Asked to
review the defaults (§8's tab contents, §10's DEFAULT parts, the case cap), the owner took them as recommended
("おすすめでいいよ", 2026-11-07), so every DEFAULT here stands as written until a slice gives a reason to change
it. Builds on docs/03 (the XVA domain), ADR-0003 (the server owns the rules; edits in an append-only log) and ADR-0009
(design guidelines). Resolves the OPEN side-specific editing of docs/03 §4.1 and replaces the page built in slice 02.

*Rewritten on 2026-11-07, the day it was proposed, when the owner folded spot SIMM and spot CCR capital into the same
page ("1ページに統合", one page). It was "The XVA page is a one-screen what-if workbench of layers, cases and
scenarios". What changed: the page is no longer XVA's. Measures register into it (§10), and the parts that were
XVA's are XVA's registration (§11). Section numbers are kept, so references to them still hold. Nothing the owner
confirmed was dropped. T-1 CRIF left Settings: the owner said it is not in this application.*

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

Spot SIMM and spot CCR capital share most of this configuration (counterparty, netting sets, CSAs, trades, Before and
After), and all three are calculations over one portfolio (owner, 2026-11-07). Both are regulatory figures, and the
owner wants every term of their formulas traceable, down to trade level. In production the XVA analytics request
computes them too: with MVA on it returns spot SIMM and a SIMM profile, and with KVA on spot CCR capital and a capital
profile; only the spot figures are drilled down (owner, 2026-11-07).

## Decision

### 1. One screen (CONFIRMED, owner 2026-11-06)

From top to bottom:

1. **The context bar**, fixed: value date, counterparty registration (searched as `code | entity | name | flag`;
   a dummy counterparty adds a required Project name beside it), main netting set with its enforceability, display
   currency, the Advanced switch (§2), Refresh, the measures to price (§10), and Price, which says what it will run
   (`Price CVA FVA IM · 3 cases × 2 scenarios`). Under it one line of status: source version, revision, `SIMULATED`
   while a provider is.
2. **The input tabs**, full width: the shared `Netting | CSA | Settings | Scenarios`, and the tabs the selected
   measures register, such as XVA's `Credit` (§11). One is open at a time, collapsible to their tab row, with a drag
   handle for their height. A tab with changes says how many (`CSA ●2`). They are full width because the CDS and CSA
   grids are wide (tenors from 3M to 20Y; a CSA's general terms plus both parties' threshold, MTA and currency). A left
   pane was proposed first and rejected for that reason (owner, 2026-11-06).
3. **The trades**: New above Existing (§6).
4. **The result dock** (§7), collapsible to one line, drag-resizable.

Below `sm` (768px) the screen is three tabs, Inputs, Trades and Results, with the context bar above them. Everything
follows `docs/design-guidelines.md`.

*Note of 2026-11-12 (ADR-0018 §5, accepted by the owner with that record):* "below `sm`" is read by the width of the
area the page is in, not the window's, now that a page can stand in an area beside others. A pricer in a 600px area is
the three tabs, as it is in a 600px window; the full-height button and the page's heights read its area alike. The
pricer reads its area from a pull request of its own, after ADR-0016's step 5d and ADR-0018's step 1 (ADR-0018 §11 and
its note of 2026-11-12); until that merges, it reads the window, as before.

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
- **Vary from a cell.** On any editable value, *Vary values…* takes a list (`10mm, 5mm, 0`) and makes one case per
  value, named from what it changed (`CSA-A threshold 5mm`). The command line does the same, naming the cell by its
  row and column: `vary CSA-A threshold 10mm, 5mm, 0` makes three cases (DEFAULT form; amounts as ADR-0008 writes
  them, `mm` and never `m`).
- **Iterative individual addition** (docs/03 §5) is a generated set of cases, one `add <trade>` per eligible new
  trade with After checked, with docs/03 §5's rules unchanged: not cumulative, a common change to existing trades is
  stated in every case, a candidate in Before is refused rather than unchecked.
- **The changes panel** lists the Common and side-only changes and each case's own, and edits or removes them there,
  so nobody has to find a changed cell in a wide grid.
- **A Price without Before prices After alone** (owner, 2026-11-07: with B not selected only After has a result, no
  Before/After comparison is wanted, and the drill-down must still work). A side with no trade in it is not priced,
  never priced as zero: with every B box clear the case is After only (the owner confirmed this reading), and the
  same holds for a Price with every A box clear. Price says so before it runs (`Price After only · CVA FVA IM`); a
  case with no trade on either side is refused by name. Metrics and the trace for such a case are in §7.
- One Price is one Run with one case per (case, scenario) (§4). Their number is capped (DEFAULT 50, configured); over
  the cap Price is refused and says by how much.

*Note of 2026-11-08 (DEFAULT, writing Delivery step 2's spec; told to the owner the same day):*

- **The Base is always priced.** The workspace with no case's change is the case `Base`, the first row, as Price always
  prices the unshifted market (§4). A `vary` value equal to the one the workspace holds makes no case, as a zero size
  makes no scenario. In iterative addition the Base holds every candidate, so the package and each trade alone are
  read together.
- **A second `vary` adds its cases** beside those already made, never crossed with them: crossing is the scenarios'
  (§4). A case whose changes are those of a case already made is not made again; `vary` says so.
- **Iterative addition's set is made once.** `add trades` makes one case per New row with A checked, at that moment.
  Price refuses, by name, a set that no longer matches the New rows with A checked (docs/03 §5: never another set
  quietly), and `add trades` again makes the set anew. A case of the set is not a `vary` case: its change is the
  membership, not a value, so it has no cell to be edited from.
- **A case's change is an edit with the case's key** (Consequences), so it is carried and refused like any other, and
  is marked against Original in the case's own reading. Removing a case removes its changes in one entry of the log.
- **The changes panel is an input tab, `Changes`**, last in the row, which says how many changes it holds as every tab
  does: the Common changes, then each case with its own. Each change is edited or removed there, and a case removed
  whole.
- **Every measure is priced for every case**, as for every scenario (§4): one request per module still, holding every
  case, so a provider prices them as it prices Before and After, independently.

*Note of 2026-11-10 (owner; ADR-0016 §3):* an iteration of iterative addition may add several candidates together,
and take existing trades out of After in the same case. It is given as lists; how the line spells them is open
there. The set is still every New row with A ticked, and no iteration is cumulative. Designed there and not built:
`add trades` still makes one case per candidate.

*Note of 2026-11-11 (ADR-0016 step 3d, its note 37):* built. `add each` takes its iterations as a list, a case of
the set names the trades it adds and those it takes out, and Price's check reads them together. The New grid's
caption has `Add each` and `Make case…`; the second, and *Make case from the selection…* on both trade grids'
Context Menus, open a dialog on what the two grids' selections name, and *Make case* adds that case beside those
made, which stay the cases they are (`keep`). The Changes tab lists a case's trades under it, leaves one out of
what the case adds and keeps in After one it takes out; the one trade a case adds is not left out there, and the
case is removed whole. How the line spells the lists is still open. *Changed in review, the same day:* the buttons
were first named `One case…`, and *Make case* first said the whole set again, which a stale case of the set
refused by a trade the dialog does not list (ADR-0016 note 37's corrections).

### 4. Scenarios (CONFIRMED, owner 2026-11-07)

- A **scenario** is a named set of market-data shifts; one scenario may shift several data at once. The Scenarios tab
  is one grid of shifts, `Scenario | Category | Index | Risk factor | Shift type | Size | Description`; rows with the
  same scenario name are one scenario. Beside it, the list of scenarios with their shift counts and an include box.
- Two shifts that touch one risk factor in one scenario are refused at Price, naming the rows.
- Price always prices the unshifted market. Each included scenario is **crossed with every case**: three cases and two
  scenarios are nine Before/After pairs (owner: "crossing is right"). Every selected measure is priced for every pair
  (DEFAULT).
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

*Note of 2026-11-09 (DEFAULT, writing Delivery step 4's spec; told to the owner the same day, as the owner asked on
2026-11-08 to go with the recommendation and hear it afterwards):*

- **The market-data catalogue is the portfolio's, and synthetic.** *IR*: each curve the portfolio prices on, the
  discount curves (`jpy_ois`, `usd_sofr`, `eur_estr`) and the funding curves (`jpy_funding`, `usd_funding`,
  `eur_funding`), whole or at one of the tenors `1m 3m 6m 1y 2y 5y 10y 30y`, shifted absolutely, in bp. *FX*: each
  currency against JPY, the bank's domestic currency (`usdjpy`, `eurjpy`, `gbpjpy`), shifted relatively, in %; every
  cross moves with the two it is made of. A size in a unit its category does not take is refused, naming the one it
  does: a relative rate shift and an absolute FX shift come when a provider needs one. *Credit*, the counterparty's
  CDS spreads, joins the catalogue with the Credit tab (Delivery step 5).
- **A tenor shift is a key-rate shift**: whole at its tenor, falling linearly to nothing at the catalogue's tenors
  either side of it, and whole beyond the first and the last. A shift of the whole index moves every point. So
  `jpy_ois +10bp` and `jpy_ois 5y +3bp` touch one risk factor and are refused together (§4); `jpy_ois 5y` and
  `jpy_ois 10y` do not touch.
- **A scenario belongs to the workspace, not to the counterparty**: a counterparty change keeps every scenario, since
  market data is no counterparty's. Each shift is a row the workspace created (ADR-0004), edited as any cell is; the
  Category follows from the index and the Shift type from the size's unit, so both are read-only. The grid always
  ends in blank rows to type or paste into, so a block copied from a spreadsheet lands on them (ExGrid's spilled
  paste, ex-grid ADR-0050 §3); clearing every cell of a row removes it. A row that is incomplete or does not resolve
  says why on the row, and Price refuses an included scenario that holds one, naming the scenario and the row.
- **A scenario is included when it is made.** Its include box, or `scenario include <name>` and `scenario exclude
  <name>`, leave it out of Price without removing it. `scenario <name> <shift>; …` defines a scenario whole: it replaces
  the rows of one of that name, keeps whether it is included, and says what it replaced. A generated scenario replaces
  one of its name the same way.
- **The cap counts pairs**: cases × (the included scenarios and the base market), DEFAULT 50 as in §3, and Price over
  it says by how much. A generator makes at most the scenarios the cap could price with the Base alone (49), and is
  refused over that; the number it would make, and the pairs that leaves against the cap, are said on the command line
  before it runs.
- **Each provider applies the shifts to what it reads**, the accepted input carrying each scenario's shifts beside the
  cases. Spot IM generates its CRIF again on the snapshot's curves and the FX shifted. The simulated XVA provider and
  spot CCR capital revalue each trade by a toy sensitivity to its curve and its currencies, and say they are simulated
  as their figures already do. A provider that reads a shifted risk factor and cannot apply the shift refuses its
  measure for that scenario, by name; it never prices the base market under the scenario's name.
- **The dock's scenario selector** (§7) lists the base market first, then each scenario the Run priced. Metrics, the
  collapsed line, the measures' result tabs and Copy quote follow it, and Copy quote names the scenario when it is not
  the base market.
- **The grid's Category and Shift type take a value, yet stay derived.** A block of all seven columns copied from a
  spreadsheet must paste whole, and ExGrid refuses a paste whose target holds a read-only cell; so both columns take
  what is typed or pasted, and the server keeps it only where it is what the index or the size gives, refusing it by
  row and column otherwise. The grid ends in a read-only *Issue* column, after the seven, which says why a row cannot
  be priced, as "says why on the row" asks, and the cell the issue is of is marked.
- **A scenario whose rows are gone is left out of nothing.** Clearing or renaming every row of an excluded scenario
  drops its name from those left out of Price, so a scenario of that name made later is included, as one made is.
- **Saved sets.** `scenario save <set> [desk|all]` saves every scenario of the workspace, included or not, under a name
  of the user's; saved again, it replaces the user's own set of that name. A set is the user's alone unless `desk` or
  `all` says otherwise; a user's desk is configuration (`Qube:Desks`), and `desk` without one is refused. Only its
  owner replaces a set (and an administrator, once roles exist, C32). `scenario load <set>` adds the set's scenarios,
  replacing those of the same name, and says what it replaced; a name that two sets the user can see share is refused,
  naming their owners, and `<set>@<owner>` picks one. A set's name is one word without `@`, so `desk` or `all` can
  follow it. A loaded scenario is made as `scenario` makes one: one that replaces a scenario of its name keeps whether
  that one was included, and one made anew is included, whatever its box said where it was saved. *(The store is the kernel's, generic over kinds, as saved workflows and views will be (C32): it keeps
  an item by kind, owner and name and decides who sees it, and never reads what the item holds. `xva.scenario.save`'s
  effect is `SaveItem`, DEFAULT 2026-11-09: it changes no workspace, so it is not `EditWorkspace`, yet it keeps
  something for its caller, which no Run may do; `SaveItem` joins `EditWorkspace`, `Subscribe` and `ReadRun` as an
  effect that makes a capability a command, as ADR-0011 added `ReadRun`.)*
- *Note of 2026-11-11 (ADR-0016 step 3b, its note 32):* every scenario command is a command capability whose line
  the server reads, and the line says what each would do before Enter: the scenario and its shifts, how many a
  generator makes and the pairs Price would then price, what a set would add. The Scenarios tab's include box,
  Remove, Duplicate… and Remove all invoke the same capabilities; the grid's own edits stay the page's. The page's
  endpoints for a scenario by name, for the generators and for the saved sets are gone. `scenarios clear` with no
  scenario is refused, where it did nothing and said it had removed them.

### 5. FVA prices only imperfectly collateralised CSAs (CONFIRMED, owner 2026-11-06; XVA's registration)

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
move a trade in or out on one side, and the column marks that. There is no XVA/FVA switch. This is
the first case of a measure's membership, which every measure now declares (§10).

### 6. The trade grids (CONFIRMED, owner 2026-11-06 and 2026-11-07)

- **New trades**: as high as their rows, at most about eight before the grid scrolls. Above it one input line: trade
  IDs pasted and Enter, or XML dropped or uploaded (also `add trades <ids>`).
  *Note of 2026-11-10 (owner; ADR-0016 §3):* bringing trades in and making the set of iterative addition (§3) are
  different acts and take different names: `load trades <ids>` here, and `add each` for the set. `add trades` stays
  the set's name in the code, and this line has no command, until those are built.
- **Existing trades** take the rest of the height and filter from their column headers.
- Both have B and A columns whose header selects or clears every row of the grid, not only those drawn (docs/03 §4).
  *Note of 2026-11-07, writing Delivery step 1's spec:* ExGrid has no header action a Consumer can give a column, so
  until it has one the all/none controls for B and A sit on the grid's caption, beside its title, and are the commands
  `before all|none` and `after all|none` for the grid in view. Setting every row is one edit on the grid, not one per
  row; a row's own edit made later wins over it.
- XVA's columns (§11):
- **PV01** of a new trade is a result, requested when the trade loads, read-only: pending, its figure, or why it
  failed; never 0. A retry is the failed cell's action and the command `pv01 retry`, not a standing button: it is
  rarely needed.
- **Margin (bps)** is an input. **PV Adj** (the exposure's adjustment at time 0, docs/03 §2.5) is PV01 × Margin, each
  with its own sign, whenever Margin is entered: Margin wins, PV Adj is then read-only and says it came from Margin.
  With Margin empty PV Adj is typed. Price freezes PV Adj with the PV01 and Margin it came from; a later PV01 does not
  change a frozen Run. With Margin entered and PV01 pending or failed, XVA's measures are refused by name at Price
  (DEFAULT, note of 2026-11-07), since the adjustment they would price is unknown.
- Existing trades show their official PV from the source as the PV that adjustment uses. The official PV is shared
  trade data, not XVA's: SA-CCR's replacement cost reads it too for an existing trade (ADR-0013).
- A selected measure that declares membership (§10) adds its read-only membership column to both grids, with its
  reasons and an *in <measure>* filter (by scope where it has several), as FVA's of §5.

### 7. The result dock (CONFIRMED, owner 2026-11-07, unless marked)

- Tabs `Metrics` and those the selected measures register (XVA's `Risks` and `Profile`, §11), and a scenario
  selector.
- **Metrics**: a row per case; per selected measure Before, After and Diff (After − Before) in the display currency.
  A failed case says `unavailable`, never 0 (EX06), and a failed measure leaves the others standing. The metrics
  shown are chosen per user and kept (DEFAULT).
- **A one-sided case** (§3) shows the priced side alone: no Diff, and the other side's cell says `not priced`, never 0
  or `unavailable`, because nothing failed. When no case of the Run has the other side, its column and Diff are not
  drawn and the dock says once `After only: Before was not priced`. The collapsed line shows the priced side's figure
  in place of a Diff, and Copy quote quotes it and says which side it is (DEFAULT).
- **A Metrics cell opens its calculation trace** where the measure has one (ADR-0011): the measure, the case and the
  scenario in view are the trace's root, and a Before, After or Diff cell opens the same view, which pairs Before,
  After and Diff term by term (ADR-0011 §4). A one-sided case is traced on its priced side alone, with the same
  root and depth (owner, 2026-11-07; ADR-0011 §4). Spot IM and spot CCR capital have one, to trade level (owner,
  2026-11-07).
  *Note of 2026-11-08 (DEFAULT, ADR-0012's delivery step 2):* a metric may name the term its cell opens, where the
  trace has several roots: spot IM's IM-R opens at `collect`, IM-P at `post`. The shared result gains a note per side,
  said beside that side's figure (spot IM's governing regime), and a basis said with the row's figures, such as
  `SIMM PARAMETERS DEMO`. Both are empty for a measure that has nothing to say there, and neither is ever a figure.
  A row may also say once the rate its figures were converted at, where a measure computes in another currency than
  the display currency (spot IM, from USD, ADR-0012 §7's note of 2026-11-08); its tab says it above the trace.
  *Note of 2026-11-08 (ADR-0012's delivery step 3):* a measure registers its own result tabs as data, each an id, a
  title and a view (`trace` today, §10's results), and the dock's tabs are Metrics and the tabs of the selected
  measures, as this section says, where the Run in view priced them, since a tab draws that Run's trace: a measure
  left out after the Price keeps its Metrics but loses its tab. A Metrics cell, or `explain`, of a measure with
  such a tab opens that tab at the term, in place of a panel of its own. Opening a measure's tab widens the dock over
  the input tabs and the trade grids; the dock's `Restore height` button, or `dock restore`, puts it back to the height
  its handle sets (CONFIRMED, owner 2026-11-08). `dock full` widens it again and `open result <tab>` shows a tab. Below
  `sm` the dock is already the page's Results section, and stays so. The tab of a measure priced on no side says why,
  as the Metrics do, rather than read a trace that was never written.
- **Collapsed**, one line per case with each selected measure's headline Diff: CVA Diff and FVA Diff when XVA is
  selected (owner), MVA when it exists.
- A result older than the workspace says so in yellow: `revision 5 · workspace now 7`.
- The inputs it priced (counterparty, netting set, recovery, CDS, cost of capital, value date, currency) come from
  the Run, not from the screen.
- **Download** saves the Run as a zip: its frozen inputs, results, lineage and diagnostic artifacts. It is a Run's
  action, so it is also on the Pricing DB and is the command `download run`.
- **Copy quote** (XVA's, §11) puts on the clipboard what a trader sends sales to charge: the Run's header and each
  case's CVA Charge and PV, as HTML and plain text. *Note of 2026-11-07 (DEFAULT):* both are the case's Diff (After −
  Before), the charge and the value the case adds; a one-sided case quotes its priced side and names it. The quote is
  read from the Run's retained result, never from the screen.

### 8. The input tabs' contents (DEFAULT, for the owner's review)

The input grids, with the layers of §2 instead of a "Use as Diff" switch, and Validate and Reset as commands and
tab actions rather than a button pair per section. Netting, CSA and Settings are shared; what a measure adds to them
is marked with the measure.

- **Credit** (XVA's): CDS type (single name, proxy). Single name: the reference searched as `ticker | ccy |
  seniority | doc clause | name`. Proxy: Sector, Region, Rating. The grid: type, ticker, currency, seniority, doc
  clause, CDS recovery and the tenor spreads in basis points; spreads editable.
- **Netting**: main, netting set ID, enforceability, and XVA's CVA recovery. Enforceability is the source's and
  read-only (docs/03 §2.2 DEFAULT). The CVA recovery is the netting set's; empty stays
  empty and the CDS recovery applies (XV15).
- **CSA**: main, ID, type, master agreement, currency, discount curve, FVA discount curve, frequency, exchange; then
  Counterparty and Self, each threshold, MTA and currency; then FVA's derived *Imperfect* column of §5. *Add CSA* makes
  a hypothetical CSA with a workspace identity (docs/03 §2.4). Eligible collateral and SPV collateral are read-only
  sub-grids, collapsed.
- **Settings**: one section per selected measure. XVA's: MPOR override, cost of capital, charge ratio, PV adjustment,
  risks included, advanced configuration (§11). Typed and validated on the server like any other input. The MPOR here
  is XVA's model override; SA-CCR's MPOR is regulatory and its own (ADR-0013).

*Note of 2026-11-09 (DEFAULT, writing Delivery step 5's spec; told to the owner afterwards, as the owner asked on
2026-11-08):*

- **The CDS catalogue is the portfolio's, and synthetic**, as the market data of §4 is: single names keyed by ticker,
  currency, seniority and doc clause, each with a name, a recovery and a spread at each CDS tenor `6m 1y 2y 3y 5y 7y
  10y`; proxies, one curve for each sector, region and rating. The source names each counterparty's CDS: a single name
  for each ABC entity, a proxy for XYZ Industries, an unrated corporate.
- **The counterparty's CDS is a choice the workspace holds**, the counterparty's as its recovery is: single name (by its
  reference), proxy (by its sector, region and rating) or none. A curve's spreads are edits of that curve, one per tenor,
  so an edit stays with its curve: choosing another CDS leaves it unpriced, and choosing the curve again prices it.
  Spreads are typed in bp and are above 0. The CDS recovery is the curve's, read-only; an empty CVA recovery takes it
  (XV15).
- **A missing CDS refuses CVA**, and CVA Charge with it (§11): none chosen, or a proxy missing one of its keys, says
  so by name, and every other measure is priced (§10).
- **Commands**: `cds <words>` chooses a single name by its words in any order, as ADR-0008 reads a trade's, refusing
  words that name none or several with what they could be; `cds proxy <sector> <region> <rating>`; `cds none`. The cell
  `cds <tenor>` is the chosen curve's spread (`set cds 5y 120bp`, `vary cds 5y 100bp, 140bp`), and `credit <field>`
  the choice itself. `open credit` and `open settings` open the tabs. `cds` is a command capability.
- **Credit joins the scenario catalogue**: the index `cds`, the counterparty's chosen curve, whole or at a CDS tenor,
  shifted absolutely in bp, key-rate on the CDS tenors. Each category reads its own tenors, so a tenor of the other is
  refused naming the category's.
- **Settings are registered by a module** with each setting's form, default and the measures it serves; the tab has
  one section per module with a selected measure, and a setting shows while a measure it serves is selected, marked with
  them, as a field of a shared tab is (§10). A setting is the workspace's, as the display currency is (§1), so a
  counterparty change keeps it; a case may vary it (`vary xva charge ratio 1, 1.5`). XVA's: MPOR override (days; empty is
  the model's own), cost of capital (10%, KVA's), charge ratio (1.25, CVA Charge's), PV adjustment (on; off prices new
  trades without their PV Adj) and risks included (off: risks cost a production engine a run per factor, so they are
  asked for when wanted). Advanced configuration waits for the owner: what it holds is the engine's (OPEN).
- **Risks and Profile are each one tab**, drawn for every selected measure that registers it: Risks for CVA and FVA,
  Profile for CVA, FVA and KVA. A request writes them as two shapes every measure shares: deltas per case, scenario,
  measure, risk class and factor, each Before, After and Diff; profiles per case, scenario, side, profile, series and
  date. The simulated provider bumps and reprices on the scenario's market: credit per CDS tenor (+1bp), IR per curve
  (+1bp parallel), FX per currency against JPY (+1%). A measure with no deltas says why, never a row of zeros. Profile
  shows one profile at a time, as a table and as ExChart's line chart (ADR-0001). The SIMM profile comes with MVA.

### 9. Every action is a command (ADR-0006)

`price`, `measures <list>` (§10), `refresh`, `vary`, `add trades`, `pv01 retry`, `advanced on`/`off`, `scenario …`
(§4), `download run`, `copy quote`, and one to open each input tab. A measure's own commands are its registration's.
Chords are not assigned here.

*Note of 2026-11-08 (DEFAULT, writing Delivery step 2's spec):* a cell is named `<row> <column>`. The row is a CSA's id,
the netting set's id, or a trade's id; the column is the field's title or a short name the domain registers for it,
case ignored (`threshold` is the counterparty's, `self threshold` the self's, `mta`, `curve`, `recovery`). An amount is
written as ADR-0008 writes it, a rate in `%` or `bp`. A row or a column that does not resolve, or resolves to more than
one, is refused and lists what it could mean, never guessed. `vary` takes the values after the cell, comma-separated.

**`set` changes a value without making a case** (owner's question, 2026-11-07; form DEFAULT). It names the cell as
`vary` does and takes one value: `set CSA-A threshold 5mm`, `set cds 5y 120bp`, `set NS-1 cvaRecovery 40%`. It is
the same edit a typed cell makes, in the Common layer, or in the side Advanced points at (`set after CSA-A threshold
5mm` names it). Trades take the same form: `set T-1001 margin 2bp`, `set T-1001, T-1002 csa CSA-B`, and the B and A
boxes as `set T-1001 A off`. A value the grid would refuse is refused the same way, by name (a PV Adj while Margin is
entered, §6). `vary` with a list makes cases; `set` changes the workspace. Like `entity.create` (ADR-0008), it is
one generic command in the kernel, and each domain registers the rows and fields it reaches and how a value is read.
Since 2026-11-11 it is the kernel's `entity.set` (ADR-0016 §3), and the server reads its line (ADR-0016 §5, note 15):
the rows are spelled as ADR-0016 §2 says (ids, `all`, `new`, `existing`, `where ccy=USD`), the line shows the rows and
what would change before Enter, and the page reads nothing of it. `reset` and `remove` are read the same way.

Those that change the workspace (an edit in any layer, `measures`, `vary`, `add trades`, the scenario commands) and
Price are registered as command capabilities (ADR-0005), so the AI calls them as it calls the Rates commands; XVA
registers none today. Opening a tab, Download and Copy quote are the page's own and are not the AI's.

*Note of 2026-11-11 (ADR-0016 step 3a, its notes 27 to 30):* `add trades` is typed `add each` (the owner's name,
ADR-0016 §3), here and in §3 and §6. `measures`, `vary`, `add each`, `cds`, `case remove` and `cases clear` are
command capabilities whose lines the server reads: the line says what each would do before Enter, and the page's
buttons and *Vary values…* invoke the same capabilities. `before`, `after` and `currency` are short forms of the
kernel's `set`. The command for each input tab is built by the shell from the page's tabs (`open csa`), and `go`
takes the grids and the dock. The page's own endpoints that wrote for `vary`, `cds`, `add trades`, the measures and
the cases' removal are gone: a measure's button invokes `measures` as the line does.

*Note of 2026-11-11 (ADR-0016 step 3b, its note 33):* `price`, `refresh` and `pv01 retry` are command capabilities
too (`xva.workspace.price`, `xva.refresh`, `xva.pv01.retry`), so the AI prices a workspace as the line does. `price`
and `pv01 retry` have an effect of their own, `execute`, a fifth beside those §4 lists. The Price and Refresh
buttons and a failed cell's *Ask again* invoke them, and the page's `/price`, `/refresh` and `/retry` endpoints are
gone. The line says what Price would price, and refuses what Price would refuse, before Enter. The dock shows the
workspace's latest Run, whoever priced it.

### 10. Measures register into the page (CONFIRMED, owner 2026-11-07: one page, a Price that selects the measures, each measure with its own trade scope, input tabs and results; the rest DEFAULT)

A **measure** is a figure family a Price can be asked for: CVA, FVA, MVA and KVA (XVA's, §11), spot initial margin
(SIMM's, ADR-0012) and spot CCR capital (SA-CCR's, ADR-0013). The page knows no measure's rules. The domain module
that owns a measure registers it with:

- **membership**: for each trade, whether the measure prices it and why, decided on the server. FVA's rule (§5) is the
  first. A measure may declare several named scopes, each in or out with its own reason: spot IM's are per direction
  and regime (IM-R and IM-P, each under JFSA, CFTC and the others, ADR-0012), shown compactly in its column
  (`R: JFSA, CFTC · P: JFSA`). A trade a measure cannot classify is out with its reason, never dropped silently.
  *(Note of 2026-11-08: a membership carries its scopes, each in or out with its reason, and its own reason is the
  compact form; Price freezes them with the trade, ADR-0012 §2.)*;
- **inputs**: the tabs and Settings section it needs, and the columns it adds to shared grids (XVA's CVA recovery on
  Netting; SA-CCR's counterparty inputs for the risk weight and its CSA and netting-set columns, ADR-0013). A tab,
  section or column shows while a selected measure needs it, and a hidden one keeps its changes. *(Note of
  2026-11-08: a shared field of the trade grids is one of these too, as spot IM's IM-R and IM-P are, and so is a tab
  of the measure's own, as spot IM's IM tab is, ADR-0012 §3.)*;
- **trade columns**: what it adds to the trade grids (XVA's PV01, Margin and PV Adj);
- **results**: its Metrics columns, any result tab of its own, and whether a Metrics cell opens a trace (ADR-0011).
  *(Note of 2026-11-08: a result tab is an id, a title and a view; spot IM's is `SIMM` and spot CCR capital's `CCR`,
  both drawing their trace, §7's note.)*;
- **validation**: what Price needs from it, refused by name. A measure that cannot be priced blocks only itself: a
  missing CDS blocks CVA, not spot IM;
- **options and commands** of its own.

Around that:

- **The calculations stay capabilities of their own** (C12, C27). XVA, spot SIMM and spot SA-CCR can be called from
  the command line, the AI and workflows without the page; the page composes them.
- **Price prices the selected measures.** The selection is part of the workspace, so Price freezes it, and a user's
  last selection is the default for a new workspace (DEFAULT). `measures cva fva im` sets it.
- **Which request computes a measure is the provider's, not the page's.** In production one XVA analytics request
  computes CVA, FVA, MVA and KVA, its MVA option also returns spot SIMM and its KVA option spot CCR capital; in the
  PoC SIMM also has a request of its own. One Price is still one Run, holding every request it made. Each measure's
  figures show as its request answers, so a figure computed on its own does not wait for an XVA simulation, and a
  failed request leaves only its own measures `unavailable`.
- **Shared, in a portfolio domain module** that XVA, SIMM and SA-CCR build on (DEFAULT name and place: beside the XVA
  module, never in the composition kernel): the context, netting sets, CSAs, trades with their B and A
  membership, the layers (§2), cases (§3), scenarios (§4), the Run's cases × scenarios, and the page itself. The
  kernel gains only domain-free pieces: the edit's opaque layer and case key (Consequences) and the calculation
  trace's contract (ADR-0011).

### 11. XVA's registration

What was XVA's in this ADR, gathered:

- **Measures**: CVA (with CVA Charge), FVA, MVA and KVA. MVA's spot SIMM and KVA's spot CCR capital are shown by
  SIMM's and SA-CCR's registrations (ADR-0012, ADR-0013), whichever request computes them.
- **Membership**: FVA's (§5).
- **Inputs**: the Credit tab, CVA recovery on Netting, the *Imperfect* column on CSA (§8), and the Settings section:
  MPOR override, cost of capital (KVA's, as §8's note of 2026-11-09 settles it; this line said "for CVA Charge and
  KVA" until then, and whether the engine's CVA Charge reads it too is OPEN below), charge ratio, PV adjustment, risks
  included, advanced
  configuration.
- **Trade columns**: PV01, Margin and PV Adj (§6).
- **Results**: CVA Charge in Metrics, the Risks and Profile tabs (the SIMM and capital profiles MVA and KVA return
  included, without a trace: only spot figures are drilled down), and Copy quote (§7).
- **Validation**: a missing CDS blocks CVA and CVA Charge (DEFAULT).

## Consequences

- The kernel's edit gains an optional **layer** and **case** key, opaque to it, as scope keys are (ADR-0003 §7): XVA
  declares Common, Before-only, After-only and its cases; the carry check and conflict groups apply per key. Rates is
  unaffected.
- A Run holds the selected measures for cases × scenarios, and every request they took; the Pricing DB lists one row
  per Run and opens its cases.
- The portfolio domain module of §10 is new. XVA's module moves onto it, and SIMM's and SA-CCR's are built on it.
- The fake XVA provider follows §5 (no FVA for an uncollateralised trade). The synthetic CSAs gain frequency,
  one-way, and both parties' threshold, MTA and currency; the spot CCR capital thread adds them with the trade, CSA
  and netting-set fields SA-CCR needs, so they are added once.
- The slice-02 page is replaced. The changes made to it on 2026-11-06 (one Control Height, an empty CVA recovery that
  shows empty, the latest Price on the page) carry into the new one.

### Delivery order (DEFAULT)

1. The screen of §1 with Common edits only and the registration of §10, with XVA's CVA and FVA as its first
   measures; Netting and CSA tabs; the FVA rule; New/Existing grids with PV01, Margin and PV Adj; the Metrics dock
   with Download and Copy quote. Spot IM and spot CCR capital join as ADR-0012 and ADR-0013 deliver them.
2. Cases: Vary, iterative individual addition, the changes panel.
3. Advanced: Before-only and After-only layers.
4. Scenarios.
5. Credit tab with CDS term structures; Risks and Profile.

*Owner, 2026-11-08:* steps 2, 4 and 5 are built now, in that order, and step 3 (Advanced) later. Until it lands a case's
change applies to both of its sides (§3), and the Settings tab of §8 joins step 5.

## OPEN

- What CVA Charge is computed from (charge ratio, cost of capital) is the engine's; the fake provider will show it
  as simulated until the owner says what it should approximate.
- MVA (with SIMM) is not specified here. *The Risks and Profile contents were decided by the owner on 2026-11-08*:
  Risks shows, per selected measure, its credit, interest-rate and FX deltas,
  each Before, After and Diff; Profile shows the exposure profiles EE and ENE with the capital and SIMM profiles, as a
  table and as a chart. They are built in Delivery step 5.
- XVA's advanced configuration (§8's note of 2026-11-09): what it holds is the engine's, as CVA Charge's basis is.
- The SIMM profile on the Profile tab: it comes with MVA, whose request returns it (§11).
- The case cap (DEFAULT 50).
- Whether a coarse pointer gets larger controls (ADR-0009's OPEN) applies here too.
- §2's diagonal split: ex-grid ADR-0490 (accepted 2026-11-07, AkiraMisawa/ex-grid#545) adds a Split Fill to
  ExGrid's `cellAppearance` (`lowerRightFill`), needed by Delivery step 3. Template Columns were rejected:
  a Template cell is not edited or copied as an ordinary cell (ex-grid ADR-0020).
- Whether CVA and FVA get a calculation trace too: only if the engine returns their terms (ADR-0011).
