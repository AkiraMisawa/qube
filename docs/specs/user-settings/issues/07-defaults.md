# 07: Defaults copied in when a workspace is made

**What to build:** `pricer.displayCurrency` and `rates.newLine.currency`, `.notional`, `.direction`. The making copies
them in and records the source; the pages show it; an unreadable default refuses the making (ADR-0017 §3, §6.1).

**Step:** 2

**Blocked by:** 02.

**Status:** done

- [x] A pricer workspace made after the display currency is set starts in it, and says it came from the setting.
- [x] A workspace made before keeps its currency; one edited in the workspace says so, and `undo` takes it back.
- [x] A Rates workspace starts with the user's defaults, each saying where it came from.
- [x] A default no longer offered refuses the making, naming the setting.
