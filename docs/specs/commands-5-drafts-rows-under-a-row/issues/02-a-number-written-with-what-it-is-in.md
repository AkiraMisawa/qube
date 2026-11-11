# 02: A number written with what it is in

**What to build:** `FieldForm.Money`: a number followed by a unit that is one of the field's choices, or the number
alone (ADR-0016 note 47).

**Pull request:** 5a

**Blocked by:** nothing.

**Status:** ready-for-agent

- [ ] `-350,000 USD`, `-350k usd` and `-350000` read; what is written reads back to the same value.
- [ ] A unit that is none of the choices, and words that are no number, are refused with what the field takes.
- [ ] A cell for a spreadsheet writes the number and its unit.
