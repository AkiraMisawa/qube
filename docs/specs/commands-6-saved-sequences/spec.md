# Generic commands, delivery step 6: saved sequences of edits

Status: built (2026-11-12, one pull request)

The decision is ADR-0016 §8 (a saved sequence of edits is a second kind of saved definition) with its notes of
2026-11-12 for step 6 (60 to 64), and C32 for whose a saved item is. This spec orders ADR-0016's Delivery order,
step 6, and names the seams it is tested at. The owner asked on 2026-11-11 for every step to be implemented without
a question at each one; the choices are DEFAULTs, in the notes, and are reported.

## Problem Statement

A user who prepares the same what-if on each counterparty types the same commands each time: clear After, put the
USD trades back, price. The chain key (§9) makes that a few lines, and nothing keeps them. A workflow cannot hold
them: a command is not a workflow step, since a Run is a function of its accepted input and a retry would edit twice
(§8).

## Solution

A user saves an ordered list of command lines under a name, for themselves, their desk or all, as a scenario set is
saved. `sequence run <name>` reads each line again on the workspace it is run on and invokes it through the command
gateway as its own command, so `undo` reaches each. The run stops at the first refusal and says where it began. Its
last line may start work on the revision the lines before it left, such as `price`.

## User Stories

1. As a desk user, I type `sequence save usd-only after none existing then set existing where currency = USD after
   on then price` and the three lines are kept under `usd-only`, for me alone.
2. As a desk user, I type `sequence run usd-only` on another counterparty's workspace; the line says the three
   lines and what the first would do, and Enter runs them and prices.
3. As a desk user, I type `undo` after a run and the last edit it made is taken back, then the one before it.
4. As a desk user, a line that is refused in the middle stops the run, and the notice says the line, why, and the
   revision the workspace was at before the run.
5. As a desk user, I share a sequence with my desk (`sequence save usd-only desk …`) and a colleague runs it on a
   workspace of their own.
6. As a desk user, I am refused when I save `find E00003`, `set selected …`, `undo` or a `price` that is not last,
   with the line's number and why.
7. As the AI, I save a sequence by giving its lines as a list, and run one by its name; each line it ran is in the
   answer with what its command answered.
8. As a Rates user, a sequence whose last line is `price` opens the Run it made, as `price` typed does.

## Implementation Decisions

- **Kernel, and no application's.** `Qube.Composition.Application` gains the two capabilities (`sequence.save`,
  `sequence.run`), their four contracts and a module that registers them; the saved item is kept by `SavedItems`
  under the kind `sequence:<app>` (note 60). A host registers the module beside the kernel's other commands.
- **What a line's first words name** is asked of `CommandLines` (a command, a verb of the screen or a short form,
  by id), since the rule of note 61 is about that and a reading that is refused does not say it.
- **The rule of note 61 is one function**, used by the save, by the save's reading and by the run as each line is
  reached.
- **The run** reads with `CommandLines.Preview` and invokes with `CommandService.InvokeAsync` (note 62). Its answer
  is a flat record with the lines that ran as a list of flat records, each with its command's whole answer as JSON
  text, since a contract holds no object in an object (note 12).
- **`undo`'s reading** says "a saved sequence for you" of a command of origin `workflow` (note 62).
- **The client** changes in one place: the door tells its page each line's answer (note 64).

## Testing Decisions

Seams (DEFAULT, reported): the kernel through `CommandLines.Preview` and the command service over the station
tables (`SensorRig`), with a saved-item store in memory and one command registered for the tests that starts work
and edits nothing; the pricer through `PricerRig`, for a sequence that ends with `price` and for one saved on one
counterparty and run on another; the host through its API for the wiring; the client's door through Vitest with the
API stubbed; and one Playwright flow through the built client. A test names the note it holds.

## Out of Scope

- Taking a saved sequence away, values given at the run, a sequence made from the log or recorded as typed, and a
  dialog to write one in (note 60's "Not built").
- All or nothing: §8 has the run stop at the first refusal with the lines before it written.
- A list of the saved sequences as a capability of its own: the line offers them as it is typed. The AI's
  `commands.preview` answers no completions, so the AI learns the names from `sequence run` with no name, whose
  refusal names three and counts the rest (corrected in review: this said the AI read the line's completions).
