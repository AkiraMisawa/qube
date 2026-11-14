# 16: A grid's row has an edit button

**What to build:** The trade grids and the CSA grid have an Action Column with one button, edit, which runs `open
<row>`.

**Pull request:** 5c

**Blocked by:** 15.

**Status:** done

- [x] The button opens the row's dialog, by mouse, touch and keyboard (Space on the button's cell).
- [ ] The column costs the grids no column a user needs at 390px. *Not met as written (2026-11-12; ADR-0016 note
      57):* the column stands beside the row's key, 64px wide, so that the button is in the grid's first view, before
      any scrolling to the side (not in view wherever the grid is scrolled: it scrolls with the columns). At 390px the
      trade grids' first view is Trade, Edit and Product, and `Ccy`, which was in it, is reached by scrolling. At
      1280px the CSA grid's counterparty threshold, which was in its first view, is reached by scrolling too. Put at
      the end of the grid the column would cost nothing and be out of view at every width. For the owner to judge.

## Comments

2026-11-14, closed by default after the owner's answers (ADR-0016 note 57's mark of that day). Asked of the column, he
kept the button's name: "the button can be Edit", after saying it felt more like a Detailer. He asked whether the
left is the better place and named no place himself, so the column stays beside the row's key: a DEFAULT, his to
change. The second criterion stays unchecked: not met as written, and accepted by default, not by his word. What it
leaves "for the owner to judge" was put to him, and the place is still his to move.
