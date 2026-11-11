# 06: The shell builds `open <tab>` from a page's tabs

**What to build:** A place says whether it is a tab. The shell builds `open <tab>` from the tabs of the page in the
active panel and `go <place>` from its other places. The pricer's seven hand-written `open <tab>` commands go
(ADR-0016 note 30).

**Pull request:** 3a

**Blocked by:** none.

**Status:** done

- [x] `open csa` shows the tab and puts the keyboard in it, and says so.
- [x] `go` offers the grids, the dock and a trace, and no tab.
- [x] `open result` and `open pricing db` are still their own commands.
- [x] A page with no tab offers no `open`.

## Comments

- One command per tab (`open csa`, `open netting`, …), built by the shell from the places that say `tab`, and not
  one `open` with an argument: a one-word command with an argument would read `open pricing db` and `open result`
  typed with no trailing space as its own, and those keep their words.
- `go <tab>` is no longer read: `go` offers the grids, the dock and a trace (ADR-0016 note 30).
