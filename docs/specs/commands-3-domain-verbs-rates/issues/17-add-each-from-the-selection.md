# 17: The page makes an iteration from the two grids' selections

**What to build:** A button `Add each` in the New grid's caption runs the command with no argument. A button and a
Context Menu item make one iteration from the rows selected in the New grid and those selected in the Existing grid.
The Changes tab shows an iteration's two lists and removes one trade from either (ADR-0016 §3).

**Pull request:** 3d

**Blocked by:** 16.

**Status:** done

- [x] `Add each` makes a case per ticked New row.
- [x] With two new trades and one existing trade selected, the button makes one case of the three.
- [x] The Changes tab lists the case's candidates and the trades it takes out.

## Comments

- *Changed as built (ADR-0016 note 37):* `One case…` and the menu's *One case from the selection…* open a dialog
  on the two selections, and *Make case* makes the case: a grid once clicked always has a row selected, so a press
  that made the case at once would take out a trade nobody chose. The second box reads so: the button opens the
  dialog, and the dialog makes one case of the three.
- *Added as built:* the selections are read by the server (`POST /api/workspaces/{id}/selection`), since the trade
  grids are paged; the page's request is made in the command's turn, of the cases it shows at the revision the
  command is sent at (spine 6); the one trade a case adds has no button on the Changes tab.
- *Corrected in review (ADR-0016 note 37):* the button is `Make case…` and the menu's item *Make case from the
  selection…*; *Make case* sends the one iteration with `keep`, so a stale case of the set no longer refuses it
  and the notice names the case made; a view's selection is checked whole, a malformed view refused with 400; on
  the Changes tab a trade a case takes out is `Keep <id> in After`.
