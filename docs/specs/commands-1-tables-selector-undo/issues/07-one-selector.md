# 07: One selector says which rows

**What to build:** `RowSelector` and `Condition` as a typed value with its contract, `SelectorReader.Read` for the
line's spelling and `Selectors.Resolve` for the rows (ADR-0016 §2, notes 7 and 8): ids and the end of an id, `all`, a
subset, `where` with `=`, `!=`, `<`, `<=`, `>`, `>=`, `~` and `in` joined by `and`, each value read by its field's
form. An amount is not compared across currencies. A period on a date counts from the table's date.

**Pull request:** 1b

**Blocked by:** 06.

**Status:** done

- [x] Ids, the end of an id that names one row, and one that names several, refused with what it could mean.
- [x] `all`, a subset, and a subset narrowed by `where`.
- [x] Each operator on a number, a date, a flag, a choice and an id; `<` on text refused by name.
- [x] `maturity>5y` counts from the table's date; an amount whose currency differs by row is refused unless the
      currency is pinned.
- [x] An id with no table belongs to the one table that has it; one two tables have is refused with both.
- [x] 10,000 rows resolve in a time linear in the rows (measured, not gated).

## Comments

- 2026-11-11: done, in `Tables/Selectors.cs` (`SelectorTests`). The reader looks at descriptions only and leaves the
  words after the rows to the verb; the resolver reads each table's rows once.
- No contract is registered for the selector in this step: contracts hold no list (ADR-0016 note 12), so the edit verbs
  take the rows as the line spells them and the typed `RowSelector` is the kernel's own value until step 3.
- Rows may be ids narrowed by `where` (`R1, R4 where station=north`); ids with `all` or a subset are refused.
- `~` is for text and compares without case; a period is text, so it takes `=`, `!=`, `~` and `in`.
- An amount's unit is pinned by `=` on its unit field, or the rows asked hold one unit; `in` on the unit pins nothing.
- Measured on a Release build with a table made for it, not gated: `where` on two fields took 6 ms at 10,000 rows, 51 ms
  at 100,000 and 0.84 s at 1,000,000; every row named by its id took 40 ms, 129 ms and 1.24 s, reading the rows included.
