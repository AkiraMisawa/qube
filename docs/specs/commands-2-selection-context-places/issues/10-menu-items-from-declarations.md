# 10: A table declares its menu items

**What to build:** A table's description lists its actions, each a title and the line it runs. A grid bound to the
table shows them in its Context Menu, runs each through the door with the menu's selection and cell as the view, and
shows the line that does the same. The kernel gives every table `Reset this cell`; the pricer declares `Take out of
After`, `Put into After`, and the same for Before (ADR-0016 §4).

**Pull request:** 2c

**Blocked by:** 05.

**Status:** done

- [x] The Existing grid's Context Menu shows `Take out of After` with `set selected after off`, and it writes the
      selected rows.
- [x] `Reset this cell` is offered on a cell that has an edit, and takes it back.
- [x] The page's hand-written item `Vary values…` still stands beside them.
- [ ] `Ask again` beside them, on a failed result: built, and held by no browser test (the sample's results do not
      fail on request). Its place in the menu is the same list `Vary values…` is in.
- [x] A refusal is a notice with the server's reason.

## Comments

- As built (ADR-0016 note 25): `TableDescriptor.Actions` (`TableAction`: a title and a line), `CommandLines.Tables`
  and `GET /api/workspaces/{id}/tables`, the kernel's `Reset this cell` first. An action whose line begins with no
  command's words fails the read.
- The client builds a grid's items with `tableMenu` (`platform/commands/served.ts`) and the view an item runs with
  with `menuView` (`platform/grid/lineView.ts`): the menu's Selection, and the cell the menu was opened on by its
  row's key and its column.
- The items stand after the grid's own commands and before the page's hand-written ones (`Ask again` on a failed
  result, `Vary values…` on a cell that can be typed in), which still end the menu.
- Every action is offered on every cell. One that changes nothing says so (`Nothing changed: … already holds B
  off.`), and one the server cannot read there is a refused notice that begins with the line.
- `Reset this cell` is offered on every cell, edited or not, for the same reason: on one with no edit it says
  `Nothing changed: DRAFT-1 has no edit of csa.`
- The CSA grid's menu is held by the browser test too: `Reset this cell` alone before `Vary values…`, and it takes
  back an edit of a CSA's threshold.
- A host that registers tables and no `entity.reset` fails when the tables are read, and a page whose grid is bound
  to a table the server does not list fails: neither is drawn as a menu with an item missing.
- Not built: the `command` target of a declarative view's row action (ADR-0016 §4); no view asks for one.
