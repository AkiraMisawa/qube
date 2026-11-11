# 01: A line that cannot be run stays open and says why

**What to build:** `execute` answers the reason when a command's `run` throws, as it answers `unavailable`
(ADR-0006's note 1 of 2026-11-11). The command line runs the command on Enter and, on a reason, stays open with what
was typed and shows the reason where it shows a missing argument. A command run by a chord shows the reason as a
notice. Every command that reads its arguments in `run` (`set`, `vary`, `scenario …`, `case remove`, `cds`,
`measures`, `currency`, `explain`, `trace …`) is covered by the one catch.

**Blocked by:** nothing.

**Status:** done

- [x] `execute` answers a thrown reason and the line does not record the command as used.
- [x] `set <trade> a` with no value keeps the line open with its text and the reason; corrected, it runs.
- [x] `vary CSA-A threshold` with no value, and `scenario duplicate s1` with no copy's name, do the same.
- [x] A chord whose command throws shows the reason as a refused notice.

Done 2026-11-11. `execute` catches (`commands.test.ts`), and the line reopens within the event that closed it
(`xva.spec.ts`, "a line that cannot be read"). The same test presses `focus grid`'s chord with no panel open and reads
the refused notice.

After the code review, 2026-11-11: `execute` catches a `CommandRefused` only. A command refuses with that type, and
anything else it throws is a defect, thrown on (it was shown to the user as a reason).
