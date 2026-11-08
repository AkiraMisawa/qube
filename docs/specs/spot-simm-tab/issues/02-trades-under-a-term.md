# 02 Trades under a term

Status: done

ADR-0011 §1 and §6, and ADR-0012 §6 ("at any term, a trade's contribution is the sum of its lines' contributions").
A leaf set names its contribution field beside its entity field. `GET …/traces/{measure}/entities?path=…` answers one
row per entity below the term, at every depth. Each row has the entity, its contribution and row count on each side
the case priced, and the Diff; the page says its membership on each side. The rows are sorted by the absolute Diff, or
by the value when one side was priced, and they are paged as leaves are. Each side says whether the entities add up to
the term's allocation. Where a side cannot be compared (unavailable, not returned, or not adding up), no entity has a
Diff and the page says why. A term with no allocation (a direction, a max over regimes) and a leaf set with no
contribution are refused by name. SIMM's leaf set names `contribution`. The leaf rows endpoint takes `below` for the
rows at every depth below a term, refused in the same place.

Below a term with an allocation, the trace view offers its entities first, named by the leaf set's entity column (`By
trade`), and beside them every row at any depth below it (`By CRIF line`).

Tests: a synthetic non-financial trace in the kernel; spot IM over HTTP, where the trades add up to the term's
contribution on each side.
