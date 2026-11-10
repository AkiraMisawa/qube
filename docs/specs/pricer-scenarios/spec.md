# The pricer workbench, delivery step 4: scenarios

Status: ready-for-agent

The decisions are ADR-0010 §4 (scenarios), with its note of 2026-11-09 written for this spec, §3 (cases and the cap),
§7 (the result dock) and §9 (commands), on ADR-0003 (the server owns the rules; the append-only edit log), ADR-0004
(entities the workspace creates), ADR-0005 (commands), ADR-0011 (calculation traces, addressed by case and scenario)
and C32 (saved items shared with a desk or everyone). This spec orders Delivery step 4 and names the seams it is tested
at. The owner asked for steps 2, 4 and 5 on 2026-11-08, step 2 is on main (PR #13), and step 3 (Advanced) comes later.

## Problem Statement

A desk user who wants CVA, FVA, spot IM and spot CCR capital under a curve move or an FX move has nowhere to say it:
the page prices the market of the value date and nothing else. A cross-gamma study of JPY OIS against USDJPY means
dozens of shifted markets, each priced for every case, and today that is one Price per market with the market changed
by hand somewhere outside the page, and figures copied out to compare.

## Solution

A **scenario** is a named set of market-data shifts (ADR-0010 §4). The Scenarios input tab holds one grid of shifts,
`Scenario | Category | Index | Risk factor | Shift type | Size | Description`, where rows sharing a name are one
scenario, typed or pasted many at a time, and beside it the list of scenarios with their shift counts and an include
box. `scenario s1 jpy_ois +10bp; usdjpy -5%` defines one whole from the command line; `scenario ladder` and
`scenario cross` make a set at once. Price prices the base market and each included scenario, crossed with every
case, for every selected measure, in one Run, and the dock's scenario selector shows each. Scenario sets are saved by
name for the user, their desk or everyone, and loaded again. The AI reaches every scenario command as a tool.

## User Stories

1. As a desk user, I want `scenario s1 jpy_ois +10bp; usdjpy -5%` to define the scenario s1 with both shifts at once,
   so that a scenario of several shifts is never built one shift per step.
2. As a desk user, I want a shift's words in any order (`+10bp jpy_ois 5y`), recognised by their form, so that I type
   it as I think it.
3. As a desk user, I want an index, a tenor or a size that does not resolve refused with what it could be, never
   guessed, so that a scenario never shifts a market I did not mean.
4. As a desk user, I want a rate shifted in bp and an FX rate in %, and a size in the other unit refused saying which
   unit the category takes, so that `+10%` on a curve is never read as something else.
5. As a desk user, I want FX shifted as each currency against JPY (`usdjpy`), with the crosses following, so that two
   FX shifts in a scenario never contradict each other.
6. As a desk user, I want a tenor shift to move that point of the curve and fade to nothing at the neighbouring
   tenors, so that `jpy_ois 5y +3bp` is a key-rate shift.
7. As a desk user, I want two shifts that touch one risk factor in one scenario refused at Price, naming them, so
   that a scenario never means two things.
8. As a desk user, I want `scenario s1 …` again to replace s1's shifts and say so, so that I can correct a scenario
   in one line.
9. As a desk user, I want a Scenarios tab with one grid of shifts, where the rows sharing a name are one scenario, so
   that I read every shift on one screen.
10. As a desk user, I want to paste a block of rows copied from a spreadsheet onto the grid's blank rows, so that a set
    built elsewhere comes in at once.
11. As a desk user, I want the Category and the Shift type filled from the index and the size, so that I type only
    what I chose.
12. As a desk user, I want an incomplete row to say what it lacks, on the row, and Price to refuse a scenario that holds
    one by name, so that a half-typed shift is never priced as nothing.
13. As a desk user, I want to clear a row's cells to remove it, so that the grid edits as a spreadsheet does.
14. As a desk user, I want beside the grid the list of scenarios with their shift counts and an include box, so that I
    choose what Price prices without deleting anything.
15. As a desk user, I want `scenario include <name>` and `scenario exclude <name>` to do what the box does, so that the
    keyboard and the AI reach it.
16. As a desk user, I want to duplicate a scenario under a new name, to be edited, so that a variant starts from what
    I have.
17. As a desk user, I want to remove a scenario, or every scenario, from the list and by command, so that I can start
    again.
18. As a desk user, I want my scenarios kept when I change the counterparty, so that one set of markets serves every
    counterparty I look at.
19. As a desk user, I want `scenario ladder jpy_ois -50bp..+50bp step 10bp` to make one scenario per size, the zero
    left out because Price prices the base market anyway, so that a ladder is one command.
20. As a desk user, I want `scenario cross jpy_ois -50bp, 0, +50bp x usdjpy -10%, 0, +10%` to make one scenario per
    combination, named from its shifts (`jpy_ois +50bp · usdjpy -10%`), so that a cross-gamma set is one command.
21. As a desk user, I want the command line to say how many scenarios a generator makes and how many pairs that leaves
    against the cap before I run it, so that I never make a set Price cannot price.
22. As a desk user, I want Price to price the base market and every included scenario, crossed with every case, for
    every selected measure, in one Run, so that three cases and two scenarios are nine pairs in one Price.
23. As a desk user, I want Price to say how many pairs it prices (`Price CVA FVA · 3 cases × 3 markets`), and to refuse
    over the cap saying by how much, so that I know what I asked for.
24. As a desk user, I want CVA, FVA and PV to move with a curve or FX shift, so that a scenario answers its question.
25. As a desk user, I want spot IM and spot CCR capital priced under each scenario too, with their traces, so that
    every measure answers the same question.
26. As a desk user, I want the dock's scenario selector to show the base market first and every scenario the Run
    priced, with Metrics, the collapsed line and the result tabs following it, so that I compare cases under one market
    at a time.
27. As a desk user, I want Copy quote to quote the scenario in view and name it when it is not the base market, so that
    a stressed figure is never sent as the quote.
28. As a desk user, I want a measure that cannot apply a shift refused for that scenario by name, so that a figure is
    never the base market's under a scenario's name.
29. As a desk user, I want the Run to freeze every scenario with its shifts, so that the Pricing DB and Download say
    what each market was.
30. As a desk user, I want `scenario save <set> [desk|all]` to save my scenarios under a name, mine alone unless I say
    my desk or everyone, so that a cross-gamma set built once serves the desk.
31. As a desk user, I want `scenario load <set>` to add a saved set's scenarios, replacing those of the same name and
    saying so, so that loading never quietly mixes two versions of one scenario.
32. As a desk user, I want a set name that two visible sets share refused, naming their owners, so that I never load
    someone else's set by mistake.
33. As a desk user, I want only the owner to replace a saved set, so that a shared set does not change under the desk.
34. As the AI pane, I want every scenario command (define, include, exclude, remove, ladder, cross, save, load) as a
    command capability with the same checks, so that one request ("cross JPY OIS ±50bp with USDJPY ±10%") is one
    generator call, shown in the grid before anything is priced.
35. As a phone user, I want the Scenarios tab among the Inputs and the scenario selector in Results, so that scenarios
    work on a phone.

## Implementation Decisions

- **The catalogue** is the portfolio module's, synthetic (ADR-0010 §4's note of 2026-11-09): the IR indices with their
  currency, kind (discount or funding) and the tenors; the FX factors as each currency against JPY. One reader turns
  a shift as written (tokens in free order, ADR-0008's style) into a resolved shift `{ category, index, tenor?, type,
  size }`, and serves the command line, the grid's cells and the generators, refusing by name with what a token could
  be.
- **A shift is a created entity of the workspace** (ADR-0004), `shift:<id>`, in a scope of its own that no
  counterparty change drops, with the fields `scenario, index, tenor, size, unit, description`; its edits are ordinary
  edits, carried and logged like any other. The inclusion of scenarios is one created entity holding the names left
  out. Defining, duplicating, removing and generating are one batch each, applied whole or refused whole.
- **The scenarios' reading** gives the rows in creation order with their derived Category and Shift type and each row's
  issue; the scenarios in order of their first row, with their shift counts and whether each is included. The
  workspace view carries both, and the tab says how many scenarios it holds.
- **The accepted input holds the scenarios** at its top, the base market first: `{ id, title, shifts }`, ids positional
  in the Run (`base`, `s1`, `s2`, …). The portfolio's case reader crosses them: each priced case carries its scenario's
  id, title and shifts, so a provider loops over pairs with its body unchanged and writes each row and trace entry with
  both. The measure result gains `scenario` and `scenarioTitle`.
- **Price** checks the included scenarios before it freezes: a row with an issue, or two shifts touching one risk
  factor, refuse it by name (`ScenarioInvalid`, `RiskFactorShiftedTwice`). The cap counts pairs.
- **The shifted market is one portfolio helper**, read by every provider: the FX of the value date with the FX shifts,
  and a curve's shift in bp at a point in years. The simulated XVA provider revalues a trade's PV by a toy PV01 at its
  maturity on its curve and its FX, moves its toy exposure with that PV, and moves FVA's toy funding spread with its
  CSA's funding curve. Spot CCR capital takes the same revalued PVs and the shifted FX. Spot IM shifts its snapshot's
  pillars and its pinned FX, and generates its CRIF again.
- **The page**: the Scenarios tab (a grid of shifts ending in blank rows, ExGrid's spilled paste declared, and the list
  beside it); the dock's scenario selector, beside the case selector; the result tabs' trace address takes the scenario
  in view. The command line's `scenario` commands read their text on the server, which answers what it made.
- **Saved sets** are items of the kernel's generic saved-item store (C32; memory, and SQLite where the workspaces
  are), of the kind `portfolio.scenarioSet`, holding a set's name, owner, visibility, desk, saved time and scenarios;
  the store decides who sees an item and never reads it (ADR-0010 §4's note). Desks are configuration by user. The page completes `scenario
  load` from the sets the user can see.
- **Commands** (ADR-0005, ADR-0006): `scenario <name> <shifts>`, `scenario include|exclude|remove <name>`, `scenario
  duplicate <name> <new name>`, `scenarios clear`, `scenario ladder …`, `scenario cross …`, `scenario save <set>
  [desk|all]`, `scenario load <set>`, and `open scenarios`. Every one that changes the workspace or saves a set is a
  command capability, reached by the AI through the command gateway with origin `ai`.

## Testing Decisions

A good test drives a public seam and asserts what a user or a caller sees: the scenarios made and refused by name, each
pair's figures, what a Run froze, which sets a user sees. It never asserts how a shift is stored. Expected figures are
computed from the synthetic sources: a scenario's figures differ from the base market's in the direction the shift
says, and a scenario of no effective shift (an index no trade is on) equals the base market. Each test names the ADR
section it holds.

The seams, the same four as steps 1 and 2 (DEFAULT, per the owner's standing preference of 2026-11-08):

1. **The portfolio workspace service with the registered measures** (xUnit, prior art `PricerCaseTests`): the shift
   reader, the scenario commands and generators, the readings, the accepted input crossing cases with scenarios, each
   provider's rows per pair, the cap and the refusals, and the saved sets' visibility.
2. **The HTTP API through the in-process host** (xUnit with `QubeFactory`, prior art `PricerApiTests` and
   `PricerCommandTests`): the scenario endpoints, Price to a Run with a row per pair, the trace of a pair, and the
   command capabilities through the command gateway.
3. **The client's pure functions** (Vitest, prior art `commands.test.ts`, `metrics.test.ts`): the generators' preview,
   the paste's rows, the Metrics rows of the scenario in view.
4. **The page in a browser** (Playwright, prior art `xva.spec.ts`), driven by keyboard and read once settled: a
   scenario defined from the command line and priced, the scenario selector, a block pasted onto the grid, the include
   box, a generator, and the phone layout.

## Out of Scope

- Credit shifts (the counterparty's CDS spreads): with the Credit tab, Delivery step 5.
- Relative rate shifts and absolute FX shifts: refused by name until a provider needs one.
- A scenario that applies to one side only, or to one case only: scenarios cross every case (§4); side-only changes are
  Delivery step 3.
- Shifts of anything a provider does not read here (volatilities, inflation, basis spreads as their own factor).
- Administrators replacing another user's saved set: the PoC has no roles yet (C32 waits for them).

## Further Notes

- The synthetic market's pillars and the catalogue's tenors happen to agree; a tenor shift applies through the key-rate
  function at whatever points a provider's curve has, so neither has to follow the other.
