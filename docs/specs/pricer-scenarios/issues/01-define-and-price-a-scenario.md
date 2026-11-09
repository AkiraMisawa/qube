# 01: a scenario defined by command is priced with every case, and the dock shows it

**What to build:** the synthetic catalogue and the shift reader (ADR-0010 §4 and its note of 2026-11-09);
`scenario s1 jpy_ois +10bp; usdjpy -5%` makes s1's rows in the workspace, replacing a scenario of that name and saying
so; Price prices the base market and every included scenario, crossed with every case, in one Run, refusing two shifts
on one risk factor and a pair count over the cap by name; the simulated XVA provider revalues each trade under the
shifts; the Metrics dock and its collapsed line follow a scenario selector, and Copy quote quotes the scenario in view.

**Blocked by:** none.

**Status:** ready-for-agent

- [ ] A shift's words read in any order; an unknown index, tenor or size, or a size in a unit its category does not
      take, is refused with what it could be.
- [ ] `scenario <name> <shifts>` makes the scenario whole, replaces one of its name and says so; a counterparty change
      keeps it.
- [ ] The Run's accepted input holds the base market first, then each included scenario with its shifts, and a result
      row per case, scenario, measure and metric.
- [ ] A curve shift moves CVA, FVA and PV of trades on that curve, a funding shift moves FVA alone, an FX shift moves
      the figures of trades in that currency, and a shift on a curve no trade is on leaves them equal to the base market.
- [ ] Two shifts touching one risk factor in one scenario, and pairs over the cap, are refused at Price by name.
- [ ] In a browser, `scenario` from the command line, then Price, shows the scenario in the dock's selector, and
      Metrics and the collapsed line follow it.
