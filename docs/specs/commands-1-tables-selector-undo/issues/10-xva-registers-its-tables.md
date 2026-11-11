# 10: XVA registers its tables, and `xva.set` retires

**What to build:** The pricer registers `xva.trades` (subsets `new` and `existing`, whole-set targets for B and A),
`xva.csas`, `xva.netting`, `xva.credit`, `xva.cds`, `xva.workspace` and a table of one row per module with settings
(ADR-0016 Consequences). `PortfolioCells` names a cell from these registrations. `xva.set` and
`PortfolioWorkspaces.SetCells` go and the AI's tool is `entity.set`. A batch over 10,000 rows is measured with its
`undo` and written into the status.

**Pull request:** 1b

**Blocked by:** 08, 09.

**Status:** done

- [x] `set where ccy=USD after off` switches the USD trades out of After, counts them, and `undo` puts them back.
- [x] `set existing before on` is one edit; `set all after off` one per grid.
- [x] `set CSA-A threshold 5mm`, `set E00003, E00004 csa CSA-B`, a module's setting and `set currency USD` write what
      the page's cells write.
- [x] A CSA that does not exist, a negative threshold and a field that is not editable are refused as the grid refuses
      them.
- [x] `vary` and the Changes tab name and read cells as before: their tests pass unchanged.
- [x] The agent's tools hold `entity_set` and no `xva_set`.

## Comments

- 2026-11-11: done (`PortfolioTables`, `PortfolioFields`, `PortfolioChecks`; `PricerTableTests`). The registration is
  what `PortfolioCells` names a cell from, so `vary`, the Changes tab and `set` read one list of fields.
- The tables: `xva.trades` (main; `existing`, `new`), `xva.csas`, and of one row each `xva.netting`, `xva.credit`,
  `xva.cds`, `xva.workspace` and `xva.settings.<module>` for a module that has settings, its word the module's id. A
  result column of a module (PV01) is no field: it changes with no edit.
- `xva.cds` has no row while no curve is chosen. A table of one row that has none refuses `NoRow` in the table's own
  words (`IWorkspaceTable.WhyEmpty`), here the reason the credit gives for having no curve.
- `remove` refuses every row of the pricer by name and says what to do instead; a trade of the source is taken out of
  After with `set <id> A off`.
- One check for a cell and a command: `PortfolioChecks`, which the page's edit, `vary` and the tables call.
- `xva.set`, `PortfolioWorkspaces.SetCells` and the page's `POST /{id}/set` are gone, in this pull request and not in
  1c as the spec had it: the page's `set` runs `entity.set` through the page's door, and shows the server's sentence.
  The line still reads `set` with the client's reader until 1c, so `where` is the AI's and the gateway's until then.
- A value in `name=value` holds at least one word: `type=proxy sector=financials` read `proxy sector` as the next field
  and left `type` no value. Fixed in `Assignments.Read`, with a test on the kernel's own fixture.
- A command of one value is named in the log as the line spells it, `set CSA-A threshold 5mm`, so `undo` says the value.
- Measured (Release, SQLite, 10,009 existing trades, one request at a time): a batch that writes 5,000 to 6,000 edits by
  row 0.1 to 0.25 s, its `undo` and `redo` 0.1 to 0.2 s, a whole grid as one edit 0.02 s. With 15,000 edits standing, a
  `set` of one row first took 0.27 to 0.36 s and the view 0.42 s, because each reader of a state worked the trades, the
  CSAs and the credit out again and walked every edit to do it. The pricer now reads a state once, finds an entity's
  edits by an index, and works a touched row out again only when what it reads changed: 0.05 to 0.13 s and 0.17 s.

