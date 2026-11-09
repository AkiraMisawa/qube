# 06: the Profile tab shows the exposure and capital profiles as a table and a chart

**What to build:** the contract `portfolio.profiles`, replacing `xva.capital-profile`; the fake XVA provider writing EE
and ENE (when CVA or FVA is priced) on a grid of dates to the side's last maturity, and the capital profile's EAD and
capital (with the KVA option); CVA, FVA and KVA register the Profile tab; the Profile view: a profile selector, a table
(dates × series Before, After and Diff) and an ExChart line chart of the series, Before and After, toggled.

**Blocked by:** 01.

**Status:** done

- [x] EE at the value date is the sum of the side's positive PVs, held, a collateralised trade's scaled by its margin
      period (ticket 04); EE and ENE run off to 0 at the last maturity.
- [x] KVA is still the cost of the capital profile, now read from `portfolio.profiles`.
- [x] The Profile tab follows the case and scenario selectors and says which profiles the Run returned.
- [x] In a browser, the Profile tab draws the table and the chart without a console error, on a laptop and a phone.
