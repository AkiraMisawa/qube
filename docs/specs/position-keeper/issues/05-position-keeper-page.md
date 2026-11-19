# 05 The Position Keeper page

Status: done

**Blocked by:** 04.

**What to build:** the `pk` application and its page: the result set's identity, the positions grid (ExGrid through
its React adapter), the risk grid with Book, What-if and Net, the moves and the predicted profit and loss, and the
commands on the line (ADR-0019 §1, §7; ADR-0001, ADR-0006, ADR-0009).

- [x] The page shows the engine's commit, the input set and the valuation date of what it displays.
- [x] `WHAT-IF` and `SIMULATED` are drawn from the server's marks on every row and total they apply to
      (`client/e2e/position-keeper.spec.ts`).
- [x] With no result set, the page says so and shows no grid of positions.
- [x] A hedge is added, changed and removed with the keyboard alone.
- [x] The page reads at laptop and phone widths, in the light and dark themes.

The pull request carries a `Screens` section (CLAUDE.md).
