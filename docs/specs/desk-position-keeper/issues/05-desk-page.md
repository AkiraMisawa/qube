# 05 The Desk page

Status: ready-for-agent

**Blocked by:** 04.

**What to build:** the `desk` application and its page: the result set's identity, the positions grid (ExGrid through
its React adapter), the risk grid with Book, What-if and Net, the moves and the predicted profit and loss, and the
commands on the line (ADR-0019 §1, §7; ADR-0001, ADR-0006, ADR-0009).

- [ ] The page shows the engine's commit, the input set and the valuation date of what it displays.
- [ ] `WHAT-IF` and `SIMULATED` are drawn from the server's marks on every row and total they apply to
      (`client/e2e/desk.spec.ts`).
- [ ] With no result set, the page says so and shows no grid of positions.
- [ ] A hedge is added, changed and removed with the keyboard alone.
- [ ] The page reads at laptop and phone widths, in the light and dark themes.

The pull request carries a `Screens` section (CLAUDE.md).
