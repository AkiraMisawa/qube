# 03: Rows left out are the selection, and `reset` alone is the focused cell

**What to build:** `set` and `reset` with no rows, whose first words name a field of the view's table, act on the
view's selection, or on the focused row where nothing is selected. `reset` with no word acts on the focused cell. A
field of a table of one row is read first; words that name both are refused with both spellings. The rows argument
then says `context` (note 18).

**Pull request:** 2a

**Blocked by:** 02.

**Status:** ready-for-agent

- [ ] `set note wet` with two rows selected reads as those two rows, `from: context`.
- [ ] With nothing selected it reads as the focused row; with no focus it is refused, saying nothing is selected.
- [ ] `reset` alone reads as the focused cell's row and field.
- [ ] `set alarm 5mm` still reads as the table of one row; a word both have is refused with the two spellings.
- [ ] With no view every line reads as it did in step 1.
