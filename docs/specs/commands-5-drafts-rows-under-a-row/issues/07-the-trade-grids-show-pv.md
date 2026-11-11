# 07: The trade grids show PV

**What to build:** Each trade grid has a PV column in the display currency, and a PV typed in a cell is the same `set`
(ADR-0016 note 49).

**Pull request:** 5a

**Blocked by:** 06.

**Status:** ready-for-agent

- [ ] The page's rows carry `pv`, and both grids show it, grouped and red when negative as amounts are.
- [ ] A PV typed in a cell runs `set <trade> pv <typed>` through the page's door, and a refusal is shown as the
      cell's.
- [ ] Looked at 390, 768, 992 and 1440px in both themes.
