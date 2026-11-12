# 03: The verbs of ADR-0018 §3, through the port

Status: ready-for-agent

**What to build:** `Arrangement` and its two implementations; `split panel`, `send panel to`, `float panel`,
`dock panel`, `size panel`, `maximize panel`, `unmaximize panel` and `gather panels` in `panelVerbs.ts`; note 7's verbs
scoped to the panel's area; `next panel` and `previous panel` in reading order; the tab's menu and the area's list
holding every verb, with the words beside each.

**Blocked by:** 02.

- [ ] Each verb and each refusal of the spec's table, tested over a fake port.
- [ ] The menus reach every verb without a drag.
