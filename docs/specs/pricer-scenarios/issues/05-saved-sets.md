# 05: scenario sets are saved for oneself, a desk or everyone, and loaded again

**What to build:** `scenario save <set> [desk|all]` and `scenario load <set>` (ADR-0010 §4 and its note of
2026-11-09, C32): a store of saved sets, in memory and in SQLite beside the workspaces; a user's desk from
configuration; visibility by owner, desk and everyone; load adding the set's scenarios and replacing those of the same
name; an ambiguous name refused naming the owners, `<set>@<owner>` picking one. The command line completes `scenario
load` from the sets the user can see.

**Blocked by:** 01.

**Status:** ready-for-agent

- [ ] A private set is seen by its owner alone, a desk's by that desk, one for all by every user.
- [ ] Saving again replaces the owner's set; another user cannot replace it.
- [ ] `desk` from a user with no desk is refused by name.
- [ ] Load adds the scenarios, replaces those of the same name and says so; an ambiguous name is refused naming the
      owners.
- [ ] A set saved before a restart loads after it (SQLite).
