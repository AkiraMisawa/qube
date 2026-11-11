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

## Comments

- As built (ADR-0016 note 24): the line says what ran under its input, as the line typed and the notice heard while it
  ran, with the notice's kind as a tick or a cross and its colour. A notice heard later (`Price was accepted as Run
  …`) is said by itself: which command a late notice belongs to is not known, and it is not guessed. A command that
  said nothing while it ran reads `price: ran.`.
- The keyboard is the line's from the first chained key until it closes (`shell/keyboard.ts`, `holdKeyboard`). A
  place a chained `go` or `find` puts it in has it taken back when it arrives, the screen is read again then, and
  the line closes on that place. A command that opens a dialog ends the chain: at once where the shell opens it,
  and when it takes the keyboard where a page opens it later.
- While an IME composes every key of the line is the IME's, plain Enter included, which used to run the line as it
  committed a composition. The browser test composes through Chromium's input protocol.
- Found on the way: the workspace's context value was made anew at every notice, so each page made its command door
  anew in the middle of the command that said it, and a `find` after a notice had no grid to put the keyboard on.
  The value is now one for the application's life; the chained `go existing` then `find` test holds it.
- Known limit: after a chained edit that moves a row of a sorted grid, the next line that says `selected` is
  refused by name and the context is drawn stale, until the line is opened again.
