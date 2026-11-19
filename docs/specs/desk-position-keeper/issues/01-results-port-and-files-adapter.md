# 01 The results port and the files adapter

Status: ready-for-agent

**Blocked by:** none.

**What to build:** `IDeskResults`, the result set's types (manifest, values, sensitivities, trades), and the adapter
that reads a directory of the engine's files with a manifest beside them (ADR-0019 §4, §7). The engine's small result
files are copied into the test project, with the commit they came from in the manifest.

- [ ] The engine's small result set reads through the adapter: every counterparty has CVA, FVA and ColVA, and the
      sensitivity kinds are those the engine wrote (the contract test, ADR-0019 §8).
- [ ] A missing file, a missing column and a number that does not parse are each refused by name; no row is skipped.
- [ ] A manifest without the engine commit, the input set or the valuation date is refused.
- [ ] A counterparty in the results that the manifest's mapping does not hold reads as unmapped, with its code.
- [ ] A sensitivity kind without a declared unit reads with no unit, not with a default one.
