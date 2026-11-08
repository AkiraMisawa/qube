# 08: The AI reaches `vary`, `add trades` and `set`

**What to build:** `vary`, `add trades` and `set` are command capabilities (ADR-0005, ADR-0010 §9), reached through the
command gateway with origin `ai`, with the same checks and answers as the command line, and recorded in the log with
their origin.

**Blocked by:** 02, 05, 06.

**Status:** ready-for-agent

- [ ] Each capability is listed with its input contract and refuses a bad cell by name.
- [ ] An AI `vary` makes the same cases the command line does, logged with origin `ai`.
