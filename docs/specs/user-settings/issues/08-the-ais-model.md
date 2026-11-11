# 08: The AI's default model, and a conversation's own

**What to build:** `Agent:Copilot:Models`, `agent.defaultModel`, a conversation's model chosen in the pane, and the
model each agent run records (ADR-0017 §1, §6.3).

**Step:** 3

**Blocked by:** 02.

**Status:** ready-for-agent

- [ ] A new conversation starts on the user's default model.
- [ ] A model chosen for one conversation changes neither the default nor another conversation; one not allowed is refused.
- [ ] An agent run says the model it ran on.
