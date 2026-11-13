import type { DockviewApi, DockviewGroupPanel, IDockviewPanel } from 'dockview-react';
import type { Area, Arrangement, Box, Side } from './arrangement';
import { keptArrangement } from './keptPanels';
import { AREA_MIN, areasOf, lineOf, needs, type Line } from './layout';

// The arrangement's port over dockview's own API (ADR-0018 §2): what `PanelAreas` registers while the panels stand in
// areas. It reads what dockview serialises, so it reads what the browser tab keeps, and changes the arrangement only
// through dockview's public calls; what each change may do has been checked by the verb before it gets here.

const SIDE = { right: 'right', left: 'left', down: 'bottom', up: 'top' } as const;

/** The page another window opens on: the client's own, same-origin and blank (ADR-0018 §9). */
export const POPOUT_URL = '/popout.html';

/** The port over this dockview, and how to stop what it watches. */
export function dockArrangement(api: DockviewApi): { readonly port: Arrangement; readonly stop: () => void } {
  // The area a panel was floated from, or left for another window (§9), so that `dock panel` puts it back there (the
  // spec's DEFAULT reading of §3), kept by the browser tab with the arrangement. It is read from dockview's own move,
  // so that a panel floated by the pointer (Shift with a drag) is put back as one floated by `float panel` is; one moved
  // into an area of the main window by any means has none. A whole area popped out is told as a move of each of its
  // panels from it, so its place, which dockview keeps hidden, is where they go back.
  const kept = keptArrangement.getState;
  const floatedFrom = (panelId: string, areaId: string | undefined): void => {
    const { [panelId]: _, ...others } = kept().floatedFrom;
    kept().keepFloatedFrom(areaId === undefined ? others : { ...others, [panelId]: areaId });
  };
  const away = (g: DockviewGroupPanel): boolean => g.api.location.type === 'floating' || g.api.location.type === 'popout';
  const moves = api.onDidMovePanel(({ panel: moved, from, to }) => {
    if (!away(to)) floatedFrom(moved.id, undefined);
    else if (from.api.location.type === 'grid') floatedFrom(moved.id, from.id);
  });

  const panel = (id: string): IDockviewPanel => {
    const found = api.getPanel(id);
    if (found === undefined) throw new Error(`The areas hold no panel ${id}.`);
    return found;
  };
  const group = (id: string): DockviewGroupPanel => {
    const found = api.groups.find((g) => g.id === id);
    if (found === undefined) throw new Error(`There is no area ${id}.`);
    return found;
  };
  // The areas of the main window. The place dockview keeps hidden for a whole area that left for another window holds
  // no panel and is none of them, though a panel docked back goes into it (below).
  const docked = (): readonly DockviewGroupPanel[] => api.groups.filter((g) => g.api.location.type === 'grid' && g.panels.length > 0);
  const line = (areaId: string): Line | undefined => lineOf(api.toJSON(), areaId);
  const titled = (g: DockviewGroupPanel): string => {
    const shown = g.activePanel ?? g.panels[0];
    if (shown === undefined) throw new Error(`The area ${g.id} holds no panel.`);
    if (shown.title === undefined) throw new Error(`The panel ${shown.id} was drawn without its title.`);
    return shown.title;
  };
  const place = (box: Box) => ({ x: box.left, y: box.top, width: box.width, height: box.height });

  const port: Arrangement = {
    strip: undefined,
    areas: (): readonly Area[] => areasOf(api.toJSON()).map((a) => {
      const g = group(a.id);
      return { ...a, width: g.width, height: g.height };
    }),
    room: () => ({ width: api.width, height: api.height }),
    lineOf: line,
    maximized: () => api.groups.find((g) => g.api.isMaximized())?.id,
    split(panelId: string, side: Side) {
      const p = panel(panelId);
      p.api.moveTo({ group: p.group, position: SIDE[side] });
    },
    sendTo(panelId: string, areaId: string) {
      const to = group(areaId);
      panel(panelId).api.moveTo({ group: to, position: 'center', index: to.panels.length });
    },
    float(panelId: string, box: Box) {
      const p = panel(panelId);
      const from = p.group;
      // A floating panel's area is placed there whole, with every panel it holds (ADR-0018 §3); a docked one leaves its
      // area for a floating area of its own.
      api.addFloatingGroup(from.api.location.type === 'floating' ? from : p, place(box));
    },
    popOut(panelId: string) {
      // dockview opens the window on the client's own blank page, over where the panel's area stands and at its size.
      // What it answers is told by its own events, which the areas hear: a window refused, or one opened. An area's only
      // panel takes its whole area out, and dockview keeps the area's place hidden for it to come back to; a panel
      // beside others leaves them its area.
      const p = panel(panelId);
      void api.addPopoutGroup(p.group.panels.length === 1 ? p.group : p, { popoutUrl: POPOUT_URL });
    },
    dock(panelId: string): string {
      const p = panel(panelId);
      const from = kept().floatedFrom[panelId];
      floatedFrom(panelId, undefined);
      const back = docked().find((g) => g.id === from);
      if (back !== undefined) {
        const where = `back into the area of ${titled(back)}`;
        p.api.moveTo({ group: back, position: 'center', index: back.panels.length });
        return where;
      }
      // The place a whole area left for another window: shown again, with the panel back in it, where it stood.
      const place = api.groups.find((g) => g.id === from && g.api.location.type === 'grid' && g.panels.length === 0);
      if (place !== undefined) {
        place.api.setVisible(true);
        p.api.moveTo({ group: place, position: 'center' });
        return 'back where its area stood';
      }
      // Where it came from has gone: a new area on the right where the room holds one, else the last area.
      if (api.width >= needs(api.toJSON()).width + AREA_MIN.width) {
        const beside = api.addGroup({ direction: 'right' });
        p.api.moveTo({ group: beside, position: 'center' });
        return 'into a new area on the right';
      }
      const last = docked().at(-1);
      if (last === undefined) throw new Error('The window has no area to dock into.');
      const where = `into the area of ${titled(last)}`;
      p.api.moveTo({ group: last, position: 'center', index: last.panels.length });
      return where;
    },
    size(areaId: string, pixels: number) {
      const shared = line(areaId);
      if (shared === undefined) throw new Error(`The area ${areaId} shares no row or column.`);
      group(areaId).api.setSize(shared.axis === 'width' ? { width: pixels } : { height: pixels });
    },
    maximize(areaId: string) {
      group(areaId).api.maximize();
    },
    unmaximize() {
      api.exitMaximizedGroup();
    },
    gather(areaId: string, order: readonly string[]) {
      if (api.hasMaximizedGroup()) api.exitMaximizedGroup();
      const to = group(areaId);
      order.forEach((id, index) => panel(id).api.moveTo({ group: to, position: 'center', index, skipSetActive: true }));
    },
    moveWithin(panelId: string, index: number) {
      const p = panel(panelId);
      p.api.moveTo({ group: p.group, position: 'center', index, skipSetActive: true });
    },
    replace(layout) {
      api.fromJSON(layout);
    },
  };
  return { port, stop: () => moves.dispose() };
}
