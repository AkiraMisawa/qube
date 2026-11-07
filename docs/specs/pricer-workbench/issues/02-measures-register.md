# 02: Measures register into the page, and Price prices the selected ones

**What to build:** the measures register (ADR-0010 §10) with XVA's CVA and FVA as its first measures (§11). The
workspace holds the selected measures; `measures cva fva` sets them, a new workspace takes its owner's last
selection, and Price says what it will run (`Price CVA FVA · 1 case`) and freezes the selection. The result is one
row per case and measure with Before, After, Diff, trade counts and a status, and the page's result dock shows it per
selected measure. A measure's validation blocks only that measure.

**Blocked by:** 01.

**Status:** ready-for-agent

- [ ] The workspace view lists the registered measures with their columns and the current selection.
- [ ] `measures cva` then Price gives a Run with CVA only; FVA is neither priced nor shown.
- [ ] A refused measure is listed with its reason and the other measures are priced.
- [ ] The Metrics dock shows Before, After and Diff per selected measure, `unavailable` for a failed one, never 0.
