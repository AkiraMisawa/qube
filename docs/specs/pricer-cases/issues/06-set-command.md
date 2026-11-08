# 06: `set` changes one value from the command line

**What to build:** `set <row> <column> <value>` makes the same edit a typed cell makes, in the Common layer, without
making a case (ADR-0010 §9). It names the cell as `vary` does and is refused by name as the grid refuses, a PV Adj while
Margin is entered included. `set T-1001, T-1002 csa CSA-B` sets each row named; `set T-1001 A off` sets a box.

**Blocked by:** 02.

**Status:** ready-for-agent

- [ ] `set CSA-A threshold 5mm` changes the workspace's value and marks it against Original.
- [ ] A value the grid would refuse is refused with the grid's reason.
- [ ] Several rows are set in one revision, or none is when one is refused.
