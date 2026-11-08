# 02: `vary` makes cases, and Price prices the Base and every case

**What to build:** `vary CSA-A threshold 10mm, 5mm, 0` makes one case per value that differs from the workspace's,
named from its change (`CSA-A threshold 5mm`), beside any case already made, never one identical to an existing
case; it answers what it made and skipped (ADR-0010 §3 and its note of 2026-11-08, §9's note on naming a cell). Price
says how many cases it prices (`Price CVA FVA · 4 cases`), refuses over the cap saying by how much, and prices the
Base and every case for every selected measure in one Run: XVA's, spot IM's and spot CCR capital's providers each
write a row per case. The Metrics dock shows a row per case and the collapsed dock a line per case.

**Blocked by:** 01.

**Status:** ready-for-agent

- [ ] `vary` on a CSA term, the netting set's CVA recovery and a trade field makes the cases, and each case reads its
      own value with the rest of the workspace.
- [ ] A value equal to the workspace's, and a case already made, are skipped and said; a row or column that resolves
      to nothing or to several is refused with what it could mean.
- [ ] A case's figures equal a Base priced with that value typed into the workspace, for CVA, FVA, spot IM and spot
      CCR capital.
- [ ] The Run's accepted input holds the Base first, then each case with its changes and trades.
- [ ] Price over the cap is refused with `TooManyCases` and the excess.
- [ ] In a browser, `vary` from the command line, then Price, shows a Metrics row per case.
