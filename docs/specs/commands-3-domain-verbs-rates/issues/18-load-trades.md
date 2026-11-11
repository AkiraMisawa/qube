# 18: `load trades <ids>`, or why not yet

**What to build:** ADR-0016 §3 names `load trades <ids>` for bringing existing trades into the New grid by id. Build
it if the sample's source can answer a trade by id; otherwise record in ADR-0016 why it waits for step 5's trade
terms.

**Pull request:** 3d

**Blocked by:** 16.

**Status:** done

- [x] Either `load trades` brings a trade into the New grid and `remove` takes it out, or the ADR says why it waits.

## Comments

- *As built:* it waits, and ADR-0016 note 37 says why. The pricer makes no trade: its New rows are the source's
  own, and a trade brought in by id needs a trade the workspace holds with its terms, which is step 5's. Nothing is
  built here and the name stays reserved.
