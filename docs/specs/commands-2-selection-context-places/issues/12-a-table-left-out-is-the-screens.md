# 12: A table left out is the screen's table

**What to build:** With a screen behind the line, rows said as `all` or by `where` alone, with no table and no
subset, are rows of the screen's table (ADR-0016 §9: "A table left out is the focused grid's table"). A subset's word
still names the table that has that subset, and a line with no screen reads as it does today, in the main table.
What could follow offers the screen's fields after `where`. Found by the review of 2a (ADR-0016 note 18).

**Pull request:** 2b

**Blocked by:** 06.

**Status:** done

- [x] On the CSA grid, `set where ccy=USD threshold 0` is the CSAs in USD, and the preview names `csas`.
- [x] On the CSA grid, `set existing a off` is still the existing trades: `existing` is a subset of trades alone.
- [x] With no screen (the AI, a saved sequence), `set where ccy=USD a off` is the trades, as in step 1.
- [x] After `where`, the fields offered are the screen's table's.

## Comments

- As built (ADR-0016 note 23): `Selectors.Unsaid` gives the table of rows said with none: the view's, the main table
  for a subset's word the view's table has not, and the main table with no view.
- The request the preview answers names the table where the screen gave one that is not the main (`table: csas`).
  Enter posts that request, and a command has no view: without the name it would have read the trades. The log then
  reads `set csas where ccy=USD threshold 0`.
- What could follow offers the subsets of the screen's table and of the main one, and after `where` the fields of the
  table the rows would be read in.
