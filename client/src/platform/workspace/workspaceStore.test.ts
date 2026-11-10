import { createWorkspaceStore } from './workspaceStore';
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

  it('activates the neighbour when the active panel closes, and cycles around the ends', () => {
    const store = createWorkspaceStore(valid, contextContract);
    const { openPanel } = store.getState();
    const a = openPanel({ kind: 'run', title: 'A', runId: 'r1', viewKey: undefined });
    const b = openPanel({ kind: 'run', title: 'B', runId: 'r2', viewKey: undefined });
    const c = openPanel({ kind: 'run', title: 'C', runId: 'r3', viewKey: undefined });
    store.getState().cycle(1);
    expect(store.getState().activePanelId).toBe(a);
    store.getState().activate(b);
    store.getState().closePanel(b);
    expect(store.getState().activePanelId).toBe(c);
    store.getState().closePanel(c);
    expect(store.getState().activePanelId).toBe(a);
  });

  // ExGrid ADR-0016: the grid reports a drag and the page keeps it; a panel's page unmounts when another tab shows.
  it('keeps the widths dragged in a panel while it is open, and forgets them when it closes', () => {
    const store = createWorkspaceStore(valid, contextContract);
    const a = store.getState().openPanel({ kind: 'run', title: 'A', runId: 'r1', viewKey: undefined });
    const b = store.getState().openPanel({ kind: 'run', title: 'B', runId: 'r2', viewKey: undefined });
    store.getState().setColumnWidth(a, 'lines', 'pv', 180);
    store.getState().setColumnWidth(a, 'lines', 'end', 90);
    store.getState().setColumnWidth(b, 'lines', 'pv', 60);
    expect(store.getState().columnWidths[a]).toEqual({ lines: { pv: 180, end: 90 } });
    store.getState().closePanel(a);
    expect(store.getState().columnWidths[a]).toBeUndefined();
    expect(store.getState().columnWidths[b]).toEqual({ lines: { pv: 60 } });
  });
});
