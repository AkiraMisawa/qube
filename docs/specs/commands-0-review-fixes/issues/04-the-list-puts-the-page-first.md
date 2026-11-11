# 04: The empty line and `?` list the page's commands first, and say what they do not show

**What to build:** `listed(query, commands, usage)` (ADR-0006's note 3 of 2026-11-11): for the empty line and `?`,
sections with headings: the page's commands under the active panel's title, then each group of the other commands
that can run, the applications' groups before the shell's, and on the empty line `Not available now` last. A command
says `page: true` when it acts on the page in the active panel. Under a heading the order is use, then registration.
A typed query stays one ranked list. The line draws at most 200 commands or 50 values, then `N more: type to
narrow.` The placeholder names the first three commands of the empty line's list.

**Blocked by:** nothing.

**Status:** done

- [x] On an XVA workspace, the first heading is the panel's title and its commands are the page's.
- [x] `?` there lists `new rates` and `open pricing db`.
- [x] A command that cannot run now is under `Not available now` on the empty line and absent from `?`.
- [x] A list past its cap says how many more there are.
- [x] The placeholder names commands of the page in view, and `new xva`, `new rates`, `open pricing db` with none.

Done 2026-11-11. `listed`, `placeholder` and `drawn` are in `commands.ts`; `appCommands` marks the page's commands
and `buildCommands` registers the applications' before the workflows' and the shell's. Under a heading that is a
command's own panel the option does not repeat the panel's title. No list of the sample modules passes a cap today
(an XVA page lists sixty commands), so the count past a cap is held by `drawn`'s test, and the browser test reads that
`?` shows no such line.

After the code review, 2026-11-11:

- Nothing is highlighted while the line lists, empty or at `?`: with the page's first command highlighted, Ctrl+K and
  Enter priced. Enter alone says to choose or to type; down chooses the first command, up the last.
- The cap counts commands across the sections in one place (`drawnSections`); the line past it is under the list, a
  `status`, and a heading names its group of options for a screen reader.
- A finished Run's views (`show <view>`) are its page's commands, and `Not available now` is ordered by use too.
