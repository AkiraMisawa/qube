# 12: `copy`

**What to build:** `copy [<rows>] [<fields>] [with headers]` puts the rows' cells on the clipboard, written for a
spreadsheet (ADR-0016 note 44).

**Pull request:** 4c

**Blocked by:** 11.

**Status:** done

- [x] The reading says the rows, the fields and what Enter would copy; rows left out are the Selection or the row
      the keyboard is on; more cells than the cap, and no row, are refused.
- [x] `FieldForms` writes a value as a cell: a number plain, a rate and a spread in percent, a flag `true` or
      `false`, none empty.
- [x] `POST /api/commands/content` answers the text of the line read at the revision given, and refuses a
      workspace that has moved.
- [x] The door writes the clipboard and says the server's sentence; a browser that refuses is said as that.
- [x] In a browser, the clipboard holds the rows after `copy`.

## Comments

- The reader of a selector is told that a list of fields follows (`fieldsFollow`): the first field ends at its comma,
  and words after a table's own that name a field of it say no rows.
- More words that name a field of the screen's table than name a table are that field (`trade id`, where `trade` is
  a table's word). This holds for every verb that reads rows and amends note 18 (ADR-0016 note 44, "Changed as built").
- The door is given where the cells go (`TakenOut`: the clipboard and a file), the browser's unless a test gives
  another. Only Chromium was exercised: a browser that wants the clipboard written inside the key press itself may
  refuse it after the round trip, which is then said with its reason (status, known limits).
- A text is written as it is; what to do with one a spreadsheet reads as a formula is put to the owner (note 44).
