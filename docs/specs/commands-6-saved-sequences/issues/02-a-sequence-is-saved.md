# 02: `sequence.save` keeps a sequence

**What to build:** The capability that keeps lines under a name as a saved item of the workspace's application
(ADR-0016 notes 60 and 61).

**Pull request:** 6

**Blocked by:** the ticket before it.

**Status:** done

- [x] Lines given as a list are kept in order, as written, each with what its first words named; saved again, they
      replace the owner's own.
- [x] Private unless shared with the owner's desk or all; a user with no desk is refused `desk`.
- [x] Refused with the line's number: a line that names no command, a verb of the screen, `selected` or `shown`,
      `undo`, `redo`, `apply`, a `sequence` verb, a command that neither edits nor starts work, and one that starts
      work before the last line.
- [x] A line that names a command and is refused as the workspace stands is kept.
- [x] No line, more than fifty lines and an empty line are refused.
- [x] The workspace is not changed, and the answer says the revision it stands at.
