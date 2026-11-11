# 07: `find` puts a grid's Focus on a row or a cell

**What to build:** `find <row> [<field>]` on the focused grid's table: the server answers the row's position in the
view's order, and the grid places its Focus there with `placeMatch`, scrolled into view. An unknown row or field is
refused by name with its candidates; an order that moved is refused (ADR-0016 §9).

**Pull request:** 2b

**Blocked by:** 06.

**Status:** done

- [x] `find E00003 csa` puts the Focus on that cell of the Existing grid, though the row was not loaded.
- [x] A row id's end that names one row is taken; one that names several is refused with them.
- [x] The keyboard is in the grid afterwards, and the notice says where.

## Comments

- As built (ADR-0016 note 21): `find` is a **view verb**, read by the server and done by the page. The server
  declares it apart from the capabilities (`GET /api/catalog`, `viewVerbs`), the preview answers a `place` and no
  command, and Enter invokes nothing. The ADR's §3 and §4 had `find` in the client only: a row the page has not loaded
  is only the server's to find, so note 21 amends them.
- The page places with ExGrid's `placeSelection`, not `placeMatch` as this ticket and §9 said: `placeMatch` takes the
  cell as the current match of the grid's own Find panel. `placeSelection` puts one cell with the Focus on it,
  reveals it, and refuses a Row Sequence Version the grid has left (`client/src/platform/grid/lineView.ts`,
  `placeFocus`).
- The row is looked for among the rows the grid on screen shows. A row of the table that this grid does not show is
  refused with where it is (`NotOnScreen`); with no grid behind the line, `NoView`. A field left out keeps the column
  the Focus was on.
- The door: a page gives the line a screen, the view and how its grid takes a place, and the door answers that the
  keyboard moved, so the line gives none back (`CommandDoor.enter` answers `{ moved: true }`).
- The acceptance line's `E00003` is loaded with the first page. The browser test finds `0300` in a workspace of 600
  synthetic trades sorted by notional, and checks first that no answer the grid received held that row.
- From the review: a position is answered only where the view names an order, whichever reader the table has; a
  page that holds part of its rows and was answered none says so and places nothing. Where the grid refuses the
  place the sentence says which it was, a grid that has moved or one that keeps an edit open (`readEditText`), and
  no sentence of the page's names `find`.
