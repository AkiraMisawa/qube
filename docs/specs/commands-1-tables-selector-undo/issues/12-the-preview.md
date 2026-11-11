# 12: `POST /api/commands/preview` reads a line and executes nothing

**What to build:** The server reads a line as typed for an application and a workspace at a revision (ADR-0016 §5 and
note 11): the command, its inputs ready to invoke, each argument as read, the rows (count and the first few), what
would change in one sentence, or the word that cannot be read with where it is and what it could be; and what could
follow the last word. For `undo` and `redo` it names what would be taken back or put back.

**Pull request:** 1c

**Blocked by:** 11.

**Status:** done

- [x] `set where ccy=USD after off` answers its inputs, `trades, 12 rows` and `A: 9 on to off, 3 already off`.
- [x] A word that names no field, an id that names several rows and a value its form refuses answer the word, its
      place and candidates.
- [x] The last word half typed answers what could complete it: rows, `all`, subsets, `where`, fields, operators, a
      choice's values, ids of the table a field points into.
- [x] `undo` answers the command it would take back, whose it was and when; nothing is written by any preview.

## Comments

- 2026-11-11: done (`CommandLines`, `ILineCommand`, `TableCompletions`, `SelectorReader.Scan`; `CommandLineTests` on the
  station readings, `PricerTableTests` on the pricer, `CommandGatewayTests` over HTTP). What it settled is ADR-0016's
  note 15.
- The request is `{ line, workspaceId, revision? }`: no application, since the workspace says whose tables it holds. A
  line that cannot be read is a 200 that holds its refusal; a workspace is refused by status (404, 403, 409).
- The verbs plan before they write (`TableService`'s `Plan`), and the preview's sentence is made from that plan, so a
  reading and a run cannot disagree. A plan costs what the write costs without the store: 0.04 to 0.17 s for 5,999
  rows by row at 10,009 trades.
- One value may follow its field with no sign (`set CSA-A threshold 5mm`), read by `entity.set` itself; before, only
  the client's reader took that spelling, and the command took `threshold=5mm`.
- `undo` and `redo` answer the moment apart from the sentence (`at`), and their inputs name the command read.
- An argument as read has no origin yet (typed, default, context): there is no context until step 2.
