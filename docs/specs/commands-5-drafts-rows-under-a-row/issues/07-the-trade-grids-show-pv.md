# 07: The trade grids show PV

**What to build:** Each trade grid has a PV column in the display currency, and a PV typed in a cell is the same `set`
(ADR-0016 note 49).

**Pull request:** 5a

**Blocked by:** 06.

**Status:** done

- [x] The page's rows carry `pv`, and both grids show it, grouped and red when negative as amounts are.
- [x] A PV typed in a cell runs `set <trade> pv <typed>` through the page's door, and a refusal is shown as the
      cell's. (As built: what the page itself refuses, nothing typed or an `=`, is the cell's; what the server
      refuses is a notice that names the trade, as for any typed cell.)
- [x] As built: the column stands after B and A on both grids, a made leg counts among the workspace's edits, and
      the cell of a PV that differs from Original says no measure reads the legs.
- [x] Looked at 390, 768, 992 and 1440px in both themes.
