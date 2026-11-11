# 13: A sorted order's token moves only when the order does

**What to build:** The pricer's token of a sorted trade grid is its source's version and a digest of the row keys in
that order, in place of the version and the revision. An edit that moves no row, adds none and removes none leaves
the token, so the grid keeps its sequence and its selection, and a second command on the same selection follows the
first with nothing selected again (ADR-0016 note 26, which amends note 17; step 2a's first known limit).

**Pull request:** 2c

**Blocked by:** 04.

**Status:** done

- [x] Sorted by notional, `set selected a off` then `set selected a on` in a chain write the same rows.
- [x] An edit that moves a row in the order moves the token, and the grid drops its selection as before.
- [x] The digest is of the keys in order, so a row added or removed moves it. The sample has no command that adds
      or removes a row of a grid, so no test holds this part.

## Comments

- As built (ADR-0016 note 26): `TradeOrder.Token` in the pricer's sample, the first eight bytes of the SHA-256 of
  the keys in order, kept for the workspace state it was taken of. The page reader and `InOrder` answer one token.
- The second criterion's first half is held by a server test (an order by After, after `a off` on its first rows,
  is `ViewMoved`). That the grid then drops its selection is ExGrid's, and was seen in a browser, not tested here.
- Measured at 10,009 rows sorted by notional (`IMPLEMENTATION_STATUS.md`): the first page of a state 0.3 to 0.36 s,
  later pages 9 ms, and the token the same after each of two commands on every row.
