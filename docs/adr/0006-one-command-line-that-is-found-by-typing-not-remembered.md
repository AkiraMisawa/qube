# ADR-0006: One command line, found by typing rather than remembered

Status: ACCEPTED 2026-10-27 by the owner, with the owner's corrections of the same day (recorded under "Changed from
the proposal"). Raised by the owner: shortcuts will run out, and Bloomberg's function codes cannot be remembered; "I
want something intuitive".

## Context

ADR-0002 makes the product keyboard-first and asks for one command system. Until now every command could carry a chord
(`mod+alt+R`, `mod+alt+P`, …), and the palette (Ctrl+K) found commands by fuzzy match on their title. Chords are a
small fixed space shared by every module: each domain application takes some, browsers and the operating system take
others, and nobody remembers more than a handful. Criterion C27 adds capabilities without touching the shell, so the
number of commands only grows.

Bloomberg avoids running out by typing short mnemonic codes (`DES`, `SWPM`) after a loaded security. The space never
runs out, but every code has to be learned. The owner does not want that.

## Decision

1. **One command line is the way to every action.** It opens with Ctrl+K from anywhere, and with `/` wherever `/`
   would not be typed into something (see 2). Buttons, the command line and the AI reach the same command (ADR-0005), so
   none of them can do something the others cannot.
2. **It is not the AI chat, and `/` goes where the keyboard is.** The AI pane has slash commands of its own, about the
   conversation; `/` opens those only while the AI chat's input has the keyboard. Elsewhere `/` opens the command line,
   except in a text field or a grid, where it is a character the user is typing (a grid cell starts editing with it,
   as in Excel). Ctrl+K opens the command line from all of these.
3. **Commands are English words for what they do**: a verb and its object (`price`, `new irs`, `live start`,
   `capture`, `open pricing db`). Nothing has to be known in advance.
4. **Completion is shown for the command and for each argument.** As the user types, the line lists the commands
   that match, each with what it will act on, its arguments, and why it is unavailable if it is. After a command is
   chosen, the line shows its arguments as slots and lists the valid values of the slot being typed, taken from the
   data (configured conventions, a counterparty search, the workspace's lines). Tab takes the highlighted completion;
   Enter runs. Missing required arguments are named and nothing runs; no value is filled in silently. A line typed in
   one go (`new irs JPY TONA 10y 3000000000 pay`) is read into the same slots and shown before Enter.
5. **The current context is the default target.** The active panel and its selection are the "loaded" object: `price`
   in a Rates workspace prices that workspace, and the line says so ("Rates · W-12, revision 9"). A command that needs
   another target takes it as an argument.
6. **Short forms are learned, not taught.** The ranking favours what this user ran often and recently, so after a few
   uses `pr` is enough for `price`. There are no user-defined aliases.
7. **`ai` is a command.** `ai <request>` hands the request, with the current context, to the AI pane, which ends in the
   same capabilities. When nothing matches what was typed, `ai <what was typed>` is offered last.
8. **Chords are kept for the shell only:** opening the command line, moving between panels, focusing the grid, editing
   the context, and closing a panel. Domain commands have no chord. Grid keys stay ExGrid's.

## Consequences

- A command declares its words, its arguments and where each argument's completions come from. A command a module
  registers through the catalog gets the same treatment, so no module edits the shell (C27).
- The chords for New XVA, Price XVA, New Rates, Pricing DB and the theme toggle are removed now, before the command
  line covers everything they did (the owner, 2026-10-27).
- Ranking by frequency is per user. Until the user settings store exists (with sign-in, C30), it is kept in the
  browser and says so; it moves to the server then. *(2026-11-11: the store exists, ADR-0017 §5. The ranking is the
  user's setting `commands.usage`, the same in every browser; a browser's earlier ranking is added to it once and
  removed. Sign-in (C30) is still the demo user.)*
- `?` lists what can be done in the current context.

## Notes of 2026-11-11: what a command says back, and what the empty line lists

Made for ADR-0016's Delivery order, step 0, after the line was driven in a browser on 2026-11-10. Each is a DEFAULT:
the owner asked for the fixes of that review that need no decision, and this is how they were read.

1. **A line that cannot be run runs nothing and stays open** (extends 4, "Missing required arguments are named and
   nothing runs"). A command reads its arguments before it does anything, and throws with the reason when it cannot
   read them. The line then keeps what was typed and shows the reason in red, as it shows a missing argument. Before,
   the line closed and nothing was said. Run from a chord, where there is no line, the reason is a refused notice.
   The reason is thrown as a refusal of its own type (`CommandRefused`): anything else a command throws is a defect,
   and is thrown on, never shown to the user as a reason.
   The reason shown while the line is typed, before Enter, is ADR-0016 §5's preview and comes with it.
2. **A notice is a command's result, of one of two kinds** (ADR-0016 §6): `done` or `refused`. The caller states the
   kind and nothing reads it from the text. Done is teal with a tick, a `status`, and leaves by itself after six
   seconds, unless the pointer is on it or the keyboard is in it: two holds, each let go by its own leaving. Refused
   is red with a cross, an `alert`, and stays until it is closed or another notice replaces it. A done notice that
   leaves something to decide stays as a refusal does: the count of edits in conflict or lost after the source was
   read again. A notice is drawn over the top of the main area and takes no room, so a one-screen page does not move
   under it (docs/design-guidelines.md §6). So a result does not outstay its command by more than those seconds. A
   reason stays through the commands that say nothing, and only the next notice takes its place: running a command
   does not clear it. A command that edits says what it did (`before none` says how many trades it cleared), except
   `measures` and `currency`, whose controls in the toolbar show what they set. A sentence names a row and a column
   as the screen does, by the row's id and the column's header (`CSA-A Ccy was not changed: …`, on Rates `L1 Leg 1
   Rate/Strike was not changed: …`), never by an entity's key (`selection:measures`) or a field's name in the code.
   Only a refused notice goes to the server's log (ADR-0014 §4): what a done one says, the server did and logged.
   *Amended 2026-11-11 (ADR-0016 step 3a, its notes 28 and 29):* what a command that edits says is now the server's
   sentence. `before none` runs as the kernel's `set` and says the rows it set (`Set B to off on … and ….`), no
   longer a count. `measures` and `currency` typed say what they did too, as every command the server reads does
   (`Price prices CVA and FVA.`); their controls in the toolbar still say nothing, showing what they set.
3. **The empty line and `?` list the page's commands first** (extends 5 and the last consequence). The commands that
   act on the page in the active panel come first, under the panel's title. Then come the other commands that can
   run, under a heading per group, the applications' groups before the shell's. On the empty line, those that cannot
   run now come last, each with its reason. Under a heading, what the user runs most comes first (6), those that
   cannot run included. A finished Run's views (`show <view>`) are its page's commands. A typed query ranks as before,
   with no headings. The list is never cut in silence: past its cap it says how many more there are. The placeholder
   names the first commands of that list, so it differs by page.
   **Nothing is highlighted while the line lists**, empty or at `?` (changes 4's "Enter runs" for these two lists).
   The first command listed is the page's, which on a pricer's page is `price`: with it highlighted, Ctrl+K and
   Enter would price. Enter alone runs nothing and says to choose with the arrows or to type; from nothing chosen,
   down is the first command and up the last. A typed query highlights its best match, and Enter runs it, as before.
4. **A flag is written one way everywhere**: `on` or `off`, `yes` or `no`, `true` or `false`, `y` or `n`, in any
   case, in a grid's cell as in `set`, and in a product's term of the form `flag` (ADR-0008 §2), which took `true`
   and `false` only. The server has one reader of the words (`FlagWords.Read`, in the kernel) and the page one
   (`readFlag`); a word that is none of them is refused, never guessed. ADR-0016's Context counts the cell editor as
   a fifth vocabulary; the one reader of every form is its §1.
5. **A command the catalog says how to type is read by the server** (added with ADR-0016's step 1, the same day; its
   §4, §5 and note 15). `set`, `reset`, `remove`, `undo` and `redo` are built from the catalog and the line reads
   nothing after their words: while one is typed it shows the server's reading under the line, its rows and what
   would change or why it cannot be read yet, and lists what could follow, which Tab takes (extends 4). Enter runs
   the reading the line shows, which a workspace that has moved since refuses; a line that cannot be read, and a
   command that is refused, stay as note 1 says, with the server's reason (ADR-0016's note 15). Every other command
   is read by the line as before, until it moves to the catalog.
6. **Ctrl+Enter runs the line and keeps it open** (added with ADR-0016's step 2, the same day; its note 24; extends
   4). ⌘+Enter on a Mac. The line says what ran and what the application said since, clears its text and reads the
   screen again. The next command is typed at once, while the one before runs, and is run once that has ended; a
   refusal stays as note 1 says. It is a key of the line, not a chord of the shell (8). While an IME composes,
   every key of the line is the IME's: before this note, the Enter that committed a composition also ran the line.

## Notes of 2026-11-12: the panels' tabs

Raised by the owner on 2026-11-11 ("what happens when I open many tabs in a page, what is good practice for tabs, and
make the good operations commands too"). DEFAULT, decided as recommended and reported, except where it says the owner.

7. **The shell manages its panels' tabs, and every way of doing so is one of its verbs** (extends 1 and 8).
   - *What many panels did* (measured on 2026-11-11 at 1440, 1280, 992, 768 and 390px with twelve panels). The
     strip is one row that scrolls sideways: about ten tabs show at 1440px, five at 768px and two and a half on a
     phone. A panel opened past the edge stayed there, out of sight, and so did one `next panel` showed. Panels of
     one title could not be told apart: each `new rates` of one date was `Rates 2026-10-27`, and `show panel` could
     name none of them. Opening a Run from the Pricing DB twice opened it twice. Closing the active panel showed its
     right-hand neighbour. Only the active panel's page is mounted, so the screen's cost does not grow with the panels
     (about 700 elements whatever their number; back to an XVA page of 10,000 trades in about 150 ms: one run of a
     Playwright script against the built client in a headless Chromium, the elements counted by `querySelectorAll`,
     the switch timed from the tab's click to its grid settled, so an indication, not a target). The price is
     that a page's own state goes when it is hidden: its inner tab, a grid's sort, filter, selection and scroll. The
     widths dragged stay: they are the user's setting by grid (ADR-0017 §5), the same in every panel.
   - *What the strip does now.*
     - The active tab is kept in sight: one just opened, one chosen elsewhere, and the active one of a strip the
       window has narrowed.
     - At the strip's end, a button says how many panels are open and lists them all. Choosing one shows it. Under the
       list are the active panel's verbs, which a touch screen has no right click for.
     - A tab's menu, by a right click or Shift+F10 (or the menu key), runs the verbs on that tab, and names the tab at
       its top. A key opens it on its first item and gives the keyboard back to the tab as it closes, or to the tab
       shown next where the menu closed its own (the WAI-ARIA tabs pattern). The menu key's own contextmenu, which
       Windows sends as the key is let go, leaves the menu that key opened as it is. An item that cannot act (Move
       left on the first tab) is dimmed with why under it, which a pointer, a key and a touch screen all read. It
       stays in the arrows' reach, as WAI-ARIA's menu pattern keeps a disabled item; choosing it does nothing and
       leaves the menu open. A refused notice would say it too, but on a phone it would lie over the list it was
       chosen from (a third review's finding); the account menu's Sign out is drawn the same way.
     - Delete on a focused tab closes it (Fn+Delete on a Mac laptop, whose delete key is Backspace), and the keyboard
       follows to the tab shown next. The middle button closes a tab. A tab dragged onto another takes its place: the
       drag calls the store's move, as `move panel` does, and reaches any place, where the verb takes a step or an end.
     - A long title is cut with an ellipsis. The tab's title and the list say it whole. A tab is one Control Height,
       as the list's button beside it is (docs/design-guidelines.md §3).
     - Only the strip scrolls to keep a tab in sight, sideways: the page under it stays where the user scrolled it.
   - *Where a panel opens, and what closing shows.* A new panel opens beside the active one, so a Run opens next to
     the page that made it. Closing the active panel shows the panel shown before it, as an editor does. A Run that
     is open already is shown in its panel, in the view asked for, and not opened twice; so is a page `reopen panel`
     would bring back that has been opened again since it closed.
   - *Two panels of one title* are numbered from the second (`Rates 2026-10-27 · 2`). A panel keeps its number while
     it is open, and reopened it has its number back where no other panel has taken it. The strip, the list and
     `show panel` use the number, and so do every command's target and the AI pane's card for a panel the AI opened:
     the first is named by the title alone and the others by their number.
   - *Pinned panels* stand first, show a pin and no close button, and stay open through the closes of several. A
     close of one closes a pinned panel too, as the user named it. A move keeps a pinned panel among the pinned, and
     the others among the others.
   - *Closed panels* are kept, the last twenty, each with where it stood, whether it was pinned and its number.
     `reopen panel` brings back the last closed, one at a time; a close of several comes back from its left, each
     where it stood, so the strip is as it was. A closed page comes back as a page opened anew: its
     workspace and its Run are the server's, so nothing of the user's work is lost by a close.
   - *The verbs.* `close panel` (Ctrl/⌘+Shift+X, as before), `close other panels`, `close panels to the right`,
     `close all panels`, `pin panel`, `unpin panel`, `move panel <left|right|first|last>` and `reopen panel`, beside
     `next panel`, `previous panel` and `show panel`. Each is declared once (`client/src/shell/panelVerbs.ts`), and the
     line, a tab's menu, the list and a tab's keys run that declaration. A menu shows each verb's words beside it, so
     the menu teaches the command. A close of several says how many it closed and how many pinned panels it kept,
     and how to bring one back. A verb that cannot act says why: on the line, under its item in a menu, and from a
     key in a refused notice.
   - *They are the client's.* They change the screen only: they are not logged, `undo` does not reach them, and the
     AI is not given them (ADR-0016 §3: the AI calls capabilities, never page automation). They are read by the line,
     not by the server: a panel is no table of a workspace. The panels are kept while the page is loaded, as before.
   - *Not taken.* Closing panels by themselves past a number (an editor's tab limit): a panel the user did not close
     would go, quietly. Tabs on several rows: the shell's height would move with the number of panels (ADR-0009).
     New chords: the browser takes Ctrl+W, Ctrl+Shift+T, Ctrl+Tab and Ctrl+1 to 9 before a page sees them, and §8
     keeps chords few.
   - *A page's state, when its panel is hidden* (the owner, 2026-11-11, from three options): each page keeps what it
     shows by panel (its inner tab, a grid's sort, filter and selection, what is opened or folded) and shows it again
     when its panel comes back; the scroll is not kept. Pages are not kept mounted while
     hidden: tried, the `new xva` dialog no longer had the keyboard as it opened over hidden pages, and a hidden Rates
     page would watch its live frames, so live would run on unseen while the panel is open (docs/07 §6 stops it once
     nobody watches). Built in its own pull request, after ADR-0016's step 5 changes the pricer's page: note 10.

## Notes of 2026-11-12: areas (ADR-0018)

8. **Note 7's strip is each area's** (ADR-0018 §1, accepted by the owner on 2026-11-12). From a panels' space of
   768px the panels stand in areas, split, floating and maximised, each under a strip of note 7's. What changes here:
   - *Within its area.* `close other panels`, `close panels to the right`, `move panel` and a tab dragged along a strip
     act on the panel's own area. `close all panels` still closes every panel but the pinned, in every area, and
     `next panel` and `previous panel` go through every panel in reading order (ADR-0018 §1). Pinned panels stand first
     in their area.
   - *The list* at an area's end lists that area's panels and the shown panel's verbs; `show panel` reaches any.
   - *Where a panel opens and what closing shows* (ADR-0018 §4): beside the active panel in its area; closing shows the
     panel that area showed before, and an area left empty goes.
   - *Below 768px, or where the arrangement does not fit,* the strip is this note's one strip, of every panel, the
     pinned first and the rest in reading order.
   - *Kept by the browser tab* (ADR-0018 §7): "the panels are kept while the page is loaded" (note 7) becomes "kept by
     the browser tab across a reload", with the arrangement. Two tabs keep their own.
   - *The new verbs* (`split panel`, `send panel to`, `float panel`, `dock panel`, `size panel`, `maximize panel`,
     `unmaximize panel`, `gather panels`, `arrange`) are declared in `panelVerbs.ts` as note 7's are, and are the
     client's in the same way. Their words are settled in `docs/specs/panels-1-areas/spec.md`.

## Notes of 2026-11-12: a panel's name

Asked by the owner on 2026-11-12 ("can a tab's name be changed?", then "let's support renaming"). DEFAULT, decided as
recommended and reported.

9. **A panel takes a name of the user's, on the line** (extends 7).
   - *The name.* `rename panel <name>` gives the active panel a name of the user's, shown in place of its page's title
     in the strip, the list, the tab's menu, `show panel`, every command's target and the AI pane's card for a panel
     the AI opened. The page's own title is kept: the name is the panel's, never the page's or its workspace's, so
     two panels of one workspace may be named apart. The page's title back is `rename panel` with that title, in any
     case, which the line completes. Spaces around a name are dropped and runs of them made one. A name is at most 60
     characters: a tab shows about half of that, and every request to the AI carries each panel's name.
   - *Numbering* (note 7) goes by what a panel is called, its name or else its title, told apart regardless of case,
     as `show panel` reads them. A panel renamed takes the lowest number free among its new name; the others keep
     theirs while they are open, as they do when a panel of their name closes. A name ending in `·` and a number is
     refused, saying why: the strip would read it as one of its numbers.
   - *Where it is typed.* A tab's menu and the list have Rename (the list is a phone's way), and F2 on a focused tab
     and a double click on a tab do the same:
     each shows that panel and opens the line on `rename panel` with the name it has, selected, so typing replaces it
     as a field for a name opens on the name. Enter names the panel; Escape names nothing. The keyboard goes back to
     the tab as the line closes, from a menu a key opened as well. There is no field in the tab itself: the line is
     where the name is typed, and the menu teaches its words (note 7).
   - *Kept.* A panel closed keeps its name, and `reopen panel` brings it back with it and its number. The browser
     tab keeps the names across a reload (note 8); one this version could not have given is refused with the rest.
   - *An arrangement the AI proposed* names panels as they were called (ADR-0018 §8): one renamed since is refused by
     that proposal's Apply, saying what it is called now, as for any panel whose title changed.
   - *It is the client's*, as note 7's verbs are: not logged, not undone, not the AI's (ADR-0016 §3).

## Notes of 2026-11-12: a page's state by its panel

The owner's choice of 2026-11-11 (note 7's last point), built after ADR-0016's steps 5 and 6. DEFAULT where it says so,
decided as recommended and reported.

10. **A page keeps what it shows by its panel, and shows it again when its panel comes back** (extends 7).
    - *What is kept.* The pricer's layout as the user left it (its part on a phone, the inputs open or folded and their
      height, the dock open or folded, its height and whether it is widened), the input tab and the dock's tab open,
      the case and the market the dock shows, the profile chosen and the series hidden; in a trace, the term selected,
      the terms open, the pages of children read and the card shown, each by the trace it is of (the Run, case, market
      and measure), so that the trace of a Run priced since starts as one opened anew; the Rates page's levels opened
      (ADR-0015 §7) and the line whose cashflows are open; the Pricing DB's domain; and every grid's sort, conditions
      (ADR-0016 note 41) and Selection, a Run's dataset by its reference. The widths a user drags are not the panel's:
      they stay the user's setting (ADR-0017 §5). A Run's view is the panel's already (`viewKey`), and a trace's order
      and what it shows below a term are kept by the view.
    - *A part drawn again while its panel is shown* (DEFAULT) takes back what it showed the same way: an input tab or a
      domain chosen again, a line's cashflows opened again. A page's state is its panel's, whichever of its parts is in
      view.
    - *What is not.* What the user typed and has not committed, a dialog, a notice, a busy state, and the scroll of a
      page or a grid (the Focus placed is revealed). Nothing kept is an input to what is priced: the user's work is
      the server's, and a page shown again reads it afresh (ADR-0003).
    - *A grid's Selection comes back only onto the rows it was made among* (DEFAULT, the spine's first rule). A
      position means a row only in one order (ExGrid ADR-0011), and the rows may have moved while the panel was
      hidden: an edit, a live tick under a sort by a live figure, a Run added to the Pricing DB. So the Selection is
      kept with the order its positions were read in, named by the page (a paged grid by the order token its reader
      answered, ADR-0016 note 17, once every row in hand came in one answer under the conditions the grid holds; a grid
      whose page holds every row by its rows' keys in turn, once the server has said which rows its conditions leave;
      rows that never change, a Run's datasets and trace, by the sort), and with the grid's columns by name. Drawn
      again, the grid takes back its sort and conditions at once; once its first rows are in hand, the Selection is
      placed where the order and the columns are the ones it was read in, and nowhere otherwise, leaving nothing
      selected as ExGrid leaves a grid whose order moved. A Selection the user made before that is the user's, and
      nothing is placed over it. A trace's term is placed by its key, as a term asked for is.
    - *One range.* ExGrid places one range and its Focus from outside a grid (`placeSelection`); a view it reads is
      handed back only to the grid that read it (`showViewWith`, its ADR-0331), and a grid drawn anew is another grid.
      So a Selection of several ranges is not kept, and neither is a grid's scroll.
    - *How long.* While the panel is open, in this page load, in memory. A panel closed takes it with it, what its page
      writes as it goes after the close included, so `reopen panel` brings back a page opened anew (note 7), and a reload restores the panels (ADR-0018 §7), each page opened
      anew. A panel moved to another area keeps its page mounted (ADR-0018 §6) and so loses nothing either way.
    - *How* (`client/src/platform/panelState.ts`, `client/src/platform/grid/keptGrid.ts`). A value a page keeps is
      declared once with its type and a name no other takes, as a verb is; `usePanelState` is React's `useState` kept
      by the panel the component is drawn in, and `useKeptGrid` keeps a grid's sort, conditions and Selection. A
      component drawn more than once in a panel (a trace of each measure) keeps its own under an instance.
    - *Not taken.* Keeping hidden pages mounted (note 7's tried and refused option). Keeping the state across a reload
      or a close: a page restored from storage would bring back a Selection of rows that may have moved, and a
      reopened panel is the page opened anew by note 7.

## Changed from the proposal

The owner's corrections, 2026-10-27: the command line and the AI chat's slash commands are separate, and `/` opens
the AI's only while the chat has the keyboard, the command line elsewhere; commands are English only, not English and Japanese; completion is shown for commands and arguments
alike; existing chords go now rather than after replacement; no user-defined aliases; an `ai` command is provided.

## Rejected

- **More chords, or chord sequences (Ctrl+K then R then P).** Still a code to learn, and still finite per module.
- **Bloomberg-style mnemonic codes as the primary input.** Unlimited, but each must be remembered (the owner's point).
- **Only natural language through the AI.** Slower, needs a model for every action, and an exact command should not
  depend on one.
- **User-defined aliases.** Not wanted by the owner; frequency ranking gives short forms without a second vocabulary.
