# 08: `focus grid` and the panel chords are `go`

**What to build:** `focus grid` runs `go` to the page's first grid and keeps its chord. A command that edits or
executes gives the keyboard back to where it was; only `go`, `find` and `open` move it.

**Pull request:** 2b

**Blocked by:** 06.

**Status:** ready-for-agent

- [ ] `focus grid` and its chord land where `go <first grid>` lands, on each page that has a grid.
- [ ] After `set`, `reset`, `undo` and Price from the line, the keyboard is where it was before the line opened.
