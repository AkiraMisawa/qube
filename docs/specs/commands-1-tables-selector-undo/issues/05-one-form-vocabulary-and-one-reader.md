# 05: One form vocabulary, read and written in one place

**What to build:** `FieldForm` takes in the forms a cell has and a product's term lacks: a signed amount, a rate as a
fraction, a spread, a number of days and an id; currency, frequency and schedule become choices, and that a field may
be empty is the field's (ADR-0016 §1 and note 5). `FieldForms.Read` and `FieldForms.Write` are the one reader and
writer. `ProductReader.Parse` and `PortfolioCells.Read` and `Write` go through them and keep their sentences, and
`CellName.Form` goes.

**Pull request:** 1b

**Blocked by:** nothing.

**Status:** done

- [x] Each form reads its spellings and refuses others, saying what the field is.
- [x] What `Write` writes, `Read` reads back to the same value, for every form.
- [x] A product's terms and a pricer's cells read as before: their existing tests pass unchanged.

## Comments

- 2026-11-11: done. `FieldForm`, `FieldSpec` and `FieldForms` are the kernel's (`FieldForms.cs`, beside `FlagWords`); a
  number is read as a decimal, and a product converts its own to doubles and still refuses an amount of nothing.
- One existing test line changed, since `CellName.Form` went: `An_amount_is_written_as_it_reads_back` makes its cell
  with `new FieldSpec(FieldForm.SignedAmount)`. Every other product and pricer test passes as it was.
- A currency, a frequency and a schedule cell are choices, so a refusal now says `one of GBP, JPY, USD, EUR` where it
  said `a currency, GBP, …`; no test or page read the old words.
- An empty text for a field that may be empty is refused (it was kept as an empty id): no value is said `empty`.
- A product's plain number now takes a scale (`2k`) and no longer an exponent (`1e3`); no product has such a field yet.
