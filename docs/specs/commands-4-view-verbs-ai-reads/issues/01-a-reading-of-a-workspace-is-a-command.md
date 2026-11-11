# 01: A reading of a workspace is a command

**What to build:** An effect, `ReadWorkspace`, for a capability that reads a caller's own workspace and writes
nothing. It makes the capability a command (ADR-0016 note 38): the gateway invokes it for the caller, the agent is
offered it as a tool, and a workflow step that names it is refused. The wire schema lists the effect.

**Pull request:** 4a

**Blocked by:** nothing.

**Status:** done

- [x] A capability whose one effect is `ReadWorkspace` is invoked through the command gateway.
- [x] The agent's tools hold it.
- [x] A workflow that names it as a step is refused (`CommandAsStep`).
- [x] `contracts/composition.schema.json` lists `readWorkspace`.

## Comments

- *As built (ADR-0016 note 38):* `IsCommand()` takes the effect, so the gateway, the agent's tools and the refusal as
  a workflow step follow from the one place that decides them. The catalog says the effect as `readWorkspace` and
  gives the three no `command`.
- *Corrected as built:* a contract's `minimum` or `maximum` written as a whole number threw where it was checked;
  the bound is read as the number is. No contract before `tables.read-request` had one.
