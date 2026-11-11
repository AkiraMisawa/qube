# 09: Ctrl+Enter runs the line and keeps it open

**What to build:** Ctrl+Enter in the line runs the command as Enter does and keeps the line open: the result is said
in the line, the text is cleared, the view is read again, and the list is the one an opening shows. A refusal stays as
with Enter. Not while an IME composes (ADR-0016 §9).

**Pull request:** 2c

**Blocked by:** 05.

**Status:** ready-for-agent

- [ ] `set selected a off`, Ctrl+Enter, `undo`, Ctrl+Enter, Escape: two commands ran and the line said each result.
- [ ] After `go existing` chained, the next command's context is the Existing grid.
- [ ] A refused line stays with its text and reason, as with Enter.
- [ ] Ctrl+Enter while an IME composes is the IME's.
