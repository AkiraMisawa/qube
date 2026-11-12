# 06: The page is told each line's answer

**What to build:** The door tells its page what each line's command answered (ADR-0016 note 64), and one flow through
the built client.

**Pull request:** 6

**Blocked by:** the ticket before it.

**Status:** done

- [x] After a run the page is told each line's command and answer, in order, then the run's own.
- [x] A run that stopped is a notice that stays.
- [x] In the browser: a sequence is saved from the line, read, run and taken back with `undo`.
