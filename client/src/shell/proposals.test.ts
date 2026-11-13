import { Orientation, type SerializedDockview } from 'dockview-react';
import { execute } from '../platform/commands/commands';
import { createWorkspaceStore, panelLabel, type PanelDraft } from '../platform/workspace/workspaceStore';
import { contextContract } from '../test/catalog';
import { stripArrangement, type Area, type Arrangement } from './arrangement';
import { keptArrangement } from './keptPanels';
import { areasOf, panelState, type Size } from './layout';
import {
  arrangeCommands, forgetArranged, panelsContext, planOf, proposalLayout, registerPageOpener, takeProposal, type Arranging, type ProposedArea,
  type ProposedPlace,
} from './proposals';

// ADR-0018 §8: the AI proposes an arrangement and the user applies it, by `arrange <n>` or the card's Apply, whole or not
// at all; `arrange back` puts back what the last one replaced, once.

const valid = { region: 'North', asOf: '2026-10-26' };
const WIDE: Size = { width: 1440, height: 800 };

/** An arrangement port over the last layout drawn, as dockview's is: what `arrange` replaced is what it reads. */
function fakeAreas(room: Size, first: SerializedDockview | undefined) {
  let drawn = first;
  const replaced: SerializedDockview[] = [];
  const port: Arrangement = {
    strip: undefined,
    areas: () => (drawn === undefined ? [] : areasOf(drawn).map((a): Area => ({ ...a, width: room.width, height: room.height }))),
    room: () => room,
    lineOf: () => undefined,
    maximized: () => undefined,
    split: () => { throw new Error('not drawn here'); },
    sendTo: () => { throw new Error('not drawn here'); },
    float: () => { throw new Error('not drawn here'); },
    popOut: () => { throw new Error('not drawn here'); },
    dock: () => { throw new Error('not drawn here'); },
    size: () => { throw new Error('not drawn here'); },
    maximize: () => { throw new Error('not drawn here'); },
    unmaximize: () => { throw new Error('not drawn here'); },
    gather: () => { throw new Error('not drawn here'); },
    moveWithin: () => { throw new Error('not drawn here'); },
    replace: (layout) => { drawn = layout; replaced.push(layout); },
  };
  return { port, replaced };
}

function page(titles: readonly string[], areas?: (store: ReturnType<typeof createWorkspaceStore>) => Arrangement) {
  const store = createWorkspaceStore(valid, contextContract);
  const ids = titles.map((title, i) => store.getState().openPanel({ kind: 'app', title, app: 'rates.workspace', params: { workspaceId: `w${i}` } }));
  store.getState().activate(ids[0]!);
  const fake = fakeAreas(WIDE, undefined);
  const port = areas?.(store) ?? fake.port;
  const runs: string[] = [];
  const on: Arranging = {
    state: () => store.getState(),
    areas: () => port,
    openRun: (runId) => {
      runs.push(runId);
      return Promise.resolve<PanelDraft>({ kind: 'run', title: `Run of ${runId}`, runId, viewKey: undefined });
    },
  };
  const notify = vi.fn();
  const command = (id: string) => {
    const found = arrangeCommands(on, notify).find((c) => c.id === id);
    if (found === undefined) throw new Error(`No command ${id}`);
    return found;
  };
  const titlesNow = () => store.getState().panels.map(panelLabel);
  const id = (i: number): string => {
    const found = ids[i];
    if (found === undefined) throw new Error(`No panel ${i}`);
    return found;
  };
  return { store, ids, id, on, notify, command, replaced: fake.replaced, runs, titlesNow };
}

let calls = 0;
/** A proposal as the server answers it, taken as its card's tool call is done. */
function proposed(areas: readonly ProposedArea[], places: readonly ProposedPlace[], needs: Size = { width: 640, height: 240 }) {
  calls += 1;
  return takeProposal(`call-${calls}`, { answer: { areas, places, count: areas.length, width: needs.width, height: needs.height, said: 'Proposed.' } });
}

const SIDE_BY_SIDE: readonly ProposedArea[] = [{ area: 'left' }, { area: 'right', beside: 'left', side: 'right', share: 50 }];

beforeEach(() => {
  keptArrangement.getState().keep(undefined);
  keptArrangement.getState().keepFloatedFrom({});
  forgetArranged();
});

describe('a proposal drawn (ADR-0018 §8)', () => {
  it('stands each area where its side and share put it, and a floating one at its place', () => {
    const plan = planOf({ areas: [{ area: 'a' }, { area: 'b', beside: 'a', side: 'right', share: 25 }, { area: 'c', beside: 'b', side: 'down', share: 50 }, { area: 'f', float: 'centre' }] });
    expect(plan.map((p) => [p.area, p.left, p.top, p.width, p.height, p.floating])).toEqual([
      ['a', 0, 0, 0.75, 1, false],
      ['b', 0.75, 0, 0.25, 0.5, false],
      ['c', 0.75, 0.5, 0.25, 0.5, false],
      ['f', 0.2, 0.2, 0.6, 0.6, true],
    ]);
  });

  it('is a dockview arrangement of new areas, the open panels it does not place in its first area', () => {
    let n = 0;
    const layout = proposalLayout(
      { areas: [...SIDE_BY_SIDE, { area: 'over', float: 'right' }], places: [{ area: 'left', panel: 'p1', title: 'A', shown: true }, { area: 'right', panel: 'p2', title: 'B', shown: true }, { area: 'over', panel: 'p3', title: 'C', shown: true }] },
      ['p1', 'p2', 'p3'], { p1: 'A', p2: 'B', p3: 'C', p4: 'D' }, ['p4'], WIDE, 'p2', () => `new-${++n}`);

    expect(layout.grid.orientation).toBe(Orientation.HORIZONTAL);
    expect(layout.grid.root.data).toEqual([
      { type: 'leaf', size: 720, data: { id: 'new-1', views: ['p1', 'p4'], activeView: 'p1' } },
      { type: 'leaf', size: 720, data: { id: 'new-2', views: ['p2'], activeView: 'p2' } },
    ]);
    expect(layout.floatingGroups).toEqual([{ data: { id: 'new-3', views: ['p3'], activeView: 'p3' }, position: { left: 720, top: 0, width: 720, height: 800 } }]);
    expect(Object.keys(layout.panels)).toEqual(['p1', 'p4', 'p2', 'p3']);
    expect(layout.activeGroup).toBe('new-2');
  });
});

describe('arrange and arrange back (ADR-0018 §8)', () => {
  it('applies a proposal whole, says where the panels it did not place went, and keeps it for the tab', () => {
    const { id, command, notify, replaced, store } = page(['A', 'B', 'C']);
    const { n } = proposed(SIDE_BY_SIDE, [{ area: 'left', panel: id(1), title: 'B', shown: true }, { area: 'right', panel: id(2), title: 'C', shown: true }]);

    expect(execute(command('panel.arrange'), { proposal: String(n) })).toBeUndefined();

    const drawn = replaced.at(-1);
    expect(drawn).toBe(keptArrangement.getState().layout);
    expect(areasOf(drawn!).map((a) => [a.panels, a.shown])).toEqual([[[id(1), id(0)], id(1)], [[id(2)], id(2)]]);
    // A, the panel in use, is not shown by the proposal: the first area's panel is.
    expect(store.getState().activePanelId).toBe(id(1));
    expect(notify).toHaveBeenCalledWith('done', `Arranged as proposal ${n}: 2 areas. 1 panel the proposal did not place is in the area of B, the first in reading order. To put back what it replaced: arrange back.`);
  });

  // ADR-0006 note 9: a panel the user renames is called so now, so a proposal that placed it by its old name is stale.
  it('ADR-0006 note 9: refuses a proposal whose panel has been renamed since, and moves nothing', () => {
    const { id, command, replaced, store } = page(['A', 'B']);
    const { n } = proposed(SIDE_BY_SIDE, [{ area: 'left', panel: id(0), title: 'A', shown: true }, { area: 'right', panel: id(1), title: 'B', shown: true }]);
    store.getState().renamePanel(id(0), 'Hedge desk');
    expect(execute(command('panel.arrange'), { proposal: String(n) })).toBe(`Proposal ${n} places ${id(0)} as A, and it is Hedge desk now; nothing was moved.`);
    expect(replaced).toEqual([]);
  });

  it('refuses by name a proposal whose panel has closed or been retitled since, and moves nothing', () => {
    const { id, command, replaced, store } = page(['A', 'B', 'C']);
    const { n } = proposed(SIDE_BY_SIDE, [{ area: 'left', panel: id(0), title: 'A', shown: true }, { area: 'right', panel: id(1), title: 'B', shown: true }]);
    store.getState().openPanel({ kind: 'app', title: 'A', app: 'rates.workspace', params: { workspaceId: 'w9' } });
    const retitled = proposed(SIDE_BY_SIDE, [{ area: 'left', panel: id(0), title: 'A · 2', shown: true }, { area: 'right', panel: id(1), title: 'B', shown: true }]);
    store.getState().closePanel(id(1));

    expect(execute(command('panel.arrange'), { proposal: String(n) })).toBe(`Proposal ${n} places B, which has been closed since; nothing was moved.`);
    expect(execute(command('panel.arrange'), { proposal: String(retitled.n) })).toBe(`Proposal ${retitled.n} places ${id(0)} as A · 2, and it is A now; nothing was moved.`);
    expect(replaced).toEqual([]);
    expect(keptArrangement.getState().layout).toBeUndefined();
  });

  it('refuses a proposal the panels\' space is too small for, saying the room it needs', () => {
    const { id, command, store } = page(['A', 'B'], () => fakeAreas({ width: 900, height: 700 }, undefined).port);
    const { n } = proposed(SIDE_BY_SIDE, [{ area: 'left', panel: id(0), title: 'A', shown: true }, { area: 'right', panel: id(1), title: 'B', shown: true }], { width: 1280, height: 240 });

    expect(execute(command('panel.arrange'), { proposal: String(n) })).toBe(`Proposal ${n} needs a panels' space of 1280 by 240px, and it is 900 by 700px now; nothing was moved.`);
    expect(command('panel.arrange').args?.[0]?.check?.(String(n), {})).toBe(`Proposal ${n} needs a panels' space of 1280 by 240px, and it is 900 by 700px now; nothing was moved.`);
    expect(store.getState().panels).toHaveLength(2);
  });

  it('refuses a number no card shows, and a page the workbench does not open', () => {
    const { id, command } = page(['A']);
    const { n } = proposed([{ area: 'a' }], [{ area: 'a', panel: id(0), title: 'A', shown: true }, { area: 'a', app: 'nowhere' }]);

    expect(execute(command('panel.arrange'), { proposal: '999' })).toBe('There is no proposal 999: the AI\'s proposals are numbered on their cards.');
    expect(execute(command('panel.arrange'), { proposal: String(n) })).toMatch(new RegExp(`^Proposal ${n} opens nowhere, which is no page the workbench opens \\(.*\\); nothing was moved\\.$`));
  });

  it('opens the pages a proposal names that are not open, and `arrange back` closes them and puts back the arrangement before', async () => {
    registerPageOpener({ app: 'test.page', title: 'Test page', workspace: true, open: (w) => Promise.resolve({ kind: 'app', title: `Test ${w ?? ''}`, app: 'test.page', params: { workspaceId: w ?? '' } }) });
    const { id, command, notify, replaced, runs, titlesNow } = page(['A', 'B']);
    const before = keptArrangement.getState().layout;
    const { n } = proposed(SIDE_BY_SIDE, [
      { area: 'left', panel: id(0), title: 'A', shown: true },
      { area: 'right', run: 'run-7', shown: true },
      { area: 'right', app: 'test.page', workspace: 'ws-1' },
    ]);

    expect(execute(command('panel.arrange'), { proposal: String(n) })).toBeUndefined();
    await vi.waitFor(() => expect(notify).toHaveBeenCalled());

    expect(runs).toEqual(['run-7']);
    // Opened beside the panel in use, as any page is; the areas set the strip's order where they are drawn.
    expect(titlesNow()).toEqual(['A', 'Run of run-7', 'Test ws-1', 'B']);
    expect(notify).toHaveBeenLastCalledWith('done', `Arranged as proposal ${n}: 2 areas. Opened Run of run-7, Test ws-1. 1 panel the proposal did not place is in the area of A, the first in reading order. To put back what it replaced: arrange back.`);
    expect(areasOf(replaced.at(-1)!).map((a) => a.panels.length)).toEqual([2, 2]);

    expect(execute(command('panel.arrangeBack'))).toBeUndefined();
    expect(titlesNow()).toEqual(['A', 'B']);
    expect(notify).toHaveBeenLastCalledWith('done', `Put back the arrangement proposal ${n} replaced. Closed Run of run-7, Test ws-1, which it opened: reopen panel brings back the last.`);
    // What was there before was every panel in one area.
    expect(before).toBeUndefined();
    expect(areasOf(replaced.at(-1)!).map((a) => a.panels)).toEqual([[id(0), id(1)]]);
    expect(command('panel.arrangeBack').unavailable).toBe('No proposal has been applied since the page was loaded, or it has been put back already.');
  });

  it('keeps the proposal for the areas where the panels are one strip, and draws nothing there', () => {
    const strip = stripArrangement({ width: 1000, height: 700 }, 'The panels are one strip; the arrangement comes back with more room.');
    const { id, command, notify } = page(['A', 'B'], () => strip);
    const { n } = proposed(SIDE_BY_SIDE, [{ area: 'left', panel: id(0), title: 'A', shown: true }, { area: 'right', panel: id(1), title: 'B', shown: true }]);

    expect(execute(command('panel.arrange'), { proposal: String(n) })).toBeUndefined();

    expect(areasOf(keptArrangement.getState().layout!).map((a) => a.panels)).toEqual([[id(0)], [id(1)]]);
    expect(notify).toHaveBeenCalledWith('done', expect.stringMatching(new RegExp(`^Arranged as proposal ${n}: 2 areas\\.`)));
  });

  it('puts back, with the arrangement, the area each of its floating panels came from; a proposal\'s floating area came from none', () => {
    const { id, command } = page(['A', 'B', 'C']);
    const floating: SerializedDockview = {
      grid: {
        root: { type: 'branch', data: [{ type: 'leaf', size: 1440, data: { id: '1', views: [id(0), id(2)], activeView: id(0) } }] },
        width: 1440, height: 800, orientation: Orientation.HORIZONTAL,
      },
      panels: { [id(0)]: panelState(id(0), 'A'), [id(1)]: panelState(id(1), 'B'), [id(2)]: panelState(id(2), 'C') },
      floatingGroups: [{ data: { id: '2', views: [id(1)], activeView: id(1) }, position: { left: 100, top: 100, width: 400, height: 300 } }],
    };
    keptArrangement.getState().keep(floating);
    keptArrangement.getState().keepFloatedFrom({ [id(1)]: '1' });
    const { n } = proposed([{ area: 'main' }, { area: 'over', float: 'right' }], [
      { area: 'main', panel: id(0), title: 'A', shown: true },
      { area: 'over', panel: id(1), title: 'B', shown: true },
    ]);

    expect(execute(command('panel.arrange'), { proposal: String(n) })).toBeUndefined();
    expect(keptArrangement.getState().floatedFrom).toEqual({});

    expect(execute(command('panel.arrangeBack'))).toBeUndefined();
    expect(keptArrangement.getState().layout?.floatingGroups?.map((g) => g.data?.views)).toEqual([[id(1)]]);
    expect(keptArrangement.getState().floatedFrom).toEqual({ [id(1)]: '1' });
  });
});

describe('the context of the panels the AI reads (ADR-0018 §8)', () => {
  it('names each open panel by its id and title, what it shows, and the pages a proposal may open', () => {
    registerPageOpener({ app: 'test.page', title: 'Test page', workspace: true, open: () => Promise.reject(new Error('not opened here')) });
    const { store, id } = page(['A', 'B']);
    const value = JSON.parse(panelsContext(store.getState(), stripArrangement({ width: 390, height: 700 }, 'One strip below 768px.')).value) as Record<string, unknown>;

    expect(value.kind).toBe('qube.panels');
    expect(value.panels).toEqual([
      { panel: id(0), title: 'A', app: 'rates.workspace', workspace: 'w0', active: true },
      { panel: id(1), title: 'B', app: 'rates.workspace', workspace: 'w1' },
    ]);
    expect(value.strip).toBe('One strip below 768px.');
    expect(value.opens).toContainEqual({ app: 'test.page', title: 'Test page', workspace: true });
  });
});
