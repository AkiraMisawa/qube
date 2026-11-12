import { adoptUsage, merged, recordUse, resetUsage, usageScore } from '../commands/usage';
import { givenSettings, heardChange, setSetting, setWritten, settingsStore, valueOf, type SettingDescribed } from './settings';
import { THEME, themeManager } from './theme';

// ADR-0017 §5: the page's own writes to the user's settings, the theme's copy for the first frame, and the command
// ranking carried over once from a browser that kept it.

const theme = (value: string, set = true, unreadable?: string): SettingDescribed => ({
  key: THEME, name: 'theme', title: 'Theme', words: ['theme'], kind: 'preference', takes: 'one of dark, light, auto', typed: true,
  choices: ['dark', 'light', 'auto'], value, registered: 'dark', set, ...(unreadable === undefined ? {} : { stored: 'pink', unreadable }),
});

const usage = (value: unknown, unreadable?: string): SettingDescribed => ({
  key: 'commands.usage', name: 'command ranking', title: 'Command ranking', words: ['command ranking'], kind: 'preference',
  takes: 'a value its page writes, as JSON', typed: false, value: value === undefined ? 'none' : JSON.stringify(value), set: value !== undefined,
  ...(unreadable === undefined ? {} : { stored: '[]', unreadable }),
});

const widths = (grid: string): SettingDescribed => ({
  key: `grid.widths.${grid}`, name: `widths ${grid}`, title: 'Column widths', words: ['widths'], kind: 'preference',
  takes: 'a value its page writes, as JSON', typed: false, value: '{}', set: true,
});

interface Sent { readonly capability: string; readonly request: Readonly<Record<string, string>> }

/**
 * The server: each set and reset is kept as sent and answered, each read answers the setting as last kept. With
 * `held`, a set waits to be answered until `answer()` lets the oldest one through, as a slow server answers.
 */
function server(options: { readonly held?: boolean; readonly refuse?: boolean } = {}) {
  const sent: Sent[] = [];
  const waiting: (() => void)[] = [];
  const kept = new Map<string, SettingDescribed>(Object.entries(settingsStore.getState().settings));
  const answered = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status });
  vi.stubGlobal('fetch', vi.fn(async (path: string, init?: RequestInit) => {
    if (path === '/api/settings') return answered({ settings: [...kept.values()] });
    const capability = /\/api\/commands\/([^/]+)\//.exec(path)?.[1] ?? '';
    const request = (JSON.parse(String(init?.body)) as { inputs: { request: Record<string, string> } }).inputs.request;
    const named = request.setting ?? '';
    const key = [...kept.values()].find((k) => k.name === named)?.key ?? named;
    if (capability === 'settings.read') {
      const one = kept.get(key) ?? widths(key.replace('grid.widths.', ''));
      return answered({ capabilityId: capability, version: '1.0.0', outputs: { answer: { settings: [one] } } });
    }
    sent.push({ capability, request });
    if (options.held === true) await new Promise<void>((resolve) => waiting.push(resolve));
    if (options.refuse === true) return answered({ code: 'InvalidValue', message: 'Column widths is an object.', issues: [] }, 422);
    const before = kept.get(key);
    const value = capability === 'settings.reset' ? (before?.registered ?? 'none') : (request.value ?? '');
    const set = capability === 'settings.set';
    if (before !== undefined) {
      const { stored: _stored, unreadable: _unreadable, ...rest } = before;
      kept.set(key, { ...rest, value, set });
    }
    else if (set) kept.set(key, { ...widths(key.replace('grid.widths.', '')), value });
    return answered({ capabilityId: capability, version: '1.0.0', outputs: { answer: {
      key, name: key, title: key, value, set, replaced: '', wasSet: false, said: `${key} set.`,
    } } });
  }));
  return {
    sent,
    /** Lets the oldest set still waiting be answered. */
    answer: () => waiting.shift()?.(),
    waiting: () => waiting.length,
  };
}

beforeEach(() => {
  localStorage.clear();
  resetUsage();
  settingsStore.setState({ loaded: false, settings: {}, writing: {}, notice: undefined });
});

afterEach(() => vi.unstubAllGlobals());

describe("the page's writes to a setting (ADR-0017 §5)", () => {
  it('applies a value at once and sends the writes in the order they were made', async () => {
    givenSettings([]);
    const { sent } = server();

    const first = setWritten('grid.widths.xva/csas', 'Column widths', { name: 120 });
    const second = setWritten('grid.widths.xva/csas', 'Column widths', { name: 120, threshold: 90 });
    expect(JSON.parse(valueOf(settingsStore.getState(), 'grid.widths.xva/csas') ?? '')).toEqual({ name: 120, threshold: 90 });
    await Promise.all([first, second]);

    expect(sent.map((s) => JSON.parse(s.request.value ?? '') as unknown)).toEqual([{ name: 120 }, { name: 120, threshold: 90 }]);
  });

  it("keeps the page's newest value while an earlier write is answered, and the server's once the last one is", async () => {
    givenSettings([widths('rates/lines')]);
    const { answer, waiting } = server({ held: true });

    const first = setWritten('grid.widths.rates/lines', 'Column widths', { end: 90 });
    const second = setWritten('grid.widths.rates/lines', 'Column widths', { end: 90, index: 70 });
    await vi.waitFor(() => expect(waiting()).toBe(1));
    answer();
    await first;
    expect(JSON.parse(valueOf(settingsStore.getState(), 'grid.widths.rates/lines') ?? '')).toEqual({ end: 90, index: 70 });

    await vi.waitFor(() => expect(waiting()).toBe(1));
    answer();
    await second;
    expect(settingsStore.getState().writing).toEqual({});
    expect(JSON.parse(settingsStore.getState().settings['grid.widths.rates/lines']?.value ?? '')).toEqual({ end: 90, index: 70 });
  });

  it("reads a grid's widths back whole once its first write is answered, by the name the server gives them", async () => {
    givenSettings([]);
    server();

    await setWritten('grid.widths.xva/csas', 'Column widths', { name: 120 });

    expect(settingsStore.getState().settings['grid.widths.xva/csas']).toMatchObject({ name: 'widths xva/csas', title: 'Column widths', set: true });
    expect(settingsStore.getState().writing).toEqual({});
  });

  it('says a write the server does not keep, and shows what it keeps', async () => {
    givenSettings([widths('xva/csas')]);
    server({ refuse: true });

    await setWritten('grid.widths.xva/csas', 'Column widths', { name: 120 });

    expect(settingsStore.getState().notice?.text).toBe('Column widths could not be kept: Column widths is an object.');
    expect(valueOf(settingsStore.getState(), 'grid.widths.xva/csas')).toBe('{}');
  });

  it('applies a value the line sets over one no longer taken, which is then gone', async () => {
    givenSettings([theme('dark', true, 'one of dark, light, auto')]);
    server();

    await setSetting('theme', 'light');

    expect(valueOf(settingsStore.getState(), THEME)).toBe('light');
    expect(settingsStore.getState().settings[THEME]?.unreadable).toBeUndefined();
  });

  it('applies at once a setting the AI changed for the user (ADR-0007 §7)', async () => {
    givenSettings([theme('dark', false)]);
    server();

    await heardChange({ key: THEME, name: 'theme', title: 'Theme', value: 'light', set: true, replaced: 'dark', wasSet: false, said: 'Theme is light.' });

    expect(valueOf(settingsStore.getState(), THEME)).toBe('light');
  });
});

describe('the theme (ADR-0017 §5, §6.2)', () => {
  it("draws the first frame in this browser's copy, then follows the user's setting", () => {
    localStorage.setItem('qube.theme.v1', 'light');
    const manager = themeManager();
    expect(manager.get('dark')).toBe('light');

    const heard: string[] = [];
    manager.subscribe((scheme) => heard.push(scheme));
    givenSettings([theme('auto')]);
    manager.unsubscribe();

    expect(heard).toEqual(['auto']);
  });

  it("applies the registered theme where the user's is no longer one the setting takes (ADR-0017 §2)", () => {
    givenSettings([theme('dark', true, 'one of dark, light, auto')]);

    expect(valueOf(settingsStore.getState(), THEME)).toBe('dark');
  });

  it('writes a theme the page sets, and not one the user already holds', async () => {
    givenSettings([theme('dark', false)]);
    const { sent } = server();
    const manager = themeManager();

    manager.set('dark');
    manager.set('light');
    await vi.waitFor(() => expect(sent).toHaveLength(1));

    expect(sent[0]).toEqual({ capability: 'settings.set', request: { setting: THEME, value: 'light' } });
    expect(localStorage.getItem('qube.theme.v1')).toBe('light');
  });

  it('ends on the theme toggled last when it is toggled back before the first write is answered', async () => {
    givenSettings([theme('dark', false)]);
    const { sent, answer, waiting } = server({ held: true });
    const manager = themeManager();
    const heard: string[] = [];
    manager.subscribe((scheme) => heard.push(scheme));

    manager.set('light');
    manager.set('dark');
    await vi.waitFor(() => expect(waiting()).toBe(1));
    answer();
    await vi.waitFor(() => expect(waiting()).toBe(1));
    answer();
    await vi.waitFor(() => expect(settingsStore.getState().writing).toEqual({}));
    manager.unsubscribe();

    expect(sent.map((s) => s.request.value)).toEqual(['light', 'dark']);
    expect(heard).toEqual(['light', 'dark']);
    expect(valueOf(settingsStore.getState(), THEME)).toBe('dark');
  });
});

describe('the command ranking (ADR-0006 §6, ADR-0017 §5)', () => {
  it("adds this browser's earlier ranking to the user's once, and removes the browser's copy", async () => {
    localStorage.setItem('qube.commandUsage.v1', JSON.stringify({ price: { count: 2, last: 1_000 } }));
    givenSettings([usage({ price: { count: 3, last: 5_000 }, 'theme.toggle': { count: 1, last: 2_000 } })]);
    const { sent } = server();

    await adoptUsage();
    await adoptUsage();

    expect(sent).toHaveLength(1);
    expect(JSON.parse(sent[0]?.request.value ?? '')).toEqual({ price: { count: 5, last: 5_000 }, 'theme.toggle': { count: 1, last: 2_000 } });
    expect(localStorage.getItem('qube.commandUsage.v1')).toBeNull();
    expect(usageScore('price', 5_000)).toBe(5);
  });

  it("takes the user's ranking as it is where the browser kept none, writing nothing", async () => {
    givenSettings([usage({ price: { count: 3, last: 5_000 } })]);
    const { sent } = server();

    await adoptUsage();

    expect(sent).toEqual([]);
    expect(usageScore('price', 5_000)).toBe(3);
  });

  it('keeps a command run once the ranking is taken in', async () => {
    givenSettings([usage(undefined)]);
    const { sent } = server();
    await adoptUsage();

    recordUse('price', 7_000);
    await vi.waitFor(() => expect(sent).toHaveLength(1));

    expect(JSON.parse(sent[0]?.request.value ?? '')).toEqual({ price: { count: 1, last: 7_000 } });
  });

  it('neither applies nor writes over a ranking that is no longer one the setting takes (ADR-0017 §2)', async () => {
    localStorage.setItem('qube.commandUsage.v1', JSON.stringify({ price: { count: 2, last: 1_000 } }));
    givenSettings([usage(undefined, 'an object of command ids')]);
    const { sent } = server();

    await adoptUsage();
    recordUse('price', 7_000);
    await Promise.resolve();

    expect(sent).toEqual([]);
    expect(localStorage.getItem('qube.commandUsage.v1')).not.toBeNull();
  });

  it("says a browser's earlier ranking it cannot read, and carries nothing over", async () => {
    localStorage.setItem('qube.commandUsage.v1', '{not json');
    givenSettings([usage(undefined)]);
    const { sent } = server();

    await adoptUsage();

    expect(sent).toEqual([]);
    expect(settingsStore.getState().notice?.text).toMatch(/^This browser's earlier command ranking could not be read, and was not carried over: /);
    expect(localStorage.getItem('qube.commandUsage.v1')).toBeNull();
  });

  it('adds two rankings run by run, keeping the later last run', () => {
    expect(merged({ a: { count: 1, last: 10 } }, { a: { count: 2, last: 5 }, b: { count: 1, last: 1 } }))
      .toEqual({ a: { count: 3, last: 10 }, b: { count: 1, last: 1 } });
  });
});
