# 13: The Rates page has a door, a screen and places

**What to build:** The Rates page registers a command door, the view of its line grid (table, selection, focused cell)
and its places. The kernel's verbs, `find`, `go`, the chain key and the Context Menu's `Reset this cell` then work
there.

**Pull request:** 3c

**Blocked by:** 12.

**Status:** ready-for-agent

- [ ] With two lines selected, `set notional 5mm` writes both, and `undo` takes it back on the page.
- [ ] The line says the screen it was opened on.
- [ ] `find <line>` puts the Focus on the line.

## Comments
