# 04: a module's settings are inputs: the Settings tab, set and vary, frozen and read by the provider

**What to build:** settings registered by a module (id, title, form, default, the measures it serves); the register's
reading for the workspace view; edits on `settings:<module>` in the workspace's scope; type and bounds checked on the
server; XVA's MPOR override, cost of capital, charge ratio, PV adjustment and risks included, carried in its input and
read by the fake provider; the cells `<module> <setting>` for `set`, `vary` and the Changes tab; the Settings tab, one
section per module with a selected measure; `open settings`.

**Blocked by:** none.

**Status:** open

- [ ] The view lists each selected module's settings with value, default and Original; a setting shows while a measure
      it serves is selected.
- [ ] A value out of its form or bounds is refused by name; a setting survives a counterparty change.
- [ ] A higher cost of capital makes KVA more negative; the charge ratio scales CVA Charge; a longer MPOR raises the
      collateralised exposure; PV adjustment off prices new trades with no PV Adj and no longer refuses a pending one.
- [ ] `set xva cost of capital 12%` and `vary xva charge ratio 1, 1.5` work; the Changes tab names them.
- [ ] In a browser, a setting typed in the Settings tab is marked and moves the figure after Price.
