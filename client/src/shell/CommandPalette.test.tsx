import { buildCommands } from './CommandPalette';
import { catalog, view, workflow } from '../test/catalog';
import type { RunRecord } from '../platform/contracts';
import type { WorkspaceState } from '../platform/workspace/workspaceStore';
import { termRequests } from '../platform/traces/open';
import { registerCommandDoor } from '../platform/commands/served';
import { execute } from '../platform/commands/commands';
import { registerGridFocus, registerPlaces } from './places';
import { rememberKeyboard, returnKeyboard } from './keyboard';

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
    expect(of('xva.run.download').page).toBe(true);
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

    registerCommandDoor('p', { workspaceId: 'w', app: 'weather', settled: () => Promise.resolve(), view: () => undefined, enter: () => Promise.resolve(undefined), invoke: () => Promise.resolve(undefined) });
    try {
      const set = served(typed);
      expect([set.words, set.group, set.target, set.page, set.unavailable, set.served?.door.workspaceId]).toEqual(['set', 'Workspace', 'XVA ABC/LE-01', true, undefined, 'w']);
    } finally {
      registerCommandDoor('p', undefined);
    }
  });

  it('ADR-0016 note 33: the page\'s application\'s served commands lead, in the catalog\'s order; the client\'s own follow, then the kernel\'s', () => {
    const served = (capabilityId: string, words: string, app?: string) => ({
      capabilityId, version: '1.0.0', title: words, inputPorts: [], outputPorts: [], executionKind: 'requestResponse' as const, effects: ['editWorkspace'],
      command: { words: [words], arguments: [], ...(app === undefined ? {} : { app }) },
    });
    const typed = { ...catalog([], []), capabilities: [served('entity.set', 'set'), served('weather.price', 'price', 'weather'), served('weather.vary', 'vary', 'weather'), served('tide.ebb', 'ebb', 'tide')] };
    registerCommandDoor('p', { workspaceId: 'w', app: 'weather', settled: () => Promise.resolve(), view: () => undefined, enter: () => Promise.resolve(undefined), invoke: () => Promise.resolve(undefined) });
    try {
      const page = build(typed, s).filter((c) => c.page === true).map((c) => c.id);
      // Of the page's application first; another application's is no command here (note 27).
      expect(page.slice(0, 2)).toEqual(['weather.price', 'weather.vary']);
      expect(page.indexOf('xva.run.download')).toBeGreaterThan(1);
      expect(page.indexOf('entity.set')).toBeGreaterThan(page.indexOf('xva.run.download'));
      expect(page).not.toContain('tide.ebb');
    } finally {
      registerCommandDoor('p', undefined);
    }
  });

  it('ADR-0016 note 33: `price` on the pricer\'s page is the server\'s command alone; the client\'s is the Rates page\'s, and says where to price with neither open', () => {
    expect(commands.some((c) => c.id === 'workspace.price')).toBe(false);
    const rates = state({ panels: [{ id: 'r', kind: 'app', title: 'Rates 2026-10-26', app: 'rates.workspace', params: { workspaceId: 'w' } }], activePanelId: 'r', showApp: vi.fn(), openAppDialog: vi.fn() });
    const priced = (s: ReturnType<typeof state>) => build(catalog([], []), s).find((c) => c.words === 'price')!;
    expect([priced(rates).id, priced(rates).page, priced(rates).target]).toEqual(['workspace.price', true, 'Rates 2026-10-26']);
    expect(priced(state({ showApp: vi.fn(), openAppDialog: vi.fn() })).unavailable).toBe('Open an XVA or Rates workspace to price it.');
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

describe('places (ADR-0016 §9, note 20 of 2026-11-11)', () => {
  const s = state({ panels: [{ id: 'p', kind: 'app', title: 'Board 2026-11-11', app: 'xva.workspace', params: { workspaceId: 'w' } }], activePanelId: 'p', showApp: vi.fn(), openAppDialog: vi.fn() });
  const built = () => {
    const notify = vi.fn();
    const commands = buildCommands({ catalog: catalog([], []), state: s, activeRun: undefined, toggleTheme: vi.fn(), notify });
    return { notify, commands, go: commands.find((c) => c.id === 'panel.go')!, focusGrid: commands.find((c) => c.id === 'panel.focusGrid')! };
  };
  afterEach(() => registerPlaces('p', undefined));

  it('note 30: `open <tab>` is built from the tabs of the page in the active panel, a command each, and `go` offers its other places', () => {
    const went: string[] = [];
    registerPlaces('p', () => [
      { word: 'notes', title: 'the Notes tab', tab: true, go: () => { went.push('notes'); return undefined; } },
      { word: 'alarms', title: 'the Alarms tab', tab: true, go: () => 'The Alarms tab shows only while a station has one.' },
      { word: 'readings', title: 'Readings', grid: true, go: () => { went.push('readings'); return undefined; } },
    ]);
    const { go, notify, commands } = built();
    const opens = commands.filter((c) => c.id.startsWith('panel.open.'));
    const place = go.args![0]!;

    // A tab is the page's own command, listed first on the empty line as its other commands are.
    expect(opens.map((c) => [c.id, c.words, c.title, c.group, c.target, c.page])).toEqual([
      ['panel.open.notes', 'open notes', 'Open the Notes tab', 'Panels', 'Board 2026-11-11', true],
      ['panel.open.alarms', 'open alarms', 'Open the Alarms tab', 'Panels', 'Board 2026-11-11', true],
    ]);
    expect(place.complete!({})).toEqual([{ value: 'readings', detail: 'Readings' }]);
    expect(place.check!('notes', {})).toBe('one of readings');

    expect(execute(opens[0]!)).toBeUndefined();
    expect(went).toEqual(['notes']);
    expect(notify.mock.calls).toEqual([['done', 'The keyboard is in the Notes tab.']]);
    // A tab that does not show now is not opened: the line keeps the page's reason, and nothing more is said.
    expect(execute(opens[1]!)).toBe('The Alarms tab shows only while a station has one.');
    expect(notify.mock.calls).toHaveLength(1);
  });

  it('note 30: a page with no tab offers no `open` of the shell\'s', () => {
    registerPlaces('p', () => [{ word: 'readings', title: 'Readings', grid: true, go: () => undefined }]);
    expect(built().commands.filter((c) => c.id.startsWith('panel.open.'))).toEqual([]);
  });

  it('`go` offers the places of the page in the active panel, puts the keyboard in the one named and says where it went', () => {
    const went: string[] = [];
    registerPlaces('p', () => [
      { word: 'notes', title: 'the notes', go: () => { went.push('notes'); return undefined; } },
      { word: 'readings', title: 'Readings', grid: true, go: () => { went.push('readings'); return undefined; } },
    ]);
    const { go, notify } = built();
    const place = go.args![0]!;

    expect([go.words, go.group, go.target, go.unavailable]).toEqual(['go', 'Panels', 'Board 2026-11-11', undefined]);
    expect(place.complete!({})).toEqual([{ value: 'notes', detail: 'the notes' }, { value: 'readings', detail: 'Readings' }]);
    // A word that names no place is refused with the places there are.
    expect(place.check!('charts', {})).toBe('one of notes, readings');
    expect(place.check!('Readings', {})).toBeUndefined();

    expect(execute(go, { place: 'Readings' })).toBeUndefined();
    expect(went).toEqual(['readings']);
    expect(notify.mock.calls).toEqual([['done', 'The keyboard is in Readings.']]);
  });

  it('a place that cannot take the keyboard now says why: nothing is said to have moved, and the line keeps the reason', () => {
    registerPlaces('p', () => [{ word: 'results', title: 'the results', go: () => 'Nothing is priced yet: Price shows its results here.' }]);
    const { go, notify } = built();

    expect(execute(go, { place: 'results' })).toBe('Nothing is priced yet: Price shows its results here.');
    expect(notify).not.toHaveBeenCalled();
  });

  it('note 22: once `go` has put the keyboard in a place the line has none to give back, and a place that refuses leaves it the line\'s to return', () => {
    const before = document.createElement('button');
    const line = document.createElement('input');
    document.body.append(before, line);
    try {
      // Taken: the place draws later than the line closes, and the line must not put the keyboard back meanwhile.
      before.focus();
      rememberKeyboard();
      line.focus();
      registerPlaces('p', () => [{ word: 'readings', title: 'Readings', grid: true, go: () => undefined }]);
      expect(execute(built().go, { place: 'readings' })).toBeUndefined();
      returnKeyboard();
      expect(document.activeElement).toBe(line);

      // Refused: nothing moved, so the keyboard is where it was once the line closes.
      before.focus();
      rememberKeyboard();
      line.focus();
      registerPlaces('p', () => [{ word: 'readings', title: 'Readings', grid: true, go: () => 'Not now.' }]);
      expect(execute(built().go, { place: 'readings' })).toBe('Not now.');
      returnKeyboard();
      expect(document.activeElement).toBe(before);
    } finally {
      before.remove();
      line.remove();
    }
  });

  it('`focus grid` is `go` to the first place that is a grid, with the same notice', () => {
    const went: string[] = [];
    registerPlaces('p', () => [
      { word: 'notes', title: 'the Notes tab', tab: true, go: () => { went.push('notes'); return undefined; } },
      { word: 'readings', title: 'Readings', grid: true, go: () => { went.push('readings'); return undefined; } },
      { word: 'stations', title: 'Stations', grid: true, go: () => { went.push('stations'); return undefined; } },
    ]);
    const { focusGrid, notify } = built();

    expect([focusGrid.words, focusGrid.shortcut]).toEqual(['focus grid', 'mod+shift+G']);
    expect(execute(focusGrid)).toBeUndefined();
    expect(went).toEqual(['readings']);
    expect(notify.mock.calls).toEqual([['done', 'The keyboard is in Readings.']]);
  });

  it('a page of one grid has the one place `grid`, and a page with no places says so', () => {
    const none = built();
    expect(none.go.unavailable).toBe('This page has no places to go to.');
    expect(execute(none.focusGrid)).toBe('This panel has no grid yet.');

    const focus = vi.fn();
    registerGridFocus('p', focus);
    const one = built();
    expect(one.go.args![0]!.complete!({})).toEqual([{ value: 'grid', detail: 'the grid' }]);
    expect(execute(one.go, { place: 'grid' })).toBeUndefined();
    expect(execute(one.focusGrid)).toBeUndefined();
    expect(focus).toHaveBeenCalledTimes(2);
    expect(one.notify.mock.calls).toEqual([['done', 'The keyboard is in the grid.'], ['done', 'The keyboard is in the grid.']]);
  });

  it('the places are read when a command runs: a place that came after the line was built is gone to', () => {
    let widened = false;
    const went: string[] = [];
    registerPlaces('p', () => [
      ...(widened ? [{ word: 'trace', title: 'the trace', grid: true, go: () => { went.push('trace'); return undefined; } }] : []),
      { word: 'readings', title: 'Readings', grid: true, go: () => { went.push('readings'); return undefined; } },
    ]);
    const { focusGrid } = built();
    widened = true;

    expect(execute(focusGrid)).toBeUndefined();
    expect(went).toEqual(['trace']);
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
