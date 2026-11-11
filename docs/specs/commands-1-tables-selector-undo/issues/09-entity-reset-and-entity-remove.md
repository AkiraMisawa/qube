# 09: `entity.reset` and `entity.remove`

**What to build:** `entity.reset` removes the edits on the rows, of one field or of all, and with a subset whose field
has a whole-set edit that edit too; `entity.remove` removes rows the workspace made and refuses any other by name
(ADR-0016 §3 and note 10). Both answer the result of `entity.set` and are taken back by `workspace.undo`.

**Pull request:** 1b

**Blocked by:** 08.

**Status:** done

- [x] `reset` of a field, and of every field, on rows by id and by `where`; a row with no edit is counted as already
      following.
- [x] `reset` over a subset removes the whole-set edit and each row's own.
- [x] `remove` of a made row, with its edits; an acquired row is refused by name; `undo` brings the row back.

## Comments

- 2026-11-11: done (`TableService.Reset`, `TableService.Remove`, `EntityReset`, `EntityRemove`; `TableCommandTests`).
- `entity.reset` takes `rows` and an optional `field`, by its words; `entity.remove` takes `rows`. Both are flat, answer
  what `entity.set` answers, and are named in the log by their own words (`reset where station=south`, `remove R7`).
- Over a whole subset every row of it counts as changed where the whole-set edit went, since each follows again.
- Whether a row may be removed is the table's to say: the kernel asks it for the changes and writes them, all or none.
