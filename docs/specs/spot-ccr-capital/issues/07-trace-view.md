# 07 The trace view

Status: done

ADR-0011 §6: the tree of terms in QubeGrid with a pinned Template Column, the typeset formula with values
substituted (KaTeX pinned, strict, its fonts served by the application), the decision steps, the leaf rows, the
reconciliation, drill-in below sm, and the case priced on one side; and `explain <measure> [path]` (ADR-0011 §7),
which opens it, moved here from ticket 06.

Done 2026-11-08: the view, `explain`, and `client/e2e/trace.spec.ts`. Not in it, and listed in
IMPLEMENTATION_STATUS.md's known limits: `trace` as a declarative view component, opening from the Pricing DB, and a
leaf row's action to its trade's row.
