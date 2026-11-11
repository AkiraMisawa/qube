# 14: Documents and status for 4c

**What to build:** `docs/10-api-contracts.md` (the preview's `asks`, `/api/commands/content`), ADR-0016 notes 42 to
44 as built, `IMPLEMENTATION_STATUS.md`, Delivery order step 4, and the pull request's pictures.

**Pull request:** 4c

**Blocked by:** 11 to 13.

**Status:** done

- [x] The documents say what is built and what is left.
- [x] The pull request shows the screens.

## Comments

- `docs/10-api-contracts.md` has the preview's `asks` by kind and `POST /api/commands/content`. ADR-0016 notes 42 to
  44 say what was added and changed as built.
- The AI's `commands.preview` refuses `copy` and `export` as it refuses a verb of the screen, and names `tables.read`.
- Note 28's leftovers (`vary`'s completion, a domain verb's refusal marking its word) are a pull request of their
  own after this one, so Delivery order step 4 is not yet marked built.
