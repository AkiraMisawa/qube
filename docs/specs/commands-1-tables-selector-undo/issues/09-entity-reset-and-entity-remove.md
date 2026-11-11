# 09: `entity.reset` and `entity.remove`

**What to build:** `entity.reset` removes the edits on the rows, of one field or of all, and with a subset whose field
has a whole-set edit that edit too; `entity.remove` removes rows the workspace made and refuses any other by name
(ADR-0016 §3 and note 10). Both answer the result of `entity.set` and are taken back by `workspace.undo`.

**Pull request:** 1b

**Blocked by:** 08.

**Status:** ready-for-agent

- [ ] `reset` of a field, and of every field, on rows by id and by `where`; a row with no edit is counted as already
      following.
- [ ] `reset` over a subset removes the whole-set edit and each row's own.
- [ ] `remove` of a made row, with its edits; an acquired row is refused by name; `undo` brings the row back.
