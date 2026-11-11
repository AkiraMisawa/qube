# 05: `measures`, `case remove` and `cases clear` are capabilities

**What to build:** `xva.measures`, `xva.case.remove` and `xva.cases.clear` are new capabilities that say how they are
typed and read their own line, with a trial as their preview. `measures` offers the measures not typed yet; `case
remove` offers the cases' titles. The client's hand-written commands go, and the page's controls (the measures' buttons,
the Changes tab's remove) call them through the door (ADR-0016 note 28).

**Pull request:** 3a

**Blocked by:** 04.

**Status:** done

- [x] `measures cva fva` previews the selection it would leave, and an unknown measure is refused by name.
- [x] `case remove` offers each case's title and removes the one named; a title no case has is refused.
- [x] `cases clear` says how many cases it would remove, and with none says so and writes nothing.
- [x] The measures' buttons and the Changes tab still do what they did, and `undo` takes each back.

## Comments

- A measure's button invokes `xva.measures` with the selection it would leave. It says nothing when taken (the button
  shows it), a refusal is a notice, and `undo` names a press as it names the line (`measures cva fva`). The request's
  `measures` has no least length, so a selection of none is refused by the command (`NoMeasure`), not the contract.
- The page's `/measures`, `/cases/remove` and `/cases/clear` endpoints are gone. The tests that chose measures
  through the endpoint choose them through the command.
- A case is named by its title, of several words, so `case remove` and `cases clear` are domain verbs and not the
  kernel's `remove` (ADR-0016 note 28).
