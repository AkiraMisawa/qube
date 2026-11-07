# ADR-0012: Spot initial margin is SIMM's measure, per IM CSA and regime, traced to the CRIF line

Status: PROPOSED 2026-11-07. Decided with the owner question by question on 2026-11-07, in the thread that started
as "spot SIMM and spot CCR capital pages". Each decision below says whether the owner confirmed it or it is a default
for review. The owner asked for a drill-down sample and agreement on the overall design before anything is built:
the sample is in the project's files (`drilldown-samples/spot-simm-drilldown.html`). Builds on ADR-0010 (the pricer
page and §10, measures that register into it), ADR-0011 (the calculation trace) and ADR-0003 (the server owns the
rules). Settles the CRIF / IM row of docs/01 and P3's "independent CRIF generation and SIMM calculation" (docs/12).

## Context

The owner asked for a spot SIMM page and a spot CCR capital page, then observed that both share almost all of the
XVA pricer's configuration and are portfolio calculations too. The owner chose one page whose Price selects the
measures (ADR-0010 §10). Spot CCR capital is ADR-0013's.

What the owner said about initial margin (2026-11-07):

- In production, the XVA analytics request computes spot SIMM, and a SIMM profile, when its MVA option is on. No
  request computes spot SIMM alone yet.
- SIMM is a regulatory calculation. Its drill-down must show each term of the formula with its value, typeset as
  real formulas, down to trade level. Only the spot figure is drilled down, not the profile.
- MVA with SIMM is the next XVA measure (docs/12 P2, P3), so what is built here is what MVA's time-0 IM uses.

## Decision

### 1. What spot IM is (CONFIRMED, owner 2026-11-07)

- **Spot** IM is the ISDA SIMM initial margin as of the value date, from that date's sensitivities (CRIF). It is not
  the projected IM profile that MVA integrates.
- **Both directions are shown.** IM-R is what the counterparty posts to us, under the IM CSA in a trade's IM-R
  column. IM-P is what we post, under its IM-P CSA. The two can differ, because each direction counts the regimes of
  its own side.
- **Per regime, the largest governs.** In each direction, SIMM is computed once for each regulatory regime that the
  CRIF lines name (`CollectRegulations` for IM-R, `PostRegulations` for IM-P). The direction's IM is the largest of
  them, and the governing regime is named wherever the figure is shown.
- **No IM threshold is applied.** The figure is SIMM before any threshold. The regulatory threshold applies across a
  group of entities, so subtracting it on a page about one counterparty would mislead.
- Before and After are independent, per case and per scenario, as every measure's are (ADR-0010 §3, §4). A Price
  that leaves Before out has After alone, and spot IM is then complete on that one side (§6, §7).

### 2. Membership: which trades count (CONFIRMED; the scope reasons are DEFAULT)

Spot IM declares named scopes, one per direction and regime (ADR-0010 §10). A trade is in a scope when two things
hold: its IM-R or IM-P column holds an IM CSA, and its CRIF lines name the regime. Otherwise it is out, with its
reason. Two examples: a physically settled FX forward is exempt in every scope, and a trade dated before the
counterparty came into a regime's scope is out of that regime only. The trade grids show the scopes compactly
(`R: JFSA, CFTC · P: JFSA`).

- A **new trade** takes the counterparty's IM CSAs by default, as it takes the main CSA (docs/03). The user can change
  them in the trade grid; that is an ordinary edit in the Common layer (ADR-0010 §2).
- Which regimes a trade is in is the source's and the engine's to say, never the page's. A trade nobody classified
  is out, with that reason.

### 3. Inputs

- **IM tab** (DEFAULT): the counterparty's IM CSAs, with their ID, direction, the regimes they serve, the calculation
  currency and the SIMM version. They are shown while spot IM is selected (ADR-0010 §10) and are read-only until a
  what-if on them is asked for.
- **Trade columns**: IM-R and IM-P (editable) and the IM scope (read-only, §2).
- **T-1 CRIF is not in this app** (CONFIRMED, owner 2026-11-07). It has no switch for it.

### 4. Capabilities, requests and providers (CONFIRMED, owner 2026-11-07)

- **Two capabilities, each callable alone** (C12, AI03), from the command line, the AI and workflows, without the
  page:
  - `risk.crif.generate`: frozen trade inputs and pinned market data in, a CRIF artifact out;
  - `margin.simm.calculate`: a CRIF, the IM CSAs and a methodology version in; spot SIMM per direction and regime
    out, with its trace (§6).
- **Which request computes them is the provider's.** The production adapter sends the XVA request with MVA on and
  takes spot SIMM from its answer. The PoC builds a request for SIMM alone, which production does not have yet. A
  real provider never falls back to a simulated one.
- One Price is one Run whatever requests it makes (ADR-0010 §10). Spot IM's figures show as soon as its request
  answers, and a failed request leaves only spot IM `unavailable`.

### 5. The PoC's reference engine (CONFIRMED, owner 2026-11-07, unless marked)

- It implements the aggregation of ISDA SIMM v2.x in full: from risk factor to bucket, to margin, to risk class, to
  product class, to the total, then the worst of the regimes. Concentration factors, bucket sums bounded by `±K_b`
  and the correlations at every level are included.
- **Its parameters are made up and say so.** Risk weights, correlations and concentration thresholds come from a
  versioned parameter table labelled `DEMO`, and every figure computed with them shows `SIMM PARAMETERS DEMO`.
  ISDA's calibration is licensed. It can be loaded from a file kept outside the repository, and it is never
  committed.
- **Risks in the PoC**: IR delta (per currency, the 12 tenors, the sub-curve), FX delta and cross-currency basis.
  Vega, curvature and base correlation exist as kinds (§6). They stay absent until a product with options arrives.
- **CRIF for the synthetic trades** (DEFAULT): IRS, CCS and FX forwards, from the Rates module's pricing at the value
  date. The FX forward is physically settled, so it is exempt in every scope.

### 6. The drill-down (CONFIRMED, owner 2026-11-07, unless marked)

The trace is ADR-0011's, and spot IM registers its kinds there. A kind carries its formula, as an evaluation tree and
a TeX template, once per methodology version. The engine sends only the values. The workbench reconciles each term
against its kind, and a term the engine did not return says so.

| Kind | Term | Formula |
|---|---|---|
| `simm.im` | IM of one direction | `\mathrm{IM} = \max_g \mathrm{SIMM}_g`, the governing regime marked |
| `simm.regime` | SIMM under one regime | `\sum_p \mathrm{SIMM}_p + \mathrm{AddOn}` |
| `simm.addon` | Add-ons | product class multiplier, notional factor, fixed amount |
| `simm.product` | Product class | `\sqrt{\sum_r \mathrm{IM}_r^2 + \sum_{r \ne s} \psi_{rs} \mathrm{IM}_r \mathrm{IM}_s}` |
| `simm.riskClass` | Risk class | `\mathrm{DM} + \mathrm{VM} + \mathrm{CM} + \mathrm{BCM}` |
| `simm.delta`, `simm.vega` | Delta or vega margin | `\sqrt{\sum_b K_b^2 + \sum_{b \ne c} \gamma_{bc} g_{bc} S_b S_c}`, plus the residual bucket |
| `simm.curvature` | Curvature margin | `\max(\sum \mathrm{CVR} + \lambda \sqrt{\dots}, 0)`, with θ and λ |
| `simm.bucket` | Bucket | `K_b`, `S_b = \max(\min(\sum WS_k, K_b), -K_b)` with the active bound marked, `CR_b` |
| `simm.factor` | Risk factor | `WS_k = RW_k \, s_k \, CR`, `s_k = \sum \mathrm{AmountUSD}_i` |
| leaf | CRIF line | the line as the engine received it, linked to its trade |

- **Literal terms and Euler allocation, both** (CONFIRMED). The formula shows the regulation's own terms, which do not
  add up, because correlations add cross terms. Beside them, every term shows its contribution to the regime's SIMM.
  The contributions of a term's children add up to the term's own contribution. At any term, a trade's contribution
  is the sum of its lines' contributions; this answers "which trades drive this bucket".
- **The risk weight says why** it is what it is: the currency's volatility group, then the tenor, then the table
  row.
- **Before, After and Diff** at every term, paired by the term's path (ADR-0011 §4). A term on one side only has the
  other side's value as its Diff, with its sign, and is marked `New` or `Gone`. An absent term is never drawn as 0.
- **After only** (CONFIRMED, owner 2026-11-07: priced without Before there is nothing to compare, and the
  drill-down must still work in full). A Run priced without Before has a trace with one side. The drill-down
  then draws no Before and no Diff at all, rather than empty columns or `new` on every term, and says once that
  Before was not priced. Everything else works as it does with both sides: every term opens to its formula with
  After's values, its contribution to the regime's SIMM, its reconciliation, and the trades and CRIF lines under
  it; "largest first" orders by contribution instead of by change; `explain` answers the same. The sample has a
  switch that shows this case.
- **Formulas are typeset** (CONFIRMED). The symbolic line comes first, then each side with its values substituted.
  A value inside the formula opens its term.
- **`explain simm <direction> <regime> <path…>`** opens a term from the command line, and the AI reads the same
  trace as data (docs/09), not the screen.
- **Layout: the whole tree beside the formula** (CONFIRMED, owner 2026-11-07, for SIMM and CCR alike). On a desktop,
  every level of the term tree can be open at once, with the selected term's formula beside it and the trades and
  CRIF lines under it, as the sample does. On a phone, it drills in one level at a time. The alternative, drilling in
  one level at a time everywhere, was not taken. SIMM is seven levels deep, and finding where a Diff came from means
  comparing a path and its siblings at once.
- **The tree is an ExGrid** (DEFAULT, 2026-11-07, told to the owner). The term column is a pinned Template Column
  (ex-grid ADR-0020; `templateColumn` in `@ex-grid/react`) whose cell carries the indent and the open / close
  button, as ExPivot draws its Compact form (ex-grid ADR-0063). The workbench holds which terms are open and gives
  the grid the rows that are showing; the grid neither holds nor orders the tree. ExGrid needs no change. *(The
  first draft of this ADR said ExGrid had no way to draw a row toggle and indentation, and put an ex-grid ADR to
  the owner for them. That was wrong: ExPivot's Compact form already does it this way, and the question was
  withdrawn.)* A tree-grid is the common form for this: Excel's row outline and PivotTable, WAI-ARIA's `treegrid`.

### 7. Results on the page

- **Metrics**: IM-R and IM-P, each Before, After and Diff in the display currency, or After alone when Before was
  not priced, with the governing regime. SIMM is computed in its calculation currency (USD) and converted at the
  workspace's pinned FX, which the Run records.
- **The SIMM tab**: the two directions' summaries, then the drill-down (§6).
- **The SIMM profile** that MVA returns is shown in XVA's Profile tab, with no trace (ADR-0010 §11).

### 8. Reuse by MVA

MVA's time-0 IM is this spot IM. The IM CSAs, the CRIF artifact, the SIMM kinds and the reference engine are shared:
MVA adds only its own figures and its profile (ADR-0010 §11). A drill-down of MVA's time-0 IM, if one is ever wanted,
is this one.

## Consequences

- A portfolio domain module (ADR-0010 §10) gains the IM CSAs and the trades' IM-R and IM-P assignments. A new SIMM
  module registers the measure, the two capabilities, the kinds and the scopes. The kernel learns no SIMM term.
- The synthetic sources gain IM CSAs, regimes, phase-in dates and IM-exempt trades.
- The Pricing DB's history projection for spot IM lists IM-R and IM-P Before, After and Diff (After alone when
  Before was not priced), with the governing regime.

### Delivery order (DEFAULT)

1. `risk.crif.generate` and `margin.simm.calculate` with the reference engine and its DEMO table, callable from the
   command line and the AI, with their trace. This step does not need the page.
2. Spot IM registered into the pricer page (ADR-0010's delivery order step 1 is its prerequisite): membership,
   columns, the IM tab, Metrics.
3. The SIMM tab's drill-down, in the layout the owner picks.

## OPEN

- Whether IM CSAs can be edited as a what-if (for example, adding a regime).
- The synthetic phase-in dates and exemptions are illustrative. Company data decides them in production.
