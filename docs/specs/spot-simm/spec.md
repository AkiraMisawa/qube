# Spot SIMM, explained to the CRIF line

Status: ready-for-agent

The decisions are ADR-0012 (spot initial margin is SIMM's measure, per IM CSA and regime) and ADR-0011 (the
calculation trace), within ADR-0010's shared pricer page. This spec only orders the work; it decides nothing they do
not. This slice is ADR-0012's delivery step 1: nothing in it needs the page.

## In this slice

- The reference SIMM engine in `Workbench.Samples.Simm`: the SIMM v2.x aggregation from risk factor to the worst
  of the regimes, per direction and IM CSA, with the Euler allocation down to the CRIF line (ADR-0012 §1, §5, §6).
- The DEMO parameter table, labelled DEMO wherever a figure computed with it is shown (ADR-0012 §5).
- CRIF for the synthetic IRS, CCS and FX forward, from the Rates module's pricing at the value date; the FX forward
  is exempt in every scope (ADR-0012 §5).
- `risk.crif.generate` and `margin.simm.calculate`, registered as capabilities and run as workflows, each alone
  (ADR-0012 §4).
- SIMM's trace kinds registered in ADR-0011's catalogue, and the engine writing its trace (ADR-0012 §6).

## Out of this slice

- Spot IM registered into the pricer page: membership, columns, the IM tab, Metrics (delivery step 2; it needs
  ADR-0010's page).
- The SIMM tab's drill-down (delivery step 3).
- Running a workflow from the AI. The agent calls commands only today (ADR-0005, ADR-0007), so AI03 needs a generic
  kernel tool for registered workflows; that is a kernel decision of its own.
- Vega, curvature and base correlation: kinds without values until a product with options arrives.

## Seams (agreed with the owner, 2026-11-07)

1. The engine: CRIF lines, IM CSAs and a parameter table in; IM per direction and regime, the governing regime, every
   term's value and its Euler contribution out. Expected values are hand-worked from the DEMO table, or come from the
   drill-down sample's separate implementation.
2. CRIF generation: synthetic trades, a market snapshot and a value date in; CRIF lines out.
3. Through the workbench: the workflows over the HTTP API, with their contracts, refusals by name, the `simulated`
   and `DEMO` labels, and the Run kept.

## Done when

- Every hand-worked case of ADR-0012 §6's formulas holds, and the sample portfolio gives the sample's figures.
- A term's children's contributions add up to its own, and a regime's lines' contributions to its SIMM.
- Both workflows run alone over the API and their Runs resolve their inputs and results.
- Every term the engine writes reconciles against its kind.
