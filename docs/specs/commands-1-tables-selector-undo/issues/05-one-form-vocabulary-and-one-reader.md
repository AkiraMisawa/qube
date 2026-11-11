# 05: One form vocabulary, read and written in one place

**What to build:** `FieldForm` takes in the forms a cell has and a product's term lacks: a signed amount, a rate as a
fraction, a spread, a number of days and an id; currency, frequency and schedule become choices, and that a field may
be empty is the field's (ADR-0016 §1 and note 5). `FieldForms.Read` and `FieldForms.Write` are the one reader and
writer. `ProductReader.Parse` and `PortfolioCells.Read` and `Write` go through them and keep their sentences, and
`CellName.Form` goes.

**Pull request:** 1b

**Blocked by:** nothing.

**Status:** ready-for-agent

- [ ] Each form reads its spellings and refuses others, saying what the field is.
- [ ] What `Write` writes, `Read` reads back to the same value, for every form.
- [ ] A product's terms and a pricer's cells read as before: their existing tests pass unchanged.
