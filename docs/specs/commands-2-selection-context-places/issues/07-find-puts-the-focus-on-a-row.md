# 07: `find` puts a grid's Focus on a row or a cell

**What to build:** `find <row> [<field>]` on the focused grid's table: the server answers the row's position in the
view's order, and the grid places its Focus there with `placeMatch`, scrolled into view. An unknown row or field is
refused by name with its candidates; an order that moved is refused (ADR-0016 §9).

**Pull request:** 2b

**Blocked by:** 06.

**Status:** ready-for-agent

- [ ] `find E00003 csa` puts the Focus on that cell of the Existing grid, though the row was not loaded.
- [ ] A row id's end that names one row is taken; one that names several is refused with them.
- [ ] The keyboard is in the grid afterwards, and the notice says where.
