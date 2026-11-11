# 12: Rates registers its lines and its defaults

**What to build:** `rates.lines` and `rates.workspace`, which holds the defaults, are registered tables with their
fields' forms (ADR-0016 note 34). `set`, `reset` and `remove` then act on a Rates workspace's lines and defaults.

**Pull request:** 3c

**Blocked by:** none.

**Status:** done

- [x] `set <line> notional 5mm` edits a line, and `remove <line>` removes one the workspace made.
- [x] `set defaults` is the kernel's `set` on the defaults, each value by its field: a value alone is written.
- [x] A rate is typed and shown in one form across the pricer and Rates.

## Comments

- *As built (ADR-0016 note 34):* `Percent` did not join `Rate`. The two are typed and written alike (`1.25%`, `5bp`),
  which is what the third criterion asks; they differ in what is held, and Rates' contract and provider hold percent.
- *As built:* the defaults are fields of `rates.workspace` (`default currency`, `default notional`, `default
  direction`), written as `WorkspaceChange.Context`; a running live subscription follows a kernel verb's write through
  the workspace's observer (note 35). Held by `RatesLineCommandTests` and `WorkspaceUndoTests`.
