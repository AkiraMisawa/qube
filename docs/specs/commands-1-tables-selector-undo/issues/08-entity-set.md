# 08: `entity.set` writes rows of any registered table and says what it did

**What to build:** The capability `entity.set` 1.0.0 in the kernel (ADR-0016 §3, §6, note 9): a selector and one or
several fields with their values as written, read by the field's form, checked by the domain, and written as one
all-or-none batch at the revision named, under one command id with the command's words. A row that already holds the
value gets no edit. A subset or `all` with no condition writes the field's whole-set edit where it registered one. The
result counts the rows matched, changed and already holding the value, names the table and the first rows, and carries
the command id and a sentence in titles.

**Pull request:** 1b

**Blocked by:** 07.

**Status:** ready-for-agent

- [ ] Rows by id, by `where` and over a subset; several fields at once; one refused value leaves nothing written.
- [ ] A row that holds the value is counted and not written; a command that changes nothing keeps the revision and is
      done.
- [ ] A whole-set edit is one log entry; a row's later edit wins over it.
- [ ] The sentence says the value as it was read (`5` typed for a threshold is `5mm`) and the counts.
- [ ] `workspace.undo` takes the whole batch back.
