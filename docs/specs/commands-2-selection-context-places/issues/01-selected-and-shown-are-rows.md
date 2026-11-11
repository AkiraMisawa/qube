# 01: `selected` and `shown` are rows, resolved with a view

**What to build:** The view as a value (`LineView`: table, subset, sorts, order token, keys or ranges, focus).
`SelectorReader` reads `selected` and `shown` where rows are said, alone or with `where`. `Selectors.Resolve` takes
the view: keys as ids, ranges through `IWorkspaceTable.InOrder`, and refuses `NoView`, `NothingSelected` and
`ViewMoved` (ADR-0016 §2, notes 16 and 17).

**Pull request:** 2a

**Blocked by:** nothing.

**Status:** ready-for-agent

- [ ] `selected` by keys and by ranges resolves to the rows, in the view's order.
- [ ] `selected where unit=m3` narrows them; `shown` is the view's subset.
- [ ] No view, nothing selected, and an order token that moved are each refused by name.
- [ ] A command invoked with `selected` (no view) is refused and writes nothing.
