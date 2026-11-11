# 09: The scenario verbs are read on the server

**What to build:** `scenario`, `scenario duplicate`, `scenario ladder`, `scenario cross`, `scenario save` and
`scenario load` say how they are typed and read their own lines, with a trial as their preview where they write the
workspace. The client's hand-written commands go.

**Pull request:** 3b

**Blocked by:** 04.

**Status:** ready-for-agent

- [ ] `scenario s1 jpy_ois +10bp; usdjpy -5%` previews the scenario and its two shifts.
- [ ] `scenario ladder` and `scenario cross` preview how many scenarios they would make, and the cap is refused there.
- [ ] `scenario load` offers the saved sets.

## Comments
