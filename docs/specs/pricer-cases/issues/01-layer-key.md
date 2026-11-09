# 01: The kernel's edit gains a layer key

**What to build:** an edit target names an optional layer beside its scope, entity and field (ADR-0010
Consequences). The same field holds a Common value and a layer's value at once; each is carried, conflicted or lost on
its own when the sources move; the store keeps the layer, and a workspace saved before has none. Removing a created
entity can remove every edit in a layer it names, in one log entry that counts them. The kernel gives a layer no
meaning.

**Blocked by:** None (can start immediately).

**Status:** done

- [x] A Set in a layer and a Set without one on the same field are two overrides, each read back as set.
- [x] A re-acquisition that changes the field's source conflicts both, each on its own key.
- [x] A workspace with layered edits is restored from the SQLite store with its layers.
- [x] Removing a created entity with its layer removes the entity and every edit in the layer, in one entry.

*Done 2026-11-08.* The layer is the edit target's fourth part, `Layer`, null for Common; the EditLog gains a `layer`
column, added to an older log on open. `Remove` takes the layer the entity owns.
