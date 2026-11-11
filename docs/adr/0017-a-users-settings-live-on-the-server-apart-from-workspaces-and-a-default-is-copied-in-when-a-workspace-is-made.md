# ADR-0017: A user's settings live on the server, apart from workspaces, and a default is copied in when a workspace is made

Status: PROPOSED 2026-11-11. Raised by the owner: "what do we do about user-specific settings?" (2026-11-11). §6's
first question was put to him that day; every other point is a DEFAULT until he confirms it. Answers ADR-0006's
consequence "it moves to the server then" and the first item of ADR-0008's Open. Changes nothing a Run does.

## Context

Nothing in the workbench is a user's setting in its own right, yet several things already behave per user or per
browser, each in its own way (main at e5e485a):

| What | Where it is kept today |
|---|---|
| Light or dark theme | The browser: Mantine's own `localStorage` manager, dark unless changed (`client/src/main.tsx`) |
| How often and how lately each command ran (ADR-0006 §6) | The browser's `localStorage` (`qube.commandUsage.v1`), "until the user settings store arrives with sign-in" |
| Column widths the user dragged (ExGrid ADR-0016) | The workspace's client store, by panel; lost when the panel closes or the page reloads |
| The measures Price prices | The workspace (an ordinary `Set`); a new workspace takes its owner's last selection, read from the owner's workspace logs (ADR-0010 §10, ADR-0016 note 2 of step 1) |
| The pricer's display currency | The workspace; a new one starts at the company profile's (`BaselineProfile.DisplayCurrency`) |
| Rates' defaults for a new line (currency, notional, direction) | The workspace's context (ADR-0008); ADR-0008 leaves open whether they should be the user's across workspaces |
| The AI's model | One for the whole server (`Agent:Copilot:Model`), though the company offers several (C34) and the host's wire already carries a model per turn |
| A user's desk | Server configuration (`Qube:Desks`) |
| Scenario sets, and later saved sequences and views | Saved items (C32): owner, name, visibility `private`, `desk` or `all` |
| Who the user is | One configured user (`Qube:DemoUser`) until ME-ID sign-in (C30) |

Two of these, a workspace's display currency and Rates' defaults, are values a Run prices. ADR-0003 §2 and the handoff
forbid Price from reapplying newer defaults: a workspace priced must be the workspace as edited. Whatever a user's
setting is, it cannot be read at Price.

## Decision

### 1. Four kinds, kept apart

| Kind | Examples | Where, and how it acts |
|---|---|---|
| **A preference** changes how things look or are found, never a figure | Theme (`dark`, `light`, `auto` following the OS); column widths by grid; command ranking; the AI's model | The user settings store (§2). Applied as soon as it changes, wherever the user is signed in |
| **A default** becomes an input value of something the user makes | The display currency of a new pricer workspace; Rates' currency, notional and direction for a new line | The user settings store, read only when a workspace is made, and copied into it (§3) |
| **An account attribute** decides what a user may see or do | Desk, the developer and administrator roles (C31, C32) | ME-ID's groups or app roles once sign-in exists; server configuration until then. Never the user's to change: a desk the user could pick would show them what another desk shared |
| **A saved definition** is something made to be used again, by name | Scenario sets; saved sequences of edits (ADR-0016 §8); saved views and workflows | Saved items (C32), as today. Not a setting |

What only makes sense on one screen stays in that browser and is no setting: whether the AI pane is open, which
panels are open, the text being typed. A phone and a desktop differ there by design.

Sort and filter (ADR-0016 step 4b) are view state: they last as long as the panel. One a user wants again is a saved
view, a saved definition, when views are saved.

### 2. The store is the kernel's, and modules register what it holds

- **One generic table** in the SQLite file, beside `SavedItem`: `UserSetting(user_id, key, value_json, updated_at)`,
  one row per user and key. The kernel keeps and checks it and knows no domain word, as the composition kernel must
  stay free of every domain.
- **A key is registered** by the module it belongs to, as a table's field is (ADR-0016 §1): its key
  (`shell.theme`, `pricer.displayCurrency`), title, words, form and choices, its kind (preference or default), and its
  value when the user has set none. A write of an unregistered key, or of a value its form refuses, is refused by name.
- **A stored value that no longer meets its form** (a currency since taken out of the choices) is not applied and is
  not replaced: a preference is shown as unreadable on the settings page and its registered value is used, which
  changes no figure; a default refuses the making of a workspace, naming the setting and how to reset it (§3).
- **A write replaces the value** and stamps the time. Settings are not workspace edits, so they are not in the edit
  log and `undo` does not reach them (ADR-0016 §11 already leaves out saved items and view state). They explain no
  figure, since a default's value is copied into the workspace that used it (§3), so they keep no history. The command
  that changes one says what it replaced, so the user can set it back.
- **Whose.** A user reads and writes their own. An administrator may read anyone's, to help them, and writes none.
  The AI reads and writes the caller's through the same commands as the user, shown as `the AI for you` (ADR-0016
  §11), never under its own account's name (C44).
- **The user id** is ME-ID's tenant and object id (`tid`, `oid`) once sign-in exists (C30): an e-mail address or a
  display name can change. Until then it is `Qube:DemoUser`, as for workspaces and Runs.

### 3. A default is copied into a workspace when it is made, and never read again

- The command that makes a workspace reads the caller's defaults and writes them as part of its own making, as the
  pricer already writes its owner's last selection of measures (ADR-0016 note 2 of step 1). Each value records where
  it came from: `your setting` or `the company profile` (or the profile it names). The page shows that source where
  it shows the value.
- From then on the value is the workspace's: edited with `set`, taken back with `undo` (a context change that keeps
  the source version, ADR-0016 §11), frozen by Price (ADR-0003 §2). A setting changed later changes no workspace and
  no Run.
- A user who has set no default gets the profile's, and the source says so: that is the registered default, not a
  fallback. A setting that cannot be read (§2) refuses the making rather than starting from another value.

### 4. Declared once, for the line, a page and the AI

The store is reached through three kernel capabilities, `settings.read`, `settings.set` and `settings.reset`, through
the gateway like any command (ADR-0016 §5), so the command line, a settings page and the AI share one declaration
and one refusal. The line's words are a DEFAULT, settled in the spec: `settings` lists the user's settings and opens
the page, `settings <name> <value>` sets one, `settings reset <name>` returns it to the registered value. They are not
`set`, which edits the workspace in view, so `set ccy USD` keeps meaning the workspace's display currency
(ADR-0016 note 18, `AmbiguousField`) and never the user's default.

### 5. What the browser does

- The shell reads the user's preferences when it starts. It may keep a copy of the last ones it read in browser
  storage, so the first frame is drawn in the user's theme instead of flashing the other; the server's answer then
  replaces it. A preference changes no figure, so this copy can make nothing wrong. A default is never kept in the
  browser.
- Column widths are kept by a stable grid id with its application's prefix (`xva/trades/existing`, `rates/lines`,
  `trace/tree`), across workspaces and panels, in place of today's per-panel memory. A drag writes when it ends;
  `settings reset` of a grid's widths puts its declared widths back.
- The command ranking a browser already holds is carried over once, the first time that browser meets the server
  store, and its local copy is then removed.

### 6. Open

1. **Explicit or last used (put to the owner 2026-11-11).** Is a default only what the user set explicitly
   (recommended: a one-off change in one workspace does not leak into the next, and the source shown is simple), or
   the value the user last chose in any workspace, as the measures are today, or an explicit setting first and the
   last used otherwise? Under the first, the measures keep their "last selection" (ADR-0010 §10) as decided.
2. Whether a preference is the user's across devices (DEFAULT: yes, with `auto` for a theme that follows each OS) or
   each device's.
3. Which models the AI may use (an allow-list in configuration, `Agent:Copilot:Models`, with the first as the default;
   DEFAULT) and whether a Run the AI made records the model it used (it should: lineage).

## The first registrations

| Key | Kind | Registered value | Replaces |
|---|---|---|---|
| `shell.theme` | preference | `dark` | Mantine's `localStorage` |
| `commands.usage` | preference | none | `qube.commandUsage.v1` in `localStorage` |
| `grid.widths.<grid id>` | preference | the declared widths | the workspace store's per-panel widths |
| `agent.model` | preference | the first allowed model | `Agent:Copilot:Model` alone |
| `pricer.displayCurrency` | default | the company profile's | the profile alone |
| `rates.newLine.currency`, `.notional`, `.direction` | default | none (the workspace's own, ADR-0008) | the workspace's context alone |

A registration belongs to its module: the pricer's and Rates' sit in their domain modules, the shell's and the grid's
in the kernel's client and server.

## Delivery order

After ADR-0016's step 4 is merged, since the same files (the host's registrations, the grid helpers) move under it.

1. The store, its three capabilities, the settings page and the line; theme, command ranking and column widths.
2. The defaults, with their source shown on the pricer and Rates pages.
3. The AI's model, once the allow-list is configured.

ME-ID sign-in (C30) is its own work. The store runs on `Qube:DemoUser` until then, and only the id changes when it
arrives.

## Consequences

- ADR-0006's "ranking by frequency moves to the server" and ADR-0008's first Open item are answered here.
- No setting is read at Price, by the AI's run, or by a workflow; each reads the workspace or the accepted input it is
  given.
- The kernel gains a store and three capabilities with no domain word; each domain registers its own keys.
- Admin tooling, and moving `Qube:Desks` to ME-ID groups, wait for sign-in.
