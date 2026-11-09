# 03: a Credit shift moves the counterparty's CDS curve in a scenario

**What to build:** the Credit category of the scenario catalogue (ADR-0010 §8's note of 2026-11-09): the index `cds`,
whole or at a CDS tenor, absolute in bp, key-rate on the CDS tenors; tenors read per category, so an IR tenor on `cds`
and a CDS tenor on a curve are refused naming the category's; the scenario market's credit shift at a point, read by
the fake XVA provider's default probabilities. The grid, the reader and the generators take it.

**Blocked by:** 01.

**Status:** open

- [ ] `scenario wide cds +50bp` prices a more negative CVA than the base market, and the same FVA.
- [ ] `cds 5y +20bp` moves only the buckets around 5y; `cds 30y` and `jpy_ois 7y` are refused with the category's tenors.
- [ ] The Scenarios grid and `scenario ladder cds …` take a Credit shift.
