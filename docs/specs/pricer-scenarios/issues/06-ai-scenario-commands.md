# 06: the AI reaches every scenario command

**What to build:** the scenario commands as command capabilities (ADR-0005, ADR-0010 §4): define, include, exclude,
remove, ladder, cross, save and load, each taking what the command line takes, as written, through the same workspace
service, logged with the caller's origin, and the open page picking up what the AI made.

**Blocked by:** 01, 04, 05.

**Status:** ready-for-agent

- [ ] Each capability is listed as a tool with its contract, and its refusals are the command line's, by code.
- [ ] A scenario the AI defines is in the log with origin `ai`, and a revision the AI moved is refused when stale.
- [ ] A cross asked of the AI is one generator call.
