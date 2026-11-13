import { execute } from '../platform/commands/commands';
import { createWorkspaceStore, panelLabel } from '../platform/workspace/workspaceStore';
import { contextContract } from '../test/catalog';
import { floatBox, stripArrangement, type Area, type Arrangement, type Box, type Side } from './arrangement';
import type { Line, Size } from './layout';
import { panelCommands } from './panelVerbs';

// ADR-0018 §3 and §4: the verbs that arrange, and note 7's verbs within an area, over a fake port. What dockview draws
// for each is layer 3's (`e2e/areas.spec.ts`); here, what each verb refuses and asks of the port.

const valid = { region: 'North', asOf: '2026-10-26' };
const ROOM: Size = { width: 1440, height: 800 };

/** An area as the test draws it: its panels by title, the one it shows, and its size. */
interface Drawn {
  readonly id: string;
  readonly panels: readonly string[];
  readonly shown?: string;
  readonly floating?: boolean;
  /** In another window of the browser (ADR-0018 §9). */
  readonly window?: boolean;
  readonly width?: number;
  readonly height?: number;
}

/**
 * The panels `titles` open, standing in `drawn` areas, the active one `active`. The port answers the areas as drawn:
 * a panel the store has closed since leaves its area, and one it has opened since joins the area `opensIn` names, as
 * dockview puts it beside the active panel. It writes down what each verb asks of it, and moves a panel it is sent.
 */
function areas(titles: readonly string[], drawn: readonly Drawn[], active: string, lines: Readonly<Record<string, Line>> = {}) {
  const store = createWorkspaceStore(valid, contextContract);
  const ids = new Map(titles.map((title, i) => [title, store.getState().openPanel({ kind: 'app', title, app: 'rates', params: { workspaceId: `w${i}` } })]));
  // A panel reopened is a new panel of the same title: the open one is named first.
  const idOf = (title: string): string => {
    const id = store.getState().panels.find((p) => panelLabel(p) === title)?.id ?? ids.get(title);
    if (id === undefined) throw new Error(`No panel ${title}`);
    return id;
  };
  const titleOf = (id: string): string => {
    const panel = store.getState().panels.find((p) => p.id === id);
    if (panel === undefined) throw new Error(`No panel ${id} is open`);
    return panelLabel(panel);
  };
  store.getState().activate(idOf(active));
  const asked: string[] = [];
  let maximized: string | undefined;
  /** Each area's panels, in tab order. */
  const held = new Map(drawn.map((a) => [a.id, a.panels.map(idOf)]));
  const first = drawn[0];
  if (first === undefined) throw new Error('A fake arrangement draws one area at least.');
  const place = { opensIn: first.id };
  const holding = (panelId: string) => [...held].find(([, panels]) => panels.includes(panelId))?.[0];
  const port: Arrangement = {
    strip: undefined,
    areas: () => {
      const open = store.getState().panels.map((p) => p.id);
      for (const [id, panels] of held) held.set(id, panels.filter((p) => open.includes(p)));
      for (const id of open) if (holding(id) === undefined) held.set(place.opensIn, [...(held.get(place.opensIn) ?? []), id]);
      return drawn.flatMap((a): Area[] => {
        const panels = held.get(a.id) ?? [];
        if (panels.length === 0) return [];
        const shown = a.shown === undefined ? undefined : idOf(a.shown);
        return [{
          id: a.id, panels, shown: shown !== undefined && panels.includes(shown) ? shown : panels[0],
          where: a.floating === true ? 'floating' : a.window === true ? 'window' : 'docked', width: a.width ?? ROOM.width, height: a.height ?? ROOM.height,
        }];
      });
    },
    room: () => ROOM,
    lineOf: (areaId) => lines[areaId],
    maximized: () => maximized,
    split: (panelId: string, side: Side) => { asked.push(`split ${titleOf(panelId)} ${side}`); },
    sendTo: (panelId: string, areaId: string) => {
      asked.push(`send ${titleOf(panelId)} to ${areaId}`);
      for (const [id, panels] of held) held.set(id, panels.filter((p) => p !== panelId));
      held.set(areaId, [...(held.get(areaId) ?? []), panelId]);
    },
    float: (panelId: string, box: Box) => { asked.push(`float ${titleOf(panelId)} at ${box.left},${box.top} ${box.width}×${box.height}`); },
    popOut: (panelId: string) => { asked.push(`pop out ${titleOf(panelId)}`); },
    dock: (panelId: string) => { asked.push(`dock ${titleOf(panelId)}`); return 'beside the area of B'; },
    size: (areaId: string, pixels: number) => { asked.push(`size ${areaId} ${pixels}px`); },
    maximize: (areaId: string) => { maximized = areaId; asked.push(`maximize ${areaId}`); },
    unmaximize: () => { maximized = undefined; asked.push('unmaximize'); },
    gather: (areaId: string, order: readonly string[]) => { asked.push(`gather in ${areaId}: ${order.map(titleOf).join(', ')}`); },
    moveWithin: (panelId: string, index: number) => { asked.push(`move ${titleOf(panelId)} to ${index}`); },
    replace: () => { throw new Error('No verb but arrange replaces the arrangement.'); },
  };
  const notify = vi.fn();
  const command = (id: string) => {
    const found = panelCommands(store.getState(), notify, port).find((c) => c.id === id);
    if (found === undefined) throw new Error(`No command ${id}`);
    return found;
  };
  const titlesNow = () => store.getState().panels.map(panelLabel);
  const activeNow = () => {
    const id = store.getState().activePanelId;
    return id === undefined ? undefined : titleOf(id);
  };
  return { store, idOf, asked, notify, command, titlesNow, activeNow, place };
}

/** Two areas side by side, A and B in the left one showing B, C and D in the right one showing D, D active. */
const twoAreas = (lines?: Readonly<Record<string, Line>>) => areas(['A', 'B', 'C', 'D'], [
  { id: 'area-1', panels: ['A', 'B'], shown: 'B', width: 720 },
  { id: 'area-2', panels: ['C', 'D'], shown: 'D', width: 720 },
], 'D', lines);

describe('the verbs that arrange (ADR-0018 §3)', () => {
  it('each is refused in the strip, saying the arrangement comes back with more room; note 7\'s are not', () => {
    const store = createWorkspaceStore(valid, contextContract);
    for (const title of ['A', 'B']) store.getState().openPanel({ kind: 'app', title, app: 'rates', params: {} });
    const why = 'The panels are one strip below 768px; the arrangement comes back with more room.';
    const commands = panelCommands(store.getState(), vi.fn(), stripArrangement({ width: 390, height: 700 }, why));
    const of = (id: string) => commands.find((c) => c.id === id)?.unavailable;
    expect(['panel.split', 'panel.send', 'panel.float', 'panel.popOut', 'panel.dock', 'panel.size', 'panel.maximize', 'panel.unmaximize', 'panel.gather'].map(of))
      .toEqual(Array(9).fill(why));
    expect(['panel.close', 'panel.closeOthers', 'panel.move'].map(of)).toEqual([undefined, undefined, undefined]);
  });

  it('`split panel` moves the panel to a new area on that side, and is refused for an area\'s only panel, a floating one, and under two minimums', () => {
    const { asked, command } = twoAreas();
    expect(execute(command('panel.split'), { side: 'down' })).toBeUndefined();
    expect(asked).toEqual(['split D down']);
    // 720px across holds two areas of 320px; one of 600px does not.
    const narrow = areas(['A', 'B', 'C'], [{ id: 'area-1', panels: ['A', 'B'], width: 600 }, { id: 'area-2', panels: ['C'], width: 840 }], 'A');
    expect(execute(narrow.command('panel.split'), { side: 'right' })).toBe('Two areas side by side need 640px wide; this one is 600px.');
    expect(execute(narrow.command('panel.split'), { side: 'up' })).toBeUndefined();
    expect(execute(narrow.command('panel.split'), { side: 'below' })).toBe('split panel takes a side: right, left, down, up.');
    narrow.store.getState().activate(narrow.idOf('C'));
    expect(narrow.command('panel.split').unavailable).toBe('It is the only panel of its area: send it to another area, or float it.');
    const floating = areas(['A', 'B', 'C'], [{ id: 'area-1', panels: ['A'] }, { id: 'area-2', panels: ['B', 'C'], floating: true }], 'C');
    expect(floating.command('panel.split').unavailable).toBe('A floating area is one area: dock it first, or float another panel.');
    expect(narrow.asked).toEqual(['split A up']);
  });

  it('`send panel to` moves the panel into the area of the panel named, shows it, and refuses its own area and a window of one', () => {
    const { asked, command, activeNow } = twoAreas();
    expect(command('panel.send').args?.[0]?.complete?.({})).toEqual([{ value: 'B' }]);
    expect(execute(command('panel.send'), { panel: 'C' })).toBe("C is in this panel's area already.");
    expect(execute(command('panel.send'), { panel: 'a' })).toBeUndefined();
    expect(asked).toEqual(['send D to area-1']);
    expect(activeNow()).toBe('D');
    const one = areas(['A', 'B'], [{ id: 'area-1', panels: ['A', 'B'] }], 'A');
    expect(one.command('panel.send').unavailable).toBe('There is no other area.');
  });

  it('`float panel` floats it in the middle or over a half of the panels\' space, and never leaves none docked', () => {
    const { asked, command } = twoAreas();
    expect(execute(command('panel.float'), { where: 'centre' })).toBeUndefined();
    expect(execute(command('panel.float'), { where: 'center' })).toBeUndefined();
    expect(execute(command('panel.float'), { where: 'right' })).toBeUndefined();
    expect(execute(command('panel.float'), { where: 'middle' })).toBe('float panel takes where: centre, left, right, top, bottom.');
    // The line runs none without its where: one missing is a defect thrown on, never shown as a reason.
    expect(() => execute(command('panel.float'))).toThrow('The line ran a command without its where.');
    const [centre, right] = [floatBox('centre', ROOM), floatBox('right', ROOM)];
    expect(centre).toEqual({ left: 288, top: 160, width: 864, height: 480 });
    expect(asked).toEqual([
      'float D at 288,160 864×480', 'float D at 288,160 864×480', `float D at ${right.left},${right.top} ${right.width}×${right.height}`,
    ]);
    const alone = areas(['A', 'B'], [{ id: 'area-1', panels: ['A'] }, { id: 'area-2', panels: ['B'], floating: true }], 'A');
    expect(alone.command('panel.float').unavailable).toBe('It is the only panel docked in the window; floating it would leave none docked.');
    // A floating panel alone in its area is placed again.
    alone.store.getState().activate(alone.idOf('B'));
    expect(execute(alone.command('panel.float'), { where: 'left' })).toBeUndefined();
    expect(alone.asked).toEqual(['float B at 0,0 720×800']);
  });

  it('`dock panel` puts a floating panel, or one in another window, back and says where; a docked panel is refused', () => {
    const { asked, command, notify, store, idOf } = areas(['A', 'B', 'C'], [
      { id: 'area-1', panels: ['A'] }, { id: 'area-2', panels: ['B'], floating: true }, { id: 'area-3', panels: ['C'], window: true },
    ], 'B');
    expect(execute(command('panel.dock'))).toBeUndefined();
    store.getState().activate(idOf('C'));
    expect(execute(command('panel.dock'))).toBeUndefined();
    expect(asked).toEqual(['dock B', 'dock C']);
    expect(notify).toHaveBeenCalledWith('done', 'Docked B beside the area of B.');
    store.getState().activate(idOf('A'));
    expect(command('panel.dock').unavailable).toBe('This panel is neither floating nor in another window.');
  });

  it('`pop out panel` asks for a window of the panel\'s own, and never leaves none docked in the main window (§9)', () => {
    const { asked, command } = twoAreas();
    expect(execute(command('panel.popOut'))).toBeUndefined();
    expect(asked).toEqual(['pop out D']);
    // The main window keeps one docked panel at least, as `float panel` does.
    const alone = areas(['A', 'B'], [{ id: 'area-1', panels: ['A'] }, { id: 'area-2', panels: ['B'], window: true }], 'A');
    expect(alone.command('panel.popOut').unavailable).toBe('It is the only panel docked in the window; popping it out would leave none docked.');
    // Alone in a window, it is there already; beside another panel there, it takes a window of its own.
    alone.store.getState().activate(alone.idOf('B'));
    expect(alone.command('panel.popOut').unavailable).toBe('It is in a window of its own already.');
    const shared = areas(['A', 'B', 'C'], [{ id: 'area-1', panels: ['A'] }, { id: 'area-2', panels: ['B', 'C'], window: true }], 'C');
    expect(execute(shared.command('panel.popOut'))).toBeUndefined();
    // A floating panel goes out too, the docked ones staying.
    const floating = areas(['A', 'B'], [{ id: 'area-1', panels: ['A'] }, { id: 'area-2', panels: ['B'], floating: true }], 'B');
    expect(execute(floating.command('panel.popOut'))).toBeUndefined();
    expect([...shared.asked, ...floating.asked]).toEqual(['pop out C', 'pop out B']);
  });

  it('a panel in another window is one area there: the verbs that arrange the main window\'s areas say to dock it first (§9)', () => {
    const { command } = areas(['A', 'B', 'C'], [
      { id: 'area-1', panels: ['A'] }, { id: 'area-2', panels: ['B'] }, { id: 'area-3', panels: ['C'], window: true },
    ], 'C', { 'area-3': { axis: 'width', total: 1440, othersNeed: 320 } });
    const dockFirst = 'It is in a window of its own, which is one area: dock it first.';
    expect(['panel.split', 'panel.float', 'panel.maximize'].map((id) => command(id).unavailable)).toEqual([dockFirst, dockFirst, dockFirst]);
    expect(command('panel.size').unavailable).toBe('A panel in a window of its own is sized by that window.');
    expect(command('panel.gather').unavailable).toBe('It is in a window of its own: gather the panels from an area of the main window.');
    // `send panel to` moves it between the windows, either way, never leaving the main window none docked.
    expect(execute(command('panel.send'), { panel: 'A' })).toBeUndefined();
    const last = areas(['A', 'B'], [{ id: 'area-1', panels: ['A'] }, { id: 'area-2', panels: ['B'], window: true }], 'A');
    expect(execute(last.command('panel.send'), { panel: 'B' })).toBe('It is the only panel docked in the window; sending it there would leave none docked.');
    const floatingToo = areas(['A', 'B'], [{ id: 'area-1', panels: ['A'] }, { id: 'area-2', panels: ['B'], floating: true }], 'A');
    expect(execute(floatingToo.command('panel.send'), { panel: 'B' })).toBe('It is the only panel docked in the window; sending it there would leave none docked.');
  });

  it('`size panel` gives the area its share of its row in pixels, within the minimums, and refuses a floating or a lone area', () => {
    const { asked, command } = twoAreas({ 'area-2': { axis: 'width', total: 1440, othersNeed: 320 } });
    expect(execute(command('panel.size'), { percent: '25%' })).toBeUndefined();
    expect(execute(command('panel.size'), { percent: '10' })).toBe('10% is 144px; an area is at least 320px wide.');
    expect(execute(command('panel.size'), { percent: '90' })).toBe('90% leaves 144px; the others need 320px wide.');
    expect(execute(command('panel.size'), { percent: '95' })).toBe('size panel takes a whole percent from 10 to 90.');
    expect(asked).toEqual(['size area-2 360px']);
    const lone = areas(['A', 'B'], [{ id: 'area-1', panels: ['A', 'B'] }], 'A');
    expect(lone.command('panel.size').unavailable).toBe('Its area is alone in its row and its column: there is nothing to share.');
    const floating = areas(['A', 'B'], [{ id: 'area-1', panels: ['A'] }, { id: 'area-2', panels: ['B'], floating: true }], 'B');
    expect(floating.command('panel.size').unavailable).toBe('A floating area is sized by its edges, or placed with float panel.');
  });

  it('`maximize panel` gives its area the room, and `unmaximize panel` gives it back; one area, or none maximized, is refused', () => {
    const { asked, command } = twoAreas();
    expect(command('panel.unmaximize').unavailable).toBe('No area is maximized.');
    expect(execute(command('panel.maximize'))).toBeUndefined();
    expect(command('panel.maximize').unavailable).toBe('Its area is maximized already.');
    expect(execute(command('panel.unmaximize'))).toBeUndefined();
    expect(asked).toEqual(['maximize area-2', 'unmaximize']);
    const one = areas(['A', 'B'], [{ id: 'area-1', panels: ['A', 'B'] }], 'A');
    expect(one.command('panel.maximize').unavailable).toBe('Its area is the only one of the window: it has the whole room already.');
  });

  it('`gather panels` puts every panel in the panel\'s own area, the pinned first, in reading order', () => {
    const { asked, command, store, idOf } = twoAreas();
    store.getState().pinPanel(idOf('C'), true);
    expect(execute(command('panel.gather'))).toBeUndefined();
    expect(asked).toEqual(['gather in area-2: C, A, B, D']);
    const one = areas(['A', 'B'], [{ id: 'area-1', panels: ['A', 'B'] }], 'A');
    expect(one.command('panel.gather').unavailable).toBe('The panels are in one area already.');
  });
});

describe('note 7\'s verbs within an area (ADR-0018 §4)', () => {
  it('`close other panels` and `close panels to the right` close in the panel\'s area alone; `close all panels` closes every one', () => {
    const { command, titlesNow, store, idOf, activeNow } = twoAreas();
    store.getState().activate(idOf('C'));
    expect(execute(command('panel.closeRight'))).toBeUndefined();
    expect(titlesNow()).toEqual(['A', 'B', 'C']);
    expect(command('panel.closeRight').unavailable).toBe('No panel is to the right of this one in its area.');
    expect(command('panel.closeOthers').unavailable).toBe('There is no other panel in its area.');
    store.getState().activate(idOf('A'));
    expect(execute(command('panel.closeOthers'))).toBeUndefined();
    expect(titlesNow()).toEqual(['A', 'C']);
    expect(activeNow()).toBe('A');
    expect(execute(command('panel.closeAll'))).toBeUndefined();
    expect(titlesNow()).toEqual([]);
  });

  it('`close panel` on the active panel shows the panel its area showed before, not one in another area', () => {
    const { command, store, idOf, activeNow } = twoAreas();
    for (const title of ['C', 'A', 'D']) store.getState().activate(idOf(title));
    expect(execute(command('panel.close'))).toBeUndefined();
    expect(activeNow()).toBe('C');
  });

  it('`move panel` moves the panel among its area\'s panels, and refuses past its area\'s end', () => {
    const { asked, command } = twoAreas();
    expect(execute(command('panel.move'), { where: 'right' })).toBe('This is the last panel already.');
    expect(execute(command('panel.move'), { where: 'first' })).toBeUndefined();
    expect(asked).toEqual(['move D to 0']);
  });

  it('`next panel` and `previous panel` go through every panel in reading order, across the areas and around the ends', () => {
    const { command, activeNow } = areas(['A', 'B', 'C'], [{ id: 'area-1', panels: ['C', 'A'] }, { id: 'area-2', panels: ['B'] }], 'A');
    const seen: (string | undefined)[] = [];
    for (let i = 0; i < 3; i++) {
      expect(execute(command('panel.next'))).toBeUndefined();
      seen.push(activeNow());
    }
    expect(seen).toEqual(['B', 'C', 'A']);
    expect(execute(command('panel.previous'))).toBeUndefined();
    expect(activeNow()).toBe('C');
  });

  it('`reopen panel` puts a panel back in the area it stood in, and says so where that area has gone', () => {
    const { asked, command, notify, store, idOf, place } = twoAreas();
    // A closed in the left area; reopened while D, on the right, is active, it opens beside D and goes back.
    store.getState().activate(idOf('A'));
    expect(execute(command('panel.close'))).toBeUndefined();
    store.getState().activate(idOf('D'));
    place.opensIn = 'area-2';
    expect(execute(command('panel.reopen'))).toBeUndefined();
    expect(asked).toEqual(['send A to area-1']);
    expect(notify).toHaveBeenLastCalledWith('done', 'Reopened A beside B, in the area it stood in.');
    // The left area closed whole, A and B: A reopened has no area to go back to, and stays beside D, saying so.
    store.getState().activate(idOf('A'));
    expect(execute(command('panel.closeOthers'))).toBeUndefined();
    expect(execute(command('panel.close'))).toBeUndefined();
    expect(execute(command('panel.reopen'))).toBeUndefined();
    expect(asked).toEqual(['send A to area-1']);
    expect(notify).toHaveBeenLastCalledWith('done', 'Reopened A beside D: the area it stood in has gone.');
  });

  it('`reopen panel` in one area says nothing of where: the panel is where it is seen (ADR-0018 §4\'s note)', () => {
    const { command, notify, store, idOf } = areas(['A', 'B'], [{ id: 'area-1', panels: ['A', 'B'] }], 'A');
    expect(execute(command('panel.close'))).toBeUndefined();
    notify.mockClear();
    expect(execute(command('panel.reopen'))).toBeUndefined();
    expect(store.getState().panels.map((p) => p.id)).toContain(idOf('A'));
    expect(notify).not.toHaveBeenCalled();
  });
});
