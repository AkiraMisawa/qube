# 18: The pricer's scenarios are a registered table

**What to build:** `xva.scenarios`, a row a scenario, with its shifts under it (ADR-0016 note 32).

**Pull request:** 5d

**Blocked by:** the pull request before it.

**Status:** done

- [x] `tables.read scenarios` answers each scenario with whether it is priced.
- [x] A scenario's shifts are rows under it, set and removed as any rows are.
- [x] The scenario verbs of note 32 still read, and make the same edits.
