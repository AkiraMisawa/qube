# 02: A notice has a kind, takes no room, and says what was done in titles

**What to build:** `notify(kind, message)` with `done` and `refused` (ADR-0006's note 2 of 2026-11-11, ADR-0016 §6);
every call site states its kind. One notice at a time, drawn over the top of the main area in a strip that takes no
room: done is teal with a tick and leaves after six seconds unless the pointer or the focus is on it, refused is red
with a cross and stays until closed or replaced. Only a refused notice is reported to the server's log (ADR-0014
§4). `before` and `after` say how many trades they ticked or cleared, and Price the Run it was accepted as. A refused
edit is named by its row and column in words (`refusedEdit`), never by an entity's key.

**Blocked by:** 01.

**Status:** done

- [x] A done notice is teal, a `status`, and leaves by itself; a refused one is red, an `alert`, and stays.
- [x] A notice does not move the page under it.
- [x] `before none` says `Cleared Before on N existing trades.`; Price names its Run.
- [x] A refused change of the measures or of the display currency names them in words.
- [x] Every call of `notify` in the client states its kind, Rates', the Pricing DB's and the views' included.

Done 2026-11-11. `notify(kind, message)` is typed, so a call without its kind does not compile. The strip is
`client/src/shell/Notice.tsx`; the browser test runs on the page's own clock (`page.clock`), so "after six seconds"
and "still there a minute later" are read and not waited for. Looked at in both themes at 1440px and at 390px.

After the code review, 2026-11-11:

- The pointer and the keyboard hold a done notice by a hold each (`leavesByItself`, `notices.ts`): with one flag, the
  pointer leaving let go of a notice the keyboard was still in. The browser test stops the clock once a notice is in
  view and reads both holds.
- A queued edit carries its title, the row's id and the column's header as its grid shows them, and `refusedEdit`
  says it; Rates does the same (`lineFieldTitle`). A refusal read `DRAFT-1 imR was not changed`.
- A done notice that counts edits in conflict or lost stays (`stays`), as a refusal does.
- The browser test reads the colour's token and the icon, and one test per width of design-guidelines §9 reads that a
  notice lies under the header, inside the page, and moves nothing. Looked at in both themes at 1440, 992, 768 and
  390px.
