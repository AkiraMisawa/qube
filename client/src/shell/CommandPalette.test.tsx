import { buildCommands } from './CommandPalette';
import { catalog, view, workflow } from '../test/catalog';
import type { RunRecord } from '../platform/contracts';
import type { WorkspaceState } from '../platform/workspace/workspaceStore';
import { termRequests } from '../platform/traces/open';

const state = (overrides: Partial<WorkspaceState> = {}) => ({
  panels: [],
  activePanelId: undefined,
  context: { requested: {}, prepared: { region: 'North', asOf: '2026-10-26' }, issues: [] },
  openRunDialog: vi.fn(),
  setPanelView: vi.fn(),
  closePanel: vi.fn(),
  cycle: vi.fn(),
  activate: vi.fn(),
  requestContext: vi.fn(),
  ...overrides,
}) as Parameters<typeof buildCommands>[0]['state'];

const build = (cat: ReturnType<typeof catalog>, s = state(), activeRun?: RunRecord) =>
  buildCommands({ catalog: cat, state: s, activeRun, toggleTheme: vi.fn(), notify: vi.fn() });

describe('palette commands', () => {
  it('CO01: a workflow added to the catalog gets a run command with no client change', () => {
    const titles = build(catalog([workflow('w.a', 'Station summary', ['s']), workflow('w.new', 'Rates curve check', ['c'])], [])).map((c) => c.title);
    expect(titles).toContain('Run Rates curve check');
  });

  it('makes run commands unavailable while the context is not valid', () => {
    const s = state({ context: { requested: {}, prepared: undefined, issues: [] } });
    const run = build(catalog([workflow('w.a', 'A', ['s'])], []), s).find((c) => c.title === 'Run A')!;
    expect(run.unavailable).toBe('The context is not valid.');
  });

  it('offers the views of the active Run only once it has finished', () => {
    const w = workflow('w.a', 'A', ['summary']);
    const cat = catalog([w], [view('v.1', 'Summary', ['summary']), view('v.2', 'Other', ['other'])]);
    const s = state({ panels: [{ id: 'p', kind: 'run', title: 'A', runId: 'r', viewKey: undefined }], activePanelId: 'p' });
    const record = (status: RunRecord['status']) => ({ runId: 'r', workflow: w, status }) as RunRecord;
    expect(build(cat, s, record('running')).filter((c) => c.group === 'Views')).toEqual([]);
    expect(build(cat, s, record('partiallySucceeded')).filter((c) => c.group === 'Views').map((c) => c.title)).toEqual(['Show as Summary']);
  });

  it('ADR-0011 §7: `explain` is offered on a trace, and shows the term named in its panel or another measure in its own', () => {
    const params = { runId: 'run-1', case: 'base', scenario: 'base', measure: 'ccr.spotCapital' };
    const showApp = vi.fn(() => 'p2');
    const s = state({ panels: [{ id: 'p', kind: 'app', title: 'Trace', app: 'trace.view', params }], activePanelId: 'p', showApp, openAppDialog: vi.fn() });
    const explain = build(catalog([], []), s).find((c) => c.id === 'trace.explain')!;

    expect(explain.unavailable).toBeUndefined();
    explain.run({ measure: 'ccr.spotCapital NS-001 CSA-A/m' });
    expect(termRequests.getState().requests.p?.path).toBe('NS-001 CSA-A/m');
    expect(showApp).not.toHaveBeenCalled();

    explain.run({ measure: 'simm.spotIm' });
    expect(showApp).toHaveBeenCalledWith('trace.view', 'Trace simm.spotIm', { ...params, measure: 'simm.spotIm' });
    expect(termRequests.getState().requests.p2?.path).toBeUndefined();
  });

  it('ADR-0011 §7: `explain` says why it cannot run where nothing is open to explain', () => {
    const explain = build(catalog([], []), state({ showApp: vi.fn(), openAppDialog: vi.fn() })).find((c) => c.id === 'trace.explain')!;
    expect(explain.unavailable).toBe('Open an XVA workspace that has been priced, or a trace, first.');
  });
});

// ADR-0006: `show panel` finds an open panel by typing part of its title, and names the candidates rather than guess.
describe('show panel', () => {
  const panels = [
    { id: 'a', kind: 'run', title: 'XVA XYZ/LE-03', runId: 'r1', viewKey: undefined },
    { id: 'b', kind: 'run', title: 'CVA/FVA XYZ/LE-03', runId: 'r2', viewKey: undefined },
    { id: 'c', kind: 'run', title: 'Pricing DB', runId: 'r3', viewKey: undefined },
  ] as const;

  it('shows the panel a title or the only title containing the text names', () => {
    const activate = vi.fn();
    const show = build(catalog([], []), state({ panels: [...panels], activePanelId: 'a', activate })).find((c) => c.id === 'panel.show')!;
    show.run({ panel: 'cva' });
    show.run({ panel: 'pricing db' });
    expect(activate.mock.calls).toEqual([['b'], ['c']]);
  });

  it('names the candidates back when the text fits several, and runs nothing', () => {
    const show = build(catalog([], []), state({ panels: [...panels], activePanelId: 'a' })).find((c) => c.id === 'panel.show')!;
    expect(show.args![0]!.check!('xyz', {})).toBe('2 panels match "xyz": XVA XYZ/LE-03, CVA/FVA XYZ/LE-03.');
    expect(show.args![0]!.check!('rates', {})).toBe('No open panel is titled like "rates".');
  });
});
