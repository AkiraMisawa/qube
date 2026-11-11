# 04: `commands.preview`

**What to build:** A kernel capability that answers a line's reading with nothing run (ADR-0016 §5, §7, note 38): the
command and the inputs that run it, each argument as read, the rows meant, and what would change. A line that cannot
be read refuses the call with the reading's own code, sentence, word and candidates.

**Pull request:** 4a

**Blocked by:** 01.

**Status:** done

- [x] A line of `set` answers the command, its inputs, its rows and what would change, and writes nothing.
- [x] The inputs it answers, handed to the command, do what it said.
- [x] A short form answers the line it stands for.
- [x] A refused line refuses the call with the word at fault and what it could have been.
- [x] `find`, `selected` and `shown` are refused (`NoView`).

## Comments

- *As built (ADR-0016 note 38):* the inputs are JSON text, since a contract holds no object in an object. A reading
  with no command (a verb of the screen that did not refuse itself) is refused `NoView` too.
- *Added as built:* the capability is registered by a module of its own (`CommandLinesModule`), given the lines
  when first asked for, since the lines are built from the registry it registers into.
