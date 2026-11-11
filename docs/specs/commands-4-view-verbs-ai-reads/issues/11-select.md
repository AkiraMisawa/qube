# 11: `select`, and one member for what a view verb asks

**What to build:** A view verb's reading holds one thing asked of the page, `asks`, with its `kind` (ADR-0016 note
42). `select <rows>` places the Selection of the grid behind the line on rows the selector names (note 43).

**Pull request:** 4c

**Blocked by:** the pull request before it.

**Status:** done

- [x] `LinePreview.asks` replaces `place`, `sort` and `filter`; the door and the contracts follow.
- [x] `select` is a view verb in the catalog. Its reading names the rows and, where the server pages the grid, their
      first position and count; where the page holds the rows, their keys.
- [x] Rows of another table or subset, rows the grid does not show, rows in several runs and no row are refused by
      name; with no grid behind the line it is refused.
- [x] A page's `Screen` takes a selection: the pricer's trade grids, its CSA grid and the Rates line grid.
- [x] In a browser, `select` then `set` with no rows writes the rows selected.

## Comments

- `asks` is a polymorphic member with `kind`; the client's `ViewAsk` is the same union, and the door reads the kind.
- As first built `where` with no rows before it meant rows of the whole table, as for `set`, so `select where ccy=USD`
  behind the Existing grid was refused where a new trade matched too. The review found that this refuses ADR-0016 §9's
  own chain: conditions said alone are now asked of the rows the grid shows, for `select` alone; `all`, a subset and
  ids stay rows of the table and are refused with `shown` as the way on (note 43, "Corrected in review").
- Where the page holds the rows, it says itself whether its grid shows them next to one another (`selectRows`), in
  the server's sentence but for the grid's name: the page says "the grid on screen" where the server names the grid
  (`trades (existing)`).
- Browser tests: the trade grid (paged), the CSA grid and the Rates line grid (held).
