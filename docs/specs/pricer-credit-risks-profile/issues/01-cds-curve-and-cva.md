# 01: the counterparty's CDS curve is chosen, edited and priced, and a missing one refuses CVA

**What to build:** the synthetic CDS catalogue (single names and proxies) and each counterparty's CDS from the source
(ADR-0010 §8's note of 2026-11-09); the choice on `credit:<registration>` and the spreads on `cds:<curve id>`; the
effective credit in the workspace view with the chosen curve, its spreads, Original and why there is none; XVA's input
carrying the curve; `NoCds` refusing CVA (and CVA Charge) by name; the fake provider's CVA integrating a toy EE profile
against the curve's default probabilities; the cells `cds <tenor>` and `credit <field>` for `set`, `vary` and the
Changes tab; the command `cds` on the server (a single name by its words in any order, a proxy by its three keys, or
none), with its capability `xva.cds`; and the CDS catalogue endpoint the page searches.

**Blocked by:** none.

**Status:** open

- [ ] Each registration has the source's CDS: ABC/LE-01 and ABC/LE-02 a single name, XYZ/LE-03 a proxy.
- [ ] `cds` chooses a single name by any of its words, refusing words that name none or several with what they could
      be; `cds proxy <sector> <region> <rating>` a proxy; `cds none` none.
- [ ] A spread edit is kept on its curve: choosing another curve leaves it out of Price, and choosing the curve again
      prices it; a spread is > 0 bp, refused otherwise.
- [ ] A wider spread makes CVA more negative; an empty CVA recovery takes the curve's recovery.
- [ ] With no CDS, or a proxy without one of its keys, CVA and CVA Charge are refused by name and FVA is priced.
- [ ] `set cds 5y 120bp` and `vary cds 5y 100bp, 140bp` reach the chosen curve; the Changes tab names them.
