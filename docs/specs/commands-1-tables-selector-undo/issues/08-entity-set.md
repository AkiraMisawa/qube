# 08: `entity.set` writes rows of any registered table and says what it did

**What to build:** The capability `entity.set` 1.0.0 in the kernel (ADR-0016 §3, §6, note 9): a selector and one or
several fields with their values as written, read by the field's form, checked by the domain, and written as one
all-or-none batch at the revision named, under one command id with the command's words. A row that already holds the
value gets no edit. A subset or `all` with no condition writes the field's whole-set edit where it registered one. The
result counts the rows matched, changed and already holding the value, names the table and the first rows, and carries
the command id and a sentence in titles.

**Pull request:** 1b

**Blocked by:** 07.

**Status:** done

- [x] Rows by id, by `where` and over a subset; several fields at once; one refused value leaves nothing written.
- [x] A row that holds the value is counted and not written; a command that changes nothing keeps the revision and is
      done.
- [x] A whole-set edit is one log entry; a row's later edit wins over it.
- [x] The sentence says the value as it was read (`5` typed for a threshold is `5mm`) and the counts.
- [x] `workspace.undo` takes the whole batch back.

## Comments

- 2026-11-11: done, in `Tables/TableCommands.cs` (`TableService.Set`, `EntitySet`, `TablesModule`; `TableCommandTests`).
- The request is flat (ADR-0016 note 12): `workspaceId`, `revision`, `table`, `rows` as the line spells them, and `set`
  as `name=value` pairs. A name may be several words and so may a value (`note=very wet wind speed=14`): the longest
  run of words before a sign that names a field is the name. The request takes no sentence: the log names the command
  by its own words, which the server puts together (`set where station=south note=wet`).
- The answer's `command` is absent where nothing changed, and `rows` is the first ten keys of the rows changed.
- The sentence names up to three rows and counts more with the table's word; it lists only the fields that changed a
  row; a value is said as it was read (`5` for an amount is `5mm`).
- A value is the same as the one held when it says the same thing: numbers by value, the rest as JSON. An id is as
  written, so `csa-b` is not `CSA-B`.
- The revision is optional, as on every command but `undo` and `redo`: left out, the command acts on the workspace as
  it stands, and still refuses if it moved while the rows were read.
