# 09: Ctrl+Enter runs the line and keeps it open

**What to build:** Ctrl+Enter in the line runs the command as Enter does and keeps the line open: the result is said
in the line, the text is cleared, the view is read again, and the list is the one an opening shows. A refusal stays as
with Enter. Not while an IME composes (ADR-0016 §9).

**Pull request:** 2c

**Blocked by:** 05.

**Status:** done

- [x] `set selected a off`, Ctrl+Enter, `undo`, Ctrl+Enter, Escape: two commands ran and the line said each result.
- [x] After `go existing` chained, the next command's context is the Existing grid.
- [x] A refused line stays with its text and reason, as with Enter.
- [x] Ctrl+Enter while an IME composes is the IME's.
- [x] A line typed while the chained one runs is kept whole, and runs only once that one has ended.

## Comments

- As built (ADR-0016 note 24): the line says what its chain did as a log under its input, in the order it happened:
  a row per line that ran (`› set selected a off`) and a row per notice since, with the notice's kind as a tick or a
  cross and its colour. No notice is said to be any line's: which command a late one (`Price was accepted as Run …`)
  belongs to is not known. The last four rows are drawn and the rest counted.
- Changed in review: the line first said one sentence, `<line>: <notice>`, with the notice heard while the line ran,
  and took no key meanwhile. A notice a moment late was then said alone, and of a line typed at machine speed only
  the end landed. Now the next line is typed while the chained one runs and kept whole; Enter and the chain key run
  nothing until it has ended, and say so; a chained line refused comes back as typed, and what was typed meanwhile
  goes with it. No queue of Enters was built.
- A chained command that puts the keyboard in a place has ended when the place has taken it (`keyboardAwaited`), so
  no line is read with the screen of before the move.
- The keyboard is the line's from the first chained key until it closes (`shell/keyboard.ts`, `holdKeyboard`). A
  place a chained `go` or `find` puts it in has it taken back when it arrives, the screen is read again then, and
  the line closes on that place. A command that opens a dialog ends the chain: at once where the shell opens it,
  and when it takes the keyboard where a page opens it later. No command of the line opens a page's dialog today,
  so the second half is held by the keyboard's unit test and by no browser test.
- While an IME composes every key of the line is the IME's, plain Enter included, which used to run the line as it
  committed a composition. The browser test composes through Chromium's input protocol, and presses Enter, the chain
  key, Tab and an arrow in the composition.
- Found on the way: the workspace's context value was made anew at every notice, so each page made its command door
  anew in the middle of the command that said it, and a `find` after a notice had no grid to put the keyboard on.
  The value is now one for the application's life; the chained `go existing` then `find` test holds it.
- Known limit: after a chained edit that moves a row of a sorted grid, the next line that says `selected` is
  refused by name and the context is drawn stale, until the line is opened again.
