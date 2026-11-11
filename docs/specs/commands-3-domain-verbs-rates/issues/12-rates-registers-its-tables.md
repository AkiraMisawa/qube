# 12: Rates registers its lines and its defaults

**What to build:** `rates.lines` and `rates.defaults` are registered tables with their fields' forms; `Percent` joins
`Rate` (ADR-0016 note 5). `set`, `reset` and `remove` then act on a Rates workspace's lines and defaults.

**Pull request:** 3c

**Blocked by:** none.

**Status:** ready-for-agent

- [ ] `set <line> notional 5mm` edits a line, and `remove <line>` removes one the workspace made.
- [ ] `set defaults` is the kernel's `set` on the defaults, each value by its field: a value alone is written.
- [ ] A rate is typed and shown in one form across the pricer and Rates.

## Comments
