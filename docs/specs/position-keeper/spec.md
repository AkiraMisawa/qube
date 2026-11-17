# The Position Keeper's first slice: a position keeper, one what-if hedge and a predicted profit and loss

Status: ready-for-agent

The decisions are [ADR-0019](../../adr/0019-the-position-keeper-keeps-positions-read-from-an-engine-and-what-if-hedges-are-rows-the-user-creates.md), accepted by the owner on 2026-11-17, and the ADRs it builds on: ADR-0003 and
ADR-0004 (the log, created entities), ADR-0005 and ADR-0016 (commands and registered tables), ADR-0009 and
`docs/design-guidelines.md` (the look) and docs/08 (providers). This spec orders ADR-0019 §7 and names the seams of
§8. It decides nothing; where a ticket meets a question the ADRs do not answer, the ticket stops and the ADR is
written first.

## Problem Statement

A pk user who manages XVA has the engine's files for the book (values, sensitivities, trades) and nothing that
shows them as positions. To see what a hedge would do, the user adds numbers by hand, in a sheet where a hypothetical
trade looks the same as a real one and a sign is easy to turn over.

## Solution

A Position Keeper page in Qube. It reads one result set of the engine's small synthetic portfolio and shows the book per
counterparty and in total. The user adds a hypothetical interest rate swap and sees Book, What-if and Net side by
side; enters market moves and sees the predicted profit and loss of each. Everything hypothetical is marked
`WHAT-IF`, everything not computed by the engine is marked `SIMULATED`, and no figure is shown without saying which
result set and provider it came from.

## User Stories

1. As a pk user, I open the Position Keeper and see, per counterparty and in total, CVA, FVA and ColVA with their standard
   errors, as negative numbers where they are costs.
2. As a pk user, I see which result set I am looking at: the engine's commit, the input set and the valuation date.
3. As a pk user, I see the first-order sensitivities of the selected counterparties by kind, input and key, and
   the notional FX delta per currency.
4. As a pk user, I add a hypothetical swap by typing its terms on the command line, and it appears as a row marked
   `WHAT-IF`; I remove it, and undo either.
5. As a pk user, I see Book, What-if and Net for each sensitivity, with What-if and Net marked `SIMULATED` while
   the hedge's risk comes from the synthetic calculator.
6. As a pk user, I enter a move for an input (a curve in basis points, an FX spot in percent) and see the predicted
   profit and loss of Book, What-if and Net, labelled first order.
7. As a pk user, I cannot enter a move for a kind whose unit the result set does not declare, and I am told why.
8. As a pk user, when no result set is configured or it cannot be read, the page says so by name and shows no
   positions.
9. As a pk user, I ask the AI what the net JPY curve risk is after my hedge, and it answers from the same tables
   through the same commands.
10. As a pk user, I use the page with the keyboard alone, at laptop and phone widths, in the light and dark themes.

## Implementation Decisions

- **Module.** `Qube.Samples.PositionKeeper` (ADR-0019 §5), registered like the other sample modules; an application `pk`
  with its page under `client/src/apps/position-keeper`.
- **Results port.** `IPkResults` answers a result set by its reference: the manifest (engine commit, input set,
  valuation date, counterparties with their legal entities, the unit of each sensitivity kind) and the rows of
  values, sensitivities and trades. One adapter reads a directory of the engine's files (`xva.csv`, `risk.csv`,
  `trades.csv`) and a manifest beside them. Only the base scenario's rows are the book in this slice; other
  scenarios' rows are read and not shown. A file that is missing, a column that is not there, a number that does not
  parse and a manifest without the three identifying fields are each refused by name (ADR-0019 §4).
- **Workspace.** `pk.workspace.create` makes a Position Keeper workspace on a result set: its acquired source version is the
  result set's reference and content hash. The book's rows are read-only tables registered with the kernel
  (ADR-0016): `pk.values`, `pk.risk`.
- **Hedges.** `pk.hedges` is a table of created entities (ADR-0004), made with the kernel's `entity.create` from
  short terms (ADR-0008) and ended with `Remove`. The slice registers one product, a fixed-float interest rate
  swap.
- **Hedge risk port.** `IHedgeRisk` answers a hedge's sensitivities on the book's inputs and keys, with its
  provider and execution mode. The slice's implementation is the synthetic calculator, `Simulated` (ADR-0019 §7).
  A hedge it cannot answer for (a currency whose curve the result set does not hold) is refused by name.
- **Net and prediction.** Pure functions in the module: Net is Book plus What-if on matching kind, input and key;
  the predicted profit and loss is the sum of sensitivity times move over the inputs that have a move. Moves are
  workspace fields (`pk.moves`), so they are in the log and undo reaches them. Read through one command,
  `pk.position.read` (`ReadWorkspace`), which returns each figure with its marks.
- **Marks.** A figure's marks (`WHAT-IF`, `SIMULATED`, `first order`) are computed on the server and travel with
  the figure; the client draws them and never infers them.
- **No Run** is made by this slice, and no engine process is started (ADR-0019 §3 and §7).
- **Signs** are the engine's, untouched (ADR-0019 §9).

## Testing Decisions

The seams are ADR-0019 §8's, confirmed by the owner.

- Command-layer tests in `Qube.Composition.Tests`, with `IHedgeRisk` a fake and `IPkResults` a small in-memory
  set: the stories above as behaviour, each test naming ADR-0019.
- A contract test reads the engine's own small result files, copied into the test project with the engine commit
  they were taken from, through the files adapter.
- `client/e2e/position-keeper.spec.ts`: the marks on every row and total they apply to, the page with no result set, keyboard
  entry of a hedge, and the two themes and a phone width.

## Out of Scope

Recomputation through the engine and its Run; the explanation of profit and loss (actual, assign, predict by
factor); second-order risk; cash, the official profit and loss and bid-ask assumptions; the intraday estimate;
hedge suggestions by the AI; intents, forecasts and judgments; real positions and real market data; sending a trade
to the pricer page.
