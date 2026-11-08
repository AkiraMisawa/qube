# Spot IM on the pricer page

Status: done

The decisions are ADR-0012 (spot initial margin is SIMM's measure, per IM CSA and regime), with its notes of
2026-11-08 for delivery step 2, and ADR-0010 §7 and §10 (the shared result and the measures register). This spec only
orders the work and decides nothing they do not. It is ADR-0012's delivery step 2. Step 1, the engine, CRIF and the
capabilities, is `docs/specs/spot-simm/`.

## In this slice

- The portfolio's IM CSAs and each trade's IM-R and IM-P, from the synthetic source, editable as assignments
  (ADR-0012 §2). Ticket 01.
- Spot IM registered into the page: the measure and its metrics, membership with named scopes, its inputs (the IM-R
  and IM-P columns, the IM tab) and its validation (ADR-0012 §2, §3, §7). Ticket 02.
- SIMM's own request over the accepted input, Before and After on their own: CRIF from the portfolio's trades, the
  engine, IM-R and IM-P in the display currency with the governing regime, a trace per side, and the CRIF retained
  (ADR-0012 §4 note, §7). Ticket 03.
- The page: the columns, the scope column, the IM tab, Metrics with the governing regime and `SIMM PARAMETERS DEMO`,
  and a Metrics cell that opens the trace at its direction (ADR-0012 §3, §7; ADR-0010 §7). Ticket 04.

## Out of this slice

- The SIMM tab: the two directions' summaries and the drill-down in the layout the owner picks (delivery step 3).
  Until it lands, a Metrics cell opens the trace view that spot CCR capital already uses.
- The Pricing DB's projection for spot IM (ADR-0012 OPEN).
- MVA, and the production adapter that takes spot SIMM from the XVA request's MVA option (ADR-0012 §4, §8).
- IM CSAs as a what-if (ADR-0012 OPEN). The IM tab is read-only.

## Seams (agreed with the owner, 2026-11-08: "go with your recommendations")

1. **The register, in process**, as the spot capital tests do: a portfolio workspace with spot IM selected, where
   membership and its scopes, the IM-R and IM-P defaults and edits, Original, and refusals by name are read, and a
   Price gives a Run with spot IM's rows, its traces and its CRIF.
2. **Over HTTP**, as the pricer API tests do: the measure, the inputs and the IM CSAs in the workspace view, an IM-R
   edit, a Price, and the trace endpoint opening `collect`.
3. **The client**: Vitest for what the page derives (the Metrics with notes and basis, the term a cell opens, the
   scope text), and a Playwright spec for the page itself.

The figures are the engine's, which step 1 holds to hand-worked cases. These tests hold the figures to their own
consistency instead: the IM is its governing regime's SIMM, the display amount is the trace's USD amount at the frozen
FX, a side's figure moves only with that side's trades, and every term reconciles.

## Done when

- A trade's IM-R and IM-P come from the source, or for a new trade from the counterparty's IM CSAs, and an edit to
  one is marked against Original and carried as a CSA assignment is.
- Every trade shows its scopes or why it is out, and a trade naming an IM CSA that is not the counterparty's refuses
  spot IM alone, by name.
- A Price with spot IM selected gives IM-R and IM-P for each priced side, named with the governing regime and labelled
  DEMO. A side or direction with nothing in scope has no figure, and a trade in scope without CRIF makes its side
  `unavailable`.
- The Run resolves the CRIF and the trace of each priced side. The trace opens from a Metrics cell at its direction,
  and After alone works as both sides do.
- The page shows the columns and the IM tab only while spot IM is selected, at phone, tablet and desktop widths, in
  light and dark.
