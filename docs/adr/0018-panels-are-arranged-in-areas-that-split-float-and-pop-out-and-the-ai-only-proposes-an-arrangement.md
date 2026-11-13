# ADR-0018: Panels are arranged in areas that split, float and pop out, and the AI only proposes an arrangement

Status: ACCEPTED 2026-11-12. Raised by the owner on 2026-11-12: beyond the panels' tabs, can the workbench float a
tab, or float and dock its parts one by one as Bloomberg's terminal does? The owner decided the AI's part the same day
(§8) and the order of delivery (§11). Proposed the same day in #30; the owner accepted it as written at 08:06Z, with
the amendment of ADR-0010 §1 below. The number 0018 is the next free one.

What it changes in earlier decisions, each said where it is decided:

- **ADR-0006 note 7** (the panels' tabs, merged in #29): its verbs act within the panel's area
  (§1), and "the panels are kept while the page is loaded" becomes "kept by the browser tab across a reload" (§7).
- **ADR-0007 §7 and §8**: the AI pane's card gains a button that applies a proposal, and the context the pane sends
  gains the open panels and their arrangement (§8).
- **ADR-0010 §1, CONFIRMED by the owner**: "Below `sm` (768px) the screen is three tabs" would read the width of the
  pricer's area rather than the window's (§5). Accepted by the owner with this record, 2026-11-12.
- **docs/design-guidelines.md §9**, which has layouts respond to the window with media queries: a page responds to its
  area (§5). The guidelines change with step 1, before its code (ADR-0009).

Each of these gets a note in its own ADR in step 1's pull request, before its code (docs/agents/domain.md). Every point
below not marked CONFIRMED or OPEN is DEFAULT.

## Context

**What the shell has.** One strip of tabs over the panels (ADR-0006 note 7): it scrolls sideways, lists every panel at
its end, has a menu per tab, pins, moves and reopens, and each of these is a client verb the line runs too
(`client/src/shell/panelVerbs.ts`). Only the active panel's page is mounted, so the screen's cost does not grow with
the panels open. The owner chose on 2026-11-11 that each page keeps its own screen state by panel and shows it again,
in a pull request after ADR-0016's step 5; until then a page hidden and shown again is drawn anew (built on
2026-11-12: ADR-0006 note 10). Which panels are open
is the browser's, not a setting (ADR-0017 §1). Today the panels go when the page is reloaded.

**What was asked.** To see two panels at once (an XVA page beside its Run, two Rates workspaces), to float one over the
others, and, as Bloomberg's Launchpad does, to put parts of a page in windows of their own across monitors and link
them by colour so that they follow one another. The guidelines already name a desktop with two screens beside a
laptop, a tablet and a phone (docs/design-guidelines.md §1; UX02, UX03).

**What constrains it.**

- The AI calls capabilities, never page automation (CLAUDE.md; ADR-0007 §3, which refuses AG-UI frontend tools;
  ADR-0016 §3, where view verbs are the client's and not the AI's).
- Every action has a command (docs/design-guidelines.md §8); chords stay few (ADR-0006 §8). The browser takes Ctrl+W,
  Ctrl+Tab and Ctrl+1 to 9, and F6 moves to its address bar. Keyboard, mouse and touch are each acceptance
  requirements (CLAUDE.md).
- The shell has a fixed height (ADR-0009). The AI pane takes 340px beside the panels from `sm` and 400px from `lg`
  (`client/src/App.tsx`), so the panels' space is the window less the pane while it is open.
- A page's responsive layout reads the window today: the pricer's three sections below `sm` (ADR-0010 §1:
  `XvaWorkspacePanel.tsx`'s section switch, `XvaResult.tsx`'s full-height button, `xva.css`) and the trace view's
  narrow layout (`TraceView.tsx`, `trace.css`).
- Nothing may be quietly wrong or quietly rearranged (CLAUDE.md, "No fallback that can make a value quietly wrong").

**Libraries considered** (npm, 2026-11-12):

| Library | Version | Licence | In-window floating | Pop-out windows | Notes |
|---|---|---|---|---|---|
| dockview-react / dockview / dockview-core | 8.9.0 (2026-11-08) | MIT | yes | yes | No dependencies; React 16 to 19; serialises its layout; pointer drags on touch |
| flexlayout-react | 0.11.1 | MIT | no | yes | |
| rc-dock | 4.1.0 | Apache-2.0 | yes | yes | Depends on lodash, classnames, rc-new-window and three `@rc-component` packages |
| golden-layout | 2.6.0 (2022-09) | MIT | no | yes | No release since 2022 |
| react-mosaic-component | 7.2.1 | Apache-2.0 | no | no | Tiles only |

dockview ships a separate package, `dockview-enterprise`, under a commercial licence (the Dockview Software Licence
Agreement, v1.5). Its modules are keyboard docking and navigation, layout undo, pinned tabs, recently-used order,
snapping guides, auto-hiding edge groups, tabs on several rows and overflow menus. Splits, floating areas, pop-out
windows, maximising, serialisation, tab drag by mouse and by pointer events on touch, and the hooks for the tabs and the
header's actions are in the MIT packages. dockview's splitters take the pointer only. A floating area has a title bar
of its own above its tab strip by default (`floatingGroupDragHandle: 'titlebar'`), which moves it.

**What a spike showed** (2026-11-12, qube at 8558df0 with dockview-react 8.9.0 and its own tabs, the client on Vite's
dev server against the host, headless Chromium at 1440×900; the spike is not committed):

- dockview drew the store's panels, the XVA, Rates and Pricing DB pages among them, split side by side and floating.
  ExGrid's grids settled in both; arrow keys moved a floating grid's Focus; the command line opened from a floating
  area with Ctrl+K.
- A panel moved to another area, floated, or popped out was not mounted anew: the pricer's inner tab (CSA) stayed
  chosen and a grid's Focus stayed on its cell.
- Popped out, the page and its grid were drawn and settled in the second window, and closing that window brought the
  panel back to where it had been. Three things were wrong there: the window lacked Mantine's colour-scheme attribute,
  so controls lost their colours; a Mantine Select opened from it listed its options in the main window; and Ctrl+K
  in it opened nothing. dockview copied 2,521 stylesheets into the window under Vite (the main window has 11).
- Read in the code, not seen: `@ex-grid/core` 0.2.0 (`observeDevicePixel`) reads the Device Pixel from the global
  `window`, the opener's, so a grid in another window would follow the opener's scale (ex-grid #565). One scale was all
  the headless spike had.
- Not tried: a drag with a finger, and the shell's own Mantine tabs in dockview's headers.

## Decision

### 1. Areas and the arrangement

- An **area** is a group of panels under one tab strip, showing one of them. The window holds areas side by side or
  one above another, **floating** areas over them, and, from step 2 (§9), areas in **other windows** of the browser.
- The **arrangement** is where each area is, its size, and which panels it holds in which order. **Reading order** is
  the window's areas in the order of their splits (in each split, the left or upper part first, all of it before the
  other part), then the floating areas in the order they were floated, then other windows' areas in the order they
  were opened.
- The **active panel** is one across every area and window: the panel the user last pressed in, focused, opened or
  showed. A panel the AI opens is not made active (ADR-0007 §7).
- Each area's strip is the strip of ADR-0006 note 7: its tabs, its list of panels, its tab menu, pinned panels first,
  and the same verbs. A verb that names "the others" or "to the right" means those of the panel's own area, and
  `move panel <left|right|first|last>` moves the panel within its area.

### 2. dockview places the panels; the workspace store says what each shows

- The arrangement is drawn and held by dockview, from the MIT packages only: `dockview-react`, `dockview` and
  `dockview-core`, each a direct dependency at exactly 8.9.0. `dockview-enterprise` is never added: it is commercial,
  and what of it the workbench needs (pins, the recently-used order, a keyboard path) is the shell's own already.
- What a panel shows (its page, title, number, pin), the panels last shown and the panels closed stay the workspace
  store's, as in note 7. Which area holds a panel is dockview's alone: the store does not copy it, and the verbs read
  and change it through one port of the shell's, which a test fakes, so a verb is tested without a browser and dockview
  could be replaced without changing one.
- Each area's tabs, their menu and its list are the shell's components, drawn with Mantine through dockview's hooks
  for a tab and for a header's actions. dockview keeps the page's content where it moves it, whatever draws the tabs;
  step 1 checks that again with these tabs. dockview's own context menu and theme are not used: its colours, borders
  and sizes are set from the shell's tokens (docs/design-guidelines.md), in both schemes.

### 3. The verbs and the gestures (step 1)

Each is declared once in `panelVerbs.ts`, as note 7's are, and the line, a tab's menu, the list and a gesture run that
declaration. The words are settled in step 1's spec:

| Words | What it does |
|---|---|
| `split panel <right\|left\|down\|up>` | Moves the panel into a new area on that side of its own. Refused, saying why, where it is the only panel of its area |
| `send panel to <title>` | Moves the panel into the area of the panel named, after it |
| `float panel <centre\|left\|right\|top\|bottom>` | Moves the panel into a new floating area in the middle of the panels' space or over that half of it; given to a floating panel, places its area there |
| `dock panel` | Puts a floating panel (from step 2, a popped-out one) back among the window's areas: into the area it came from where that is still there, else into a new area at the right where the room holds one, else into the last area, and says where |
| `size panel <percent>` | Gives the panel's area that share of its row or column, within §5's minimum. Refused for a floating panel, which `float panel <where>` sizes |
| `maximize panel`, `unmaximize panel` | Gives the panel's area the whole of the panels' space, and back |
| `gather panels` | Puts every panel back in one area, in reading order: the arrangement of today |
| `arrange <n>`, `arrange back` | Applies the AI's proposal numbered `n`, and puts back what it replaced (§8) |

*Note of 2026-11-12, as step 1 was built.* Two rows changed, both DEFAULTs put to the owner with step 1's pull
request. `float panel` was `float panel [<left|right|top|bottom|centre>]`, its place optional with the middle the
default: the line takes an optional value only by its name (ADR-0006), so `float panel left` was refused, and the words
a menu shows beside Float ▸ Left were words the line did not run. Its place is now required, as `split panel`'s side
is. `dock panel` put the panel "beside the area it came from where that is still there, else at the right": that read
as a new area beside it, which undoes nothing; it now goes back into that area, as one of its tabs, which is where the
float took it from. Where that area has gone, it goes into a new area at the right where the room holds one more area
at §5's minimum, else into the last area, and says which.

Three readings were settled as it was built, also DEFAULTs put to the owner with that pull request. A floating area is
one area: `split panel` is refused in it, and a drop onto its edge is not offered, so a panel is docked first and split
in the window. Where a floating panel came from is kept by the browser tab with the arrangement (§7), and is read from
dockview's own move, so a float by the pointer or by Shift is docked back as one by the line is. And of §11's step 1,
the pricer's part (its section switch, its full-height button and `xva.css` reading its area, ticket 07's rest) is a
pull request of its own after ADR-0016's step 5d, which changed the same files (5d merged first, as #33): until that
pull request, the pricer read the window. In an area shorter than its parts need, its grids still scroll under its
result dock, as they do in a window that short. With 5d, ADR-0016's step 5, which §11's step 3 waits for, is built. The pricer's pull
request followed step 1's the same day, so the pricer reads its area's width and height; it also writes a page's
container queries in pixels, since a container query's `em` is the page's 13px, not a media query's 16px, and in the
range syntax (`width < 768px`), since an area's width is fractional. With it, a trace's roots and a declarative
view's columns read their panel too; a dialog, a row's draft among them, still reads the window, since it is drawn
over the window and not in an area.

Two sentences of §4 are read so, DEFAULTs put to the owner with that pull request too. While the panels stand in one
area, no area is framed: its selected tab already says which panel the line acts on, as in the strip, and the frame
comes with a second area. And `reopen panel` says where it put the panel where the area it stood in has gone, and,
back in that area, only where there is more than one area: in one area, or in the strip, the panel is where it is seen,
and a panel closed while the panels were one strip stood in no area, so it opens beside the active panel, as any panel
does.

- **The pointer.** A tab dragged onto an area's edge splits it there, into another strip joins that area, and with
  Shift held floats. A floating area moves by its title bar (dockview's own, above its tab strip) and is resized by its
  edges; a splitter is dragged.
- **The keyboard.** The line and the menus reach every verb, and no chord is added (ADR-0006 §8). `next panel` and
  `previous panel` go through every panel in reading order, and `show panel` reaches any by its title. A splitter is a
  focusable separator that the arrow keys move by 24px (WAI-ARIA), as the pricer's own handle is
  (`client/src/apps/xva/layout.tsx`); the shell adds this, since dockview's take the pointer only. A floating area's
  title bar is a focusable handle that the arrow keys move by 24px and Shift with the arrows resizes.
- **Touch.** The tab menu and the list hold every verb above (note 7: a touch screen has no right click), so nothing
  needs a drag: `send panel to` lists the other areas by the panel each shows, `size panel` offers 25, 33, 50, 67 and
  75%, and `float panel` its five places. A splitter and a floating area's title bar and edges are at least 24 by
  24px to a finger (docs/design-guidelines.md §8). dockview drags a tab by pointer events on touch; step 1 tries it
  on a touch device before saying so.
- They act on the screen only: they are the client's, not logged, not `undo`'s, and not the AI's, as note 7's are.

### 4. The active panel, and where panels open and go

- The line acts on the active panel (ADR-0006 §5), so the screen shows which it is: its area's frame and its tab take
  the accent while other areas are framed in the neutral border. The line names it as today.
- A panel the user opens, by a command, a button or a Run, opens beside the active panel in its area (note 7) and
  becomes active.
- Closing a panel shows the panel its area showed before it. An area left empty goes, and the panel shown before it in
  any area becomes the active one. `reopen panel` puts a panel back in the area it was in, where that is still there,
  else in the active panel's area, and says which.

### 5. Widths: the panels' space and an area's own width

These read the **panels' space**, the window less the AI pane while it is open.

- **Below 768px** there are no areas: one strip of every panel, note 7's, the pinned panels first and the rest in
  reading order, and the verbs that would arrange are refused, saying that the arrangement comes back with more room.
  The arrangement is kept: in the strip, note 7's `move panel` and a drag move a panel within its own area, which is
  where it stands when the areas show again.
- **An area is at least 320px wide and 240px high**, the narrowest phone a page is laid out for. Two areas side by side
  fit from 768px, where areas begin, three from 960px, four from 1280px. A split, a size or a proposal that would make
  one smaller is refused, saying the room it needs.
- **A floating area** is at least that size too, and is shown inside the panels' space: where the space has narrowed
  under it, it is drawn moved inside, and its own place is kept for when there is room.
- **A panels' space too small for the arrangement** (its areas narrower than their minimum, as when the AI pane opens
  on a laptop beside three areas) shows the one strip, and the arrangement as it was when there is room again. Nothing
  is merged into fewer areas by itself: that would rearrange what the user arranged. Each change between the strip and
  the areas draws the pages in view anew, and each page takes back the screen state its panel kept (ADR-0006 note 10,
  since 2026-11-12).
- **A page lays itself out by its area's width, not the window's** (amends ADR-0010 §1 and docs/design-guidelines.md
  §9, above). The pricer's three sections, its full-height button, the trace view's narrow layout and every rule that
  reads `sm`, `md` or `lg` read the width of the panel they are in: a CSS container query, and for a hook the panel's
  width as the browser reports it when it changes. An XVA page in a 600px area is a narrow XVA page, as it is in a
  600px window today.

This differs from what the owner was told on 2026-11-12 ("at most two side by side up to 1199px, floating from
1200px"), which read the window. The AI pane takes 340 to 400px of it, so by the window alone a 1280px laptop with the
pane open would have had two areas of 440px or three of 293px, under the narrowest a page is laid out for. Read by the
panels' space instead:

- with the pane shut, two areas fit from 768px as told, but three fit from 960px, where the owner was told two at
  most up to 1199px;
- with the pane open, areas need a window of 1108px (768 + 340), so a tablet or a small laptop shows the strip while
  the pane is open;
- a floating area is allowed wherever areas are, where the owner was told from 1200px.

### 6. What is mounted

Each area mounts the page of the panel it shows, and no other (note 7). The screen's cost grows with the areas in view,
not with the panels open. A panel moved between areas, floated or popped out keeps its page mounted, as the spike saw.

Step 1 measures three areas each showing an XVA page of 10,000 trades, at 1440px: the elements on the page, and how
long choosing another tab of one area takes, beside the same with one area. It passes when the elements are the three
pages' own and one tab per open panel, and nothing more for each panel open. The times are written in
`IMPLEMENTATION_STATUS.md` and put to the owner with the pull request; they gate nothing, as note 7's 150ms is an
indication from one run.

### 7. The arrangement is kept by the browser tab

- The arrangement, with the panels and what each shows, is kept by the browser tab (its session storage, under the
  user's id) and restored when that tab reloads: ADR-0017 §1 makes which panels are open the browser's, and an
  arrangement takes effort to make. Two tabs of the workbench keep their own, and neither takes the other's on a
  reload; a new tab starts with no panels, as today. The owner was told "kept per browser"; kept for the tab, a reload
  cannot quietly bring back another tab's arrangement. Keeping one after the tab is closed is the last point below.
- A restored panel is a page opened anew: its workspace or Run is read from the server. One that is gone says so in its
  panel, with its title, and is not dropped from the arrangement.
- A stored arrangement this version cannot read is not applied: the shell says so and starts with one strip.
- Named arrangements kept across devices ("my morning layout") would be saved definitions (C32, ADR-0017 §1). OPEN.

### 8. The AI proposes an arrangement, and the user applies it

**CONFIRMED** (the owner, 2026-11-12): "it is enough that it proposes, and the person applies it with a button or a
command". The AI never moves, floats, places or closes a panel itself. How, below, is DEFAULT: the capability, the
card with its Apply button and its command, and the one step back were told to the owner the same day; the rest is
new here.

- **The proposal is a declarative arrangement**, validated, never code: areas split by a direction with each one's
  share, floating areas with their place, and in each area the panels it holds and the one it shows. An open panel is
  named by its id with its title, as the context gave them. A page not open is named by what the shell opens today: a
  Run by its id, or an open-able application page (the Pricing DB; a workspace that exists, by its id). A proposal
  makes nothing: no workspace, no Run.
- **The AI makes it through a capability that changes nothing**, `layout.propose`, in the kernel, with no domain word.
  It is offered to the agent as a tool as every registered capability is (ADR-0007 §3), like the three of ADR-0016 §7
  that write nothing. It checks the arrangement's shape (each share, place and minimum, one shown panel per area, no
  panel twice) and answers the arrangement. It writes no workspace and nothing in an edit log. Whether the panels it
  names are open is checked where it is applied, in the browser that has them. The pane's context gains the open
  panels, their ids, titles, what each shows and their arrangement, as a description (ADR-0007 §8), so the agent can
  name them.
- **The AI pane's card draws it** as a small plan of boxes with the panels' titles, with a button, **Apply**, and the
  line's words for it, `arrange <n>`, where `n` is the proposal's number on the card, counted for the page's life.
- **Applying is one step**, the user's: the arrangement is made as proposed, and the pages it names that are not open
  are opened in their places. Open panels it does not name go to the first area in reading order, and the result says
  so ("2 panels the proposal did not place are in the left area"). A proposal that names a panel closed since, or whose
  title has changed, is refused by name and not applied in part. Under §5's minimum it is refused, saying the room it
  needs; with the AI pane open, closing the pane may give it.
- **`arrange back`** puts back the arrangement the last `arrange` replaced, once, and closes the pages that `arrange`
  opened, which `reopen panel` brings back, saying so.

### 9. Step 2: other windows of the browser

`pop out panel` moves a panel into an area of its own in a new browser window, for a second monitor. It is run from a
key or a press, so the browser allows the window. Before it ships, each of these holds, checked in a real browser:

- The window has the scheme and the stylesheets of the main one, and follows a change of scheme.
- A menu, a select, a tooltip or a dialog opened from a panel in that window opens in that window.
- The command line, `/`, the shell's chords and the AI's `ai` work from that window, on its panels.
- ExGrid reads that window's Device Pixel and media (ex-grid #565 is fixed and taken in).
- A window the browser blocks or closes leaves its panels in the main window, where they were, and the shell says so.
- On reload, an arrangement with other windows comes back with their panels docked in the main window, saying so,
  because a browser opens a window only on a key or a press; `pop out panel` sends them out again.

The AI pane stays in the main window.

*Note of 2026-11-13, as step 2 began* (the owner: "前者は始めていいよ"; spec
[panels-2-windows](../specs/panels-2-windows/spec.md)). How the points above are read, each a DEFAULT put to the owner
with step 2's pull request:

- **A window of its own is one area**, as a floating area is (§3's note of 2026-11-12): `split panel`, `float panel`
  and `maximize panel` are refused for a panel in it, saying to dock it first, and `size panel` saying that the window
  sizes it (not §3's words for a floating area, which point to `float panel <where>`: no verb sizes another window, the
  user does, as any window). `send panel to` moves a panel between windows either way, `gather panels` run from the
  main window brings every panel into that area (and is refused in another window), and a window whose last panel
  leaves it closes. `pop out panel`, and `send panel to` into another window or a floating area, are refused where
  they would leave no area docked in the main window, as `float panel` is. An area's only panel takes its whole area
  out, and dockview keeps the area's place, hidden, for it to come back to; a panel beside others leaves them its
  area.
- **Back where it was.** `dock panel` puts a panel back into the area it came from (kept with the area a floating
  panel came from), or into the place a whole area left, else by `dock panel`'s rule of §3. (A window's last panel
  goes back into that place with its window's area: dockview takes an emptied window's place with it as the window
  goes, before a panel moved out of it arrives.) A window the user closes has its panels put back by dockview:
  into the area they came from where it is there, else into an area of their own, and the shell says which. A reload
  brings them back into that area where it is there, else into the last area of the main window, and says which: the
  window's own place is not reopened, since only a key or a press opens one.
- **The strip closes the other windows.** The areas, dockview with them, are drawn only while the main window shows
  areas (§5), so the main window showing the one strip closes the other windows too, and their panels join the strip,
  said so; the areas drawn again have them in the main window, as after a reload. Keeping a window open beside the
  strip would need dockview drawn without its areas, which §5 does not have.
- **"Opens in that window"** is read for what a page draws (its menus, selects, tooltips and popovers, by Mantine's
  portal target for that page) and for the shell's dialogs and the command line, which open in the **window in use**:
  the one the user last pressed in, focused or typed a key of the shell's in, or the one a panel was just popped out
  into, kept while a dialog is open; in the main window a press and the window's own focus say it, since the shell
  moves the keyboard inside it itself. The notices are said in the window in use too, one at a time as in the main
  window. A panel's menus keep inside the window they open in and scroll there, since another window is often shorter
  than the menu of every verb. The active panel is one the window in use shows (there, the one it shows; in the main
  window, the one active there last), so the line, `/`, the chords and `ai` act on a panel of the window they are
  typed in.
- **What Mantine hears on the main document only** (a press outside a popover, a dialog's Escape) is told to it from
  the other windows as a copy of the event. A dialog's Tab is kept inside it in its own window instead: Mantine's focus
  trap walks up from the dialog to the main document's body, which a dialog in another window is not under. The
  shell's own keys are attached to every window, so no other key of another window reaches the main document.
- **`arrange` and `arrange back`** act on the main window (§8). Applying a proposal draws the arrangement anew, which
  closes the other windows; their panels are placed as the proposal says, or join its first area as a panel it does
  not place does. `arrange back` puts back what it replaced with those windows' panels in the main window, where their
  areas stood, and says so, as a reload does: a window is opened again by `pop out panel`. The AI's context of the
  panels says of each area whether it is docked, floating or in another window.
- **ExGrid** reads a grid's window once, as the grid is created (ex-grid ADR-0090, #565, in 0.3.0-rc.1), so a page
  moved into another window, or back, is drawn anew there and takes back its panel's screen state (ADR-0006 note 10).

### 10. Step 3: parts of a page, and links (OPEN in its details)

- Views that are already panels (a Run's views, the Pricing DB, a calculation trace, a data view) split, float and pop
  out whole from step 1. Step 3 is about a page that holds several parts.
- Such a page may declare **parts** that can each be a panel: the pricer's Inputs, Trades and Results, the three
  sections it already has on a phone (ADR-0010 §1). Rates' parts are to be named when step 3 starts. Every part of one
  workspace reads that workspace (ADR-0003 §1), so they agree revision by revision. The screen state the parts share
  (the selection given to commands, the Run in view, the Advanced side) moves from the page to a store for that
  workspace.
- **Links**, as Bloomberg's colour groups: panels given one colour follow the context one of them changes. What
  follows what (a counterparty, a value date, the Run in view) is the owner's to say when step 3 starts. The parts of
  one workspace are linked by the workspace itself, with no colour.

### 11. Order of delivery

**CONFIRMED** (the owner, 2026-11-12): step by step, each a pull request, in this order. What each waits for is
DEFAULT.

1. In the window: areas, splits, floating, maximising, the verbs and gestures, widths read by area, the arrangement
   kept by the tab, and the AI's proposal. Built after this ADR is accepted, on main, which has had note 7's tabs since
   #29. It changes the pricer's files (the section switch, the full-height button,
   `xva.css`) last, after the pull requests open on them at that time are merged: ADR-0016's step 5 and note 7's
   per-panel page state, which changes the same section switch. Whichever of step 1 and that page-state pull request
   comes second is built on the first.
2. Other windows (§9).
3. Parts and links (§10), after ADR-0016's step 5, which is changing the pricer's page now.

## Consequences

- The shell draws the panels in two ways from one store: dockview's areas, and note 7's single strip where §5 says so.
- `panelVerbs.ts` gains the verbs of §3 and the port they act through.
- Pages move their breakpoints from media queries to container queries (`xva.css`, `trace.css`, the pricer's section
  switch and full-height button, `TraceView.tsx`), so a page reads alike in an area and in a window of that width.
- Layer 3 gains the arrangement: split, float, size and dock by verb, by drag with a mouse and with a finger, by the
  keys of a splitter and a title bar, the panels' space at 390, 768, 992 and 1440px with the AI pane shut and open, in
  both schemes, and §6's three XVA pages.
- docs/design-guidelines.md gains areas: frames, the active panel's accent, minimum sizes, the floating title bar, and
  §9's container queries.

## Rejected

- **The AI arranging the screen itself** (the owner, 2026-11-12): it would be page automation, and the arrangement is
  the user's.
- **dockview-enterprise's keyboard docking (Ctrl+M) and layout undo.** Commercial, and the line already reaches every
  verb without a chord. `undo` stays the workspace's (ADR-0016 §11); `arrange back` is the one step back a proposal
  needs.
- **Mounting the hidden panels of an area.** Note 7 tried it: a hidden page took the keyboard from a dialog, and a
  hidden Rates page would have kept its live frames running unseen.
- **Fitting the arrangement to a smaller space by itself** (merging areas, folding floats into the grid).
- **Window widths for the rules of §5**: the AI pane's 340 to 400px would count as room for areas, and pages would be
  drawn narrower than they are laid out for.
- **flexlayout-react, golden-layout, react-mosaic-component**: no floating area in the window; golden-layout has had
  no release since 2022. **rc-dock**: six dependencies, lodash among them, for what dockview does with none.
- **Building the docking ourselves**: splitters, drop targets, floating windows, pop-outs and serialisation are a
  library's worth of work that the composition kernel does not need to own.

## Open

- The verbs' words (§3), settled in step 1's spec.
- Named arrangements across devices (§7), as saved definitions.
- Rates' parts and the links (§10), when step 3 starts.
