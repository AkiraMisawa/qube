# 16: `add each` takes iterations as lists

**What to build:** A contract may hold a list. `xva.trades.add` takes a list of iterations, each the candidates it
adds and the existing trades it takes out of After, under ADR-0016 §3's rules; with none it makes one case per New row
with A ticked, as now. A case names its trades as a list, and Price's check of the set reads the iterations'
candidates together.

**Pull request:** 3d

**Blocked by:** 04.

**Status:** ready-for-agent

- [ ] An iteration of two candidates and one trade taken out makes one case that names all three.
- [ ] A candidate with A clear or B ticked, and a trade taken out that the Base has not in After, are refused by name.
- [ ] A ticked candidate no iteration names is one iteration alone.
- [ ] Price refuses a set that no longer matches the ticks, as before.

## Comments
