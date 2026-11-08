# The SIMM tab

Status: done

The decisions are ADR-0012 §6 and §7 (the drill-down and the SIMM tab), ADR-0011 §6 (one view draws every trace) and
ADR-0010 §7 and §10 (the result dock's tabs and the measures register). This spec orders the work and states its DEFAULTs,
told to the owner on 2026-11-08, who asked that DEFAULTs be used without asking again; they are recorded in the ADRs'
notes of that date. It is ADR-0012's delivery step 3. Step 2, spot IM on the page, is `docs/specs/spot-simm-page/`.

The owner's sample of 2026-11-07 (https://claude.ai/artifact/S1P64xEfAW4WNonthA6Hf8) is the target. The generic trace
view of ADR-0011 already draws most of it: the tree of every level beside the typeset formula, drilling in on a phone,
After alone, and the CRIF lines below a term. This slice adds what the sample has and the view lacks, and it puts the
view in the dock.

## In this slice

- **Result tabs.** A measure registers result tabs beside Metrics (ADR-0010 §7, §10), and spot IM registers `SIMM`. A
  Metrics cell of a measure with a tab opens that tab at its term. Spot CCR capital registers `CCR` the same way
  (owner, 2026-11-08: the drill-down view is the one the spot CCR thread built, so both get their tab now). A Run opened from the Pricing DB keeps the
  separate trace panel. Ticket 01.
- **Trades under a term.** The trade view sums each trade's CRIF-line contributions per side, with the Diff. It is
  generic in the kernel: a leaf set names its entity and its contribution field, and an `entities` endpoint beside the
  leaves endpoint sums the rows below a term by entity. Ticket 02.
- **The trace view, where the sample differs.** Ticket 03:
  - contributions on each side, with their Diff;
  - a governing mark on the operand that governed;
  - the selected term's `explain` line, with a copy button, for the case the view shows;
  - the trades and the CRIF lines at every term with an allocation;
  - the term's own parameters first, with those of the terms above it folded, and no sideways scroll at any width;
  - spot IM's labels: `IM-R · collect` and `JFSA regime`.
- **The SIMM tab.** It shows one card per direction: its IM CSA, the governing regime, and Before, After and Diff in
  the calculation currency, which the trace's header says, with the rate the Metrics' figures were converted at said once
  above the cards. Choosing a card roots the drill-down at that direction. Opening
  the tab widens the dock over the trade grids, and a button or command puts it back to its own height (CONFIRMED,
  owner 2026-11-08). The layout is the confirmed one: on a phone,
  drilling in; After alone works in full. Ticket 04.

## Out of this slice

- XVA's Risks and Profile tabs (ADR-0010 delivery step 5).
- Vega, curvature and base correlation terms (ADR-0012 §6's amendment: they are registered with the first product
  with options).
- The Pricing DB's projection for spot IM (ADR-0012 OPEN).

## Seams (DEFAULT, told to the owner 2026-11-08)

1. **The register, in process and over HTTP**: spot IM's measure declares the `SIMM` result tab, and the pricer API
   lists it.
2. **The kernel's trace reader, in process and over HTTP**: the leaf rows below a term, summed by entity, give each
   entity's contribution per side and the Diff. A synthetic non-financial trace holds the kernel to it, as ADR-0011's
   Consequences ask, and spot IM's trace holds it over HTTP: at any term, a trade's contribution is the sum of its
   lines', and the trades add up to the term's own contribution.
3. **The client**: Vitest covers what the page derives (the dock's tabs, the cards from the trace's roots, the
   `explain` line, the parameters split). A Playwright spec covers the tab itself.

## Done when

- With spot IM selected and priced, the dock has `Metrics` and `SIMM`, and an IM-R or IM-P cell opens `SIMM` at its
  direction.
- The SIMM tab shows both directions' cards. A card roots the tree, and every level opens to its formula, its
  contributions on each side, its trades and its CRIF lines.
- The trades under a term add up to the term's contribution on each side, largest first.
- The selected term's `explain` line opens the same term from the command line.
- After alone draws no Before and no Diff, and still drills to every level.
- At 1440, 992, 768 and 390px, in light and dark, every text reads at 4.5:1, and the page never scrolls sideways.
