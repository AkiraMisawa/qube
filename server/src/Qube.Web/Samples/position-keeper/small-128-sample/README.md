# Position Keeper sample result set (development and browser tests)

A cut of the engine's synthetic `small-128` test vector (alpha at the commit `manifest.json` names), for the
development host and `client/e2e/position-keeper.spec.ts`. Every figure is synthetic.

It is not a whole result set: `risk.csv` keeps the base scenario's rows of six kinds only (`zero_rate`,
`hazard_rate`, `fx_spot`, `fx_atm_vol`, `recovery`, `funding_spread`). `xva.csv`, `trades.csv` and `curves/` are as
the engine wrote them. `recovery` and `funding_spread` are left without a declared unit on purpose, so the page shows
a kind no move can be typed for.

`levels.csv` is not the engine's output: it is the level of each FX spot and each FX ATM vol in the synthetic input
set the vector was computed from (the engine's `data/synthetic/small/market`, at the same commit), keyed as
`risk.csv` keys them, with the vol expiries matched to the keys in order. It is what a move in `%` is taken of. No
level is given for a rate, so a move in `%` of a curve is refused here, by the key.

The whole vector, uncut, is the contract fixture under
`server/tests/Qube.Composition.Tests/Fixtures/position-keeper/alpha-small-128`.
