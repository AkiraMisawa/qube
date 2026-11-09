# 02: the Credit tab shows the curve, chooses a CDS and edits its spreads

**What to build:** XVA's Credit input tab (ADR-0010 §8), shown while CVA is selected: the CDS type (single name, proxy,
none); a single name found by a searchable select over `ticker | ccy | seniority | doc clause | name`; a proxy by sector,
region and rating; one ExGrid row of the curve (type, ticker, currency, seniority, doc clause, CDS recovery, the spread at
each tenor in bp), the spreads editable, marked where they differ from the catalogue's, with *Vary values…* on them; the
reason CVA is refused when no CDS is chosen. The commands `cds …` (completed from the catalogue) and `open credit`.
The Netting tab's CVA recovery placeholder names the curve's recovery.

**Blocked by:** 01.

**Status:** open

- [ ] The Credit tab shows while CVA is selected and counts the curve's changed spreads.
- [ ] Choosing a single name, a proxy or none sends the choice and the grid shows the new curve.
- [ ] A spread typed in bp is sent and marked; one the server refuses is said on the cell.
- [ ] In a browser, `cds` from the command line and a spread typed in the grid change the Metrics after Price.
