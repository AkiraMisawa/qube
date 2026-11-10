# 02 The SA risk weight, resolved and explained

Status: done

ADR-0013 §5: exposure class, approach (ECRA or SCRA), eligible ratings and the rating used, short-term treatment,
due diligence, the SCRA sovereign floor and the table lookup, each a term with what it read and its rule. Basel III
values; a national rule's differences are configuration.

## Comments

- 2026-11-07: done in `Qube.Samples.Ccr/RiskWeights.cs`, proven by `CcrRiskWeightTests` (46 cases, tests first),
  with each rule checked by breaking it. Every class of ADR-0013 §5: sovereigns (Table 1, ECA scores, the
  domestic-currency discretion, the CRE20.10 organisations), PSEs (both options, and treatment as the sovereign), MDBs,
  banks (ECRA with short-term and due diligence, SCRA with footnote 15's 30% and the CRE20.32 floor), securities firms
  and other financial institutions, corporates (rated, unrated, SME, and the jurisdiction without ratings), and retail.
- Not modelled: retail transactors (45%, a revolving facility, not a derivative counterparty) and defaulted exposures
  (ADR-0013 OPEN). Retail is cited by section: its paragraph numbers were not in the text read.
