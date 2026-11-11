# 13: The line shows the server's reading, completes from it, and runs it

**What to build:** The line builds `set`, `reset`, `remove`, `undo` and `redo` from the catalog. While one is typed it
asks for the preview, the latest answer winning, shows what would change or the reason under the line, and offers what
could follow. Enter runs the server's reading of exactly the text on the line, or nothing while it cannot be read, and
the result is the server's sentence. The client's `setTyped` and `setHint`, the page's `set` and `POST
/api/xva/workspaces/{id}/set` go (ADR-0016 §5, Consequences).

**Pull request:** 1c

**Blocked by:** 12.

**Status:** ready-for-agent

- [ ] `set where ccy=USD after off` shows its rows and what would change before Enter, and the result's sentence
      after.
- [ ] A line the server cannot read keeps the line open at Enter with its text and the reason.
- [ ] `set` offers rows, then fields, then a field's values, and Tab takes one.
- [ ] `undo` shows what it would take back before Enter.
- [ ] Looked at in a browser at 1440, 992, 768 and 390 px in both schemes.
