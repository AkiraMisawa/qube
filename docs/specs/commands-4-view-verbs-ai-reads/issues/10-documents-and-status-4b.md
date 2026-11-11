# 10: Documents and status for 4b

**What to build:** `docs/10-api-contracts.md` (the preview's `sort` and `filter`, a view's `where`, the page endpoint's
`where`, `/shown`), ADR-0016 notes 39 to 41 as built, `IMPLEMENTATION_STATUS.md`, and the pull request's pictures.

**Pull request:** 4b

**Blocked by:** 06 to 09.

**Status:** done

- [x] The documents say what is built and what is left.
- [x] The pull request shows the screens.

## Comments

- `docs/10-api-contracts.md` has the preview's `sort` and `filter`, a view's `where`, `/shown` and the pricer's page
  endpoint, which it did not list before. ADR-0016 notes 39 to 41 say what changed as built.
- *In review:* the Rates line grid's filter line was looked at at the four widths in both themes too. The line of
  the CSA grid takes the height it is drawn at (it wraps where the tab is narrow), and no longer a height assumed.
  A test of the line's list (`workbench.spec.ts`, ADR-0006) read the list before the Rates page had drawn its grid,
  whose `focus grid` joins the list: it failed once in a full run here and now waits for the grid.
