# 08: The pricer's scenarios are a registered table

**What to build:** `xva.scenarios`, keyed by the scenario's name, with `included` as a flag and the count of its
shifts. `set s1 included off` and `remove s1` are the kernel's. `scenario include`, `scenario exclude`, `scenario
remove` and `scenarios clear` are short forms, and the Scenarios tab's controls go through the door.

**Pull request:** 3b

**Blocked by:** 02.

**Status:** done (changed as built: no table in this step)

- [ ] `set s1 included off` leaves the scenario out of Price, and `undo` puts it back. *Not built: step 5.*
- [x] `scenario exclude s1` leaves the scenario out of Price, says so before Enter, and `undo` puts it back.
- [ ] `remove s1` removes the scenario with its shifts; `scenarios clear` is `remove scenarios all`. *Not built:
      step 5.*
- [x] `scenario remove s1` and `scenarios clear` remove them, each saying what it would remove before Enter.
- [x] The Scenarios tab's include box and Remove still do what they did, through the door.

## Comments

2026-11-11, as built. The scenarios are not a registered table in 3b, and the two criteria that need one are left
unmet and moved to step 5: ADR-0016 note 32 says why (a name of several words, inclusion as one list of the
workspace, a scenario as its shifts). `scenario include`, `scenario exclude`, `scenario remove` and `scenarios clear`
are domain verbs with a trial as their preview, and the Scenarios tab's gestures invoke them through the door.
