# 02: `undo` takes back the latest command and `redo` puts it back

**What to build:** `WorkspaceService.Undo` and `Redo` (ADR-0016 §11, notes 1 to 3): linear over the log, each one
all-or-none command of its own that restores what the command's entries replaced, sequence numbers included. They
answer what was taken back: the command's id, its words or its edits' titles (`IEditRules.TitleOf`), whose it was, its
origin, when, and how many edits. Refused by name at a command that took another source version, left a scope or
decided a held edit, at another user's command, at a named id that is not the latest, and where there is nothing.

**Pull request:** 1a

**Blocked by:** 01.

**Status:** done

- [x] `undo` of a set puts back the override it replaced, with its sequence number, or removes it where there was
      none.
- [x] `undo` of a batch, a create, and a remove with its edits; `undo` twice takes back the two latest.
- [x] `redo` puts back what `undo` took back, and a command ends what can be redone; `undo` after `redo` works.
- [x] A change of context that kept the source version is taken back; a refresh is refused by name and nothing older
      is reached.
- [x] Another user's command, and a named id that is not the latest, are refused by name.
- [x] After a load from SQLite, `undo` still takes back the command before the load.

Done 2026-11-11. `CommandHistory.Read` reads the two stacks from the log, and `WorkspaceService.Undo` and `Redo` write
one command that puts back what each entry replaced (`WorkspaceUndoTests`, thirteen tests; the load from SQLite in
`SqliteWorkspaceStoreTests`).

Found by the browser test run three times: the pricer made a workspace and then set its owner's last selection of
measures as a second command, so the first `undo` in a new workspace took back a command its user never ran.
`WorkspaceService.Create` now takes the values a workspace is made with and writes them in the making's own command, and
the last selection is read past one that was taken back (ADR-0016's note 2 of 2026-11-11;
`Values_a_workspace_is_made_with_are_part_of_its_making`, and over HTTP in `CommandGatewayTests`).
