# 01: A log entry says which command wrote it and what it replaced

**What to build:** `EditEntry` gains `Command` (a `CommandStamp`: id, the command's words where given, and the command
an undo or a redo takes back) and `Replaced` (what the entry found on its target). `WorkspaceService` stamps every
entry of one accepted call with one id, `c` and the first sequence number the call writes, the derivation's entries
included. `Batch` takes the command's words. The SQLite store keeps and reads the new columns, and a log written
before them still opens (ADR-0016 §11 and note 1 of 2026-11-11, an addition to ADR-0003's entry).

**Pull request:** 1a

**Blocked by:** nothing.

**Status:** done

- [x] Every entry of one `Batch` carries the same command id, and the next call another.
- [x] A `Set` over an override records that override; a `Set` on a field that followed its source records none.
- [x] A `Reset` records the override it removed; a `Remove` the entity and the edits that ended with it.
- [x] The SQLite store answers the same stamps and replaced values after a load; an older log opens with none.

Done 2026-11-11. `CommandStamp` and `Replaced` are on `EditEntry` (`Workspace.cs`); `WorkspaceService.Apply` stamps
every entry a call writes, the derivation's included, and the making's two entries are `c1`. The SQLite log has five new
columns, added to an older log when it opens (`WorkspaceUndoTests`, `SqliteWorkspaceStoreTests`).
