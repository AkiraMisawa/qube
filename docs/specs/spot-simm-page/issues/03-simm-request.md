# 03 SIMM's own request over the accepted input

Status: done

ADR-0012 §4's note of 2026-11-08, and §7. `simm.spotIm` is SIMM's module step. For each side on its own, it does the
following:

1. The trades in any spot IM scope become Rates lines.
2. CRIF is generated at the value date on the frozen market.
3. The engine runs under the frozen IM CSAs, each holding the side's trades that name it.
4. IM-R and IM-P are converted from USD to the display currency at the frozen FX.

Its rows are the shared result's. Each side's note is the governing regime, the basis is `SIMM PARAMETERS DEMO` while
the table is the DEMO one, and the trade counts are the side's trades in that direction's scopes. A direction with
nothing in scope on a side is `notPriced` there, with why. A trade in scope that gave no CRIF line makes its side
`unavailable`, naming the trade.

Its other outputs are a trace per priced side (case and scenario `base`, measure `simm.spotIm`) and each side's CRIF
(`crifBefore`, `crifAfter`). The shared result gains the columns `beforeNote`, `afterNote` and `basis` (ADR-0010 §7's
note). The trace's measure becomes the measure's ID, the standalone workflow's included.
