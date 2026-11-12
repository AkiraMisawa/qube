# Design guidelines for the workbench client

How the React client looks and is laid out, and why. The decision to keep these guidelines, and the few choices in
them that were made rather than found, is [ADR-0009](adr/0009-one-control-height-a-fixed-shell-and-design-guidelines.md).
The theme they describe is `client/src/theme.ts`, and the shell's own layout classes are `client/src/shell/shell.css`.

**MUST** is a rule a change follows or changes this document first. **SHOULD** is the default, and a departure
says why in the pull request. Each rule names its reason, and its source where it came from outside this repository
([References](#references)).

## 1. What we are designing for

The workbench is a keyboard-first front-office tool (ADR-0002) used for hours at a time by people who know it well,
on a desktop with two screens and also on a laptop, a tablet and a phone (UX02, UX03). It shows money and risk.

Four principles follow, and every rule below is one of them made concrete.

1. **Density is value per time and space, not pixels per square inch.** A screen is dense when the user gets more
   done for the room and time it takes, not when it is crowded ([Strom](#references)). Every mark earns its
   place: a border, a colour, a badge or a line of help that carries no information is removed, not styled
   ([Tufte, via Strom](#references)). Dense never means cramped or misaligned: a crowded screen is read slowly.
2. **One system, not per-component taste.** Heights, spacing, type and colour come from the theme. A component that
   needs a value the theme does not have is a question for the theme, not a literal in the component. Most visual
   defects in this client so far were two components each choosing a size that looked fine alone.
3. **Familiarity outranks novelty.** Expert users build habits; a control that moves or changes shape costs them
   more than it pays. Change the shell rarely and on purpose ([Bloomberg](#references)).
4. **Never quietly wrong.** A figure that is simulated, stale or unavailable says so on screen, in words, not only
   in colour. A blank is blank, never zero (ADR-0003, slice 01).

## 2. The theme's scales

| Scale | Values | Use |
|---|---|---|
| Spacing | `xs` 4 · `sm` 8 · `md` 12 · `lg` 16 · `xl` 24 px | Gaps and padding. A 4px grid, the base unit of Fluent and Carbon ([Fluent 2](#references)). |
| Font size | `xs` 11 · `sm` 12 · `md` 13 · `lg` 15 · `xl` 18 px | `md` is body text, `sm` dense labels and table text, `xs` captions and hints. |
| Control Height | 30px (Mantine `xs`) in rows; 36px (`sm`) in dialogs | See §3. |
| Radius | `xs` (2px) | Square enough to tile; nothing in the workbench is a pill except a Badge, and the account menu's avatar, a circle as a person is drawn everywhere (2026-11-11). |
| Colour | `signal` (amber, primary), `ink`, `dark` | See §7. |

- **MUST** take spacing, font size, radius and colour from the theme (`gap="sm"`, `c="dimmed"`,
  `var(--mantine-spacing-sm)`). A literal (`gap={6}`, `#ffa218`, `margin-top: 3px`) is allowed only where the
  value is geometry the theme has no name for, with a comment that says what it is (the header's row arithmetic is
  the example).
- **MUST NOT** fix an alignment with a nudge (`mt={2}`, `top: 1px`). A nudge is right at one font size and wrong at
  the next; make the two things the same height instead (§3, §4).

## 3. Control Height and density

**Control Height** is the outer height of an interactive control: a button, an input, a select, an action icon.

- **MUST** give every control in one row the same Control Height. Mantine's sizes are not one scale across
  components: `Button size="compact-xs"` is 22px, `ActionIcon size="sm"` 22px, `TextInput size="xs"` 30px. Mixing
  them in a row is exactly how the header's AI and theme buttons ended up off the Region and As-of inputs (§9).
  Design systems that pair fields and buttons say the same: keep one field height on a surface
  ([Carbon](#references)).
- **Rows of controls** (the header, a panel's toolbar): Control Height 30px.

  | Component | Prop |
  |---|---|
  | `Button` | `size="xs"` |
  | `TextInput`, `Select`, `NumberInput`, `IsoDateInput` | `size="xs"` |
  | `ActionIcon` | `size="input-xs"` (Mantine's size that matches an input) |
  | `Switch` | `size="xs"`, centred on the row |

- **Dialogs and forms** (the run dialog, a new-workspace dialog): Control Height 36px (`size="sm"`), the label above
  the input. A dialog is read once and filled carefully; it is not a place to save pixels.
- **A row's dialog** (`DraftDialog`, ADR-0016 note 57) is a dialog as above, 64rem wide and the whole screen below
  `sm`. Its fields stand in one column, two from `xs` and three from `md`, a switch on the line of the inputs beside
  it. Cancel and Save stay in view under the form as it scrolls. A table under the row is a compact `QubeGrid` as
  high as its rows, up to eight, with no column menus. What a field takes is its placeholder, never a line under it.
  Escape and the close button ask as Cancel does, in every state of the dialog; a grid of the dialog hands on the
  Escape it has nothing left to dismiss with (`onLeave`). The keyboard never falls to the page: it stays in the
  dialog while it is open, and goes back to the grid behind it when it closes (`returnKeyboard`).
- **A row's button** (ADR-0016 note 57): a grid whose rows have a dialog has one Action Column beside the row's key,
  64px wide with no header, holding the one button `Edit`. A second button on a row is a decision, not a habit.
- **Inside content** (a resolution line under a grid, a card in the AI pane): `compact-xs` buttons (22px) are allowed
  where no input shares the row and the button sits in running text. They still meet the target size rule of §8 by
  their spacing.
- **Grids:** every grid is `QubeGrid` (`client/src/platform/grid/QubeGrid.tsx`), which is ExGrid drawn by
  its Mantine Wrapper `@ex-grid/mantine`, with `density="compact"`. **MUST NOT** render `ExGrid` from `@ex-grid/react`
  directly: it would miss the theme's colours and Mantine's menus. A row's height is ExGrid's, set through its
  parameters, never in CSS (ex-grid ADR-0013/0016): a CSS row height breaks its virtualisation arithmetic and
  selection overlay. The Wrapper sizes the grid's own Mantine controls from the density (`xs` for compact).

Why 30px: it is the smallest Mantine input height, it holds 12–13px text with room for a focus ring, it is the
size Carbon gives its "small" field for constrained space and long forms ([Carbon](#references)), and it already meets
WCAG's minimum target size (§8). Smaller controls would save little: the grids, not the controls, hold the density.

## 4. Alignment

- **MUST** align a row of controls on the controls, not on their labels.
  - **A toolbar** (a row whose purpose is to change something quickly) puts each label to the left of its input,
    `ContractFields layout="toolbar"` (`client/src/shell/ContractForm.tsx`). Every control is then one height and
    the row is centred.
  - **A form row** with labels above its inputs uses `Group align="flex-end"`, so buttons line up with the inputs'
    bottom edge, not the middle of label-plus-input. The Rates toolbar's Valuation date and its buttons are the
    example.
- **MUST NOT** let a message change a toolbar's height. A toolbar input marks itself invalid (red edge,
  `aria-invalid`); the message is said in the main area, as `ContextIssues` does for the header.
- **Shared edges.** The header, the panel tabs and the panels start at one left gutter, `spacing.sm` (8px). A new
  region starts at the same edge, not at its own padding.
- **Groups.** Controls of different scope are separated by space or a vertical `Divider` (the header separates the
  context from the shell switches). Controls of one scope are 8px apart (`gap="xs"`/`"sm"`); groups 12px or more.
  Proximity is the cheapest grouping there is ([Strom, design density](#references)).
- **Numbers.** Quantities are right-aligned, in tabular lining figures (`font-variant-numeric: tabular-nums`, set on
  every grid by ExGrid's own stylesheet), so that digits of the same place line up. A column header takes
  its column's alignment. Identifiers that happen to be digits (a Run ID, a trade number) are not quantities and are
  left-aligned ([Spectrum](#references)).
- **Content in a row is vertically centred**, text and controls alike ([Spectrum](#references)).
- **Column widths.** Every data grid takes `onColumnWidthChanged` through `useColumnWidths`
  (`client/src/platform/grid/columnWidths.ts`): a drag on a header's right edge moves the column (owner, 2026-11-10),
  and the width is the user's setting for that grid, by its id with its application's prefix (`xva/trades/existing`):
  the same in every workspace, after a reload and in another browser (ADR-0017 §5). A declared width is a default
  that never cuts its header.

## 5. Icons

- **MUST** draw icons from one set, `@tabler/icons-react` (the set Mantine's own documentation uses), not from text
  glyphs. A glyph such as `☀` or `☾` comes from whichever fallback font has it, so its size, weight and baseline
  differ from the text beside it and between platforms.
- Size 16 with `stroke={1.75}` inside a 30px control; 14 inside a `compact-xs` one.
- An icon-only control **MUST** have an `aria-label`, and **SHOULD** have a `Tooltip` that names it and, where one
  exists, the command that does the same. The account menu's avatar has none: resting the pointer on it opens the
  menu, which names it and the `settings` command. Decorative icons beside text take
  `aria-hidden`.
- An icon beside a word is preferred to an icon alone; only a control whose meaning is conventional (close, the
  account's avatar, the command line) goes without its word.

## 6. The shell

The shell is the header, the panels under their tabs, and the AI pane (ADR-0007 §7).

- **The header has three zones**, left to right: what this is and how to command it (title, Commands); the context
  every run is bound to (Region, As of); what is the user's own (the AI pane, and the account menu: who they are,
  Settings, the theme, signing out). The account menu opens as the pointer rests on the avatar, or on a click or a
  tap (owner, 2026-11-11: settings are looked for under the user's account, not behind a theme button).
- **MUST** give the header a fixed height on each breakpoint, `HEADER_HEIGHT` in `client/src/shell/AppHeader.tsx`,
  never `auto`. AppShell offsets the main area by that number; a header that grows past it covers the top of the
  panel. That is what `height: 'auto'` did on a phone before ADR-0009: the Rates toolbar slid under the header.
- **The header folds where its content stops fitting**, not at a round number. One row needs about 850px, so it is one
  row from `md` (992px) up and two rows below, the context on the second. At 768px a single row put the Region field
  under the title.
- **Transient messages are in the main area**: the context's issues above the panels, and a notice drawn over the
  top of the area. The header and the toolbars do not grow to say something.
- **A notice takes no room** (ADR-0006's note 2 of 2026-11-11). It is one line of result over the top of the main
  area, so a one-screen page does not move under it. It is `done` (teal, a tick, gone after six seconds unless the
  pointer or the keyboard is on it) or `refused` (red, a cross, there until closed or replaced). The kind is said by
  the caller, the icon and the colour, never read from the text.
- **The panels' tabs are one row** that scrolls sideways, never wraps, and keeps the active tab in sight; the button
  at its end lists every panel and the active panel's verbs, which is how a phone reaches them (ADR-0006 note 7). A
  tab's menu (right click, Shift+F10) shows each verb with the words the command line takes for it. A tab is one
  Control Height, as the button is (§3). A new item of a menu or key of a tab is a verb of
  `client/src/shell/panelVerbs.ts`, so the line, the menus and the keys stay one; the pointer's own gestures (a click
  that shows a tab, a drag that moves one to any place) call the same action of the store the verb does. A name is
  typed on the line, never in a field of the tab: Rename, F2 and a double click open `rename panel` with the panel's
  name selected (ADR-0006 note 9). A page shown again is as it was left: what it shows (a tab, a fold, a grid's sort,
  conditions and Selection) is kept by its panel with `usePanelState` and `useKeptGrid`, never in a page's own
  module-level map (ADR-0006 note 10).
- **The AI pane** is an aside beside the panels from `sm` up, and a drawer over them on a phone; neither takes the
  keyboard when it opens (ADR-0007).
- **Areas** (ADR-0018). From a panels' space of 768px (the window less the AI pane), the panels stand in areas, each
  under a strip of note 7's, drawn by dockview with the shell's tabs, menus and list (`client/src/shell/PanelAreas.tsx`).
  - An area is at least 320 by 240px. A split, a size or a proposal that would make one smaller is refused, saying
    the room it needs; where the space cannot hold the arrangement, the panels show as one strip until it can.
  - The active panel's area is framed in `signal`, 2px; the others in the default border, 1px. The frame is the only
    signal of which panel the line acts on besides the selected tab, so it is never dropped for looks. One area alone
    has no frame: its selected tab says it, as the strip's does.
  - A floating area has a title bar above its strip, one Control Height high, which moves it; its edges resize it. The
    title bar carries the grip of a part's height handle, so that it reads as something to take. The title bar and
    the splitters take the keyboard (arrows by 24px; Shift with the arrows resizes a floating area), show its focus
    as a tab does, and are at least 24px to a finger (§8).
  - The areas use the panels' space edge to edge: the shell's padding is the strip's, not the areas'.
  - dockview's own colours, borders and sizes are set from the theme's tokens in `client/src/shell/shell.css`, in both
    schemes; dockview's own context menu, overflow list and theme are not used.

## 7. Colour and themes

- **Both themes are acceptance requirements** (UX03). Dark is the default. Every change is looked at in both.
- **MUST** take colour from the theme and Mantine's variables. CSS that differs by theme uses `light-dark()` or
  Mantine's scheme attribute. ExGrid's colours come from `@ex-grid/mantine/styles.css`, which maps its `--ex-`
  tokens onto Mantine's variables. Paint an owner decision asks for beyond that (the Rates Header Groups' ruled edges
  and primary toggle, 2026-11-04) goes in `client/src/theme.css` on `QubeGrid`'s class `qube-grid`, with the
  decision named: colour and paint only, never `--ex-font-family` (metrics-bearing: see §10) and never a geometry
  (ex-grid ADR-0027/0030).
- **Meaning of colour** — one meaning each, everywhere:

  | Colour | Means | Examples |
  |---|---|---|
  | `signal` (amber, primary) | the primary action, focus, the selection | Price, the focus ring, the brand |
  | `red` | an error, an action that stops something, or a negative amount (C48) | a refused edit or command, Stop live, a PV of `-1,250` |
  | `yellow` | a warning, something stale, or something simulated | the context in force is the previous one, `SCRIPTED · NO MODEL`, a live frame behind the workspace |
  | `teal` | live or succeeded | Start live, a frame in step, a command that was done |
  | `blue` | information or pending | an argument still to give |
  | `gray` | neutral metadata | the market source |

- **MUST NOT** use colour as the only signal ([WCAG 1.4.1](#references)). A status badge says its status in words;
  a negative figure keeps its minus sign.
- **A negative amount** is red as finance reads it (owner, 2026-11-10): the figure alone, in red's text colour
  (`--mantine-color-red-text`, 4.5:1 in both schemes), never a note beside it. In a grid it is the column's `tone`
  (`amountTone`, `client/src/platform/grid/amounts.ts`); in a table, `data-negative` on the cell. A percentage, a rate
  or a spread is not an amount and is never red for its sign.
- **Contrast**: text at least 4.5:1 against its background, and the edges of controls and focus indicators at
  least 3:1 ([WCAG 1.4.3, 1.4.11](#references)). `c="dimmed"` is for secondary text, never for a figure the user
  must read. The theme holds this, not each page: a filled control's label is dark or light by its fill
  (`autoContrast`), and in the light scheme a colour's text and a light variant's label are its ninth shade darkened
  by a third, a link takes the primary colour's text, and dimmed is grey 7 (`cssVariablesResolver` in `theme.ts`). Mantine's own light scheme read at 2.7:1
  for a yellow badge and 3.3:1 for a dimmed label (measured 2026-11-08); the XVA page's browser test measures every
  text in both themes.
- **Dark surfaces** are dark grey, not black, with lighter surfaces for what is raised (the `dark` palette in
  `theme.ts`), and saturated colours are used sparingly on them ([Material, dark theme](#references)).

## 8. Pointer, touch and keyboard

- **Target size.** Every pointer target is at least 24×24 CSS px, or has 24px of clear space around its centre
  ([WCAG 2.5.8](#references)). A 30px control meets it; a 22px `compact-xs` button meets it only by its spacing, so
  two of them are never placed edge to edge.
- **Touch.** Platform guidance for touch is larger: 44pt on iOS and 48dp on Android ([Fluent 2](#references)).
  DEFAULT: the 30px Control Height stays on touch screens, because a phone is a secondary surface here and the header
  must keep its two rows. OPEN: whether `(pointer: coarse)` raises the shell's controls to 36–44px is decided on the
  iPhone journey (UX02), on a real device, not here.
- **Hover never hides anything.** Information or an action shown only on hover is missing on touch. A hover style is
  paired with the same style on `:focus-visible` ([web.dev](#references)).
- **Focus ring**: `focusRing: 'auto'` in the theme, so the ring shows for the keyboard (`:focus-visible`) and not as a
  leftover of a click or a tap. ExGrid draws its own Focus inside a grid.
- **Every action has a command** (ADR-0006). A key hint (`Ctrl K`) is shown only where a keyboard is likely, from `sm`
  up (`visibleFrom="sm"`).

## 9. Responsive layout

Mantine's breakpoints, beside the window classes Android and Material use for the same purpose
([Android](#references)):

| Mantine | Width | Window class | Here |
|---|---|---|---|
| base | < 576px | compact (< 600dp) | phone portrait: one column, the AI pane a drawer |
| `xs` | 576px (36em) | | |
| `sm` | 768px (48em) | medium (600–839dp) | tablet portrait: the AI pane beside the panels |
| `md` | 992px (62em) | expanded (840–1199dp) | the header on one row |
| `lg` | 1200px (75em) | large (1200–1599dp) | the AI pane at 400px |
| `xl` | 1408px (88em) | extra-large (≥ 1600dp) | desktop |

- **SHOULD** respond in CSS (`@media` in `client/src/shell/shell.css` or a component's CSS, `hiddenFrom`,
  `visibleFrom`), not with `useMediaQuery`. CSS answers before the first paint; a hook answers after it. Mantine
  recommends the same, and notes that a `size` prop is not responsive: render two components and show one
  ([Mantine, responsive](#references)). `useMediaQuery` is for choosing which component mounts (the AI pane's aside
  or drawer), not for styling.
- CSS variables do not work inside a media query, so a breakpoint in CSS is written in `em` and matches the table
  above (`max-width: 61.99em` is "below `md`").
- **A page responds to its panel, not the window** (ADR-0018 §5). A panel's content is a CSS size container of its
  width (`container: qube-panel / inline-size`, `PanelPage` in `client/src/platform/panelPage.tsx`), in an area and in
  the strip alike: not of its height too, since in the strip a page grows with its content, which a `size` container
  would not let it. A page's breakpoint is a container query on it, written with the table's widths
  (`@container qube-panel (max-width: 47.99em)` is "below `sm`" for the page), and a height that filled the window
  fills its area's page (`.qube-panel-page-body` in an area, its area's height, which scrolls the page). The width is
  the panel's whole width, the shell's gutter included, and a scrollbar beside the page takes nothing from it: a page
  in one area of a 768px window reads 768px, as it read the window. A hook that chooses what mounts reads the panel's
  width as the browser reports it when it changes (`usePanelWidth`), never the window's. The shell itself (the header,
  the AI pane, areas or a strip) still reads the window.
- **MUST NOT** scroll the page sideways. A grid scrolls inside itself; a toolbar wraps.
- **MUST** look at a layout change at 390, 768, 992 and 1440px wide, in both themes, before it is reviewed: those are
  the widths where this shell changes shape.

## 10. Type and words

- **One face**: Mantine's default system stack (`-apple-system`, `BlinkMacSystemFont`, `Segoe UI`, `Roboto`, …),
  taken from `mantineDefaultFont.family` in `theme.ts`. It is the face ExGrid has measured widths for, so the grid
  judges a figure that does not fit (`####`) by the face it is drawn in, and the cells and the text beside them
  match. **MUST NOT** set a grid's font in CSS or load a web font for the UI without handing the grid that font's
  measured widths (`MantineExGrid`'s `font`, ex-grid ADR-0027/0390 §9): a grid judging widths for one face while
  drawing another cuts or overflows figures, quietly.

- Sentence case for labels, buttons, titles and messages (`Leg 1 details`, `Using the previous context until this one
  is valid.`). Upper case only for the product's name and a Badge.
- A button says what it does as a verb (`Price`, `Start live`, `Add IRS`); a toggle says what it shows and reports
  its state with `aria-pressed`.
- A disabled control says why, in its `title` or in text beside it; a busy one shows `loading` on itself, not a
  spinner elsewhere. A menu item that cannot act is `UnavailableMenuItem` (`client/src/shell/`): dimmed, its reason
  under it where a phone reads it, and still in the arrows' reach (ADR-0006 note 7).
- What is simulated says so in the place the figure is shown (`SCRIPTED · NO MODEL`, `MARKET SYNTHETIC`).
- **A date is written `yyyy-MM-dd`** (ISO 8601) in a field, whatever the browser's locale, as the grids and the
  sentences already write it. A date field is `IsoDateInput` (`client/src/platform/IsoDateInput.tsx`): a text field
  with a calendar a click or a tap opens. It shows what was typed and its holder has exactly that text; text that is
  not a date that exists is marked and said, never rolled over or put back to an older date. **MUST NOT** use
  `<input type="date">`: it draws the browser's locale (`11/11/2026` in en-US, `2026/11/11` in ja-JP) and a page
  cannot tell it otherwise. Nor Mantine's `DateInput`: its parser makes `2026-03-02` of `2026-02-30`, and when it
  cannot read the text it keeps the last date while the text says another.

## 11. Checking a change

A UI change is not done when it compiles (CLAUDE.md, "Completion reporting"):

1. Screenshots at the four widths of §9, dark and light.
2. A Playwright test where the defect would be geometry only a browser shows. `e2e/workbench.spec.ts`'s ADR-0009
   test is the model: it reads the header's controls' boxes at each width and checks one Control Height, shared rows,
   and that the header covers nothing.
3. No console warning or error (the e2e fixture fails a test that has one).
4. The keyboard path still works: Ctrl/⌘+K, Tab through the header, Escape out of a dialog.

## 12. Known gaps

Seen while writing this, and not fixed by ADR-0009's change:

- On a phone, the Rates toolbar wraps to several rows; a phone may want its secondary actions in a menu.
- A grid's Action Column buttons (Rates' `Flows`) are drawn by ExGrid's core, not by the Wrapper, and read
  as the browser's own grey buttons in both themes. Seen after the switch to `@ex-grid/mantine`; still so in 0.2.0,
  ex-grid#553.
- A required context field's message repeats its name (`As of: asOf is required.`), because `validate` names the
  field and the message is shown without its label.
- Touch sizes have not been checked on an iPhone (UX02).
- In a grid's column menu, `@ex-grid/mantine` (0.2.0-beta.3, still so in 0.2.0) draws a disabled item as a filled grey
  block, where Mantine's own Menu only dims the text. That is the Wrapper's, ex-grid#552, not patched here.

## References

- Strom, M. *UI density* — visual, information, design and temporal density; density as value over time and space.
  <https://mattstromawn.com/writing/ui-density/>
- Bloomberg. *How Bloomberg Terminal UX designers conceal complexity* — familiarity, incremental change.
  <https://www.bloomberg.com/company/stories/how-bloomberg-terminal-ux-designers-conceal-complexity>
- Carbon Design System. *Text input, usage* — 24/32/40/48px field heights; one field height on a surface.
  <https://carbondesignsystem.com/components/text-input/usage/>
- Fluent 2. *Layout* — the 4px spacing ramp, alignment, touch targets 44×44 (iOS, web) and 48×48 (Android).
  <https://fluent2.microsoft.design/layout>
- Adobe Spectrum. *Table* — right-aligned quantities, tabular figures, headers take their column's alignment.
  <https://spectrum.adobe.com/page/table>
- W3C. *Understanding SC 2.5.8 Target Size (Minimum)*. <https://www.w3.org/WAI/WCAG22/Understanding/target-size-minimum.html>
  (and SC 1.4.1 Use of Color, 1.4.3 Contrast (Minimum), 1.4.11 Non-text Contrast in WCAG 2.2).
- web.dev. *Learn Responsive Design: Interaction* — `pointer`, `any-pointer`, hover paired with focus.
  <https://web.dev/learn/design/interaction>
- Android Developers. *Use window size classes* — compact, medium, expanded, large, extra-large.
  <https://developer.android.com/guide/topics/ui/layout/window-size-classes>
- Mantine. *Responsive styles* (breakpoints, `hiddenFrom`/`visibleFrom`, `useMediaQuery`) and *ActionIcon*
  (`input-*` sizes). <https://mantine.dev/styles/responsive>, <https://mantine.dev/core/action-icon>
- Material Design. *Dark theme* — dark grey surfaces, desaturated colours. <https://m2.material.io/design/color/dark-theme>
