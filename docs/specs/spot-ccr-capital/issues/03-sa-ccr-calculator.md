# 03 SA-CCR in full

Status: done

ADR-0013 §3: EAD, margined and unmargined replacement cost, the multiplier, the five asset classes with their hedging
sets (basis and volatility included), trade-level adjusted notional, supervisory delta (options and CDO tranches) and
maturity factor, the MPOR derivation, and the decomposition of a trade into its exposures. Checked against the BCBS
worked examples and a case per rule.

## Comments

- 2026-11-08: done in `Workbench.Samples.Ccr/SaCcr.cs` and `Decompose.cs`, proven by `CcrSaCcrTests` (36 cases) and
  `CcrDecompositionTests` (6), tests first, each rule then checked by breaking it. The BCBS worked examples hold their
  published EADs: interest rate 569 (569.47), credit 381 and commodity 5,406. The other expected figures are a
  separate hand calculation of the rule each test names.
- The collateral's ten-day haircut is the collateral data's (CRE22's table is not coded here); the calculator scales
  it to the MPOR, or unmargined to the longest trade up to a year (CRE52.5, CRE22.59), and adds the 8% currency
  mismatch. A floating leg of a cross-currency swap carries no interest rate exposure.
