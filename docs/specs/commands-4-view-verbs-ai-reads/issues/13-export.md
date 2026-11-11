# 13: `export`

**What to build:** `export [<table>] [<rows>] [<fields>]` saves the rows as a CSV file with a header line (ADR-0016
note 44).

**Pull request:** 4c

**Blocked by:** 12.

**Status:** done

- [x] The reading names the file. A table named with no rows is every row of it; with no word, the rows the grid
      behind the line shows.
- [x] The content is RFC 4180 with a header line and a byte order mark.
- [x] The door saves the file under the name the reading gives.
- [x] In a browser, the file saved holds the table.

## Comments

- The file is named by the table's word (`trades.csv`, `lines.csv`), whatever subset or rows it holds. The word is
  written as it is: as first built a space in it became a hyphen, which nothing asked for (corrected in review).
- The door hands the file to the browser and cannot hear whether it was saved (note 44, "Corrected in review").
- `export` accepts `with headers` and writes the header line it always writes.
- The browser tests read the file saved: the byte order mark, the header line, CRLF after every line, and the rows
  in the grid's order where the server pages the grid.
