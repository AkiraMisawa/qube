# ADR-0015: The Rates line grid reads at laptop width: each leg in one line, and lean headers

Status: ACCEPTED 2026-11-10 by the owner. Raised by the owner the same day: on a laptop the line pricer can only be read
by scrolling sideways; "on a narrow screen, show only the trade's summary and fold each leg?" Three layouts were
prototyped on the real grid and photographed at 1280px; the owner asked whether an opened leg can open one level further
(yes, §2) and chose this one ("それじゃあ案２でいいよ"). Amends C43 (the levels of a leg's terms) and the order of the
result columns given on 2026-11-04 (the inputs first, the result after them, which still holds). The number 0015 is
taken because 0014 is held by the open pull request #17.

## Context

Measured on `main` at 4696820 with `@ex-grid/*` 0.2.0, compact density, five lines (four IRS and a CCS):

- The line grid was **4,288px** wide with both legs collapsed: the pinned line and its actions 183, Trade 460, each leg's
  basic terms 1,186, Terms 402, Result 551, Issues 320. A 1280px window shows 1,262px of it, a 1440px one 1,422px and a
  1920px desktop 1,902px. The result started 3,426px from the left. The strip above the grid (2026-11-04) shows only the
  focused line's result.
- About 40px of every column was the header's reserve for ExGrid's column-menu button and sort mark
  (`gridHeaderRequiredPx(…, { menuButton: true, sortable: true })`), so a column of three-letter values (`Ccy`, `P/R`)
  was about 100px wide. Twenty-five columns paid it.
- Folding each leg into one column alone (the owner's idea as first stated) brings the grid to 2,145px: the trade and
  both legs fit at 1280px, the result still does not.

Widths that switch the layout were considered and set aside: today's shape does not fit even 1920px, so a narrow
layout would be the layout everywhere, and a grid that changes shape when the window is resized breaks the habit the
guidelines protect (docs/design-guidelines.md §1, familiarity).

## Decision

1. **A leg is one line until it is opened.** A collapsed leg is one read-only column, `Summary`, that reads the leg as a
   trader says it: the currency and the notional in the scale it is typed in (`3bn`, `500mm`), what it pays (`Fixed
   4.10%`, `Fixed par`, `TONA +0%`, `SOFR +fair` when the spread is solved, `ATM+5bp`, `12.5 pts`), and its payment
   frequency. Leg 2 leaves out its currency and notional when they are leg 1's, as on every IRS. Typing on it is
   refused by the grid as on any read-only cell; its tooltip says how to open it, and a double click opens it.
2. **Three levels per leg.** The leg's Header Group draws ExGrid's toggle (ExGrid ADR-0400): opened, the leg shows its
   basic terms (C40) under a `Basic` group, and beside them a `Conventions` group with its own toggle. Closed,
   `Conventions` is one read-only column, `Overrides`, that names the second-level terms (C43) set on the leg, or says
   `Profile` when none is; opened, it is C43's nine columns. While a leg is open the header has one more tier (28px);
   the groups that have no level under them span both tiers (ExGrid ADR-0032's `tierSpan`). Alt+Shift+↓ and ↑ open and
   close the lowest group over the Focus's column, as ExGrid ADR-0400 gives them.
3. **The Trade group opens the trade's second terms.** Collapsed, Trade is Product, Start, End and P/R; its toggle adds
   Collateral, Break and Risk, which C40 lists and few lines set.
4. **The line grid's headers carry no column menu and no sort mark** (`hideColumnMenu`, every column `queryable: false`),
   so a column is as wide as its values or its label. The line grid gives up sorting, filtering and the column menu's
   commands: it holds the few lines a user is pricing, in the order they were made.
5. **Remove is in the line's context menu** (`Remove line`, by right click or the context-menu key), not a button beside
   Flows. The pinned part is the line and Flows.
6. **The result follows the inputs**, as on 2026-11-04: PV, its currency and `Par %` (a par rate on a fixed leg, a par
   spread on a floating one; the cell's message says which), then Status and Issues.
7. **The levels a user opened are kept for the page** while the application runs, so leaving the tab and coming back
   does not fold them again. (Since 2026-11-12 they are kept by the page's panel, with the rest of what a page shows,
   and go when the panel closes: ADR-0006 note 10.)

With five lines the grid is **1,644px** wide, and at 1280px every column up to `Par %` is in view with 27px to spare.

## Consequences

- At 1280px the line, the trade, both legs and the result read without scrolling. At 1440px Status and the start of
  Issues are in view too. With the AI pane open at 1440px the grid is 1,022px wide and the result scrolls again; the
  strip above the grid still shows the focused line's.
- Editing a leg's terms means opening it, which scrolls the result away for as long as it is open; the strip above the
  grid keeps the focused line's result in view meanwhile.
- No column of the line grid sorts or filters. If that is wanted later, the lighter alternative is ExGrid's to give (a
  header that does not reserve both bands on every column), not a menu put back on every column here.
- The browser test that types a line cell by cell opens both legs first, and the test of C43 opens Conventions.

## Alternatives

- **The legs folded, nothing else changed** (2,145px): the result still needs a scroll at 1280px.
- **This, plus the focused line's legs side by side below the grid** (a field per row, Leg 1 and Leg 2 as columns, as
  a pricing ticket shows them): every term of the line in hand without widening the grid, at the cost of a second
  place to edit the same value and the most work. Left for later; it composes with this decision.
- **The result pinned at the left**: ExGrid pins on the left only, so the result would stand before the inputs, which
  2026-11-04 decided against.

## Note of 2026-11-14: rows to type in from the start (owner)

The grid was as tall as its lines, three rows at least, and showed no cell until a line was added by a button or
from the command line. The owner, asked what the grid shows before it has lines: cells could be shown from the
start, "five to ten blank rows, and more as they fill" (2026-11-14).

That is all he said. Every point below is how it was built, a DEFAULT for him to change.

1. **Blank rows stand below the lines**, enough that the grid shows eight rows, and never fewer than two after the
   last line: eight with no line, two with six lines or more. Two, so that Enter from a blank row lands on a blank
   row and line after line is typed down the grid. He said five to ten blank rows: the grid starts with eight, and
   from the fourth line fewer than five stand, so that it does not grow before it holds seven lines. The grid is
   as tall as its rows up to the height it had (356px of rows), and scrolls beyond.
2. **A blank row is the page's and no line of the workspace.** Nothing is saved for it. The count of lines and the
   rows a command reads from the grid (its selection, its Focus, the rows shown) do not hold it. Its context menu
   holds no command of the table, since there is no line to remove: the grid's own Copy items are all it has. The
   result above the grid shows what it shows with no Focus on a line: the only line's result, or none.
3. **A product typed under Product in a blank row makes the line.** One word there, in either case, runs the line
   `add line <word>` that the Add buttons run (ADR-0016 note 36). What a product is stays the server's to say:
   `irs` or `ccs` today, and another word is its refusal, a notice that begins with the line. A cell runs no other
   line: what is not one word (a letter, then letters or digits) is refused as in point 4. The line made stands
   first among the blank rows, wherever among them it was typed: the lines are in the workspace's order. A line's
   own Product is not edited, as before.
4. **Anything else typed in a blank row's cells is refused** with what to do instead (type the product first). No
   product is guessed for it (CLAUDE.md, no fallback). A term typed in a blank row before its line has come is
   refused the same way: the row is still blank. The cells no row is typed in (the line's name, a folded leg's
   summary, the result) take no typing in a blank row either, as in a line.
5. **While the line's conditions filter the grid, it shows no blank row**: the rows are then what the conditions
   leave. The grid keeps the height of its unfiltered rows, as it did, so the page below it does not move as a
   filter goes on or off.
6. **The Add buttons stay**: they are the touch's and the mouse's way to the same line.
7. **The grid holds no blank row before the workspace's lines are read.** A grid that held its blank rows alone
   until its lines came would be in an order no Selection kept for it was read in, and a page shown again would
   lose its Selection (ADR-0006 note 10). The lines and the blank rows below them come to the grid together.
8. **A line this page made keeps the keyboard where it was.** ExGrid drops a Selection as its rows' order moves
   (ExGrid ADR-0011). A blank row is keyed by its place in the grid, so when a line is made every row keeps its
   place but the first blank row, whose place the line takes. The page knows the line it made by the name `add
   line` answers, whether a blank row's product, an Add button or the command line ran it, and puts the Selection
   back as it gives the grid that line:
   - Tab from the first blank row's Product is on the line's Start, and its terms are typed on from there.
   - Enter is on the blank row below, where the next product is typed.
   - From a later blank row the Selection stays on the blank row the key left it on, and the line stands above.
   - An edit open as the line comes is left open in its row, with what was typed: placing a Selection would commit
     it half typed. A product half typed in the row below, as the line before it comes, runs as the whole word.
     An edit open in the row whose place the line takes is point 9's.
   - A line that came from elsewhere (another page, the AI), alone or with this page's own, drops the Selection,
     as before: a position kept through it could name another's line.
   - Products typed faster than their lines come stop at the grid's last row, since the grid grows a row only as
     a line comes: Enter there has no row to go to, and the Focus ends on a made line's Product, where a product
     typed is refused in words. Each product typed in a blank row still makes its line.

   The outcome does not depend on how soon after another change the line comes. ExGrid's source gathers live
   changes within an interval (ExGrid ADR-0141), so the page has it put out what it gathered before the Selection
   is read, and its own line at once. One order matters, and fails to the safe side: the page learns the name as
   the command's answer is told, which the door does once the workspace is read again and before the page draws
   it. A reading from elsewhere that shows the line first (the AI's turn ending, the live stream ending) brings it
   to the grid as a line from elsewhere, and the Selection is dropped.
9. **A commit the grid refused because its row left is said in words.** A term half typed in the first blank row
   as the line takes its place cannot be written: the row it was typed in is not among the grid's rows, and the
   grid refuses the commit and keeps the editor open (ExGrid ADR-0142), saying so to a screen reader alone. The
   page raises a notice ("Nothing was written: ..."), so an editor that stays open on Enter does not look stuck.
   The same refusal answers a product half typed in the first blank row as another line takes its place, an edit
   of a line removed elsewhere, and an edit of a line that a filter from the line stopped showing, and the grid
   tells no cause. The notice so names none and says what holds in each: nothing was written, the edit stays
   open, and Escape leaves it without writing. By the grid's own rule a commit made once the row is back lands;
   the notice does not promise it, since a row a line took the place of does not come back.

Three limits of ExGrid 0.3.0 bear on a blank row, each filed there:

- **A blank row has a Flows button.** An Action Column declares its actions for every row
  ([ex-grid#572](https://github.com/AkiraMisawa/ex-grid/issues/572)). Its press on a blank row opens nothing and
  is refused with the words of point 4. Drawing the button as a Template Column was not taken: its key would
  become Space to enter the cell and a second key to press.
- **A blank row's other cells open the editor and refuse at the commit.** A column's `editable` is one answer for
  every row ([ex-grid#573](https://github.com/AkiraMisawa/ex-grid/issues/573)), as it already was for a line's
  Product. A blank row's Product cell says in its message, shown with the Focus on it, what it takes.
- **The commit does not wait for the line.** ExGrid's pending commit (its ADR-0380) would hold the keys typed
  until the line came. Tried, it answers the committing key from the edited blank row's new place and not from
  the line, and keys typed meanwhile were replayed into an error
  ([ex-grid#574](https://github.com/AkiraMisawa/ex-grid/issues/574)). So the commit is answered at once, and
  points 8 and 9 say what becomes of the keys typed before the line comes.

The DEFAULTs most his to weigh: eight rows from the start and two blank rows at least, fewer than his five from
the fourth line; the product as the one cell a blank row takes; the line standing first among the blank rows and
not in the row typed in; a line an Add button or `add line` on the command line made keeping the Selection too,
which it dropped before; the Flows button left on blank rows until ExGrid lets a row withhold it.
