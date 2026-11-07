# 07: Download a Run, and Copy quote

**What to build:** Download saves a Run as a zip of its frozen inputs, results, lineage and diagnostics (ADR-0010
§7), from the dock, from the Pricing DB and as `download run`. Copy quote puts the Run's header and each case's CVA
Charge Diff and PV Diff on the clipboard as HTML and plain text (§7 note of 2026-11-07), read from the Run alone, and
is the command `copy quote`.

**Blocked by:** 02.

**Status:** ready-for-agent

- [ ] The zip of a finished Run holds its record, its accepted input and its result.
- [ ] Copy quote's plain text names the Run, counterparty, netting set, value date, revision and currency, says
      simulated, and gives each case's CVA Charge and PV.
- [ ] A one-sided case's quote names the side it quotes.
