# Panels in areas, step 1: split, float and maximize inside the window, and the AI's proposal

Status: ready-for-agent (2026-11-12)

The decisions are [ADR-0018](../../adr/0018-panels-are-arranged-in-areas-that-split-float-and-pop-out-and-the-ai-only-proposes-an-arrangement.md),
accepted by the owner on 2026-11-12, and the decisions it builds on: ADR-0006 note 7 (the panels' tabs and their verbs),
ADR-0007 §7 and §8 (the AI pane, its card and its context), ADR-0009 (the design guidelines), ADR-0010 §1 (the pricer's
three sections below `sm`) and ADR-0017 §1 (which panels are open is the browser's). This spec is ADR-0018 §11's step 1.
It settles the verbs' words (ADR-0018's first Open item) and names the seams; both are DEFAULTs, reported to the owner
with the pull request, per his standing preference of 2026-11-08.

## Problem Statement

A desk user can see one panel at a time. Pricing beside the Run it made, two Rates workspaces side by side, or a Pricing
DB kept in a corner while a page is worked on, all mean switching tabs. The owner asked on 2026-11-12 for floating and
docking as Bloomberg's terminal has it, and decided that the AI may only propose an arrangement, which he applies.

## Solution

One pull request (ADR-0018 §11, step 1).

- **Areas.** From a panels' space of 768px (the window less the AI pane), the panels are drawn by dockview 8.9.0 in
  areas: one at first, holding every panel under note 7's strip. A tab dragged to an area's edge splits it, onto
  another strip joins that area, and with Shift floats it. Below 768px, or where the arrangement does not fit, note 7's
  one strip is drawn as today, and the arrangement comes back with the room.
- **Verbs**, declared once in `client/src/shell/panelVerbs.ts`, for the line, the tab's menu, the area's list and the
  gestures. Note 7's verbs act within the panel's area.
- **Widths.** An area is at least 320 by 240px; a page lays itself out by its area's width.
- **Kept by the browser tab.** The panels, what each shows and the arrangement come back on a reload of that tab.
- **The AI proposes.** `layout.propose` answers an arrangement and changes nothing; the AI pane's card draws it with
  Apply and `arrange <n>`; `arrange back` undoes the last.

## The verbs' words (settles ADR-0018's first Open item)

| Line | Menu (tab, area list) | Refused when |
|---|---|---|
| `split panel <right\|left\|down\|up>` | Split ▸ Right, Left, Down, Up | the panel is its area's only one; either half would be under 320×240px; areas are not shown |
| `send panel to <title>` | Send to ▸ one item per other area, named by the panel it shows | there is no other area; the title names a panel of its own area |
| `float panel [<left\|right\|top\|bottom\|centre>]` | Float ▸ Centre, Left, Right, Top, Bottom | the panel is its area's only one and that area is the only one in the window (nothing would stay docked) |
| `dock panel` | Dock | the panel is not floating |
| `size panel <percent>` | Size ▸ 25%, 33%, 50%, 67%, 75% | the panel is floating, or alone in its row or column; the share would put an area under its minimum |
| `maximize panel` / `unmaximize panel` | Maximize / Restore | the window has one area; nothing is maximized |
| `gather panels` | Gather all panels here | there is one area already |
| `arrange <n>` / `arrange back` | the card's Apply | no proposal `n`; nothing to put back |

`<title>` takes what `show panel` takes. `<percent>` takes a whole number from 10 to 90, with or without `%`. Every
verb but `arrange` is refused below 768px or where the arrangement does not fit, saying the arrangement comes back with
more room. Each is the client's: not logged, not `undo`'s, not the AI's.

## User Stories

1. As a desk user at a laptop, I drag a Run's tab to the right edge of the XVA page and see both side by side.
2. As a desk user, I type `split panel right` and the active panel moves into a new area on the right.
3. As a desk user, I float the Pricing DB over the page I work in, move it by its title bar, and dock it again.
4. As a desk user, I maximize one area to read a wide grid, and restore the others.
5. As a desk user, I see which panel the line acts on: its area is framed in the accent.
6. As a desk user, a panel I open opens beside the active panel, in its area.
7. As a desk user, closing the last panel of an area removes the area, and the panel I showed before becomes active.
8. As a desk user on a tablet, I reach every verb from the tab's menu or the area's list, without a drag.
9. As a desk user with a keyboard, I move a splitter with the arrow keys and a floating area with its title bar.
10. As a desk user, I open the AI pane on a laptop beside three areas; the panels show as one strip until I close it,
    and my three areas are back.
11. As a desk user on a phone, I see note 7's one strip, and my arrangement waits for a wider screen.
12. As a desk user, I reload the browser tab and my panels and their arrangement come back; another tab keeps its own.
13. As a desk user, I ask the AI to put two panels side by side; its card shows the plan with Apply and `arrange 1`;
    I apply it, and `arrange back` puts my arrangement back.
14. As a desk user, the XVA page in a 600px area is the narrow XVA page, its three sections switched as on a phone.

## Implementation Decisions

- **Two drawings, one store.** `Panels` measures the panels' space (`.qube-panels`, which cancels the shell's padding so
  that its width is the window less the AI pane) and draws either note 7's strip (`PanelStrip`, today's code) or the
  areas (`PanelAreas`, dockview). The workspace store keeps what each panel shows, the active panel, the panels shown
  last and the panels closed (ADR-0018 §2). In the strip its order is the strip's; while areas are shown it is set from
  dockview's reading order, the pinned first, so the strip shows that order when it is drawn again.
- **The port.** The verbs read and change the arrangement through `Arrangement` (`client/src/shell/arrangement.ts`):
  the areas in reading order, each area's panels, its size and whether it floats, its row or column, the room, and the
  changes of §3. `DockArrangement` implements it over dockview's API; the strip's implementation refuses what needs
  areas. A verb is tested against a fake port, as `panelVerbs.test.ts` tests note 7's verbs against the store.
- **The kept arrangement** is dockview's serialised layout, the last one taken while it fitted. It is what the strip
  waits with and what the browser tab keeps. Drawn again, it is first made to agree with the panels open
  (`reconciled`, a pure function): a panel closed since is taken out and an area left empty goes; a panel opened since
  joins the area of the panel before it in the strip; each area's panels follow the strip's order.
- **What fits** is read from the kept arrangement's tree (`needs`, a pure function): an area is 320 by 240px; areas
  side by side add their widths, one above another their heights. Floating areas always fit, drawn inside.
- **Mounting.** dockview keeps a panel's React tree for its life; the page is drawn only while its panel is the one its
  area shows (ADR-0018 §6), read from dockview's visibility, so a move between areas keeps the page mounted.
- **Tabs.** Each area's tabs are dockview's (`role="tab"`, roving focus, arrows, Home, End, Enter) with the shell's
  tab inside: the title, the pin, the close button, and note 7's menu on a right click, Shift+F10 or the menu key. The
  area's header actions hold note 7's list button, listing that area's panels and the shown panel's verbs. dockview's
  own Delete and Backspace close through `close panel`; its context menu and overflow list are not used.
- **Theme.** dockview's colours, borders, tab height and title bar are set from the shell's tokens in `shell.css`, in
  both schemes. The active panel's area is framed in `signal`; others in the default border.
- **Keyboard on splitters and title bars.** The shell makes each splitter a focusable `separator` whose arrows move it
  by 24px (`size panel` arithmetic, through the port), and each floating title bar a focusable handle whose arrows
  move the area by 24px and Shift with the arrows resize it.
- **Kept by the tab** (`client/src/shell/keptPanels.ts`): `sessionStorage` under `qube.panels.v1:<user id>`, written as
  the panels or the arrangement change. A stored value this version cannot read is not applied, and the shell says so.
- **`layout.propose`** is a kernel capability with a new effect, `Propose` (offered to the agent, writes nothing). Its
  request is flat (notes 12 and 37 of ADR-0016): a list of areas (`area`, `beside`, `side`, `share`, `float`) and a
  list of places (`area`, `panel` with `title`, or `run`, or `app` with `workspace`, and `shown`). It checks the shape
  and answers it with a number of areas. The pane's context gains `qube.panels`: each open panel's id, title and what
  it shows, and the areas holding them. The scripted agent understands "side by side" and "beside" for the panels the
  context names.
- **The card** draws a proposal as a small plan of boxes with the panels' titles, an Apply button and `arrange <n>`,
  numbered for the page's life. Apply and `arrange <n>` are one verb.

## Testing Decisions

- Vitest: the verbs over a fake port (each refusal, each change, note 7's verbs scoped to an area); `reconciled`,
  `needs`, a proposal made into a layout and refused by name; the store's order and its closing in an area; the kept
  value read and refused.
- xUnit: `layout.propose` takes a well-formed proposal and refuses each malformed one by path; the scripted request.
- Playwright (`e2e/areas.spec.ts`): split, float, size, dock by verb; a drag with the mouse to an edge and with Shift;
  a drag with a finger; a splitter's and a title bar's keys; the panels' space at 390, 768, 992 and 1440px with the AI
  pane shut and open, in both schemes; a reload of the tab; the AI's proposal applied and put back; the XVA page by its
  area's width. Note 7's specs keep passing in the strip and in one area.
- ADR-0018 §6's measurement, three XVA pages of 10,000 trades at 1440px, written in `IMPLEMENTATION_STATUS.md`.

## Out of Scope

- Other browser windows (ADR-0018 §9, step 2) and parts of a page with links (§10, step 3).
- Named arrangements across devices (§7's Open item).
- The per-panel page state (note 7's own pull request).
