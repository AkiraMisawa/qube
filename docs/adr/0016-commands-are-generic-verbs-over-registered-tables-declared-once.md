# ADR-0016: Commands are generic verbs over registered tables, declared once for the line, the buttons and the AI

Status: ACCEPTED 2026-11-11. The owner asked that day for every step of the Delivery order to be implemented, each
through `/implement`, without a question at each step; this record takes that as his confirmation of §1 to §5, which
was the first item under Open. A decision marked DEFAULT stays a DEFAULT, and what is still Open is settled when its
slice is reached (CLAUDE.md, "Decision handling"). Proposed 2026-11-10, raised by the owner after a review of the
pricer's commands found that trades can be picked by command only as all, none, or a list of exact ids: "I want
commands to be as generic as possible. Not only the XVA trade selection: think about how commands should be designed
for operating this workbench's applications in general" (2026-11-10). Completes ADR-0006 §1 and its first consequence,
and ADR-0010 §9's generic `set`. Amends ADR-0005 §5 and ADR-0008's forms, and adds to ADR-0003's log entry. Changes
nothing a Run does. What is built is said step by step under "Delivery order".

## Context

What is generic today: the command model, its ranking and its slot completion (`client/src/platform/commands`);
`run <workflow>`, `show <view>` and `set <context field>`, which the shell builds from the catalog; `new <product>`
(ADR-0008); and a workspace's edits, which every domain writes as `EditTarget(scope, entity, field, layer)` at a
revision (ADR-0003).

What is not, on main at 4696820:

- **The commands themselves.** The pricer's and Rates' 47 commands are written by hand in
  `client/src/apps/index.tsx`, and the pricer's call some thirty-five functions the page registers while it is mounted.
  ADR-0006 decided that "a command a module registers through the catalog gets the same treatment, so no module edits
  the shell". The code does not do that yet.
- **Two doors.** The page writes through its own endpoints (35 in `XvaApi.cs`); the AI goes through the command
  gateway. Fourteen of the pricer's actions are command capabilities. Price, `measures`, `before`/`after`, `refresh`
  and the case commands are not, so the AI cannot do what the page does, against ADR-0006 §1 and ADR-0010 §9.
- **Two readers.** `set` and `vary` are read on the client (`setTyped`, `varyTyped`) and again on the server
  (`PortfolioCells.Resolve`). The client's reader throws inside the command's `run`, and the line closes without a
  word.
- **`set` is the pricer's.** Its rows and columns live in the XVA sample (`PortfolioCells`), though ADR-0010 §9 says
  "one generic command in the kernel, and each domain registers the rows and fields it reaches".
- **A column is declared several times.** A trade's columns are written in `EffectiveTrade`, in `TradeColumns` for
  `set`, and in the client's grid. How a value is written has four vocabularies (`FieldForm`, `CellName.Form`,
  `ColumnFormat`, `DatasetColumnType`), and the client's cell editor is a fifth: it takes `TRUE`/`FALSE` where `set`
  takes `on`/`off`.
- **Nothing says which rows.** There is `before all`, and a list of exact ids. Nothing names the grid's selection, the
  rows a filter leaves, or the rows that meet a condition.
- **A capability does not say how it is typed.** A `CapabilityDescriptor` has contracts and effects, and no words or
  argument forms, so the command line cannot be built from it. A view's row actions (`ActionDefinition`) and a grid's
  context commands are two more mechanisms beside it.
- **Rates shows the same gap from the other side** (its line driven in a browser on main at f37bbbb, 2026-11-10).
  The line makes lines (`new irs`, `new ccs`, `add line`), sets the defaults, starts and stops live, captures and
  prices, and says why when the server refuses. It cannot change a cell of a line, remove a line, pick lines, or open
  a leg or its flows: `set` there is the pricer's and answers "Open an XVA workspace first." A refusal from the server
  closes the line, and what was typed has to be typed again.

So a new command is written in up to four places (an endpoint, a capability, a client command, a page function), and a
new domain application cannot be driven by commands without client code. That is ADR-0002's acceptance criterion,
unmet for commands.

## Decision

### 1. A domain registers its tables

A **table** is a named set of rows of one kind that a command can address. A cell's address is `table row field`, the
shape `EditTarget` already has, so the kernel gains no new way to edit: it gains the description of what there is.
"Dataset" is not used for this: a `Dataset` is a document a capability outputs, and may be what a read-only table
shows.

**A table is written in the vocabulary the kernel already has, not in a registry beside it** (checked against the AI
and saved workflows at the owner's request, 2026-11-10). Its row shape is a versioned contract. A table at a revision
is a `TypedDataRef` (data id, contract, revision), which a workflow binding, a view's data and a row action already
take. So a workflow, a view and the AI reach a table with what they have, and the validator checks them as it checks
any binding.

A domain module registers each table as data, through a catalog port as products are (ADR-0008 §7):

- its **id and version** (`xva.trades` 1.0) and its row contract. A saved definition and an AI's call name these, never
  the words below, so the words can change without breaking what was saved;
- its **words** (`trades`), the workspace app it lives in, and whether its rows come from a **workspace** (editable) or
  a **Run** (read-only);
- its **key**, the field that names a row;
- its **fields**: name, title, the words it answers to (`after`, `A`), its form, its **unit** or the field that
  carries it (docs/09: a unit or a currency is column metadata, never inferred from a title), whether it is editable,
  and for an id the table it points into (`csa` of a trade names a row of `csa`);
- its fields' **groups**, where it has them (a trade's identity, agreement, dates and model; a CSA's counterparty
  side and own side): a form draws a group as a section and a grid as a header group, and a field's words may be said
  with its group's (`cpty threshold`, `self threshold`);
- its **templates**: named sets of field values, which may take a few values of their own (a two-way CSA in a
  currency). `new` makes a row from one (ADR-0008 §4), and `set <rows> as <template>` writes one over rows (§3).
  A template is where a row starts, and it holds nothing afterwards (owner, 2026-11-10: a CSA template is a CSA only
  once its currency is given, and what it made is then changed further, which is the usual way to work). So a value
  a template needs and is not given refuses it, naming the value; the row it made is changed with `set` like any
  other; and a template changed later moves no row made from it (DEFAULT, read from the owner's words);
- its **subsets**, groups of rows the domain names (`existing`, `new`);
- its **row reader**: the rows at a revision. The selector, completion, the AI's read (§7) and a grid all use it.

**The fields are resolved for a scope, not fixed for an app.** The selected measures add trade columns (ADR-0010
§10), so what a table has is asked of a workspace. **An address is complete outside a page**: its scope (the
workspace or the Run), the revision, the layer where there is one (ADR-0010 §2), and for a result its case and
scenario. The command line fills these from the context; the AI and a workflow always pass them.

A table of one row (settings, a workspace's defaults, the shell's context) is addressed without a row:
`set currency JPY`. `set <context field>` is already built this way from the context contract.

**A table may belong to a row** (the owner's examples of 2026-11-10: a detail dialog shows what a grid has no room
for). A trade's legs, each with a currency, a PV and a notional, and its independent amounts are tables scoped by the
trade, as a CSA's eligible collateral is by the CSA. Their rows are addressed through the parent's
(`set legs E00003/0 pv 250,000`, `new leg E00003 eur …`, `remove legs E00003/1`), and by their own key where the
parent is the context (§9, §10). Whether a field or a table is on a grid, in a dialog or on no screen at all changes
nothing about how a command names it.

**A field may hold several values, or be a figure of the rows under its row.** A cross-currency trade has two
currencies: a field's form may be a set of a form (§2). A trade's PV on a grid is the sum of its legs' PVs in the
display currency: the registration says a field is such a figure, names its unit, and says whether it can be
written. What a write means is the domain's rule and never the kernel's guess. In the owner's example, writing a
trade's PV leaves the trade one leg of that amount, in the currency written with the amount (`-350,000 USD`) or, with
none written, in the display currency (owner, 2026-11-10). The preview says "2 legs replaced by 1 leg, USD", the
write is one command, and `reset` brings the source's legs back.

**One form vocabulary.** `FieldForm` (ADR-0008) takes in what `CellName.Form` has and it lacks (an id of a table, a
signed amount, a schedule, a spread, a number of days). How a column is drawn (`ColumnFormat`) and stored
(`DatasetColumnType`) follow from its form. The command line, a grid cell and the AI all write a value in the same
spellings, read once, on the server: a flag is `on`/`off`/`yes`/`no`/`true`/`false` wherever it is typed.

**The domain's rules stay the domain's.** Validation and following (a CSA's discount curve reaching the trades that
name it) remain its `IWorkspaceRules`. The kernel turns `(rows, field, text)` into edits and knows no financial word.

### 2. One selector says which rows

Every command that takes rows takes them in one grammar:

| Written | Means | Resolved by |
|---|---|---|
| `E00003, E00004` | Rows by id. The end of an id is accepted when it names exactly one row; otherwise refused with what it could mean. | server |
| `all` | Every row of the table, whatever the grid's filter (docs/03 §4). | server |
| `new`, `existing` | A registered subset. | server |
| `selected` | The rows the grid's selection touches. | client where it holds them, else server from the view |
| `shown` | The rows the grid's filter leaves. Never called `all`, always counted (docs/03 §4). | the same |
| `where ccy=USD and maturity>5y` | Rows by their values, each value read by its field's form. | server |

`where` is kept small: `=`, `!=`, `<`, `<=`, `>`, `>=`, `~` (contains) and `in`, joined by `and`. There is no `or` and
no parentheses: it has to be typed in one line by a person and built reliably by a model, and it can grow later
without breaking what was written. A subset or `selected` may be narrowed by `where`. On a field that holds several
values, `=` and `in` are true when one of them matches and `!=` when none does, and the preview says so ("has
USD").

**The selector is a typed value, and the line is one spelling of it.** Its contract holds the table, a subset, ids and
conditions (field, operator, value). The command line's text is read into it; the AI and a workflow pass it as it is;
one resolver serves all three.

`selected` and `shown` are not words the server reads; the client sends what they mean. Where it holds every row it
sends ids. The trade grids are paged and sorted on the server (ADR-0010 §6), so the client holds a window and cannot
name a row it has not loaded: there it sends **the view it shows**, which is the table, the subset, the filter's
conditions, the sort and the selected row ranges, at a revision, and the server resolves the ranges with the row
reader that pages the grid. The rows are then the ones the user saw, or the command is refused because the workspace
moved. The AI is given the selection in its context (ADR-0007 §8, a description and no authority) and names rows
itself; a saved definition holds neither word, and may bind rows from the context it is run with.

**An amount is not compared across currencies.** A condition on an amount whose currency differs by row is refused
unless the currency is pinned too (`ccy=USD and notional>100mm`). A shape that matches is not an economic comparison.

Three rules make the outcome independent of timing:

1. **The count is shown before Enter**: the table, how many rows, and what would change ("trades, 12 rows; After: 9
   on to off, 3 already off").
2. **A command carries the revision it was read at**, as an edit does today. A workspace that has moved on refuses it.
3. **The log records the rows that matched, not the condition.** A trade added later that also matches is not
   switched off silently.

A whole set is the exception that stays one edit: a table or a subset may register a whole-set target, as each trade
grid's B and A have (ADR-0010 §6: one edit on the grid, a row's later edit wins). `set existing …` writes that one
edit, and `set all …` writes one for each subset. Rows chosen by id or by `where` are one all-or-none batch.

"Selecting trades" meant two things, and they are now two words. The grid's selection is view state: `selected` names
it and `select <rows>` moves it. Membership of Before and After is the value of two flag fields:
`set <rows> after off`.

### 3. A small set of verbs in the kernel

**Edit verbs**, over any workspace table, each a command capability (ADR-0005) beside `entity.create`:
`set <rows> <field> <value>` (`entity.set`, replacing `xva.set`), `reset <rows> [<field>]` (`entity.reset`),
`remove <rows>` (`entity.remove`), and `undo` and `redo` (§11). `set` takes several fields as `name=value`
(`set E00003 notional=5bn rate=1.25%`), written as one all-or-none batch at one revision, and
`set <rows> as <template> [name=value …]` writes a registered template's values the same way: the fields the
template names, with the values it was given, and no other field of the row (DEFAULT; the preview names each cell).

**View verbs**, over any grid bound to a table, in the client only: `open <tab, view or row>`, `go <place>` and
`find <row> [<field>]` (§9), `select <rows>`, `filter <table> where …` and `filter off`, `sort <table> by <field>`,
`copy <rows> [<fields>]`, `export <table>`, and `apply` and `cancel` for an open draft (§10). They change no
workspace, are not logged and are not the AI's (CLAUDE.md: the AI calls capabilities, not grid automation). A view
declares its tabs, places and options; the shell builds their commands, as it builds `show`. `filter` and `sort` set
the view's conditions, and the row reader applies them where the server pages the grid, as it sorts the trade grids
today (they refuse a filter until it does).

**Domain verbs** are capabilities a domain registers, and only for what is more than writing cells: something
**executes** (`price`, `refresh`, `capture`, `live start`), one command **makes many things** (`vary`, `add each`,
`scenario ladder`), the command **works out what to write** (solve a line's rate, roll the picked trades to their next
date), or something **leaves** the workbench (`download run`, `copy quote`). Their arguments use the same forms and
the same selector, so completion, the preview and the result are theirs without code.

Three acts on trades have three names (the review's R8). The owner, 2026-11-10: loading and adding are different
acts and must read differently, and the second is `each`. `load trades <ids>` brings trades that exist into the New
grid, by id or from a file, and computes nothing (DEFAULT name). `add each` makes the cases of iterative individual
addition, one per candidate (ADR-0010 §3): it adds no trade, so it does not say "trades". `new <product>` makes a
trade from typed terms (ADR-0008). `add trades`, which ADR-0010 wrote for the first two, is retired when these are
built; until then the code says `add trades` for the second and has no command for the first.
*Note of 2026-11-11 (step 3a, note 28):* the second is now typed `add each`, and `add trades` is no command.

**An iteration of `add each` is a change of After's membership, given as lists** (the owner, 2026-11-10 and
2026-11-11: an iteration that adds several trades is more flexible, better still one that takes trades out in the
same case, and it is passed a list in some form; how the line spells the list is left aside). The command's
argument is a typed value, as the selector is (§2): a list of iterations, each a list of the candidates it adds and
a list of the existing trades it takes out of After. The AI and a saved sequence pass that value as it is. With no
argument `add each` makes one case per New row with A ticked, as `add trades` does today. An iteration that adds
`DRAFT-1` and `DRAFT-2` and takes out `E00003` compares Before with After less `E00003` plus the two, so the trade
taken out stays in Before and the difference is the whole change; its case names all three. The rules, all DEFAULT:

- **The set is still every New row with A ticked** (docs/03 §5). A ticked candidate that no iteration names is one
  iteration alone, and a candidate may be in several (alone, and with another). A candidate named with A clear, or
  with B ticked, is refused by name and never ticked or unticked for the user.
- **What held holds**: no iteration is cumulative, the Base holds every candidate, a change common to every case is
  made in the grid and said in each case, and `add each` again makes the set anew.
- **A trade taken out is one the Base has in After**; any other is refused by name. An iteration adds one candidate
  at least: a case that only takes a trade out is `vary E00003 A off`, which the code makes and prices today.

**How the line spells the lists is open.** One spelling, not decided:
`add each DRAFT-1+DRAFT-2 without E00003, DRAFT-3`, where `+` joins the candidates of one iteration, `without` names
what it takes out and a comma starts the next. Whatever is chosen, no bare sign means "take out", because an id may
hold a hyphen (`DRAFT-1`).

**Without the line, the lists are the grids' selections** (DEFAULT, proposed 2026-11-11 when the owner asked how
this is supported with no command; not confirmed). On the pricer's page, each of these calls the same capability
with the same value (§4):

- a button `Add each` in the New grid's caption, beside `A all` and `A none`: the command with no argument, which
  only the line reaches today;
- a button, and the same item on the Context Menu, that makes one iteration from the selection: the rows selected in
  the New grid are the candidates it adds, and those selected in the Existing grid the trades it takes out. Pressed
  again with another selection it makes the next. It needs the selection given to commands (§2, Delivery order 2):
  the page reads no selection today;
- the Changes tab, which already lists each case and removes it, shows an iteration's two lists and removes one
  trade from either.

**No switch is needed** (the owner, 2026-11-11: the mode switch he has used for iterative pricing need not come
back, so long as what replaces it is as simple). It is as simple. `add each` with no argument, or its button, makes
the iteration of every New row with A ticked at once, ten trades or one, and Price follows: two steps, as the switch
and Price were. A trade is named only for an iteration that is not one trade alone. What the switch did beyond that
was to follow the ticks. Here a set that no longer matches them is refused at Price by name, never priced as it was
(ADR-0010 §3), and `add each` again, one command or one press, makes it anew.

A flag of the workspace that keeps the set in step was proposed and withdrawn the same day. It stays possible later
without changing the above: it would keep one iteration alone for each ticked New row that no iteration made by hand
names, and never remake one made by hand. A column of ticks per case beside B and A, trades down and cases across,
would show every iteration at once and be edited in place; it costs a column a case and is left for later.

In the workspace an iteration stays what it is today, a case whose layer switches A off on rows (ADR-0010 §3), so the
log and Price's requests gain nothing. What changes when it is built: the reading of the line, a case that names its
trades as a list, and Price's check of the set, which reads the iterations' candidates together.

Sorted this way, of today's 47 hand-written commands 12 become kernel edit verbs, 14 become view verbs, 2 come from
the app's registration, and 19 remain domain verbs, declared on the server instead of the client.

A domain may register **short forms** that expand to a kernel command. `before all` acts on one grid today, the one
the keyboard was last in or the one named, so it is `set existing before on` or `set new before on`, with the grid
shown in the line before Enter (§9); `set all before on` is every trade of both grids. A short form has no behaviour
of its own. Users still define no aliases (ADR-0006 §6).

**Generic first, and a special command where it saves real work** (the owner, 2026-11-10: pages made later and
user-defined flows are served by the generic commands as far as they go, and a special command is right when it makes
something much easier). A page made later registers its tables and has the edit and view verbs with no command
written. A saved workflow's result is a table at a `TypedDataRef` (§8), so the view verbs reach it the same way, and
`run <workflow>` is built from the catalog as today. A special command is then added for what is typed often and is
long, or needs a calculation, when said with the generic ones. It is a short form or a domain verb as above, and it
stays a command like the others:

- it is declared once on the server (§4), with the same forms, selector, preview and result;
- what it writes goes through the same batch as `set` (§5), so the preview names the cells, the log records them and
  `undo` takes them back (§11);
- it does not take the generic path away: what it does to cells can still be said with `set`, and the AI and a saved
  definition may use either;
- the line shows what a short form expands to before Enter.

What this ADR removes is the other kind: a command only one page understands, read on the client, or written past the
gateway.

### 4. A command is declared once, on the server

A command capability's descriptor gains its **command form**: its words, and for each argument its name, whether it is
required, whether it may be typed without its name, and its form or the table whose rows it takes. The catalog serves
it, as it serves capabilities and contracts today.

- The **command line** builds its entries, slots and completions from the catalog.
- A **button** or a **context menu item** names a command id and fixed arguments, and can show the line that does the
  same. A row action of a declarative view (`ActionDefinition`) gains a `command` target beside `openView` and
  `runWorkflow`.
- The **AI's tools** are the same descriptors, as the fourteen are now.

This is what ADR-0006 §1 and its first consequence already require. Only view verbs are declared in the client.

### 5. One gateway, and a preview (amends ADR-0005 §5)

The page's commands go through the command gateway too: `POST /api/commands/{id}/{version}`, the caller and its origin
set by the server as now. ADR-0005 §5 had the page's endpoints and the commands share the application service; they
now share the door, and the page's write endpoints go as their commands move. Reads stay endpoints.

`POST /api/commands/preview` takes the line as typed with its context (workspace, revision, the focused table, the
selection's ids when `selected` or `shown` is typed) and executes nothing. It returns the command it read, each
argument as read and where it came from (typed, default, context), the rows resolved (count and the first few), what
would change, or the word that cannot be read, why, and what it could be. This is ADR-0008 §6's product preview for
every command. **The server owns the reading**: the client completes and shows, and reads nothing itself. While a line
cannot be read, Enter runs nothing, the line stays open and keeps its text.

### 6. One result

`CommandResult.Done` carries a sentence in the registered titles (never `selection:measures`), how many rows changed
and how many already held the value, the new revision, what undoes it, and where to look (table and rows).
`Refused` carries its code, a sentence, the word at fault and the candidates. The line colours a notice by this kind
and never by its text. A command that changed nothing is done, and says why.

### 7. The AI describes, reads and previews with the same model

The agent is offered one tool per command capability (ADR-0007 §3). Verbs of each domain's own would add tools with
every application (the pricer alone has fourteen); the kernel's verbs stay a handful whatever is registered. Three
capabilities make that enough:

- **Describe.** A tool's arguments are a contract's fixed schema, which cannot list a workspace's tables or fields. One
  capability answers, for a scope, its tables, their fields, forms, units and which are editable. It is the
  registration, read out. Without it the agent learns a field's name only from a refusal.
- **Read.** A table, a selector and the fields wanted; the answer is the count and a bounded page, never every cell
  (docs/09), and it may be retained as a dataset a view binds, so "show me the USD trades" is answered with a grid.
  Today the agent has no way to read a trade row.
- **Preview.** §5's preview as a capability, so the agent can say what a command would change before it runs it.

The agent has no page: it names the scope in every call, as the pricer's commands already take `workspaceId` and an
optional `revision`. Because a command is declared on the server, none of this needs the page open (AI03).

### 8. A workflow reads tables; it still does not edit

Reading a table at a pinned revision is a function of its inputs, so it may be a workflow step and the source of a
step's `forEach`: "for each USD trade, compute" needs no code. A saved definition holds ids with versions and the
selector as written (the condition, not the rows it matched); each execution resolves it at its revision and freezes
the rows in its accepted input, as Price freezes a revision. A definition that names a field a measure adds is
validated again when it is rebound (CO04). Because fields are registered, a definition is checked when it is saved:
that `csa` is an editable field of the table, and that the value has its form.

A command is still not a workflow step (ADR-0005 §2; the validator's `CommandAsStep`), so a Run stays a function of
its accepted input: a retry would edit twice. A saved sequence of edits is therefore not a workflow.

**A saved sequence of edits is a second kind of saved definition** (owner, 2026-11-10: wanted, and not at once). A
user saves "clear After, put the USD trades back, price" under a name: an ordered list of commands, holding
selectors as written and never the rows they matched, so it serves another counterparty. Running it invokes each
command through the gateway for the caller, with origin `workflow`: each is in the edit log and `undo` reaches it,
the run stops at the first refusal and names the revision it began at, and it may end by submitting a workflow at
the revision it left. Its shape is a DEFAULT until its spec is written; it follows delivery step 5.

### 9. Focus is context: the line reads it, shows it, and moves it by name

Raised by the owner (2026-11-10): operating by a chain of commands with no mouse needs commands that move the focus
between a page's components and commands that act on where the focus is.

Today the line gives the keyboard back to where it was, `open <tab>` puts it in the tab (ADR-0010 §9), `focus grid`
reaches a panel's first grid only, and the pricer's `before all` acts on "the grid the keyboard was last in", which
nothing on the screen shows. No command acts on the focused row or cell.

- **Read.** ADR-0006 §5's context ("Rates · W-12, revision 9") gains the focused table, its selection and its focused
  cell ("trades (existing), 3 selected, E00003 · csa"). A table left out is the focused grid's table; rows left out
  are that grid's selection (`set csa CSA-B`); a command on one cell (`explain`, `reset`) takes the focused cell. Each
  is shown in its slot as "from context" with the count before Enter (ADR-0006 §4), and a command whose target would
  be empty is refused with that reason.
- **Snapshot.** The focus is read once, when the line opens, and the client makes it explicit: the request carries
  the table, the rows (their keys, or §2's view and ranges where the grid is paged) and the revision, never "the
  focused row". The server, the log and a result never know a focus, so an outcome cannot depend on where the keyboard
  went while the line was open.
- **Move by name.** A view declares its **places**: each grid, tab, form and the dock, by a word. The shell builds
  `go <place>` from them, as it builds `open <tab>`, and `find <row> [<field>]` puts a grid's Focus on a row or a
  cell. `focus grid` and the panel chords are `go` with a key. ExGrid's handle already takes these (`focus`,
  `placeSelection` and `placeMatch`, each refused when the row order it was taken against has moved).
- **Where the keyboard is afterwards.** A command that edits or executes returns the keyboard to where it was. Only
  `go`, `find` and `open` move it, and they say where it went.
- **Chain.** Ctrl+Enter in the line (owner, 2026-11-10) runs the command and keeps the line open: its result is shown
  in the line, the text is cleared, the context is read again, and the list completes the next command from its first
  letter as it does when the line opens. So `go existing`, `select where ccy=USD`, `set csa CSA-B`, `price` is four
  lines and no reach for the mouse. It is a key of the line, not a shell chord (ADR-0006 §8), and is checked with an
  IME composing before it ships.

Not supported, deliberately:

- **Relative moves as commands** (`next`, `down 3`). The grid's own keys do this, and a typed step's outcome depends
  on a position no line shows.
- **Focus in what is not a person at this screen.** The AI names its scope and never moves the focus (ADR-0007 §7);
  a saved definition holds ids and a selector (§8). `selected`, `shown` and `go` are refused for both.
- **Several commands in one line.** One preview could not show what the second would read after the first, and a
  refusal in the middle leaves half done. The chain key gives each command its own preview and result.

### 10. A form or a dialog is a pending command

Raised by the owner (2026-11-10): a grid short of room shows a trade's other fields in a detail dialog, opened by an
icon button in a column of the grid, and a command should change values in the dialog, confirm them and close it.

- **A dialog's fields are the table's fields.** A grid shows some of a table's fields and a detail dialog the rest;
  whether a field is on the screen and whether a command can name it are separate. `set E00003 notional=5bn` needs no
  dialog, and is the form the AI and a saved definition use: they never drive a dialog.
- **The dialog is a draft of one batch**, drawn as a form from the registration, as the dialog `run <workflow>` opens
  is drawn from the workflow's input contract today: the fields' groups are its sections, and the tables that belong
  to the row (§1) are grids in it. A draft is view state: the row, its layer, the revision it was opened at, and the
  changes, which are fields set and rows added to, changed in or removed from the tables under the row. Each change
  is read by §5's preview, so a field's refusal and what follows from it are the server's and the dialog has no
  rules of its own.
- **The button, the keys and the line are one command.** The icon button is a row action naming `open <row>` (§4);
  ExGrid's Action Column reports the action and the row, and a button in a Template Column calls the same. With the
  dialog open the line acts on the draft: `set notional 5bn` (no row) fills the field, `new leg eur 20mm` and
  `remove leg 1` change the rows under it, `set as 2way ccy=USD` fills a template as a template button does, `apply`
  invokes the batch, closes the dialog and returns to the row, `cancel` discards. A refusal leaves the dialog open
  with the field at fault marked (an issue's path names it). A row with several actions (edit, view, download) has
  one row action for each.
- **A dialog shows one layer.** The owner's examples show the original beside the editable layers as tabs. The
  original is read-only, a draft belongs to the layer it was opened on, and the tabs are places (§9).
- **The same `set` is never ambiguous about where it goes.** A grid cell commits each edit (ADR-0003); a draft commits
  on `apply`. While a draft is open the line says so ("editing E00003, 2 pending") and the preview says "pending";
  only the draft's own `set`, `apply`, `cancel` and view verbs are available, and the rest are listed unavailable with
  that reason. Price takes committed state only (CLAUDE.md), so `price` is among the refused.
- **A draft carries its revision.** If the workspace moved while it was open, `apply` is refused with what changed.

### 11. `undo` takes back the workspace's last command, and `redo` puts it back

Wanted by the owner (2026-11-10). ADR-0003 already makes an undo a new log entry. There is no command for it, and the
log cannot yet say which entries were one command, or what an entry replaced.

- **A command is one accepted write.** The workspace service stamps the entries of one accepted call (a `set`, a
  batch over thousands of rows, a cell typed in a grid, an `apply`) with one command id, whichever door it came
  through, and each entry records what it replaced. The log alone is then enough to write a command's inverse.
- **`undo` is linear.** It appends, as one all-or-none batch, the inverse of the workspace's latest command not yet
  taken back: a `Set` becomes the value or the reset it replaced, a `Reset` the override it removed, a `Create` a
  `Remove`, a `Remove` the row as it was. `undo` again takes back the one before. Nothing was written after the
  command it inverts, so there is nothing to merge and no cell that a later edit owns.
- **`redo`** applies again what the last `undo` took back, while that `undo` is still the workspace's last command.
  Any other command ends what can be redone.
- **It says what it will take back.** In the line the preview names the command, its rows, who ran it and when ("set
  where ccy=USD after off, 12 rows, the AI for you, 19:20"), and the request carries the revision, so a workspace
  that moved refuses it (§2). In a grid, ExGrid reports its undo and redo keys (`onUndo`, `onRedo`) and the page
  invokes the same two commands; no preview is shown there, so the notice says what was taken back and `redo`
  restores it.
- **Whose.** The caller's own commands, whether typed, clicked, or made by the AI for them. Where the latest command
  is another user's, `undo` is refused and names them. The AI's `undo` names the command id it means (a result
  carries it, §6) and is refused unless that command is the latest.
- **What it does not reach.** A command is taken back when its changes are `Set`, `Reset`, `Create` and `Remove`, or
  a change of a context field that takes no new source (the reporting currency), which goes back to the context it
  replaced (owner, 2026-11-10: `undo` reaches such a change).
  One that takes a new source or decides a held edit (a refresh, a change of counterparty) is not: `undo` then
  refuses and names it, and never reaches past it to an older command, whose inverse would be written against a base
  that has changed. A Run already priced is unchanged; the page shows it as of its revision, as after any edit. Saved
  items, view state and the text being typed in an editor are not workspace edits and are not `undo`'s.
- **A draft** (§10) has no `undo`. After `apply`, one `undo` takes back the whole batch.

## Consequences

- The kernel gains the table registry and its catalog port, the unified forms, the selector's reader, `entity.set`,
  `entity.reset`, `entity.remove`, `undo` and `redo` (with a command id and the replaced value on a log entry, an
  addition to ADR-0003's entry), the command form on descriptors, the preview, and the richer result. All are
  domain-free: a test registers a non-financial table (station readings, as ADR-0008's test does) and sets its rows by
  `where` with no kernel or client change.
- XVA registers its tables and `PortfolioCells`' tables leave the module; `setTyped` and `varyTyped` leave the client.
  Rates registers `lines` and its defaults.
- `client/src/apps/index.tsx` shrinks to what is view-only, and then to what views declare.
- The acceptance test of this ADR: a new domain application writes its table registrations and its domain verbs, and
  no client command code, and is driven by the command line, buttons and the AI.

### Delivery order (DEFAULT)

0. The review's fixes that need no decision (an error is always shown; notices by kind; one spelling for a flag; the
   list's order). Independent of this ADR. **Built 2026-11-11** (`docs/specs/commands-0-review-fixes`); what it settled
   is ADR-0006's notes of that day. A sentence that says how a value was read and how many rows it changed (§6) comes
   with step 1's result, not here.
1. Tables, forms, the selector, `entity.set`/`reset`/`remove`, `undo` and `redo`, the preview; XVA registers. `set
   where ccy=USD after off` works with its count and can be taken back, and `set` completes. **Built 2026-11-11**
   (`docs/specs/commands-1-tables-selector-undo`, three pull requests); what it settled is the notes below.
2. The grid's selection (`selected`, `shown`), the context on the line down to the focused cell, `go` and `find`,
   the chain key, context menus from declarations. **Built 2026-11-11**
   (`docs/specs/commands-2-selection-context-places`, three pull requests); what it settled is notes 16 to 26 below.
   Left for later steps: `shown` is the grid's subset until `filter` (step 4), and a declarative view's row action
   has no `command` target yet (note 25).
3. The pricer's remaining commands as capabilities through the gateway; Rates registers. **Built 2026-11-11**
   (`docs/specs/commands-3-domain-verbs-rates`, four pull requests); what it settled is notes 27 to 37 below.
   3a: a command's application, short forms, the pricer's verbs that edit, `open` from tabs. 3b: the pricer's
   scenario verbs, `price`, `refresh` and `pv01 retry` (notes 32 and 33). 3c: Rates registers its tables, its door
   and its verbs (notes 34 to 36). 3d: `add each` takes its iterations as a list, and a contract may hold one
   (note 37). Left for later steps: `load trades <ids>` (step 5, note 37), and how the line spells an iteration's
   lists, which stays open (§3).
4. View verbs and the AI's row read. **In progress** (`docs/specs/commands-4-view-verbs-ai-reads`, three pull
   requests); what it settles is notes 38 onward below.
5. Drafts (§10): a detail dialog drawn from a registration, with the tables under its row and its templates. The
   pricer's trade and CSA must carry those fields and tables first; its sample has neither them nor such a dialog.
   The pricer's scenarios as a registered table, a row computed from its shifts with their table under it, come
   with this step too (note 32).
6. Saved sequences of edits (§8). §1 and §2 serve them as they are.

### Notes of 2026-11-11: what step 1 settles (all DEFAULT)

Written before step 1 is built (`docs/specs/commands-1-tables-selector-undo`), for what the sections above leave to
the builder. Step 1 ships as three pull requests: the log with `undo` and `redo`; the tables, the forms, the selector
and the edit verbs with XVA registered; the preview and the line.

1. **What an entry records (§11).** A command's id is `c` and the sequence number of its first entry (`c12`), unique in
   its workspace. An entry records what it replaced as it stood: the override or the lost edit on its target, for a
   `Remove` the entity with the layer it owned and the edits that ended with it, for a context change the context
   before. Taking a command back puts those back as they were, their sequence numbers included, so that "a row's later
   edit wins" (ADR-0010 §6) reads as it did before the command. The entries an `undo` writes record what they replaced
   too, an entity put back the layer that came back with it, so `redo` is the same act applied to the `undo`, and a
   command put back can be taken back again. The derivation runs after an `undo` as after any command and decides on the
   edits of the scopes it moved again.
2. **What can be taken back (§11).** The stack is read from the log: a command joins it and ends what can be redone,
   an `undo` moves its top to what can be redone, a `redo` moves it back. `undo` refuses, by name, at a command that
   took another source version, left a scope or decided a held edit, at one recorded before commands had ids, and at the
   workspace's making. A value a workspace is made with, as the pricer's takes its owner's last selection of measures
   (ADR-0010 §10), is written by the making's own command, so `undo` finds nothing of it; and a selection taken back is
   no longer the owner's last. A change of context that keeps the source version (Rates' defaults and valuation date) is
   taken back; the pricer's display currency is an ordinary `Set`.
3. **How a command is named (§11, §6).** By the words it was given (`set where ccy=USD after off`) where its caller
   gave them, else by its edits' titles, which the domain's rules give (`E00003 A`, `CSA-A threshold`), never an
   entity's key. The sentence holds no time: the result carries when and who, and the client writes the time in the
   user's zone.
4. **The page's door (§5), and what a request says (§11).** `POST /api/commands/{id}/{version}` answers as the agent's
   door does (404, 403, 409, else 422), with the origin of the page. A request to `undo` or `redo` says what it means:
   the revision its caller read, as the page's does, or the id of the command, which is refused unless it is the latest;
   with neither it is refused, and the AI's names the command whatever else it gives.
5. **Forms (§1).** `FieldForm` gains a signed amount, a rate as a fraction, a spread, a number of days and an id,
   which may name the table it points into; currency, frequency and schedule are choices. That a field may be empty
   (`empty`, `none`) is the field's, not a form. An amount is 0 or more and a product still refuses 0. `Percent`, a
   product's rate in percent, stays beside `Rate` until Rates registers (step 3). One reader and one writer serve a
   product's term, a cell, a condition and the AI. *Changed 2026-11-11 (note 34):* `Percent` stays after Rates
   registered too; note 34 says why.
6. **What a registration holds in step 1 (§1).** Its id and version, words, key, fields, subsets, whole-set targets
   and row reader. The row contract and the `TypedDataRef` come with the AI's read and the workflows (steps 4 and 6);
   templates, tables under a row and fields of several values with drafts (step 5).
7. **A selector with no table (§2).** Rows named by id belong to the one table of the workspace that has such a row;
   an id two tables have is refused with both. `all`, a subset or `where` with no table mean the application's main
   table (the pricer's trades) until step 2 gives the focused one. A table of one row is named by its words, or by
   none where the field's words say which (`set currency JPY`).
8. **`where` as typed (§2).** An operator is written with or without spaces; `in` takes values separated by commas;
   a field is named by its words, which may be several; `empty` and `none` stand for no value. Text is compared
   without case, and `<`, `<=`, `>`, `>=` only on numbers and dates. On a date a period counts from the table's date
   (the pricer's value date) in calendar months and days with no business-day rule, and the preview shows the date
   it means (`maturity>5y` is `Maturity > 2031-11-11`).
9. **What is written (§2, §6).** A row that already holds the value gets no edit and is counted as such; a command
   that would change no row writes nothing, keeps the revision and is done. A whole-set edit is written when the rows
   are a subset or `all` with no id and no `where` and the field registered one; otherwise the rows are a batch.
10. **`reset` and `remove` (§3).** `reset <rows> <field>` over a subset whose field has a whole-set edit removes that
    edit and each row's own; with no field it removes every edit on the rows. `remove` takes rows the workspace made
    (a new trade) and refuses an acquired one by name, saying what switches it out instead.
11. **The preview (§5).** The request holds the line as typed, the application (dropped when it was built: note 15),
    the workspace and its revision; the
    answer holds the command and its inputs ready to invoke, the arguments as read, the rows (count and first few),
    what would change in one sentence, and what could follow the last word. The line asks while it is typed and Enter
    runs the server's reading of exactly that text, or nothing while it cannot be read.
12. **The edit verbs' contracts are flat (§2, §3).** A contract holds fields of one value each (text, a number, a
    flag), from which the forms and the AI's tools are generated, and no list. So `entity.set`, `entity.reset` and
    `entity.remove` take the rows as the line spells them (`rows`: ids, `all`, a subset, `where …`) and the values as
    `name=value` pairs, with the table where it is named; the server reads both with the selector's one reader, and the
    preview shows the typed reading. The typed selector as a value of the contract waits for contracts that hold lists,
    which `add each` needs (step 3).
13. **The pricer's registration (§1, §3; settled while it was built).** Its tables are `xva.trades` (main; `existing`,
    `new`), `xva.csas`, and of one row each `xva.netting`, `xva.credit`, `xva.cds`, `xva.workspace` and
    `xva.settings.<module>`, named by the module's id (`set xva charge ratio 1.5`). A figure a module computes for a
    row and no one types (PV01) is no field, since it changes with no edit. A table of one row that has none for now,
    the CDS while no curve is chosen, refuses in the domain's words and never writes an edit of nothing. No row of the
    pricer is of the workspace's making until a command creates a trade (step 3), so until then `remove` refuses each
    by name and says what to do instead; note 10 holds from that step on. In `name=value` a value holds at least one
    word, so
    `type=proxy sector=financials` is two values. A command of one value is named in the log as the line spells it
    (`set CSA-A threshold 5mm`), which is what `undo` then says. `xva.set` left with the page's `/set` endpoint in the
    second pull request: the page's `set` runs `entity.set` through the page's door and shows the server's sentence.
14. **What the review of the second pull request settled (§1 to §3, §6).**
    - *Several values are written in the order given.* Each is checked against the rows as the values before it left
      them, so one command takes what two would (`note=wet drift=1%` where a drift needs a note) and refuses what the
      second would; refused, it writes none of them. The domain's derivation still runs once, when the batch is
      written.
    - *After `in`, a comma before three digits is not guessed.* `1,500k` could be one number grouped or two values, so
      it is refused (`AmbiguousValues`) with both spellings: `1, 500k` and `1500k`. `8,3` is two values.
    - *Two signs are no operator* (`==`, `=>`: `UnknownOperator`), and an order against nothing is refused
      (`<empty`: `UnorderedValue`). `=empty` and `!=empty` compare no amount, so they are asked across units.
    - *A table said beside the rows is the one they are read in*: its subsets, and no row where it has one.
    - *A refusal carries its word.* The word at fault and what it could have been go on the refusal's issue (`word`,
      `candidates`), through the command gateway, the HTTP envelope and the AI's tool result (§6).
    - *A table's subsets share no word*, and a registration that breaks this fails when the table is read.
    - *The pricer.* A module's typed column is a field while one of its measures is selected, as the grids show it
      then; an edit of a column that does not show stays where it is, `reset` of every field included. A module's
      amount (PV Adj) is in the trade's currency, so a condition on it is refused across currencies as one on a
      notional is, and it is typed as its grid cell is, a plain number (`-3000`, `-3k`), never in millions.
15. **What the third pull request settled (§4, §5; settled while it was built).**
    - *A command form (§4)* holds the command's words, one spelling or several, and for each argument, in the order
      typed, its name, whether it is required and its kind: rows, assignments, a field or text. An argument's own
      form, and whether it may be typed without its name, wait for a command that needs them. A capability that says
      how it is typed reads its own line (`ILineCommand`); one that does not, and two that answer to the same words,
      stop the host at start.
    - *The preview's request* is the line, the workspace and, where the caller read one, its revision. It names no
      application (amends note 11): the workspace says whose tables it holds. With no revision the line is read as
      the workspace stands; with one, a workspace that moved refuses (409). A line that cannot be read is an answer
      (200) that holds its refusal: the code, the sentence, the word at fault with its place in the line, and what it
      could have been. A word at fault that stands twice in the line is named and given no place: the reader says the
      word and not which of the two, and the wrong one marked would send the user to a word that is right. An unknown
      workspace (404) and another user's (403) are refused by status.
    - *An argument as read* is its name and its words. Where it came from (typed, default, context) waits for step 2,
      when there is a context for it to come from.
    - *The rows* are their table, a title in its words (`trades, 12 rows`), their count and the first ten ids, of which
      the line names five. A table of one row is said by its word alone and lists no row.
    - *What would change* is one sentence a verb: `A: 9 on to off, 3 already off`; for one row `threshold: 0 to 5mm`;
      `csa: 2 reset, 1 with no edit`; `3 removed, with their edits`. A command that would change nothing says
      `Nothing would change:` and the reason the command itself gives. The sentence is made from the plan the command
      writes from, so what is read is what would be written. What only the write refuses (an edit of the field that
      is held and not yet decided, `ConflictUnresolved`) is refused at Enter, by the command: nothing ran, and the
      line stays with that reason as it does with a reading's.
    - *One value may follow its field with no sign* in `entity.set` (`threshold 5mm`, `self threshold 5mm`), as the
      line has always spelled one value: the longest leading words that name a field are the field, and the rest its
      value. Several values are `name=value` (note 12). This replaces the client's own reader of `set`.
    - *`undo` and `redo`* read as `Takes back: set CSA-A threshold 5mm (you, 1 edit)`, and the moment that command ran
      is answered beside the sentence, for the client to write in its user's zone. Whose it was is `you` for the
      reader's own, the user's id for another's, and `the AI for you` where the AI ran it (§11): what undo would take
      back is then not the user's own typing. Their inputs name the command read, so Enter takes back what was shown
      or is refused (note 4). A command of no argument takes no word after its own (`UnreadWords`). The sentence
      counts the command's edits, not its rows: an entry of the log holds no count of rows.
    - *What could follow* is a list of `{ value, start, detail }`, each written from its place in the line, and how
      many more there are than listed. After the command's words: `all`, the subsets, `where`, the tables, the fields
      of the tables of one row with their table, and rows by id, twenty at a time, by how an id starts or ends; a row
      two tables have is offered with its table. After the rows: the editable fields of their table. After a field:
      its values where they are a closed set (a flag, a choice, the ids of the table it points into, `empty` where it
      may be empty). In a condition: the fields, the operators of the field's form, its values, and `and`.
    - *The line (§5).* It builds a command per word of each form, in the group Workspace, as a command of the page in
      the active panel, and reads nothing after the words. Where the active panel has no such page the commands are
      listed as not available, with why. While it is typed the line shows the answer for the text it holds, asked for
      after the edits the page has already sent. One for an earlier text is never taken from, and once the wait is
      long enough to be seen it is dimmed and said to be read; a reading belongs to the opening of the line and the
      workspace it was asked for, and none is carried into another. A request the server did not answer with a
      reading is said as a failure, in the line's red, and never drawn as what the line means.
    - *Enter (§2, §5).* A page gives the line a **door** to its workspace, and Enter goes through it in the page's own
      turn, after the edits the page has sent. The reading the line shows for exactly its text is what is invoked, by
      the inputs that reading answered, which name the revision it was read at: a workspace someone else has moved
      since refuses it, so Enter never runs what the user did not see. Where the reading for the text has not arrived
      yet, the server reads the line at the revision the page then holds, and that reading is invoked. The line
      answers once the command is taken or refused. Taken, it closes, the page shows the workspace as read again and
      the notice is the server's sentence. Refused, by the reading or by the write, nothing ran: the line stays with
      its text and the reason, and is read again; the word at fault is selected, so that typing replaces it, and one
      that ends the line may only lack what follows, so the caret stands after it. Where the reason is that the
      workspace has moved, the page reads it again first, so the next Enter is tried against what the page then
      shows. A line closed while Enter was being answered has no line left, and its reason is a notice.
    - *The door is the kernel's* (`commandDoor`): a page gives it its workspace, the turns of its edits and how it
      reads its view again, and the next page opens one in those few lines. A grid's Ctrl+Z and Ctrl+Y go through it
      too: `workspace.undo` and `workspace.redo` by their ids, at the page's revision, with no preview, since a key
      has shown nothing to hold it to.
    - *Left as it is:* the shell's own `set region` is read before a table's field of that word, because the line
      takes the command with the most words. No table has such a field today; step 2, which gives the line its
      context, is where the two meet.
    - *Measured* at 10,009 trades (Release, SQLite, five requests each): the preview of `set where ccy=JPY after off`
      over 5,999 rows 0.04 to 0.17 s, of `set existing csa CSA-B` over all of them 0.04 to 0.12 s, of `reset existing`
      0.14 to 0.18 s, and what could follow `set` 0.01 s, with 9,994 rows counted and not listed.

### Notes of 2026-11-11: what step 2 settles (all DEFAULT)

Step 2 is three pull requests (`docs/specs/commands-2-selection-context-places`): 2a the selection and the context,
2b places with `go` and `find`, 2c the chain key and menu items from declarations. Notes 16 to 19 are 2a's, 20 to
23 are 2b's and 24 to 26 are 2c's.

16. **What the screen gives a line (§2, §9).** When the line opens, the page's door answers a **view**: the table of
    the grid the keyboard was last in, its subset, the order its rows are shown in, the selection and the focused
    cell. Where the page holds every row of the grid it names rows by key. Where the server pages the grid it gives
    the sort as the grid's reader takes it, the order token that reader last answered, the selection as ranges of
    positions, and the focused cell as a position with its field. The view is read once at each opening and sent
    with every preview of that opening. The server keeps none of it.
    - *As built.* A view is `{ table, subset?, sorts?, order?, keys?, ranges?, focus? }`, and the focused cell is
      `{ field, key? | position? }`, its field the name of the grid's column. A column that is no field of the table
      (a result, an action) is a place the keyboard can be: the view then names the row and no field.
    - *A view the workspace cannot have* is the page's fault and no reading: an unknown table, subset or sort column,
      a selection said both by keys and by positions, a sort or positions in a table no reader pages, a range of no
      row or from before the first row, rows shown named beside positions. HTTP 400, `InvalidView`. It is checked
      whole as the reading starts, whatever the line, and a sort though no position is read in it: a range of fewer
      than no rows is never "nothing selected", which a line that leaves its rows out would read as the focused row.
    - *A grid its page filters* names the rows it shows, by key (`shown`), where the page holds every row: the
      pricer's CSA grid, filtered from its column's menu. The trade grids, which the server pages, refuse a filter.
    - *Which order a position is in.* The page keeps, for each row its reader answered, the sort and the token of
      that answer, and reads them from the rows the grid holds when the line opens, never from the answer that
      arrived last. Where the selection is written in another sequence than those rows, where they are not all of
      one order, and where none is in hand, the view gives no order, and the server refuses its positions.
    - *A grid that is no table gives no view.* Before any grid has had the keyboard the line has no screen, and once
      a grid of no table has had it (the scenarios' shifts, a result, a trace) it has none again. The pricer's page
      gives its two trade grids by position and its CSA grid by key.
    - *From a key or a button* a command has no line: its words are read with the view as it is at the press.
17. **`selected` and `shown` are the preview's to resolve, and no command takes them (§2).** The selector's reader
    takes the two words where rows are said, alone or narrowed by `where`. The preview resolves them with the view:
    keys as they are, positions through the table's own reader of that order (`IWorkspaceTable.InOrder`, the one
    that pages the grid), refused where the order token is no longer the one the grid was drawn from (`ViewMoved`).
    The inputs it answers hold the keys of the rows resolved and their table, at the revision read, so a command is
    still rows by id and nothing in a contract, the log or a result knows a screen. A command invoked with either
    word (the AI, a saved sequence) is refused by name (`NoView`). `shown` is the grid's subset until `filter`
    gives a view conditions (step 4), and is counted like any rows. A command of many keys is named in the log by
    its first three and how many more there are.
    - *`shown` under a page's own filter* is the rows the view names (note 16), read by their keys, and one that is
      gone is refused (`ViewMoved`). The note first took no grid to filter yet; the review of 2a found the CSA
      grid's column menu, where `shown` would have written the rows the filter hid (corrected 2026-11-11).
    - *Keys are read as the command will read them.* The preview writes the keys as a line lists ids and reads that
      text again as Enter will. Keys it would read as anything but those rows are refused before Enter
      (`UnsayableRow`): a single key that is a subset's word (`auto`), a key of two words. Run, they would have
      been other rows. The row of a table of one row is said by its table alone, with no key.
    - *Positions the order has no row at* are of another order, whatever its token: refused, and said as an order
      that has moved.
    - *Rows of a screen that come to none are refused*, never "nothing would change": a command run by keys cannot
      say "no row". `NothingSelected`, `NothingShown` for a grid with no row, and `NoRowMatches` where a `where`
      leaves none of them (`No selected row of trades matches ccy = USD.`).
    - *The table of a keyed command* is said by its word where one table has it (`set trades E00001, E00002, E00003
      and 2 more csa CSA-B`), so the log reads as a line that could be typed.
    - *One order for the grid and the table.* The pricer keeps a grid's order per state and sort (`TradeOrder`), and
      both its reader of pages and its table's `InOrder` answer from it. Unsorted, the token is the source's version;
      sorted, the version and the revision, since any edit may move a sorted row.
    - *Left as it is:* after a command on a sorted grid the token has moved, so the grid reads again in a new
      sequence and drops its selection; the next line then says that nothing is selected. An unsorted grid keeps
      both. A token that moves only when the order does is for step 2c, where a second command follows the first.
    - *Measured* at 10,009 trades, every one selected by one range (Release, SQLite, five requests each, after the
      review's checks were added): the preview of `set selected a off` 0.09 to 0.25 s, of `set csa CSA-B` with the
      rows left out 0.05 to 0.09 s, of `set selected where ccy=USD a off` (4,011 rows) 0.04 to 0.08 s, of `reset`
      on the focused cell 0.02 to 0.06 s, and the opening's sentence 0.01 to 0.05 s. An answer that names 10,009
      rows is 177 KB, and Enter on it 0.52 s. Before the order was kept per state the same previews took 0.2 to
      0.7 s: each read the order five times.
18. **Rows left out (§9).** `set` and `reset` with no rows, whose first words name a field of the view's table, act
    on the view's selection; with nothing selected, on the focused row. `reset` with no word at all acts on the
    focused cell. A field of a table of one row is read first, as before (`set alarm 5mm`); words that name both
    are refused with the two spellings that say which. With no view, and with a view that has nothing selected and
    no focus, the line is refused as it is today and says that nothing is selected. Each argument as read says
    where it came from, `typed` or `context` (§5), and the line marks the second.
    - *Words that name two things are refused with both spellings, never guessed.* A word that is a table and a
      field of the screen's table (`AmbiguousWords`); a word that a table of one row has and that a command could
      also set on the screen's rows (`AmbiguousField`: `set ccy USD` on the CSA grid is the display currency or the
      CSA's). Where the screen's field cannot be set, the table of one row keeps its word: on the trade grids
      `set ccy USD` is still the display currency.
    - *The CSA table answers to `csas` alone.* `csa` was its second word and is the trade's field, so
      `set csa CSA-B` with trades selected sets their CSA. `set csa CSA-A threshold 5mm` no longer reads;
      `set CSA-A threshold 5mm` and `set csas CSA-A threshold 5mm` do.
    - *Several values with the rows left out* read too (`set note=wet checked=on`), and so does a table of one row's
      (`set alarm=5mm daylight=off`), which step 1 took for a row's id.
    - *`selected` said with nothing selected is refused.* Only rows left out fall back to the focused row.
    - *`reset` alone needs a cell.* With no focus, or on a column that is no field, it is refused (`NoFocus`).
      `remove` takes no rows from the screen: it says its rows.
    - *What could follow.* After `all`: `selected (3 rows)` and `shown (250 rows)`, where the view has them and the
      table said is the screen's. Where rows may be left out: the fields of the screen's table that can be set, each
      with what it would act on (`3 selected`, or the focused row's id).
    - *The mark.* An argument from the view says so in words, before the words read for it, so that a narrow line
      that cuts many rows short still says it: `rows from the screen: DRAFT-1, DRAFT-2, DRAFT-3`.
    - *Enter before the reading has arrived* runs the line with the view read as the line opened, as note 15 has it
      for rows that are typed: the rows are those the screen showed at the opening, whenever Enter falls, so the
      outcome does not depend on how fast the line was typed. The count is then said by the notice, not before.
    - *A table left out was not yet the screen's in 2a.* `all`, a subset and `where` with no table still meant the
      main table, as in step 1, though §9 gives them the focused grid's table. Built with the places (ticket 12,
      2b), where `go csa` first makes another grid the screen from the line: note 23.
19. **The line says its context (§9, ADR-0006 §5).** Every preview asked with a view answers the view in the
    server's words, one sentence (`trades (existing), 3 selected, E00003 · csa`). The line asks once as it opens,
    with no text, and shows the sentence under the input until it closes.
    - *The sentence:* the table and its subset, how many rows are selected or `nothing selected`, then the focused
      row and its field's word (`csas, 1 selected, CSA-A · threshold`). Positions whose order has moved since are
      said as that: `trades (existing), no longer in the order it was shown in`.
    - *On the line* it reads `On screen: …`, and the line shows the sentence of its latest reading where it has
      one, so a view that has gone stale while the line was open is said as soon as the server says it.
    - *A filter and a moved order are said.* A grid its page filters says how many rows that leaves (`csas, filtered
      to 1, nothing selected`). The answer also says apart that the order has moved (`viewMoved`), and the line
      draws that sentence as a warning, since its rows can no longer be said (design guidelines §7).

Notes 20 to 23 are 2b's (tickets 06 to 08 and 12).

20. **Places, and `go` (§9).** A page registers its places with the shell while it is mounted: for each a word,
    what the screen calls it, whether it is a grid, and how the keyboard is put there. The shell builds `go <place>`
    from the places of the page in the active panel, with their words to complete from, and refuses a word that
    names none with the places there are. `go` is the client's alone: it reads no workspace and writes none, it is
    not logged, and the AI is not offered it.
    - *It says where the keyboard went*, in a notice: `The keyboard is in Existing trades.`
    - *A place that cannot take the keyboard now says why, and nothing moves*: `go results` with nothing priced, a
      tab that shows only while a selected measure needs it. The line stays, with the reason.
    - *A page of one grid* has one place, `grid`. A page with none lists `go` and says it has no places to go to.
    - *The pricer's places:* `new` and `existing`, its trade grids; its input tabs by the words `open` takes
      (`netting`, `csa`, `credit`, `im`, `scenarios`, `settings`, `changes`), where `go csa` does what `open csa`
      does; and `results`, the dock. A trace widened over the grids is the place `trace`, and comes first while it
      is. Below `sm`, where the page is three parts, `go` shows the part its place is in first.
    - *`focus grid` is `go`* to the first place that is a grid, and keeps its chord and its words. On the pricer
      that is New trades, or the trace while one is widened over the grids, as before.
    - *`open <tab>` stays* as it is. It and `go <tab>` are two words for one act; which goes is step 3's, where the
      pricer's commands move to the server.
    - *As built.* The registry is the shell's (`shell/places.ts`), by panel, read when a command runs, so a place
      that comes and goes with the page's state (the trace) is offered only while it is there. A word that names no
      place is refused by the line's own check of an argument: `place: one of new, existing, …, results`. `open
      <tab>` for a tab that does not show now keeps the line with the reason too, where it was a refused notice.
      From the chord there is no line, and the reason is a notice: `focus grid: This panel has no grid yet.`
    - *From the review of 2b.* `open <tab>` is `go` to that tab's place, so it too says where the keyboard went
      (`The keyboard is in the CSA tab.`) and gives none back. A page offers its places only while it draws them:
      while the pricer is still reading its workspace, or could not, it has none. And a place a page offers always
      has something to take the keyboard. A tab with nothing to work in (Netting while no selected measure reads
      the recovery, CSA where the agreement has none) takes it on its own tab, and a dock that draws no tab and no
      control takes it itself. A place gone to with nothing drawn to take the keyboard is a defect, and throws.
21. **`find` is read by the server and done by the page (§3, §9; amends §3 and §4's "in the client only").**
    `find <row> [<field>]` puts the Focus of the grid on screen on a row or on one of its cells. Its row may be one
    the page has not loaded, and the reading of a line is the server's (§5): so `POST /api/commands/preview` reads
    it, with the view, and answers a **place** and no command: the table, the row's key, its position in the
    view's order where the server pages the grid, the column, and the sentence to say once the keyboard is there.
    Enter invokes nothing: the page puts the Focus on the place. `go` and `open` name only what a page declares
    and stay the client's.
    - *A view verb is declared once, on the server, apart from the capabilities.* `GET /api/catalog` lists the view
      verbs (`viewVerbs`: an id, a title and the command form), and the line builds their entries as it builds a
      capability's. They are no capabilities: nothing invokes them, the log never holds one, and the agent's tools,
      which are the capabilities (ADR-0007 §3), hold none.
    - *The row* is looked for among the rows the grid shows: its subset's, or those its page's filter leaves. By
      its id as written, then by the end of an id (note 7); an end that names several is refused with them. A row
      of the table that this grid does not show is refused, and the reason says where it is (`NotOnScreen`).
    - *The field* is a field of the table by its words, refused with the fields there are. Left out, the Focus
      keeps the column it was on, or takes the first where the grid had none.
    - *The place.* Where the server pages the grid, the row's position in the view's sort, refused where the
      order token is no longer the grid's (`ViewMoved`). Where the page holds every row, the key alone.
    - *With no screen* the line is refused (`NoView`): `find` looks in a grid, and says to go to one.
    - *The page places with `placeSelection`*, one cell with the Focus on it, against the Row Sequence Version the
      view was read in: ExGrid reveals the Focus and refuses a version it has left. Not `placeMatch`, which §9
      named: that takes the cell as the current match of the grid's own Find panel. A grid that refuses, and a
      field the grid shows no column of, leave the line open with the reason.
    - *The keyboard is in the grid afterwards*, and the notice is the server's sentence: `The keyboard is on
      ABC-LE-01-E00003 · csa in trades (existing).`
    - *Enter before the reading has arrived* has the page ask for it, with the view read as the line opened. A
      reading on screen that is of an older revision than the page then holds is asked for again.
    - *As built.* A page gives its door a **screen**: the view, and how the grid it was read of takes a place. The
      door keeps each view it handed the line with that grid, so a place is put in the grid the line opened on,
      whatever the page shows by then. It answers that the keyboard has moved, and the line then gives none back.
      The line's entry is `view.find`, among the workspace's commands. `find` completes the ids of the rows the
      grid shows that start or end with what is typed, twenty at a time, and then the table's fields.
    - *From the review of 2b.* A position is answered only where the view names an order (`order`), whichever
      reader the table has. A view that names its rows by key, and a paged one whose rows were in no one order,
      are answered the key alone. A page that holds every row finds the key itself. A page that holds part of its
      rows and was answered no position places nothing and says so: the grid `held its rows in no one order when
      the line opened`. Where the grid refuses the place the reason says which it was: it has moved since the
      line opened, or it keeps an edit open that it could not commit (told apart by ExGrid's `readEditText`).
      None of these sentences names `find`: the next view verb that answers a place reads them too.
22. **Where the keyboard is afterwards (§9).** A command that edits or executes gives the keyboard back to where
    it was when the line opened: `set`, `reset`, `undo`, Price. So it has been since step 1; a browser test now
    holds it. `go` and `find` leave it where they put it, and the line gives nothing back after them.
    - *From the review of 2b.* Who gives the keyboard back is kept in one module (`shell/keyboard.ts`). `go`,
      `find` and `open <tab>` say the keyboard is theirs as soon as the place is taken, before the page has drawn
      it: the line then has none to give back, however late the page's own focus arrives.
23. **A table left out is the screen's (§9; ticket 12, from the review of 2a).** With a view behind the line,
    rows said as `all` or by `where` alone, with no table, are rows of the view's table. A subset's word names
    the view's table where that table has such a subset, and the main table otherwise: on the CSA grid `existing`
    is still the existing trades. With no view (the AI, a saved sequence) the main table, as in step 1. Rows by
    id are found as before, in the one table that has them (note 7).
    - *The request names the table.* Where the screen gave a table that is not the main one, the inputs the
      preview answers hold it (`table: csas`), so the command run is rows of that table whoever reads the request,
      and the log names it as a line that could be typed anywhere: `set csas where ccy=USD threshold 0`.
    - *What could follow* offers the subsets and, after `where`, the fields of the table the rows would be read in.
    - *Rows left out, `selected` and `shown` need no table named here*: the request already holds their keys and
      their table (note 18). An id is found in the one table that has it.
    - *From the review of 2b.* The line's reader is the one place that decides the table of rows said with none,
      and what it decided travels with the reading: the preview, the completions and the request read that, and
      none works it out again. A subset's word that both the screen's table and the main one have names the
      screen's, and the list offers it once.
24. **The chain key (§9).** Ctrl+Enter in the line, and ⌘+Enter on a Mac, runs the line as Enter does and keeps
    the line open. It is a key of the line: no shell chord, and nothing outside the line takes it.
    - *Taken*, the text is cleared, the list is the one an opening shows, and the line says what its chain has
      done, in the order it happened: each line the chain key ran (`› set selected a off`), and each notice the
      application has said since the chain began (`Set A to off on DRAFT-1, DRAFT-2 and DRAFT-3.`). The line hears
      the notices while it is open, so a result that arrives later (Price) is said there too, when it arrives. No
      notice is said to be any line's: which command a late one belongs to is not known. The notice is still drawn
      in its own strip, behind the line.
    - *Each chained command is a new opening*: the view is read again from the page, and the commands are built
      again, as when the line opens. So after `go existing` the next line's context is the Existing grid.
    - *Refused*, nothing ran: the line stays with its text and the reason, as with Enter (ADR-0006's note 1).
    - *While a chained line runs, the next is typed.* The text is cleared as the chained line begins, the line says
      it is running, and what is typed meanwhile is kept whole. Nothing runs before the chained line has ended:
      Enter and the chain key are refused with the reason (`set selected a off has not ended yet. Press Enter again
      once it has.`), and the server is asked to read nothing, since a reading then would be of the workspace the
      running line is about to leave (spine 6). A chained line that is refused comes back on the line as typed, with
      why; what was typed while it ran was typed for after it, and goes with it. With plain Enter the line is
      closing and takes no key, as before.
    - *A chained command that moves the keyboard has ended when the keyboard is there.* A place may take the
      keyboard after its command returns (`go existing`, a tab the page has yet to draw). Until it has, the line
      still says the command is running and runs nothing, so no line is read with the screen of before the move.
    - *The keyboard stays the line's.* A chained `go`, `find` or `open <tab>` puts the keyboard in its place as it
      always does; the line takes it back at once and remembers that place as where the keyboard goes when the line
      closes (`shell/keyboard.ts`, `holdKeyboard`). The line listens for a focus outside itself only from the first
      chained key until it closes. A command that opens a dialog ends the chain: the line closes, as with Enter.
    - *While an IME composes, every key of the line is the IME's*: Enter, Ctrl+Enter, Tab and the arrows. Before
      this note a plain Enter that committed a composition also ran the line.
    - *A view read before the grid has drawn what the command left* may name an order the grid is about to leave.
      The next line that says rows by position is then refused by name (`ViewMoved`), never run on other rows. Note
      26 makes this rare: an edit that moves no row leaves the order's token as it was.
    - *As built.* What the line says is a log under its input (`command-said`, `role="log"`): a row per line that
      ran, dimmed, and a row per notice with its kind as a tick or a cross and its colour. It draws the last four
      rows and counts the rest (`2 earlier not shown.`). A line that is running says so in words (`› price is
      running…`). The workspace's `notify` is the one thing heard (`platform/notices.ts`, `hearing`).
    - *As built: the keyboard.* A place that takes the keyboard a render after its command (`go existing`, `find`)
      has it taken back when it arrives, and the screen is read again then, so the line's context is that grid's.
      `keyboardAwaited` (`shell/keyboard.ts`) says whether a command gave the keyboard to a place that has not
      taken it yet. The line closes on the last place a chained command put the keyboard in, or where it was opened
      from when none did.
    - *Changed in review, 2026-11-11.* As first built the line said one sentence, `<line>: <notice>`, with the
      latest notice heard while the line ran, and took no key while a chained line ran. Both depended on timing: a
      notice that arrived a moment late was said alone or with the next line, and of a line typed at machine speed
      only the end landed, which Ctrl+Enter could then run as another command. The log names no owner for a
      notice, and the next line is kept whole and run only once the one before has ended. A queue of Enters pressed
      meanwhile was not built: a line that would run unseen after another's refusal is what the refusal prevents.
    - *As built: a dialog.* Where the shell opens the dialog (`run <workflow>`, `new xva`) the chain ends in the
      same key. A dialog a page opens later ends it when it takes the keyboard: the line closes and leaves the
      keyboard to it, and keys typed into the line before that are lost with the line. No command of the line opens
      such a dialog today (the counterparty's confirmation is opened from the context bar), so the rule is held by
      the keyboard's unit test alone; when that dialog closes, the keyboard is where the dialog leaves it.
    - *As built: what the chain found.* The workspace's context value was made anew at every notice, so every page
      made its command door anew in the middle of the command that said it, and a `find` after a notice had no
      grid to put the keyboard on. The value is now one for the application's life.
    - *As built: what was not typed.* With no command matched, Ctrl+Enter asks the AI as Enter does, and the line
      says `› ai <text>`. An option clicked with the pointer runs as Enter: the chain key is a key.
25. **A table's actions, and the Context Menu (§4).** A table's description lists its **actions**: each a title and
    the line that does it (`Take out of After`, `set selected after off`). `GET /api/workspaces/{id}/tables` answers
    the workspace's tables with their actions; the kernel puts `Reset this cell` (`reset`) first on every table.
    - *A grid bound to the table shows them in its Context Menu*, after the grid's own commands and before the
      page's hand-written ones, each with its line (`Take out of After · set selected after off`): the menu teaches
      the line.
    - *An item runs its line through the page's door*, as a served command run from a key does: read by the server
      with a view, in the page's turn, logged and taken back as that line. The view is the menu's: its Selection,
      and for the focused cell the cell the menu was opened on, which inside a Selection is not always the Focus.
    - *Every action is offered on every cell.* One that changes nothing there says so when run, and one the server
      cannot read there (a column that is no field) is refused. Either is a notice with the server's sentence.
    - *An action's line begins with a command's words*, checked when the tables are read: one that does not is the
      registration's defect, and fails.
    - *Not built here:* the `command` target of a declarative view's row action (`ActionDefinition`, §4), which no
      view asks for yet. It comes with the first that does.
    - *As built.* `TableDescriptor.Actions` holds a table's own (`TableAction`: a title and a line), and
      `CommandLines.Tables` answers each table with the kernel's first, and fails on a host that registers tables
      without `entity.reset`. The pricer's trades declare `Take out of
      After`, `Put into After`, `Take out of Before` and `Put into Before`; its CSAs declare none, so their menu
      has `Reset this cell` alone before `Vary values…`. A page reads the tables once for its workspace and builds
      a grid's items with `tableMenu` (`platform/commands/served.ts`); the view an item runs with is `menuView`
      (`platform/grid/lineView.ts`): the menu's Selection as the grid's, and the focused cell named by the row's
      key and the column the menu was opened on.
    - *As built: what an item says.* What it did is the command's own notice (`Set A to off on … and ….`), and
      `undo` names its line with the rows it resolved to. One that changes nothing says so (`Nothing changed: …
      already holds B off.`, `… has no edit of csa.`). A refusal is a refused notice that begins with the line
      (`reset: reset alone resets the cell the keyboard is on, and that cell is no field of trades: …`). Tables
      that could not be read are said once, as a refused notice, and the menus then hold the grid's own commands
      and the page's. Tables that were read and lack one a grid of the page is bound to are a defect, and the page
      fails.
    - *As built: the pointer.* A secondary press outside the Selection is on that cell alone, and inside it the
      Selection stands (ExGrid's rule), so an item acts on what the menu was opened on either way.
26. **A sorted order's token moves only when the order does (amends note 17).** The pricer's token for a sorted
    grid was its source's version and the revision, so every edit moved it and the grid dropped its selection.
    It is now the source's version and a digest of the row keys in that order: an edit that moves no row, adds
    none and removes none leaves it, the grid keeps its sequence and its selection, and a second command on the
    same selection needs nothing selected again. An edit that does move a row moves the token, as before.
    - *As built.* `TradeOrder.Token` in the pricer's sample: the digest is the first eight bytes of the SHA-256 of
      the keys in order, joined by a line break, and is kept beside the sorted rows for the workspace state it was
      taken of, so a state is digested once per order. An unsorted grid's token is still the source's version
      alone. The reader of a page (`Page`) and `InOrder` answer the same token, which a test holds.

### Notes of 2026-11-11: what step 3 settles (all DEFAULT)

Step 3 is four pull requests (`docs/specs/commands-3-domain-verbs-rates`): 3a short forms and the pricer's verbs
that edit, 3b its scenarios and the verbs that execute, 3c Rates, 3d `add each` with lists. Notes 27 to 30 are 3a's
and are built; note 31 is the step's as a whole; notes 32 and 33 are 3b's and are built; the later ones are added as
each is built.

27. **A command says which workspaces it is for (§4).** A command form names the workspace application its command
    acts on (`app`); a kernel verb names none, and acts on every workspace's tables. The line lists a command on a
    page whose workspace is of its application, and a page's door says which that is. The server reads a line for a
    workspace with the commands of that workspace's application and the kernel's: another application's words are
    no command there.
    - *As built (3a).* `CommandForm.App`, which the pricer's verbs and short forms set to `xva`; `state.App` says a
      workspace's. A command of an application is listed on a page of that application only. Elsewhere, and where
      no page holds a workspace, it is not listed as unavailable either: its words are no command there, which is
      what the server would say. A kernel verb with no workspace to act on is still listed, with why.
    - *Two applications may share words* (`price` is the pricer's and will be Rates'), since no workspace is of
      both. Words shared within one application, or with a kernel verb, are refused when the server starts.
28. **A domain verb reads its own line (§3, §4).** Each of the pricer's verbs declares its form and reads the words
    after its own into the request its capability already takes, where the AI writes the same words (`cell` and
    `values`, `cds`, `measures`). One reader, on the server, serves the line, a button and the AI.
    - *The preview is a trial.* The command plans its changes for the workspace as it stands, and the kernel checks
      them with nothing written (`WorkspaceService.After`). The reading says what would be made (`Makes 2 case(s):
      CSA-A threshold 10mm, CSA-A threshold 5mm.`, in the words `vary` already said back) or refuses with the
      command's own code and sentence. Enter invokes the capability with the inputs read, at the revision read.
    - *What could follow is the domain's to say*: the measures not yet typed, the CDS catalogue's words, the titles
      of the cases.
    - *3a's verbs:* `vary`, `add each`, `cds`, `measures`, `case remove` and `cases clear`.
    - *A case is no row the selector can name.* It is named by its title, of several words, and a row's id is one
      word (note 7). So `case remove` and `cases clear` are domain verbs (`xva.case.remove`, `xva.cases.clear`) and
      not the kernel's `remove`.
    - *`measures` is a domain verb.* Its value is a list, and a field of several values comes with step 5 (note 6).
    - *`add trades` is now `add each`* (§3, the owner's name), with no argument until 3d. `add trades` is retired
      and is no second spelling. The capability keeps its id, `xva.trades.add`: no user reads an id.
    - *The page's own controls call the same capabilities* through the page's door (`invoke`), and the page's
      endpoints that wrote for them go (§5).
    - *As built (3a).* A domain verb is an `ILineCommand` beside its capability, as the kernel's verbs are. Each
      plans its changes first (`PlanVary`, `PlanAdd`, `PlanCds`, `PlanMeasures`, `PlanRemoveCase`,
      `PlanClearCases`): the reading tries the plan with nothing written, and the capability writes the same plan,
      so the two cannot differ. The log names the command by the words that would type it (`vary CSA-A threshold
      10mm, 5mm`, `add each`, `cds none`, `measures cva fva`, `case remove <title>`, `cases clear`), which is what
      `undo` then says.
    - *A request names its cell once.* `xva.vary` takes `cell`, as the line and the AI name it, or `entity` and
      `field`, as *Vary values…* names the cell its menu opened on. Both, or neither, is refused. The dialog sends
      its values as typed, for the server to read as it reads the line's: the client holds no copy of the comma
      rule.
    - *What a domain verb does not do yet.* `vary` offers nothing to complete: its cell is named by a row and a
      column's header (`PortfolioCells`), not by the selector, so the rows and fields `set` offers are not offered
      here. A domain verb's refusal is a code and a sentence: it marks no word of the line and lists no
      candidates, as the kernel's verbs and the short forms do (§5). Both are step 4's, with `select` and the
      other verbs that name rows.
    - *A caller that says the answer itself is handed it.* The door's `invoke` takes a listener for what the
      command said: *Vary values…* shows the sentence in its dialog, and the page raises no notice of it.
    - *Gone with this:* the page's `/vary`, `/cds` (the write), `/add-trades`, `/cases/remove`, `/cases/clear` and
      `/measures` endpoints, and the client's readers of `measures`, `vary` and `cds`. The Changes tab's removals
      invoke `xva.case.remove` and `xva.cases.clear`, and a measure's button invokes `xva.measures` with the
      selection it would leave. The button shows what it holds, so the page says nothing more when it is taken;
      a refusal is a notice (`The measures were not changed: Select at least one measure to price.`), and `undo`
      names a press as it names the line (`measures cva fva`). The request's `measures` has no least length, so
      a selection of none is the command's to refuse in its own words, not the contract's.
    - *What stays an edit of the page's own queue:* the currency select and the Credit tab's selects (the CDS type,
      its reference, a proxy's sector, region and rating). Each is a control bound to one field, as a cell is, and
      shows what it holds itself; a typed cell writes the same way (note 15). A proxy is chosen a key at a time,
      and `cds` says a whole choice in one line, so a select cannot stand for it. `currency` and `cds` typed go
      through the commands.
29. **A short form expands to a kernel line (§3).** A domain registers short forms for its application: words, a
    title, how it is typed, and the line it expands to, given what was typed and the view behind the line. The
    server reads a short form by expanding it and reading the expansion. The preview answers the kernel command and
    its inputs, and says the line it expands to (`expands`), which the line shows before Enter. What runs, what
    the log names and what `undo` takes back is the kernel command, by the expanded line. A short form has no
    behaviour of its own. The catalog lists short forms apart (`shortForms`), as it lists the screen's verbs: the
    agent's tools hold none, since the AI has the kernel command.
    - *The pricer's:* `before all` and `before none`, `after all` and `after none`, each with `new` or `existing`
      after it or neither: `set existing before on`. With no grid named it is the grid of the view behind the line;
      with no view either it is refused, naming the two grids. `currency USD` is `set workspace currency USD`.
      *Corrected as built:* this note first said `set currency USD`, which names no row; the settings are the row
      `workspace` of their table, and naming it reads the same whatever grid is behind the line.
    - *The grids' caption buttons* (`B all`, `A none`) run the expanded line through the door.
    - *As built (3a).* `IShortForm` (`Descriptor`, `Expand`, `Complete`), listed by an `IShortFormCatalog` per
      module. A short form expands to a command or to a verb of the screen, never to another short form; one that
      expands to no command's words is a defect, and throws. Where the expansion is refused, the reason is placed
      on the word in the line as typed. The line shows `Runs as <line>` over the reading, and no argument badges of
      the short form's own once it is read.
    - *No grid is assumed.* The page used to take the grid the keyboard was last in, and Existing before any was
      entered. A short form with no grid typed and no trade grid behind the line is now refused, naming both.
    - *What the notice says is the kernel's sentence* (`Set B to off on E00003 and E00004.`), which replaces the
      page's own (`Cleared Before on 2 existing trades.`): one command, one sentence, from the line or the button.
30. **`open` is for a page's tabs, `go` for the parts that hold the keyboard (settles note 20).** A page's place
    says whether it is a tab. The shell builds `open <tab>` from the tabs and `go <place>` from the rest, as it
    built `go` from all of them: the pricer's seven hand-written `open <tab>` commands go, and `go csa` with them.
    Both still say where the keyboard went.
    - *As built (3a).* `Place.tab`. The shell makes one command per tab (`open csa`), from the tabs the page has
      when the line opens, and not one `open` that takes the tab as an argument: `open result` and `open pricing
      db` keep their words, and the list names each tab. A page with no tab has no `open` of the shell's.
31. **What stays in the client in step 3.** `download run` and `copy quote` act in the browser (a file saved, the
    clipboard written), and a result that carries a file or a text to copy is not built. `explain`, the trace's
    drawing options, `open result` and `dock` are verbs of the screen (§3). `new xva` and `new rates` open their
    application's dialog.
32. **The pricer's scenarios are verbs of the pricer, and no table yet (§1, §3; 3b, changing its plan).** The
    step's spec first made the scenarios a registered table, `xva.scenarios`, with `set s1 included off` and `remove
    s1` the kernel's. Building it found three things the kernel's tables do not hold yet, so the table waits for
    step 5 and the scenario commands are domain verbs, as a case's are (note 28).
    - *Why no table.* A scenario's name may be several words: a generator names each scenario from its shifts
      (`jpy_ois -50bp · usdjpy -10%`), and the selector names a row by an id of one word (note 7). Whether Price
      prices a scenario is not a field of a row: it is one list the workspace holds, of the names left out. And a
      scenario is not one entity `remove` could take: it is its shifts, each a row of its own. A table whose rows
      are computed from several entities, with a table under each row, is step 5's (note 6).
    - *The ten verbs:* `scenario`, `scenario include`, `scenario exclude`, `scenario remove`, `scenario duplicate`,
      `scenarios clear`, `scenario ladder`, `scenario cross`, `scenario load` and `scenario save`. Each declares its
      form for the pricer's application and reads its own line. The nine that write the workspace plan first
      (`PlanScenario`, `PlanInclude`, `PlanRemoveScenario`, `PlanDuplicate`, `PlanClearScenarios`, `PlanGenerate`,
      `PlanLoad`); the reading tries the plan with nothing written, and the capability writes the same plan
      (`WriteScenarios`), so the two cannot differ.
    - *How a name is typed.* A scenario's name is the rest of the line, since it may hold spaces. `scenario
      duplicate` takes the copy's name as its last word, and the scenario to copy is every word before it. A name
      the workspace does not hold is refused with the names it does.
    - *What a reading says.* `scenario s1 jpy_ois +10bp; usdjpy -5%`: `Makes the scenario s1 with 2 shifts: …`, or
      `Replaces the shifts of s1 with …` where one of that name stands. A generator: how many it makes, how many of
      the same name it replaces, the first three names and how many more, and the pairs of a case and a market
      Price would then price against its cap; a set that leaves more pairs than Price prices is still made, as
      before, and the reading says Price would refuse it. `scenario include` and `exclude`: what Price would then
      do, or `Nothing would change: Price already …` where it stands so already. `scenario remove` and `duplicate`
      count the shifts that go or are copied. `scenario load`: the scenarios the set would add and those it would
      replace. A reading names three scenarios at most, then how many more: a set may hold dozens.
    - *What could follow.* `scenario include` offers the scenarios Price leaves out, `exclude` those it prices,
      `remove` and `duplicate` all of them, each with its count of shifts. `scenario load` offers the sets the user
      can see, by the name `load` takes for each, with how many scenarios, for whom, and who saved it. `scenario
      save` offers `desk` and `all` after the set's name.
    - *The log names each by its words* (`scenario s1 jpy_ois +10bp; usdjpy -5%`, `scenario exclude s1`, `scenarios
      clear`), which is what `undo` says.
    - *`scenarios clear` with no scenario is refused* (`NothingToChange`). The page's endpoint did nothing then and
      said that it had removed every scenario.
    - *A generator's request names its text `factors`*, as its form's argument does; it was `text`. The AI's tool
      takes the same field.
    - *`scenario save` writes no workspace.* Its effect is `saveItem`. Its reading checks the save as the save
      checks it, says whom the set would be for and whether it replaces the user's own, and keeps nothing. It is in
      no workspace's log, and `undo` does not take it back.
    - *The Scenarios tab's gestures are the commands.* The include box, Remove, Duplicate… and Remove all invoke
      the capabilities through the page's door, and the notice is the server's sentence. The grid's own edits
      (cells typed, a block pasted, cells cleared) stay the page's `/shifts` endpoint: they are cells, as a trade
      grid's are (note 28).
    - *Gone with this:* the page's `/scenarios`, `/scenarios/generate`, `/scenarios/include`, `/scenarios/remove`,
      `/scenarios/duplicate`, `/scenarios/clear`, `/scenarios/save` and `/scenarios/load` endpoints and `GET
      /scenario-sets`; the client's ten hand-written commands, its readers of a `scenario` line and of the
      generators, and its copies of the sentences. What the line showed of a generator as it was typed (`makes 8
      scenario(s)`, `pairs 9 of 49`) is now the server's sentence, read once the line holds a generator whole.
33. **`price`, `refresh` and `pv01 retry` go through the gateway (§3, §5; 3b).** Each is a capability that says how
    it is typed, read by the server, and invoked by the line, the page's own control and the AI alike.
    - *An effect of their own.* A command is a capability that edits a workspace, subscribes, reads a Run or saves
      an item (§4). `price` submits a Run and `pv01 retry` asks a provider again; neither edits the workspace. So
      there is a fifth effect, `execute`: what a capability with it starts is not taken back by `undo`.
    - *The command is `xva.workspace.price`.* `xva.price` is already the step that prices XVA inside the Run, and a
      capability's id names one thing.
    - *`price` is not tried, and its reading builds what Price would freeze.* The reading freezes the workspace as
      it stands and builds the Run's input, keeping and submitting nothing. So it says what would be priced (`Prices
      CVA and FVA for 3 cases on 2 markets (the base market and 1 scenario), as one Run.`), names Before only or
      After only where one side is empty, and refuses everything Price refuses before a Run exists: held edits, too
      many pairs, an `add each` set that no longer matches. (A workspace never holds no measure: `measures`
      refuses to leave none.) A figure a case's input already refuses is counted, and the first is said. Building
      the input reads the trades' cells, so a result not asked for yet, such as a new trade's PV01, is asked for
      by the reading as it is by the grid's rows. *Not measured:* what this costs on a workspace of 10,000 trades,
      where it runs for each reading of the line; it is to be measured before the line is used at that size.
    - *One submission, by the invocation's key.* A caller invokes a command with an idempotency key (note 4). The
      gateway now hands that key to the handler (`CapabilityInvocation.IdempotencyKey`), and `price` submits its
      Run under it. So the same invocation sent twice is one Run, also after the gateway's own memory of the key is
      gone, and the request holds no key field for a caller to forget. That holds where the request names its
      revision, as a line's reading and the page's door always do. A request that names none takes the revision
      the workspace then holds, so sent again after the workspace has moved it is refused (`IdempotencyConflict`)
      and still makes no second Run. Invoked with no key, `price` is refused.
    - *A refusal is a sentence.* A line and a notice show a sentence alone, so a refusal that names the edits it
      waits on, or the issues the Run service found, says them in its sentence.
    - *`refresh`* reads the counterparty's trades and CSAs again on the value date the workspace holds. Its reading
      reads no source: it says what would be read again. It is written as one command named `refresh`, with its
      caller's origin, and `undo` takes it back where the source's version is still the one it replaced. Its
      answer counts the edits carried, in conflict and lost, and says `stays` where any is held: the page's door
      then raises a notice that does not leave by itself, as the page's own Refresh did.
    - *`pv01 retry`* takes one trade, or none for every trade. Its reading names the trades with a failed result
      and asks again for nothing; with none it says that nothing would be asked. It reads the cells as the grid's
      rows do, so a result never asked for is asked for by that read. Though it writes no workspace, it names the
      revision it was read at and is refused where the workspace has moved since, as every command is (§2). A
      failed cell's *Ask again* invokes it with its trade.
    - *The page's controls.* The Price and Refresh buttons invoke the two capabilities through the door; the dock
      shows the workspace's latest Run, whoever priced it, so a Run the AI priced is shown too. The page reads its
      workspace again after the AI invokes any of these commands or a scenario verb, as after any of its edits.
    - *Where the source moved, the grids start over.* The page keeps what it last showed its trades were read of
      (the counterparty, the value date, the source's version). A reading of the workspace in which that differs
      reads the grids from their first row again, since the trades and their order may have moved; otherwise the
      rows in view are read again where they are. So a `refresh` that read the same version of the source keeps
      the rows in view and what is selected, where the page's own Refresh started the grids over every time. The
      synthetic source is the same for a counterparty and a value date however often it is read, so in the sample
      only a change of the value date or the counterparty moves it, and no browser test reaches this through
      `refresh`; `undo` refuses to take back a `refresh` that read another version (note 2).
    - *A command that needs no argument is read by its words alone.* The line asked the server to read a command
      chosen in its list only where the command takes no argument (note 15). It now asks wherever every argument
      may be left out, so `pv01 retry` says what it would ask again before Enter, as `price` and `refresh` do.
      Enter on such a command in the list runs it, as it runs one that takes no argument, where it wrote the words
      on the line for a second Enter before. That reaches the kernel's `reset` too: typed alone and entered, it
      resets what the screen names, or is refused where the screen names nothing.
    - *The list's order.* The line lists the commands of the page's application first, in the order its module
      registers them, with `price` first; then the commands still written in the client; then the kernel's. A
      served command says its application (`Served.app`) for this.
    - *The client's `price` is the Rates page's until 3c*, and says where to price while neither page is open. On
      the pricer's page the word is the server's alone (note 27: two applications may share words).
    - *Gone with this:* the page's `/price`, `/refresh` and `/retry` endpoints, and the client's hand-written
      `refresh` and `pv01 retry`.

34. **Rates registers its lines and its defaults (§1, §3; 3c).** Two tables (`RatesTables`), and the kernel's
    `set`, `reset` and `remove` then act on a Rates workspace.
    - *`rates.lines`* (main; `lines`, `line`; a row is named by its line, `L3`). A line's flat fields are its
      fields, each named as the grid names its column (`leg1.notional`), so the cell the keyboard is on is a field
      and `Reset this cell` finds it. A leg's field is typed by one word that ends in the leg's number, as a
      product's terms are: `currency1` (`ccy1`), `index1`, `notional1`, `kind1`, `quote1`, `frequency1` (`freq1`),
      `reset1`, `float1`, and for its conventions `daycount1`, `bdc1`, `average1`, `shift1`, `lookback1`, `delay1`,
      `arrears1`, `gearing1`, `fixing1`. The trade's own are `start`, `end`, `direction`, `collateral`, `break` and
      `risk`. `product` is read by a condition and never set.
    - *An IRS's terms are fields too*, by the names `new irs` gives them, so that `set L3 notional 5mm` and `set
      L3 rate 1.25%` say what a person means: `currency`, `index` and `notional` write both legs; `rate` writes leg
      1's quote as a fixed rate; `spread` leg 2's as a floating spread; `spread1` and `spread2` write a floating
      leg's, as `new ccs` names them. A term that does not apply to a line is refused by the line's name, with the
      fields that do (`notional` on a cross-currency swap: `notional1`, `notional2`); a row holds no value in a
      term that does not apply to it, or whose two legs differ. `rate` and the spreads take a value: a leg goes
      back to par by emptying its `kind` and its `quote`, as its two cells are emptied, or by `reset`.
    - *Added in review (2026-11-11): the two frequencies.* `new irs` names a fixed and a floating frequency
      (`fixedFrequency` or `fixed`, `floatFrequency` or `float`), and this list had left them out, so `set L3 fixed
      6m` named no field though `new irs … fixed=6m` did. They are fields by the same names: the fixed is leg 1's
      payment frequency and the floating leg 2's, as an IRS is made, each while its leg is that; on a
      cross-currency swap, or an IRS whose leg was changed, they are refused by name with `frequency1` or
      `frequency2`. `float1` stays the leg's flag: a frequency typed for it is refused by its form.
    - *Added in review: a leg's flag left empty.* The grid lets the `Float` cell be emptied. A term that depends
      on the flag (`rate`, a spread, a frequency) failed on an empty one, so `set L3 rate 1%` was a server error.
      It is refused by name now (`L3 does not say whether leg 1 floats: set float1 on or off first.`), and a row's
      values are read without the flag. The command does not read an empty flag as fixed. The resolver does, as it
      did before this step, and the line's summary then says `Fixed`: that default is older than CLAUDE.md's rule
      on fallbacks and is left for the owner to decide, not widened here.
    - *Added in review: `quote1` and `quote2` stay numbers.* They are the leg's quote in the unit its `kind` names:
      percent for a fixed rate or a spread, points for `fxpts`, basis points for `atm`. So they are typed bare, as
      their cell is, and it is `rate` and the spreads that are typed as a rate is (`1.25%`, `5bp`).
    - *A write is checked for its form only*, as a typed cell is. Whether the terms make a line is the resolver's,
      and shows on the row; a command that refused it would refuse the second of two edits a line needs both of.
      One check is the command's alone: a currency or an index is one the confirmed conventions name, where a cell
      takes any code and the row then says no convention names it. A word the conventions do not have is a typing
      slip, and the line says so with the ones there are.
    - *`reset <line> [field]`* removes the line's own edits, so the field is again what the line was made with; a
      term's are the edits of the legs' fields that hold it, and a term the line does not have is refused as `set`
      refuses it. *`remove <line>`* removes a line, with its edits. The grid's *Remove line* is the table's action
      (`remove selected`; note 25).
    - *`Percent` stays beside `Rate`* (changes note 5, which had it leave when Rates registered, and the spec).
      The two are typed and written alike (`1.25%`, `5bp`) and differ only in what is held: `Rate` a fraction,
      `Percent` percent. Rates' line contract, its quote kinds (`FixedRatePercent`) and its provider hold percent.
      Holding a fraction there changes a wire contract and a provider for no gain a person sees; converting at
      the module's edge makes two numbers for one term, a factor of a hundred apart, where a test of the kernel
      sees neither. So a rate is typed in one form in both applications, and a field says which of the two it holds.
    - *`rates.workspace`* is a table of one row (`workspace`), the workspace's own settings, as the pricer has
      `xva.workspace`. It holds the workspace's defaults for new lines (ADR-0008 §4b). Its fields answer to `default
      currency` (`default ccy`), `default notional` and `default direction`, so `set default currency USD` writes
      one value and leaves the others ("Set workspace default currency to USD."), `reset default currency` removes
      one, and several are written as `name=value`. The words differ from a line's on purpose: a table of one row
      is addressed by its field's words alone (note 7), and a `currency` of the workspace would make `set currency
      USD` with lines selected a question of which was meant (note 18). `set workspace currency USD` is refused
      with the fields there are. A new line takes a default so set: the product's rules read the same context.
    - *A change of context is a change of a batch.* The defaults live in the workspace's context, so a table's
      write needs to say one: `WorkspaceChange.Context` holds the whole context after it and is written as a
      change of context that keeps the source's version, which `undo` takes back (note 2). The checks are the ones
      the page's control made: a currency with confirmed conventions, a notional of more than 0, pay or receive.
    - *Not a field yet:* the valuation date, which is context too and stays the page's control.

35. **A workspace's observers (§3; 3c).** A live subscription prices the revision it was last told of, and was
    told by Rates' own service after each of its writes. The kernel's verbs now write a Rates workspace too
    (`set`, `reset`, `remove`, `undo`, `redo`), and nothing told the subscription. So the workspace service takes
    **observers**, each for one application: after an accepted command is committed, and outside the service's
    lock, each observer of the workspace's application is given the state the command left. Two commands may tell
    an observer in the other order than they were accepted, so an observer takes a revision no newer than the one
    it holds as already told, which the live service does. Rates' observer tells the workspace's running
    subscriptions; its service tells them nothing itself any more.

36. **Rates' verbs are read on the server (§3, §4, §5; 3c).** As the pricer's are (notes 28 and 33).
    - *`price`* is `rates.workspace.price` (`rates.price` is the step inside the Run). Its reading names the
      market snapshot that is the latest as it is read, and says how many lines would be priced and which are left
      out; the inputs carry that snapshot and the revision, so Enter prices what was shown. Invoked with no
      snapshot, as the AI may, it prices on the latest then. It submits its Run under the invocation's key and
      names the held edits a refusal waits on (note 33). Before any market is published it is refused.
    - *`capture`* is `rates.frame.capture` (`rates.capture` is the step). A request names the frame it means (its
      subscription, the subscription's revision, the input revision and the market snapshot), all of them or none.
      A reading names the frame the subscription shows as it is read, and its time; Enter captures that frame and
      never a later one, and a frame the subscription no longer keeps is refused. The page's button names the
      frame the page drew. With none named, as the AI may, it is the frame shown when the command runs.
    - *`live start` and `live stop`* are `rates.live.start` and `rates.live.stop`, which now say how they are
      typed. Their answer names the subscription as before, and says the revision and a sentence.
    - *`add line <product>`* is `rates.line.add`: an empty line of the product to fill in the grid, named by the
      revision that makes it.
    - *`new <product> <terms>` is the kernel's.* `entity.create` says how it is typed (§3): the first word names a
      product of the workspace's application, the rest are its terms. Its reading fills the defaults and says the
      entity the terms would make, each term, and where each default came from; it refuses a missing or unreadable
      term by name, and what the product's rules refuse, and writes nothing. For that a product's rules say what
      their `Create` would make without making it (`WouldCreate`). The request takes the revision read, and the
      answer says a sentence.
    - *What the door reads.* The door read the output named `answer`. It now reads that, or the one output of a
      command that has one (`entity`, `subscription`); such an output says the revision and a sentence.
    - *A client command of the same first word.* `new xva` and `new rates` open a dialog and stay in the client
      (note 31), beside the served `new`; the shell's `set <context field>` stands beside the served `set` the same
      way. A line whose words are exactly a client command's words is that command's, typed to its end or not, so
      Enter on `new rates` opens the dialog and is not read as a product named `rates`.
    - *The page.* The Rates page has a door, the view of its line grid (the lines, the selection and the cell the
      keyboard is on) and two places, `lines` and `flows`. Add IRS, Add CCS, Price, the live toggle and Capture
      invoke their commands through the door; a typed cell stays the page's own edit.
    - *Gone with this:* the client's hand-written `set defaults`, `add line`, `live start`, `live stop`, `capture`,
      `price` and `new <product>`, the client's own reading of a product's terms, and the page's endpoints whose
      writes moved (`lines`, `lines/new`, `lines/{line}/remove`, `defaults`, `price`, `live`, `live/{id}`,
      `live/{id}/capture`).
    - *The three defects of 2026-11-10 end here.* A default is set alone; a refusal is the server's sentence and
      the line keeps what was typed (ADR-0006's note 1); and `set`, `reset` and `remove` edit and remove a line.
    - *As built: the sentences.* `price` reads `Prices 1 of 2 lines on market …, as one Run. Left out: L4.` and
      answers `Price was accepted as Run ….`, with the market the Run's own input names. `capture` reads `Captures
      the frame of market … at revision 4, as a Run.` and answers the same in the past, with that market. `live
      start` answers `Live is running.` whether it started the subscription or found one running: which of the two
      happened cannot be said without a race between two pages, and the user's question is only whether it runs.
      `new` reads `Makes L5: Interest rate swap. Not typed: index … from …, ….` and answers `Made L5: Interest
      rate swap.`; with a term missing, `irs still needs notional: type it, or set a default for it.`
    - *As built: what `new` offers.* After `new`, the products; after a product, each field not yet typed: its
      choices as its form writes them, or its `Examples` (new on `ProductField`: the tenors of an end, which the
      line offers and never fills in), and `name=` for a named field. A sequence (a CCS's two currencies) offers
      its next field alone. The kernel names no tenor and no currency.
    - *As built: capture's request and answer.* The request takes the `revision` the door sends with every
      request and does not check it: a capture saves the frame it names whatever the workspace holds since, and
      its contract says so. The answer's `revision` is the workspace's own, which a capture leaves as it is, and it
      names the market of the frame, by which the page titles the Run's panel. A subscription that is not the
      workspace's is refused.
    - *As built: the door.* `toldOf` reads the output named `answer`, or the one output. An answer with several
      outputs and none named `answer`, or one that says neither the revision nor a sentence, is said as the
      command's defect (`… answered made and log, and none is its answer.`); no other output is read in its place.
      A page may say what it does with what its own command answered (`DoorPage.answered`): the Rates page opens
      the Run that `price` or `capture` made, titled by the market the answer names. It is told once the command
      is done, whether or not the page could read the workspace again.
    - *As built: what the line draws of a reading.* The server reads a product's terms under their fields' names
      (`end 10y`, `notional 3bn`), which the form (`product`, `terms`) does not list. The line draws the form's
      arguments, each with what was read for it, then those read under other names; an argument of the form that
      may be left out and was not read by its own name is then not drawn as left out (`argumentsDrawn`).
    - *As built: the page.* The page follows the workspace's live subscription from what the workspace says after
      each command, whoever ran it (this page, another, the AI): it attaches to the one running, and leaves one
      that stopped with its last frame shown. A stream is held by its subscription's id, so the end of one the
      page already left says nothing of the one it hears now. The line grid's Context Menu holds its table's
      actions (`Reset this cell · reset`, `Remove line · remove selected`); the hand-written *Remove line* is
      gone. The grids' undo and redo keys run `undo` and `redo`. The page's places are `lines` and, while a line's
      cashflows are open, `flows`; `focus grid` goes to the lines. The cashflows are a grid of no table: with the
      keyboard there the line is told of no screen.
    - *As built, corrected in review: the Price button names the market the page shows*, as Capture names the
      frame it drew, and says `No market snapshot has been published yet` where the page shows none. The first
      build ran the typed `price`, which reads the latest: a later snapshot than the one in the page's badge could
      be priced, which docs/07 §3 rules out and the button did not do before this step. Typed, `price` still reads
      the latest and says which before Enter.
    - *As built: the client's `price`* is listed only where neither a pricer's nor a Rates page is open, where it
      says `Open an XVA or Rates workspace to price it.` and never runs. `new rates` is the one Rates command
      still written in the client. On another page Rates' commands are no commands (note 27), so the empty line
      no longer lists `capture` or `live start` there as unavailable with `Open a Rates workspace first.`, as it
      does not list the pricer's.
    - *As built: the AI.* It calls the same capabilities; `rates.workspace.price`, `rates.frame.capture` and
      `rates.line.add` are new to it, and a page showing the workspace reads it again after each.
    - *As built: also gone.* `GET /api/products` and `POST /api/products/{id}/preview`, with
      `ProductService.Preview` and the client's `platform/products.ts`: the reading of a product's terms is the
      line's (`ProductService.Would`, `ProductReader.Offers`). `RatesWorkspaces.ChangeDefaults`, `RemoveLine` and
      `StopLive` by a subscription's id went with their endpoints. The host's Rates endpoints answer 409 for a
      moved revision alone.
    - *As built: moved to the kernel.* `WorkspaceRefused.Named()` (a refusal that names its targets in its
      sentence), `TableWords.Counted` and `TableWords.Some` were the pricer's and are now shared with Rates. So is
      what every module's command read of its request in a copy of its own (`CommandRequests`): a field the request
      must name, an optional one, the revision as a contract says it, the key of a command that submits a Run, the
      Run a submission was accepted as, and a refusal named by the edits it waits on.
    - *Found, and left.* A line is named by the revision that makes it, so the first line of a workspace just made
      is `L3`, and two made one after the other are `L3` and `L5`: a line from terms writes two entries. A context
      change (`set default currency`) writes two as well. `new irs 5y` on a workspace with no defaults says six
      terms are missing, the index and the frequencies among them, though typing a currency alone would give those
      from its profile: the reading does not know which missing terms a profile would supply once another is
      typed.
37. **`add each` takes its iterations as a list (§3; 3d).** §3 decides the value and its rules. What is left to
    settle is how a contract says a list, what is made of the value, how a case names its trades, and how a page
    with two grids gives the lists without the line.
    - *A contract may hold a list (amends note 12).* The kernel's check of a contract understands a field of
      `type: array` with `items`, and `minItems`: its items are values (text, a number, a flag), or records
      (`type: object`, `properties`, `required`, no additional properties) whose fields are values or lists of
      values. No deeper: a typed value of a command is small. A list's problem is said at its path
      (`/inputs/request/iterations/0/add`). A form draws one control for one value, so a workflow whose input
      contract holds a list is refused when it is registered, and the client's form says a list it is handed
      cannot be drawn. The edit verbs keep their rows as the line spells them (note 12): the typed selector as a
      value is not this step's.
    - *The value.* `xva.trades.add`'s request may hold `iterations`: a list of records, each `add` (the candidates'
      ids, one at least) and `without` (the ids of the existing trades taken out of After, which may be left out).
      Left out, or empty, the command is `add each` as typed. An id is read whatever the case of its letters, as a
      key is.
    - *What is made.* The iterations given, in their order, then one alone for each New row with A ticked that
      none of them names, in the grid's order. The set made before is replaced whole, as §3 says: the value is the
      whole set made by hand, and nothing of the old one is kept unless it is given again.
    - *Refused by name, with nothing made:* an id that is no trade of the workspace; an existing trade named to be
      added, or a New row named to be taken out; a candidate with A clear (`CandidateNotTicked`), or with B ticked
      (`CandidateInBefore`, as before); a trade taken out that the Base has not in After (`NotInAfter`); an
      iteration that adds none; a trade twice in one list, and the same iteration twice (`GivenTwice`). Nothing is
      ticked, cleared or left out for the caller.
    - *A case names its trades.* A case of kind `add` holds its `iteration` (`add` and `without`) where it held
      one `trade`, in the workspace's view and in the input Price freezes. Its title is `add DRAFT-1` for one trade
      alone, as before, and otherwise `add DRAFT-1 + DRAFT-2 without E00003 + E00004`. The `+` is the title's: how
      the line spells a list stays open (§3). A case stored before this step names one `trade`, and is read as the
      iteration of that trade alone: the value it stored, in the shape of today.
    - *Price's check of the set* reads the candidates of every iteration together: a ticked New row in no case, a
      candidate no longer ticked and a candidate in Before are refused as before, and so is a trade a case takes
      out that the Base no longer has in After. Each case's After must hold, of the New rows, its own candidates
      and no other.
    - *The command's name in the log.* `add each` where nothing is given. With a value there is no line that types
      it, so the name says the iterations given, the first three and how many more
      (`add each: DRAFT-1 + DRAFT-2 without E00003; DRAFT-3`); `undo` names it so.
    - *A view's selection, read for a page (§2, note 17).* A page that makes a typed value from a grid's selection
      needs the rows' keys, and a grid the server pages holds positions. `POST /api/workspaces/{id}/selection`
      answers the keys a view's selection names, read as the preview reads `selected` (the order's token checked,
      `ViewMoved` where it has moved), and no key where nothing is selected. It writes nothing and runs nothing.
    - *The page (§3's proposal, with one change).* `Add each` in the New grid's caption invokes the command with
      no value. `One case…` beside it, and *One case from the selection…* on both trade grids' Context Menus,
      open a dialog that lists what the selections name: the New grid's as the trades the case adds, the Existing
      grid's as the trades it takes out, each of which may be left out there. *Make case* then invokes the command
      with the iterations already made and this one (*corrected in review, below: with this one and `keep`; and
      the button is named `Make case…`*). **The dialog is the change:** §3 had the press make the case.
      A grid that has been clicked always has a row selected, so a press would nearly always take an existing
      trade out that nobody chose; the dialog says the two lists before anything is made, as the line says a
      reading before Enter.
    - *The Changes tab* shows an iteration's two lists under its case, and removes one trade from either by
      invoking the command with the made iterations, that one changed. The last candidate of a case is not
      removed there: the case is.
    - *`load trades <ids>` waits for step 5.* The pricer makes no trade: its New rows are the source's own. A trade
      brought in by id needs a trade the workspace holds with its terms, which is step 5's (a trade made in the
      pricer from typed terms). Nothing is built for it here, and the name stays reserved (§3).
    - *Added as built: a request that says again what the page shows is made in the command's turn (spine 6).*
      The page reads the iterations already made from the cases it shows. Made at the press, that list would miss
      what an edit still on its way does to the cases (another `add each`, a case removed), and the set that
      replaced the old one would depend on which answer came first. The door's `invoke` therefore takes a request
      as a function of the revision too: it is made in the command's turn, from the cases the page shows at the
      revision the command is sent at. A page that shows another revision by then sends nothing and says so, and
      a workspace someone else moved refuses the command as any other (`RevisionMoved`).
    - *Added as built: the dialog.* A trade it lists is a button that is pressed while the trade is in the case,
      so one left out is put back by the same press. *Make case* is disabled, and says why, while no new trade is
      kept. Refused, the dialog stays with the server's reason and no case was made; taken, it closes and the
      notice is the server's sentence, which names the whole set (`Made 4 case(s): …; replaced …`), since the set
      is what the command wrote (*corrected in review, below: it names the case made*). From a Context Menu, the
      grid the menu opened on is read as the menu has it (its Selection, or the row under it), and the other grid
      by its own Selection.
    - *Added as built: the Changes tab.* The one trade a case adds is said without a button. A trade left out of
      what a case adds is still ticked, so the rule above makes it one more case alone; where what is left of the
      case is a case already made, the server refuses it (`GivenTwice`) and the case is removed whole instead.
    - *Found as built:* the wire leaves an empty field out, and the client typed a case's `note` as `null` where
      it has none. A case's `iteration` and `note` are typed as left out now, which is what the page reads.
    - *Corrected in review: one more case is made beside the cases made (`keep`).* As built, *Make case* said the
      whole set again with one more. A case of the set that had gone stale (its trade no longer ticked, say) then
      refused the new case by a trade the dialog does not list, and the log named the set, never the case just
      made. §3 has the button make "the next" case, so the request may hold `keep: true`. The cases of `add each`
      made before then stay the cases they are and are not read again: a stale one refuses nothing here and stays
      Price's to refuse (ADR-0010 §3's note). The iterations given are made beside them; one that a case kept
      already is, is refused by that case's title (`AlreadyMade`). A ticked New row that no case names, kept or
      given, is one more case alone, and a candidate that is in the set now is turned off in each case kept that
      does not add it, so every case still adds its own trades alone. A candidate in Before is refused where the
      command would make its case. `keep` needs an iteration (`NothingGiven`). The answer names what was made
      (`Made 1 case(s): …`) and replaces none. Without `keep` the list is the whole set, as above: what the
      Changes tab still sends, since a trade left out there changes a case, which is made anew where it stood.
    - *Corrected in review: the command's name in the log says what is new.* With a whole set: the iterations
      given that no case made before is, the first three and how many more, then how many are as before
      (`add each: DRAFT-1 + DRAFT-2, with 3 as before`; `add each: 2 as before` where none is new). With `keep`:
      the iterations given, then `beside the cases made`.
    - *Corrected in review: a view's selection is checked whole.* As built, the reading answered no key for a view
      that selects nothing, without checking it, and 409 for a table with no order. The view is now checked as
      the preview checks one, before anything is read: a view that names what the table has not is `InvalidView`
      (400), and only an order that has moved is `ViewMoved` (409, to be read again).
    - *Added in review: Price's check of a case* also refuses a case whose After holds a trade the case takes
      out. No state reaches it today, since a case of the set is not edited (ADR-0010 §3's note); it is there so
      that such a case is refused, never priced as another set.
    - *Changed in review: the page's names.* A button says what it does as a verb (design guidelines §10):
      `Make case…` in the caption, *Make case from the selection…* on the menus and as the dialog's title. On the
      Changes tab a trade a case takes out is `Keep <id> in After`, which is what its press does. The two caption
      buttons say in their title why they are disabled while the page is busy.
    - *Known limits, recorded in review.* The dialog's case and the Changes tab's buttons have no line and no
      palette entry, since how the line spells a list is open (§3): they are the page's gestures over a command
      the AI invokes with the same value. A trade left out on the Changes tab says the whole set again, so a
      stale case of the set refuses it by that case's trade: the stale case is removed first.

### Notes of 2026-11-11: what step 4 settles (all DEFAULT)

Step 4 is three pull requests (`docs/specs/commands-4-view-verbs-ai-reads`): 4a the AI's describe, read and
preview (§7), 4b `sort` and `filter`, 4c `select`, `copy` and `export`. Note 38 is 4a's; the later ones are added
as each is built.

38. **The AI describes, reads and previews through three capabilities (§7; 4a).** §7 decides that there are three
    and what each answers. What is left to settle is what makes a reading a command, what each takes and answers
    within a contract's shapes (notes 12 and 37), and the bounds of a page.
    - *A reading of a workspace is a command.* A workspace is its owner's (ADR-0003), so reading one acts for a
      caller, as reading a Run does (`ReadRun`, ADR-0011 §7). One more effect, `ReadWorkspace`, makes a
      capability a command: invoked through the gateway for the caller the server authenticated, offered to the
      agent as a tool, refused as a workflow step. It writes nothing and starts nothing. A table read as a
      workflow step (§8) is another capability, of a Run's accepted input, and is not this step's.
    - *None of the three is typed.* They declare no command form, so the line does not list them: a person has
      the screen, the line's own preview and the grids. They are the kernel's and name no application.
    - *`tables.describe`* takes the workspace, the revision it was read at where the caller holds one, and a
      table where one alone is wanted. It answers the registration read out, as two lists, since a contract
      holds no list in a list: `tables` (id, version, title, words, the key's field, how many rows as the
      workspace stands, its subsets by name, whether it is of one row, whether it is the application's main
      table) and `fields` (its table, name, title, words, form, how a value of it is written in the reader's own
      words, whether a command sets it, whether it may be empty, its choices as a cell writes them, the field
      that carries its unit, the table an id points into, its group). Choices are listed to fifty, and how many
      more there are is said beside them.
    - *`tables.read`* takes the workspace and revision, a table and rows exactly as `entity.set` takes them
      (note 12: the rows as the line spells them, read by the one selector), the fields wanted by their words,
      and where the page starts and how many rows it holds. It answers the table, how many rows the selector
      matched, where the page starts, the fields by name in the order asked, and for each row of the page its
      key, its subset and its cells: each value as text, written as `set` reads it back (`5mm`, `1.25%`,
      `empty`), so what the agent read it can write. A unit is in the field the description names, never in a
      title. With no field asked the rows are their keys alone, which is how rows are counted.
    - *The page is bounded and says so.* At most 200 rows, 50 where none is asked, and at most 2,000 cells: a
      page of many fields holds fewer rows. The answer always says the count, where it started and how many
      rows follow (`more`), and the next page is asked from there. A limit over 200 is refused by the
      contract, never cut quietly. The rows come in the table's own order, or as their ids were listed; a
      reading does not sort.
    - *`commands.preview`* takes the workspace, the revision and a line as typed. It answers §5's reading: the
      command and its version, the inputs that run it (as JSON text, since a contract holds no object in an
      object: they are handed to the command's own tool as they are), each argument as read, the rows meant
      (their table, their title, their count and the first of them), what would change, the line a short form
      stands for, and the moment a reading speaks of. A line that cannot be read refuses the call with the
      reading's own code, sentence, word and candidates. It has no view to give: `selected`, `shown` and the
      verbs of the screen are refused by name (`NoView`), as note 17 has it for any caller without a screen.
      What could follow the last word is typing help, and is not answered.
    - *Refusals are the tables' own.* An unknown table, field, subset or row, a condition that cannot be read
      and a workspace that has moved are refused with the code and sentence a line gets, with the word at
      fault and what it could have been. *Corrected as built:* another user's workspace is refused as every
      command refuses it (`NotOwner`), and the first draft of this note said "as an unknown one is". The
      three go through the door every command of a workspace goes through, and a refusal of their own would be
      a second rule for who may read.
    - *Left for later.* Retaining a reading as a dataset a view binds (§7's "may"), so that the pane draws it
      as a grid: the pane shows a reading's answer as it does any tool's. A sort of the reading.
    - *Added as built: how a value is written has one wording.* The sentence a description gives for a field
      ("an amount: 5mm, 250k, 1bn, or a bare number of millions") is the one a refused value is told, from one
      place in the reader, so the description cannot say a spelling the reader then refuses.
    - *Added as built: a field asked twice is refused* (`FieldTwice`), since a row's cells are held by field.
    - *Added as built: the agent's instructions name the three by what they answer*, and only "where they are
      among your tools": read rather than guess, say a count as the tool gave it, and preview when asked what a
      command would do and before a command over many rows. The instructions still name no application.
    - *Added as built: the pane keeps a long answer inside its card.* A page of rows is a long block; the
      answer of a step scrolls within 30% of the window's height and takes the keyboard, so the conversation
      under it stays in reach. This is the pane's drawing of any tool's answer, not of a reading alone.
    - *Added as built: the stand-in agent reads too.* With no model configured the pane's agent is the scripted
      stand-in (ADR-0007), and without words for the three nothing on screen would show them. It takes
      `describe the tables`, `read <rows> [fields <a>, <b>]` and `preview <line>`, for the workspace the pane's
      context names, and says the count and the sentence as the tool answered them. It lower-cases what it is
      told, as it always has, so a key typed in capitals is not found by it; the real agent passes words as
      they are.
    - *Corrected as built: a bound of a contract written as a whole number threw.* `minimum` and `maximum` are
      now read as the number is, whichever way the schema wrote them. No contract before this one had a bound.

## Rejected

- **A range syntax of the pricer's own** (`after off where ccy=USD`, the review's R6). A second grammar per domain,
  and nothing for a CSA, a Rates line or the next application.
- **Recording `where` as a rule** that keeps applying. A figure would change because a trade was added, with no edit
  that says so.
- **Reading on the client.** Two readers disagree, and one of them already fails silently.
- **A full query language** (`or`, parentheses, functions). Not typed in one line, and the AI's read (§7) covers what
  a person would not type.
- **The AI driving view verbs.** Grid automation, against CLAUDE.md.

## Open

- How many selected ids the pane gives the agent as context before it gives only their count.
- How an edit over many matched rows, and its `undo`, are recorded so the workspace stays fast. A batch writes one
  log entry a row today. The owner's sizing (2026-11-10): a workspace holds at most about 10,000 trades, and about
  100 at the median. To be measured at 10,000 rows before §2 is built for `where` (ADR-0003 measures at 10,000 and
  50,000).
- How a layer is named. ADR-0010 §9 writes `set after CSA-A threshold 5mm`; with `after` also a field of a trade, a
  named option (`layer=after`) reads without ambiguity. Decided when ADR-0010's Advanced step is built.
- How a currency is written with an amount (`-350,000 USD`), as a form of `FieldForm`, where the field's unit is not
  fixed by the row.
- How the line spells the lists of an `add each` iteration (§3): the trades added together, the trades taken out,
  and where the next iteration starts. Left aside by the owner (2026-11-11); the typed value does not wait for it.
