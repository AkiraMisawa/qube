# 04 The SIMM tab

Status: done

ADR-0012 §7 ("the two directions' summaries, then the drill-down"). Read `docs/design-guidelines.md` first.

- **One card per direction**: `IM-R · collect` and `IM-P · post`. Each card shows its IM CSA, the governing regime,
  and Before, After and Diff in the calculation currency, with the FX said once. Choosing a card roots the drill-down
  at that direction. The keyboard reaches both cards and the tree.
- **The drill-down** is the trace view of ticket 03, in the dock. Opening the SIMM tab widens the dock over the
  trade grids, and a button or command puts it back to the height its handle set (CONFIRMED, owner 2026-11-08: "widen
  the dock to the whole screen").
- **On a phone** the cards stack, and the drill-down drills in one level at a time.
- **After only**: each card shows After alone, and the drill-down works in full.

A Playwright spec covers the tab at 1440, 992, 768 and 390px, in both themes. It opens the tab from Metrics and by
command, switches cards, drills to a risk factor, reads its trades and CRIF lines, copies the `explain` line and runs
it, and repeats with Before not priced.
