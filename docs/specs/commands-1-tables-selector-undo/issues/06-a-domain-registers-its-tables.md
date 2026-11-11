# 06: A domain registers its tables through a catalog port

**What to build:** `TableDescriptor`, `TableField`, `TableSubset`, `IWorkspaceTable` and `ITableCatalog` in the kernel
(ADR-0016 §1 and note 6): a table's id and version, its words, key, fields with their forms, subsets, and its rows at
a revision, with what a write to a row's field is. `Tables` finds the tables of a workspace's application by id or by
words. A test registers station readings, a table with no financial word.

**Pull request:** 1b

**Blocked by:** 05.

**Status:** done

- [x] A registered table is found by its id and by each of its words, without case; an unknown one is refused with the
      tables there are.
- [x] A field is found by its words, which may be several; one that names two fields is refused with both.
- [x] The same id registered twice fails at start.

## Comments

- 2026-11-11: done, in `Tables/Tables.cs`. The catalog port is `ITableCatalog.Tables`, as `IProductCatalog.Products`; a
  table says its application, and `TableRegister.Of(state)` gives a workspace its own, each described for it.
- A write answers workspace changes (`Set`, `SetAll`, `Reset`, `ResetAll`, `Remove`), so the domain decides scopes and
  checks a value as it checks a typed cell; a row gives its values one field at a time, so a reader builds none it is
  not asked for.
- A description says which table is its application's main one (`Main`), which note 7 needs.
- `TableRefused` carries the word at fault and the candidates (ADR-0016 §6), for the preview of step 1c to point at.
- Tested through `TableRegister` itself (`TableRegisterTests`), with the station registration every later ticket uses
  (`StationTables.cs`); the spec's testing decisions name the seam.
