# 03: The Changes tab lists, edits and removes every change and case

**What to build:** an input tab `Changes`, last in the row, saying how many changes it holds, that lists the Common
changes, then each case with its own (ADR-0010 §3's note of 2026-11-08). Each change shows its entity, field, value and
Original's; its value is edited there with the grid's validation, or it is removed. A case is removed whole, and
`case remove <name>` and `cases clear` do the same from the command line. `open changes` opens the tab.

**Blocked by:** 02.

**Status:** ready-for-agent

- [ ] The workspace's changes list holds each Common change and each case's, with Original's value.
- [ ] Editing a case's change from the list changes that case only; removing it leaves the case without it.
- [ ] Removing a case removes its row from the next Price's Metrics.
- [ ] In a browser, the tab says its count and a case is removed from it.
