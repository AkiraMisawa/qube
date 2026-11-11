# 06: The settings dialog and the line

**What to build:** `settings` opens a dialog listing each setting, its value and whether it was set; a choice is
changed there and any setting reset. `settings <name> <value>` and `settings reset <name>` on the line, completing
names and values from the catalog, on every page (ADR-0017 §4).

**Step:** 1

**Blocked by:** 02.

**Status:** done

- [x] The dialog lists the settings and changes and resets one.
- [x] `settings theme light` and `settings reset theme` run from the line; a value not offered is refused by name.
