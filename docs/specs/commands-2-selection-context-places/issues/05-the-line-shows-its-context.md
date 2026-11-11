# 05: The line reads the page's view, shows it, and sends it

**What to build:** `CommandDoor.view()`. The pricer's page answers the view of the trade grid the keyboard was last
in: its sort and order token as its reader last answered, its selection and Focus from `readView()`. The line reads it
once as it opens, asks the preview with no text and shows the sentence under the input, sends the view with each
preview and with Enter's own reading, and marks an argument that came from context (notes 16 and 19).

**Pull request:** 2a

**Blocked by:** 02, 03, 04.

**Status:** ready-for-agent

- [ ] Three trades selected with the keyboard, then `set csa CSA-B`: the line shows the three rows and what would
      change, and Enter writes them.
- [ ] `set selected where ccy=USD a off` changes the USD trades among the selected only.
- [ ] Every row of the Existing grid selected with one key: the count is the grid's, and Enter writes them all.
- [ ] `reset` on a focused cell that has an edit takes the edit back.
- [ ] The sentence under the line names the grid, the count and the cell; with no grid focused there is none.
- [ ] Looked at in a browser at 1440, 992, 768 and 390 px in both schemes.
- [ ] Measured: the preview of `set selected a off` with 10,000 rows selected.
