# 02 The module, the Position Keeper workspace and the book's tables

Status: done

**Blocked by:** 01.

**What to build:** `Qube.Samples.PositionKeeper` and its registration, `pk.workspace.create` on a result set, and the
read-only tables `pk.values` and `pk.risk` (ADR-0019 §2, §5; ADR-0016).

- [ ] A workspace made on a result set holds its reference and content hash as the acquired source version.
- [ ] `tables.read` returns the book's values and sensitivities of the base scenario, with the engine's signs.
- [ ] An edit of a book row is refused: the table is read-only.
- [ ] With no result set configured, the command is refused by name and no workspace is made.
- [ ] The kernel gains no type that names a position, a hedge or a sensitivity.
