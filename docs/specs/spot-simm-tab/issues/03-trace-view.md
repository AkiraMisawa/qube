# 03 The trace view where the sample differs

Status: done

ADR-0011 §6 and ADR-0012 §6. These are generic changes, and each measure that has a trace gets them:

- **Contributions** on each side the case priced, with their Diff, where today only After's allocation is shown.
- **A governing mark** in the term column, on the operand a max, min or bound took (`Governs`), and `Not governing`
  on its other operands, drawn faint. The kernel resolves which term took it on each side.
- **The `explain` line** of the selected term, with a copy button. Run from the command line, it opens the same term.
- **Parameters**: first those the term's formula names and those scoped to the term. Those scoped to a term above
  follow, folded under a heading that opens them. A matrix scrolls inside its own box, and the source and reasons sit
  under the value. The page never scrolls sideways.
- **Spot IM's labels**: `IM-R · collect` and `IM-P · post` for the roots, and `JFSA regime` for a regime.

Tests: the kernel's `governs` in process; Vitest for the mark, the allocation columns and the parameters split; the
browser for the `explain` line and its copy, and checks at every width.
