# 02: The preview takes a view and answers rows by key

**What to build:** `CommandLines.Preview` and `POST /api/commands/preview` take `view`. A line that says `selected` or
`shown` answers inputs whose `rows` are the keys resolved and whose `table` is the view's. Each argument says where it
came from (`typed`, `context`). The answer says the view in one sentence. What could follow offers `selected` and
`shown` with their counts. A command of many keys is named by its first three and the count of the rest (notes 17 and
19).

**Pull request:** 2a

**Blocked by:** 01.

**Status:** done

- [x] `set selected checked on` answers inputs with the keys, and running them writes those rows and no other.
- [x] The answer's `view` reads `readings (manual), 2 selected, R1 · note`.
- [x] `selected` and `shown` are offered after the command's words, with their counts, only where the view has them.
- [x] A command of five keys is named `set R1, R2, R3 and 2 more checked on` in the log and by `undo`; one the
  preview keyed is named with its table's word (`set readings R1, R2, R3 and 2 more note wet`).
- [x] HTTP: the endpoint takes `view` and answers the sentence.

## Comments

- As built: the rows of a screen that come to none are refused, since rows by key cannot say "none": `NoRowMatches`
  after a `where`, `NothingShown` for an empty grid. A view that names a table or subset the workspace has not, or
  positions in a table no reader pages, is the page's fault: `InvalidView`, HTTP 400, never a reading.
- The inputs name the table by its word where the word names one table (so the log reads as a line), else by its id.
- The review of 2a (2026-11-11) found, and this pull request fixed: keys joined into a line's rows were read again at
  Enter, so a single key that is a subset's word wrote the subset (now `UnsayableRow`, refused before Enter); a range
  of no row read as nothing selected and fell back to the focused row, and a sort was checked only where a position
  was read in it (now `InvalidView`, checked whole as the reading starts); a page that filters the rows it holds
  names the rows it shows, and `shown` is those; the row of a table of one row is said by its table alone.
