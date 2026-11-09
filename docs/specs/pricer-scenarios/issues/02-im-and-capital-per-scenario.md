# 02: spot IM and spot CCR capital are priced under each scenario, with their traces

**What to build:** spot IM generates its CRIF again on the snapshot's curves and its pinned FX, shifted; spot CCR
capital takes the revalued PVs and the shifted FX (ADR-0010 §4's note of 2026-11-09). Their traces are written per case
and scenario (ADR-0011), and the SIMM and CCR tabs read the trace of the case and the scenario in view.

**Blocked by:** 01.

**Status:** ready-for-agent

- [ ] Spot IM under a curve shift and under an FX shift differs from the base market's, and equals it under a shift no
      priced trade reads.
- [ ] Spot CCR capital under an FX shift and a curve shift differs from the base market's, through its replacement
      cost and its add-on.
- [ ] Each pair's trace reads at its case and scenario, and a scenario the Run did not price is refused by name.
- [ ] In a browser, the SIMM tab follows the dock's scenario selector.
