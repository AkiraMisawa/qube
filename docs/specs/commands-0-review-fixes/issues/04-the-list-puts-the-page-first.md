# 04: The empty line and `?` list the page's commands first, and say what they do not show

**What to build:** `listed(query, commands, usage)` (ADR-0006's note 3 of 2026-11-11): for the empty line and `?`,
sections with headings: the page's commands under the active panel's title, then each group of the other commands
that can run, the applications' groups before the shell's, and on the empty line `Not available now` last. A command
says `page: true` when it acts on the page in the active panel. Under a heading the order is use, then registration.
A typed query stays one ranked list. The line draws at most 200 commands or 50 values, then `N more: type to
narrow.` The placeholder names the first three commands of the empty line's list.

**Blocked by:** nothing.

**Status:** ready-for-agent

- [ ] On an XVA workspace, the first heading is the panel's title and its commands are the page's.
- [ ] `?` there lists `new rates` and `open pricing db`.
- [ ] A command that cannot run now is under `Not available now` on the empty line and absent from `?`.
- [ ] A list past its cap says how many more there are.
- [ ] The placeholder names commands of the page in view, and `new xva`, `new rates`, `open pricing db` with none.
