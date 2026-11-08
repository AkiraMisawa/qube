# The pricer workbench, delivery step 1

Status: ready-for-agent

The decisions are ADR-0010 (accepted 2026-11-07), on ADR-0003 (the server owns the rules), ADR-0005 and ADR-0006
(commands) and ADR-0009 (design guidelines). This spec orders ADR-0010's Delivery step 1 and names the seams it is
tested at. Where writing it needed a decision the ADR did not hold, the decision went into ADR-0010 first (its notes
of 2026-11-07 on §6 and §7).

## Problem Statement

The slice-02 XVA page is one toolbar and one trade grid. A desk user who changes a CSA cannot see, on one screen, the
input they changed, the trades it moved in or out of FVA, and the difference it made: the netting set and CSAs have no
grid, new and existing trades share one grid, nothing says which trades FVA prices, PV01 and the margin a quote adds
are missing, and Price opens its figures in another panel. SIMM and SA-CCR, which price the same portfolio, have no
place to join.

## Solution

One screen, top to bottom (ADR-0010 §1): a context bar with the value date, counterparty, main netting set and its
enforceability, display currency, Refresh, the measures to price and a Price that says what it will run; input tabs
Netting and CSA; New trades above Existing trades; and a result dock with Metrics, Download and Copy quote. Every edit
is Common (applies to both sides). A value that differs from Original is marked in its cell. The page knows no
measure's rules: the measures register into it on the server (§10), and XVA's CVA and FVA are the first two.

## User Stories

1. As a desk user, I want the value date, counterparty, main netting set and its enforceability always in view, so
   that I never price the wrong context.
2. As a desk user, I want to choose which measures Price runs (`measures cva fva`), so that I do not wait for figures I
   do not need.
3. As a desk user, I want Price to say what it will run (`Price CVA FVA · 1 case`, or `Price After only · CVA FVA`),
   so that I know before I press it.
4. As a desk user, I want the netting set in a Netting tab with its CVA recovery, empty staying empty, so that I can
   change the recovery and see that the CDS recovery applies when it is empty.
5. As a desk user, I want the CSAs in a CSA tab with their type, currency, discount curves, frequency, exchange, and
   both parties' threshold, MTA and currency, so that I can change a term and see its effect.
6. As a desk user, I want each CSA's Imperfect column with its reason, so that I know whether FVA prices its trades.
7. As a desk user, I want an FVA column on both trade grids with its reason (`CSA-A: daily, MTA+threshold 0.4mm
   GBP`), so that I see which trades FVA prices without asking.
8. As a desk user, I want an uncollateralised trade (`nocsa_…`) outside FVA with FVA 0, so that the figure is the
   engine's rule and not a guess.
9. As a desk user, I want new trades in their own grid above the existing ones, so that the trades I am quoting are
   never lost in a long list.
10. As a desk user, I want to set or clear B or A for every row of a grid at once, not only the rows drawn, so that a
    hundred thousand existing trades are one action.
11. As a desk user, I want a new trade's PV01 requested when it loads and shown as pending, its figure, or why it
    failed, never 0, so that I can trust the adjustment computed from it.
12. As a desk user, I want to retry a failed PV01 from its cell or with `pv01 retry`, so that one failure does not
    stop the quote.
13. As a desk user, I want to type a Margin in bps and get PV Adj as PV01 × Margin with each sign kept, PV Adj then
    read-only and saying it came from Margin, so that I cannot enter two contradicting figures.
14. As a desk user, I want to type PV Adj myself when Margin is empty, so that I can quote an adjustment I already
    have.
15. As a desk user, I want an existing trade's official PV shown from the source, so that the PV the adjustment uses
    is visible.
16. As a desk user, I want a value that differs from Original marked in its cell, including one a rule changed (a CSA
    curve carried to its trades), and not one I edited back, so that I see what is different from the sources.
17. As a desk user, I want the cell's description to give Original and the current value, so that colour or a mark is
    never the only signal.
18. As a desk user, I want each input tab to say how many changes it holds (`CSA ●2`), so that I find my changes.
19. As a desk user, I want Price to freeze everything it priced, including the measures, the FVA portfolio with its
    reasons and rule version, and each PV Adj with the PV01 and Margin it came from, so that a later PV01 or rule
    change never alters a Run.
20. As a desk user, I want a measure that cannot be priced to block only itself, refused by name, so that one missing
    input does not stop every figure.
21. As a desk user, I want the Metrics dock to show, per selected measure, Before, After and Diff in the display
    currency, a failed measure as `unavailable` and never 0, so that I read the effect of my change on the same
    screen.
22. As a desk user, I want each measure's trade counts in the dock (`FVA 300 → 301`), so that I see that a trade moved
    into FVA.
23. As a desk user, I want a Price with every B box clear to price After alone, with no Before column or Diff and the
    dock saying `After only: Before was not priced`, so that I am not shown a comparison with nothing.
24. As a desk user, I want a result older than the workspace to say so in yellow (`revision 5 · workspace now 7`), so
    that I never read stale figures as current.
25. As a desk user, I want the dock to collapse to one line that shows CVA Diff and FVA Diff, so that the trades get the
    height while I keep the headline.
26. As a desk user, I want Download to save the Run as a zip of its frozen inputs, results, lineage and diagnostics,
    from the dock, the Pricing DB and `download run`, so that a price can be reproduced and audited.
27. As a desk user, I want Copy quote to put the Run's header and each case's CVA Charge and PV on the clipboard as
    HTML and plain text, so that I can send sales a quote in one step.
28. As a desk user, I want every action above to be a command too (`price`, `measures`, `refresh`, `pv01 retry`,
    `download run`, `copy quote`, and one to open each input tab), so that the keyboard and the AI reach them.
29. As a phone user, I want the screen as three tabs (Inputs, Trades, Results) under the context bar below 768px, so
    that it works on a phone.
30. As the SIMM and SA-CCR modules, I want to register a measure with its membership, inputs, trade columns, results
    and validation, so that I join the page without the page knowing my rules.

## Implementation Decisions

- **A portfolio domain module** (ADR-0010 §10), beside the XVA module and outside the composition kernel, holds what
  XVA, SIMM and SA-CCR share: the synthetic context (registration, agreement, netting set with its enforceability),
  CSAs with their terms, trades with their start, maturity, official PV (existing) and B/A membership, the edit rules
  and scopes, the effective rows, the trade pages and the workspace service. The XVA module moves onto it and keeps
  only what is XVA's (§11). The wire paths and the stored app id stay as they are, so a workspace saved before this
  slice still opens; a renamed entity (the CVA recovery moves from the counterparty's credit to the netting set, as
  §8 places it) is migrated by the module, not dropped.
- **The measures register** is the module's contract: a measure has an id, a title, its owner module, a membership
  rule (per trade: in or out with a reason, optionally per named scope), the input tabs, sections and columns it
  needs, the trade columns it adds, its Metrics columns and whether a cell opens a trace, a validation that returns
  refusals naming the measure, and its own options and commands. The page reads the register from the workspace view
  and never decides membership or validity.
- **The selection of measures is workspace state**: an ordinary edit, frozen at Price. A new workspace takes its
  owner's last selection; the very first takes every registered measure.
- **CSA terms** (ADR-0010 §5, §8): type, currency, discount curve, FVA discount curve, frequency, exchange, one-way, and
  for the counterparty and for self a threshold, MTA and currency. Every term is editable through the same edit as a
  trade field and validated on the server. FX for the 1mm GBP test comes from the source version's synthetic FX at
  the value date, so another value date may move a CSA in or out.
- **FVA's membership** is XVA's rule on the server, once: imperfect when one-way, less frequent than daily, or either
  party's MTA + threshold above 1mm GBP (configured). An uncollateralised trade is out. The rule's id and threshold
  are frozen into the Run.
- **Values that differ from Original** are computed on the server per row and per input entity, as the list of fields
  whose effective value is not Original's (the source under the rules with no edit), with Original's value. B and A
  membership is not in it (§2).
- **Two trade pages**: the rows endpoint pages new trades and existing trades separately, each sorted on the server.
  Setting B or A for every row of a grid is one edit on the grid (not one per row): a row's value is its own edit when
  that edit is newer than the grid's, else the grid's, else the source's. ExGrid has no header action, so until it
  has one the all/none controls sit on the grid's caption (ADR-0010 §6 note of 2026-11-07).
- **PV01** is a result, not an input: a separate capability asked for each new trade when its terms are first seen,
  answered asynchronously, kept per source version and trade terms, and shown as `pending`, a figure, or the failure.
  `pv01 retry` asks again for the failed ones. Margin (bps) and PV Adj are inputs; PV Adj is PV01 × Margin when Margin
  is entered and read-only then; with Margin entered and PV01 not available, XVA's measures are refused by name.
- **Price** freezes the selected measures, each measure's membership with reasons, and each side's trades. A side with
  no trade is not priced (never priced as 0); both empty is refused. Validation runs per measure: a refused measure
  is listed with its reason and the others are priced. The fake XVA provider prices CVA over every trade of a side,
  FVA over FVA's portfolio only (an uncollateralised trade is 0 and outside it), CVA Charge as CVA times the simulated
  charge ratio, and PV as the official PVs plus the PV Adjs, all in the display currency at the synthetic FX, and says
  `simulated`. The result dataset is one row per case and measure: Before, After, Diff, the trade counts, and a status
  (`priced`, `notPriced`, `unavailable`, `refused`) with its reason.
- **Download** is a Run's action, generic, in the kernel's HTTP surface: a zip of the Run record (lineage and
  provenance), every input and output dataset it retained, and its diagnostics. The page, the Pricing DB and
  `download run` call the same endpoint.
- **Copy quote** is XVA's: the client builds it from the Run's retained result alone (never from the screen): a header
  (Run, counterparty, netting set, value date, revision, display currency, simulated) and per case CVA Charge Diff and
  PV Diff, or the priced side's figure and its name for a one-sided case, written as HTML and as plain text.
- **The page** replaces the slice-02 page and keeps what was fixed on it on 2026-11-06 (one Control Height, an empty
  CVA recovery that shows empty, the latest Price on the page). It follows docs/design-guidelines.md. Commands are
  registered by the page while it is mounted, as Price is today.

## Testing Decisions

A good test drives a public seam and asserts what a user or a caller sees: figures, refusals by name, membership with
its reason, what a Run froze. It never asserts how the module stores a value. Expected figures are hand-computed from
the synthetic sources. Each test names the ADR section it holds, as the existing tests do.

The seams (the owner took the recommendation, 2026-11-07):

1. **The portfolio workspace service with the registered measures** (xUnit, prior art `XvaTests`): effective rows,
   CSA terms, FVA membership and reasons, Original differences, the grid-wide B/A edit, PV01/Margin/PV Adj, per-measure
   validation and the frozen accepted input. Catches the rules; misses the wire shape.
2. **The HTTP API through the in-process host** (xUnit with `WorkbenchFactory`, prior art `XvaApiTests`): the two
   trade pages, edits and refusals with their status codes, Price to a finished Run with its result dataset, and the
   Run's zip. Catches the wire contract and the provider end to end; misses the screen.
3. **The client's pure functions** (Vitest, prior art `editing.test.ts`): reading Metrics rows from a result dataset
   (`not priced`, `unavailable`, Diff), the Price label, the Copy quote text and HTML, and the command arguments.
   Catches what the page derives; misses layout.
4. **The page in a browser** (Playwright, prior art `xva.spec.ts`), driven by keyboard and read once settled: one
   flow from a new workspace through a CSA edit that moves a trade into FVA, the mark on the changed cells, Price,
   the Metrics and trade counts, Copy quote, Download, and the phone layout. Catches what only a browser shows.

## Out of Scope

- Cases (vary, iterative addition, the changes panel), Advanced and side-only layers, scenarios, the Credit tab, the
  Settings tab, Risks and Profile: ADR-0010 Delivery steps 2 to 5.
- The diagonal Split Fill: every change in this step differs on both sides, so a changed cell wears ExGrid's
  `modified` mark (ADR-0010 §2) until the ExGrid release with ex-grid ADR-0490.
- Adding trades by ID or XML, a dummy counterparty with its project name, and the `set` command (ADR-0010 §9), which
  follow once this screen stands.
- Spot IM and spot CCR capital on the page: they register on this module as ADR-0012 and ADR-0013 deliver them.
- A real engine, real market data, or a real PV01 service: every provider here is simulated and says so.

## Further Notes

- The SA-CCR thread builds its fields (exposure-class inputs, independent amounts, collateral balances, trade legs) on
  this module once it is pushed, reusing the trade's start and maturity.
