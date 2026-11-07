# 03: CSA terms, FVA's membership, and the Netting and CSA tabs

**What to build:** the synthetic CSAs gain their terms (type, currency, discount curve, FVA discount curve,
frequency, exchange, one-way, and both parties' threshold, MTA and currency) and the netting set its enforceability
and CVA recovery (ADR-0010 §8). FVA prices only trades on imperfectly collateralised CSAs (§5), decided on the
server with a reason per trade and the rule's version frozen into the Run; an uncollateralised trade is out with FVA
0. The page gains the Netting and CSA tabs (editable terms, the derived Imperfect column), an FVA column on the
trades, and trade counts per measure in the dock (`FVA 300 → 301`). Figures are in the display currency.

**Blocked by:** 02.

**Status:** ready-for-agent

- [ ] Each CSA says whether it is imperfect and why (`CSA-A: daily, MTA+threshold 0.4mm GBP`), converted at the value
      date's synthetic FX.
- [ ] Raising a CSA's threshold past 1mm GBP moves its trades into FVA: their reason says so, and the next Price
      counts them.
- [ ] An uncollateralised trade is outside FVA and adds 0 to it.
- [ ] An empty CVA recovery on the netting set stays empty and the CDS recovery applies; a recovery set before this
      slice on the counterparty still applies.
