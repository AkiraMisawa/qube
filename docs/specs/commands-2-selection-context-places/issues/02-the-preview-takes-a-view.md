# 02: The preview takes a view and answers rows by key

**What to build:** `CommandLines.Preview` and `POST /api/commands/preview` take `view`. A line that says `selected` or
`shown` answers inputs whose `rows` are the keys resolved and whose `table` is the view's. Each argument says where it
came from (`typed`, `context`). The answer says the view in one sentence. What could follow offers `selected` and
`shown` with their counts. A command of many keys is named by its first three and the count of the rest (notes 17 and
19).

**Pull request:** 2a

**Blocked by:** 01.

**Status:** ready-for-agent

- [ ] `set selected checked on` answers inputs with the keys, and running them writes those rows and no other.
- [ ] The answer's `view` reads `readings (manual), 2 selected, R1 · note`.
- [ ] `selected` and `shown` are offered after the command's words, with their counts, only where the view has them.
- [ ] A command of five keys is named `set R1, R2, R3 and 2 more checked on` in the log and by `undo`.
- [ ] HTTP: the endpoint takes `view` and answers the sentence.
