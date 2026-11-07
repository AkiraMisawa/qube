# 04: New trades above existing trades, B and A for a whole grid, and After-only Price

**What to build:** two trade grids (ADR-0010 §6): New trades, as high as their rows up to about eight, above Existing
trades, each paged and sorted on the server. B or A is set or cleared for every row of a grid by one edit, from the
grid's caption and the commands `before all|none` and `after all|none` (§6 note of 2026-11-07). An existing trade
shows its official PV. With every B box clear Price prices After alone (§3): no Before figures, no Diff, the dock says
`After only: Before was not priced`, and Price says `Price After only · …`. A Price with no trade on either side is
refused by name.

**Blocked by:** 02.

**Status:** ready-for-agent

- [ ] Clearing B for all existing trades is one edit, and a row ticked afterwards stays ticked.
- [ ] A Price with every B box clear shows `not priced` for Before and no Diff, never 0.
- [ ] The official PV of an existing trade is the source's.
