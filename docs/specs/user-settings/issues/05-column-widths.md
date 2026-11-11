# 05: Column widths follow the user

**What to build:** `grid.widths.<grid id>`, by a grid id with its application's prefix, across workspaces, panels and
reloads, in place of the per-panel memory; a drag writes when it ends; a grid's widths can be reset (ADR-0017 §5).

**Step:** 1

**Blocked by:** 02.

**Status:** ready-for-agent

- [ ] A width dragged in the pricer's existing trades is the same after a reload and in another workspace.
- [ ] Reset puts the declared widths back.
