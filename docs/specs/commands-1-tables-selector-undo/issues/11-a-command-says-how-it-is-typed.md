# 11: A command capability says how it is typed

**What to build:** `CapabilityDescriptor` gains its command form, words and arguments (ADR-0016 §4), for `entity.set`,
`entity.reset`, `entity.remove`, `workspace.undo` and `workspace.redo`. The catalog serves it and the client's
contracts read it.

**Pull request:** 1c

**Blocked by:** 10.

**Status:** done

- [x] `GET /api/catalog` answers the five commands with their words and arguments.
- [x] A capability with no form answers none, and nothing else of the catalog changes.

## Comments

- 2026-11-11: done (`CommandForm`, `CommandArgument`, `CommandArgumentKind` in the contracts; `Capability.command` in
  `contracts/composition.schema.json`; `CommandLineTests`, `CommandGatewayTests`). A form holds one spelling or several
  and each argument's name, kind and whether it is required, in the order typed.
- A capability that says how it is typed must read its line (`ILineCommand`), and two forms may not share a word:
  either stops the host at start (`CommandLines`, resolved in `Program.cs` as the registry is).
