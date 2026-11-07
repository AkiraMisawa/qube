# 08: One screen on every width

**What to build:** the layout of ADR-0010 §1 finished: the context bar (value date, counterparty searched by code,
entity and name, main netting set with enforceability, display currency, Refresh, measures, Price) with its status
line (source version, revision, `SIMULATED`); the input tabs collapsible to their tab row with a drag handle for
their height; the result dock collapsible to one line with each measure's headline Diff, and drag-resizable; a result
older than the workspace in yellow; and below 768px three tabs, Inputs, Trades and Results. Commands open each input
tab. One browser test drives the whole flow by keyboard.

**Blocked by:** 03, 05, 06, 07.

**Status:** ready-for-agent

- [ ] The browser flow passes: new workspace, a CSA edit that moves a trade into FVA, the marked cells, Price, the
      Metrics and counts, Copy quote, Download.
- [ ] At 390px there is no sideways scroll and the three tabs reach every part.
- [ ] Light and dark themes both keep the text's contrast.
