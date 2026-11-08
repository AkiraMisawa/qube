# 06: PV01, Margin and PV Adj on new trades

**What to build:** a new trade's PV01 is requested when the trade is first seen and shown as pending, its figure, or
why it failed, never 0 (ADR-0010 §6). `pv01 retry` and the failed cell's action ask again. Margin (bps) is an input;
PV Adj is PV01 × Margin, each signed, when Margin is entered, and read-only then; with Margin empty PV Adj is typed.
Price freezes PV Adj with the PV01 and Margin it came from; with Margin entered and PV01 not available, XVA's
measures are refused by name.

**Blocked by:** 04.

**Status:** ready-for-agent

- [ ] A new trade shows `pending` and then its PV01 without a page reload.
- [ ] Margin 2bp on a PV01 of −1,500 gives PV Adj −3,000, read-only and saying it came from Margin.
- [ ] A Run's frozen input holds each PV Adj with its PV01 and Margin, and a later PV01 does not change it.
