# ADR-0015: The Rates line grid reads at laptop width: each leg in one line, and lean headers

Status: ACCEPTED 2026-11-10 by the owner. Raised by the owner the same day: on a laptop the line pricer can only be read
by scrolling sideways; "on a narrow screen, show only the trade's summary and fold each leg?" Three layouts were
prototyped on the real grid and photographed at 1280px; the owner asked whether an opened leg can open one level further
(yes, §2) and chose this one ("それじゃあ案２でいいよ"). Amends C43 (the levels of a leg's terms) and the order of the
result columns given on 2026-11-04 (the inputs first, the result after them, which still holds). The number 0015 is
taken because 0014 is held by the open pull request #17.

## Context

Measured on `main` at 4696820 with `@ex-grid/*` 0.2.0, compact density, five lines (four IRS and a CCS):

- The line grid was **4,288px** wide with both legs collapsed: the pinned line and its actions 183, Trade 460, each leg's
  basic terms 1,186, Terms 402, Result 551, Issues 320. A 1280px window shows 1,262px of it, a 1440px one 1,422px and a
  1920px desktop 1,902px. The result started 3,426px from the left. The strip above the grid (2026-11-04) shows only the
  focused line's result.
- About 40px of every column was the header's reserve for ExGrid's column-menu button and sort mark
  (`gridHeaderRequiredPx(…, { menuButton: true, sortable: true })`), so a column of three-letter values (`Ccy`, `P/R`)
  was about 100px wide. Twenty-five columns paid it.
- Folding each leg into one column alone (the owner's idea as first stated) brings the grid to 2,145px: the trade and
  both legs fit at 1280px, the result still does not.

Widths that switch the layout were considered and set aside: today's shape does not fit even 1920px, so a narrow
layout would be the layout everywhere, and a grid that changes shape when the window is resized breaks the habit the
guidelines protect (docs/design-guidelines.md §1, familiarity).

## Decision

1. **A leg is one line until it is opened.** A collapsed leg is one read-only column, `Summary`, that reads the leg as a
   trader says it: the currency and the notional in the scale it is typed in (`3bn`, `500mm`), what it pays (`Fixed
   4.10%`, `Fixed par`, `TONA +0%`, `SOFR +fair` when the spread is solved, `ATM+5bp`, `12.5 pts`), and its payment
   frequency. Leg 2 leaves out its currency and notional when they are leg 1's, as on every IRS. Typing on it is
   refused by the grid as on any read-only cell; its tooltip says how to open it, and a double click opens it.
2. **Three levels per leg.** The leg's Header Group draws ExGrid's toggle (ExGrid ADR-0400): opened, the leg shows its
   basic terms (C40) under a `Basic` group, and beside them a `Conventions` group with its own toggle. Closed,
   `Conventions` is one read-only column, `Overrides`, that names the second-level terms (C43) set on the leg, or says
   `Profile` when none is; opened, it is C43's nine columns. While a leg is open the header has one more tier (28px);
   the groups that have no level under them span both tiers (ExGrid ADR-0032's `tierSpan`). Alt+Shift+↓ and ↑ open and
   close the lowest group over the Focus's column, as ExGrid ADR-0400 gives them.
3. **The Trade group opens the trade's second terms.** Collapsed, Trade is Product, Start, End and P/R; its toggle adds
   Collateral, Break and Risk, which C40 lists and few lines set.
4. **The line grid's headers carry no column menu and no sort mark** (`hideColumnMenu`, every column `queryable: false`),
   so a column is as wide as its values or its label. The line grid gives up sorting, filtering and the column menu's
   commands: it holds the few lines a user is pricing, in the order they were made.
5. **Remove is in the line's context menu** (`Remove line`, by right click or the context-menu key), not a button beside
   Flows. The pinned part is the line and Flows.
6. **The result follows the inputs**, as on 2026-11-04: PV, its currency and `Par %` (a par rate on a fixed leg, a par
   spread on a floating one; the cell's message says which), then Status and Issues.
7. **The levels a user opened are kept for the page** while the application runs, so leaving the tab and coming back
   does not fold them again. (Since 2026-11-12 they are kept by the page's panel, with the rest of what a page shows,
   and go when the panel closes: ADR-0006 note 10.)

With five lines the grid is **1,644px** wide, and at 1280px every column up to `Par %` is in view with 27px to spare.

## Consequences

- At 1280px the line, the trade, both legs and the result read without scrolling. At 1440px Status and the start of
  Issues are in view too. With the AI pane open at 1440px the grid is 1,022px wide and the result scrolls again; the
  strip above the grid still shows the focused line's.
- Editing a leg's terms means opening it, which scrolls the result away for as long as it is open; the strip above the
  grid keeps the focused line's result in view meanwhile.
- No column of the line grid sorts or filters. If that is wanted later, the lighter alternative is ExGrid's to give (a
  header that does not reserve both bands on every column), not a menu put back on every column here.
- The browser test that types a line cell by cell opens both legs first, and the test of C43 opens Conventions.

## Alternatives

- **The legs folded, nothing else changed** (2,145px): the result still needs a scroll at 1280px.
- **This, plus the focused line's legs side by side below the grid** (a field per row, Leg 1 and Leg 2 as columns, as
  a pricing ticket shows them): every term of the line in hand without widening the grid, at the cost of a second
  place to edit the same value and the most work. Left for later; it composes with this decision.
- **The result pinned at the left**: ExGrid pins on the left only, so the result would stand before the inputs, which
  2026-11-04 decided against.
