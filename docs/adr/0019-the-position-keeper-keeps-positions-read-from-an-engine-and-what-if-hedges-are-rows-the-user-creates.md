# ADR-0019: The Position Keeper page keeps positions read from an engine, and what-if hedges are rows the user creates

Status: ACCEPTED 2026-11-17 by the owner (§1 to §8 are his answers D1 to D8 of that day, each "yes" to the
recommendation put to him; what is marked DEFAULT is this ADR's proposal and may be changed with a reason). Builds on
ADR-0003 and ADR-0004 (the edit log, created entities), ADR-0005 and ADR-0016 (commands), ADR-0007 (the AI calls
commands), ADR-0010 (the pricer page, which this ADR does not change) and docs/08 (provider boundaries). The
requirements behind it are the owner's "XVA pk simulator" requirements, kept with the engine
(`docs/reports/xva-pk-simulator-requirements.html` in the engine's repository, in Japanese; its questions Q1 to Q22
were answered on 2026-11-16 and 2026-11-17).

## Context

The pricer page (ADR-0010) answers "what does this trade cost against this counterparty": one counterparty, Before
and After, a Price that is a Run. A pk that manages XVA asks a different question every day: what is the whole
book's XVA and risk now, what would a hedge do to it, and what did yesterday's profit and loss come from. That is a
position keeper over every counterparty, with hypothetical hedges laid over the real book, and it is the first piece
of a tool meant for real use on real positions, with training done on the same screens.

The calculation is not Qube's. An engine kept in a repository of its own (C++ and C#, called `alpha` here) computes
CVA, FVA and ColVA per counterparty with first-order risk by adjoint differentiation, and is specified to add the
explanation of profit and loss (assign and predict). It writes files: values, sensitivities, exposures, trades. The
Web server must not load it (CLAUDE.md).

## Decision

### 1. A page of its own (CONFIRMED, D1)

The Position Keeper is its own page and application, not a tab of the pricer page. It shares the portfolio module's tables of
counterparties, netting sets and CSAs where it shows the same things, and a trade under consideration is sent to
the pricer page for its pre-trade Price; the Position Keeper does not grow a second pre-trade workflow. Its screens are in
English (CONFIRMED, requirements Q1).

### 2. The real book is an acquired source; a what-if hedge is a created entity (CONFIRMED, D2)

A Position Keeper workspace has one acquired source version: the book's results as the engine wrote them, for one valuation
date. A hypothetical hedge is an entity the user creates in the workspace (`Create`, ADR-0004), removed with
`Remove`, and every such row is in the one edit log (ADR-0003 §6). There is no second store for hypothetical
positions, and no edit ever changes an acquired row: the book is read, never booked into.

A row the user created is marked `WHAT-IF` wherever it or a figure that includes it is shown, and a total that
includes one says so. This marking is an acceptance requirement, not a style.

### 3. What is a Run (CONFIRMED, D3)

- A **Run** is made only when the user asks for a recomputation (the book with its what-if rows through the engine).
  One request is one Run, whatever the number of counterparties and engine calls inside it.
- An **intraday estimate** (yesterday's risk against today's market moves) is a subscription (ADR-0005), never a Run
  per tick.
- The **official nightly results** are computed outside Qube. The Position Keeper reads them as an acquired source with the
  identity the batch gives them; it does not submit them and does not claim their lineage as its own.

A figure computed from sensitivities inside Qube (a predicted profit and loss, §7) is neither: it is a pure
function of the workspace and the acquired version, recomputed on read, and labelled as a first-order prediction.

### 4. The engine is a separate worker, pinned, and never silently replaced (CONFIRMED, D4)

The engine is reached through a port of the Position Keeper module. Its adapter runs the engine as a separate process, at a
commit pinned in the flake; the Web server loads no engine library. Every figure carries the provider that produced
it. A provider that is not the engine is `Simulated` and its figures show `SIMULATED`; when the engine cannot be
reached the request is refused or the figure shows as unavailable, and nothing falls back to a simulated provider.

Reading result files the engine already wrote is a second adapter behind the same module (the results port, §7).
A result set states the engine's commit, the input set it was computed from and its valuation date; a set that does
not state them is refused.

### 5. The module (CONFIRMED, D5)

Everything the Position Keeper knows is in a domain module, `Qube.Samples.PositionKeeper`, beside the portfolio and XVA modules. The
composition kernel gains nothing that names a position, a hedge, a sensitivity or a profit and loss.

### 6. Intents, forecasts and judgments (CONFIRMED, D6)

A hedge decision is kept with why it was made (the intent), what it was expected to do (the forecast, frozen when
made), and how it turned out (the judgment, written against later actual figures). These are stored per user in
Qube's persistence, as records of the Position Keeper module. They are not part of the first slice.

### 7. The first slice (CONFIRMED, D7; the detail DEFAULT)

Synthetic data only.

1. **Position keeper.** A Position Keeper workspace acquires one result set of the engine's small synthetic portfolio and shows,
   per counterparty and in total, CVA, FVA and ColVA with their standard errors, and the first-order sensitivities
   by kind, input and key, including the notional FX delta per currency.
2. **One what-if hedge column.** The user creates a hypothetical interest rate swap (currency, notional, direction,
   maturity, fixed rate). The grid shows Book, What-if and Net for each sensitivity.
3. **Predicted profit and loss.** The user enters market moves; the page shows sensitivity times move for Book,
   What-if and Net. First order only, and it says so.

DEFAULT, for this slice:

- The result set is read from files (the results port). The first slice makes no Run and starts no engine process;
  the worker adapter of §4 arrives with the first recomputation.
- The hedge's own sensitivities come through an engine port (`hedge risk`: the trade's value sensitivities on the
  same inputs and keys as the book's). The engine does not yet answer that question for a trade outside a netting
  set, so the first slice's implementation is a synthetic calculator in the Position Keeper module, `Simulated`, and every
  What-if and Net figure shows `SIMULATED`. It is replaced by the engine adapter without a change to the page.
- A sensitivity's unit (per what move of what quantity) is declared by the result set's adapter for each kind. A
  move can be entered only for a kind whose unit is declared; other kinds are shown and take no move. Nothing
  assumes a unit.
- A counterparty is the engine's code together with its legal entity (CLAUDE.md). The synthetic result set carries
  the mapping; a counterparty in the results that the mapping does not hold is shown as unmapped by its code, and
  its legal entity is not guessed.

### 8. Where it is tested (CONFIRMED, D8)

- At the **command layer**, with the engine port replaced by a fake: the workspace, the created hedge, Book, What-if
  and Net, and the predicted profit and loss.
- A **contract test** reads the engine's own small result files (copied with the engine's commit recorded) through
  the results adapter, so a change of the engine's output shape fails here and not on the page.
- A **browser test** holds that `WHAT-IF` and `SIMULATED` appear on every row and total they apply to.

### 9. Signs (CONFIRMED, owner 2026-11-16, the engine's ADR 0010)

The Position Keeper shows the engine's convention unchanged: a cost is negative (CVA, FVA and ColVA as adjustments to value),
a profit and loss is the change of the value, and a sensitivity is the derivative of the adjustment. The Position Keeper never
flips a sign for display. The pricer page's convention is not changed by this ADR.

## Consequences

- The Position Keeper's later pieces each need the engine or another source first, and are separate slices: recomputation
  through the worker (the first Run), the explanation of profit and loss (actual, assign, predict) read from the
  engine's output, cash and the official profit and loss read from product control's figures, bid-ask assumptions,
  the intraday estimate, hedge suggestions by the AI through commands (ADR-0007), and §6's records.
- The AI needs no knowledge of the engine: it reads the Position Keeper's tables and calls its commands, and what a column means
  (sign, unit) is declared by the module, generated from the engine's documents at the pinned commit.
- Two repositories now move together. The contract test's copied files name the engine commit they came from, and
  moving the pin is a change that runs that test.

## Rejected

- **A tab of the pricer page.** One counterparty with Before and After is that page's shape (ADR-0010 §1 to §3); a
  book of every counterparty with layered hypothetical positions is another, and forcing it in would make every
  Position Keeper refresh look like a Price.
- **A store of its own for hypothetical positions.** A second edit store, against ADR-0003 §6.
- **A Run per refresh or per tick.** Against "one RunId is one business submission".
- **Loading the engine's C# version into the Web server.** It is managed code, but it is the quant library, and the
  rule is about what the Web process may host, not about the language.
- **A simulated book when no result set is configured.** A page with invented positions that looks like the book is
  the fallback CLAUDE.md forbids; the page says no result set is configured.

## OPEN

- Whether the engine will compute a hypothetical hedge's own risk (and so retire the synthetic calculator of §7),
  or a pricing library will.
- The identity of an official nightly result set (§3), asked when that source is reached.
- Whether the pricer page should show costs as negative too (§9).
