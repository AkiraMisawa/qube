# A user's settings: the store, its commands, preferences, defaults and the AI's model

Status: in progress

The decisions are [ADR-0017](../../adr/0017-a-users-settings-live-on-the-server-apart-from-workspaces-and-a-default-is-copied-in-when-a-workspace-is-made.md),
accepted on 2026-11-11 with the owner's answers to its §6, and the ADRs it builds on: ADR-0003 (the log, Price never
reads newer defaults), ADR-0005 (commands), ADR-0006 (the line), ADR-0008 (Rates' defaults) and ADR-0016 (one
declaration for the line, a page and the AI; what `undo` reaches). This spec orders ADR-0017's Delivery order and names
the seams it is tested at. The owner asked on 2026-11-11 for the implementation to start once the generic commands'
step 4 was merged; the seams below are DEFAULTs, per his standing preference of 2026-11-08, and are reported, not asked.

## Problem Statement

A desk user who picks the light theme, drags a column wider or runs `price` a hundred times finds each kept by one
browser only, or not at all: the theme and the command ranking live in that browser's storage, and a dragged width
goes when its panel closes. A user who always prices in USD sets the display currency again in every new workspace,
and a Rates user types the same currency and notional into every new workspace's defaults. The AI runs on one model
for the whole server, though the company offers several.

## Solution

One pull request, in three steps, each a commit of its own (ADR-0017's Delivery order).

**Step 1. The store, its commands and the preferences.** The kernel keeps a user's settings in one table, whose keys
modules register with a form. `settings.read`, `settings.set` and `settings.reset` are commands through the gateway, so
the line, a settings page and the AI share them. The theme (`dark`, `light`, `auto`), the command ranking and the
column widths move into it, and follow the user to another browser.

**Step 2. The defaults.** The pricer's display currency and Rates' currency, notional and direction for a new line are
defaults a user sets. The command that makes a workspace copies them in, and the page says where each came from: the
user's setting, the company profile, or this workspace. A setting changed later changes no workspace and no Run.

**Step 3. The AI's model.** Configuration lists the models the AI may use. The user's default model is a setting; a new
conversation starts on it, and the model changed in the AI pane belongs to that conversation alone. Each agent run
records the model it ran on.

## User Stories

1. As a desk user, I pick the light theme on my laptop and see it on my desktop too.
2. As a desk user, I set the theme to `auto` and each machine follows its own OS.
3. As a desk user, I widen the Notional column of the existing trades, reload, open another workspace, and it is
   still that wide.
4. As a desk user, I reset a grid's widths and see its declared widths again.
5. As a desk user, the commands I run most come first on any machine; what my browser had ranked is not lost.
6. As a desk user, I type `settings` and see each setting, its value, and whether I set it or it is the registered
   value; I change one there or reset it.
7. As a desk user, I type `settings theme light` or `settings reset theme`, with the names and values completed.
8. As a desk user, I ask the AI to switch me to the dark theme, and it does so through the same command.
9. As a desk user, a setting I set cannot be read or changed by another user.
10. As a desk user, I set my default display currency to USD; every pricer workspace I make from then on starts in
    USD and says it came from my setting; the ones I made before are unchanged.
11. As a desk user, I change one workspace's display currency to EUR; my default stays USD, and `undo` takes the EUR
    back as before.
12. As a desk user, I set my Rates defaults (USD, 10mm, pay) once and every new Rates workspace starts with them.
13. As a desk user, a default I set that is no longer offered refuses the making of a workspace, naming the setting
    and how to reset it, rather than starting from another value.
14. As a desk user, I choose my default model among those configured, and a new conversation starts on it.
15. As a desk user, I switch one conversation to another model; my default and my other conversations are unchanged.
16. As a developer, an agent run says which model it ran on.

## Implementation Decisions

- **Registrations.** A module registers `SettingDefinition`s through an `ISettingCatalog`, as tables are registered:
  key (or a key family such as `grid.widths.*`), title, words, kind (preference or default), form (a choice among
  values, a number, text, or a structured value its module checks), and the registered value. The kernel's
  catalog registers `shell.theme`, `commands.usage` and `grid.widths.*`; the pricer `pricer.displayCurrency`; Rates
  `rates.newLine.currency`, `.notional` and `.direction`; the agent `agent.defaultModel` where models are configured.
- **The store** is a port (`IUserSettingStore`) with an in-memory implementation for tests and SQLite's
  `UserSetting(user_id, key, value_json, updated_at)` beside `SavedItem`. A write replaces the value (ADR-0017 §2).
- **Reading** answers each registered setting for the caller: its value, whether it was set or is the registered
  value, and, for a stored value its form no longer takes, that it is unreadable (a preference then shows its
  registered value; a default refuses a making that needs it).
- **The commands** carry a new effect, `UserSetting`, so they are commands (never a workflow step) and the agent is
  offered them as it is every command. The caller is the server's, never an input. Their answers say what was
  replaced, so a notice can say it and the user can set it back.
- **The page reads them** with `GET /api/settings` at start, which answers as `settings.read` does, and writes through
  the gateway. The theme's last value is also kept in the browser, for the first frame only (ADR-0017 §5).
- **The line's words** (DEFAULT, ADR-0017 §4): `settings` opens the settings dialog; `settings <name> <value>` and
  `settings reset <name>` set and reset, a name being the setting's words (`theme`, `display currency`). They are the
  shell's commands, built from the settings catalog as `set <context field>` is built from the contract catalog, and
  work on every page, with or without a workspace; the server checks the value when it is set.
- **Grid ids** carry their application's prefix (`xva/trades/existing`, `rates/lines`, `trace/tree`).
- **Defaults at making.** The pricer's making writes the display currency as the making's own `Set`, as it writes the
  last selection of measures, so `undo` finds nothing of it; the page derives the source from the log (an edit of the
  making is the user's setting, a later one this workspace's, none the profile's). Rates' making writes the defaults
  into the context it is made with and records which came from the user's settings; a later edit of a default drops
  that field's record.
- **The AI's model.** `Agent:Copilot:Models` lists the models allowed, the first being the registered default; the
  scripted agent has none and registers no setting. A conversation holds its model from its first turn: the user's
  default then, or the one chosen in the pane, which is refused unless allowed. Each agent run records it.

## Testing Decisions

Seams, as the earlier steps have them (DEFAULT):

- **The settings service** (`UserSettings`) over a synthetic registration with no domain word: what reading answers,
  what a write of an unregistered key, a value its form refuses or another user's key does, and an unreadable value.
- **The command gateway** (`CommandService`) with the three commands: answered for the caller, refused by name.
- **The host** (`ApiTests`' factory): `GET /api/settings`, and a setting kept in SQLite surviving a restart.
- **The pricer's and Rates' making** through their services: a default copied in with its source, and unchanged after
  the setting moves.
- **The agent's endpoint** (`AgentApiTests`): a conversation's model and the run that records it.
- **Vitest** for the client's helpers: the theme's manager, the widths by grid id, the ranking's carry-over.
- **Playwright** for what only a browser shows: the theme and a width kept across a reload, the settings dialog and the
  line, and a default's source on the pricer page.

A test names the ADR section it holds. Expected values are literals.

## Out of Scope

- ME-ID sign-in (C30) and roles (C31): the store runs on `Qube:DemoUser`; an administrator's reading waits for roles.
- Moving desks to ME-ID groups.
- Saved views, saved sequences of edits (ADR-0016 step 6).
- A model switch for the stand-in agent, which has no model.
