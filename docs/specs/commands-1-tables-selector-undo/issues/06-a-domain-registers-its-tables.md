# 06: A domain registers its tables through a catalog port

**What to build:** `TableDescriptor`, `TableField`, `TableSubset`, `IWorkspaceTable` and `ITableCatalog` in the kernel
(ADR-0016 §1 and note 6): a table's id and version, its words, key, fields with their forms, subsets, and its rows at
a revision, with what a write to a row's field is. `Tables` finds the tables of a workspace's application by id or by
words. A test registers station readings, a table with no financial word.

**Pull request:** 1b

**Blocked by:** 05.

**Status:** ready-for-agent

- [ ] A registered table is found by its id and by each of its words, without case; an unknown one is refused with the
      tables there are.
- [ ] A field is found by its words, which may be several; one that names two fields is refused with both.
- [ ] The same id registered twice fails at start.
