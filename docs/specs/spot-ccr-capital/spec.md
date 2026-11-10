# Spot CCR capital, explained to trade level

Status: ready-for-agent

The decisions are ADR-0011 (the calculation trace) and ADR-0013 (spot CCR capital is SA-CCR's EAD times SA's risk
weight), within ADR-0010's shared pricer page. This spec only orders the work; it decides nothing they do not.

## In this slice

- The kernel's calculation trace: contract, kinds and their registration check, the closed operation set and its
  evaluator, reconciliation, pairing of sides including a case priced on one side, reading by term (ADR-0011 §1–§5).
- The SA risk weight, resolved at run time with every step as a term (ADR-0013 §5).
- SA-CCR in full, as the reference implementation in `Qube.Samples.Ccr`, writing its trace (ADR-0013 §3–§4).
- The fake XVA provider's KVA option returning spot capital, its trace, a capital profile and KVA (ADR-0013 §2).
- The read endpoint, `trace.read` and `explain` (ADR-0011 §5, §7).
- The trace view in the client: the tree beside the typeset formula, drill-in on a phone, leaf rows (ADR-0011 §6).

## Out of this slice

- The engine adapter: the engine's drill-down output is still being built (ADR-0013 OPEN).
- A request that computes spot capital alone (it does not exist yet).
- National rules beyond Basel's, which are configuration once named (ADR-0013 OPEN).

## Done when

- The BCBS SA-CCR worked example's first netting set gives EAD 569.47, and a case per rule of ADR-0013 §3 and §5
  holds its hand-computed figure.
- Every term the reference implementation writes reconciles against its kind.
- A Price with KVA opens spot capital's trace in the page and through `explain`, on both sides and on After alone.
