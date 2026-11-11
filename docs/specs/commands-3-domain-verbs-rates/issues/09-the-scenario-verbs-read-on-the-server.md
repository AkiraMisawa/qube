# 09: The scenario verbs are read on the server

**What to build:** `scenario`, `scenario duplicate`, `scenario ladder`, `scenario cross`, `scenario save` and
`scenario load` say how they are typed and read their own lines, with a trial as their preview where they write the
workspace. The client's hand-written commands go.

**Pull request:** 3b

**Blocked by:** 04.

**Status:** done

- [x] `scenario s1 jpy_ois +10bp; usdjpy -5%` previews the scenario and its two shifts.
- [x] `scenario ladder` and `scenario cross` preview how many scenarios they would make, and the cap is refused there.
- [x] `scenario load` offers the saved sets.

## Comments

2026-11-11, as built (ADR-0016 note 32). All ten scenario verbs read their own lines. A generator over its own limit
is refused in the reading; a set that would leave more pairs than Price prices is still made, as before, and the
reading says that Price would refuse it. The generator's request field is `factors` (it was `text`). `scenario
save` writes no workspace, and its reading keeps nothing.
