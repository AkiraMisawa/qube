# 03: A figure of the rows under a row

**What to build:** A field says it is a figure of a table under its row and how a write of it is written; `set` asks
the table for each row's changes, counts a row with none as holding the value, and says for one row what the table
says the write would do; a field names its unit for the workspace (ADR-0016 note 47).

**Pull request:** 5a

**Blocked by:** 01, 02.

**Status:** ready-for-agent

- [ ] `set <row> <figure> <value>` writes the changes the table answers, as one command.
- [ ] A row whose write changes nothing is counted as holding it, and a line of such rows alone changes nothing.
- [ ] The preview of one row says the table's own sentence; of several, the count.
- [ ] `reset <row> <figure>` writes the changes the table answers.
- [ ] A figure of a table that is not under its own fails when the tables are read, by name.
- [ ] `tables.describe` answers a field's `figureOf` and `unit`.
