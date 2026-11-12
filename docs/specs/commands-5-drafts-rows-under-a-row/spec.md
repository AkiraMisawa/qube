# Generic commands, delivery step 5: rows under a row, templates, and drafts

Status: in progress (5a and 5b built; 5c next)

The decisions are ADR-0016 §1 (a table under a row, a figure, templates) and §10 (a dialog is a draft), with its
notes of 2026-11-11 for step 5 (46 onward). This spec orders ADR-0016's Delivery order, step 5, and names the seams
it is tested at. The owner asked on 2026-11-11 for every step to be implemented without a question at each one, and
for this step said questions were welcome: four choices of the dialog's look were put to him together before it
started and are in the notes' preamble. The seams below are DEFAULTs and are reported, not asked.

## Problem Statement

A grid has no room for everything a row holds. The owner's examples (2026-11-10) open a detail dialog from a button
in the row: a trade's identity, agreement, dates and model, its PV legs and its independent amounts; a CSA's general
terms, each side's terms, and four templates that fill the form for a currency. The workbench has none of it: a
trade is the columns of its grid, a CSA the columns of its own, and nothing is addressed below a row.

The command model has no word for it either. A table's rows are all of one level, so a leg cannot be named; a field
is a value of its row, so a PV that is the sum of legs cannot be registered or overwritten; a set of values cannot
be written as one named thing; and a change is committed the moment it is read, so there is nothing to confirm or
to discard.

Two things wait for this step by earlier notes: the pricer's scenarios as a registered table (note 32), and trades
that already exist read into the workspace by id (note 37).

## Solution

**5a. Rows under a row.** A table may be under another's rows. Its rows are said as any rows are, or all of one
row's at once by that row's id. A field may be a figure of the rows under its row, read as a number and written as
the table decides. A number may be written with what it is in (`-350,000 USD`). `new` makes a row under a row, and
`remove` takes one away. The pricer registers a trade's PV legs and independent amounts, and a trade's PV as the sum
of its legs in the display currency, overwritten as the owner's rule says.

**5b. Templates, and the fields the examples show.** A table registers templates: named sets of field values that
take a few values of their own. `set <rows> as <template>` writes one over rows. The pricer's CSA and trade carry
the other fields of the examples, each under its group, and the CSA its four templates.

**5c. Drafts and the detail dialog.** `open <row>` opens a dialog drawn from the row's registration: its groups as
sections, the tables under it as grids, its templates as buttons, with tabs Original and Live. What is changed in
it is a draft: a list of lines read by the server on top of the workspace, committed as one command by `apply` and
discarded by `cancel`. While it is open the line acts on the draft. A grid's row has one button, edit.

**5d. Scenarios as a table, and `load trades`.** The pricer's scenarios are a registered table with each one's
shifts under it. `load trades <ids>` reads booked trades from a synthetic store into the workspace's new trades.

## User Stories

1. As a desk user, I type `set legs E00003/1 pv 250,000` and one leg of the trade changes (5a).
2. As a desk user, I type `set E00003 pv -350,000 USD`, the line says "2 legs replaced by 1 leg, USD" before Enter,
   and `undo` or `reset E00003 pv` brings the two legs back (5a).
3. As a desk user, I type `new leg E00003 eur 250,000` and the trade has one leg more; `remove legs E00003/3` takes
   it away (5a).
4. As a desk user, I see each trade's PV in the grid, in the display currency (5a).
5. As the AI, I read `legs E00003` with `tables_read` and learn from `tables_describe` that legs are under trades
   and that a trade's PV is a figure of them (5a).
6. As a desk user, I type `set CSA-B as 2way ccy=USD` and the CSA's terms are a two-way CSA's in dollars; without
   the currency I am told the template needs it (5b).
7. As a desk user, I name any field of the owner's examples in a command, whether or not a grid shows it (5b).
8. As a desk user, I press a row's edit button, change three fields and a leg in the dialog, and Save writes them
   as one command that `undo` takes back whole; Cancel writes nothing (5c).
9. As a desk user, with the dialog open I type `set notional 5bn` and the field fills; `apply` saves, `cancel`
   discards, and `price` is refused while the draft is open (5c).
10. As a desk user, a value the server refuses leaves the dialog open with that field marked (5c).
11. As a desk user, I look at the Original tab to see what the source holds, read-only (5c).
12. As a desk user on a phone, the dialog fills the screen and every field is reachable (5c).
13. As a desk user, I type `set scenarios "parallel up" in off`-like lines through the same verbs as any table
    (5d; the spelling is the note's).
14. As a desk user, I type `load trades B00012, B00013` and the two booked trades join the new trades (5d).

## Implementation Decisions

### 5a. Rows under a row (tickets 01 to 08; ADR-0016 notes 46 to 49)

- **Under.** `TableDescriptor.Under` names the table above; `ITableRow.Under` the key of the row above. A row's key
  is whole in its table. The selector reads an id of a table under another first among its own rows, then among the
  rows above, and then means every row under that one.
- **A figure.** `TableField.Figure` (`TableFigure(Of, Written)`) and `TableField.Unit`. `set` reads a figure's value
  as `Written` says, asks the table for each row's changes, and counts a row with none as holding it.
  `IWorkspaceTable.SetSays` is what the preview says for one row.
- **Money.** `FieldForm.Money` reads a number and, after it, a unit that is one of the field's choices. Its value is
  a record of the amount and, where written, the unit.
- **`new` under a row.** `ProductDescriptor.Table` and `Under`. `EntityCreate` reads the first word after the
  product's as the row above where it names one, and writes it into the terms by name and whole key.
- **The pricer.** Entities `leg:<trade>/<n>` and `ia:<trade>/IA<n>`, read from the source, edited field by field,
  made and removed as any entity is; a source's row is taken out by an edit of its own, so `reset` and `undo` bring
  it back. `pv` is the trade table's figure. The page's rows carry `pv`, and the grids show it.
- **What no measure reads.** The legs change no figure Price answers (note 49). The dialog says so in 5c.

### 5b. Templates and fields by group (tickets 09 to 12)

- **A template** is registered on its table: a name, words, a title, and the values it takes, each a field of its
  own with a form. The table answers the field values it writes for the values given
  (`IWorkspaceTable.TemplateValues`). `set <rows> as <template> [<name>=<value> …]` writes them as several values are
  written (note 9), each checked in order; a value it needs and is not given refuses it by name.
- **Completion** offers the templates after `as`, then each value it takes by name.
- **The pricer's fields.** The trade's and the CSA's other fields are registered with their groups, read from the
  synthetic source, and edited as any field is. A field the source holds and no one edits is registered read-only.
- **The CSA's templates**: two-way, one-way with our posting, one-way with the counterparty's posting, and none,
  each taking a currency.
- **What Price freezes** (added 2026-11-12 on the owner's word; the decision is ADR-0016 note 53). Each trade of a
  case's accepted input carries its legs, its independent amounts and, under `details`, its fields beyond the
  grids; each case carries the CSAs its trades use, with their fields beyond the grid under `details`. A value a
  user can set is in the input as set, whether or not a simulated measure reads it, and a test holds the input.

### 5c. Drafts and the detail dialog (tickets 13 to 17)

- **A draft is lines.** The page holds the row, the revision it was opened at and the lines accepted so far. The
  server reads them in order on the workspace as it stands and answers the form: each field with its group, its
  value as written, whether the draft changed it, and the tables under the row with their rows. Nothing is stored
  on the server.
- **The line acts on the draft.** The view a preview is asked with carries the draft. A line that edits the row or
  the rows under it is read on the workspace as the draft leaves it and answers a line to add to the draft, not a
  command to invoke. `apply` invokes one command that writes every change as one batch at the revision opened;
  `cancel` discards. Any other command is refused while the draft is open, saying so.
- **`open <row>`** is a verb of the screen: the page opens the dialog for the row. A grid's Action Column has one
  button that runs it.
- **The dialog** is a Mantine modal, full screen on a phone, drawn from the form: a section a group, a grid a
  table under the row, a button a template, tabs Original and Live. Original is the row as the source alone gives
  it, read-only.

### 5d. Scenarios as a table, and `load trades` (tickets 18 to 20)

- **Scenarios.** `xva.scenarios`, a row a scenario, with `xva.shifts` under it; the scenario verbs of note 32 stay
  as short ways to say the same edits.
- **`load trades <ids>`** reads the ids from a synthetic store of booked trades of the counterparty and makes each
  a new trade of the workspace; an id the store has not is refused by name, and none is loaded.

## Testing Decisions

Seams (DEFAULT, reported): the kernel through `TableService`, `CommandLines.Preview` and the command service over
the station tables, which gain a table under the stations and a figure of it, registered only for the tests that
ask for them so that no other test's sentences move; `FieldForms` directly for the form; the pricer through its
line and command rigs (`PricerLineTests`, `PricerCommandTests`, `PricerTableTests`); the client's dialog through
Vitest with the door stubbed, and one Playwright flow through the built client for 5c. A test names the note it
holds.

## Out of Scope

- A changed PV reaching a measure (note 49): ADR-0010 and ADR-0013 decide what a measure reads.
- Before and After tabs in the dialog: they come with ADR-0010's Advanced step (the owner's choice of 2026-11-11).
- View and download buttons in a row (the owner's choice of 2026-11-11): Original is a tab, and `export` saves rows.
- Saved sequences of edits (step 6).
