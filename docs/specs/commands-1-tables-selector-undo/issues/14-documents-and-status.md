# 14: The documents say what step 1 built

**What to build:** ADR-0016's Delivery order marks step 1 built and names what it settled; ADR-0006 and ADR-0010 §9
point at the server's reader where they describe `set`; `docs/10-api-contracts.md` gains the gateway, the preview and
the new capabilities; `IMPLEMENTATION_STATUS.md` says what runs, how it was verified, the measurement at 10,000 rows
and what is next.

**Pull request:** 1c

**Blocked by:** 13.

**Status:** done

- [x] The status names the three pull requests, the suites' counts and the measurement.
- [x] No document describes `set` as read on the client.

## Comments

- 2026-11-11: done. ADR-0016's Delivery order marks step 1 built and its note 15 says what the third pull request
  settled; ADR-0006 gains note 5 and ADR-0010 §9 a sentence, both pointing at the server's reader;
  `docs/10-api-contracts.md` names the preview and the commands the page runs through the gateway.
