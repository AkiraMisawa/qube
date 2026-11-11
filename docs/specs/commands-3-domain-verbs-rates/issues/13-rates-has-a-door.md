# 13: The Rates page has a door, a screen and places

**What to build:** The Rates page registers a command door, the view of its line grid (table, selection, focused cell)
and its places. The kernel's verbs, `find`, `go`, the chain key and the Context Menu's `Reset this cell` then work
there.

**Pull request:** 3c

**Blocked by:** 12.

**Status:** done

- [x] With two lines selected, `set notional 5mm` writes both, and `undo` takes it back on the page.
- [x] The line says the screen it was opened on.
- [x] `find <line>` puts the Focus on the line.

## Comments

- *As built (ADR-0016 note 36):* the door, the line grid's view (`heldView`: every line is in hand, so rows are
  named by their keys) and the places `lines` and `flows`. The grid's Context Menu holds the table's actions (`Reset
  this cell`, `Remove line`), and its undo and redo keys run `undo` and `redo`. Held by `rates.spec.ts` (two lines
  selected, `set notional 5mm`, the undo key, `find`, the menu's `Remove line`, `go flows` and `go lines`).
- *Found:* a line's id is the revision that makes it, so a test reads the ids from the grid and assumes none.
- *Corrected in review (2026-11-11):* the Price button names the market the page shows, as it did before this step
  and as Capture names its frame; the first build ran the typed `price`, which reads the latest. A disabled Capture
  says why in its `title`. `rates.spec.ts` now also presses Price (against a market held while a later one is
  published), Add CCS and the toggle both ways, and runs the menu's `Reset this cell`.
- *Found, and left:* ExGrid bounds its Context Menu by the grid's own box (its ADR-0040). The lines grid shows three
  rows at least, so on a workspace of up to three lines the menu shows three of its four items and scrolls to
  `Remove line`.
