# ADR-0013: Spot CCR capital is SA-CCR's EAD times SA's risk weight, explained to trade level

Status: PROPOSED 2026-11-07. From the owner the same day: in production, putting KVA in the options of the XVA
analytics request makes the engine compute CCR capital too, a spot figure and a capital profile; only the spot figure
needs a drill-down, and it must reach trade level; EAD is SA-CCR and the risk weight is the standardised approach
(SA); "SA-CCR and SA-RW must be implemented in full"; the analytics resolve the risk weight at run time from the
counterparty's information, and the page must show why it resolved that way; a request that computes spot capital
alone will come later and does not exist yet; KVA is shown too. Builds on ADR-0010 §10 (a measure registers into the
pricer page), ADR-0011 (the calculation trace) and docs/08 (provider boundaries). The client's proposal of the
engine's output format is the Claude Doc "ドリルダウン形式の提案：スポットCCRキャピタル（SA-CCR / SA-RW）", which
follows this ADR and ADR-0011; the look was shown to the owner as an interactive sample
(https://claude.ai/artifact/Sij1rR9eh3a89RpzBCivv6, the tree layout the owner chose; the first, drill-in version was
https://claude.ai/artifact/U6XtjAisqPAyT3zSCB1QW6).

## Context

Spot CCR capital is computed over the same portfolio as CVA and FVA: the counterparty, its netting sets and CSAs,
and the trades on Before and After. It is a regulatory figure whose every term has a published definition, so a
change in it can always be explained, but only if the terms are kept: the add-on of one hedging set, the multiplier,
a maturity factor set by an MPOR, a rating chosen among three.

Two facts shape the provider. The engine computes the figure inside the XVA request, not on its own, and its
drill-down output is being built now. The PoC has no engine, so it needs a calculator of its own that is complete,
not a toy, because the owner wants the page and the format proven against the real rules before the engine's
output lands.

## Decision

### 1. The measure (CONFIRMED, owner 2026-11-07, unless marked)

`ccr.spotCapital` is SA-CCR's measure in the page (ADR-0010 §10):

- **K = κ · RWA**, with **RWA = RW · EAD**, where EAD is the counterparty's sum over its netting sets and RW is the
  counterparty's SA risk weight. κ is a configured value, not a constant (DEFAULT 8%; whether it is 8% and where it
  is configured is OPEN, asked 2026-11-07).
- Results: Before, After and Diff in Metrics, opening the trace (ADR-0011 §6); a Price without Before has After
  alone, and its trace is explained on that side (ADR-0011 §4, CONFIRMED, owner 2026-11-07). The capital profile is XVA's Profile
  tab, without a trace (ADR-0010 §11). KVA is XVA's measure and comes from that profile.
- Commands: `measures … ccr` selects it, `explain ccr.spotCapital [path]` opens its trace.

### 2. Who computes it

- **Production**: the XVA engine, with KVA in the request's options. Which request computes a measure is the
  provider's (ADR-0010 §10), so selecting spot capital without KVA still asks the XVA request with KVA on, and
  the page shows KVA only if it is selected. The engine adapter maps the engine's drill-down output onto this ADR's
  kinds (§4). When a request for spot capital alone exists, it is another adapter behind the same capability.
- **PoC**: a reference implementation of SA-CCR and SA-RW in a domain module (`Workbench.Samples.Ccr`), behind the
  fake XVA provider's KVA option and callable as its own capability `ccr.spotCapital` (C12, C27). Its Runs are
  `Simulated`, and its trace says so on every level (ADR-0011 §3). There is never a silent fallback from the engine
  to it.
- Both write the same trace, so the page, the reconciliation and the AI treat them alike, and the reference
  implementation's tests are the cases the engine adapter is checked against.

### 3. SA-CCR, in full (CONFIRMED: in full; the list is the rule's, Basel Framework CRE52 as of 2023-01-01)

- **EAD** = α × (RC + PFE), α = 1.4 (CRE52.1). A margined netting set takes the smaller of its margined and
  unmargined EAD (CRE52.2). A netting set that is not legally enforceable is one netting set per trade.
- **Replacement cost**: unmargined `max(V − C, 0)` (CRE52.10); margined `max(V − C, TH + MTA − NICA, 0)`
  (CRE52.18–19), with each party's threshold and MTA and the CSA's independent amounts. **V** is the trades' market
  value: the official PV for an existing trade (ADR-0010 §6), the provider's own valuation for a new one, each row
  saying which. **C** and **NICA** are the collateral held net of posted, after supervisory haircuts scaled to the
  MPOR, from the collateral balances.
- **PFE** = multiplier × aggregate add-on (CRE52.20), multiplier
  `min{1, F + (1 − F) · exp((V − C) / (2 (1 − F) AddOn))}` with F = 5% (CRE52.22–23); the aggregate add-on is the sum
  of the asset classes' (CRE52.25).
- **Asset classes**: interest rate (hedging set per currency, maturity buckets under 1y, 1–5y and over 5y, and
  `EN = sqrt(D1² + D2² + D3² + 1.4 D1 D2 + 1.4 D2 D3 + 0.6 D1 D3)`, CRE52.56–57); FX (hedging set per currency pair,
  CRE52.58–59); credit and equity (single-factor aggregation over entities and indices, CRE52.60–68); commodity
  (four hedging sets, single-factor within each, CRE52.69–71). Basis hedging sets take half the supervisory factor
  and volatility hedging sets five times it. Supervisory factors, correlations and option volatilities are CRE52.72's
  Table 2.
- **Trade level**: the adjusted notional (CRE52.34–35, the supervisory duration for interest rate and credit), the
  supervisory delta (±1 for a linear trade, the CRE52.40 formula for an option, CRE52.41 for a CDO tranche), and the
  maturity factor, unmargined `sqrt(min(max(M, 10/250), 1))` and margined `1.5 · sqrt(MPOR / 250)` (CRE52.48–53).
- **MPOR** per netting set, derived and explained: 10 business days for daily margining, otherwise 9 plus the
  remargining period, 20 when the netting set has more than 5,000 trades or holds illiquid collateral or a trade
  hard to replace, doubled after more than two margin disputes in the last two quarters (CRE52.50–51).
- A trade is decomposed into its exposures as the rule requires: a cross-currency swap into two interest rate legs and
  an FX exposure, a basis swap into its basis hedging set. Each exposure is a leaf row with its `component`.

### 4. The trace's kinds

The catalogue of kinds, ids and leaf sets is the format proposal's §8 and §9, registered by the domain module for
method `sa-ccr` version `2023-01-01` (ADR-0011 §2). Ids start at the netting set (`NS-002/pfe/addon/ir/USD/en/b3`); a
margined netting set has `/m` and `/u` beneath it for its margined and unmargined figures. Declared depth: trade.
(Implemented 2026-11-08 in `Workbench.Samples.Ccr/SpotCapitalTrace.cs`, which the proposal now follows: each basis's
V, C and NICA beneath its replacement cost, since the haircuts differ by basis; an effective notional term above
every set of rows; and a margined leaf set for each asset class, whose maturity factor reads the MPOR.)

### 5. The risk weight is resolved, and the resolution is part of the trace (CONFIRMED: at run time from the counterparty, and shown)

The risk weight is not an input. It is decided by the provider from the counterparty's attributes, its ratings and
the configuration, and every step of that decision is a term of the trace, with the inputs it read, its reasons and
its citation:

1. **Exposure class** from the counterparty's attributes: sovereign, public sector entity, multilateral development
   bank, bank, securities firm or other financial institution (treated as a bank where it is supervised as one),
   corporate (with SME), or retail.
2. **Approach**: external ratings (ECRA) where the jurisdiction allows them and an eligible rating exists, else, for a
   bank, the standardised credit risk assessment (SCRA, grade A, B or C, and A at 30% for the strongest banks), and
   for a corporate the unrated weight.
3. **Eligible ratings**, and **the rating used**: one is used as it is; of two, the one with the higher weight; of
   three or more, the higher of the two lowest weights (CRE21.9–11).
4. **Short-term treatment** for banks (original maturity three months or less), applied or not, with the reason.
5. **Due diligence**: a weight at least one bucket higher when the bank's own assessment finds more risk than the
   rating implies, never a lower one.
6. **Floors**: under SCRA, a foreign-currency exposure is not weighted below the sovereign of incorporation.
7. **The table lookup**, against the table the provider used, written as a `regulation` parameter.

The values the PoC uses are Basel III's (bank ECRA 20%, 30%, 50%, 100%, 150%; short-term 20%, 20%, 20%, 50%, 150%;
SCRA A 40%, B 75%, C 150%), checked against a jurisdiction's published implementation on 2026-11-07. The paragraph
numbers of CRE20 are confirmed against the current text before the tables are coded, and a national rule's
differences are configuration, not code. (Confirmed on 2026-11-07 against CRE20 as in force from 2023-01-01, for
every class but retail, whose paragraphs were not in the text read and are cited by section until they are; the use
of ratings is CRE21.9–11.) An attribute the decision needs and the counterparty does not carry (its SCRA grade, the
due diligence outcome of a rated bank or corporate, whether a securities firm is supervised as a bank, whether an
unrated corporate is an SME) refuses the measure by name; it is never assumed.

Central counterparties are out of this measure: exposures to a CCP follow CRE54, and a trade cleared through one is
out of `ccr.spotCapital`'s membership with that reason.

### 6. What SA-CCR registers into the page (ADR-0010 §10)

- **Membership**: every derivative in the counterparty's netting sets. A trade the calculator cannot decompose is out
  with its reason, and blocks only this measure.
- **Inputs**: a Counterparty section for the risk weight (institution type, supervision, country of incorporation,
  SME, the SCRA grade's inputs, the due diligence outcome; ratings are source data and read-only); on CSA, the
  independent amounts, the dispute count and illiquid-collateral flag, beside the thresholds and MTAs it shares with
  XVA; the collateral balances held and posted, by type, currency and amount; and the trade fields the decomposition
  needs (start, end, direction, legs, option terms).
- **Trade columns**: the classification the provider decided (asset class, hedging set, bucket, delta sign), read-only.
- **Settings**: κ and the national-rule configuration, shown, not edited in the page (DEFAULT).
- **Validation**: a counterparty without an exposure class, a netting set without its CSA's terms, or a trade without
  the fields its decomposition needs refuses this measure by name, and leaves CVA and FVA standing.

## Consequences

- `Workbench.Samples.Ccr` is new: the calculator, the risk weight resolver, the kinds' registration, and tests
  against the BCBS SA-CCR worked examples (the first netting set's EAD is 569) and against cases built for each rule
  above.
- The synthetic sources gain what §3 and §6 read: trade start, end, direction, legs and option terms; both parties'
  thresholds, MTAs and currencies, independent amounts, frequency, one-way, disputes; collateral balances; the
  counterparty's class, country and ratings. ADR-0010's Consequences lists the CSA fields; they are added once.
- The fake XVA provider's KVA option returns spot capital and its trace from the reference implementation, a capital
  profile, and a KVA from that profile.

## OPEN

- κ: 8%, and where it is configured (asked 2026-11-07).
- Which products come first (asked 2026-11-07: IRS, CCS and FX forwards proposed).
- What the engine's drill-down output returns, and to which depth: the format proposal's questions.
- National rules (the domestic regulation's differences from Basel), as configuration.
- A counterparty in default (CRE20's defaulted exposures, 100% or 150% by specific provisions): the resolver does not
  read default status yet, so whether such a counterparty is weighted or refused is open.
