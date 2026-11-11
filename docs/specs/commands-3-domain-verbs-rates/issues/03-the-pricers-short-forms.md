# 03: The pricer's `before`, `after` and `currency` are short forms

**What to build:** `before all|none [new|existing]`, `after all|none [new|existing]` and `currency <ccy>` expand to
`set`. With no grid named the grid is the view's; with no view either the line is refused, naming the grids. The
client's three hand-written commands go, and the grids' caption buttons run the expanded line through the door
(ADR-0016 note 29).

**Pull request:** 3a

**Blocked by:** 02.

**Status:** done

- [x] `before all` with the keyboard in the Existing grid shows `set existing before on` and writes it as one edit.
- [x] `after none new` names its grid and needs no view.
- [x] `before all` with no grid and no view is refused, naming `new` and `existing`.
- [x] `currency USD` shows `set workspace currency USD`; a currency the page does not offer is refused by name.
- [x] `B all`, `B none`, `A all` and `A none` on a grid's caption write what they wrote, and `undo` takes each back.

## Comments

- `currency USD` expands to `set workspace currency USD`: the display currency is a field of the workspace's settings
  row, which the selector names `workspace`, and `set currency USD` would have no row (ADR-0016 note 29, as built).
- No grid is assumed. The client's old rule (the grid the keyboard was last in, Existing by default) is gone: with no
  grid named and none in the view the line is refused, naming `new` and `existing`.
- A caption button runs the short form (`before all existing`) through the door, so what it says is the kernel's
  sentence (`Set B to off on … and ….`) and `undo` names it as it names the line. It is still one edit on the grid
  (the trades table's `SetAll`). Where every row already holds the value the kernel writes nothing and says
  `Nothing changed`, where the page's endpoint wrote the grid's edit again.
- The browser test types `before none existing`, `before all` with the keyboard in the Existing grid and `currency
  usd`, and presses one caption button; the four forms and the grid from the view are held on the server
  (`PricerLineTests`).
