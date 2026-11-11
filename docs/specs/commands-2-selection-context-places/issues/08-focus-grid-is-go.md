# 08: `focus grid` and the panel chords are `go`

**What to build:** `focus grid` runs `go` to the page's first grid and keeps its chord. A command that edits or
executes gives the keyboard back to where it was; only `go`, `find` and `open` move it.

**Pull request:** 2b

**Blocked by:** 06.

**Status:** done

- [x] `focus grid` and its chord land where `go <first grid>` lands, on each page that has a grid.
- [x] After `set`, `reset`, `undo` and Price from the line, the keyboard is where it was before the line opened.

## Comments

- As built (ADR-0016 notes 20 and 22): `focus grid` is `go` to the first place that is a grid, with the same notice
  (`The keyboard is in New trades.`), and keeps `mod+shift+G`. Pages of one grid (Rates, the Pricing DB, a Run) land
  on their place `grid`. With no grid it is refused: on the line with the reason, from the chord as a notice.
- The keyboard afterwards was right since step 1 (the line gives it back after a command it ran); a browser test now
  holds it for `set`, `reset`, `undo` and Price, from a grid's cell and from a field that is no grid. `go` and `find`
  keep the keyboard where they put it.
- "The panel chords" of this ticket's title are the one chord there is, `focus grid`'s. No chord was added per place.
