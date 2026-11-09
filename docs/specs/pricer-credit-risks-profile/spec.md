# The pricer workbench, delivery step 5: Credit and Settings tabs, Risks and Profile

Status: done

The decisions are ADR-0010 §7 (the result dock), §8 (the input tabs' contents), §10 (what a measure registers), §11
(XVA's registration) and its OPEN (the Risks and Profile contents, decided by the owner on 2026-11-08), with §8's note of
2026-11-09 written for this spec; on ADR-0003 (the server owns the rules), ADR-0005 (commands), ADR-0001 (the ExGrid
family draws grids and charts) and ADR-0011 (only spot figures are traced). The owner asked for steps 2, 4 and 5 on
2026-11-08; steps 2 and 4 are on main (PRs #13, #14), and step 3 (Advanced) comes later.

## Problem Statement

The page prices CVA on a credit nobody chose: every counterparty's default probability is a constant of the fake
provider, and the CDS recovery a constant of the source, so the first input of a quote, the counterparty's CDS curve, has
nowhere to be read, chosen, or edited, and a counterparty with no CDS is priced as if it had one. The model settings a
desk user sets before a quote (cost of capital, charge ratio, MPOR override, whether new trades' PV adjustment is
priced, whether risks are asked) are constants too. And what the desk reads after a Price besides the Metrics, the
deltas of each measure and the exposure and capital profiles, is not on the page at all, though the provider returns a
capital profile already.

## Solution

- **Credit** (XVA's input tab, §8): the counterparty's CDS, a single name found by searching `ticker | ccy | seniority |
  doc clause | name`, or a proxy by sector, region and rating, with one grid row of the curve: type, ticker, currency,
  seniority, doc clause, CDS recovery and the spread at each tenor in bp, the spreads editable and marked where they
  differ from the catalogue's. A counterparty with no CDS refuses CVA, and CVA Charge with it, by name. The CDS spreads
  join the scenario catalogue as Credit shifts, and `set cds 5y 120bp` and `vary cds 5y …` reach them.
- **Settings** (shared, §8): one section per module whose measure is selected; XVA's holds MPOR override, cost of
  capital, charge ratio, PV adjustment and risks included, typed and checked on the server, frozen by Price, and read by
  the provider.
- **Risks** (XVA's result tab, owner 2026-11-08): per selected measure, its credit, IR and FX deltas, each Before, After
  and Diff, for the case and the market in view, when the Run asked for risks.
- **Profile** (XVA's result tab, owner 2026-11-08): the exposure profiles EE and ENE and the capital profile (EAD and
  capital), as a table and as a chart, for the case and the market in view.

## User Stories

1. As a desk user, I want the Credit tab to show the counterparty's CDS curve, with its type, reference, recovery and
   the spread at each tenor in bp, so that I see the credit CVA is priced on.
2. As a desk user, I want to find a single-name CDS by typing any of its ticker, currency, seniority, doc clause or
   name, so that I choose it without knowing its key.
3. As a desk user, I want to choose a proxy by sector, region and rating when the counterparty has no single name, so
   that an unrated corporate is still priced on a curve.
4. As a desk user, I want to edit a spread in the grid, in bp, and see the edited cell marked with the catalogue's
   value, so that a what-if on the credit is one typed cell.
5. As a desk user, I want an edit of a curve to stay with that curve, so that choosing another CDS and coming back
   neither loses it nor applies it to the other curve.
6. As a desk user, I want CVA and CVA Charge refused by name while no CDS is chosen, or a proxy lacks a key, and FVA,
   KVA, spot IM and spot capital priced as before, so that a missing CDS blocks only what reads it (§10).
7. As a desk user, I want an empty CVA recovery to take the chosen curve's CDS recovery, and to say which, so that the
   recovery CVA reads is never a constant nobody chose (XV15).
8. As a desk user, I want `cds abch usd snrfor`, `cds proxy industrials asia bbb` and `cds none` on the command line,
   and the AI to reach them, so that the keyboard and the AI choose a CDS as the tab does (§9).
9. As a desk user, I want `set cds 5y 120bp` and `vary cds 5y 100bp, 140bp`, so that a spread is set or varied like
   any other cell (§9).
10. As a desk user, I want a Credit shift, `cds +50bp` or `cds 5y +20bp`, in a scenario, so that a credit stress is
    priced beside the base market like a rate stress (§4).
11. As a desk user, I want the Settings tab to show one section per module whose measure is selected, each setting
    marked with the measures it serves and shown only while one of them is selected, so that I see what each figure
    is computed with.
12. As a desk user, I want to set XVA's MPOR override, cost of capital, charge ratio, PV adjustment and risks included,
    each checked on the server and marked where it differs from its default, so that a setting is an input like any
    other (§8).
13. As a desk user, I want `set xva cost of capital 12%` and `vary xva charge ratio 1, 1.5`, so that a setting is set
    or varied from the command line.
14. As a desk user, I want the Risks tab to show, per selected measure, its credit, IR and FX deltas, each Before,
    After and Diff, for the case and the market in view, so that I read what moves each figure.
15. As a desk user, I want the Risks tab to say why a measure has no deltas (risks not asked, the measure refused, the
    provider computes none), so that an empty table is never read as zero.
16. As a desk user, I want the Profile tab to show the exposure profile EE and ENE, and the capital profile EAD and
    capital, Before and After, as a table and as a chart, for the case and the market in view, so that I see how the
    exposure runs off.
17. As a desk user, I want Risks and Profile drawn from the Run's retained results, never from the screen, and to stay
    with the Run in view, so that a figure is always the one priced (§7).
18. As a phone user, I want the Credit and Settings tabs and the Risks and Profile tabs to fit the screen, so that I
    can check a quote away from the desk.

## Implementation Decisions

- **The CDS catalogue is the portfolio's, synthetic** (§8's note of 2026-11-09): single names keyed by ticker,
  currency, seniority and doc clause, each with a name, a recovery and a spread at each CDS tenor (`6m 1y 2y 3y 5y 7y
  10y`); proxies, one curve per sector, region and rating. The source names each counterparty's CDS.
- **The counterparty's CDS is a choice on `credit:<registration>`** (`cdsType`, `cdsReference`, `proxySector`,
  `proxyRegion`, `proxyRating`); a curve's spreads are edits on `cds:<curve id>`, one field a tenor. The effective
  credit in the workspace view carries the chosen curve with its spreads, Original and ownership, or why there is none.
- **XVA's input** carries the curve priced: its id, type, recovery and spreads. Its validation refuses CVA when the
  curve is missing (`NoCds`). The fake provider integrates a toy EE profile against the curve's default probabilities.
- **A module registers its settings** (id, title, form, default, the measures it serves), as it registers trade
  columns; the register gives the workspace view each selected module's settings with their values and Original.
  Values are edits on `settings:<module>`, in the workspace's scope. A module reads its own in its input part.
- **Cells** gain the row `cds` (the chosen curve, its columns the tenors), the row `credit` (the CDS choice) and a row
  per module with settings (its columns the settings); `set` and `vary` reach them, and the Changes tab names them.
- **The scenario catalogue** gains the Credit category: the index `cds`, the counterparty's chosen curve, at a CDS tenor
  or whole, absolute in bp, key-rate on the CDS tenors. Tenors are the category's.
- **Risks** is the contract `portfolio.risks`, one row per case, scenario, measure, risk class and factor, Before,
  After and Diff with each side's status; a row with no factor says why a measure has no deltas. The fake provider
  bumps and reprices on the scenario's market.
- **Profile** is the contract `portfolio.profiles`, one row per case, scenario, side, profile, series and date, replacing
  `xva.capital-profile`. Exposure is the toy EE and ENE CVA integrates; capital is spot CCR capital's run-off.
- **The dock** draws a tab several measures register once, for every one of them; the views `risks` and `profile`
  read their contract's rows from the Run's invocations, as the trace view reads its traces. The chart is ExChart
  (`@ex-grid/chart-react` with `@ex-grid/chart-mantine`'s Chrome), at the version of the ExGrid packages the client pins.
- **Commands**: `cds …`, `open credit`, `open settings`; `set`/`vary` on the new rows; the AI reaches `xva.cds` and
  reaches settings and spreads through `xva.set` and `xva.vary`.

## Testing Decisions

A good test drives a public seam and asserts what a user or a caller sees: the curve shown and priced, a measure refused
by name, the settings a Run froze and what they moved, the deltas' sign and the profile's shape. It never asserts how a
choice is stored. Expected figures are read from the synthetic sources: a wider spread makes CVA more negative, a higher
cost of capital makes KVA more negative, a delta is the difference of two priced figures, and EE at the value date is
the sum of the positive PVs held. Each test names the ADR section it holds.

The seams are the four of steps 1, 2 and 4 (DEFAULT, per the owner's standing preference of 2026-11-08):

1. **The portfolio workspace service with the registered measures** (xUnit, prior art `PricerCaseTests`,
   `PricerScenarioTests`).
2. **The HTTP API through the in-process host** (xUnit with `WorkbenchFactory`, prior art `PricerApiTests`,
   `PricerCommandTests`).
3. **The client's pure functions** (Vitest, prior art `metrics.test.ts`, `dock.test.ts`): the risks and profile tables.
4. **The page in a browser** (Playwright, prior art `xva.spec.ts`): a CDS chosen and a spread edited, a setting set,
   the Risks and Profile tabs after a Price, and the phone layout.

## Out of Scope

- Step 3 (Before-only and After-only layers).
- XVA's advanced configuration: what it holds is the engine's (OPEN, as CVA Charge's basis is).
- MVA and its SIMM profile (ADR-0010 OPEN): the Profile tab draws the profiles a Run returns, and no request returns a
  SIMM profile yet.
- IR deltas per tenor, cross-gammas (scenarios do those), and deltas of KVA, spot IM and spot capital.
- A real CDS feed and a real engine: everything here is synthetic and simulated, and says so.

## Further Notes

- The toy's numbers are chosen so that each input visibly moves the figure that reads it; they approximate nothing,
  and every figure is labelled simulated.
