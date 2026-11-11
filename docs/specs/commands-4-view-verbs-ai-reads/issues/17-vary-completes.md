# 17: `vary` completes

**What to build:** After `vary`, the line offers the rows a cell is named on, then the row's columns, then a flag's
or a choice's values, again after each comma (ADR-0016 note 45).

**Pull request:** 4d

**Blocked by:** 16.

**Status:** done

- [x] `vary ` offers the CSAs, the netting set, `credit`, `cds` and the modules with settings, and trades by id as
      typed, a few at a time with how many more.
- [x] After a row, its columns by the word that names each, a column of several words written from its first.
- [x] After a flag or a choice, its values, and again after a comma; nothing for an amount, a rate or an id.
- [x] Nothing while the verb's own word is typed.

## Comments

- The rows that are no trade come first, then trades, twenty at a time, each with what it is.
- A column is found by any of its names as far as typed and offered by the word that names it.
- A column that may be empty offers `empty`, whatever its form, as `set` does.
- What a flag and a choice have to choose from is the kernel's `FieldForms.Choices`, which `set`'s completion asks
  too.
