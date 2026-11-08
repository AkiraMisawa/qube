# 04 Spot IM on the page

Status: done

ADR-0012 §3 and §7 with their notes, and ADR-0010 §7. Read `docs/design-guidelines.md` first.

While spot IM is selected, the page shows the following:

- **The trade grids**: the IM-R and IM-P columns on both grids, editable, and its scope column with each scope's
  reason in the cell's message.
- **The IM tab**: read-only, with a sentence where the counterparty has no IM CSA, and opened by `open im`.
- **Metrics**: IM-R and IM-P, with each side's governing regime beside its figure and `SIMM PARAMETERS DEMO` with the
  measure's figures. An IM-R cell opens the trace at `collect`, and an IM-P cell at `post`.

A Playwright spec covers the page at desktop and phone widths, in both themes: select spot IM, see the columns and the
tab, edit an IM-R, Price, read Metrics, and open the trace from a cell. A Price without Before is covered too.
