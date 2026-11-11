# 08: The pricer's scenarios are a registered table

**What to build:** `xva.scenarios`, keyed by the scenario's name, with `included` as a flag and the count of its
shifts. `set s1 included off` and `remove s1` are the kernel's. `scenario include`, `scenario exclude`, `scenario
remove` and `scenarios clear` are short forms, and the Scenarios tab's controls go through the door.

**Pull request:** 3b

**Blocked by:** 02.

**Status:** ready-for-agent

- [ ] `set s1 included off` leaves the scenario out of Price, and `undo` puts it back.
- [ ] `remove s1` removes the scenario with its shifts; `scenarios clear` is `remove scenarios all`.
- [ ] The Scenarios tab's include box and Remove still do what they did.

## Comments
