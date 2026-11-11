# 12: `POST /api/commands/preview` reads a line and executes nothing

**What to build:** The server reads a line as typed for an application and a workspace at a revision (ADR-0016 §5 and
note 11): the command, its inputs ready to invoke, each argument as read, the rows (count and the first few), what
would change in one sentence, or the word that cannot be read with where it is and what it could be; and what could
follow the last word. For `undo` and `redo` it names what would be taken back or put back.

**Pull request:** 1c

**Blocked by:** 11.

**Status:** ready-for-agent

- [ ] `set where ccy=USD after off` answers its inputs, `trades, 12 rows` and `A: 9 on to off, 3 already off`.
- [ ] A word that names no field, an id that names several rows and a value its form refuses answer the word, its
      place and candidates.
- [ ] The last word half typed answers what could complete it: rows, `all`, subsets, `where`, fields, operators, a
      choice's values, ids of the table a field points into.
- [ ] `undo` answers the command it would take back, whose it was and when; nothing is written by any preview.
