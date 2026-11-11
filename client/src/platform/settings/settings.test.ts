import { adoptUsage, merged, recordUse, resetUsage, usageScore } from '../commands/usage';
import { givenSettings, setWritten, settingsStore, type SettingDescribed } from './settings';
import { themeManager } from './theme';

// ADR-0017 §5: the page's own writes to the user's settings, the theme's copy for the first frame, and the command
// ranking carried over once from a browser that kept it.

const theme = (value: string, set = true): SettingDescribed => ({
  key: 'shell.theme', name: 'theme', title: 'Theme', words: ['theme'], kind: 'preference', takes: 'one of dark, light or auto', typed: true,
  choices: ['dark', 'light', 'auto'], value, registered: 'dark', set,
});

const usage = (value: unknown): SettingDescribed => ({
  key: 'commands.usage', name: 'command ranking', title: 'Command ranking', words: ['command ranking'], kind: 'preference',
  takes: 'a value its page writes, as JSON', typed: false, value: JSON.stringify(value), registered: '{}', set: true,
});

interface Sent { readonly capability: string; readonly request: Readonly<Record<string, string>> }

/** The server, answering each set as kept, and the settings as last set. */
function server(): Sent[] {
  const sent: Sent[] = [];
  vi.stubGlobal('fetch', vi.fn(async (path: string, init?: RequestInit) => {
    if (path === '/api/settings') return new Response(JSON.stringify({ settings: Object.values(settingsStore.getState().settings) }));
    const capability = /\/api\/commands\/([^/]+)\//.exec(path)?.[1] ?? '';
    const request = (JSON.parse(String(init?.body)) as { inputs: { request: Record<string, string> } }).inputs.request;
    sent.push({ capability, request });
    const key = request.setting ?? '';
    return new Response(JSON.stringify({ capabilityId: capability, version: '1.0.0', outputs: { answer: {
      key, name: key, title: key, value: request.value ?? '', set: true, replaced: '', wasSet: false, said: `${key} set.`,
    } } }));
  }));
  return sent;
}

beforeEach(() => {
  localStorage.clear();
  resetUsage();
  settingsStore.setState({ loaded: false, settings: {}, failed: undefined });
});

afterEach(() => vi.unstubAllGlobals());

describe("the page's writes to a setting (ADR-0017 §5)", () => {
  it('applies a value at once and sends the writes in the order they were made', async () => {
    const sent = server();
    givenSettings([]);

    const first = setWritten('grid.widths.xva/csas', 'Column widths', { name: 120 });
    const second = setWritten('grid.widths.xva/csas', 'Column widths', { name: 120, threshold: 90 });
    expect(JSON.parse(settingsStore.getState().settings['grid.widths.xva/csas']?.value ?? '')).toEqual({ name: 120, threshold: 90 });
    await Promise.all([first, second]);

    expect(sent.map((s) => JSON.parse(s.request.value ?? '') as unknown)).toEqual([{ name: 120 }, { name: 120, threshold: 90 }]);
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

  it('writes a theme the page sets, and not one the user already holds', async () => {
    const sent = server();
    givenSettings([theme('dark', false)]);
    const manager = themeManager();

    manager.set('dark');
    manager.set('light');
    await vi.waitFor(() => expect(sent).toHaveLength(1));

    expect(sent[0]).toEqual({ capability: 'settings.set', request: { setting: 'shell.theme', value: 'light' } });
    expect(localStorage.getItem('qube.theme.v1')).toBe('light');
  });
});

describe('the command ranking (ADR-0006 §6, ADR-0017 §5)', () => {
  it("adds this browser's earlier ranking to the user's once, and removes the browser's copy", async () => {
    const sent = server();
    localStorage.setItem('qube.commandUsage.v1', JSON.stringify({ price: { count: 2, last: 1_000 } }));
    givenSettings([usage({ price: { count: 3, last: 5_000 }, 'theme.toggle': { count: 1, last: 2_000 } })]);

    await adoptUsage();

    expect(JSON.parse(sent[0]?.request.value ?? '')).toEqual({ price: { count: 5, last: 5_000 }, 'theme.toggle': { count: 1, last: 2_000 } });
    expect(localStorage.getItem('qube.commandUsage.v1')).toBeNull();
    expect(usageScore('price', 5_000)).toBe(5);
  });

  it("takes the user's ranking as it is where the browser kept none, writing nothing", async () => {
    const sent = server();
    givenSettings([usage({ price: { count: 3, last: 5_000 } })]);

    await adoptUsage();

    expect(sent).toEqual([]);
    expect(usageScore('price', 5_000)).toBe(3);
  });

  it('keeps a command run once the settings are read', async () => {
    const sent = server();
    givenSettings([usage({})]);

    recordUse('price', 7_000);
    await vi.waitFor(() => expect(sent).toHaveLength(1));

    expect(JSON.parse(sent[0]?.request.value ?? '')).toEqual({ price: { count: 1, last: 7_000 } });
  });

  it('adds two rankings run by run, keeping the later last run', () => {
    expect(merged({ a: { count: 1, last: 10 } }, { a: { count: 2, last: 5 }, b: { count: 1, last: 1 } }))
      .toEqual({ a: { count: 3, last: 10 }, b: { count: 1, last: 1 } });
  });
});
