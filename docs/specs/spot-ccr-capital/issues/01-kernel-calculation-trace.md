# 01 The kernel's calculation trace

Status: done

ADR-0011 §1–§5: the contract types, kind and leaf-set registration with the TeX marker check, the operation set and
its evaluator, the reconciliation check, the pairing of Before and After by term id with the Diff rules, the case
priced on one side, and reading a term with its path, children, parameters and leaf rows. Proven over a synthetic
trace that is not a regulation.

## Comments

- 2026-11-07: done in `Qube.Composition.Contracts/Traces.cs` and `Qube.Composition.Application/Traces/`,
  proven by `TraceTests` over the synthetic garden-water method. The tests were written after the evaluator; each rule
  was then checked by breaking it and seeing a test fail.
