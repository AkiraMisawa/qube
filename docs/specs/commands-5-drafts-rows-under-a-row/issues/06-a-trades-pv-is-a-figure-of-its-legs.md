# 06: A trade's PV is a figure of its legs

**What to build:** `pv` on the trades: the legs' PVs in the display currency, added up; written, the trade is left one
leg; `new leg` adds one (ADR-0016 note 49).

**Pull request:** 5a

**Blocked by:** 03, 04, 05.

**Status:** ready-for-agent

- [ ] `pv` reads as the sum in the display currency, and is empty for a trade with no leg.
- [ ] `set E00003 pv -350,000 USD` leaves one leg of that amount in USD, and with no currency in the display currency;
      the preview says how many legs are replaced.
- [ ] A trade that already holds that one leg alone is counted as holding it.
- [ ] `reset E00003 pv` brings the source's legs back.
- [ ] `new leg E00003 eur 250,000` adds a leg numbered after the trade's last, and is refused for a trade the
      workspace has not.
- [ ] `where pv<0` reads the figure.
