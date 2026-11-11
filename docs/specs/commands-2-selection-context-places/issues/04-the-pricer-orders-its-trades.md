# 04: The pricer answers the order its grids show

**What to build:** `xva.trades` implements `InOrder` with the sorter and the order token its `rows` endpoint uses, for
`new` and `existing`, sorted and not. One function gives both, so a position on the page and a position in a command
are the same row.

**Pull request:** 2a

**Blocked by:** 01.

**Status:** ready-for-agent

- [ ] For each sort a test takes, `InOrder` and `Page` name the same trades at the same positions, with one token.
- [ ] A sort by a column the grid cannot sort by is refused by name.
- [ ] `set selected a off` over ranges of the Existing grid, sorted by notional descending, writes the trades at those
      positions.
