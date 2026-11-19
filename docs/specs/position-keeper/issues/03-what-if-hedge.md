# 03 A what-if hedge and its risk

Status: done

**Blocked by:** 02.

**What to build:** the created-entity table `pk.hedges`, the interest rate swap's short terms for `entity.create`
(ADR-0008), the `IHedgeRisk` port, and the synthetic calculator behind it, `Simulated` (ADR-0019 §2, §7).

- [ ] A swap created from its short terms is a row of the workspace, in the log, and `undo` takes it back.
- [ ] A removed hedge leaves the figures as they were before it.
- [ ] The port's answer carries its provider and execution mode; the synthetic calculator's is `Simulated`.
- [ ] A hedge in a currency whose curve the result set does not hold is refused by name.
- [ ] The synthetic calculator's sensitivities of a par swap agree with a bump of the same curve, to a stated
      tolerance.
