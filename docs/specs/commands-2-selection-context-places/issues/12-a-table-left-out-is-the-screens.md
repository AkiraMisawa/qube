# 12: A table left out is the screen's table

**What to build:** With a screen behind the line, rows said as `all` or by `where` alone, with no table and no
subset, are rows of the screen's table (ADR-0016 §9: "A table left out is the focused grid's table"). A subset's word
still names the table that has that subset, and a line with no screen reads as it does today, in the main table.
What could follow offers the screen's fields after `where`. Found by the review of 2a (ADR-0016 note 18).

**Pull request:** 2b

**Blocked by:** 06.

**Status:** ready-for-agent

- [ ] On the CSA grid, `set where ccy=USD threshold 0` is the CSAs in USD, and the preview names `csas`.
- [ ] On the CSA grid, `set existing a off` is still the existing trades: `existing` is a subset of trades alone.
- [ ] With no screen (the AI, a saved sequence), `set where ccy=USD a off` is the trades, as in step 1.
- [ ] After `where`, the fields offered are the screen's table's.
