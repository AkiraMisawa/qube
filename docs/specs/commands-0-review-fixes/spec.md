# Generic commands, delivery step 0: the review's fixes

Status: ready-for-agent

The decisions are ADR-0006's notes of 2026-11-11 (what a command says back, and what the empty line lists), with
ADR-0016 §6 (a notice has a kind) and docs/design-guidelines.md §6 and §7. This spec orders ADR-0016's Delivery
order, step 0, and names the seams it is tested at. The owner asked on 2026-11-11 for every step to be implemented
without a question at each one, so the seams below are DEFAULTs and are reported, not asked.

## Problem Statement

The command line was driven in a browser on 2026-11-10, on an XVA workspace of 215 existing trades. Four things made
it hard to trust:

- A line the command could not read closed without a word: `set <trade> a` with no value, `vary CSA-A threshold` with
  no value, `vary CSA-A threshold 10,000k`, `scenario duplicate s1`. Nothing ran and nothing said so.
- A result and a refusal looked the same, a yellow band. It pushed the one-screen page down, it stayed after later
  commands that said nothing (`before none`, `price`), and a refused edit named an entity's key (`selection:measures
  measures was not changed`).
- A trade grid's B and A cells took `TRUE` and `FALSE` only, where `set` takes `on` and `off`. The hints of `set` and
  `vary` showed ids no workspace has (`T-1001`).
- The empty line and `?` listed the sample workflows first, in the order registered, and stopped at fifty without
  saying so: on the XVA page `new rates` and `open pricing db` were not listed. The placeholder showed Rates'
  commands on every page.

## Solution

Four changes in the client, with no change to what a command does:

1. A command that throws while it reads its arguments keeps the line open, with what was typed and the reason.
2. A notice has a kind, `done` or `refused`, drawn over the page and never pushing it; a done one leaves by itself.
   Commands that edit say what they did, in titles.
3. A flag is read one way in a cell and in `set`, and the hints of `set` and `vary` are made from the open workspace.
4. The empty line and `?` list the page's commands first under headings, say how many more there are past the cap,
   and the placeholder follows the page.

## User Stories

1. As a desk user, I want a line I typed wrongly to stay open with my text and the reason, so that I fix a word
   instead of typing the line again.
2. As a desk user, I want a command run by a chord that cannot run to say why, so that a key never does nothing in
   silence.
3. As a desk user, I want a refusal to look unlike a result, by colour, icon and role, so that I see at a glance
   whether the command ran.
4. As a desk user, I want a result to go away by itself and a refusal to stay until I close it, so that the screen
   does not keep old results and never loses a reason.
5. As a desk user, I want a notice drawn over the page, so that the grids do not jump when a command answers.
6. As a desk user, I want `before none` to say how many trades it cleared and `price` to name the Run, so that every
   command that changes something says so.
7. As a desk user, I want a refused edit to name the row and the column as the screen does, so that I am never shown
   an internal key.
8. As a desk user, I want to type `off` in an A cell, so that the grid and the command line read a flag alike.
9. As a desk user, I want the hint of `set` to show a row of my workspace, so that the example can be typed as it is.
10. As a desk user, I want the empty line to list this page's commands first, under headings, so that I find what the
    page can do without scrolling past the sample workflows.
11. As a desk user, I want the list to say how many commands it does not show, so that I know to type.
12. As a desk user, I want the placeholder to name commands of the page I am on.

## Implementation Decisions

- **Reading before doing.** `execute` (`client/src/platform/commands/commands.ts`) catches what a command's `run`
  throws and answers its reason, as it answers `unavailable`. A command's `run` reads its arguments first and throws
  before any effect: every command of `client/src/apps/index.tsx` already does. The line (`CommandPalette`) runs the
  command, and on a reason stays open with the text and shows it where it shows a missing argument. A chord shows
  the reason as a refused notice.
- **A notice is `{ kind, message }`.** `notify(kind, message)` replaces `notify(message)` on the workspace context,
  and every call site states its kind. A reason a command gives for doing nothing is `refused`, a command that found
  nothing to do and says so is `done` (ADR-0016 §6).
- **Where a notice is drawn.** A zero-height strip at the top of the main area, sticky under the header, holding one
  Mantine `Notification`: teal with a tick and `role="status"` for done, red with a cross and `role="alert"` for
  refused, `data-testid="notice"` and `data-kind`. A done one is removed six seconds after it is shown, the wait
  held while the pointer is over it or the focus is in it. A later notice replaces an earlier one.
- **Sentences.** `before` and `after` say `Cleared Before on 2 existing trades.` or `Ticked After on 3 new trades.`
  once the edit is taken, from the command or from the caption's buttons, which run the same function (`sideSaid`,
  `client/src/apps/xva/editing.ts`). Price on the XVA page says the Run it was accepted as (`Price was accepted as
  Run <id>.`); on Rates the Run's panel opens, which says it. `measures` and `currency` say nothing more: the
  toolbar's controls show what they set. A refused edit is said by `refusedEdit` (same file): a trade's id and its
  column in words, `The measures`, `The display currency`, `Before … on the existing trades`.
- **Flags.** `parseBoolean` reads `on`, `off`, `yes`, `no`, `true`, `false`, `y`, `n` in any case, the list the
  server's `PortfolioCells.Read` has; a cell's refusal says `Type on or off.` Rates' flags read through it too.
- **Hints.** The XVA page offers `examples()`: its first CSA's id, and a trade a grid has read (the first of the page
  read last, the Existing grid's before the New one's). The hints of `set` and `vary` are made from them when the
  line opens (`setHint`, `varyHint`), and are the form alone while the page has none.
- **The list.** `listed(query, commands, usage)` answers sections `{ heading, commands }` for the empty line and `?`:
  the page's commands (a command says `page: true` when it acts on the page in the active panel) under the panel's
  title; then each group of the other commands that can run; on the empty line, `Not available now` last. The
  applications' commands are registered before the shell's. Under a heading the order is use, then registration. A
  typed query is one section with no heading, ranked as `rankCommands` ranks it. The line draws at most 200 commands
  or 50 values and then a line `N more: type to narrow.`
- **The placeholder** is made from the first three commands of the empty line's list.

Not changed: what any command does on the server, the words of any command, `set`'s and `vary`'s readers
(ADR-0016's step 1 moves them to the server).

## Testing Decisions

Tested at the seams the client already has: pure functions under Vitest, and what only a browser shows under
Playwright. A test names the ADR it holds.

- `commands.test.ts`: `execute` answers a thrown reason and runs nothing further; `listed` puts the page's commands
  first, groups the rest, keeps the unavailable last on the empty line and out of `?`, and orders by use under a
  heading; the placeholder's words.
- `editing.test.ts`: every spelling of a flag, and a word that is none; `refusedEdit` for a trade's cell, the
  measures, the display currency and a grid's side; `sideSaid`.
- `xva.spec.ts`: a line that cannot be read stays open with its text and its reason, and runs once corrected; a done
  notice is teal, does not move the page's toolbar, and leaves by itself; a refused one is red and stays; `before
  none` says its count; `off` typed in an A cell clears it; `?` on the XVA page lists `new rates` and `open pricing
  db`, with the page's heading first; the placeholder names the page's commands.
- Existing browser tests that read a notice keep reading it by `data-testid="notice"`, straight after the command
  that makes it.

## Out of Scope

- The reason shown while a line is typed, completion of `set`'s rows and columns, and a result that says how a value
  was read and how many rows it changed: ADR-0016 §5 and §6, step 1.
- Keeping the line open when the server refuses: the chain key and the preview (ADR-0016 §5, §9), steps 1 and 2.
- History in the line, a run button on a phone, search words a command declares (the review's F10, F12, F15).
- Rates' three command defects of 2026-11-10: with Rates' registration, step 3.

## Further Notes

Providers are unchanged: every figure on these pages is simulated, and says so.
