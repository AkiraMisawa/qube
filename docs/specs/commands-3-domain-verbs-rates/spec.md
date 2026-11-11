# Generic commands, delivery step 3: the pricer's verbs on the server, short forms, and Rates

Status: in progress

The decisions are ADR-0016 §3, §4 and §5, with its notes of 2026-11-11 for step 3 (27 onward). This spec orders
ADR-0016's Delivery order, step 3, and names the seams it is tested at. The owner asked on 2026-11-11 for every step
to be implemented without a question at each one, so the seams below are DEFAULTs and are reported, not asked.

## Problem Statement

After step 2 the kernel's verbs (`set`, `reset`, `remove`, `undo`, `redo`, `find`) are declared once on the server
and read there. Everything else the pricer's line can do is still written by hand in the client: some forty commands
in one file, each read by the client, each calling an endpoint of its own. The line cannot preview them, the chain key
runs them without a reading, and the AI's capabilities are a second copy of the same words. `before all` acts on "the
grid the keyboard was last in". Rates has no registered table and no door, so none of the kernel's verbs work there,
a line of Rates cannot be edited or removed by command, and `set defaults` fails on half its values.

## Solution

**3a. Short forms and the pricer's verbs that edit.** A command says which application's workspaces it is for. The
pricer's `vary`, `add each`, `cds`, `measures`, `case remove` and `cases clear` say how they are typed and read their
own line on the server, with a preview that is a trial of the command. `before all`, `after none` and `currency USD`
are short forms that expand to `set`. The shell builds `open <tab>` from a page's tabs.

**3b. Scenarios and the verbs that execute.** The scenario verbs, `price`, `refresh` and `pv01 retry` say how they
are typed and go through the door. *Changed as built (2026-11-11):* this first said that the pricer's scenarios are
a registered table. They are verbs of the pricer for now, and the table waits for step 5 (ADR-0016 note 32 says
why).

**3c. Rates registers.** Its lines and defaults are tables, its page has a door, a screen and places, and its verbs
say how they are typed.

**3d. `add each` with lists.** The capability takes iterations as lists, the page makes one from the two grids'
selections, and the Changes tab shows an iteration's lists.

## User Stories

1. As a desk user, I type `vary CSA-A threshold 10mm, 5mm` and see the two cases it would make before Enter.
2. As a desk user, I type `vary CSA-A colour red` and am told why it cannot be read, with nothing run.
3. As a desk user, I type `before all` with the keyboard in the Existing grid, and the line shows `set existing
   before on` before Enter; `undo` then names that line.
4. As a desk user, I type `measures` and am offered the measures I have not typed yet.
5. As a desk user on a Rates page, I am not offered `vary`.
6. As a desk user, I chain `add each`, Ctrl+Enter, `price`.
7. As the AI, I call `xva.vary` with the same `cell` and `values` a user types, and get the same refusal.
8. As a desk user, I type `scenario exclude s1` and `scenario remove s1`, and see what each would do before Enter
   (3b). *Changed as built:* this was `set s1 included off` and `remove s1`, which need the scenarios' table
   (step 5; ADR-0016 note 32).
9. As a desk user on Rates, I select two lines and type `set notional 5mm`, then `undo` (3c).
10. As a desk user, I select two new trades and one existing trade and press `Add each of selection` (3d).
11. As a developer of the next page, I declare a verb's form and its reader, and write no client command.

## Implementation Decisions

### 3a. Short forms and the pricer's verbs that edit (tickets 01 to 07; ADR-0016 notes 27 to 31)

- **A command's application.** `CommandForm.App`, on the wire `app`. `CommandLines` reads a line with the commands
  whose form names the workspace's application or none. `CommandDoor.app` is the page's, and `servedCommands` lists
  a command whose form names it or none.
- **Short forms.** `IShortFormCatalog` answers a module's `IShortForm`s: a descriptor (an id, a title and the form,
  which names the application), `Expand(LineContext)`, which answers the kernel line or refuses by name
  (`TableRefused`), and `Complete(LineContext)`. `CommandLines` holds them as it holds the screen's verbs; its
  reading of one is the reading of the expansion, and `LinePreview` gains `Expands`. `GET /api/catalog` lists
  `shortForms`. The line builds their entries and shows `expands` under the input. Enter on one runs the
  expansion's inputs.
- **The pricer's short forms** (`PortfolioShortForms`): `before`, `after`, `currency`.
- **A domain verb's reader.** `PortfolioVary`, `PortfolioTradesAdd`, `PortfolioCds`, and the new `PortfolioMeasures`,
  `PortfolioCaseRemove` and `PortfolioCasesClear` implement `ILineCommand`. Each service method is split into its
  plan (the changes and what is said of them) and the write; the reader plans and has `WorkspaceService.After` check
  the changes, and the handler plans and writes.
- **The page.** Its functions for `vary`, the measures' buttons, the cases and the caption buttons call the door
  (`invoke`, or `enter` with a line), and `XvaApi`'s endpoints that wrote for them go. The Credit tab's selects and
  the currency select stay field edits (ADR-0016 note 28). `xva.vary`'s request names its cell by `cell`, or by
  `entity` and `field` for the dialog, whose cell is the one its menu opened on.
- **Tabs.** `Place.tab`; the shell builds one `open <tab>` command for each tab of the page, and `go` takes the
  rest.

### 3b. Scenarios and the verbs that execute (tickets 08 to 11)

Decided in ADR-0016 notes 32 and 33, which this section follows.

- *Changed as built (note 32):* the scenarios are not a registered table in this step. A scenario's name may be
  several words, whether Price prices it is one list of the workspace, and its rows are its shifts: the kernel's
  tables hold none of the three until step 5. So `scenario include`, `scenario exclude`, `scenario remove` and
  `scenarios clear` are domain verbs with a trial as their preview, as `case remove` is, and not short forms.
- `scenario`, `scenario duplicate`, `scenario ladder`, `scenario cross`, `scenario save` and `scenario load` read
  their own lines as 3a's verbs do. `scenario save` writes no workspace: its reading checks the save and keeps
  nothing.
- `price`, `refresh` and `pv01 retry` are capabilities with forms (note 33). They are not tried: the preview says
  what would start, and refuses what can be refused without starting. `price` and `pv01 retry` have the effect
  `execute`; `price` is `xva.workspace.price`, and submits its Run under the key it was invoked with.
- The Scenarios tab's gestures on a scenario by name, the Price and Refresh buttons and a failed cell's *Ask again*
  invoke the same capabilities through the page's door, and the page's endpoints that wrote for them go.
- The line lists the page's application's served commands first, in the module's order.

### 3c. Rates registers (tickets 12 to 15)

Decided in ADR-0016 notes 34 to 36, which this section follows.

- `rates.lines` and `rates.workspace` are registered tables (`RatesTables`, note 34). A line's flat fields are its
  fields, and an IRS's terms as `new irs` names them are fields too. *Changed as built (note 34):* this said that
  `Percent` joins `Rate`. The two stay: they are typed and written alike, and Rates holds percent in its line
  contract and its provider, so one form would mean a changed wire contract or a conversion at the module's edge.
- The defaults are fields of `rates.workspace`, written as a change of context in a batch
  (`WorkspaceChange.Context`). `set defaults` is `set default currency USD`: a value alone is written.
- The workspace service tells observers of each accepted command (`IWorkspaceObserver`, note 35), so a live
  subscription prices a revision whoever wrote it.
- `price` (`rates.workspace.price`), `capture` (`rates.frame.capture`), `live start`, `live stop` and `add line`
  (`rates.line.add`) say how they are typed, for Rates' application (note 36). `new <product> <terms>` is the
  kernel's `entity.create` with a form: a product's rules say what they would make (`IProductRules.WouldCreate`).
- The door reads a command's `answer`, or its one output. A line that is exactly a client command's words is that
  command's (`readLine`).
- The Rates page has a door, a screen and places; its buttons invoke through the door, and the page's endpoints
  whose writes moved go, with the client's hand-written Rates commands.
- Rates' three command defects of 2026-11-10 end here.
- *Added as built (note 36):* the door tells its page what the page's own command answered, by which the Rates page
  opens the Run of `price` and `capture`; the line draws what the server read under names a form does not list (a
  product's terms by their fields); `live start` says `Live is running.` whether it started or joined; a product's
  field may carry examples the line offers (an end's tenors); and `GET /api/products` with its preview goes too.

### 3d. `add each` with lists (tickets 16 to 19)

- A contract may hold a list, and `xva.trades.add` takes iterations: each the candidates it adds and the existing
  trades it takes out of After (§3).
- The page makes one iteration from the two grids' selections, by a button and a menu item, and the Changes tab
  shows an iteration's two lists.
- How the line spells the lists stays open (§3): the line takes `add each` with no argument.

## Testing Decisions

A test names the ADR it holds. Tested at these seams, each through its public interface:

- `CommandLines.Preview` (`CommandLineTests`, station readings): a command of another application is no command; a
  short form's reading is its expansion's, with `Expands`; a short form refused by its own rule.
- `CommandLines.Preview` and `CommandService` over the pricer (`PricerLineTests`): each verb's reading (inputs,
  arguments, what would be made), its refusals by code, that a reading writes nothing, and that invoking the inputs
  read does what the reading said.
- HTTP (`CommandGatewayTests`): the catalog lists `shortForms` and a form's `app`.
- Client, Vitest: `servedCommands` (a command of another application is not listed; a short form's entry);
  `buildCommands` (`open` from tabs, `go` from the rest).
- Client, Playwright (`xva.spec.ts`): `vary` previewed, refused and run; `before all` showing its expansion and
  taken back; `measures` completed; the existing tests of `add trades`, `cds`, `case remove`, `cases clear`,
  `currency` and the caption buttons, on the new path.
- 3b, the same seams: `PricerLineTests` holds each scenario verb's reading and write, `price` read without being
  tried and submitted once under its key, `refresh` and `pv01 retry`; `CommandGatewayTests` the catalog's forms,
  order and `execute`; Vitest the list's order, the client's `price` on a Rates page alone, and a command read by
  its words where every argument may be left out; Playwright `price`, `refresh` and `pv01 retry` previewed and
  run, the Refresh and Price buttons on the same commands, and the existing scenario tests on the new path.

- 3c, the same seams: `RatesLineCommandTests` (`CommandLines.Preview` and `CommandService` over a Rates workspace)
  holds `set`, `reset` and `remove` on a line, an IRS's terms as fields, a default set alone and taken back, a
  subscription told of a revision the kernel wrote, each Rates verb's reading and write, and `new irs` read with
  nothing made; `WorkspaceUndoTests` a batch's change of context; `RatesApiTests` and `RatesCommandTests` the
  moved writes through the gateway; Vitest the door's reading of a command's one output and `readLine`'s exact
  client command; Playwright (`rates.spec.ts`) two lines selected, `set notional 5mm` and `undo`, the line saying
  its screen, `find <line>`, a default set alone, `new irs` previewed and run, and the buttons on the same
  commands. *Added as built:* Vitest also holds what the line draws of a reading whose arguments the form does
  not list (`argumentsShown`), what the door tells its page of an answer, and that the client lists no Rates
  command but `new rates` (`buildCommands`); Playwright also holds the menu's `Remove line` and the page's places.

Not tested: what a domain service already holds by its own tests, and the kernel's reading of the expanded line.

## Out of Scope

- `select`, `filter`, `sort`, `copy`, `export` and the AI's row read (step 4).
- Drafts, templates and tables under a row; a trade made in the pricer from typed terms (step 5).
- Saved sequences of edits (step 6).
- A result that carries a file or a text to copy: `download run` and `copy quote` stay in the client (note 31).
- The line's spelling of `add each` lists (ADR-0016 §3, open).

## Further Notes

- Sizing: a counterparty has at most about 10,000 trades and about 100 at the median (the owner, 2026-11-10).
