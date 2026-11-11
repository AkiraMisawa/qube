import { buildCommands } from './CommandPalette';
import { catalog, view, workflow } from '../test/catalog';
import type { RunRecord } from '../platform/contracts';
import type { WorkspaceState } from '../platform/workspace/workspaceStore';
import { termRequests } from '../platform/traces/open';
import { registerCommandDoor } from '../platform/commands/served';

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

describe('the page\'s commands (ADR-0006 note 3 of 2026-11-11)', () => {
  const s = state({
    panels: [{ id: 'p', kind: 'app', title: 'XVA ABC/LE-01', app: 'xva.workspace', params: { workspaceId: 'w' } }], activePanelId: 'p',
    showApp: vi.fn(), openAppDialog: vi.fn(),
  });
  const commands = build(catalog([workflow('w.a', 'Station summary', ['s'])], []), s);
  const of = (id: string) => commands.find((c) => c.id === id)!;

  it('a command says that it acts on the page in the active panel, and only such a command does', () => {
    expect(of('xva.vary').page).toBe(true);
    expect(of('workspace.price').page).toBe(true);
    expect(of('trace.explain').page).toBe(true);
    // Not of the page: one that opens another, one of another application, and the shell's own on the active panel.
    expect(of('xva.new').page).toBeUndefined();
    expect(of('rates.capture').page).toBeUndefined();
    expect(of('panel.close').page).toBeUndefined();
  });

  it('ADR-0016 §4: a command the catalog says how to type is the page\'s where the page gives it a workspace, and says why not elsewhere', () => {
    const typed = { ...catalog([], []), capabilities: [{
      capabilityId: 'entity.set', version: '1.0.0', title: 'Set fields of rows', inputPorts: [], outputPorts: [], executionKind: 'requestResponse' as const, effects: ['editWorkspace'],
      command: { words: ['set'], arguments: [{ name: 'rows', kind: 'rows' as const, required: false }, { name: 'set', kind: 'assignments' as const, required: true }] },
    }] };
    const served = (cat: typeof typed) => build(cat, s).find((c) => c.id === 'entity.set')!;

    expect(served(typed).unavailable).toBe('This panel has no workspace for it to act on.');

    registerCommandDoor('p', { workspaceId: 'w', enter: () => Promise.resolve(undefined) });
    try {
      const set = served(typed);
      expect([set.words, set.group, set.target, set.page, set.unavailable, set.served?.workspaceId]).toEqual(['set', 'Workspace', 'XVA ABC/LE-01', true, undefined, 'w']);
    } finally {
      registerCommandDoor('p', undefined);
    }
  });

  it('a finished Run\'s views are its page\'s commands', () => {
    const w = workflow('w.a', 'A', ['summary']);
    const run = state({ panels: [{ id: 'p', kind: 'run', title: 'A', runId: 'r', viewKey: undefined }], activePanelId: 'p', showApp: vi.fn(), openAppDialog: vi.fn() });
    const shown = build(catalog([w], [view('v.1', 'Summary', ['summary'])]), run, { runId: 'r', workflow: w, status: 'succeeded' } as RunRecord)
      .find((c) => c.group === 'Views')!;
    expect([shown.words, shown.target, shown.page]).toEqual(['show summary', 'A', true]);
  });

  it('the applications\' commands are registered before the catalog\'s and the shell\'s, so their groups are listed first', () => {
    const at = (id: string) => commands.findIndex((c) => c.id === id);
    expect(at('xva.new')).toBeLessThan(commands.findIndex((c) => c.group === 'Workflows'));
    expect(at('history.open')).toBeLessThan(at('panel.close'));
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
