# Desk sample result set (development and browser tests)

A cut of the engine's synthetic `small-128` test vector (alpha at the commit `manifest.json` names), for the
development host and `client/e2e/desk.spec.ts`. Every figure is synthetic.

It is not a whole result set: `risk.csv` keeps the base scenario's rows of six kinds only (`zero_rate`,
`hazard_rate`, `fx_spot`, `fx_atm_vol`, `recovery`, `funding_spread`). `xva.csv`, `trades.csv` and `curves/` are as
the engine wrote them. `recovery` and `funding_spread` are left without a declared unit on purpose, so the page shows
a kind no move can be typed for.

The whole vector, uncut, is the contract fixture under
`server/tests/Qube.Composition.Tests/Fixtures/desk/alpha-small-128`.
