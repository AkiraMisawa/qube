# 03: Rows left out are the selection, and `reset` alone is the focused cell

**What to build:** `set` and `reset` with no rows, whose first words name a field of the view's table, act on the
view's selection, or on the focused row where nothing is selected. `reset` with no word acts on the focused cell. A
field of a table of one row is read first; words that name both are refused with both spellings. The rows argument
then says `context` (note 18).

**Pull request:** 2a

**Blocked by:** 02.

**Status:** done

- [x] `set note wet` with two rows selected reads as those two rows, `from: context`.
- [x] With nothing selected it reads as the focused row; with no focus it is refused, saying nothing is selected.
- [x] `reset` alone reads as the focused cell's row and field.
- [x] `set alarm 5mm` still reads as the table of one row; a word both have is refused with the two spellings.
- [x] With no view every line reads as it did in step 1.

## Comments

- As built: a line whose first words name a table and a field of the screen's table is refused too (`AmbiguousWords`),
  with the table's other word and `selected <word>` as the two spellings. The pricer's CSA table therefore answers to
  `csas` only, so that `set csa CSA-B` on a selection of trades is the trade's field (ticket 04).
- A table of one row now takes several values with their signs and no table word (`set alarm=5mm daylight=off`), as
  the screen's table does: the reader looks for a field before the first sign.
- Where rows may be left out, the fields of the screen's table are offered, each saying what it would act on
  (`note (2 selected)`, or the focused row's key).
- `reset` alone with no focused cell, or on a column that is no field of the table, is refused (`NoFocus`).
