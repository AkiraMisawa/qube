# 04 SA-CCR writes its trace

Status: done

ADR-0013 §4: the kinds and leaf sets of method sa-ccr 2023-01-01 registered by the domain module, and the calculator
writing every term it computes with ids from the netting set down to trade rows. Every term reconciles.

## Comments

- 2026-11-08: done in `Qube.Samples.Ccr/SpotCapitalTrace.cs`, proven by `CcrTraceTests` (20 cases, tests first).
  Every term of fourteen portfolios reconciles against its kind, and every computed column of every row against its
  row: the BCBS netting sets, several asset classes, margined FX with collateral, threshold and MTA less NICA,
  haircuts with segregated and foreign collateral, basis and volatility hedging sets, shifted and sold options, a CDO
  tranche, no offset across buckets, a netting set that is not legally enforceable, a floored risk weight, an
  unrated corporate, and an interest rate hedging set with all three buckets beside a short FX and a ten-day trade.
  Each formula was then broken in turn and a test failed for every one.
- The kinds and ids are the format proposal's §8 and §9, with these refinements, written back into the proposal the
  same day: a basis's V, C and NICA sit beneath its replacement cost (`NS-001/m/rc/v`), because the haircuts and so C
  differ between the margined and the unmargined figure; FX hedging sets, credit and equity entities and commodity
  types have an effective notional term (`…/en`) above their rows, so EN is a figure the page can show; credit and
  equity keep a hedging set level, since a volatility hedging set sits beside the regular one; and a trade row's
  maturity factor is computed by its basis, so each asset class has a margined leaf set beside the unmargined one.
- A risk weight step cites the paragraph that decided it on the term itself (ADR-0011 §1, DEFAULT of 2026-11-08), and
  its table row is a term (`rw/bucket`) that the weight's lookup reads.

