# 03: `workspace.undo` and `workspace.redo` are commands, and the page has a door to the gateway

**What to build:** Two kernel capabilities, `workspace.undo` and `workspace.redo` 1.0.0, with their contracts, invoked
through `CommandService` by the owner of the workspace. `POST /api/commands/{id}/{version}` invokes a command for the
signed-in user with the origin of the page and answers as the agent's door does (ADR-0016 §5 and note 4). The pricer's
rules title an edit's target as the screen does.

**Pull request:** 1a

**Blocked by:** 02.

**Status:** done

- [x] `workspace.undo` through `CommandService` answers the new revision, its own command id and what it took back.
- [x] It refuses a workspace of another user (`NotOwner`) and one that moved (`RevisionMoved`).
- [x] `POST /api/commands/workspace.undo/1.0.0` answers 200, 404 for an unknown command, 409 for a moved revision, 422
      for nothing to undo.
- [x] The pricer names a taken-back edit by row and column (`CSA-A threshold`), a grid's side and the measures in
      words.

## Comments

Done 2026-11-11. `WorkspaceCommandsModule` registers the two capabilities with the contracts
`workspace.take-back-request` and `workspace.taken-back`; one loop in `Api.cs` maps the agent's door and the page's.
`PortfolioEditRules.TitleOf` names a target as the Changes tab does (`CommandGatewayTests`, over HTTP, the
refusals of `CommandService` among them; no test calls `CommandService` apart from the door, which adds only the
statuses).

After the code review, 2026-11-11. A request with neither a revision nor a command is refused, and the AI's names the
command (ADR-0016 §11 and note 4); it had taken back whatever was latest. `origin` is written as the wire writes it
(`pricer`). The tests read the answers alone, no longer the log's positions, and assert `the measures`.
