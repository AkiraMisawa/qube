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
4. View verbs and the AI's row read. **Built 2026-11-11** (`docs/specs/commands-4-view-verbs-ai-reads`, in four
   pull requests: the AI's reads; `sort` and `filter`; `select`, `copy` and `export`; and note 28's leftovers,
   `vary`'s completion and a domain verb's refusal marking its word); what it settles is notes 38 to 45 below.
5. Drafts (§10): a detail dialog drawn from a registration, with the tables under its row and its templates. The
   pricer's trade and CSA must carry those fields and tables first; its sample has neither them nor such a dialog.
   The pricer's scenarios as a registered table, a row computed from its shifts with their table under it, come
   with this step too (note 32). **Built 2026-11-12** (`docs/specs/commands-5-drafts-rows-under-a-row`, four pull
   requests); what it settled is notes 46 to 59 below. 5a: rows under a row, and the pricer's PV legs and
   independent amounts. 5b: templates, the pricer's fields beyond its grids, and the input Price freezes. 5c:
   drafts, a row's dialog and its button in the grid. 5d: the scenarios and their shifts as tables, and
   `load trades <ids>`. Left for the owner: where a row's button stands in a grid (ticket 16).
6. Saved sequences of edits (§8). §1 and §2 serve them as they are. **Built 2026-11-12**
   (`docs/specs/commands-6-saved-sequences`, one pull request); what it settled is notes 60 to 64.

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
      *Amended in step 4 (note 44):* where the words go on to name a field of the screen's table with more words
      than name the table (`trade id`, where `trade` is a table's word), they are that field, and nothing is
      refused. Words that name both with the same count are refused as before.
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
      other verbs that name rows. *Both built 2026-11-11 (note 45): `vary` offers its rows, columns and values,
      and the pricer's refusals name their word and what it could have been.*
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
preview (§7), 4b `sort` and `filter`, 4c `select`, `copy` and `export`. Note 38 is 4a's, notes 39 to 41 are 4b's
and notes 42 to 44 are 4c's.

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
    - *Added as built: how a value is written is said in one place.* The sentence a description gives for a
      field ("an amount: 5mm, 250k, 1bn, or a bare number of millions") is the one a refused value is told, from
      one place in the reader, so the description cannot say a spelling the reader then refuses. A choice is the
      one form with two sentences, both there: a refusal names its values, and a description, which lists them
      beside the sentence to fifty, says "one of its choices".
    - *Added as built: a field asked twice is refused* (`FieldTwice`), since a row's cells are held by field.
    - *Added in review: where a page starts is bounded too.* A start past the last position a page can hold is
      refused by the contract, as a limit over 200 is; the first build read it as a number it could not hold and
      failed. And a preview always says what would change: `would` is part of every answer, since every command's
      reading says it, and a reading that did not would be a defect, never an answer with a sentence made up.
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
      told, as it always has: rows, fields and choices read the same in either case, and a value whose capitals
      matter (an identifier a `set` would write) reaches the tool in lower case, so its preview shows it so. The
      real agent passes words as they are. *Corrected in review:* the first wording said a key typed in capitals
      is not found, which is untrue, since keys are read without regard to case. And a reading that was refused
      took every word after its own, so what is said next is a request of its own: the first build joined it to
      the refused one, as it joins an answer to a question about a missing term, and read `describe the tables`
      after a refused `read` as more rows.
    - *Corrected as built: a bound of a contract written as a whole number threw.* `minimum` and `maximum` are
      now read as the number is, whichever way the schema wrote them. No contract before this one had a bound.

39. **A verb of the screen asks its page (§3; 4b).** `find` answers a place (note 21). `sort` and `filter` answer
    what the grid's page is to do with its grid: a reading of a view verb names no command and one thing asked of
    the page, a `place`, a `sort` or a `filter`, each with the sentence to say once it is done. The page's door does
    it with the grid the view was read of, as it puts a place, and answers why where the grid cannot.
    - *They act on the grid behind the line.* §3 spells them with a table (`sort <table> by …`). The table is the
      grid's, so it may be left out (note 23), and where it is said it must be the table of the grid behind the
      line: another table's grid is gone to first (`go`). With no grid behind the line they are refused (`NoView`).
    - *The grid's own Sort and Filter are what they set.* The header that sorts a grid and the line set one state,
      so a grid sorted from the line shows it in its header, and a sort from the header is what `sort` then
      replaces. Nothing is kept on the server: a view is a value (note 16).
    - *Not the AI's, not logged, not undone* (§3): they change no workspace.
    - *Added as built.* A sort or a filter ends as a command done does, the keyboard back where it was; only a
      place answers that the keyboard was moved. What any of the three asks is of the workspace as it stood when
      it was read, and no write refuses one that has moved since, so a reading of another revision than the page
      then holds is asked for again (as note 21 has it for a place). The grid may be said by its table and subset
      (`filter trades existing where …`); another table's or another subset's is refused (`OtherGrid`).

40. **`sort` (4b).** `sort [<table>] by <field> [desc] [, <field> [desc]]` and `sort off`.
    - A field is one of the table's, by its words; `desc` or `descending` after it turns it, `asc` or `ascending`
      may be said. Several fields are separated by commas, the first the major key. `sort off` gives the grid the
      order it has with no sort.
    - Where the server pages the grid, its reader is asked whether it sorts by those fields before the page is:
      a field it does not sort by is refused by name (`NotSortable`), never ignored. Where the page holds every
      row the grid sorts them itself, as its header does, and a field the grid shows no column of is the page's to
      refuse.
    - It completes `by` and `off`, then the table's fields, then `desc`.
    - *Added as built.* `by` is required (`sort notional` is refused, `NoSort`), so that a table's word is never
      taken for a field's. The reader is asked once for each field, and the one it does not sort by is named.
    - *Added as built: a grid that does not sort a column says so.* A page that holds its rows names the columns
      its grid does not sort by (ExGrid's `queryable: false`), and a sort by one is refused on the line by the
      column's name, the grid keeping the sort it had. The Rates line grid sorts by none: it shows a term as
      typed (`10y`, `5mm`), which the grid would order as text, and an order that looks right and is not is the
      one thing not to show. Sorting it by what a term means is left for later.

41. **`filter` (4b).** `filter [<table>] where <conditions>` and `filter off`.
    - *The conditions are the selector's* (§2, note 8): the same fields, operators and values, read by the same
      reader and refused as `where` is, so the rows `filter where ccy=USD` leaves are the rows of the grid's subset
      that `set where ccy=USD` would write. They are kept as typed and read again at each reading: a row edited
      so that it no longer meets them leaves the grid, and a period (`maturity<1y`) keeps counting from the
      table's date. A new `filter where` replaces the conditions; it does not add to them.
    - *A view carries its conditions* (`where`, beside its sort): `shown` then means the rows they leave (§2,
      which step 2 left as the grid's subset), a position is a position among those rows, and the view's sentence
      says how many they leave. The order's token covers them: it moves when a row enters, leaves or moves, and
      only then.
    - *Where the server pages the grid*, the page keeps the conditions as the grid's filter and sends them with
      each page it asks for; the table's own reader gives the order and the kernel narrows it, so the page and a
      line's reading of `shown` cannot disagree. The trade grids refused a filter until now (§3).
    - *Where the page holds every row*, it shows the rows the server says the conditions leave, asked for again at
      each revision it shows (`POST /api/workspaces/{id}/shown`): the client reads no condition (see Rejected,
      "Reading on the client"). Until the answer for a revision arrives the grid shows the rows of the one before
      that are still rows, and with none yet, no row.
    - *A filtered grid says so.* Beside the grid's title: the conditions, how many rows of how many, and a button
      that takes the filter off, which is the pointer's `filter off`. A filter that can no longer be read (a field
      gone) shows no row and says why, never every row.
    - *It may leave no row.* `filter where ccy=CHF` on a grid with no such row shows none, and the reading says
      "0 of 43" before Enter.
    - *Left for later.* The grid's own filter panel (ExGrid's column filter) stays refused where the server pages:
      its conditions are the grid's and would need a second reader on the server. `or` and parentheses (see
      Rejected, "A full query language").
    - *Added as built: where the conditions are kept.* They are the Opaque Filter of the grid's source (ExGrid's
      name for the part of a Filter only its Consumer reads), beside the filters of the grid's own columns, which
      the grid keeps as they are. The view reads them from there, so it says the conditions the grid holds
      whether or not its rows have arrived under them.
    - *Added as built: `shown` and the conditions together.* A view that names the rows it shows and carries
      conditions means the rows named that meet them. A grid whose page holds its rows says both.
    - *Changed as built: before the first answer.* The note said that a grid whose page holds its rows shows no
      row until the server has answered for new conditions. It keeps the rows it has instead, its caption
      saying the conditions with no count yet: with no row shown, a line chained on `filter` (`set shown …`)
      would have been read as of no row or of the right rows by which arrived first, and an outcome is not to
      depend on timing. Read with the rows named and the conditions together, it is the right rows either way.
    - *Added as built: conditions that can no longer be read* are refused as that (`FilterUnread`), never read as
      none: by a reading of `shown`, by `find`, by a position, by the page endpoint and by `/shown`. The view's
      sentence says so and that its rows can no longer be said, and `shown` is not offered. A line that names its
      own rows is still read, and so is `filter off`. A grid whose page holds its rows then shows no row and says
      why beside the filter. A grid the server pages keeps the rows it last read, as it does when any read
      fails, with why in the page's alert, said for that grid and taken back when its reader answers again.
    - *Added as built: what is said.* The caption says the conditions as the grid holds them, which is as typed
      (`Filter: ccy=USD · 17 of 43`), and the notice as the server read them (`where ccy = USD`). The page's
      answer says how many rows the grid has with no conditions (`of`), and so does `/shown`. The order's token
      under conditions is the reader's with a digest of the keys left, in their order (corrected below).
    - *Measured at 10,009 rows* (Release build, SQLite, the Existing grid sorted): a page under conditions takes
      15 to 57 ms (84 ms the first time) against 7 to 8 ms with none, a reading of `set shown a off` under them
      46 to 129 ms. The conditions are read again at each page, and nothing is kept between pages: a module's
      cell arrives with no edit, so rows kept by the state alone could be stale.
    - *Corrected in review: which rows conditions leave is the server's alone to say.* "`shown` and the
      conditions together" had a grid whose page holds its rows name the rows it shows beside its conditions.
      That left one more outcome to timing: at a new revision the grid still holds the rows of the answer before,
      so a row an edit had just brought into the conditions was missed by a line chained on it until `/shown`
      answered. A view now says its conditions and names no row for them, where the page holds the rows as where
      the server pages them: the server reads which rows they leave at the revision the line is read at, whatever
      the grid has drawn. One grid still names rows beside its conditions: one that also filters by its own
      columns (ExGrid's filter panel, where the page holds every row), since only the grid knows which rows
      those leave. The server reads the conditions among the rows it names, and its page gives it every row
      until the server has answered for the conditions and the revision it shows, so the rows named are never
      short of one.
    - *Corrected in review: what a grid whose page holds its rows draws while it waits.* It keeps what the server
      last said until it says otherwise: the rows of the same conditions at the revision before, the rows it had
      under the conditions before these (every row where it had none), and no row, with why, where the
      conditions could not be read, at a later revision too. As built it showed every row for a moment in the
      last two cases. The caption counts the rows drawn, and says new conditions with no count until they are
      answered. An answer that did not come is said as conditions that could not be read are.
    - *Corrected in review: the order's token under conditions* is a digest of the keys left, in their order, and
      no part of the reader's. The reader's moves with a row the conditions leave out, which made "and only then"
      untrue: a selection in a filtered grid was refused as moved after an edit elsewhere.
    - *Added in review: they last as long as the panel.* Sort and filter are view state, not settings
      (ADR-0017 §1 and §6, the owner's): neither is kept with the workspace or for the user, and a grid drawn again
      starts with none.

42. **One thing asked of the page, said by its kind (§3; 4c; changes how notes 21 and 39 are sent).** A view
    verb's reading had three members, `place`, `sort` and `filter`, of which one was there. With three more verbs
    it has one, `asks`, whose `kind` says which it is: `place`, `sort`, `filter`, `select`, `copy` or `export`.
    The first four are done with the grid the view was read of, and name it by its table and subset. `copy` and
    `export` are done by the page's door and need no grid. What each asks is unchanged; only where a reading
    holds it has moved. The reading of a revision the page no longer holds is asked for again, for all six
    (note 39).

43. **`select` (4c).** `select <rows>` places the Selection of the grid behind the line on the rows named.
    - *The rows are the selector's* (§2), read with the view: ids, `all`, a subset, `shown`, `selected`, each
      narrowed by `where` (`select where ccy=USD`, `select selected where notional>50mm`). An id is looked for in
      the grid's table. Rows must be said: `select` alone is refused (`NoRows`).
    - *They are rows the grid shows.* Rows of another table, of another subset, or that the grid's filter leaves
      out are refused by name (`OtherGrid`, `NotOnScreen`), never left out quietly: `select all` behind a grid of
      one subset says which rows are not in it, and `select shown` is the way to say every row of the grid.
      Conditions that no row meets are refused (`NoRowMatches`).
      *Corrected in review (2026-11-11).* As first built, `where` with no rows before it meant rows of the whole
      table, as it does for `set`, so `select where ccy=USD` behind the Existing grid was refused where a new
      trade was in USD too, and the line had to say `select shown where ccy=USD`. That refused §9's own chain
      (`go existing`, `select where ccy=USD`, `set csa CSA-B`), which is accepted. Conditions with no rows said
      before them are now asked of the rows the grid shows, for `select` alone: the Selection is that grid's, so
      there is no other row the line could mean. `all`, a subset and ids still say rows of the table, as they do
      for every verb, and one the grid does not show is refused by name with `shown` as the way on. For `set`,
      `copy` and `export` a `where` said alone is still the whole table (note 18): they act on rows, on screen
      or not.
    - *One run of rows.* ExGrid places one range for a Consumer (`placeSelection`, 0.2.0); a Selection of several
      ranges is made with the pointer and the keys alone (asked for as ex-grid issue #561). Rows that are not
      next to one another in the grid's order are therefore refused (`SeveralRuns`), with how many runs they are
      in and the way on: filter or sort the grid so that they are, then `select shown`. Selecting the first run
      and saying nothing would be a Selection that looks like the answer and is not.
    - *Who says whether they are one run.* Where the server pages the grid, the server does, in the view's order
      (refused as moved where that is no longer the table's, as a position is), and answers the first position
      and how many. Where the page holds every row, the grid orders them itself: the server answers the rows'
      keys, and the page refuses at Enter the ones its grid does not show next to one another.
    - *What is placed.* Whole rows: every column of the grid, the Focus on the first row in the column it was
      on, or the first column. It is placed in the Row Sequence Version the view was read in, so a grid that has
      moved since refuses it, and so does one that keeps an edit open (as `find`, note 21).
    - *Afterwards* the keyboard is back where it was, as after a command done. With the chain key (note 24) the
      next line reads the view again, so `select where ccy=USD` then `set notional 5mm`, or `copy`, acts on the
      rows just selected (note 18).
    - It completes as rows are said (note 15). Not the AI's, not logged, not undone: it changes no workspace.
    - *Left for later.* Several ranges, when ExGrid places them. Taking the Selection off (`select off`): ExGrid
      places a range and has no call that leaves none.

44. **`copy` and `export` take rows out of the workbench (§3, amending its spelling; 4c).**
    `copy [<rows>] [<fields>] [with headers]` puts cells on the clipboard, and
    `export [<table>] [<rows>] [<fields>]` saves them as a file.
    - *The rows are the selector's* (§2), with no grid needed where the line names them (`export trades`,
      `copy csas where ccy=USD`). Left out, `copy` means the Selection, or the row the keyboard is on (note 18),
      and `export` means the rows the grid behind the line shows; a table named with no rows is every row of it
      for `export` (§3's `export <table>`), and refused for `copy` as it is for `set`. With neither rows nor a
      grid both are refused. No row to write is refused (`NoRowMatches`), never an empty clipboard or file.
      *Corrected in review:* a grid that shows no row refuses `export` as `shown` is refused there
      (`NothingShown`), and a line with nothing selected and no Focus as `selected` is (`NothingSelected`);
      `NoRowMatches` is for conditions, `all` and a subset that come to no row.
    - *The fields* are the table's, by their words, separated by commas, in the order named and each once
      (`UnknownField`, `AmbiguousField`, `FieldTwice` as elsewhere). With none named, every field of the table in
      the table's order, which begins with the row's key.
    - *The order of the rows.* As their ids were listed. Rows said as the screen's (`selected`, `shown`, or left
      out) come in the order the grid shows them where the server pages it, since the server knows that order.
      Any other rows come in the table's own order; a grid whose page holds its rows and sorts them itself is
      not followed, which the notice cannot say and the status lists as a limit.
      *Corrected in review:* where the page holds every row, the Selection does come in its grid's order: the
      page names the selected rows by their keys in the order the grid has them, and the server writes them as
      named. So does `shown`, and the rows `export` takes when none are said, where the grid's own column
      filters narrow it, since the page then names the rows it shows. What is not followed is `shown` on such a
      grid with no filter of its own: the page names no rows then, and how it sorts them is not sent, so they
      come in the table's order.
    - *A cell is written for a spreadsheet to read as the value held.* An amount, a number and a count of days
      as a plain number with every decimal and no grouping (`50000000`), since a spreadsheet reads `5mm` as text.
      A rate and a spread with the percent sign (`1.25%`, `40%`, `1.2%` for 120bp): a spreadsheet reads each as
      the number it means, where the bare number held would be a percent for one field and a fraction for
      another with nothing to tell them apart. A flag `true` or `false`. A date `yyyy-mm-dd`. A period, an id
      and a choice as written. No value is an empty cell. Nothing is rounded, and nothing is cut.
    - *What it does not do.* A text is written as it is. A spreadsheet reads one that begins with `=`, `+`, `-`
      or `@` as a formula, and may drop the leading zeros of an id made of digits: writing an apostrophe before
      it would put a value in the file that the workspace does not hold, which needs the owner's word (the
      fallback rule), so it is **put to the owner** and until then nothing is altered. An amount copied does not
      read back as an amount typed here: a bare number is millions (ADR-0008), so one of a million or more is
      refused and a smaller one would be read a million times too large. That is the reader's rule for every
      bare number, and `copy` is for a spreadsheet.
    - *`copy`*: tabs between cells and CRLF between lines, none after the last, as a spreadsheet's own copy; a
      cell holding a tab, a line break or a quote is quoted. No header line, as the grid's own Copy has none, so
      that what is copied is pasted as cells; `with headers` at the end of the line adds one, as the grid's Copy
      with headers does.
    - *`export`*: comma-separated values (RFC 4180), always with a header line, UTF-8 with a byte order mark so
      that Excel reads it as UTF-8, named by the table's word (`trades.csv`).
    - *A header* is the word a command names the field by (`notional`, `wind speed`), which no two fields of a
      table share. A field's title may be shared by fields of two groups.
    - *Not truncated, and capped.* More than 1,000,000 cells are refused before Enter (`TooManyCells`), with the
      count: ten thousand trades of every field are well under it.
    - *The content is asked for at Enter.* A reading is made at every keystroke and says only how many rows and
      which fields. At Enter the page's door asks `POST /api/commands/content` with the line, the view and the
      revision of the reading: the server reads the same line again at that revision, refused if the workspace
      has moved, and answers the text. The door writes the clipboard or saves the file, and says the server's
      sentence once the browser has taken it. A browser that refuses the clipboard is said as that, with its
      reason, and nothing is half done.
    - *Where the clipboard is not to be had.* A browser gives a page the clipboard only over https or on
      localhost. Elsewhere `copy` is refused saying so, and `export` still works.
    - Not the AI's (it has `tables.read`, note 38), not logged, not undone: they change no workspace. That rows
      left the workbench is written nowhere, as the grid's own Copy writes nothing.
    - *Added as built: where the fields begin.* A list of fields follows the rows, so the reader is told so: the
      first field ends at its comma (`copy volume, unit` names two fields and no row), and words after a table's
      own that name a field of it say no rows (`export stations altitude`). A word there that names no field is
      still read as a row's id, so a misspelt field after a table is refused as a row that is not there
      (`UnknownRow`), not as a field.
    - *Changed as built: more words that name a field are that field (amends note 18 of step 2).* `trade` is a
      word of the pricer's trades and `trade id` a field of them, so `copy trade id, notional` was read as the
      table with the fields `id, notional`, and refused for naming no rows. Where the words at the start of the
      rows name a field of the screen's table with more words than name a table, they are the field and no table
      is said, as the longest words name a command (§1). This holds for every verb that reads rows, not only
      these two: `set trade id` on the trade grid was refused as words that could be either, and is now read as
      the field. Words that name both with the same count are still refused with both spellings.
    - *Corrected in review (2026-11-11).*
      - *A line of one empty cell is written as a quoted empty cell* (`""`), in both formats. Left blank, such a
        line is one that readers of comma-separated values skip (a row lost without a word), and the last line
        of a clipboard, or its only one, would be no text at all: `copy` of one empty cell said Copied and left
        the clipboard empty. A spreadsheet reads `""` as an empty cell, as it reads every quoted cell. Beside
        another cell an empty one stays empty: the separator says it is there.
      - *The file is named by the table's word as it is* (`trades.csv`). As first built a space in the word was
        written as a hyphen, which nothing asked for.
      - *The door cannot hear whether a file was saved.* A browser saves a file a page hands it, or asks the
        user where, and tells the page neither: the door says the server's sentence once it has handed the file
        over, and cannot say that the browser kept it. The clipboard does answer, and a refusal is said with its
        reason. The status lists this as a limit.
      - *A short form that stands for a line of `copy` or `export`* (note 29) answers that line's content, as its
        reading is that line's; the content followed no short form as first built, and Enter was refused.
      - *After a comma a field must be named* (`NoField`), and the first words of a line with no rows said are
        the first field where a comma ends them (`copy site, volume` is two fields, whatever table `site` also
        names).
      - *What the list offers.* A field offered with no rows before it says what the verb would take: the
        Selection or the row the keyboard is on for `copy`, the rows the grid shows for `export`
        (`volume (3 shown)`), which said the Selection as first built. Fields go on being offered after a comma
        with the rows left out. After a table's own word `copy` offers rows and no field, since that word alone
        says no rows for it.
    - *Added as built.* `export` accepts `with headers` and writes the header line it always writes. The most
      cells is the kernel's, given where the lines are built (`CommandLines`' `mostCells`); no host setting names
      it yet. `select`'s list offers the rows of the grid's table, and one the grid does not show is refused when
      the line is read (`NotOnScreen`): the list is not narrowed to the rows shown. The AI's
      `commands.preview` refuses both lines as it refuses a verb of the screen, and names `tables.read`.

45. **What note 28 left: a domain verb's refusal names its word, and `vary` completes (4d).** DEFAULT, 2026-11-11.
    - *A workspace's refusal may name the word it refuses and what it could have been* (`WorkspaceRefused.Word`
      and `Candidates`), as a table's does (`TableRefused`). A reading marks the word where it stands in the line
      after the command's own words, and answers the candidates; a word that stands there twice is not marked, as
      for the kernel's verbs (note 15). The capability the same reader serves refuses by its code and sentence,
      as before: one reader, so the AI's `commands.preview` answers the same word and candidates.
    - *Why on the workspace's refusal, and not a second type thrown by the reader.* A domain verb plans its
      changes in one place for the line and for the capability (note 28), and that place is where the word is
      known. A reader that caught the plan's refusal to throw another would say each refusal twice.
    - *Which of the pricer's refusals name their word:* `vary`'s row and its column (`UnknownCell`,
      `AmbiguousCell`), a measure that is not registered (`UnknownMeasure`), `cds`'s single name that the
      catalogue has not, or has several of, and a word that is no sector, region or rating, a case that
      `case remove` cannot find or finds twice, and a scenario's name that the workspace has not. A refusal of
      the whole line (nothing to vary, no measure left) names no word. *Added in review:* a value `vary` cannot
      read (`InvalidValue`), below.
    - *The candidates are what the sentence lists.* For a row: the CSAs, the netting set, `credit`, `cds` and
      the modules that have settings. A trade's id is a row too and is not listed: one counterparty may have ten
      thousand, and the sentence says so in words.
    - *`vary` offers what could follow* (note 28's "the domain's to say"): the rows a cell is named on, a trade's
      id as far as it is typed and a few at a time with how many more, as `set` offers rows (note 15); then the
      row's columns, by the word that names each, written from the column's first word; then, for a flag and a
      choice, the values it has, again after each comma. An amount, a rate and an id are typed freely, so nothing
      is offered for them.
    - Rates' verbs that read words (`add line`) and the scenario verbs' own words already refuse with the word
      (they throw the table's refusal); nothing of theirs changes.
    - *Changed as built.* `cds` is a row only where the counterparty has a curve chosen: the sentence, the
      candidates and what `vary` offers are the rows the state has (`PortfolioCells.Rows`), and with no curve
      `cds` is refused by why there is none, with its word and no candidate. A row or a case that names two
      things is named with its word; a case's candidates are then the ids to say instead, and a row's are none,
      since both are the word. `cds`'s word that is no sector, region or rating is offered the values of the
      kinds the line has not given yet, and a single name the catalogue has not is offered each name's words.
    - *Added as built.* `vary` offers the rows that are no trade first, then trades, twenty at a time, each with
      what it is (`a CSA`, `the netting set`, `a trade`). A column is found by any of its names as far as typed
      (`fixed` finds `rate`) and offered by the word that names it. A column that may be empty offers `empty`,
      whatever its form, as `set` does. What a field has to choose from is one function of the kernel's
      (`FieldForms.ChoicesWritten`: a flag's two words, a choice's values, `empty` where it may be empty), which
      `set`'s completion and `vary`'s both ask.
    - *The client draws none of this anew.* The line already selects the word a refusal marks, or leaves the
      caret after it where the word ends the line, and lists what a reading offers; `candidates` is in the
      preview's contract for the AI, and the line does not draw it.
    - *Corrected in review.*
      - A value `vary` cannot read is a word of the line too, and was not named: `InvalidValue` now names it,
        with what the cell's form has to choose from, and a comma between digits (`10,000k`) names that text.
      - `cds`'s single name was named with one space between its words, so words typed with two were not
        marked: it is named as typed. Two sectors, regions or ratings name the second. `case remove` with a
        name and no case names the name, as a scenario's does.
      - Rows were offered by how an id starts alone; they are offered by how it starts or ends, as note 15 says
        `set` offers them, so `e00003` finds a trade whose id ends with it.
      - A value listed before a comma is left out however it was written (`yes` is `on`).
      - The sentence for a row that is not there listed `cds` with no curve chosen, against "the candidates are
        what the sentence lists" above; it lists `cds` where the state has it.
      - A choice registered with nothing to choose from: `set`'s completion offered nothing for it, or the rows
        of a table it pointed into, and the first build made the function above throw there, at each key typed.
        It is the registration's defect, and now fails where the others do, when the table is read
        (`TableRegister.Of`), naming the field.
      - Not held by a test: a row, a column or a case that names two things (`AmbiguousCell`,
        `AmbiguousCase`). Nothing in the sample's own data makes one.
      - Left as it is: a refusal names one word, so of several measures that are not registered it names the
        first, while its sentence says them all.

### Notes of 2026-11-11: what step 5 settles (all DEFAULT unless marked)

Step 5 is four pull requests (`docs/specs/commands-5-drafts-rows-under-a-row`): 5a tables under a row, a figure of
them and the rows `new` makes under a row; 5b templates and the pricer's fields by group; 5c drafts and the detail
dialog; 5d the pricer's scenarios as a table, and `load trades`. The owner chose four things of the dialog's look
before it started (2026-11-11, by decision cards in the thread, each the option proposed): the sample's trade and CSA
carry every field of his examples and the tables under the row; the dialog's tabs are Original and Live; a grid's row
has one button, edit; and `load trades` reads a synthetic store of booked trades by id. Notes 46 to 49 are 5a's.

46. **A table under a row (§1; 5a).** A table's description names the table its rows are under (`Under`), and each
    of its rows the key of the row it is under. A row's key is whole in its own table, and its table writes it: the
    key of the row above, a slash, and its own (`E00003/1`). The kernel takes no key apart.
    - **Rows are said as any rows are**, and one way more: in a table under another, an id that names no row of it
      and one row of the table above, as that table's ids are read (note 7: whole, or by how it ends), names every
      row under that row. `legs E00003/1` is one leg, `legs E00003` the trade's legs. An id that names a row above
      with nothing under it is refused as rows that match nothing are, saying so (`NoRowMatches`).
    - **With no table said**, ids are still looked for among each table's own rows (note 7): `E00003/1` is a leg and
      `E00003` a trade, never the trade's legs.
    - **The registration is checked when the tables are read**, as the others are: the table above is one of the
      workspace's, and a table under another is neither the main table nor a table of one row.
    - `tables.describe` (note 38) says a table's `under`; `tables.read` reads its rows as any table's.
47. **A figure of the rows under a row, and a number written with what it is in (§1; 5a; settles an Open item).**
    - **A field says it is a figure** (`Figure`): the table under its row whose rows it is worked out from, and,
      where it can be written, how a write is written, which may differ from how it is read. The figure is read as
      its form says (a number), in the unit the registration names for the workspace (`Unit`: the pricer's PV is in
      the display currency, which is the workspace's and no field of the row; §1's "resolved for a scope").
    - **What a write means is the table's**, as §1 says: `Set` answers the changes, and a row whose write changes
      nothing is counted as holding the value, since the figure alone cannot say whether the rows under it would
      change. For one row the preview says what the table says the write would do (`SetSays`: "2 legs replaced by
      1 leg, USD"); for several it says the count, as for any field.
    - **`FieldForm.Money`** is a number followed by what it is in: `-350,000 USD`, `-350k usd`, or the number alone.
      It reads as the number and, where one was written, the unit, one of the field's choices; a unit that is not
      one is refused with those there are. Written alone, the unit is the table's to supply and to say (the
      pricer's display currency). This settles "how a currency is written with an amount" under Open. A grid cell,
      the line and the AI write it the same way.
    - **`reset <row> <figure>`** is the table's too: it answers the changes that bring back the rows the source has.
    - *Added as built.* A figure whose table is not under its own is refused when the tables are read, naming the
      field. `tables.describe` says a figure's `figureOf` and `unit`, and what a write takes in `written`, which for
      a figure is how it is written and not how it is read. What a money field takes lists its units as a sentence
      does (`GBP, JPY, USD or EUR`). A figure that no command sets is registered as any field that is not editable.
    - *Corrected in review (2026-11-12).* A field written as a number with what it is in and given nothing it may
      be in is refused when the tables are read, naming the field: it was found only at the first write. A
      condition compares a figure as the number it is (`where load>50`), which the tests now hold.
48. **`new` makes a row under a row, and `remove` takes one away (§1, §3; 5a).** A row a workspace makes is an entity
    of a described product (ADR-0008), as a Rates line is. A product may say the registered table its entities are
    rows of (`Table`) and the term that names the row its entity is made under (`Under`).
    - **`new leg E00003 eur 250,000`**: after the product's words, the first word is the row above where it names
      one of that table's rows, as its ids are read; it is then written into the terms by its name and its whole
      key, so what is invoked and logged names the row exactly. Typed by its name (`trade=E00003`) it is read as any
      term. With neither, the product is refused as missing that term, as any required term is.
    - **`remove legs E00003/1`** is §3's `remove`: the table answers the changes. A row the workspace made is
      removed; a row the source holds is the table's to decide (the pricer marks a source's leg as taken out, so
      `reset` and `undo` bring it back).
    - Left for 5c: a product whose rules can say the changes that would make its entity without writing them
      (`Plan`), which a draft needs; and the row above read from the draft, where the line names none.
    - *Added as built.* A first word whose end names several rows above is refused with them, as a selector refuses
      it (`AmbiguousRow`), by the line and by the command invoked alike. One that names no row above is read as any
      term, and refused as a word that fills none. A product made under a row that has no such term, or says no
      table, is refused when the products are read.
    - *Corrected in review (2026-11-12): the term typed by its name.* "Read as any term" left `trade=E00004`
      refused where `E00004` as the first word was read: the two now read alike. The value typed by the term's name
      or by one of its other names names the row above as the first word does, by its whole key or by its end, is
      written into the terms with its whole key, and is refused where it names several; the first word is then a
      term like any other. A product whose table its application does not register, or made under a row with no
      tables given to read it in, is refused when the products are read; one whose table is under no table still
      fails where its terms are first read, since that is a table's description and not the product's.
49. **The pricer's legs and independent amounts (5a; the sample's own, every figure synthetic).**
    - **Tables**: `xva.legs` (`legs`, `leg`; under the trades; the leg's id, its trade, currency, PV in that
      currency, and notional) and `xva.amounts` (`independent amounts`, `amounts`, `ia`; under the trades; currency,
      start, end and value, which a command removes and does not change, as the owner's example shows them). An
      existing trade's legs are the source's; a new trade has none, since no book holds it.
    - *Changed as built: what the source's legs add up to.* A swap has two legs in its currency, which add up to
      its official PV. An FX forward has a leg in each of its currencies, which add up to its official PV at the
      value date's synthetic rate, to the unit: the first note said "in the trade's currency" of every trade, which
      a forward's legs are not. So a forward's first leg moves with the value date, and an edit of it is then held
      as a conflict, as any edit whose source moved is (ADR-0003).
    - *Added as built: ids.* A leg is `<trade>/<n>` and an independent amount `<trade>/IA<n>`, so that no leg and
      amount of a trade share an id and an id typed with no table names one row. A leg's PV and notional are plain
      numbers, as a grid cell takes them (`250,000`, `250k`), never in millions; a leg an overwritten PV made holds
      no notional.
    - **A trade's `pv`** is a figure of its legs: each leg's PV into the display currency at the value date's
      synthetic rate, added up; empty where the trade has no leg. Written (`set E00003 pv -350,000 USD`), the trade
      is left one leg of that amount, in the currency written or, with none, the display currency (owner,
      2026-11-10); the preview says "2 legs replaced by 1 leg, USD", the write is one command, and `reset E00003 pv`
      brings the source's legs back. A trade that already holds that one leg alone is counted as holding it.
    - *Added as built: the write and the reset.* The PV is held to the cent. Each leg the trade holds is taken out
      (a source's by an edit of its own, one the workspace made by its removal) and one leg is made, numbered after
      the last the trade holds as the write reads it, taken out or not; a new trade, which has none, is told "1 leg
      made". `reset
      <trade> pv` resets every edit of the trade's legs and removes every leg made under it, and `reset <trade>`
      with no field does that too, since the figure is one of the row's fields. A figure is no cell `vary` names:
      it holds no value of its own for a case to change.
    - *Corrected in review (2026-11-12): a number is not kept for a leg that is gone.* "The last the trade has
      had" said more than is built. A leg is numbered after the last of the source's legs, taken out or not, and of
      the legs the workspace holds as the command reads it: an overwrite's leg therefore takes the number after the
      one it replaces, and a made leg removed by `remove` leaves its number to the next leg made, which holds none
      of its edits (the removal drops them with it). The log names both by the same id; `undo` reads them in order.
    - **`new leg <trade> <currency> <pv> [notional=<amount>]`** adds a leg, numbered after the trade's last.
      Nothing is defaulted. The leg is in its trade's scope: a new trade's leg stays with it through a counterparty
      change, and an existing trade's is dropped with the counterparty's other edits, counted among them.
    - **A trade's `independent amount`** is a figure of its independent amounts, added up in the display currency
      as the PV is, which no command sets; `reset <trade> independent amount` brings back those taken out (*added
      as built*: without it only `undo` and the Changes tab would).
    - **Each grid shows PV** beside the trade's other columns, and a PV typed in a cell is the same `set`.
    - *Added as built: the grids and the Changes tab.* The column's header names the display currency (`PV (JPY)`),
      since the figure is not in the trade's own. It stands after B and A on both grids, where a grid's figures
      begin (the New grid's PV01, Margin and PV Adj follow it), so no column a term is typed in moves and the
      assignments stay in view at a laptop's width. The server sorts by it, and a PV that differs from what the
      source's legs give is marked against Original as any value is (ADR-0010 §2). A typed PV is sent as it was
      typed to `entity.set`, which reads it: the page reads none of it, and refuses only nothing typed and an `=`,
      which would name another field. The Changes tab lists each edit of a leg, each leg or amount taken out and
      each leg made, named in words (`leg E00003/1 taken out`), and resets each; their values are typed in a
      command, and in the trade's dialog from 5c, never there. The workspace's count of edits holds a leg made,
      as it holds the legs taken out for it: a PV typed for a new trade is one edit, not none.
    - *Corrected in review (2026-11-12): the cell and its header.* A PV cell committed as it was shown writes
      nothing: what it shows is every leg in one rounded figure, and written back it left the trade one leg of that
      figure, which the first build did. The header's currency is the page's own (`pvCurrency` on each page of
      rows), not the workspace view's, so a header never names another currency than the rows under it hold while
      one of the two answers is still on its way; before a grid's first page it names none. `ia` is the amounts'
      table alone: as a word of the trade's field too, it was ambiguous with a trade grid behind the line.
    - **No measure of the sample reads the legs yet.** SA-CCR's replacement cost reads an existing trade's
      official PV and takes a new trade as traded at market (ADR-0010 §6, ADR-0013), and a change of the legs
      moves neither. The PV legs say so where they are shown (in 5a, on the cell of a PV that differs from
      Original, which names the one of the two that holds for its trade; from 5c, in the trade's dialog), and
      whether a changed PV should reach a measure is those ADRs' to decide, put to the owner with the summary of
      decisions made for him.
    - *Corrected in review (2026-11-12): who reads what.* Naming SA-CCR alone was too narrow. The simulated XVA
      provider reads an existing trade's official PV too, and a new trade's PV Adj where XVA's PV adjustment is on
      (ADR-0010 §6, §8); spot IM reads sensitivities. So the cell says what Price reads, not what one measure
      does: "Price reads the official PV" on an existing trade, "Price takes a new trade at market, or at its PV
      Adj" on a new one.
    - *Decided by the owner (2026-11-12):* a changed PV does not reach a measure for now; it will when the real
      XVA analytics is connected. The question above is closed until then, and the cell goes on saying what Price
      reads.
    - *Changed as built: what Price freezes.* The first note said the legs are frozen with the rest of the input.
      They are not: the accepted input carries what a measure reads, and none reads a leg or the PV they add up
      to, so it carries neither. When a measure reads them, its module's part of the input will carry them.
    - *Changed after the owner's words of 2026-11-12: what Price freezes.* The entry above was wrong for what the
      owner needs, and stood for one pull request (5a). The legs and the independent amounts are frozen with the
      rest of the input, whichever measure reads them: note 53.
50. **A template (§1, §3; 5b).** A table's description lists its templates (`Templates`): each a name, a title, the
    words it answers to, the terms it takes and the fields it writes.
    - **The terms a template takes are read as a product's are (ADR-0008 §3):** by name (`currency=USD`,
      `ccy=USD`), or by form in any order where a term may be typed without its name (`set CSA-B as 2way usd`). §10's
      `set as 2way ccy=USD` reads the same. One reader serves `new` and `as`, and the owner's rule for typed terms
      holds for both (2026-11-04: short, in any order).
    - **`set <rows> as <template> [<terms>]`.** After the rows, `as` names a template of their table by its words,
      the longest run that names one. The table answers the value of each field the template writes for the terms
      given (`IWorkspaceTable.TemplateValues`), and they are written as several values are (note 9): in the template's
      order, each checked as the ones before it left the rows, all or none, one command and one `undo`. No other
      field of a row is written. `entity.set` takes the same words in `set`, so the AI and a button write a
      template as the line does.
    - **Refused by name:** `as` on a table with no template; a template the table has not, with those it has; a
      term it needs and was not given; and a term it has not, a word that fills none and a value that cannot be
      read, each as `new` refuses a product's.
    - **What the line says.** Before Enter, for one row, each cell that would change with what it holds and what it
      would hold, and how many cells hold their value already; for several rows, each field's counts as `set` says
      them. §3 said the preview names each cell: a cell that would not change is counted and not named, since a
      template writes ten cells or more. After Enter it says the rows and the template with its terms by name
      (`Set CSA-B as 2way currency=USD.`), which is also how the log names the command.
    - **Completion.** After the rows, `as` is offered beside the fields where the table has a template; after
      `as`, the templates; after a template, its terms as `new` offers a product's.
    - **`tables.describe`** answers each template: its name, title and words, the terms it takes (name, title,
      form, whether it is needed, its other names, what a choice has to choose from) and the fields it writes.
    - **Checked when the tables are read:** a template that writes a field its table has not, one no command sets
      or a figure, and two templates that answer to one word, fail by name. At a write, an answer that names a
      field the template did not declare, or leaves one out, is the registration's defect and throws.
    - *Added as built.* `as` ends the rows as `where` does, so a row whose id is `as` cannot be said alone; no
      table of the samples has one. With no rows written, `set as <template>` is read for the rows a `set` with no
      rows means: the grid's selection where the grid's table has the template, or a table of one row that has it.
      A row that would change nothing is counted, and where no row would change the line says the rows already
      hold every value.
    - *Corrected in review.* The table's answer was first named `IWorkspaceTable.Template`, which read as the
      template itself; it is `TemplateValues`, and the bullet above now says so. A template's words are read in
      one place, where its name was matched in three. Tests were added for what the bullets above say and no test
      held: a template that writes a figure, an answer with a field the template does not declare, the rows that
      already hold a template's values counted in what is said, and the log's name for a template with terms.
    - Not built: `new` making a row from a template (§1). No table of the samples makes its rows from one yet
      (the pricer's hypothetical CSA is ADR-0010 §8's, not built); it comes with the first that does.
51. **The pricer's fields beyond its grids (5b; the sample's own, every value synthetic).** The owner chose that the
    sample's trade and CSA carry every field of his examples (2026-11-11).
    - **The trade** gains its identity (requested id, trade type, the source's product type, SIMM product class,
      user-defined), its master agreement and netting set, its break (date, mandatory) and its model (model,
      pricer and interpolation ids, domestic currency, the IR model and its basis, the equity, credit and inflation
      models, the method for what is not modelled). The model's and the break's are set; the rest are the
      source's and are refused as not editable.
    - **The CSA** gains its general terms (counterparty, netting set, agreement name, enforceable, main,
      user-defined), read-only, and for each side whether it posts, its VM cap and the cap's currency, its
      independent amount, its rounding and how a delivery and a return are rounded, all set.
    - **Every field of both tables has a group**, the ones the grids already showed too: a trade's Identity,
      Terms, Agreement, Dates, Model and Valuation, a CSA's General, Terms, Counterparty and Self. B and A are
      membership and have none. A side's field is said with its side (`self posts`, `counterparty posts`, `cpty
      posts`), and `cpty` now also says the counterparty's side of the fields the grid had.
    - **They are fields of the row and no column of the page's rows.** The table reads them, so a condition,
      `set`, `reset`, `tables.read` and from 5c the dialog do; an edit of one is an edit of the row's entity as any
      other, so it is listed in Changes, undone, and carried or held back when the source is acquired again, with
      the source's value as Original. The page's row contracts are unchanged, and no grid shows them.
    - **Checked by their forms:** a flag, one of a choice's values, an amount of 0 or more, a date, an id of one
      word; a field that may be empty says so.
    - **No simulated measure reads them, and Price freezes every one** (owner, 2026-11-12: "not now; when the XVA
      analytics is connected", said of the PV legs of note 49 and read here for these fields too; and, of the
      input, note 53). FVA's rule and SA-CCR keep reading `one-way`, `exchange`, the thresholds and the MTAs. Who
      posts is carried beside `one-way` and `exchange`, not in their place: a template writes them together, an
      edit by hand can leave them saying different things, and no simulated measure reads the side's flag.
      Whether `one-way` should become what the two flags say is put to the owner with the summary of decisions
      made for him.
    - *Added as built.*
      - A word the two sides share (`posts`, `vm cap`, `rounding`) names neither alone: it is refused with the two
        it could be. `threshold` and `mta` alone stay the counterparty's, as the grid's first columns are.
      - A side's VM cap may be empty, which is no cap, and has a currency of its own; its independent amount and
        its rounding are in the side's currency. A delivery and a return are rounded `up`, `down` or `nearest`.
      - A trade's model fields choose from textbook names (an IR model of `HW1F`, `HW2F` or `LMM`, a basis that is
        `deterministic` or `stochastic`); the equity, credit and inflation models may be empty, and are for a swap
        and an FX forward. The ids are one word each.
      - The synthetic source works each value from what it holds already: a trade's identity from its id and its
        product, its agreement from the counterparty's, no break, and its product's usual model. A new trade reads
        the agreement it would join, so a counterparty change moves that and keeps what the user set on the trade:
        an edit of a new trade's break or model is the trade's own content (ADR-0003 §7), as its notional is.
      - `vary` names an editable one as it names a cell, since a cell is a field the table says a command sets,
        and the Changes tab lists an edit of one by its row and its words and takes a value typed over it.
      - A value is checked wherever it comes from: typed in a line it is read by the field's form, and one that
        arrives as a value (the API, a typed cell) is refused in the field's words (`self vm cap is an amount of 0
        or more, or empty.`).
      - The fields are listed after the grid's, so a refusal that lists a table's fields is long (a trade has
        more than thirty). Their groups say how to draw them; the order of the groups is the dialog's to say (5c).
      - The CSA grid already drew a Main column, which the client works from the agreement's main CSA. Its name
        is the field's, `main`, so with the Focus on it the line's context names the field with the row (`CSA-A ·
        main`), as it names any column that is a field. The grid draws it as before, and it is the one field of
        this note a grid shows.
    - *Corrected in review.* The source's record of a CSA holds what its fields beyond the grid read where no edit
      is set. The workspace view writes the agreement's CSAs from that record, and as first built it wrote those
      values too: the source's, beside rows a user may since have changed. They are no part of the view, and a
      test holds the view's CSA to the names it had. Each field is now declared with what the source holds in it,
      and a side's terms once for both sides, where a second list by field name had to be kept in step.
52. **The CSA's four templates (5b).** `2way` (both sides post), `1way-self` (we post alone), `1way-cpty` (the
    counterparty posts alone) and `none` (no collateral). Each takes a currency, by name or alone, and writes the
    CSA's currency and each side's, `exchange`, `one-way`, who posts, and both sides' threshold and MTA as 0. Curves,
    frequency, VM caps, independent amounts and rounding stay as they are and are changed with `set`: the market
    has no curve for every currency a CSA may be in, and none is guessed. The owner's examples give the four
    templates and that each takes a currency; what each writes beyond who posts is the sample's own.
    *Added as built:* `exchange` is on where either side posts and `one-way` where exactly one does. A template
    also answers to its words: `two-way`, `one-way self`, `one-way cpty` or `one-way counterparty`, `no
    collateral`.
53. **The accepted input carries what the user set (owner, 2026-11-12; 5b).** The owner, the same day he put off
    the measures: what matters for now is that the input to the analytics is built correctly and as expected. A
    value a user can set is therefore in what Price freezes exactly as set, whether or not a simulated measure
    reads it; a value left out because nothing reads it yet is the defect the real analytics would meet.
    - **Each trade of a case carries its `legs` and its `independentAmounts`**, as the reading of that case holds them:
      a leg as its id, currency, PV and notional, the notional `null` where it holds none (the leg an overwritten PV
      leaves); an independent amount as its id, currency, start, end and value. A trade with none carries an empty list,
      never no list, so that "it has no leg" is said and not left to be guessed. A PV overwritten is the one leg the
      write left (note 49), in the currency written.
    - **The PV they add up to is not carried.** It is a figure of the legs in the display currency, which the
      input names; a second copy could only disagree with them.
    - **Each case carries its `csas`**: every CSA a trade of the case uses, in the CSA table's order, with the
      terms the page shows and, under `details`, the fields of note 51 by name. Before this note a CSA's terms
      reached the input only in SA-CCR's part, and only where a capital measure was selected.
    - **Each trade carries, under `details`, the fields of note 51 by name**, those the source alone sets among
      them. An empty one is carried as empty (`null`), not left out.
    - **A leg and an independent amount of a priced trade are priced entities**: an unresolved conflict or an
      unacknowledged lost edit on one refuses Price, as one on the trade does.
    - **What the simulated measures read stays as it was** (note 49). The input so holds an existing trade's
      official PV and its legs, and where the PV was overwritten the two differ. Which of them the real request
      reads is decided when the analytics is connected (ADR-0010, ADR-0013).
    - *Corrected in review.* A trade under no CSA carries the agreement's `nocsa_{ma id}` as its `csa` (docs/03
      §2). That names no CSA, so the case's `csas` has no entry for it, and an uncollateralised counterparty's
      `csas` is empty: the trades' `csa` says why. As first built this was so and was not said, and any id that
      was no CSA of the agreement was passed over in the same way. Such an id cannot reach the input (the trade
      has an issue, which refuses Price); were one to, building the input throws. The tests of the input held
      what the code computes in two places; they hold literals now, with a case other than the Base, a CSA no
      priced trade is under, and an uncollateralised counterparty.
54. **A draft is its lines (§10; 5c).** §10 says a draft is view state: the row, the revision it was opened at
    and its changes. Its changes are held as lines.
    - **A line of a draft is a whole line of the workbench that names the row** (`set E00003 notional 5bn`, `new
      leg E00003 eur 250,000`, `remove legs E00003/1`), as the log would name the command. A draft so needs no
      reader of its own: what reads the line typed at the workbench reads it in a draft, and a draft can be read
      again by anything that holds its lines.
    - **The server reads the lines in order and stores nothing.** Each line's changes are worked on the state
      the lines before it left, which is written nowhere (as a batch checks its later changes against its
      earlier ones, note 9). A command says the changes its line would write only where it can be part of a
      draft: `set`, `reset`, `new` and `remove`.
    - **The form** is what the page draws a dialog from: the row's fields in the table's order, each with its
      group, its value as written and whether the draft changed it; each table under the row with its rows as
      the draft leaves them, a row the draft made and one it removed each marked; the table's templates; each
      line with the sentence it was accepted with; and Original, the row as the source alone gives it.
    - **A line that no longer reads** is answered with its refusal and the form of the lines before it. The lines
      after it are not read: each was accepted on what the ones before it left.
    - **A draft carries its revision.** One the workspace has left is refused, saying the revision the workspace
      is at and the commands that moved it, so nothing typed before is silently read on other rows.
    - **A draft is of the workspace's own rows**, the layer every grid shows. §10's "a dialog shows one layer" is
      kept by its two tabs (the owner's answer of 2026-11-11): Original, read-only, and Live, the draft.
    - *Added as built (2026-11-12).* **A field says how it is typed**: with its value the form gives each field the
      word a line says it by, its form, what it takes in words, its choices, its unit, and whether a command sets it
      and whether it may be empty, so that a page draws an input for it knowing no table. A template says the terms it
      takes the same way.
    - *Added as built.* **A row under the row whose field a line changed is marked `changed`**, beside `made` and
      `removed`. A row made and then removed within the draft is in neither state and is not listed.
    - *Added as built.* **Original is the row on the workspace with nothing of the user's on it**: every edit,
      conflict, lost edit and made row taken out and what is derived worked again. A row the workspace made has no
      Original, and the form says none.
    - *Added as built.* **The host's door** is `POST /api/workspaces/{id}/draft`, whose body is the draft. A draft the
      workspace has left is refused as `RevisionMoved` (409), and a table or a row the workspace has not as
      `UnknownTable` or `UnknownRow` (404).
    - *Added in review (2026-11-12).* **A field says whether it holds a number** (`number`), from the one list of forms
      the selectors read as numbers, so a page draws a number against the right edge and knows no form. The dialog had a
      list of its own.
55. **The line acts on a draft (§10; 5c).** The view a preview is asked with carries the draft.
    - **`set`, `reset`, `new` and `remove` are read for the draft's row** on the state its lines leave. `set
      notional 5bn` names no row and means the draft's; `new leg eur 250,000` makes a row under it; `remove leg 1`
      takes the row under it of that number. A line that names another row is refused: a draft is of one row.
    - **Such a line is answered as pending**: the whole line the draft gains and what it would change. It has no
      command to invoke, and Enter adds it to the draft.
    - **`apply` is one command.** It reads the lines again at the draft's revision and writes every change as
      one batch, which the log names by the row and the count of lines and one `undo` takes back whole. It is
      refused where the workspace moved, as the form is.
    - **`cancel`** is a verb of the screen: the page discards the draft.
    - **Every other command is refused while the draft is open**, saying whose draft it is and that `apply` or
      `cancel` ends it; `price` is among them (Price takes committed state only). The verbs of the screen that
      change nothing (`find`, `sort`, `filter`, `select`, `copy`, `export`) stay.
    - **The line says the draft it is on**: the row and how many lines are pending.
    - *Changed as built (2026-11-12).* **The verbs of a grid are refused in a draft too** (`find`, `sort`, `filter`,
      `select`, `copy`, `export`), where the bullet above let them stay. A dialog has no grid for them to act on, and
      `copy` would take out what the draft has yet to write as if the workspace held it. `cancel` is the one verb of
      the screen a draft reads.
    - *Added as built.* **With a draft in the view the server reads the line with the draft's row as the screen**,
      whatever else the view says: the row is the one row selected, on the state the draft's lines leave. A reading is
      of the revision the draft was opened at.
    - *Added as built.* **What a reading asks of the page**: a pending line is the ask `draft` (the whole line and its
      sentence), `cancel` the ask `cancel`, and `open` (note 56) the ask `open` (the table and the row).
    - *Added as built.* **In a draft a table under the draft's table has the rows under the draft's row alone**, for a
      line and for what a line offers, so `remove legs 1` is the draft's own leg though another trade has a leg 1. A
      field named alone is the draft's row's even where a table of one row has the word: nothing is ambiguous.
    - *Added as built.* **What a draft does not gain** is refused by name: a line of another row (`NotTheDraftsRow`),
      one that changes nothing (`NothingToAdd`), and one that takes the draft's own row away (`RemovesTheDraftsRow`).
      Every command that is not the draft's is `DraftOpen`.
    - *Added as built.* **`apply` is the capability `draft.apply`**, whose request holds the draft whole (its table,
      row, revision and lines), since the server stores none. The log names it `apply <row> (<n> lines)`. It is
      refused where the workspace moved (`RevisionMoved`), where a line no longer reads (`DraftUnread`, with the
      line's number and its refusal) and where no line changes anything (`NothingToApply`); typed with no draft open,
      `apply` and `cancel` are `NoDraft`.
    - *Added as built.* **The catalog lists what a draft reads** (`draftCommands`, by id), from the same mark the
      refusal is made of, so that a line lists every other command as unavailable while a draft is open (§10).
    - *Corrected in review (2026-11-12).* **`NothingToApply` is a draft of no line**, not one where "no line changes
      anything" as the bullet above says. A line that changes nothing is refused as it is typed (`NothingToAdd`), so a
      draft holding one by `apply`'s time has a line that no longer reads: `apply` answers `DraftUnread`, naming the
      line by its number with its refusal, as the form does. The request's contract asks for at least one line.
    - *Added in review.* **`draft.apply` is among the AI's tools**, as every command is (ADR-0007): the AI sends the
      row, the revision and the lines whole, and they are read as a dialog's are. It opens no dialog and holds no draft.
      Its contract is held by the test of the tools' contracts. Listed for the owner, who may take it off the tools.
    - *Added in review.* **Nothing else runs on the page while a dialog is open** (§10: "the rest are listed
      unavailable"), by whatever way it is asked. A command run by its id, as a grid's undo key pressed in the dialog or
      a button of the page is, is refused in its turn with the sentence of `DraftOpen`: before, `Ctrl+Z` in the dialog's
      grid took back the workspace's last command under the draft. A line taken on the page before the dialog opened,
      and read after, is refused the same way (spine 6); the one that asks for the dialog that is open, as a second
      press of the row's button does, has nothing left to do and says nothing. The shell's own commands (the panels',
      the theme, the workflows, the applications') are listed as unavailable with the same sentence, and a chord is
      asked again at its press, so `next panel` cannot unmount the page under a draft. A short form stays listed: the
      server reads it as the line it stands for, and says at Enter.
56. **`open <row>` is read by the server, beside the shell's `open <tab>` (§3, §10; 5c; DEFAULT).** §3 lists
    `open <tab, view or row>` among the verbs of the client, and note 30 gave the shell one command for each tab.
    - **`open <row>` is a verb of the screen that the server reads**, as it reads `find` and `select`: the row is
      said as any command's rows are (an id, the end of one, its table where two tables have it), and with no
      word it is the row the keyboard is on. It answers the page the one row whose dialog to open and the
      revision it was read at, and invokes nothing. A line that names no row, or several, is refused: a dialog
      is of one row.
    - **`open <tab>` stays the shell's.** A line that is a tab's words is the tab's (`open csa`), and one that
      is not is the server's to read (`open csa-b`). While what is typed still begins a command of the shell's,
      the line lists those, so a tab's word half typed offers the tabs as it did; the same rule lets `new x`
      offer `new xva`, which the server's `new` had taken from the list since step 3.
    - **One draft is open at a time**: with a draft open, `open` is refused as every command but the draft's is
      (note 55).
    - **A row's button runs the same line** (`open <table> <key>`, ticket 16), so the button, the keys and the
      line are one command (§10).
    - *Corrected as built (2026-11-12).* **The rule is for a served command that needs no argument**, as `open` needs
      no row: while what follows its word could still become a command of the shell's (`open `, `open cs`), the line
      lists those with `open` first, which Enter runs for the row the keyboard is on. A served command that needs an
      argument (`set`, `new`) reads whatever follows its word, as it has since step 1: the server completes it, and
      listing the shell's `set currency` at `set c` would hide the fields it offers there. So `new x` does not offer
      `new xva`, against what the bullet above says; `new xva` typed whole is the shell's, as before.
    - *Corrected in review (2026-11-12).* **With no word `open` is of the row the screen gives**, as every command's
      rows are (note 16): the one row selected, else the row the keyboard is on. The first bullet says the keyboard's
      row alone. Several rows selected are refused, naming them, and a screen that gives none is refused too: a dialog
      is of one row.
57. **The dialog is drawn from the form (§10; 5c; DEFAULT).** The page holds the draft and draws one dialog for it,
    the same for every table; nothing in it knows a table or a field.
    - **A modal, the whole screen on a phone.** Its title is the row, and under it the line's own sentence of the
      draft ("Editing E00003: 2 lines pending."). Tabs Live and Original (the owner's answer of 2026-11-11): Original
      is read-only, and is not offered for a row the workspace made.
    - **A group is a section**, in the order the groups first appear among the table's fields; the fields of no group
      come last, under "Other". A field is drawn by its form: a choice as a select of its choices, a flag as a switch,
      and every other as text typed as a line types it (`5bn`, `2027-03-31`), with what it takes said under it. A
      field no command sets is drawn as text.
    - **An edit is a line.** Leaving a field whose text changed, picking a choice or turning a flag asks the server to
      read `set <field> <text>` in the draft. Accepted, the draft gains the line the server answered; refused, the
      field keeps what was typed and says the server's sentence, and the draft gains nothing. An emptied field that
      may be empty is `set <field> empty`. The dialog has no rule of its own.
    - **A table under the row is a grid** (ExGrid), its fields the columns. A cell typed into is `set <table> <row>
      <field> <text>`, a row's Remove button `remove <table> <row>`, and under the grid each product that makes a row
      there (`new leg`) is a button that asks for its terms, the row above left out: the form lists them, as it lists
      a template's. A row made, changed or removed is marked, and a removed row has no button.
    - **A template is a button** that asks for its terms and adds `set as <template> name=value …`.
    - **The pending lines are listed**, each with the sentence it was accepted with, and each can be taken back. The
      lines after one taken back are read again on what is left, and one that no longer reads is said with its
      refusal.
    - **Save is `apply` and Cancel is `cancel`**, run as the line runs them. Save with nothing pending is not offered.
      Cancel, Escape and the close button ask before discarding pending lines. While the dialog is open the line is
      typed over it (Ctrl/⌘+K), and Escape there closes the line alone.
    - **A draft the workspace has left** is said with the server's sentence, and the dialog offers to read the same
      lines again on the workspace as it now is, or to cancel. Nothing is read again unasked.
    - **A page may say one sentence above the form**, such as the pricer's that the simulated measures do not read the
      legs yet.
    - *Changed as built (2026-11-12).* **The title is the row and how many lines are pending** ("2 pending"), not the
      line's sentence of the draft under it: the count is what changes as the dialog is used.
    - *Changed as built.* **What a field takes is its input's placeholder and its tip**, not text under it: a dialog of
      forty fields reads better without forty hints. A field that may be empty says `empty` there.
    - *Changed as built.* **The Cancel button, Escape and the close button discard the draft on the page and ask the
      server nothing**, after asking the user where lines are pending: a draft is view state (§10), and one the
      workspace has left must still close. `cancel` typed on the line is read by the server as before, and is read on
      the workspace as it stands, so it closes a draft the workspace has left and one with a line that no longer reads
      (note 55 had every reading be of the draft's revision).
    - *Changed as built.* **A removed row keeps its Remove button**, where the bullet above gave it none: ExGrid's
      Action Column draws the same buttons on every row. Pressed again, the server's refusal is said under the grid; the
      removal is taken back among the pending lines.
    - *Added as built.* **A table under the row is drawn without the field that names the row above** (a leg's trade):
      every row listed has the draft's row there, so the form leaves the field out.
    - *Added as built.* **A plain number is drawn with its digits grouped** (`3,600,000,000`), in a field and in a cell,
      as every grid of the workbench draws one. A line reads the grouped number as it reads the plain one, so what is
      drawn is still what a line writes. Every other value is drawn as the form writes it (`5bn`, `0.448%`), and a date
      is typed as text (`2027-03-31`): no date picker is drawn yet.
    - *Added as built.* **A line is sent once, and read on the draft as it is.** A field the keyboard leaves before the
      form of its line is drawn does not send the line again. A line typed for a draft is read on the draft as its turn
      finds it, and a reading of a draft that changed meanwhile does nothing and says so (spine 6).
    - *Added as built.* **The line is typed over the dialog**: it is drawn above the dialog, says the draft it is on,
      and lists every command a draft does not read as unavailable, naming the dialog that is open (note 55's
      `draftCommands`). Escape pressed in the line closes the line alone.
    - *Added as built.* **A row's button** (ticket 16): the trade grids and the CSA grid have a column of one button,
      Edit, beside the row's key, with no header. It asks the page's door to read `open <key>` in the view of the grid's
      table, where note 56 wrote `open <table> <key>`: the view says the table, so the page needs no table's word. The
      press, Space on the button's cell and the line are so one command, and the dialog a button opens raises no notice.
      A key that is not one word is refused on the page, never sent to be read as something else. At 390px the column
      moves `Ccy` out of the trade grids' first view (Trade, Edit and Product stay, and the grid scrolls to the rest):
      accepted for a button in view at every width.
    - *Corrected in review (2026-11-12).* **A control sends a text once**, where the bullet above had the dialog send a
      line once: that rule took a second `new leg` of the same terms, pressed before the form of the first was drawn,
      for the first, and closed its terms as if a row had been added. What a field sent stands for it until the form
      shows what the draft made of it or the server refuses it, so leaving the field meanwhile sends nothing; the same
      line from another press is a line of its own. A text typed on while the field's line is read is the user's: it is
      sent after the first, and the form arriving does not write over it. A switch and a list show what they sent while
      it is read, so a second press is of what the first left.
    - *Corrected in review.* **The list of pending lines takes a press only as the list of the draft as it is.** While
      the form of an earlier draft is still drawn, or Save is being read, no line is taken back: the line pressed may
      not be the one at its place. A press carries the draft it was made on, and the page takes nothing back from a
      draft that has changed since (spine 6). Before, a press on the list of an earlier form took back another line.
    - *Corrected in review.* **The question before discarding is of one draft.** It is gone once that draft gains or
      loses a line, and with the dialog: it stayed for the next row's dialog before ("Discard the 0 pending lines?"). It
      is asked in every state of the dialog, one whose form could not be read among them, where Discard discarded
      unasked before. A form that could not be read for another reason than a workspace that has moved offers to ask for
      it again.
    - *Added in review.* **Save waits for the lines still being read, and writes nothing where one of them was
      refused**: a text typed and then Save pressed sent its line and `apply` together, and a refusal of the line went
      unseen behind a dialog that closed. While Save is read the form takes nothing.
    - *Added in review.* **The keyboard stays in the dialog and goes back to the grid.** A line taken back leaves the
      keyboard on the line that took its place, or on Cancel once none is left; the question answered with no leaves it
      on Cancel: before, it fell to the page and Escape was no longer heard. Escape pressed in a grid of the dialog is
      the grid's first, and the one with nothing of the grid's left to dismiss is handed on to the dialog (ex-grid
      ADR-0070). §10's "returns to the row" is, as built, this: once the dialog has closed, written or discarded, the
      grid the page had on screen takes the keyboard back at its Focus, where nothing else took it meanwhile. That is
      the row where its button was pressed with the keyboard in the grid. After a dialog the line opened for another
      row, the Focus is where it was: moving it to the row asks the server for a place, and is not built.
    - *Corrected in review.* **A table under the row is drawn without the one field that names the row above.** Where a
      table has several fields that point into the table above, none is left out: which of them names the row above is
      not the form's to guess. The bullet above left out every such field.
    - *Added in review.* **A value the form does not list among a field's choices is still drawn**, as what the row
      holds: a list that drew nothing would say the field is empty. A flag is read by the one reader of flags, and a
      value that is neither on nor off is said, never drawn as off.
    - *Added in review.* **A table's column widths in the dialog are kept** as every grid's are (ADR-0017 §5), by the
      table, whichever row's dialog shows it.
    - *Corrected in review.* **The row's button is not in view at every width**, as the bullet above says: its column is
      the second, beside the pinned key, and scrolls away with the others. It is in the first view at every width. At
      1280px it also moves the counterparty threshold out of the CSA grid's first view. Ticket 16's criterion ("costs no
      needed column at 390px") is so not met as written, and the ticket is left for the owner.
    - *Left as they are in review.* The two `catch` blocks of the door that say an error alike (two kinds of error with
      no base of their own); the dialog's own reading of the phone width beside the shell's (the shell's is in
      `App.tsx`, which the panels' work is changing); and the older browser tests that find a cell by its number, which
      the new ones find by the column's header.

58. **The pricer's scenarios are a table, with their shifts under them (§1; 5d; DEFAULT, 2026-11-12).** Note 32 left
    the table for this step and named what the kernel's tables did not hold. What 5d decides of each:
    - *A scenario's key is one word.* The selector names a row by an id of one word (note 7), and a name may be
      several (`jpy_ois -50bp · usdjpy -10%`). A scenario's key is its name where that is one word, and else its
      name with each run of spaces, and each comma, written as one `_` (`parallel up` is `parallel_up`). Two names
      that give one key are told apart in the scenarios' order: the second is `<key>_2`, and so on, so no key is
      held twice. The name is a field of the row, read-only. A quoted id (`"parallel up"`) was weighed and left:
      the selector, the lists of a `where`, the completions and the client's reading of a line would each learn
      quotes for one table. *For the owner to judge.*
    - *`xva.scenarios`* (`scenarios`): a row a scenario, in the order of their first shifts. Its fields: `scenario`
      (the key), `name`, `shifts` (how many it holds), `included` (whether Price prices it; a flag a command sets),
      and `issue` (why Price cannot price it, empty where it can). `set scenarios parallel_up included off` is
      `scenario exclude parallel up`; `remove scenarios parallel_up` is `scenario remove parallel up`.
    - *`xva.shifts`* (`shifts`), under the scenarios: a row a shift, keyed by its id (`s12`), under the scenario its
      name gives; a shift that names no scenario is under none. Its fields: `shift` (the key), `scenario` (the key
      of the row above, read-only), `category` and `type` (read-only: they follow the index and the size), `index`
      and `factor` (a choice each: the catalogue's indices; a tenor, `all` or `spot`), `size`, `description`, and
      `issue`. A write is the grid's own edit of that cell (`PortfolioScenarios.Edit`), with its checks and its
      refusals, which name the grid's row and column: one reader of a shift's cell, whichever door it came through.
      `remove shifts s12` clears the row, which removes it; the last shift of a scenario removed, the scenario is
      gone and is left out of nothing.
    - *A size is written with its unit* (`+10bp`, `-5%`): the unit says how the index shifts (ADR-0010 §4), so a
      form that turns one unit into the other (`Rate`, `Spread`) would take `1%` on a curve as 100bp where the
      grid refuses it. The field's form is `Money` over `bp` and `%`: its amount and what it is in.
    - *What the kernel gains, each for any table.* **A number's unit may be written against it** (`10bp`, `-5%`,
      `350k usd` as before): `Money` reads a word that ends with one of the field's units as the number before it
      and that unit, where the word is no number as it stands, and reads a `+` before the number. **Rows written
      together**: a table may answer the write of a field on several rows, and the removal of several rows, as one
      (`SetRows`, `RemoveRows`), where its rows share what holds the value. The scenarios left out of Price are one
      list of the workspace, so two rows' changes worked out on the same state would each write the list without
      the other's name, and the second would win. A table that answers neither is written a row at a time, as
      before.
    - *Not in 5d.* Renaming a scenario and moving a shift to another scenario (`scenario` of a shift is read-only):
      the grid's cell and `scenario duplicate` do that. No product makes a shift (`new shift`): `scenario <name>
      <shifts>` and the grid's blank row do. The Scenarios tab is as it was, and its grid is no view of the table:
      `find`, `sort`, `filter` and `select` do not act on it yet.
    - *The ten verbs of note 32 stay*, each reading its own line and making the edits it made.
    - *Changed as built (2026-11-12).*
      - *The key.* Every run of characters a typed id does not hold is written as one `_`, not spaces and commas
        alone: a name may hold a `·`, a stroke or a bracket, and each would part the id or be read as something
        else. A typed id holds letters, digits and `_ + - % .`, the signs a shift is written with, so
        `jpy_ois -50bp · usdjpy -10%` is `jpy_ois_-50bp_usdjpy_-10%`. A key that is a word that says rows (`all`,
        `selected`, `shown`, `where`, `as`) gains a `_` (`all_`), since the selector reads the word as itself. Two
        keys are one whatever their case.
      - *A shift's key is its scenario's key, a stroke, and the number of its row's id* (`up/12`, and `/12` for a
        shift that names no scenario), not the id alone (`s12`). The id alone says nothing of where the shift is,
        and `s1` is a name a scenario may have. The number is the row's own: it does not move when another shift
        is removed, so a key read before a removal still names its row, and the numbers of one scenario need not
        begin at 1. *For the owner to judge.*
      - *The fields' names.* A scenario's are `scenarioId` (said `id`), `name`, `count` (titled Shifts: `shifts` is
        the word of the table under it), `included` and `issue`. A shift's are `shiftId` (said `id`), `scenario`,
        `category`, `index`, `factor` (said `factor`, `risk factor` or `tenor`), `type`, `size`, `description` and
        `issue`, in the order of the grid's columns.
      - *A size without its unit is refused* (`InvalidShift`), with the size as the index takes it (`write
        +15bp`): `Money` reads a bare number, and the grid's cell would then read it by the index alone.
      - *A refusal names the shift by its key* where a command named it so (`up/4, Size: an FX shift is relative,
        in %: write +3%.`), and by the grid's row where the grid's cell did (`Row 2, Size: …`), as before. One
        reader of the cell still, and each door told in its own words.
      - *`reset scenarios <key>` is refused* (`NotResettable`): a scenario is the workspace's own and has nothing to
        go back to, and the refusal says the line that prices it or leaves it out. A shift is reset to what it was
        made as.
      - *What the kernel gains beside the two above, each for any table.* A condition compares a `Money` field with
        its unit said (`where size > 5bp`): a number alone is refused, since it would be compared with values in
        every unit, and a row whose value is in another unit is no match. `tables.describe` lists a `Money`
        field's `units`. A draft's form offers no list of values for a `Money` field: what it is in is no value
        of it.
      - *A scenario opens as any row does* (notes 56, 57): `open up` draws its fields and its shifts under it, and a
        shift's cell typed there is a line of the draft. With nine fields the grid of shifts is wider than the
        dialog, so its Draft mark and its Remove are reached by scrolling the grid or by the keyboard. Where a
        row's buttons stand is ticket 16's question, which is the owner's.
    - *Corrected in review (2026-11-12).*
      - *An id as written is read before the end of one, above as here.* A scenario may be named with digits alone
        (`4`), and a shift's key ends with digits (`up/4`). `shifts 4` was read as the shift whose key ends with
        `4`, where note 46 has it the shifts of the scenario `4`. For any table under another, the kernel now reads
        an id as a row's whole key, then as the whole key of a row above (that row's rows), then as the end of a
        row's key, then as the end of a row above's: note 7's order, a table further.
      - *`all` is no risk factor of an FX shift.* The cell took `all` for an FX shift and wrote nothing of it. So
        `set shifts up factor all` counted the FX shift among the rows it wrote, and on that shift alone was read
        as a change and then refused as none. The cell now refuses it as it refuses a tenor there (`usdjpy is a
        spot rate and has no tenor: leave it spot`), at the grid's door as at the command's. A line that names
        curves and FX rates together is refused whole; `where category = IR` says the curves.
      - *A condition with `!=`.* "A row whose value is in another unit is no match" holds of an order (`<`, `>`),
        which is among the values in the unit asked. Equal is the same number in the same unit, so `!=` holds of
        a value in another unit, as the kernel's tests have had it.
    - *Left as they are in review.*
      - *A key with `_2` follows the scenarios' order.* Once the earlier scenario of that key is removed or
        renamed, the later one takes the key without the number, and its shifts' keys change with it. A key typed
        from memory after that names the other scenario; a line names what its reading says as it is typed. Keys
        that kept their number would have the workspace hold them. *For the owner to judge*, with the key's
        spelling.
      - `ScenarioTable` and `ShiftTable` override most of what the pricer's `Table` gives a table, since their
        writes are the Scenarios grid's own edits; `PortfolioScenarios.Keyed` answers its two lists as a pair.

59. **`load trades <ids>` (§3; 5d; DEFAULT, 2026-11-12).** The owner chose that it reads a synthetic store of booked
    trades by id (2026-11-11). §3 says it brings trades that exist into the New grid and computes nothing.
    - *The store* (`BookedTrades`) holds forty synthetic trades, `B00001` to `B00040`, each with the terms the
      source gives an existing trade: its start and maturity, its rate, schedule and last fixing, its official PV,
      its PV legs and its independent amounts. It is deterministic by id and value date; a trade past its maturity
      on the value date is not in it. *Changed as built:* the spec says the store is "of the counterparty". It is
      of no counterparty: the counterparty's booked trades are the Existing grid already, and a trade brought into
      the New grid is one the netting set does not hold.
    - *What the workspace holds is that it loaded the trade; the terms stay the store's.* A load makes one entity
      per trade (`loaded:<id>`, in the trade's own scope, so a counterparty change keeps it, as it keeps a draft).
      The trade's terms, its legs and its independent amounts are read from the store on the workspace's value
      date, as an existing trade's are read from the source: a value date moved, or a `refresh`, reads them again,
      and an edit of the trade carries or is held as any edit is (ADR-0003 §7). A loaded trade past its maturity
      on the value date is in no grid, as a matured existing trade is in none; the Changes tab still lists that it
      was loaded, and says that the store has not got it on that date.
      *Weighed and left: the terms kept as they were loaded* (note 37 expected "a trade the workspace holds with
      its terms"). The workspace would then price an official PV, legs and a last fixing of the day of the load on
      another value date and say nothing of it; and every field a command may set, a module's column among them,
      would be copied at the load, so a column registered later could not be set on the trade. *For the owner to
      judge.*
    - *A loaded trade is a new trade.* It is in the New grid, out of Before and in After. Its CSA is the main CSA
      and its IM CSAs the counterparty's, as a draft's are: the CSA it was booked under is not carried, since which
      CSA it would join is what the user is asking. Every field is set, reset and varied as any trade's; its legs
      and independent amounts are taken out and brought back as the source's are. The synthetic measures price it
      as they price a draft: none reads its official PV or its legs yet (ADR-0010 §6; the owner, 2026-11-12).
    - *The line.* `load trades B00012, B00013`: ids separated by commas or spaces, whatever their case. Its reading
      says the trades it would load with each one's product, currency and notional. One command, one `undo`.
      Refused, with nothing loaded: no id; an id said twice; an id the store has not on the value date
      (`UnknownTrade`, every such id named); and a trade the workspace has already loaded (`TradeHeld`). The line
      offers the store's ids that begin with what is typed, without those the line or the workspace already has.
    - *`remove trades B00012` removes a loaded trade*: the entity that says it was loaded, every edit of the trade,
      of its legs and of its independent amounts, and the legs made under it, as one command that one `undo`
      takes back. It is refused while a case holds a change of the trade or an iteration of `add each` names it
      (the case is removed first), and while an edit of it is held as a conflict (the user decides it first). A
      trade the source gave is still refused, as before. The Changes tab lists a loaded trade where it was loaded,
      and a reset there removes it as the command does.
    - *An edit names a trade the workspace holds.* The source answers for every trade of the store, loaded or not,
      so that the kernel reads an edit of a loaded trade against it as it reads any edit. The pricer's checks
      therefore refuse a value written on a trade, a leg or an independent amount whose trade the workspace does
      not hold (`UnknownTarget`), whichever door it came through.
    - *Price freezes it as any trade* (note 53): the accepted input carries a loaded trade in After with its terms,
      its legs, its independent amounts and its fields beyond the grids, and names the trades that were loaded
      (`loadedTrades`), since the source version it also names is the counterparty's.
    - *A capability, `xva.trades.load`*, with the workspace's revision as every command of the pricer has it; it
      is among the AI's tools, as every command is.
    - *Not built:* loading from a file (§3 names it; the owner chose the store), and a search of the store by
      anything but an id's first characters.
    - *Added as built (2026-11-12).*
      - *The store's trades run five years longer than an existing trade's.* Generated as an existing trade is,
        eleven of the forty were past their maturity on the first value date a workspace opens with, the two this
        note's example names among them. With five years more all forty are in the store on 2026-10-26.
      - *The refusals' codes.* No id is `NothingToLoad`, with a line of the store's first two ids to type; an id
        said twice is `GivenTwice`. `remove trades` of a loaded trade is `TradeInCase` while a case holds a change
        of it, naming the case or, with several, how many and the first of them, and `ConflictUnresolved` while an
        edit of it waits as a conflict.
      - *The line lists twenty ids at a time*, and says how many more there are, as every list of rows does.
      - *The page's source version is the counterparty's still.* What names a grid's rows for a selection (note 26)
        is that version and the ids loaded after it, so rows chosen before a load or a removal are not acted on
        after it; the version the page shows, and Price freezes as `sourceVersion`, does not change with a load.
      - *A Changes tab's row for a loaded trade* says `loaded` against `not in the workspace`, under Common, and
        counts as one edit in the workspace's count of edits.
    - *Corrected in review (2026-11-12).*
      - *A loaded trade's dialog has its Original*: the store's terms, legs and independent amounts, as an existing
        trade's has the source's. The state with no edit (note 54) dropped every entity the workspace held, the
        one that says a trade was loaded among them, so the dialog said the workspace had made the row. A module's
        rules now say which entities bring in a row a source has (`IEditRules.Brings`; none, for most modules),
        and the state with no edit keeps those.
      - *The Existing grid's order does not move with a load.* A loaded trade is in the New grid alone, so the
        token of the Existing grid's order names the acquired version alone, and rows chosen there are still
        acted on after a load.
      - *Every id the store has not is named in one refusal.* Where a line named an id of no trade of the store
        and a trade past its maturity, the refusal named the first alone.
      - *The accepted input names the store's version* beside the ids (`loadedTradesVersion`, `booked@<value
        date>#<trades it has then>`): the terms it froze were read from the store as of the value date, and the
        source version says nothing of the store. Where no trade is loaded it is null.
      - *Tests added:* a loaded trade past its maturity is not among `loadedTrades`; `redo` of a load and of a
        removal; the Changes tab's removal of a loaded trade past its maturity, with the edit it lost its place
        for.
      - *From the standards review:* the source's views with loaded trades are remembered under a lock; a loaded
        trade's terms as a line says them (`Terms`) and the entities that say a trade was loaded (`Markers`) are
        each read in one place; what names a grid's rows is `RowsVersion`, beside `Version`.
    - *Left as they are in review.* No test reaches `ConflictUnresolved` on `remove trades`: no value of a store's
      trade that a command sets moves with the value date, so the synthetic store makes no conflict. A reader that
      cast a workspace's source to the pricer's own type would read it without its loaded trades; every reader
      goes through `PortfolioSourceVersion.Of`, and review alone holds that. The store's indices are built in two
      places, and two readers gather a trade's edits from the three places an edit may be held.

### Notes of 2026-11-12: what step 6 settles (all DEFAULT)

Step 6 is one pull request (`docs/specs/commands-6-saved-sequences`). §8 leaves a saved sequence's shape a DEFAULT
"until its spec is written"; these notes are that shape. The owner asked for the step to be built without a question
(2026-11-11), so each choice here is reported, and the ones to judge are marked.

60. **What a saved sequence is (§8).** A saved item (C32, ADR-0010 §4), kept by the kernel's store of saved items as
    a scenario set is: its owner's alone unless shared with the owner's desk or with all, replaced by its owner
    saving the name again, and named `<name>` or `<name>@<owner>`.
    - *It holds lines.* Each line is a command as a person types it, kept as written, in order: no row a selector
      matched, no revision, no workspace and no counterparty, so that it serves another counterparty's workspace
      (§8). Beside each line it keeps what the line's first words named when it was saved, a command's id or a
      short form's, and nothing of the command's inputs: the server reads the line again each time it runs (§5).
    - *It is of one application.* A command is of an application's workspaces (note 27), so a sequence saved on a
      pricer's workspace is listed and run on the pricer's workspaces alone (its kind is `sequence:<app>`), and
      the same name may be another application's sequence.
    - *One to fifty lines.* A sequence runs in one call, so the cap is on what is executed (`TooManyLines`).
    - *Not built:* taking a saved sequence away (the store of saved items removes nothing, a scenario set
      neither: saving the name again for oneself alone withdraws it from a desk); values given when it runs
      (`sequence run usd-only ccy=EUR`); a sequence made from the commands the log holds, or recorded while they
      are typed; a dialog to write one in. Each is on the status document's list of what may come later.
    - *Added as built:* a kept sequence that holds no line, or holds its lines otherwise than a save writes
      them, is a defect of the store and thrown as one: it is not read as a sequence of fewer lines.

61. **What a line of a sequence may be (§8, §9).** One rule, read when the sequence is saved and again as each
    line is reached when it runs:
    - its first words name a command of the workspace's application, or a short form (note 29) whose line does;
    - the command edits the workspace (`EditWorkspace`), or, as the last line alone, starts work on it at the
      revision the lines before it left (`Execute`: `price`). §8 says a sequence "may end by submitting a
      workflow at the revision it left", and this reads it as written. *For the owner to judge:* `price` between
      two edits would make two Runs of one sequence, each of its own revision, and is refused today
      (`ExecutesBeforeTheEnd`).
    - Refused by name (`NotInASequence`): a verb of the screen, and `selected` and `shown`, since a sequence has
      no screen (§9); `undo` and `redo`, which act on whatever command is the log's latest when they run;
      `apply`, which writes a dialog's draft; a `sequence` verb, so no sequence runs another; and a command that
      neither edits nor starts work, such as `scenario save` and `live start`.
    - *A line that names a command and is refused as the workspace stands does not refuse the save.* What it
      names may be made by a line before it (`new irs …` then `set DRAFT-1 …`), and a sequence is saved on one
      counterparty to serve another. The save's reading says which lines are refused where it stands and why, and
      that each is read again when it runs. A line that begins with no command's words, or breaks the rule above,
      refuses the save, with its number.
    - *Changed as built:* `selected` and `shown` are refused by the reader's own code (`NoView`), with the
      line's number before its sentence; the others above are `NotInASequence`. A save of no line is `NoLines`,
      an empty line `EmptyLine`, and a line that begins with no command's words `NotACommand`.
    - *Added as built:* a short form is held to the rule by the line it stands for, once that is read. Where its
      line is refused as the workspace stands it is kept, as any line is, and held to the rule when it runs.
    - *Added as built:* a line is read with no screen behind it, on the workspace as it is given: the save's
      reading, as a person types it, reads the lines on the workspace the page shows.

62. **`sequence.run` runs each line as its own command (§8, §11).**
    - *Each line is read, then invoked through the gateway.* The server's reader reads the line for the caller at
      the revision the line before it left (the first at the revision the request names), and the command it
      names is invoked through the command gateway with the inputs that reading gave. So each line is a command
      of its own in the log, with its own words, and `undo` takes them back one at a time, the last first.
    - *A workspace another hand moved stops the run.* A line is read at the revision the line before left, and a
      workspace that is elsewhere refuses the reading (`RevisionMoved`), as it refuses any line read too late.
    - *Origin.* The lines run with origin `workflow` (§8). `undo` says of such a command "a saved sequence for
      you", as it says "the AI for you". *A sequence the AI runs keeps origin `ai`* for its lines: the log would
      otherwise say that no AI wrote what the AI ran. *For the owner to judge.*
    - *It stops at the first refusal.* A refusal of the first line refuses the command, with that line's code
      and nothing run. A later one ends the run: the lines before it stay written, and the answer says the line
      it stopped at, why, the revision the run began at and the one it left the workspace at. That answer is
      done, not refused: a refusal is the gateway's word for a command that left nothing done, which is what
      lets it be tried again. It stays on the screen until it is closed (`stays`, note 33). *For the owner to
      judge:* the notice is drawn as a done one.
    - *What was read is what runs.* The request names the sequence and the moment it was saved, as its reading
      read it; a sequence saved again since is refused (`SequenceMoved`). A line whose first words name another
      command than when it was saved is refused with its number (`LineMoved`): the vocabulary changed under it,
      and a line run blind is not read as whatever its words mean now.
    - *Asked again with the same key*, the command answers what it answered (ADR-0005), and each line is invoked
      with a key of its own made from that key and the line's number.
    - *A command that answers neither the revision it left nor what it did* cannot be chained: that is the
      command's defect, thrown as one, as the page's door throws it (note 36).
    - *Added as built:* the request the line makes names the sequence as `<name>@<owner>` beside the moment it
      was saved, so that a sequence of that name the caller saves between the reading and the run is not run in
      its place. A refusal of the first line says that nothing ran before the line's own sentence.
    - *Added as built:* the answer holds, for each line that ran, the command it named and that command's own
      answer whole, as JSON text: a contract holds no object in an object (note 12).
    - *Added as built:* a stopped run says what `undo` reaches: of the lines before the one it stopped at, only
      those that wrote the workspace, and "wrote nothing" where none did.

63. **The lines that say them (§3, §9).** Two commands of every application, both among the AI's tools as every
    command is.
    - `sequence save <name> [desk|all] <line> then <line> …` keeps the lines under the name. The lines are
      separated by the word `then`, standing alone: `;` is already the scenario verbs' own, between shifts. A line
      that holds `then` as a word of its own cannot be typed this way; the capability takes its lines as a list.
      The reading says each line with its number, whom the sequence is for, and whether it replaces the user's
      own. It changes no workspace and is in no log.
    - `sequence run <name>` runs one. The line offers the sequences the user can see, each with how many lines
      it has and whose it is. Its reading says every line, and reads the first on the workspace as it stands:
      what that line would do is said, and a first line that is refused refuses the reading.
    - *Added as built:* the word after the name says whom the sequence is for when it is `desk` or `all`, in
      any case. A typed sequence's first line so cannot begin with either word; no command does today, and the
      capability's list of lines is not read that way.
    - *Added as built:* what the line offers. After the name, `desk` and `all`; where a line begins, the words
      of the commands a sequence may hold; within a line, what that line's own command offers; and the word
      `then` after a line that reads as the workspace stands and may have another after it. Nothing is offered
      while the name is typed, nor within a line a sequence may not hold.
    - *Added as built:* the save's reading reads every line at each keystroke, fifty at most; what that costs is
      not measured. Its `lines` argument shows the lines as typed, with `then` between two.
    - *Added as built:* the run's reading. It says no moment: the line shows a reading's moment as a time of
      day, which says nothing of a sequence saved on another day. It says the first line's own sentence last,
      since a command's sentence may end without a full stop, and names the owner of a sequence that is
      another user's. `sequence run` with no name is refused with the names the user can see
      (`SequenceNotNamed`), and a word after the name is refused (`UnreadWords`).
    - *§9's "several commands in one line" stands.* It refuses running a line of several commands, because one
      preview cannot show what the second would read after the first. `sequence save` runs none; and the run's
      reading therefore says the lines as they are written and reads the first alone, claiming nothing of what
      a later line would change.
    - *The words.* `sequence`, as §8 names the thing. *For the owner to judge*, with `then`.

64. **What the page does with a run (§5, note 36).** The door reads the answer as any command's: where the
    workspace was left and what was done. It also tells its page what each line's command answered, in order, as
    if the page had run that command: Rates opens the Run a `price` made (note 36) whether `price` was typed or
    was a sequence's last line. Nothing else of the client knows a sequence: both commands are on the line from
    the catalog, as every command is.
    - *Added as built:* a run that stopped tells the page of the lines that ran, then of the run. A run's
      answer without its lines, or with a line that holds no command and no answer of it, is the server's
      defect: the door says it by name and tells the page of no line.

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
- How the line spells the lists of an `add each` iteration (§3): the trades added together, the trades taken out,
  and where the next iteration starts. Left aside by the owner (2026-11-11); the typed value does not wait for it.
