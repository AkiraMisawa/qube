# 08: One screen on every width

**What to build:** the layout of ADR-0010 §1 finished: the context bar (value date, counterparty searched by code,
entity and name, main netting set with enforceability, display currency, Refresh, measures, Price) with its status
line (source version, revision, `SIMULATED`); the input tabs collapsible to their tab row with a drag handle for
their height; the result dock collapsible to one line with each measure's headline Diff, and drag-resizable; a result
older than the workspace in yellow; and below 768px three tabs, Inputs, Trades and Results. Commands open each input
tab. One browser test drives the whole flow by keyboard.

**Blocked by:** 03, 05, 06, 07.

**Status:** done

- [x] The browser flow passes: new workspace, a CSA edit that moves a trade into FVA, the marked cells, Price, the
      Metrics and counts, Copy quote, Download.
- [x] At 390px there is no sideways scroll and the three tabs reach every part.
- [x] Light and dark themes both keep the text's contrast.

*Done 2026-11-08.* The context bar has the value date, the counterparty searched as `code | entity | name` (the flag
waits for the dummy counterparty), the main netting set with its enforceability (yellow when not enforceable), the
display currency (a workspace setting, frozen into the Run, which the fake engine converts its JPY figures to),
Refresh (the trades acquired again for the same value date, edits carried or held), the measures and Price; under it
the source version, revision and `SIMULATED` while a selected module's provider is. The input tabs collapse to their
row, fit the open tab's content until their handle is dragged, and the handle is a separator that also takes the
arrow keys, Home and End. The dock stays at the window's bottom, collapses to a line a case with each measure's
headline Diff (or its one side), and has a handle of its own. The Existing grid has the window's rest, from the
heights the page set, never measured. Below 768px the page is three tabs. `open netting` and `open csa` open the tab
and put the keyboard in it; `refresh` and `currency <ccy>` are commands. The browser tests: the whole flow by
keyboard, the parts' heights and collapse, 390px, and every text at 4.5:1 in both themes at 1440 and 390px.

Two defects found on the way and fixed. The light theme failed WCAG 1.4.3 across the page (field labels at 3.3:1,
yellow badges at 2.7:1) and the dark theme's Price label at 2.1:1: the theme now takes `autoContrast` and darkens
coloured and dimmed text in the light scheme (`theme.ts`, docs/design-guidelines.md §7). And Price, a value date, a
counterparty or Refresh sent while an edit was on its way was refused with 409 for the revision its own page moved:
each now runs after the edits sent before it (`EditQueue.after`, spine 6), on Rates' Price and valuation date too.
