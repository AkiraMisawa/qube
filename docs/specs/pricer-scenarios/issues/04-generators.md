# 04: `scenario ladder` and `scenario cross` make a set of scenarios at once

**What to build:** the generators of ADR-0010 §4: `scenario ladder <index> [<tenor>] <from>..<to> step <step>` makes a
scenario per size, and `scenario cross <list> x <list> [x …]` one per combination, each shifting every factor of its
combination, named from its shifts (`jpy_ois +50bp · usdjpy -10%`); a zero size is no shift, so the all-zero
combination is not made. The command line says how many it would make and the pairs that leaves against the cap before
it runs (the note of 2026-11-09), and over 49 scenarios it is refused.

**Blocked by:** 01.

**Status:** ready-for-agent

- [ ] A ladder of -50bp..+50bp step 10bp makes ten scenarios, without the zero.
- [ ] A cross of three sizes by three makes eight scenarios, those with one zero shifting the other factor alone.
- [ ] A generated scenario replaces one of its name, and the answer says how many it made and replaced.
- [ ] Over the limit, and on a step that does not divide the range, the generator is refused by name and makes nothing.
- [ ] The command line's preview counts the scenarios and the pairs against the cap as the generator is typed.
