# 10: The CSA's other fields, by group, and its four templates

**What to build:** The pricer's CSA carries the general and per-side fields of the owner's examples, each under its
group, and four templates that take a currency.

**Pull request:** 5b

**Blocked by:** 09.

**Status:** done

- [x] Each field is read from the synthetic source and named in a command by its words, with its group's where two
      sides share a word.
- [x] A field the source holds and no one edits is refused as not editable.
- [x] Each of the four templates writes its terms for the currency given.
- [x] Each CSA of the accepted input carries the fields under `details`, as the case holds them (ADR-0016 note 53,
      added 2026-11-12).
