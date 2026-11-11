# Generic commands, delivery step 4: the AI's reads, and the verbs of the screen

Status: built 2026-11-11 (4a, 4b, 4c and 4d)

The decisions are ADR-0016 §3 (view verbs), §5 and §7, with its notes of 2026-11-11 for step 4 (38 onward). This spec
orders ADR-0016's Delivery order, step 4, and names the seams it is tested at. The owner asked on 2026-11-11 for every
step to be implemented without a question at each one, so the seams below are DEFAULTs and are reported, not asked.

## Problem Statement

After step 3 every command of the two applications is declared once on the server and read there, and the agent is
offered each as a tool. The agent still cannot see what it acts on. A tool's arguments are a fixed schema, so it
learns a table's name or a field's words only from a refusal; it has no way to read a trade row, so "which USD trades
are over 5bn?" cannot be answered; and it cannot say what a command would change before it runs it, which a person
sees on the line before Enter.

The screen has one verb, `find`. A grid is sorted and filtered only by its headers, a selection is made only with the
mouse or the keyboard's arrows, and nothing copies or exports rows by name. `shown` means the grid's subset, since no
view has conditions.

## Solution

**4a. The AI describes, reads and previews.** Three kernel capabilities, each a command because it reads a caller's
own workspace: `tables.describe` answers the registration read out, `tables.read` answers the count and a bounded
page of rows for a selector and the fields wanted, and `commands.preview` answers the line's reading with nothing
run. None is typed on the line. Each is offered to the agent as a tool, as every command is.

**4b. `sort` and `filter`.** Two verbs of the screen, read by the server as `find` is and done by the page: the
grid's order by a field, and the rows a condition leaves. Where the server pages a grid its reader applies the
conditions; where the page holds every row the page narrows them. `shown` is then what the filter leaves.

**4c. `select`, `copy` and `export`.** Three more verbs of the screen: the grid's Selection put on the rows a selector
names, rows and fields written to the clipboard, and a table saved as a file.

**4d. What note 28 left.** A domain verb's refusal names the word it refuses and what it could have been, as the
kernel's verbs do, and `vary` offers its rows, its columns and a choice's values. Split from 4c so that the three
verbs could be read on their own.

## User Stories

1. As the AI, I call `tables_describe` and learn that the workspace has trades with a field `csa` that a command
   sets, an identifier that points into the table of CSAs, before I name it in a command (4a).
2. As the AI, I call `tables_read` with `where ccy=USD and notional>5bn` and the fields `notional, csa`, and answer
   how many there are and what the first hold, with every value written as `set` would read it (4a).
3. As the AI, I ask for the next page from where the last one ended, and am told how many rows follow (4a).
4. As the AI, I call `commands_preview` with `set where ccy=USD csa CSA-B` and tell the user that 12 trades would
   change before I run it (4a).
5. As the AI, I am refused by name, with the word at fault and what it could have been, when I name a field the
   table has not (4a).
6. As a desk user, I see each of these calls and its result in the AI pane, as I see any tool call (4a).
7. As a desk user, I type `sort by notional desc` with the keyboard in a grid, and the grid is in that order (4b).
8. As a desk user, I type `filter where ccy=USD`, the grid shows the USD rows, and `set shown csa CSA-B` then acts
   on those rows alone; `filter off` shows them all again (4b).
9. As a desk user, I type `select where ccy=USD`, and the grid's Selection is on those of its rows, or the line says
   why it cannot be (4c).
10. As a desk user, I type `copy selected notional, csa` and paste the rows into a spreadsheet (4c).
11. As a desk user, I type `export trades` and a file of the table is saved (4c).
12. As a desk user, I type `vary CSA-Z threshold 5mm`, the line stays with `CSA-Z` selected, and what I type next
    replaces it (4d).
13. As a desk user, I type `vary ` and choose the row, then its column, from the list (4d).
14. As the AI, I preview `measures cva dva2` and am told the word at fault and the measures there are (4d).

## Implementation Decisions

### 4a. The AI's describe, read and preview (tickets 01 to 05; ADR-0016 note 38)

- **A reading effect.** `CapabilityEffect.ReadWorkspace` joins the effects that make a capability a command
  (`IsCommand`): the gateway invokes it for a caller, the agent's tools hold it, and the validator refuses it as a
  step. `contracts/composition.schema.json` lists it.
- **Where they live.** `tables.describe` and `tables.read` are the kernel's tables module's (`TablesModule`), read by
  `TableService` with the reader of rows its edit verbs use, so `rows` means to a reading what it means to `set`.
  `commands.preview` is a module of its own over `CommandLines`, given lazily as the tables' service is, since the
  lines are built from the registry the module registers into.
- **Contracts.** Requests and answers are flat with lists, as notes 12 and 37 allow: `tables` and `fields` are two
  lists of records, a row is a record with a list of cells, a preview's inputs are JSON text. An answer meets the
  contract it declares, and a test holds that.
- **What a form is written as.** `FieldForms` says in one place what a field takes ("an amount: 5mm, 250k, 1bn, or a
  bare number of millions"); a refusal of a value and the description read the same words.
- **Bounds.** 200 rows a page at most and 50 by default, 2,000 cells an answer, 50 choices a field; each is said in
  the answer where it cut anything.
- **The agent.** Its instructions say, with no domain's word, that the tables are described and read by tools and
  that a preview says what a command would change. The pane draws these calls as it draws any, in a block that
  scrolls within a bounded height.
- **The stand-in agent** (added as built, note 38). With no model configured the pane's agent is scripted, so it
  takes `describe the tables`, `read <rows> [fields …]` and `preview <line>` for the workspace the pane's context
  names. It is how the three are seen working on screen here, and it stays labelled a stand-in.

### 4b. `sort` and `filter` (tickets 06 to 10; ADR-0016 notes 39 to 41)

- **What a view verb answers.** A reading of a view verb names no command and one thing asked of the page: a
  `place` (`find`), a `sort` or a `filter`, each with its sentence. `LinePreview` carries them beside `place`.
- **The view's conditions.** `LineView` gains `where`, the grid's filter as typed. `Views` reads `shown`, positions
  and the order's token under it, with the selector's own reader of conditions, in one place that the pricer's
  page reader calls too.
- **The verbs.** `sort` and `filter` are kernel view verbs beside `find`, declared in the catalog's `viewVerbs`.
  Each reads the grid behind the line, refuses another table by name, and completes its words.
- **The door.** A page's `Screen` says how its grid takes a place, a sort and a filter. The door hands each to the
  grid the view was read of and says the reading's sentence.
- **A grid the server pages** (the pricer's trade grids): the conditions are the grid's Filter (ExGrid's Opaque
  Filter), sent with each page request; the page endpoint takes `where`. The view's `where` is the filter the
  grid holds, whether or not its rows have arrived, so a chained `set shown …` is read under it.
- **A grid whose page holds every row** (the pricer's CSAs, the Rates lines): the page asks the server which keys
  the conditions leave at each revision it shows (`POST /api/workspaces/{id}/shown`) and shows those rows.
- **A filtered grid says so**: the conditions, the count and a button that takes the filter off, beside its title.
- **As built** (ADR-0016 notes 39 to 41, "as built"): the conditions are the Opaque Filter of the grid's source for
  both kinds of grid; a grid whose page holds its rows keeps them until the server has answered for new conditions;
  the Rates line grid refuses a sort by the column's name, since it shows its terms as typed.

### 4c. `select`, `copy` and `export` (tickets 11 to 14; ADR-0016 notes 42 to 44)

- **One member for what a view verb asks.** `LinePreview` holds `asks`, one thing asked of the page whose `kind`
  says which (`place`, `sort`, `filter`, `select`, `copy`, `export`), in place of a member for each (note 42).
- **`select <rows>`** is a kernel view verb. The rows are the selector's, read with the view, and must be rows the
  grid behind the line shows, next to one another in its order: ExGrid places one range for a Consumer. Conditions
  said alone are asked of the rows the grid shows. Where the server pages the grid it answers a position and a
  count; where the page holds the rows, their keys, and the page says whether its grid shows them as one run
  (note 43).
- **`copy [<rows>] [<fields>] [with headers]` and `export [<table>] [<rows>] [<fields>]`** are kernel view verbs
  that need no grid where the line names its rows. Their reading says the rows and the fields; more cells than the
  most a line takes out are refused there. The content is asked for at Enter (`POST /api/commands/content`), read
  from the same line at the reading's revision, and the door writes the clipboard or saves the file (note 44).
- **How a cell is written** is one function beside the reader of values (`FieldForms`): a number plain, a rate
  and a spread with the percent sign, a flag `true` or `false`, the rest as written, none empty.
- **The door.** A page's `Screen` says how its grid takes a selection. What takes content out of the page (the
  clipboard, a file saved) is given to the door, so a test gives its own.

### 4d. What note 28 left (tickets 15 to 18; ADR-0016 note 45)

- **A workspace's refusal names its word.** `WorkspaceRefused` holds `Word` and `Candidates`, as `TableRefused`
  does, and a reading carries them: the word is marked where it stands in the line, by the function that marks a
  table's (`Place`). A capability's failure is its code and sentence, unchanged.
- **The pricer says the word where it refuses**, in the one place the line and the capability share: `vary`'s row
  and column, a measure, `cds`'s words, `case remove`'s case and a scenario's name.
- **`vary` completes** from the rows a cell is named on and their columns (`PortfolioCells`), which its reader
  already resolves a cell with: one list for the reading, the refusal's candidates and what is offered.

## Testing Decisions

Seams, as the earlier steps have them:

- **The command gateway** (`CommandService`) over the synthetic registration with no financial word (`Stations`):
  what each capability answers and refuses, for its owner and for another user, at a revision that has moved.
- **The agent's tools** (`AgentToolGateway`): the three are offered with their contracts' schemas, and a call is
  invoked for the server's caller.
- **The pricer's registration** through the same gateway: a trade row reads as its grid shows it.
- **The host** (`ApiTests`' factory): the catalog lists the three with their effect and no form, and the gateway's
  endpoint answers a reading.
- **The composition validator**: a workflow step that names a reading is refused.
- **The agent's endpoint with the stand-in** (`AgentApiTests`), and one Playwright test of the pane (added as
  built): the stand-in's three requests, and a page of rows scrolling inside its card.
- 4b and 4c add the preview (`CommandLines.Preview`) with a view, Vitest for the door and the grids' helpers, and
  Playwright for what only a browser shows.
- For 4b: the preview with a view over `Stations` (the verbs' readings, `shown` and positions under a view's
  conditions); the host for the pricer's page endpoint with `where` and for `/shown`; Vitest for the door's
  handing of a sort and a filter and for the views' `where`; Playwright for a trade grid sorted and filtered from
  the line, `set shown` under a filter, and a grid whose page holds its rows.

- For 4c: the preview with a view over `Stations` (the reading of `select` where the server pages and where the
  page holds the rows, and what each refuses; the readings of `copy` and `export`); `CommandLines.Content` over
  `Stations` for the text of each, cell by cell; the host for `/api/commands/content` and the catalog; Vitest for
  the door (a selection handed to the grid, content written to the clipboard and saved, each refused) and for the
  grid's helper; Playwright for a trade grid's Selection placed from the line and `set` then acting on it, the
  clipboard read back after `copy`, and the file `export` saves.

- For 4d: the preview over `Stations` with a command whose reader refuses with a word (marked, candidates
  answered, a word that stands twice not marked), and the pricer's lines (`PricerLineTests`): each refusal's word,
  where it stands and its candidates, and what `vary` offers for a row, a column and a value. No browser test is
  added: that the line selects the word a refusal marks is the kernel's, and has its test (note 15).

A test names the ADR section or note it holds. Expected values are literals of the synthetic registration.

## Out of Scope

- Retaining a reading as a dataset a view binds, and drawing it as a grid in the pane (ADR-0016 §7's "may").
- A table read as a workflow step and as the source of a step's `forEach` (§8).
- A sort of the AI's reading.
- Drafts, templates, tables under a row, `load trades` (step 5); saved sequences of edits (step 6).
