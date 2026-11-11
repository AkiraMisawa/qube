# 01: A table under a row

**What to build:** A table's description names the table its rows are under and each row the row it is under; the
selector reads the row above's id as every row under it; the registration is checked; `tables.describe` says it
(ADR-0016 note 46).

**Pull request:** 5a

**Blocked by:** nothing.

**Status:** ready-for-agent

- [ ] `<table> <key>` names one row under a row, whole or by how its key ends.
- [ ] In a table under another, an id that names one row above names every row under it; one with nothing under it is
      refused, saying so.
- [ ] With no table said, an id is looked for among each table's own rows alone.
- [ ] A table under one the workspace has not, or a main table or a table of one row under another, fails when the
      tables are read, by name.
- [ ] `tables.describe` answers a table's `under`.
