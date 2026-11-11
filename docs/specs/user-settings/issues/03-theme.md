# 03: The theme follows the user

**What to build:** `shell.theme` (`dark`, `light`, `auto`; registered `dark`). The page's colour scheme manager reads
and writes it; the header's toggle and `toggle theme` set it; its last value is kept in the browser for the first frame
only (ADR-0017 §5, §6.2).

**Step:** 1

**Blocked by:** 02.

**Status:** ready-for-agent

- [ ] A theme set in one browser is the theme in a fresh one.
- [ ] `auto` follows the OS's scheme.
