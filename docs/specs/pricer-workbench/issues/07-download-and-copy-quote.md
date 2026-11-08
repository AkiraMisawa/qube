# 07: Download a Run, and Copy quote

**What to build:** Download saves a Run as a zip of its frozen inputs, results, lineage and diagnostics (ADR-0010
§7), from the dock, from the Pricing DB and as `download run`. Copy quote puts the Run's header and each case's CVA
Charge Diff and PV Diff on the clipboard as HTML and plain text (§7 note of 2026-11-07), read from the Run alone, and
is the command `copy quote`.

**Blocked by:** 02.

**Status:** done

- [x] The zip of a finished Run holds its record, its accepted input and its result.
- [x] Copy quote's plain text names the Run, counterparty, netting set, value date, revision and currency, says
      simulated, and gives each case's CVA Charge and PV.
- [x] A one-sided case's quote names the side it quotes.

*Done 2026-11-08.* Download is the kernel's: `GET /api/runs/{id}/archive` writes `run.json` (the record, with the
pinned workflow and every invocation: its lineage and diagnostics), each value the Run references under `data/`, and
`manifest.json`, which gives each value's roles (`input:xva/acceptedInput`, `invocation:…`, `output:xva`) and whether
it is retained, expired or missing, never leaving one out. A running Run, and another owner's, are refused. The quote
is XVA's and the server's: `GET /api/xva/runs/{id}/quote` reads the Run's accepted input and XVA result and answers
text and HTML; the page puts both on the clipboard, or refuses where the browser cannot hold HTML, rather than copy
half. A failed or refused figure is quoted as what it is, never 0. *Decided here (DEFAULT):* PV is a metric of CVA's
measure, since the request that prices CVA values the portfolio; the fake engine's PV is each trade's official PV, or
a new trade's PV Adj (0 for one dealt at market), in the result's currency. Download is on the dock, on each Pricing
DB row and the command `download run`; Copy quote on the dock and `copy quote`.

