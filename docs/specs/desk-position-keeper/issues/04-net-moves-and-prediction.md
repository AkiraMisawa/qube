# 04 Net, moves and the predicted profit and loss

Status: ready-for-agent

**Blocked by:** 03.

**What to build:** `desk.moves`, the pure functions for Net and for the prediction, and `desk.position.read`
(`ReadWorkspace`), which returns Book, What-if and Net with their marks (ADR-0019 §3, §7).

- [ ] Net is Book plus What-if on matching kind, input and key; a key only one side holds is kept.
- [ ] The predicted profit and loss is the sum of sensitivity times move, for Book, What-if and Net, marked first
      order.
- [ ] A move for a kind without a declared unit is refused by name.
- [ ] Every figure that includes a created row carries `WHAT-IF`, and every figure from a `Simulated` provider
      carries `SIMULATED`; a book-only figure carries neither.
- [ ] No Run is made by any command of the slice.
