import type { DockviewApi, DockviewGroupPanel, IDockviewPanel } from 'dockview-react';
import type { Area, Arrangement, Box, Side } from './arrangement';
import { AREA_MIN, areasOf, lineOf, needs, type Line } from './layout';

// The arrangement's port over dockview's own API (ADR-0018 §2): what `PanelAreas` registers while the panels stand in
// areas. It reads what dockview serialises, so it reads what the browser tab keeps, and changes the arrangement only
// through dockview's public calls; what each change may do has been checked by the verb before it gets here.

const SIDE = { right: 'right', left: 'left', down: 'bottom', up: 'top' } as const;

/** The port over this dockview. */
export function dockArrangement(api: DockviewApi): Arrangement {
  // The area a panel was floated from, so that `dock panel` puts it back there (the spec's DEFAULT reading of §3).
  const cameFrom = new Map<string, string>();

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
  const docked = (): readonly DockviewGroupPanel[] => api.groups.filter((g) => g.api.location.type === 'grid');
  const line = (areaId: string): Line | undefined => lineOf(api.toJSON(), areaId);
  const titled = (g: DockviewGroupPanel): string => {
    const shown = g.activePanel ?? g.panels[0];
    if (shown === undefined) throw new Error(`The area ${g.id} holds no panel.`);
    return shown.title ?? shown.id;
  };
  const place = (box: Box) => ({ x: box.left, y: box.top, width: box.width, height: box.height });

  return {
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
      if (from.api.location.type === 'floating' && from.panels.length === 1) {
        api.addFloatingGroup(from, place(box));
        return;
      }
      if (from.api.location.type === 'grid') cameFrom.set(panelId, from.id);
      api.addFloatingGroup(p, place(box));
    },
    dock(panelId: string): string {
      const p = panel(panelId);
      const from = cameFrom.get(panelId);
      cameFrom.delete(panelId);
      const back = docked().find((g) => g.id === from);
      if (back !== undefined) {
        const where = `back into the area of ${titled(back)}`;
        p.api.moveTo({ group: back, position: 'center', index: back.panels.length });
        return where;
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
      cameFrom.clear();
      api.fromJSON(layout);
    },
  };
}
