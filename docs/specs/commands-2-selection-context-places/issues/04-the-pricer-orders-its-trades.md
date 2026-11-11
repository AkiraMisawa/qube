# 04: The pricer answers the order its grids show

**What to build:** `xva.trades` implements `InOrder` with the sorter and the order token its `rows` endpoint uses, for
`new` and `existing`, sorted and not. One function gives both, so a position on the page and a position in a command
are the same row.

**Pull request:** 2a

**Blocked by:** 01.

**Status:** done

- [x] For each sort a test takes, `InOrder` and `Page` name the same trades at the same positions, with one token.
- [x] A sort by a column the grid cannot sort by is refused by name.
- [x] `set selected a off` over ranges of the Existing grid, sorted by notional descending, writes the trades at those
      positions.

## Comments

- As built: `TradeOrder` (the pricer's) holds the sortable columns, a grid's rows, the stable sort and the token;
  `PortfolioWorkspaces.Page` and the trades table's `InOrder` both call it. `PricerTableTests` holds the two to one
  order and one token for six sorts over `existing`, `new` and both.
- A sort a view names that the grid cannot sort by is the page's fault (`InvalidView`), not a reading.
- The CSA table answers to `csas` only: `csa` alone is the trade's field, so `set csa CSA-B` acts on selected trades.
- A word a table of one row has (`ccy`, the display currency) stays that table's where no command could set the
  screen's field of that word (a trade's currency); where one could (a CSA's currency), the line is refused with both.
