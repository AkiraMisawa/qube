# 07: One selector says which rows

**What to build:** `RowSelector` and `Condition` as a typed value with its contract, `SelectorReader.Read` for the
line's spelling and `Selectors.Resolve` for the rows (ADR-0016 §2, notes 7 and 8): ids and the end of an id, `all`, a
subset, `where` with `=`, `!=`, `<`, `<=`, `>`, `>=`, `~` and `in` joined by `and`, each value read by its field's
form. An amount is not compared across currencies. A period on a date counts from the table's date.

**Pull request:** 1b

**Blocked by:** 06.

**Status:** ready-for-agent

- [ ] Ids, the end of an id that names one row, and one that names several, refused with what it could mean.
- [ ] `all`, a subset, and a subset narrowed by `where`.
- [ ] Each operator on a number, a date, a flag, a choice and an id; `<` on text refused by name.
- [ ] `maturity>5y` counts from the table's date; an amount whose currency differs by row is refused unless the
      currency is pinned.
- [ ] An id with no table belongs to the one table that has it; one two tables have is refused with both.
- [ ] 10,000 rows resolve in a time linear in the rows (measured, not gated).
