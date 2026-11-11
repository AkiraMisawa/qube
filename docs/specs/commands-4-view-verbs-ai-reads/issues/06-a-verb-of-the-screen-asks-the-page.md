# 06: A verb of the screen asks its page

**What to build:** A view verb's reading answers one thing asked of the page (ADR-0016 note 39): a place, a sort or a
filter, each with the sentence said once it is done. `sort` and `filter` are declared beside `find`. The door hands a
sort or a filter to the grid the view was read of, as it puts a place.

**Pull request:** 4b

**Blocked by:** 05.

**Status:** done

- [x] A reading of `sort` or `filter` names no command and one ask, a `sort` or a `filter`, with its sentence.
- [x] With no grid behind the line both are refused (`NoView`); a table said that is not the grid's is refused by name.
- [x] The door hands the ask to the grid its view was read of and says the sentence; where the grid cannot, the line
  stays with why and nothing is said done.
- [x] The catalog lists both as verbs of the screen, and the agent's tools hold neither.

## Comments

- *Added as built (ADR-0016 note 39):* a sort or a filter ends as a command done does, the keyboard back where it
  was; a reading of another revision than the page holds is asked for again, as a place is; the grid may be said by
  its table and subset, and another's is refused (`OtherGrid`).
- *Held by:* the preview over `Stations` (the readings name no command and one ask; `NoView`, `OtherGrid`), Vitest
  for the door (handed to the grid the view was read of, the sentence said, the line kept with why), and the host's
  catalog.
