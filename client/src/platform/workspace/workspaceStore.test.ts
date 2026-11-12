import { CLOSED_KEPT, createWorkspaceStore, panelLabel } from './workspaceStore';
import { contextContract } from '../../test/catalog';

const valid = { region: 'North', asOf: '2026-10-26' };

describe('workspace store', () => {
  it('keeps the last valid context prepared while the requested one is invalid', () => {
    const store = createWorkspaceStore(valid, contextContract);
    store.getState().requestContext(contextContract, { asOf: '2026-13-01' });
    expect(store.getState().context.prepared).toEqual(valid);
    expect(store.getState().context.issues.map((i) => i.field)).toEqual(['asOf']);
    store.getState().requestContext(contextContract, { asOf: '2026-10-27', region: 'South' });
    expect(store.getState().context.prepared).toEqual({ region: 'South', asOf: '2026-10-27' });
  });

  // ADR-0006 note 7: closing the active panel shows the one shown before it, as a browser's or an editor's tab does.
  it('shows the panel shown last when the active panel closes', () => {
    const store = createWorkspaceStore(valid, contextContract);
    const { openPanel } = store.getState();
    const a = openPanel({ kind: 'run', title: 'A', runId: 'r1', viewKey: undefined });
    const b = openPanel({ kind: 'run', title: 'B', runId: 'r2', viewKey: undefined });
    const c = openPanel({ kind: 'run', title: 'C', runId: 'r3', viewKey: undefined });
    store.getState().activate(a);
    store.getState().activate(b);
    store.getState().closePanel(b);
    expect(store.getState().activePanelId).toBe(a);
    store.getState().closePanel(a);
    expect(store.getState().activePanelId).toBe(c);
  });

  // ADR-0006 note 7: a panel opens beside the one it was opened from, and a Run that is open is shown, not opened again.
  it('opens a panel next to the active one, and shows a Run that is open in its panel', () => {
    const store = createWorkspaceStore(valid, contextContract);
    const { openPanel } = store.getState();
    const a = openPanel({ kind: 'run', title: 'A', runId: 'r1', viewKey: undefined });
    const b = openPanel({ kind: 'run', title: 'B', runId: 'r2', viewKey: undefined });
    store.getState().activate(a);
    const c = openPanel({ kind: 'run', title: 'C', runId: 'r3', viewKey: undefined });
    expect(store.getState().panels.map((p) => p.id)).toEqual([a, c, b]);
    expect(openPanel({ kind: 'run', title: 'B again', runId: 'r2', viewKey: 'v@1' })).toBe(b);
    expect(store.getState().panels).toHaveLength(3);
    expect(store.getState().activePanelId).toBe(b);
    expect(store.getState().panels.find((p) => p.id === b)).toMatchObject({ title: 'B', viewKey: 'v@1' });
  });

  // ADR-0006 note 7: two panels of one title are told apart by a number each keeps while it is open.
  it('numbers the panels of one title from 2, and gives a freed number to the next', () => {
    const store = createWorkspaceStore(valid, contextContract);
    const open = () => store.getState().openPanel({ kind: 'app', title: 'Rates 2026-10-27', app: 'rates', params: { workspaceId: crypto.randomUUID() } });
    const [first, second, third] = [open(), open(), open()];
    const labels = () => store.getState().panels.map(panelLabel);
    expect(labels()).toEqual(['Rates 2026-10-27', 'Rates 2026-10-27 · 2', 'Rates 2026-10-27 · 3']);
    store.getState().closePanel(second);
    open();
    expect(labels()).toEqual(['Rates 2026-10-27', 'Rates 2026-10-27 · 3', 'Rates 2026-10-27 · 2']);
    store.getState().closePanel(first);
    expect(store.getState().panels.find((p) => p.id === third)?.copy).toBe(3);
    // Reopened, a panel has the number it had, where that is free.
    store.getState().closePanel(third);
    store.getState().reopenPanel();
    expect(labels()).toEqual(['Rates 2026-10-27 · 3', 'Rates 2026-10-27 · 2']);
  });

  // ADR-0006 note 7: the closes of several leave the pinned panels open, and say how many they took and kept.
  it('closes the others, those to the right or all, leaving the pinned, and shows the panel shown last', () => {
    const store = createWorkspaceStore(valid, contextContract);
    const ids = ['A', 'B', 'C', 'D', 'E'].map((t, i) => store.getState().openPanel({ kind: 'run', title: t, runId: `r${i}`, viewKey: undefined }));
    const [a, b, c, d, e] = ids as [string, string, string, string, string];
    store.getState().pinPanel(d, true);
    expect(store.getState().panels.map((p) => p.title)).toEqual(['D', 'A', 'B', 'C', 'E']);
    expect(store.getState().closePanels('right', b)).toEqual({ closed: 2, pinned: 0 });
    expect(store.getState().panels.map((p) => p.id)).toEqual([d, a, b]);
    expect(store.getState().activePanelId).toBe(d);
    expect(store.getState().closePanels('others', a)).toEqual({ closed: 1, pinned: 1 });
    expect(store.getState().panels.map((p) => p.id)).toEqual([d, a]);
    expect(store.getState().closePanels('all', a)).toEqual({ closed: 1, pinned: 1 });
    expect(store.getState().panels.map((p) => p.id)).toEqual([d]);
    expect([c, e]).not.toContain(store.getState().activePanelId);
  });

  // ADR-0006 note 7: a closed panel comes back where it stood, pinned if it was.
  it('reopens the panels closed, the last first, where they stood', () => {
    const store = createWorkspaceStore(valid, contextContract);
    const [a, b, c] = ['A', 'B', 'C'].map((t, i) => store.getState().openPanel({ kind: 'run', title: t, runId: `r${i}`, viewKey: undefined })) as [string, string, string];
    store.getState().closePanel(b);
    store.getState().closePanel(a);
    expect(store.getState().closed.map((p) => p.page.title)).toEqual(['B', 'A']);
    const again = store.getState().reopenPanel()?.id;
    expect(store.getState().panels.map((p) => p.title)).toEqual(['A', 'C']);
    expect(store.getState().activePanelId).toBe(again);
    const back = store.getState().reopenPanel()!.id;
    expect(store.getState().panels.map((p) => p.title)).toEqual(['A', 'B', 'C']);
    expect(store.getState().reopenPanel()?.id).toBeUndefined();
    expect(c).not.toBe(back);
  });

  // ADR-0006 note 7: a page is not opened twice, by `reopen panel` either.
  it('shows a page opened again since it closed, rather than reopen a second panel of it', () => {
    const store = createWorkspaceStore(valid, contextContract);
    const run = store.getState().openPanel({ kind: 'run', title: 'A', runId: 'r1', viewKey: undefined });
    store.getState().closePanel(run);
    const again = store.getState().openPanel({ kind: 'run', title: 'A', runId: 'r1', viewKey: undefined });
    const db = store.getState().showApp('pricing-db', 'Pricing DB', {});
    store.getState().closePanel(db);
    store.getState().showApp('pricing-db', 'Pricing DB', {});
    expect(store.getState().closed).toHaveLength(2);
    expect(store.getState().reopenPanel()?.id).not.toBe(db);
    expect(store.getState().panels.map(panelLabel)).toEqual(['A', 'Pricing DB']);
    expect(store.getState().reopenPanel()?.id).toBe(again);
    expect(store.getState().activePanelId).toBe(again);
    expect(store.getState().panels.map(panelLabel)).toEqual(['A', 'Pricing DB']);
    expect(store.getState().closed).toHaveLength(0);
  });

  // ADR-0006 note 7: a close of several comes back from its left, each where it stood, so the strip is as it was.
  it('reopens a close of several in the order the panels stood', () => {
    const store = createWorkspaceStore(valid, contextContract);
    ['A', 'B', 'C', 'D', 'E'].forEach((t, i) => store.getState().openPanel({ kind: 'run', title: t, runId: `r${i}`, viewKey: undefined }));
    const order = () => store.getState().panels.map((p) => p.title).join('');
    const idOf = (title: string) => store.getState().panels.find((p) => p.title === title)!.id;
    store.getState().closePanels('others', idOf('A'));
    for (let i = 0; i < 4; i++) store.getState().reopenPanel();
    expect(order()).toBe('ABCDE');
    store.getState().closePanels('right', idOf('C'));
    store.getState().reopenPanel();
    expect(order()).toBe('ABCD');
    store.getState().reopenPanel();
    expect(order()).toBe('ABCDE');
  });

  // The owner's rule of 2026-11-11: no answer stands in for a panel that is not there.
  it('refuses to close, move, pin or show a panel that is not open', () => {
    const store = createWorkspaceStore(valid, contextContract);
    expect(() => store.getState().closePanels('others', 'panel-0')).toThrow('No open panel is panel-0.');
    expect(() => store.getState().closePanel('panel-0')).toThrow('No open panel is panel-0.');
    expect(() => store.getState().movePanel('panel-0', 0)).toThrow('No open panel is panel-0.');
    expect(() => store.getState().pinPanel('panel-0', true)).toThrow('No open panel is panel-0.');
    expect(() => store.getState().activate('panel-0')).toThrow('No open panel is panel-0.');
    expect(() => store.getState().setPanelView('panel-0', 'table')).toThrow('No open panel is panel-0.');
  });

  it('keeps no more closed panels than it can reopen', () => {
    const store = createWorkspaceStore(valid, contextContract);
    for (let i = 0; i < CLOSED_KEPT + 5; i++) store.getState().closePanel(store.getState().openPanel({ kind: 'run', title: `R${i}`, runId: `r${i}`, viewKey: undefined }));
    expect(store.getState().closed).toHaveLength(CLOSED_KEPT);
    expect(store.getState().closed.at(-1)?.page.title).toBe(`R${CLOSED_KEPT + 4}`);
  });

  // ADR-0006 note 7: a move keeps the pinned first; a pinned panel moves among the pinned, the others among the others.
  it('moves a panel within the pinned or the others, and pins at the end of the pinned', () => {
    const store = createWorkspaceStore(valid, contextContract);
    const [a, b, c] = ['A', 'B', 'C'].map((t, i) => store.getState().openPanel({ kind: 'run', title: t, runId: `r${i}`, viewKey: undefined })) as [string, string, string];
    const order = () => store.getState().panels.map((p) => p.title).join('');
    store.getState().movePanel(c, 0);
    expect(order()).toBe('CAB');
    store.getState().pinPanel(b, true);
    expect(order()).toBe('BCA');
    store.getState().movePanel(a, 0);
    expect(order()).toBe('BAC');
    store.getState().movePanel(b, 2);
    expect(order()).toBe('BAC');
    store.getState().pinPanel(b, false);
    expect(order()).toBe('BAC');
    expect(store.getState().panels.some((p) => p.pinned === true)).toBe(false);
    store.getState().movePanel(b, 2);
    expect(order()).toBe('ACB');
  });

  // ADR-0018 §4: closing the active panel shows what its area showed before; an area left empty gives way to the panel
  // shown before it anywhere. A closed panel is kept with its area, which `reopen panel` puts it back in.
  it('ADR-0018 §4: follows the active panel within its area as it closes, and keeps the area of a panel closed', () => {
    const store = createWorkspaceStore(valid, contextContract);
    const [a, b, c] = ['A', 'B', 'C'].map((t, i) => store.getState().openPanel({ kind: 'run', title: t, runId: `r${i}`, viewKey: undefined })) as [string, string, string];
    const area: Record<string, string> = { [a]: 'left', [b]: 'left', [c]: 'right' };
    const areaOf = (id: string) => area[id];
    store.getState().activate(b);
    store.getState().activate(c);
    store.getState().activate(a);
    store.getState().closePanel(a, areaOf);
    expect(store.getState().activePanelId).toBe(b);
    expect(store.getState().closed.at(-1)).toMatchObject({ area: 'left' });
    store.getState().closePanel(b, areaOf);
    expect(store.getState().activePanelId).toBe(c);
    // Without areas, the panel shown last anywhere follows, as note 7 has it.
    const strip = createWorkspaceStore(valid, contextContract);
    const [x, y, z] = ['X', 'Y', 'Z'].map((t, i) => strip.getState().openPanel({ kind: 'run', title: t, runId: `s${i}`, viewKey: undefined })) as [string, string, string];
    strip.getState().activate(y);
    strip.getState().activate(x);
    strip.getState().closePanel(x);
    expect(strip.getState().activePanelId).toBe(y);
    expect(strip.getState().closed.at(-1)?.area).toBeUndefined();
    expect(z).toBeDefined();
  });

  // ADR-0018 §1: a close of several within an area names the panels of that area only.
  it('ADR-0018 §1: closes the others of an area, the pinned kept', () => {
    const store = createWorkspaceStore(valid, contextContract);
    const [a, b, c, d] = ['A', 'B', 'C', 'D'].map((t, i) => store.getState().openPanel({ kind: 'run', title: t, runId: `r${i}`, viewKey: undefined })) as [string, string, string, string];
    store.getState().pinPanel(b, true);
    expect(store.getState().closePanels('others', c, [a, b, c])).toEqual({ closed: 1, pinned: 1 });
    expect(store.getState().panels.map((p) => p.title)).toEqual(['B', 'C', 'D']);
    expect(d).toBeDefined();
  });

  // ADR-0018 §5: while areas are shown, the strip's order is set from their reading order, the pinned first.
  it('ADR-0018 §5: takes an order naming every open panel once, and refuses another', () => {
    const store = createWorkspaceStore(valid, contextContract);
    const [a, b, c] = ['A', 'B', 'C'].map((t, i) => store.getState().openPanel({ kind: 'run', title: t, runId: `r${i}`, viewKey: undefined })) as [string, string, string];
    store.getState().setOrder([c, a, b]);
    expect(store.getState().panels.map((p) => p.title)).toEqual(['C', 'A', 'B']);
    expect(() => store.getState().setOrder([c, a])).toThrow();
    expect(() => store.getState().setOrder([c, a, a])).toThrow();
  });

  // ADR-0018 §7: what a browser tab kept comes back as it was, and a panel opened after it takes a new id.
  it('ADR-0018 §7: takes up the panels a browser tab kept, and numbers the next after them', () => {
    const store = createWorkspaceStore(valid, contextContract);
    store.getState().restore({
      panels: [{ id: 'panel-4', kind: 'run', title: 'A', runId: 'r1', viewKey: undefined }, { id: 'panel-7', kind: 'run', title: 'B', runId: 'r2', viewKey: undefined, pinned: true }],
      activePanelId: 'panel-7',
      recent: ['panel-7', 'panel-gone', 'panel-4'],
      closed: [],
    });
    expect(store.getState().activePanelId).toBe('panel-7');
    expect(store.getState().recent).toEqual(['panel-7', 'panel-4']);
    expect(store.getState().openPanel({ kind: 'run', title: 'C', runId: 'r3', viewKey: undefined })).toBe('panel-8');
    expect(() => store.getState().restore({ panels: [], activePanelId: undefined, recent: [], closed: [] })).toThrow();
  });
});
