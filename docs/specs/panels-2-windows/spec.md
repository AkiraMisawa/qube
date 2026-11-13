# Panels in areas, step 2: a panel in another window of the browser

Status: in progress (2026-11-13): all but the ExGrid bump, which waits for `v0.3.0-rc.1` on npm

The decision is [ADR-0018](../../adr/0018-panels-are-arranged-in-areas-that-split-float-and-pop-out-and-the-ai-only-proposes-an-arrangement.md)
§9, with §1 (reading order, the active panel), §3 (`dock panel` from step 2) and §7 (the arrangement kept by the
browser tab). Step 1 is [panels-1-areas](../panels-1-areas/spec.md), merged as #34 and #37; the per-panel page state
(ADR-0006 note 10) merged as #38. The owner said to start this step on 2026-11-13 ("前者は始めていいよ"). What this
spec settles beyond §9 is DEFAULT, recorded in ADR-0018 §9's note of 2026-11-13 and put to the owner with the pull
request, per his standing preference of 2026-11-08.

## Problem Statement

A desk user with two monitors keeps every panel inside one browser window. A Run beside the pricer, or the Pricing DB
on the second screen while the first holds the page being worked on, means one window stretched across both, or
switching tabs.

## Solution

One pull request (ADR-0018 §11, step 2).

- **`pop out panel`** moves the panel into an area of its own in a new browser window, over where its area stood and
  at its size, to be dragged to another monitor. It is run from a key or a press (the line's Enter, the tab's menu, the
  area's list), so the browser allows the window. dockview 8.9.0 opens it (`addPopoutGroup`) on the client's
  `popout.html`, a same-origin page that holds nothing of its own.
- **The window is the main one's.** It takes the main window's stylesheets and its `<html>` attributes (the colour
  scheme among them) and keeps them in step; it is titled by the panel it shows; a menu, a select, a tooltip or a
  dialog opened from it opens in it; the command line, `/`, the shell's chords and `ai` work from it, on its panels; the
  shell's notices are said in it while it is the window in use.
- **ExGrid reads the window it is in.** A page moved into another window, or back, is drawn anew there: ExGrid reads
  its window's Device Pixel as a grid is created (ADR-0090 in ex-grid, released in 0.3.0-rc.1), and the page takes back
  the screen state its panel kept (ADR-0006 note 10).
- **Back where it was.** `dock panel` puts a popped-out panel back into the area it came from. A window the user
  closes puts its panels back there too; a window the browser blocks leaves the panel where it stood. A reload, or the
  main window showing one strip, brings the other windows' panels back into the main window. Each is said.

## The verbs (ADR-0018 §3's table, from step 2)

| Line | Menu (tab, area list) | Refused when |
|---|---|---|
| `pop out panel` | Pop out | areas are not shown; the panel is in a window of its own already and alone there; it is the only panel docked in the main window |
| `send panel to` | Send to | as in step 1; and into another window or a floating area, where the panel is the only one docked in the main window |
| `dock panel` | Dock | the panel is neither floating nor in another window |

A panel in another window is in an area of its own window, which is one area, as a floating area is (ADR-0018 §3's note
of 2026-11-12): `split panel`, `float panel`, `size panel` and `maximize panel` are refused for it, saying to dock it
first. `send panel to <title>` moves a panel between the main window and another, either way. `gather panels`, run from an
area of the main window, brings every panel into that area, and the other windows close; in another window it is
refused. When the last panel leaves a window, by
`send panel to`, `dock panel` or `close panel`, the window closes.

## User Stories

1. As a desk user with two monitors, I type `pop out panel` on a Run and drag the window it opens to my second screen.
2. As a desk user, I open a grid's column menu, a select or a tooltip in that window, and it opens there.
3. As a desk user, I press Ctrl+K or `/` in that window and the command line opens there, acting on its panel.
4. As a desk user, I switch to the light theme in the main window and the other window follows.
5. As a desk user, I close that window and its panel is back in the area it came from, and the shell says so.
6. As a desk user whose browser blocks pop-ups, I am told so, and the panel stays where it was.
7. As a desk user, I reload the tab and the panels of my other windows are back in the main window, said so;
   `pop out panel` sends them out again.
8. As a desk user, I type `dock panel` in the other window and its panel goes back into the main window.
9. As a desk user, the grid in the other window is sharp on a display of another scale than the main window's.

## Implementation Decisions

- **The arrangement holds the windows.** dockview serialises a popped-out area under `popoutGroups`, with the area it
  left (`gridReferenceGroup`). `areasOf` reads them after the floating areas, each marked as in another window, in the
  order they were opened, so reading order is §1's. An area dockview keeps hidden in the main window as the place a
  whole area left for a window is no area: it holds no panel, takes no room and is not listed, and it goes once no
  window will come back to it. `needs` counts the main window's areas only. The area a panel left is kept with the area
  a panel was floated from (`floatedFrom`, read from dockview's own move), so `dock panel` puts it back there. An area's
  only panel takes its whole area out (`addPopoutGroup` of the area), so that its place is kept.
- **The port** gains `popOut(panelId)`, which answers whether the window opened, and `dock` handles a panel in another
  window as it does a floating one. `Area` gains `window` (true for an area in another window). A verb is tested over
  the fake port, as step 1's are.
- **Coming back.** Before the kept arrangement is drawn (a reload, or the areas drawn again after the strip), its other
  windows are folded into the main window (`withWindowsDocked`, a pure function): each window's panels go back into the
  area they came from where it is there, else into the last area of the main window, and the shell says where. A
  closed window's panels are put back by dockview into the area they came from, or, where that area has gone, into an
  area of their own; the shell says which. The arrangement is not kept while the page is going (`beforeunload`, heard
  before dockview's), since dockview puts the windows' panels back as it goes and the reload is to say so.
- **One window shell per window** (`WindowShell`), drawn by the areas into each other window's document while it is
  open: it copies the main `<html>`'s attributes and keeps them in step, adds a stylesheet the main document gains after
  the window opened, titles the window by the panel it shows, draws the notice strip, holds the window's portal node,
  and attaches the window's keys.
- **Portals.** Mantine's `Portal` reads its target from the theme. A page in another window is drawn inside a
  `MantineThemeProvider` whose `Portal` target is that window's portal node, so its menus, selects, tooltips and
  popovers open there. The shell's dialogs (the command line, the Run dialog, the applications' dialogs, the settings)
  open in the **window in use**: the window the user last pressed in, focused or typed a key of the shell's in, or the
  one a panel was just popped out into, changed only while no dialog is open, so that a dialog never moves between
  windows while it is open. The notices are said there. The active panel is one the window in use shows: there, the
  one it shows; in the main window, the one active there last.
- **What Mantine listens for on the main document** is told from the other windows: a press (`mousedown`,
  `touchstart`) outside that window's popovers, which closes an open menu or select as a press outside it does in the
  main window, and Escape, which a dialog's close listens for. Each is told as a copy of the event on the main
  document's body, and a copy whose default was prevented prevents the original's. Tab is kept inside a dialog open in
  that window by its shell, as Mantine's focus trap keeps it in the main one: the trap, told the key, walks up from the
  dialog to the main body and fails on another document. No other key is told: the shell's own keys are attached to
  each window's document.
- **The shell's keys** (Ctrl+K, `/`, the chords) are attached to the main document and to each other window's, by one
  function, in place of Mantine's `useHotkeys`, which listens on the main document only. The command line opens in the
  window the key was pressed in. `keyboard.ts` reads the focused element and focus changes of the line's own document.
- **`ai` from another window** asks with that window's active panel, as from the main one; the AI pane stays in the
  main window (§9), and the shell says in the other window that the answer is there.
- **A page drawn anew in its window.** An area's page is keyed by the window its area is in, so a move into another
  window or back mounts it anew there, after dockview has moved its element. The workbench takes `@ex-grid/*`
  `0.3.0-rc.1`, all seven packages at one exact version (ADR-0001).
- **Blocked or failed.** dockview's `onDidOpenPopoutWindowFail` is said as a refusal: the browser did not open the
  window, and the panel is where it was.
- **The strip closes the other windows.** The areas, dockview with them, are not drawn while the main window shows the
  one strip (ADR-0018 §5), so the other windows close with them, and their panels join the strip; the shell says so,
  and the arrangement drawn when there is room again has them in the main window, as after a reload.

## Testing Decisions

- Vitest: `areasOf`, `needs` and reading order with other windows and a hidden area; `withWindowsDocked`; the kept
  value with `popoutGroups` read and refused; `pop out panel` and `dock panel` over the fake port, and the verbs refused
  for a panel in another window.
- Playwright (`e2e/windows.spec.ts`, new; Chromium, which opens windows a test asks for): pop out by the line and by
  the menu; the window's scheme, followed; a Select, a menu and a tooltip in the window; Ctrl+K and `/` there, and a
  verb run there; a dialog opened from there; the window closed by the user; a window refused (`window.open` answering
  nothing); a reload; `send panel to` both ways and `dock panel`; the strip closing the windows; a grid in the window
  told that window's Device Pixel when the window's scale differs (Chromium's device metrics on the window alone).
  Step 1's specs keep passing.

## Out of Scope

- Parts of a page and links (§10, step 3).
- An area split inside another window (one area per window, as a floating area is).
- Windows opened again by themselves after a reload: a browser opens a window only on a key or a press (§9).
- Other browsers than Chromium, and touch, in this pull request's checks; Safari's and Firefox's pop-up rules are not
  checked here.
