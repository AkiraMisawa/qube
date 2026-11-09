# 05: the Risks tab shows each measure's credit, IR and FX deltas, Before, After and Diff

**What to build:** the contract `portfolio.risks`; the fake XVA provider, with risks included, bumping and repricing
on the scenario's market: CVA's credit delta per CDS tenor (+1bp key-rate), IR delta per discount curve (+1bp
parallel), FX delta per currency against JPY (+1%); FVA's IR deltas per discount and funding curve and FX deltas; a row
with no factor saying why a measure has none. CVA and FVA register the Risks tab; the dock draws a tab several
measures register once; the client's Risks view reads the rows of the case and the market in view.

**Blocked by:** 01, 04.

**Status:** open

- [ ] With risks included, each delta is the bumped figure minus the priced one, per side, and Diff is After − Before.
- [ ] Without it, or with the measure refused, the tab says why for that measure, never a table of zeros.
- [ ] The Risks tab is one tab while CVA and FVA are both selected, and follows the case and scenario selectors.
- [ ] In a browser, after Price with risks included, the Risks tab shows CVA's credit deltas.
