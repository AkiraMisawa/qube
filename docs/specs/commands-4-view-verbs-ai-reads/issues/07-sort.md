# 07: `sort`

**What to build:** `sort [<table>] by <field> [desc] [, <field> [desc]]` and `sort off` (ADR-0016 note 40), setting the
grid's own Sort.

**Pull request:** 4b

**Blocked by:** 06.

**Status:** done

- [x] `sort by volume desc`, several fields and `sort off` each ask the page for that sort.
- [x] A field the table has not is refused with the fields there are; a field the table's reader does not sort by is
  refused (`NotSortable`).
- [x] It completes `by` and `off`, then the fields, then `desc`.
- [x] In a browser a trade grid sorted from the line shows the sort in its header and its rows in that order.
- [x] A grid whose page holds its rows is sorted from the line too.

## Comments

- *Added as built (note 40):* `by` is required; the reader is asked once for each field.
- *The last box, as built:* the pricer's CSA grid is sorted from the line and shows it in its header. The Rates line
  grid is not: it shows a term as typed (`10y`, `5mm`), which the grid would order as text, so its page names its
  columns as ones the grid does not sort by, and `sort by end` is refused on the line by the column's name. Sorting
  it by what a term means is left for later.
