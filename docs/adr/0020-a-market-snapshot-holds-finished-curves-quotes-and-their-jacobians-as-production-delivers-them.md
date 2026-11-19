# ADR-0020: A market snapshot holds finished curves, quotes and their Jacobians, as production delivers them

Status: ACCEPTED 2026-11-17. The owner, in the thread on pricing Rates lines with alpha: "what arrives every second is
not quotes but curves", "the Jacobians come with them", and "qube and alpha are products of the same world" (asked how
to pass them, he chose to change the market itself over a side door for the alpha provider; "進めて").

## Context

Until now a `MarketSnapshot` held, per currency and index, pillar levels in percent: inputs to a toy curve that the
fake provider interpolated, with 1bp level bumps for risk. Production delivers something else every second: curves
already built (discount factors at nodes), the quotes they were solved from, and the par-delta Jacobians. alpha's XVA
input has exactly that shape (alpha `docs/spec/inputs.md` §3: `market/curves`, `market/curve_settings.csv`,
`market/quotes`, `market/jacobians`, `market/fx_spot`). A Rates provider, SIMM's CRIF and later the Position Keeper all
read the same market, and none of them should build curves.

## Decision

1. A snapshot holds `Curves` (name, currency, index, role, composition `base` or `ratio`, interpolation, the index's
   tenor in months for a term index, and nodes: tenor label, days from the valuation date, discount factor),
   `Quotes` (per quote set, tenor label, days, par rate in percent), `Jacobians` (per quote set and curve, `Rows[i][j]` =
   ∂z_j/∂q_i, zero rates continuously compounded on ACT/365F) and `Fx`, mapped one to one onto alpha's input files.
2. Nothing in qube solves curves from quotes. A provider reads the curves; par delta is the zero delta through the
   delivered Jacobian.
3. Measures are named by the market's own names: `zero:{curve}:{node tenor}` and `delta:{quote set}:{quote tenor}` (par).
4. The synthetic market keeps its seeded steps but produces this shape: its configured numbers are zero rates; each curve
   is one base OIS curve; its quotes are annual OIS par rates at the nodes and its Jacobian is their exact inverse
   (`CurveMath.OisQuotes`). These definitions are the synthetic market's own, not any market's conventions.
5. The fake provider values on these curves (log-linear discount factors), returns zero deltas by 1bp node bumps and par
   deltas through the Jacobian. It still projects and discounts on the index's curve and says `simulated`.
6. Only `log_linear_df` base curves are read by qube's own code (the fake provider, SIMM scenarios); any other curve is
   refused on the row, never read as one. A ratio curve is carried and passed to a provider unchanged.
7. A SIMM scenario moves a synthetic curve's zero rates and solves its quotes and Jacobian again with the synthetic
   definitions; a market of another origin is refused under a scenario rather than given the base Jacobian.

## Consequences

- Connecting production means one adapter that fills a snapshot from the delivered curves, quotes and Jacobians.
- Nodes are counted in days from the workspace's selected valuation date, taken as today. A workspace reads the
  market of its own valuation date: when its valuation date changes, the market it reads changes with it (owner,
  2026-11-18), so a curve's days never count from another date. The synthetic market's nodes are tenor-relative and
  are read as of whichever date the workspace has selected.
- The fake provider's figures and risk moved: its curve is now a discount-factor curve, and its par risk is per quote.
