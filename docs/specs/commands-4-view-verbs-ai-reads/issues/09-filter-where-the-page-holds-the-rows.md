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
- *Corrected in review (note 41):* the view of such a grid no longer names its rows beside its conditions. At a new
  revision the grid still held the rows of the answer before, so a line chained on an edit that brought a row into
  the conditions missed it until `/shown` answered. The view says the conditions alone and the server reads which
  rows they leave; only a grid that also filters by its own columns names rows, and its page then gives it every row
  until the server has answered for what it shows.
- *Corrected in review:* while it waits the grid keeps what the server last said: the rows of the same conditions,
  the rows it had under the conditions before, and no row with why where the conditions could not be read, at a
  later revision too. As built it showed every row for a moment in the last two cases.
- *Held by, since the review:* Vitest for the rows such a grid draws in each of those cases (`heldFilter.test.ts`),
  and a step of the CSA browser test that holds the page's question back: the grid still draws one CSA, and the line
  reads the two the conditions now leave.
