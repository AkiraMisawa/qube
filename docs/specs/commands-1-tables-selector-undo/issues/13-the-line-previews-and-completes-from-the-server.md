# 13: The line shows the server's reading, completes from it, and runs it

**What to build:** The line builds `set`, `reset`, `remove`, `undo` and `redo` from the catalog. While one is typed it
asks for the preview, the latest answer winning, shows what would change or the reason under the line, and offers what
could follow. Enter runs the server's reading of exactly the text on the line, or nothing while it cannot be read, and
the result is the server's sentence. The client's `setTyped` and `setHint`, the page's `set` and `POST
/api/xva/workspaces/{id}/set` go (ADR-0016 §5, Consequences).

**Pull request:** 1c

**Blocked by:** 12.

**Status:** done

- [x] `set where ccy=USD after off` shows its rows and what would change before Enter, and the result's sentence
      after.
- [x] A line the server cannot read keeps the line open at Enter with its text and the reason.
- [x] `set` offers rows, then fields, then a field's values, and Tab takes one.
- [x] `undo` shows what it would take back before Enter.
- [x] Looked at in a browser at 1440, 992, 768 and 390 px in both schemes.

## Comments

- 2026-11-11: done (`client/src/platform/commands/served.ts`, `CommandPalette.tsx`, the pricer page's door;
  `served.test.ts`, `CommandPalette.test.tsx`, six tests of `xva.spec.ts`). `setTyped`, `setHint`, the page's `set`
  and the line's own `undo` and `redo` entries are gone; the endpoint went in 1b.
- Enter, as first built, had the server read the line again and ran that second reading. The review found that this
  could run what the user never saw, when someone else moved the workspace between the two. Changed before the merge:
  Enter invokes the inputs of the reading on screen, which name the revision it was read at, and a workspace that
  moved since refuses it; the page then reads the workspace again, and the line stays and is read again. The preview
  is asked for after the page's own edits have been answered, so the page's own typing never causes that refusal.
  Only where no reading for the text has arrived does the server read the line at Enter, at the page's revision.
- The door is the kernel's client code (`commandDoor`): the pricer's page gives it its workspace, its edit queue and
  how it reads its view again. The grids' undo and redo keys go through it too, by the commands' ids.
- The line answers once the command is taken or refused, so a refusal that only the write makes (a held edit not yet
  decided) keeps the line as a reading's refusal does. A line closed meanwhile gets its reason as a notice.
- The line stays open, and is not typed into, while Enter is answered; keys typed meanwhile go to nothing, as keys do
  while a surface is on its way. The test fixture's `command` therefore waits for the line before to be gone.
- A reading belongs to one opening of the line on one workspace; a request the server did not answer with a reading
  is said in red as a failure. A word at fault that stands twice in the line is named and not marked.
- A word at fault is selected, so that typing replaces it, unless it ends the line, where the fault may be what is
  missing after it (`set DRAFT-1 a`): there the caret stands after the line.
- While the server reads the text now on the line, the reading of the earlier text stays in place, so the list does
  not jump at each key; after 0.2 s it is dimmed and said to be read (`shell.css`). Nothing is taken from it.
- `reset` and `remove` came to the line with no code of their own. The pricer has no row a command made yet, so
  `remove` refuses each by name until step 3.
- The page's examples no longer carry a trade: only `vary`'s hint reads them, and it names a CSA.
- Looked at with a script in Chromium at the four widths, light and dark: the empty `set`, a `where` with its rows
  and what would change, a refused line with its word selected, and `undo`. Nothing scrolls sideways at 390 px.
