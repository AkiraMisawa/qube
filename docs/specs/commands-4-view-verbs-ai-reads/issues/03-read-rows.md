# 03: `tables.read`

**What to build:** A kernel capability that answers rows of a registered table (ADR-0016 §7, note 38): the rows as
`entity.set` takes them, the fields wanted by their words, and a page. The answer is the count the selector matched
and a bounded page of rows, each with its key, its subset and its cells as text that `set` reads back.

**Pull request:** 4a

**Blocked by:** 01.

**Status:** done

- [x] Rows by `where`, by ids, by a subset and `all` are read by the one selector, with the count.
- [x] A cell is written as the line types it (`5mm`, `on`, `empty`), in the order the fields were asked.
- [x] With no field asked the rows are their keys alone.
- [x] A page starts where asked, holds 50 rows by default and 200 at most, and says how many rows follow; many
      fields make it hold fewer rows, to 2,000 cells.
- [x] An unknown field is refused with the fields there are; a field asked twice is refused.
- [x] `selected` and `shown` are refused (`NoView`).
- [x] A trade row of the pricer reads as its grid shows it.

## Comments

- *As built (ADR-0016 note 38):* the page is the smaller of the limit and 2,000 cells over the fields asked, so 20
  fields give 100 rows; a limit over 200 is the contract's refusal (`ValidationFailed`), never a cut.
- *Added as built:* a field asked twice is `FieldTwice`. The pricer's rows are compared with what the grid's own
  page endpoint answers, through the agent's endpoint.
