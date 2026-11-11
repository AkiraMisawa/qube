# 09: `filter` where the page holds the rows

**What to build:** On a grid whose page holds every row, the page shows the rows the server says the conditions leave,
asked for again at each revision (ADR-0016 note 41): the client reads no condition.

**Pull request:** 4b

**Blocked by:** 08.

**Status:** done

- [x] `POST /api/workspaces/{id}/shown` answers the keys a view shows, its conditions read by the selector's reader.
- [x] The pricer's CSA grid and the Rates lines show the rows left, say the filter and take it off by the button.
- [x] The view such a grid gives the line carries its conditions, so `shown` is read under them.
- [x] A filter that cannot be read shows no row and says why.

## Comments

- *Changed as built (note 41):* until the server has answered for new conditions the grid keeps the rows it has,
  and does not show none: a line chained on `filter` is then read under the conditions with the rows named, the
  right rows whichever arrives first. The view says both `shown` and `where`.
- *Held by:* the host's test of `/shown` (the keys, 422, 400, 403, 404), Vitest for the views, and two browser tests
  (the CSA grid with a chained `set shown`, a CSA edited out of the conditions, a filter refused as unreadable
  showing no row; the Rates line grid).
