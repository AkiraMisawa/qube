# The pricer workbench, delivery step 2: cases

Status: ready-for-agent

The decisions are ADR-0010 §3 (cases), §7 (the result dock), §9 (commands) and §10 (the measures register), with
their notes of 2026-11-08 written for this spec, on ADR-0003 (the server owns the rules; the append-only edit log),
ADR-0005 (commands) and docs/03 §5 (individual-addition iterative pricing). This spec orders Delivery step 2 and names
the seams it is tested at. The owner asked for steps 2, 4 and 5 on 2026-11-08 with step 3 (Advanced) later, so every
case's change here applies to both of its sides.

## Problem Statement

A desk user who wants to know what a threshold of 10mm, 5mm or nothing would do to CVA and FVA has to change the
threshold, Price, note the figures, and do it again for each value, then compare the figures by hand. Quoting three new
trades one at a time, each against the existing portfolio, means three Prices with the other two trades unticked each
time, and nothing on screen says which Price was which. Changes made across wide grids are found only by scanning
the cells for their marks.

## Solution

A **case** is the workspace plus its own changes, priced as one Before/After pair (ADR-0010 §3). `vary CSA-A threshold
10mm, 5mm, 0`, or *Vary values…* on the cell, makes one case per value; `add trades` makes one case per new trade with
A ticked, each holding that trade alone. Price prices the Base and every case, for every selected measure, in one Run,
and the Metrics dock shows a row per case. An input tab, `Changes`, lists the Common changes and each case's own,
where each is edited or removed. `set` changes one value from the command line without making a case.

## User Stories

1. As a desk user, I want `vary CSA-A threshold 10mm, 5mm, 0` to make one case per value, named from what it changed
   (`CSA-A threshold 5mm`), so that one Price answers all three.
2. As a desk user, I want *Vary values…* on any editable cell of the input tabs or the trade grids to take a list and
   make the same cases, so that I do not have to know the command.
3. As a desk user, I want the workspace itself priced as the `Base` case on the first row, so that every case is read
   against what I have now.
4. As a desk user, I want a value equal to the one the workspace holds to make no case, and `vary` to say so, so that
   the Base is not priced twice.
5. As a desk user, I want a second `vary` to add its cases beside the first set's, so that I can compare a threshold
   change and a curve change in one Price.
6. As a desk user, I want a case identical to one already made not made again, and told so, so that the dock never
   holds two rows meaning the same.
7. As a desk user, I want `add trades` to make one case per new trade with A ticked, each case holding that trade alone
   on top of the existing portfolio, never the trades before it in the list, so that I see each trade's own effect
   (docs/03 §5: not cumulative).
8. As a desk user, I want a new trade that is in Before refused by name when I make that set, never unticked for me,
   so that I know why it cannot be priced alone.
9. As a desk user, I want Price refused by name when the New rows with A ticked no longer match the set I made, so that
   the trades priced alone are never quietly another set.
10. As a desk user, I want a common change to the existing trades said in every case of the set, so that I know each
    case carries it.
11. As a desk user, I want Price to say how many cases it prices (`Price CVA FVA · 4 cases`), so that I know before
    I press it.
12. As a desk user, I want Price refused when the cases are over the cap (50), saying by how much, so that I am not
    left waiting for a Run nobody can read.
13. As a desk user, I want Metrics to show a row per case with each selected measure's Before, After and Diff, a failed
    case `unavailable` and never 0, so that the cases are compared on one screen.
14. As a desk user, I want the collapsed dock to show one line per case with each measure's headline Diff, so that I
    keep the comparison while the trades get the height.
15. As a desk user, I want spot IM and spot CCR capital priced for every case too, and their tabs to follow the case I
    pick, so that every measure answers the same question.
16. As a desk user, I want Copy quote to quote every case, so that I can send sales the alternatives in one step.
17. As a desk user, I want a `Changes` input tab that lists every Common change and each case with its changes, and
    says how many it holds (`Changes ●5`), so that I find a change without scanning a wide grid.
18. As a desk user, I want to edit a change's value, or remove it, from the Changes tab, so that I fix a change where I
    read it.
19. As a desk user, I want to remove a case, or every case, from the Changes tab and with a command, so that I can
    start again.
20. As a desk user, I want a case's change carried, refused or lost like any other edit when the sources move, so that
    a case never prices a value against a source it was not made for.
21. As a desk user, I want `set CSA-A threshold 5mm` to change the workspace's value without making a case, refused by
    name as the grid would refuse it, so that the command line edits what the grid edits.
22. As a desk user, I want a cell named by a row and a column I can guess (`CSA-A threshold`, `NS-1 recovery`,
    `T-1001 notional`), and a name that matches nothing or several things refused with what it could mean, so that a
    command never edits a cell I did not mean.
23. As a desk user, I want the Run to freeze every case with its changes and its trades, so that the Pricing DB and
    Download say what each row priced.
24. As the AI pane, I want `vary`, `add trades` and `set` as command capabilities with the same checks, so that the
    agent edits the workspace as the user does and never by driving the grid.
25. As a phone user, I want the Changes tab among the Inputs and the Metrics rows per case in Results, so that cases
    work on a phone.

## Implementation Decisions

- **The kernel's edit gains a layer key** (ADR-0010 Consequences), opaque to it as the scope is: an edit target is a
  scope, an entity, a field and an optional layer. Overrides, conflicts and lost targets are keyed by the whole target,
  so the same field holds a Common value and a case's value at once, each carried and checked on its own. The store
  persists the layer, and a workspace saved before it has none (Common). Removing a created entity can remove every
  edit in a layer the caller names, in one log entry that counts them.
- **A case is a created entity of the workspace** (ADR-0004), `case:<id>` in the counterparty's scope, with its title, its
  kind (`vary` or `add`), and for `add` the trade it adds. Its changes are edits in its layer. The portfolio module, not
  the kernel, says how layers compose: the Base is Original plus Common; a case is the Base plus its own layer, its
  value winning on a field both hold. Every reading the page has (trades, CSAs, netting, membership, cells, Original
  differences) is computed from such a layered reading, so the existing materialiser runs unchanged on it. The
  workspace view keeps showing the Base, and the Changes tab shows each case's changes.
- **An `add` case** sets A off, in its reading only, for every other New row of its set, so its After is the existing
  portfolio's members and that one trade. Making the set refuses, by name, a candidate ticked in Before (docs/03 §5's
  DEFAULT). Price checks that the set's trades are exactly the New rows with A ticked and that none is in Before, and
  refuses otherwise, naming the trades.
- **`vary` and `set` name a cell** as ADR-0010 §9's note says: the portfolio registers its rows (CSAs, the netting
  set, trades) and per row kind its fields with their titles and short names, and how a written value is read
  (amounts as ADR-0008, rates in `%` and `bp`, booleans, curve and CSA ids). The same resolver serves both, and the
  value goes through the same validation as a typed cell. `vary` makes its cases, skips the value the workspace holds
  and a case already made, and answers what it made and skipped.
- **The accepted input holds every case.** What the cases share stays at its top (context, display currency, the
  membership rules' versions, the selection); each case is `{ id, title, kind, changes, trades, modules, measures }`,
  its trades and module parts read from its layered state, its measures with their refusals, so one case's refused
  measure leaves the others standing. The Base is first. A provider reads its cases through one portfolio helper that
  gives each case in the shape a one-case input had, so XVA's, spot IM's and spot CCR capital's providers loop over
  cases with their bodies unchanged and write each row and trace entry with the case's id and title.
- **One request per module, every case in it** (ADR-0010 §3's note): the workflow is unchanged in shape, its outputs
  one per module. The cap counts cases with the Base, configured, DEFAULT 50; Price over it is refused with
  `TooManyCases` and the excess.
- **The Changes tab** reads a per-workspace changes list from the server: each Common change and each case's change
  with its entity, field, title, value and Original's, and each case's title, kind and count. Editing a value there is
  the same edit as the grid's, in that case's layer; removing is a reset in that layer; removing a case removes the
  entity and its layer.
- **The page** keeps reading results by case as it does (Metrics rows, the collapsed line, Copy quote). The SIMM and CCR
  tabs read the trace of the case in view, chosen with the dock's case selector, the Base by default. *Vary values…*
  is a Consumer command on ExGrid's Context Menu (`contextCommands`, ex-grid ADR-0036) of the editable cells, opening a
  dialog for the list.
- **Commands** (ADR-0005, ADR-0006): `vary <row> <column> <values>`, `add trades`, `set <row> <column> <value>`,
  `case remove <name>`, `cases clear`, and `open changes`. `vary`, `add trades` and `set` are command capabilities,
  reached by the AI through the command gateway with origin `ai`; removing cases is the page's.

## Testing Decisions

A good test drives a public seam and asserts what a user or a caller sees: the cases made and skipped, refusals by
name, each case's figures, what a Run froze. It never asserts how a layer is stored. Expected figures are computed
from the synthetic sources, a case against a Base priced with the same value typed into the workspace. Each test names
the ADR section it holds, as the existing tests do.

The seams, the same four as step 1 (DEFAULT, per the owner's standing preference of 2026-11-08 to go with the
recommendation and report it):

1. **The kernel's workspace service** (xUnit, prior art `WorkspaceTests`): an edit in a layer beside a Common one, carried,
   conflicted and lost per layer, stored and restored, removed with its entity.
2. **The portfolio workspace service with the registered measures** (xUnit, prior art `PricerTests`): `vary`, `add
   trades`, `set`, the layered readings, the Changes list, the frozen accepted input per case, each provider's rows per
   case, the cap and docs/03 §5's refusals.
3. **The HTTP API through the in-process host** (xUnit with `WorkbenchFactory`, prior art `XvaApiTests` and
   `AgentApiTests`): the case commands and the Changes list on the wire, Price to a Run with a row per case, and the
   command capabilities through the command gateway.
4. **The client's pure functions** (Vitest, prior art `metrics.test.ts`) and **the page in a browser** (Playwright,
   prior art `xva.spec.ts`), driven by keyboard and read once settled: `vary` from the command line and from the
   Context Menu, Price, a row per case, the Changes tab removing a case, and the phone layout.

## Out of Scope

- Before-only and After-only changes, and so After-only within a case: Delivery step 3, later.
- Scenarios, and cases crossed with them: Delivery step 4.
- The Credit and Settings tabs, Risks and Profile: Delivery step 5. A `cds <tenor>` row for `set` and `vary` arrives
  with the Credit tab.
- A package of two trades as one candidate (docs/03 §5, out of the initial scope).
- Saving a set of cases by name, or sharing one.

## Further Notes

- A case belongs to the counterparty it was made on: a counterparty change drops it with its changes, as any edit in
  that scope, and counts it in the confirmation (ADR-0003 §6).
