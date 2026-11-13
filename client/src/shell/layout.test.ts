import { Orientation, type SerializedDockview } from 'dockview-react';
import { AREA_MIN, areasOf, fits, lineOf, needs, oneArea, readingOrder, reconciled, stripOrder, withWindowsDocked, type OpenPanels } from './layout';

// ADR-0018 §1, §5 and §7: what an arrangement needs of the panels' space, its reading order, and a kept arrangement
// made to agree with the panels open when it is drawn again.

const leaf = (id: string, views: string[], activeView = views[0], size = 500) => ({ type: 'leaf' as const, size, data: { id, views, activeView } });
const branch = (size: number, ...data: unknown[]) => ({ type: 'branch' as const, size, data }) as SerializedDockview['grid']['root'];

/** A over B on the left, C on the right; D floating. */
const arranged: SerializedDockview = {
  grid: {
    root: branch(800, branch(600, leaf('ab', ['a', 'b'], 'b'), leaf('c', ['c'])), leaf('right', ['r1', 'r2'], 'r1')),
    width: 1200,
    height: 800,
    orientation: Orientation.HORIZONTAL,
  },
  panels: {},
  floatingGroups: [{ data: { id: 'float', views: ['d'], activeView: 'd' }, position: { left: 10, top: 10, width: 400, height: 300 } }],
};

const open = (order: string[], active?: string, recent: string[] = []): OpenPanels => ({
  order,
  titles: Object.fromEntries(order.map((id) => [id, id.toUpperCase()])),
  recent,
  activePanelId: active,
});

describe('an arrangement', () => {
  it('ADR-0018 §5: needs an area\'s minimum for each area, side by side adding widths and one above another heights', () => {
    expect(needs(arranged)).toEqual({ width: 2 * AREA_MIN.width, height: 2 * AREA_MIN.height });
    expect(fits(arranged, { width: 640, height: 480 })).toBe(false); // under 768px there are no areas
    expect(fits(arranged, { width: 1000, height: 479 })).toBe(false);
    expect(fits(arranged, { width: 1000, height: 480 })).toBe(true);
    const three: SerializedDockview = { ...arranged, grid: { ...arranged.grid, root: branch(800, leaf('x', ['x']), leaf('y', ['y']), leaf('z', ['z'])) } };
    expect(fits(three, { width: 959, height: 800 })).toBe(false);
    expect(fits(three, { width: 960, height: 800 })).toBe(true);
    expect(fits(undefined, { width: 768, height: 240 })).toBe(true);
    expect(fits(undefined, { width: 767, height: 800 })).toBe(false);
  });

  it('ADR-0018 §1: reads its areas in the order of their splits, the left or upper part first, then the floating ones', () => {
    expect(areasOf(arranged).map((a) => [a.id, a.shown, a.where])).toEqual([['ab', 'b', 'docked'], ['c', 'c', 'docked'], ['right', 'r1', 'docked'], ['float', 'd', 'floating']]);
    expect(readingOrder(arranged)).toEqual(['a', 'b', 'c', 'r1', 'r2', 'd']);
    expect(stripOrder(arranged, (id) => id === 'r2' || id === 'c')).toEqual(['c', 'r2', 'a', 'b', 'r1', 'd']);
  });
});

/**
 * The arrangement above with two other windows (ADR-0018 §9): `c`'s whole area went out, leaving its place hidden in
 * the main window; `r2` went out of the right area alone; D still floats.
 */
const withWindows: SerializedDockview = {
  ...arranged,
  grid: {
    ...arranged.grid,
    root: branch(800, branch(600, leaf('ab', ['a', 'b'], 'b'), { ...leaf('c', []), visible: false }), leaf('right', ['r1'], 'r1')),
  },
  popoutGroups: [
    { data: { id: 'c-out', views: ['c'], activeView: 'c' }, gridReferenceGroup: 'c', position: { left: 0, top: 0, width: 500, height: 400 } },
    { data: { id: 'r2-out', views: ['r2'], activeView: 'r2' }, gridReferenceGroup: 'right', position: null },
  ],
};

describe('an arrangement with other windows (ADR-0018 §9)', () => {
  it('reads their areas after the floating ones, in the order they were opened, and the place an area left is no area', () => {
    expect(areasOf(withWindows).map((a) => [a.id, a.where])).toEqual([
      ['ab', 'docked'], ['right', 'docked'], ['float', 'floating'], ['c-out', 'window'], ['r2-out', 'window'],
    ]);
    expect(readingOrder(withWindows)).toEqual(['a', 'b', 'r1', 'd', 'c', 'r2']);
    // The main window's areas alone take its room: A and B's area has its column to itself.
    expect(needs(withWindows)).toEqual({ width: 2 * AREA_MIN.width, height: AREA_MIN.height });
    expect(lineOf(withWindows, 'ab')).toEqual({ axis: 'width', total: 1100, othersNeed: AREA_MIN.width });
  });

  it('folds each window\'s panels back into the area they came from, shown again where it was hidden, and says so', () => {
    const { layout, returned } = withWindowsDocked(withWindows);
    expect(layout.popoutGroups).toBeUndefined();
    expect(areasOf(layout).map((a) => [a.id, a.panels, a.shown])).toEqual([['ab', ['a', 'b'], 'b'], ['c', ['c'], 'c'], ['right', ['r1', 'r2'], 'r1'], ['float', ['d'], 'd']]);
    expect(returned).toEqual([{ panels: ['c'], into: 'c', cameFrom: true }, { panels: ['r2'], into: 'right', cameFrom: true }]);
    expect(needs(layout)).toEqual(needs(arranged));
  });

  it('folds a window whose area has gone into the main window\'s last area, after its panels, and says where', () => {
    const gone: SerializedDockview = { ...withWindows, popoutGroups: [{ ...withWindows.popoutGroups![1]!, gridReferenceGroup: 'gone' }] };
    const { layout, returned } = withWindowsDocked(gone);
    expect(areasOf(layout).find((a) => a.id === 'right')?.panels).toEqual(['r1', 'r2']);
    expect(returned).toEqual([{ panels: ['r2'], into: 'right', cameFrom: false }]);
    // The place `c` left stays empty, and goes with its split when the panels open are drawn.
    const made = reconciled(layout, open(['a', 'b', 'r1', 'r2', 'd'], 'r2'))!;
    expect(areasOf(made).map((a) => [a.id, a.panels])).toEqual([['ab', ['a', 'b']], ['right', ['r1', 'r2']], ['float', ['d']]]);
    expect(withWindowsDocked(arranged)).toEqual({ layout: arranged, returned: [] });
  });
});

describe('a kept arrangement drawn again', () => {
  it('ADR-0018 §5: loses the panels closed since, and an area left empty goes with its split', () => {
    const made = reconciled(arranged, open(['a', 'b', 'r1', 'r2', 'd'], 'r2'));
    expect(made).toBeDefined();
    expect(areasOf(made!).map((a) => [a.id, a.panels])).toEqual([['ab', ['a', 'b']], ['right', ['r1', 'r2']], ['float', ['d']]]);
    expect(made!.activeGroup).toBe('right');
    expect(Object.keys(made!.panels)).toEqual(['a', 'b', 'r1', 'r2', 'd']);
    expect(made!.panels.a).toMatchObject({ id: 'a', title: 'A', params: { panelId: 'a' } });
  });

  it('ADR-0018 §5: puts a panel opened since in the area of the panel before it in the strip, in the strip\'s order', () => {
    // n opened beside r1 in the strip, then the strip moved b before a.
    const made = reconciled(arranged, open(['b', 'a', 'c', 'r1', 'n', 'r2', 'd'], 'n'))!;
    expect(areasOf(made).map((a) => [a.id, a.panels, a.shown])).toEqual([['ab', ['b', 'a'], 'b'], ['c', ['c'], 'c'], ['right', ['r1', 'n', 'r2'], 'r1'], ['float', ['d'], 'd']]);
    expect(made.activeGroup).toBe('right');
  });

  it('ADR-0018 §5: an area whose shown panel was closed shows the one of its panels shown last', () => {
    const made = reconciled(arranged, open(['a', 'c', 'r1', 'r2', 'd'], 'c', ['c', 'r2', 'a']))!;
    expect(areasOf(made).find((a) => a.id === 'ab')?.shown).toBe('a');
  });

  it('ADR-0018 §5: with no area of the window left, starts again in one area', () => {
    expect(reconciled(arranged, open(['d', 'z'], 'z'))).toBeUndefined();
    const one = oneArea(open(['d', 'z'], 'z'));
    expect(areasOf(one).map((a) => [a.panels, a.shown])).toEqual([[['d', 'z'], 'z']]);
  });
});
