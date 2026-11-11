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
   the chain key, context menus from declarations.
3. The pricer's remaining commands as capabilities through the gateway; Rates registers.
4. View verbs and the AI's row read.
5. Drafts (§10): a detail dialog drawn from a registration, with the tables under its row and its templates. The
   pricer's trade and CSA must carry those fields and tables first; its sample has neither them nor such a dialog.
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
   product's term, a cell, a condition and the AI.
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
2b places with `go` and `find`, 2c the chain key and menu items from declarations. Notes 16 to 19 are 2a's.

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
    - *A table left out is not yet the screen's.* `all`, a subset and `where` with no table still mean the main
      table, as in step 1, though §9 gives them the focused grid's table. Built with the places (ticket 12, 2b),
      where `go csa` first makes another grid the screen from the line.
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
