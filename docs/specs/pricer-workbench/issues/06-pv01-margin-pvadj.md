# 06: PV01, Margin and PV Adj on new trades

**What to build:** a new trade's PV01 is requested when the trade is first seen and shown as pending, its figure, or
why it failed, never 0 (ADR-0010 §6). `pv01 retry` and the failed cell's action ask again. Margin (bps) is an input;
PV Adj is PV01 × Margin, each signed, when Margin is entered, and read-only then; with Margin empty PV Adj is typed.
Price freezes PV Adj with the PV01 and Margin it came from; with Margin entered and PV01 not available, XVA's
measures are refused by name.

**Blocked by:** 04.

**Status:** done

- [x] A new trade shows `pending` and then its PV01 without a page reload.
- [x] Margin 2bp on a PV01 of −1,500 gives PV Adj −3,000, read-only and saying it came from Margin.
- [x] A Run's frozen input holds each PV Adj with its PV01 and Margin, and a later PV01 does not change it.

*Done 2026-11-08.* A module registers its trade columns (ADR-0010 §10: `TradeColumns`, `Cells`, `Check`, `Retry`); a
trade's field for one is `{module}.{id}`, which no source holds, so a module's input starts empty and a value typed
into it differs from Original. Each cell carries its value, its state (`value`, `pending` or `failed`, never 0 for
either), a note and whether it may be typed now; the server refuses an edit the cell does not allow, with its note.
Cells are read with each page and view, never kept with the state, since PV01 arrives with no edit. XVA's PV01 comes
from `Pv01Provider`, asked once per trade's terms and value date; the simulated engine answers after
`Xva:Pv01DelayMilliseconds` (400 unless set) and fails for a curve its market lacks. The view counts the pending
cells on new trades, and the page reads it again every 500ms while any is, then reads the New grid again. Price reads
the cells once, so what XVA refuses (`PvAdjUnknown`, by name, for each selected XVA measure) and what it freezes on
each trade (`cells` beside `membership`) are one reading. A failed cell's action is the grid's context-menu command
*Ask again*, and `pv01 retry` asks again for all. The fake engine shifts a new trade's exposure by its PV Adj as a
toy. Columns sit after B and A on the New grid, 168px wide so that amounts of the synthetic notionals' size fit.

