# 02: A notice has a kind, takes no room, and says what was done in titles

**What to build:** `notify(kind, message)` with `done` and `refused` (ADR-0006's note 2 of 2026-11-11, ADR-0016 §6);
every call site states its kind. One notice at a time, drawn over the top of the main area in a strip that takes no
room: done is teal with a tick and leaves after six seconds unless the pointer or the focus is on it, refused is red
with a cross and stays until closed or replaced. The line removes the notice in view when it runs a command. Only a
refused notice is reported to the server's log (ADR-0014 §4). `before` and `after` say how many trades they ticked or
cleared, Price the Run it was accepted as, `measures` and `currency` what they set. A refused edit is named by its
row and column titles (`editTitle`), never by an entity's key.

**Blocked by:** 01.

**Status:** ready-for-agent

- [ ] A done notice is teal, a `status`, and leaves by itself; a refused one is red, an `alert`, and stays.
- [ ] A notice does not move the page under it.
- [ ] Running a command from the line removes the notice in view.
- [ ] `before none` says `Cleared Before on N existing trades.`; Price names its Run.
- [ ] A refused change of the measures or of the display currency names them in words.
- [ ] Every call of `notify` in the client states its kind, Rates', the Pricing DB's and the views' included.
