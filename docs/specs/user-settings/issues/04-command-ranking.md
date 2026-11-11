# 04: The command ranking follows the user

**What to build:** `commands.usage` on the server; the line reads it at start and writes it as commands run. A browser
that holds `qube.commandUsage.v1` carries it over once, then removes it (ADR-0017 §5).

**Step:** 1

**Blocked by:** 02.

**Status:** ready-for-agent

- [ ] A command run in one browser ranks first in a fresh one.
- [ ] A browser's own ranking is carried over once and its local copy removed.
