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
  browser and says so; it moves to the server then.
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
