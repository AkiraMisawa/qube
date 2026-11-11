# 11: A command capability says how it is typed

**What to build:** `CapabilityDescriptor` gains its command form, words and arguments (ADR-0016 §4), for `entity.set`,
`entity.reset`, `entity.remove`, `workspace.undo` and `workspace.redo`. The catalog serves it and the client's
contracts read it.

**Pull request:** 1c

**Blocked by:** 10.

**Status:** ready-for-agent

- [ ] `GET /api/catalog` answers the five commands with their words and arguments.
- [ ] A capability with no form answers none, and nothing else of the catalog changes.
