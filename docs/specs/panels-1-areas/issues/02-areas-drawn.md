# 02: Areas drawn by dockview, and the strip where there is no room

Status: done

**What to build:** dockview-react, dockview and dockview-core at exactly 8.9.0; `PanelAreas` drawing the store's
panels in areas with the shell's tabs, menus and list; `Panels` choosing it or note 7's strip by the panels' space and
`needs`; the store's order set from the reading order; opening beside the active panel in its area; closing showing
what the area showed before; the active panel's frame; the theme from the shell's tokens.

**Blocked by:** 01.

- [x] One area at first holds every panel, under a strip that behaves as note 7's (keys, menu, list, pin, close).
- [x] A tab dragged to an edge splits, onto a strip joins, with Shift floats.
- [x] Below 768px, and where the arrangement does not fit, note 7's strip is drawn, and the arrangement comes back.
- [x] Only each area's shown page is mounted; a move keeps it mounted.
