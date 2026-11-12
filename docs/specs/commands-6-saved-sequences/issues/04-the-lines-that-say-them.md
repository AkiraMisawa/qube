# 04: The lines `sequence save` and `sequence run`

**What to build:** The two commands as a person types them, with their readings and what the line offers (ADR-0016
note 63).

**Pull request:** 6

**Blocked by:** the ticket before it.

**Status:** done

- [x] `sequence save <name> [desk|all] <line> then <line>` reads its lines apart at `then`, and its reading says each
      with its number, whom the sequence is for and whether it replaces one.
- [x] The save's reading says which lines are refused as the workspace stands, and refuses what the save refuses.
- [x] `sequence run <name>` says every line and what the first would do; a first line that is refused refuses the
      reading.
- [x] The line offers `desk` and `all` after the name, the commands a sequence may hold where a line starts, and the
      sequences the user can see after `sequence run`.
- [x] `undo`'s reading says "a saved sequence for you" of a command a sequence ran.
