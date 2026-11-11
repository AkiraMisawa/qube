# 15: Documents and status for 3c

**What to build:** ADR-0016's notes for 3c as built, the contracts document, `IMPLEMENTATION_STATUS.md`, and this
directory's statuses.

**Pull request:** 3c

**Blocked by:** 12 to 14.

**Status:** done

- [x] The notes say what was built and what was found.
- [x] `IMPLEMENTATION_STATUS.md` says what runs and what was verified.

## Comments

- *As built:* ADR-0016 notes 34 to 36 carry their "as built" and "found" entries; `docs/10-api-contracts.md` lists
  the Rates commands the gateway takes and the endpoints that went; `IMPLEMENTATION_STATUS.md` has the step's
  section.
- *Found with the pictures:* the Run panel's header wrote a Rates Run's `defaults` as `[object Object]`. It writes
  each value under its path now (`defaults.currency JPY`; `client/src/shell/runFacts.ts`, with its unit test).
