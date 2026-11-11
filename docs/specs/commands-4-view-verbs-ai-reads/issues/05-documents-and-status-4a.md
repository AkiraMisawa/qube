# 05: The agent's words, the pane, documents and status for 4a

**What to build:** The agent's instructions say that tables are described and read by tools and that a preview says
what a command would change, with no domain's word. The pane's block of a tool's answer scrolls within a bounded
height. `docs/10-api-contracts.md`, `docs/09-ai-and-views.md` where it says what the agent can read, ADR-0016's note
38 as built, and `IMPLEMENTATION_STATUS.md`.

**Pull request:** 4a

**Blocked by:** 02, 03, 04.

**Status:** done

- [x] The instructions name the three tools' purposes and no table.
- [x] A long answer in the pane scrolls in its block.
- [x] The documents say what is built and what is left.

## Comments

- *Added as built (ADR-0016 note 38):* the stand-in agent takes `describe the tables`, `read <rows> [fields …]` and
  `preview <line>` for the workspace the pane's context names, so that the three are seen working with no model
  configured; a browser test reads a workspace of more than fifty trades through it and holds the block's scroll.
  Its refusal no longer ends with two full stops.
- *Not done here:* a real model with the three tools. None is configured, and the status file says so.
