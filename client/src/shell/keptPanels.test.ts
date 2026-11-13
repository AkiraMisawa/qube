import { createWorkspaceStore, panelLabel } from '../platform/workspace/workspaceStore';
import { contextContract } from '../test/catalog';
import { keepPanels, keptArrangement, keptKey, readKept } from './keptPanels';

// ADR-0018 §7: a browser tab keeps its panels and their arrangement across a reload, and reads back only a value of
// its own version, whole. What a reload draws is layer 3's (`e2e/areas.spec.ts`).

const valid = { region: 'North', asOf: '2026-10-26' };
const USER = 'demo';
const fresh = () => createWorkspaceStore(valid, contextContract);

afterEach(() => {
  window.sessionStorage.clear();
  keptArrangement.getState().keep(undefined);
  keptArrangement.getState().keepFloatedFrom({});
});

describe('the panels a browser tab keeps (ADR-0018 §7)', () => {
  it('a reload takes up the panels, the active one, those closed, the arrangement and where a floating panel came from', () => {
    const before = fresh();
    const stop = keepPanels(before, vi.fn(), USER);
    const a = before.getState().openPanel({ kind: 'app', title: 'Pricing DB', app: 'pricing-db', params: {} });
    before.getState().openPanel({ kind: 'run', title: 'Run R1', runId: 'R1', viewKey: undefined });
    const rates = before.getState().openPanel({ kind: 'app', title: 'Rates', app: 'rates', params: { workspaceId: 'w1' } });
    before.getState().closePanel(rates);
    before.getState().activate(a);
    const layout = { grid: { root: { type: 'branch', data: [] }, height: 800, width: 1440, orientation: 'HORIZONTAL' }, panels: {} } as never;
    keptArrangement.getState().keep(layout);
    keptArrangement.getState().keepFloatedFrom({ 'panel-3': '2' });
    stop();

    keptArrangement.getState().keep(undefined);
    keptArrangement.getState().keepFloatedFrom({});
    const after = fresh();
    const notify = vi.fn();
    keepPanels(after, notify, USER)();
    expect(after.getState().panels.map(panelLabel)).toEqual(['Pricing DB', 'Run R1']);
    expect(after.getState().activePanelId).toBe(a);
    expect(after.getState().closed.map((c) => c.page.title)).toEqual(['Rates']);
    expect(keptArrangement.getState().layout).toEqual(layout);
    expect(keptArrangement.getState().floatedFrom).toEqual({ 'panel-3': '2' });
    expect(notify).not.toHaveBeenCalled();
  });

  // ADR-0006 note 9: a panel's name is the user's, so a reload keeps it, on a panel open and on one closed.
  it('ADR-0006 note 9: a reload keeps the names the user gave panels, open and closed, and refuses a name it could not give', () => {
    const before = fresh();
    const stop = keepPanels(before, vi.fn(), USER);
    const a = before.getState().openPanel({ kind: 'app', title: 'Rates', app: 'rates', params: { workspaceId: 'w1' } });
    const b = before.getState().openPanel({ kind: 'app', title: 'Rates', app: 'rates', params: { workspaceId: 'w2' } });
    before.getState().renamePanel(a, 'Hedge desk');
    before.getState().renamePanel(b, 'Swaps');
    before.getState().closePanel(b);
    stop();

    const after = fresh();
    keepPanels(after, vi.fn(), USER)();
    expect(after.getState().panels.map(panelLabel)).toEqual(['Hedge desk']);
    after.getState().reopenPanel();
    expect(after.getState().panels.map(panelLabel)).toEqual(['Hedge desk', 'Swaps']);

    const named = (name: unknown) => JSON.stringify({ version: 1, panels: { panels: [{ id: 'panel-1', kind: 'run', title: 'A', runId: 'r1', name }], recent: [], closed: [] }, floatedFrom: {} });
    expect(readKept(named('Mine'))).toMatchObject({ kept: { panels: { panels: [{ name: 'Mine' }] } } });
    expect(readKept(named(7))).toEqual({ refused: 'panels.panels[0].name is not text' });
    // A name the store could not have given: empty, spaced, its title, or one the strip would read as a number.
    for (const name of [' ', ' Mine', 'My  rates', 'a', 'Mine · 2', 'x'.repeat(61)]) {
      expect(readKept(named(name))).toEqual({ refused: 'panels.panels[0].name is not a name the workbench gives' });
    }
    const closedNamed = (name: unknown) => JSON.stringify({ version: 1, panels: { panels: [], recent: [], closed: [{ page: { kind: 'run', title: 'A', runId: 'r1' }, index: 0, pinned: false, name }] }, floatedFrom: {} });
    expect(readKept(closedNamed('Mine'))).toMatchObject({ kept: { panels: { closed: [{ name: 'Mine' }] } } });
    expect(readKept(closedNamed('A'))).toEqual({ refused: 'panels.closed[0].name is not a name the workbench gives' });
  });

  it('another user\'s panels are not this user\'s, and a tab that kept nothing starts with none', () => {
    const before = fresh();
    const stop = keepPanels(before, vi.fn(), USER);
    before.getState().openPanel({ kind: 'app', title: 'Pricing DB', app: 'pricing-db', params: {} });
    stop();
    expect(readKept(window.sessionStorage.getItem(keptKey(USER)))).toMatchObject({ kept: { panels: { panels: [{ title: 'Pricing DB' }] } } });
    const other = fresh();
    keepPanels(other, vi.fn(), 'someone-else')();
    expect(other.getState().panels).toEqual([]);
  });

  it('a value of another version, or one that does not read whole, is not applied, and the shell says why', () => {
    expect(readKept(null)).toEqual({ kept: undefined });
    expect(readKept('{"version":2,"panels":{}}')).toEqual({ refused: 'it is of version 2, and this workbench reads 1' });
    expect(readKept('{"version":1,"panels":{"panels":[{"id":"panel-1","title":"A","kind":"chart"}],"recent":[],"closed":[]}}'))
      .toEqual({ refused: 'panels.panels[0].kind is not a kind of panel' });
    expect(readKept('{"version":1,"panels":{"panels":[],"recent":[],"closed":[]},"layout":{}}')).toEqual({ refused: 'layout.grid is not an object' });
    const grid = (root: unknown) => JSON.stringify({ version: 1, panels: { panels: [], recent: [], closed: [] },
      layout: { grid: { root, width: 1440, height: 800, orientation: 'HORIZONTAL' }, panels: {} }, floatedFrom: {} });
    expect(readKept(grid({ type: 'branch', data: [{ type: 'leaf', size: 720, data: { id: '1', views: ['panel-1'] } }] }))).toMatchObject({ kept: {} });
    expect(readKept(grid({ type: 'branch', data: [{ type: 'leaf', data: { id: '1', views: 'panel-1' } }] })))
      .toEqual({ refused: 'layout.grid.root.data[0].data.views is not a list' });
    expect(readKept(grid({ type: 'tree', data: [] }))).toEqual({ refused: 'layout.grid.root.type is not a part of a grid' });
    const floating = (position: unknown) => JSON.stringify({ version: 1, panels: { panels: [], recent: [], closed: [] },
      layout: { grid: { root: { type: 'branch', data: [] }, width: 1440, height: 800, orientation: 'HORIZONTAL' }, panels: {},
        floatingGroups: [{ data: { id: '2', views: ['panel-1'] }, position }] }, floatedFrom: {} });
    expect(readKept(floating({ right: 0, top: 40, width: 400, height: 300 }))).toMatchObject({ kept: {} });
    expect(readKept(floating({ top: 40, width: 400, height: 300 })))
      .toEqual({ refused: 'layout.floatingGroups[0].position is placed from neither or both of left and right' });
    expect(readKept(floating({ left: 0, top: '40', width: 400, height: 300 }))).toEqual({ refused: 'layout.floatingGroups[0].position.top is not a number' });
    // ADR-0018 §9: another window's area and the area it came from are read; they come back docked as it is drawn.
    const windows = (popout: unknown) => JSON.stringify({ version: 1, panels: { panels: [], recent: [], closed: [] },
      layout: { grid: { root: { type: 'branch', data: [] }, width: 1440, height: 800, orientation: 'HORIZONTAL' }, panels: {},
        popoutGroups: [popout] }, floatedFrom: {} });
    expect(readKept(windows({ data: { id: '3', views: ['panel-1'] }, gridReferenceGroup: '1', position: null }))).toMatchObject({ kept: {} });
    expect(readKept(windows({ data: { id: '3', views: ['panel-1'] }, gridReferenceGroup: 1, position: null })))
      .toEqual({ refused: 'layout.popoutGroups[0].gridReferenceGroup is not text' });
    expect(readKept(windows({ position: null }))).toEqual({ refused: 'layout.popoutGroups[0].grid is not an object' });
    expect('refused' in readKept('not json')).toBe(true);

    window.sessionStorage.setItem(keptKey(USER), '{"version":2,"panels":{}}');
    const store = fresh();
    const notify = vi.fn();
    keepPanels(store, notify, USER)();
    expect(store.getState().panels).toEqual([]);
    expect(notify).toHaveBeenCalledWith('refused', 'The panels this tab kept could not be read, so it starts with none: it is of version 2, and this workbench reads 1.', { stays: true });
  });

  it('where who the user is could not be read, nothing is kept, and the shell says so', () => {
    const notify = vi.fn();
    keepPanels(fresh(), notify, undefined, 'the server did not answer')();
    expect(window.sessionStorage.length).toBe(0);
    expect(notify).toHaveBeenCalledWith('refused', 'Your panels are not kept across a reload of this tab: who you are could not be read (the server did not answer).', { stays: true });
  });
});
