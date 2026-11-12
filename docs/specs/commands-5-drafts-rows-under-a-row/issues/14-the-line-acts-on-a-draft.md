# 14: The line acts on a draft

**What to build:** `open <row>` asks the page for the row's dialog; with a draft in the view, `set`, `reset`, `new`
and `remove` are read on the draft and answer a line to add to it; `apply` writes every change as one command;
`cancel` discards; anything else is refused while the draft is open (ADR-0016 §10).

**Pull request:** 5c

**Blocked by:** 13.

**Status:** done

- [x] `open E00003` answers the dialog's row; a row the workspace has not is refused by name.
- [x] With a draft open, `set notional 5bn` names no row and answers the line the draft gains, with what it would
      change, and writes nothing.
- [x] `new leg eur 250,000` and `remove leg 1` are of the draft's row.
- [x] `apply` writes one command that `undo` takes back whole, and is refused where the workspace moved.
- [x] `price` and every command that is not the draft's is refused, saying a draft is open.
- [x] The line says the draft it is on and how many lines are pending.
