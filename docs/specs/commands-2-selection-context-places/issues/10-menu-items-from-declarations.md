# 10: A table declares its menu items

**What to build:** A table's description lists its actions, each a title and the line it runs. A grid bound to the
table shows them in its Context Menu, runs each through the door with the menu's selection and cell as the view, and
shows the line that does the same. The kernel gives every table `Reset this cell`; the pricer declares `Take out of
After`, `Put into After`, and the same for Before (ADR-0016 §4).

**Pull request:** 2c

**Blocked by:** 05.

**Status:** ready-for-agent

- [ ] The Existing grid's Context Menu shows `Take out of After` with `set selected after off`, and it writes the
      selected rows.
- [ ] `Reset this cell` is offered on a cell that has an edit, and takes it back.
- [ ] The page's hand-written items (`Ask again`, `Vary values…`) still stand beside them.
- [ ] A refusal is a notice with the server's reason.
