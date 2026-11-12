# 03: `sequence.run` runs a sequence

**What to build:** The capability that reads each line again and invokes it through the gateway (ADR-0016 note 62).

**Pull request:** 6

**Blocked by:** the ticket before it.

**Status:** done

- [x] Each line is a command of its own in the log, with origin `workflow`, and `undo` takes them back one at a time.
- [x] The answer says the revision the run began at, the one it left, and each line with what its command answered.
- [x] A first line that is refused refuses the command with that line's code, and nothing runs.
- [x] A later line that is refused ends the run: the lines before it stay, and the answer says the line, why, and
      `stays`.
- [x] A workspace moved between two lines stops the run; a sequence saved again since it was read, and a line whose
      first words name another command than when it was saved, are refused.
- [x] Asked again with the same key it runs nothing twice.
- [x] A sequence of another user is run where it is shared, on the caller's own workspace; one that is not shared is
      not seen.
- [x] A sequence the AI runs keeps origin `ai` for its lines.
