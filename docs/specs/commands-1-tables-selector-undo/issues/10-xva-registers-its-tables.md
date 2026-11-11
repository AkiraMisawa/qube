# 10: XVA registers its tables, and `xva.set` retires

**What to build:** The pricer registers `xva.trades` (subsets `new` and `existing`, whole-set targets for B and A),
`xva.csas`, `xva.netting`, `xva.credit`, `xva.cds`, `xva.workspace` and a table of one row per module with settings
(ADR-0016 Consequences). `PortfolioCells` names a cell from these registrations. `xva.set` and
`PortfolioWorkspaces.SetCells` go and the AI's tool is `entity.set`. A batch over 10,000 rows is measured with its
`undo` and written into the status.

**Pull request:** 1b

**Blocked by:** 08, 09.

**Status:** ready-for-agent

- [ ] `set where ccy=USD after off` switches the USD trades out of After, counts them, and `undo` puts them back.
- [ ] `set existing before on` is one edit; `set all after off` one per grid.
- [ ] `set CSA-A threshold 5mm`, `set E00003, E00004 csa CSA-B`, a module's setting and `set currency USD` write what
      the page's cells write.
- [ ] A CSA that does not exist, a negative threshold and a field that is not editable are refused as the grid refuses
      them.
- [ ] `vary` and the Changes tab name and read cells as before: their tests pass unchanged.
- [ ] The agent's tools hold `entity_set` and no `xva_set`.
