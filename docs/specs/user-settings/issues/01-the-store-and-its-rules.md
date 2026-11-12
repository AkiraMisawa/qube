# 01: The store and its rules

**What to build:** setting registrations (`ISettingCatalog`, `SettingDefinition`), the store port with its in-memory
and SQLite implementations, and the `UserSettings` service: read, set, reset, for the caller alone (ADR-0017 §2).

**Step:** 1

**Blocked by:** none.

**Status:** done

- [x] A registered setting reads as its registered value until set, and as the value set after.
- [x] An unregistered key, and a value its form refuses, are refused by name; nothing is written.
- [x] A stored value its form no longer takes reads as unreadable, never as another value.
- [x] One user's setting is not another's.
- [x] A setting kept in SQLite is read again after a restart.
