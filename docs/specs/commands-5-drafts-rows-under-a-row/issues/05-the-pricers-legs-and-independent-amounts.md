# 05: The pricer's legs and independent amounts

**What to build:** The synthetic source gives each existing trade its PV legs, which add up to its official PV, and
some trades independent amounts; `xva.legs` and `xva.amounts` are registered under the trades; a leg's currency, PV
and notional are set, a row is removed, and `reset` and `undo` bring it back (ADR-0016 note 49).

**Pull request:** 5a

**Blocked by:** 01.

**Status:** ready-for-agent

- [ ] `set legs E00003/1 pv 250,000` changes the leg, checked as a typed cell is.
- [ ] `remove legs E00003/1` takes a source's leg out; `reset` of the trade's PV and `undo` bring it back.
- [ ] `remove amounts E00003/1` takes an independent amount out; its fields are not set.
- [ ] A new trade has no leg.
- [ ] An edit of a leg survives a re-acquisition as any edit does, and is the trade's scope's.
