# 04: Vary values… from a cell

**What to build:** *Vary values…* on ExGrid's Context Menu of every editable cell of the input tabs' grids and the
trade grids (ADR-0010 §3) opens a dialog that takes a comma-separated list and makes the same cases `vary` makes,
naming the cell for it. The dialog says what was made and skipped, and a value the cell would refuse is refused there.

**Blocked by:** 02.

**Status:** ready-for-agent

- [ ] The Context Menu of an editable cell offers Vary values…; a read-only cell's does not.
- [ ] The dialog's list makes the cases `vary` would make for that cell, said back as `vary` says them.
- [ ] In a browser, Vary values… on a CSA threshold makes three cases, driven by the keyboard.
