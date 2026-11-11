# 05: The line reads the page's view, shows it, and sends it

**What to build:** `CommandDoor.view()`. The pricer's page answers the view of the trade grid the keyboard was last
in: its sort and order token as its reader last answered, its selection and Focus from `readView()`. The line reads it
once as it opens, asks the preview with no text and shows the sentence under the input, sends the view with each
preview and with Enter's own reading, and marks an argument that came from context (notes 16 and 19).

**Pull request:** 2a

**Blocked by:** 02, 03, 04.

**Status:** done

- [x] Three trades selected with the keyboard, then `set csa CSA-B`: the line shows the three rows and what would
      change, and Enter writes them.
- [x] `set selected where ccy=USD a off` changes the USD trades among the selected only.
- [x] Every row of the Existing grid selected with one key: the count is the grid's, and Enter writes them all.
- [x] `reset` on a focused cell that has an edit takes the edit back.
- [x] The sentence under the line names the grid, the count and the cell; with no grid focused there is none.
- [x] Looked at in a browser at 1440, 992, 768 and 390 px in both schemes.
- [x] Measured: the preview of `set selected a off` with 10,000 rows selected.

## Comments

- As built: `CommandDoor.view()` and `enter(line, shown?, view?)`; the preview's request takes `view`
  (`api.preview(line, workspaceId, { revision?, view? })`). The line reads the view once in its opening, asks the
  preview with no text for the sentence and shows it as `On screen: …`, and sends the view with each reading.
- The view is made by two pure functions (`client/src/platform/grid/lineView.ts`): `pagedView` for a grid the server
  pages and `heldView` for one whose page holds every row. The pricer's page keeps, for each row its reader answered,
  that answer's sort and token, so the order sent with positions is the one the rows on screen are in (note 16).
- The pricer's CSA grid is a screen too, by key. A grid of no table (the scenarios' shifts, a result, a trace) gives
  none, and before any grid has had the keyboard there is none.
- The mark of an argument from context is words before the value (`rows from the screen: DRAFT-1, DRAFT-2, DRAFT-3`):
  at 390 px a badge of many rows is cut at its end.
- Measured (10,009 trades, all selected, Release, SQLite): `set selected a off` 0.08 to 0.19 s; ADR-0016 note 17
  has the rest. The pricer's page reader and its table now answer one order kept per state (`TradeOrder.Ordered`).
- Looked at in Chromium at 1440, 992, 768 and 390 px in both schemes, by a script and by eye: nothing scrolls
  sideways, and the sentence wraps to two lines at 390 px.
