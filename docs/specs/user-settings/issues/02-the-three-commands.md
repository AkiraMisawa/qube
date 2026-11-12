# 02: `settings.read`, `settings.set`, `settings.reset`

**What to build:** the three commands through the gateway, with the `UserSetting` effect, their contracts, and
`GET /api/settings` (ADR-0017 §2, §4).

**Step:** 1

**Blocked by:** 01.

**Status:** done

- [x] Each answers for the caller, and a set or a reset says what it replaced.
- [x] A refusal is by name, with the setting's words.
- [x] They are commands: refused as a workflow step, offered to the agent.
- [x] `GET /api/settings` answers as `settings.read`.
