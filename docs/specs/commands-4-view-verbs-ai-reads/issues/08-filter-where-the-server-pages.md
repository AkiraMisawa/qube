# 08: `filter` where the server pages the grid

**What to build:** `filter [<table>] where <conditions>` and `filter off` (ADR-0016 note 41) on a grid the server pages:
the conditions are the selector's, a view carries them, and the page reader narrows by them.

**Pull request:** 4b

**Blocked by:** 06.

**Status:** done

- [x] The reading says how many rows the conditions leave, of how many, and refuses conditions as `where` refuses them.
- [x] Under a view's conditions `shown` is the rows they leave, a position is among those rows, and the order's token
  moves when a row enters or leaves.
- [x] The pricer's page endpoint takes `where` and answers the rows left, their count and that token; conditions it
  cannot read are refused, never ignored.
- [x] In a browser the grid shows the rows left and says the filter beside its title; `set shown …` writes those rows
  alone; a row edited out of the conditions leaves; `filter off` and the button show every row again.
- [x] A page of a filtered grid of 10,000 rows is measured.

## Comments

- *Added as built (note 41):* the conditions are the Opaque Filter of the grid's source, beside the filters of its
  own columns, which the trade grids still refuse by name. Conditions that can no longer be read are `FilterUnread`
  (422 from the page endpoint). The answer says `of`, the rows with no conditions.
- *Not as the box says:* a trade grid whose read fails under its conditions keeps the rows it last read, as it does
  when any read fails, with why in the page's alert; it does not show no row. Never every row.
- *Measured at 10,009 rows* (Release build, SQLite, sorted by notional): the first page under `ccy=USD` (4,011 rows
  left) 84 ms, then 15 to 53 ms, against 7 to 8 ms with no conditions; a reading of `set shown a off` under them 46
  to 129 ms (71 KB); `filter where ccy=USD` itself 9 to 62 ms. Recorded, not gated.
- *Corrected in review (note 41):* the order's token under conditions was the reader's with a digest of the keys left,
  so it moved with a row the conditions leave out and a selection in the filtered grid was refused as moved. It is
  the digest alone. The offer of `shown` swallowed every refusal of its count; it now leaves out the word only where
  the filter is unread. The measurement was run by a script of this session against a host of the Release build;
  the script is not in the repository.
