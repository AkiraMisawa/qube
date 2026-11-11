# Generic commands, delivery step 1: tables, the selector, the edit verbs, undo and the preview

Status: ready-for-agent

The decisions are ADR-0016 §1 to §6 and §11, with its notes of 2026-11-11 for what those sections leave to the
builder, and ADR-0003 (the log), ADR-0005 (commands), ADR-0006 (the line) and ADR-0008 (forms). This spec orders
ADR-0016's Delivery order, step 1, and names the seams it is tested at. The owner asked on 2026-11-11 for every step
to be implemented without a question at each one, so the seams below are DEFAULTs and are reported, not asked.

## Problem Statement

A desk user who wants to switch every USD trade out of After has to tick each row or type every id. `set` is the
pricer's own: its rows and columns live in the XVA sample, it is read once on the client and again on the server, it
names rows only by exact id, and it says nothing before Enter about what it will touch. Nothing takes a command back:
a `set` over many rows, once sent, is undone cell by cell. The AI writes cells through a tool of the pricer's
(`xva.set`), which the next application would have to write again. And a batch over rows is quadratic: `set a off` on
7,016 trades took 79 seconds on 2026-11-11.

## Solution

Three pull requests, each run and reviewed on its own.

**1a. The log says what one command did, and `undo` and `redo` use it.** Every accepted call to the workspace service
is one command with an id, and each entry it writes records what it replaced. `undo` takes back the workspace's latest
command and `redo` puts it back, as kernel commands through the page's own door to the command gateway. The line has
`undo` and `redo`, and the pricer's grids pass ExGrid's undo and redo keys to the same two commands.

**1b. A domain registers its tables, and the kernel edits them.** One form vocabulary with one reader and one writer.
A table registration (id, words, key, fields, subsets, row reader). One selector, a typed value with one spelling on
the line and one resolver. `entity.set`, `entity.reset` and `entity.remove` over any registered workspace table, with
a result that counts. XVA registers its tables, `xva.set` retires, and a batch over 10,000 rows is linear.

**1c. The server reads the line.** A command capability says how it is typed. `POST /api/commands/preview` reads a
line and answers what it means, the rows and what would change, or the word it cannot read, and what could follow.
The line shows that while it is typed, completes from it, and Enter runs the server's reading. The client's `set`
reader and the page's `/set` endpoint go.

## User Stories

1. As a desk user, I want `undo` to take back my last command whatever it touched, one cell or ten thousand rows,
   so that a wrong command costs one more command.
2. As a desk user, I want `redo` to put back what `undo` took back, so that an `undo` too many costs nothing.
3. As a desk user, I want Ctrl+Z in a grid to do what `undo` does, so that the key I know works.
4. As a desk user, I want `undo` to say what it took back, by the command's words or the cells' titles, so that I
   know what changed without looking for it.
5. As a desk user, I want `undo` to refuse at a refresh or a change of counterparty and say so, so that it never
   writes old values over a portfolio that has moved.
6. As a desk user, I want `set where ccy=USD after off` to switch every USD trade out of After, so that I name rows
   by what they are.
7. As a desk user, I want the line to say how many rows a command will touch and what will change before I press
   Enter, so that I never find out afterwards.
8. As a desk user, I want `set existing before on` and `set all after off`, so that a whole grid is one command.
9. As a desk user, I want `set E00003 notional=5bn rate=1.25%`, so that several fields of a row are one command
   that is written whole or not at all.
10. As a desk user, I want the end of an id to be enough when it names one row, so that I do not type `E0000`.
11. As a desk user, I want `reset` to put rows back to their source and `remove` to take a row I made away.
12. As a desk user, I want the result to say how the value was read and how many rows changed or already held it.
13. As a desk user, I want `set` to offer the rows, the fields and a field's values as I type.
14. As a desk user, I want a line the server cannot read to stay open with the word at fault marked and what it could
    be, so that I fix a word.
15. As the AI, I want one `entity.set` that takes a table, a selector and values for any application, and a result
    that carries the command's id, so that I can edit and take back my own edit by name.
16. As a developer of the next application, I want to register my tables and get `set`, `reset`, `remove`, `undo`
    and their completion with no command written, on the server or the client.

## Implementation Decisions

### 1a. Commands in the log, `undo` and `redo`

- **A stamp on every entry.** `EditEntry` gains `Command`, a `CommandStamp(Id, Said, Undoes, Redoes)`, and
  `Replaced`. `WorkspaceService.Apply` makes one stamp per accepted call, `c` and the first sequence number it
  writes, and every entry of the call carries it, the derivation's included. `Batch` takes the command's words
  (`said`) where its caller has them. `Create` of a workspace stamps its two entries too.
- **What was replaced.** `Replaced` is a record: the `Override` and the `LostTarget` a `Set` or a `Reset` found on its
  target; for a `Remove`, the `CreatedEntity` and the overrides, conflicts and lost edits that ended with it; for a
  context change, the context before and the source version before. A `Create` replaces nothing.
- **Taking back** is one private operation over a command's entries in reverse: a `Set` or a `Reset` puts back the
  override and the lost edit it found, or removes the override where it found none; a `Create` removes the entity; a
  `Remove` puts the entity and its edits back; a context change that kept the source version adopts the context
  before. Each step appends an entry of the kind that says what it did (`Set`, `Reset`, `Remove`, `Create`,
  `Context` and `Acquire`), with what it replaced, under the stamp of the `undo` or the `redo`. A `Derive` entry is
  not inverted: the derivation runs after, as after any command.
- **`Undo(workspaceId, revision, user, command?, origin)` and `Redo(…)`** on `WorkspaceService`, each answering the
  new state and a `TakenBack` (the command's id, its words or its edits' titles, its user, its origin, when, and how
  many edits). The stacks are read from the log (`CommandHistory`), as ADR-0016's note 2 says. Refusals:
  `NothingToUndo`, `NothingToRedo`, `CannotUndo` (naming what stands in the way), `NotYourCommand` (naming the
  user), `NotLatestCommand` (a named id that is not the top), `RevisionMoved`.
- **Titles.** `IEditRules.TitleOf(state, target)` names an edit's target as the screen does; its default is the
  entity and the field. The pricer's rules answer a row and its column (`CSA-A threshold`), `Before on the existing
  trades`, `the measures`, `the display currency`.
- **The store.** `EditLog` gains `command`, `said`, `undoes`, `redoes` and `replaced_json`, added to a log written
  before them, whose entries then carry none and are not taken back.
- **Two capabilities in the kernel**, `workspace.undo` and `workspace.redo` 1.0.0, with the effect `EditWorkspace`:
  inputs `{ workspaceId, revision?, command? }`, of which the revision or the command is given and the AI's names the
  command (§11); output `{ workspaceId, revision, command, said, took, by, at, edits, origin? }`, flat as every contract
  is (the ADR's note 12). They check that the caller owns the workspace.
- **The page's door.** `POST /api/commands/{id}/{version}` invokes a command for the signed-in user with the origin
  of the page, and answers as the agent's door does.
- **The client.** `Api.command(id, version, inputs, idempotencyKey)` in `platform/api.ts`. `undo` and `redo` are
  commands of the line, available on a page that offers a workspace (the pricer's in this step; Rates' with its
  registration, step 3). The page runs them after the edits already sent, reads its view again and tells its grids. The
  notice says what was taken back and that `redo` puts it back. The pricer's grids give ExGrid `onUndo` and `onRedo`,
  which run the same two commands.

### 1b. Forms, tables, the selector and the edit verbs

- **Forms.** `FieldForm` is `Period`, `Date`, `PeriodOrDate`, `Amount`, `SignedAmount`, `Percent`, `Rate`, `Spread`,
  `Number`, `Days`, `Choice`, `Flag`, `Id`. `FieldForms.Read(spec, text)` answers the value or what the field is
  ("an amount: 5mm, 250k, 1bn, or a bare number of millions"), and `FieldForms.Write(spec, value)` the spelling that
  reads back to the same value. A spec is the form, the choices, and whether the field may be empty.
  `ProductReader` and `PortfolioCells` read through it and keep their sentences.
- **Tables.** `TableDescriptor(TableId, Version, App, Title, Words, Key, Fields, Subsets, SingleRow)`,
  `TableField(Name, Title, Words, Form, Editable, Optional, Choices, Table, UnitField, Group)` and
  `TableSubset(Name, Title, Words)`. A domain registers an `IWorkspaceTable` per table through `ITableCatalog`
  (`TablesOf(app)`), as products come through `IProductCatalog`: `Describe(state)` (the fields resolved for the
  workspace), `Rows(state)` (each row's key, subset and values by field), `AsOf(state)` (the date a period counts
  from), and what a write is: `Set(state, row, field, value)`, `SetAll(state, subset, field, value)` where the field
  has a whole-set target, `Reset(state, row, field?)`, `Remove(state, row)`. A write answers workspace changes or
  refuses by name; the kernel knows no financial word.
- **The selector.** `RowSelector(Table?, All, Subset?, Ids, Where)` with `Condition(Field, Operator, Value)`, the
  value as written. `SelectorReader.Read(words, tables)` reads the line's spelling into it, and
  `Selectors.Resolve(selector, tables, state)` answers the table and its rows, or refuses with the word at fault and
  what it could be. Resolving is linear in the rows.
- **`entity.set` 1.0.0** takes `{ workspaceId, revision, rows, set: [{ field, value }], said? }` and writes one
  all-or-none batch at that revision. Its output is the result of §6: `{ workspaceId, revision, command, said,
  table, matched, changed, held, rows }`, where `said` is the sentence (`Set A to off on 9 trades; 3 already held
  it.`) and `rows` the first few keys. **`entity.reset`** takes `{ …, rows, field? }` and **`entity.remove`**
  `{ …, rows }`, with the same output.
- **XVA registers** `xva.trades` (subsets `new` and `existing`, whole-set targets for `before` and `after`),
  `xva.csas`, `xva.netting`, `xva.credit`, `xva.cds`, `xva.workspace` (the display currency) and one table of one
  row per module that has settings. `PortfolioCells`' column tables become these registrations, and its `Read` and
  `Write` the kernel's. `xva.set` and `PortfolioWorkspaces.SetCells` go; the AI's tool is `entity.set`.
- **Linear.** A batch resolves its rows once. Measured at 10,000 rows with its `undo`, on a Release host with the
  SQLite store, and written into the status; not gated.

### 1c. The preview and the line

- **The command form.** `CapabilityDescriptor` gains `Command`, a `CommandForm(Words, Arguments)` with
  `CommandArgument(Name, Kind, Required)`, kind one of `rows`, `assignments`, `field`, `text`. The catalog serves it.
- **`POST /api/commands/preview`** takes `{ line, app, workspaceId, revision }` and executes nothing. It answers
  `{ command: { id, version }, inputs, arguments, rows: { table, title, count, first }, would, complete }` or
  `{ refused: { code, sentence, word, start, end, candidates }, complete }`. `would` is the sentence of what would
  change (`trades, 12 rows; A: 9 on to off, 3 already off`). `complete` is what could follow the last word: rows,
  `all`, subsets, `where`, fields, operators, a field's values.
- **The line.** A command whose form the catalog serves is built from it: `set`, `reset`, `remove`, `undo`, `redo`.
  While its line is typed the client asks for the preview (the latest answer wins, an older one is dropped), shows
  `would` or the reason under the line, and offers `complete`. Enter invokes the command with the preview's inputs
  for exactly the text on the line, asking first where no answer for that text has come; while the line cannot be
  read Enter runs nothing and the line stays open with its text. `setTyped`, `setHint`, the page's `set` and
  `POST /api/xva/workspaces/{id}/set` go. `vary` keeps its reader until step 3.

## Testing Decisions

A test names the ADR it holds. Tested at these seams, each through its public interface:

- `WorkspaceService` (`WorkspaceUndoTests`): one command id per call; what each kind of entry replaced; `undo` and
  `redo` of a set, a reset, a batch, a create, a remove with its edits, a context change; sequence numbers kept;
  the refusals; `undo` after `redo`.
- `SqliteWorkspaceStore` (`SqliteWorkspaceStoreTests`): a command taken back after the workspace is loaded again;
  a log written before the columns still opens.
- `CommandService` with a non-financial registration, station readings (`TableCommandTests`): `entity.set` by id,
  by the end of an id, by `where`, over a subset; several fields; all-or-none; counts; `entity.reset`;
  `entity.remove`; `workspace.undo` of each.
- `FieldForms` (`FieldFormTests`): each form read and written back; what a refusal says.
- `SelectorReader` and `Selectors` (`SelectorTests`): the spellings of ADR-0016 §2 and note 8; the refusals with
  their candidates; amounts across currencies.
- `CommandService` with XVA registered (`PricerTableTests`): `set where ccy=USD after off` with its counts and its
  `undo`; `set existing before on` as one edit; `set CSA-A threshold 5mm`; a module's setting; the display currency.
- HTTP (`CommandGatewayTests`): the page's door and the preview, their statuses and shapes.
- Client, Vitest: what the line does with a preview's answer (pure functions). The sentence for `undo` is the server's,
  held by `CommandGatewayTests`.
- Client, Playwright (`xva.spec.ts`): `undo` and `redo` typed; Ctrl+Z and Ctrl+Y in a grid; `set where …` shows its
  count before Enter and its result after; a line the server cannot read stays open; `set` completes.

Not tested: the kernel's private stacks, and anything a test of the public seam already holds.

## Out of Scope

- `selected` and `shown`, the context on the line, `go`, `find`, the chain key, context menus (step 2).
- The pricer's other commands through the gateway, Rates' registration and its `undo` on the page (step 3).
- View verbs and the AI's row read; the row contract and `TypedDataRef` of a table (step 4).
- Templates, `set … as <template>`, tables under a row, fields of several values, drafts (step 5).
- Saved sequences (step 6). A layer on the line (`layer=after`), open in ADR-0016.

## Further Notes

Providers are unchanged: every figure on these pages is simulated, and says so.
