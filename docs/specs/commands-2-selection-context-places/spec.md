# Generic commands, delivery step 2: the selection, the context, places, the chain key and menus

Status: done

The decisions are ADR-0016 §2, §4, §5 and §9, with its notes of 2026-11-11 for step 2 (16 onward), and ADR-0006 §5
(the current context is the default target). This spec orders ADR-0016's Delivery order, step 2, and names the seams
it is tested at. The owner asked on 2026-11-11 for every step to be implemented without a question at each one, so the
seams below are DEFAULTs and are reported, not asked.

## Problem Statement

After step 1 a desk user can say rows by id, by subset and by `where`, and nothing else. The rows they have just
selected in a grid cannot be named: `set` needs their ids typed again. The line does not say which grid, row or cell
a command would act on, and `before all` still acts on "the grid the keyboard was last in", which nothing shows. No
command moves the keyboard to a grid, a tab or a row, so a chain of commands still needs the mouse between them, and
each command closes the line. A grid's Context Menu is written by hand in each page.

## Solution

**2a. The selection and the context.** When the line opens, the page says what the user was looking at: the grid's
table, its selection and its focused cell. The line shows it. `selected` and `shown` are rows like any other, and
`set csa CSA-B` with no rows acts on the selection. The preview resolves all of it to rows by id before Enter.

**2b. Places.** A page declares its places. `go <place>` puts the keyboard there and `find <row> [<field>]` puts a
grid's Focus on a row or a cell. `focus grid` and the panel chords become `go`.

**2c. The chain key and menus.** Ctrl+Enter runs the line and keeps it open for the next command. A table declares
the lines worth a menu item, and every grid bound to it shows them in its Context Menu.

## User Stories

1. As a desk user, I select three trades, open the line and type `set csa CSA-B`, and the line shows those three
   rows and what would change before Enter.
2. As a desk user, I type `set selected where ccy=USD a off`, and only the USD trades among the selected change.
3. As a desk user, I see under the line which grid, how many rows and which cell my command would act on.
4. As a desk user, I select every row of a grid of 10,000 with one key, and `selected` still means exactly those rows.
5. As a desk user, I type `reset` with the keyboard on a cell, and that cell goes back to its source.
6. As a desk user, I am refused, with the reason, when the grid's order moved after I selected.
7. As the AI, I am refused when I say `selected`: I name rows.
8. As a desk user, I type `go existing`, `go csa`, `go results`, and the keyboard is there.
9. As a desk user, I type `find E00003 csa`, and the grid's Focus is on that cell, scrolled into view.
10. As a desk user, I press Ctrl+Enter, see the result in the line, and type the next command at once.
11. As a desk user, I open a grid's Context Menu and find `Take out of After`, with the line that does the same.
12. As a developer of the next page, I register a table and a door, and get all of the above with no command code.

## Implementation Decisions

### 2a. The selection and the context (tickets 01 to 05; ADR-0016 notes 16 to 19)

- **The view, as a value.** `LineView`: `table`, `subset?`, `sorts` (`column`, `descending`), `order?` (the order
  token), `keys?` or `ranges?` (`start`, `count`) for the selection, and `focus?` (`key?` or `position?`, `field`).
  `POST /api/commands/preview` takes it as `view`. `LineContext` carries it to the command that reads the line.
- **The reader.** `SelectorReader` reads `selected` and `shown` where it reads `all`. `RowSelector` says which was
  said. `Selectors.Resolve` takes the view: keys as ids; ranges through `IWorkspaceTable.InOrder(state, subset,
  sorts)`, which answers the rows in the order the page's reader gives and that order's token, or null where the
  table has no such reader. A token that differs from the view's is `ViewMoved`; no view is `NoView`; a selection
  of no rows is `NothingSelected`.
- **Rows left out.** In `set` and `reset`, as note 18 says.
- **What the preview answers.** `inputs` hold `table` and `rows` as keys. Each `LineArgument` gains `from`
  (`typed`, `context`). `LinePreview` gains `view`, the sentence of note 19. What could follow offers `selected`
  and `shown` with their counts where the view has them.
- **The pricer.** `xva.trades` answers `InOrder` with the sorter its `rows` endpoint uses; a test holds the two to
  the same order. `xva.csas` and the tables of one row have none.
- **The door.** `CommandDoor.view()` answers the page's view now. The pricer's page keeps, for each trade grid, the
  sort and the order token of its reader's last answer, and reads the grid's selection and Focus with `readView()`.
- **The line.** Reads the door's view once as it opens, asks the preview with no text for the sentence, shows it
  under the input, sends the view with each preview and with Enter's own reading, and marks an argument from context.

### 2b. Places, `go` and `find` (tickets 06 to 08 and 12; ADR-0016 notes 20 to 23)

- **Places.** A page registers its places with the shell (`shell/places.ts`): a word, a title, whether it is a grid,
  and how the keyboard is put there, which answers why where it cannot. The shell builds `go <place>` from the
  active panel's places. `focus grid` is `go` to the first place that is a grid, and keeps its chord.
- **`find <row> [<field>]`** is a view verb the server reads (note 21). `GET /api/catalog` lists the view verbs
  (`viewVerbs`), and the line builds their entries as it builds a capability's. The preview answers a `place`
  (`table`, `subset?`, `key`, `position?`, `column?`, `said`) and no command.
- **The door.** `DoorPage.screen()` answers the view and how the grid it was read of takes a place (`put`), which
  answers why where it cannot. `enter` on a reading with a place invokes nothing: it has that grid take the place,
  says the server's sentence as the notice, and answers that the keyboard was moved, so the line gives none back.
- **The grid.** The page keeps, for each view it gave the line, how that grid takes a place: `placeSelection` of the
  one cell against the Row Sequence Version the view was read in.
- Only `go`, `find` and `open` move the keyboard, and each says where it went.
- **A table left out is the screen's** for `all` and `where` (note 23). The line's reader decides once which table
  rows said with no table mean (`SelectorScan.Unsaid`); the preview, the completions and the request read that, and
  the request names a table the screen gave.

### 2c. The chain key and menus (tickets 09 to 11 and 13; ADR-0016 notes 24 to 26)

- **The chain key.** Ctrl+Enter (⌘+Enter) in the line runs the line and keeps it open: the text is cleared, the line
  says what ran and the notice it heard, the view and the commands are read again as at an opening. A refusal stays
  as with Enter. While an IME composes every key of the line is the IME's.
- **The keyboard in a chain** (`shell/keyboard.ts`): from the first chained key until the line closes, a focus that
  lands outside the line is where the keyboard goes when the line closes, and the line takes it back at once.
- **The line hears notices** (`platform/notices.ts`, `hearing`): the workspace's `notify` can be listened to, and the
  line says the latest it heard since the chained command began. One heard after that command ended is said by
  itself. A dialog that takes the keyboard ends the chain (`holdKeyboard`'s `lost`).
- **A table's actions.** `TableDescriptor.Actions`: a title and a line. `GET /api/workspaces/{id}/tables` answers each
  table's id, title and actions, the kernel's `Reset this cell` first. An action whose line begins with no command's
  words fails when the tables are read.
- **The Context Menu.** A page gives a grid bound to a table that table's actions (`platform/commands/served.ts`,
  `tableMenu`): each item is labelled with its title and its line, and runs the line through the door with the
  menu's view, the Selection and the cell the menu was opened on (`platform/grid/lineView.ts`, `menuRead`). A refusal
  is a notice. The pricer declares `Take out of After`, `Put into After`, `Take out of Before`, `Put into Before`.
- **The order's token** (ticket 13). The pricer's token of a sorted order is the source's version and a digest of
  the keys in that order, so it moves only when the order does.

## Testing Decisions

A test names the ADR it holds. Tested at these seams, each through its public interface:

- `SelectorReader` and `Selectors` (`SelectorTests`, station readings): `selected`, `shown`, each with `where`; by
  keys and by ranges; `ViewMoved`, `NoView`, `NothingSelected`.
- `CommandLines.Preview` (`CommandLineTests`, station readings): the inputs hold keys; rows left out; the focused
  cell; `from`; the view's sentence; what could follow; the first three keys and the count in a command's name.
- `CommandService` (`TableCommandTests`): a command invoked with `selected` is refused.
- `PortfolioWorkspaces` and `xva.trades` (`PricerTableTests`): `InOrder` and `Page` give one order, sorted and not.
- HTTP (`CommandGatewayTests`): the preview takes a view and answers its sentence.
- `CommandLines.Preview` again, for 2b: `find` answers a place for a paged view and for a held one, and its
  refusals (no view, no row, an end that names several, a row the grid does not show, an unknown field, a moved
  order); `all`, `where` and a subset with a table left out, with a view and without, and the request's table.
- HTTP again, for 2b: the catalog lists `find` among its view verbs and among no capabilities; the preview's place.
- Client, Vitest: what the line makes of a view and of `from` (pure functions); the door passes the view.
- Client, Vitest, for 2b: the places a page registers and what `go` and `focus grid` do with them
  (`buildCommands`); the door on a reading with a place; the cell a place names in a grid (`lineView`).
- Client, Playwright (`xva.spec.ts`): rows selected with the keyboard, then `set csa CSA-B`; `selected where`;
  every row of a grid selected; `reset` on the focused cell; the context under the line; `go` to each place and
  `focus grid`; `find` of a row the grid has not loaded; the keyboard back where it was after `set`, `reset`,
  `undo` and Price; `where` with the table left out on the CSA grid; Ctrl+Enter; a menu item from a declaration.
- For 2c, server: the token of a sorted order across an edit that moves no row, one that moves a row and one that
  adds a row (`PricerTableTests`); a table's actions with the kernel's first, and a line of no command
  (`CommandLineTests`); `GET /api/workspaces/{id}/tables` (`CommandGatewayTests`).
- For 2c, client, Vitest: `menuRead` (the menu's Selection and cell as a grid's reading); `tableMenu` (labels, the
  line and the view an item runs, a refusal as a notice); `holdKeyboard` (a focus outside the line is remembered and
  the line has the keyboard back); `hearing` (a listener hears what is notified).
- For 2c, Playwright (`xva.spec.ts`): two commands chained and what the line said of each; `go existing` and `find`
  chained and the next line's context; a refused line in a chain; a later notice said in the line; a key of the line
  while an IME composes; a dialog ending the chain; `Take out of After` from the Context Menu on selected rows,
  `Reset this cell`, an action that changes nothing and one refused; a sorted grid keeps its selection across two
  chained commands.
- Measured, not gated: the preview of `set selected a off` with 10,000 rows selected.

Not tested: ExGrid's own selection and Focus, and anything a test of a public seam already holds.

## Out of Scope

- `select <rows>`, `filter`, `sort`, `copy`, `export` (step 4). `shown` therefore means the grid's subset for now.
- The pricer's other commands through the gateway, `before all` as a short form, Rates' door (step 3).
- The selection given to the AI as context (ADR-0007 §8; ADR-0016's first open question).
- The `Add each` buttons made from two grids' selections (ADR-0016 §3; they need step 3's capability).
- Drafts and their `apply` and `cancel` (step 5).

## Further Notes

- Step 1's known limit stands: the shell's `set region` is read before a table's field of that word.
- Sizing: a counterparty has at most about 10,000 trades and about 100 at the median (the owner, 2026-11-10).
