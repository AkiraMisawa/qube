# 16: `add each` takes iterations as lists

**What to build:** A contract may hold a list. `xva.trades.add` takes a list of iterations, each the candidates it
adds and the existing trades it takes out of After, under ADR-0016 §3's rules; with none it makes one case per New row
with A ticked, as now. A case names its trades as a list, and Price's check of the set reads the iterations'
candidates together.

**Pull request:** 3d

**Blocked by:** 04.

**Status:** done

- [x] An iteration of two candidates and one trade taken out makes one case that names all three.
- [x] A candidate with A clear or B ticked, and a trade taken out that the Base has not in After, are refused by name.
- [x] A ticked candidate no iteration names is one iteration alone.
- [x] Price refuses a set that no longer matches the ticks, as before.

## Comments

- *As built (ADR-0016 note 37):* the request's `iterations` is the whole set, so the cases of `add each` made before
  are replaced by it, and a caller that adds one case gives the made ones again. The same iteration twice, and a
  trade twice in one list, are refused (`GivenTwice`) rather than merged. A case names its `iteration` where it
  named one `trade`; one stored before is read as that trade alone.
- *Added as built:* a workflow whose input contract holds a list is refused at registration (`ListInForm`), and
  Price refuses a set that takes out a trade After no longer holds.
- *Corrected in review (ADR-0016 note 37):* the request also takes `keep`, with which the iterations given are made
  beside the cases made, which stay the cases they are; the log names what is new of a set; and the request's
  lists are read by `Iteration`'s own readers, a list that is none after the contract passed being a defect.
