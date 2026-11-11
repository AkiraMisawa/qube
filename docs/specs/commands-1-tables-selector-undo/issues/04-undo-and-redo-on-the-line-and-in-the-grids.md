# 04: `undo` and `redo` on the line and on the grids' keys

**What to build:** `undo` and `redo` are commands of the line on the pricer's page, and the trade grids and the input
grids pass ExGrid's `onUndo` and `onRedo` to them. The page runs them after the edits already sent, reads its view
again and tells its grids. The notice says what was taken back and that `redo` puts it back; a refusal says why
(ADR-0016 §11).

**Pull request:** 1a

**Blocked by:** 03.

**Status:** done

- [x] `undo` after `set CSA-A threshold 5mm` puts the threshold back and says so; `redo` sets it again.
- [x] Ctrl+Z in a trade grid takes back the cell just typed, and Ctrl+Y puts it back.
- [x] `undo` with nothing to take back says so in a refused notice and changes nothing.
- [x] Looked at in a browser at 1440, 992, 768 and 390 px in both schemes.

Done 2026-11-11. `undo` and `redo` are commands of the pricer's page, after `set` in its list, and every grid under the
page takes `onUndo` and `onRedo` from `GridUndoContext` (`QubeGrid`), so the input grids have the keys too. The page
runs them through `Api.command` after the edits already sent (`xva.spec.ts`, "ADR-0016 §11", run three times). Looked at
in Chromium at the four widths in both schemes: the notice wraps to two lines on a phone, and the two commands list
under the page's title.
