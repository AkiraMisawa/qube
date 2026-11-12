import { createStore } from 'zustand/vanilla';
import { useStore } from 'zustand';
import { call, outputOf, type CommandAnswer } from '../api';

// A user's settings (ADR-0017 §2): kept on the server, read by the page as it starts, written through the command
// gateway's three commands, as the line, the settings dialog and the AI write them. A preference applies as soon as it
// changes; a default is the server's to copy into what the user makes, and the page only shows it.

/** A setting as the server reads it for the signed-in user (`settings.read`). */
export interface SettingDescribed {
  readonly key: string;
  /** What a line names it by: its words, and a family member's name after them (`widths rates/lines`). */
  readonly name: string;
  readonly title: string;
  readonly words: readonly string[];
  readonly kind: 'preference' | 'default';
  /** What it takes, in words that follow "is". */
  readonly takes: string;
  /** A line types its value; otherwise its page writes it, as JSON (a grid's widths). */
  readonly typed: boolean;
  readonly choices?: readonly string[];
  /** The value that applies, as a line types it; JSON for one its page writes; {@link NO_VALUE} for no value. */
  readonly value: string;
  readonly registered?: string;
  /** The user set it; otherwise the registered value applies. */
  readonly set: boolean;
  /** What the user set, where it is no longer a value the setting takes, and why. */
  readonly stored?: string;
  readonly unreadable?: string;
  readonly at?: string;
}

/** How the server writes a setting that has no value: one set to none, or registered as none and not set. */
export const NO_VALUE = 'none';

/** What a set or a reset answered: what applies now, what it replaced, and how to set it back (`said`). */
export interface SettingChanged {
  readonly key: string;
  readonly name: string;
  readonly title: string;
  readonly value: string;
  readonly set: boolean;
  readonly replaced: string;
  readonly wasSet: boolean;
  readonly said: string;
}

/** Something about the settings the user is told, numbered so that the same thing happening twice is said twice. */
export interface SettingsNotice {
  readonly text: string;
  readonly seq: number;
}

export interface SettingsState {
  /** True once the server's answer is in. Until then a preference is the page's own first value (the theme's copy), and the page sends nothing. */
  readonly loaded: boolean;
  /** Each setting as the server last described it. */
  readonly settings: Readonly<Record<string, SettingDescribed>>;
  /**
   * The values the page wrote by itself (a theme toggled, a column dragged) while their writes are not all answered:
   * what applies meanwhile, by key. The server's answer to the last of them takes their place.
   */
  readonly writing: Readonly<Record<string, string>>;
  /** The last thing about the settings the user was told: a write the page made that was not kept, a reading that failed. */
  readonly notice: SettingsNotice | undefined;
}

export const settingsStore = createStore<SettingsState>()(() => ({ loaded: false, settings: {}, writing: {}, notice: undefined }));

export function useSettings(): SettingsState {
  return useStore(settingsStore, (s) => s);
}

/**
 * The value that applies to a setting, as a line types it (JSON for one its page writes): the page's own while its
 * writes are on their way, else the server's. None where the setting has no value or is not held (a family member never
 * set). Where the user's value is no longer taken, the server answers the registered value, which applies (ADR-0017
 * §2: a preference changes no figure), and the dialog says that theirs is unreadable.
 */
export function valueOf(state: SettingsState, key: string): string | undefined {
  const own = state.writing[key];
  if (own !== undefined) return own;
  const held = state.settings[key];
  if (held === undefined || held.value === NO_VALUE) return undefined;
  return held.value;
}

/** {@link valueOf} for one its page writes, as the JSON it wrote. */
export function writtenOf<T>(state: SettingsState, key: string): T | undefined {
  const text = valueOf(state, key);
  return text === undefined ? undefined : (JSON.parse(text) as T);
}

/** The value that applies to a setting, as {@link valueOf} reads it, kept current. */
export function useValue(key: string): string | undefined {
  return useStore(settingsStore, (s) => valueOf(s, key));
}

/** True where the user set a value the setting no longer takes: it is not applied, and not replaced unasked (ADR-0017 §2). */
export function isUnreadable(state: SettingsState, key: string): boolean {
  return state.settings[key]?.unreadable !== undefined;
}

let said = 0;

/** Tells the user something about their settings: the shell shows it as a refused notice. */
export function say(text: string): void {
  settingsStore.setState({ notice: { text, seq: ++said } });
}

const messageOf = (error: unknown): string => (error instanceof Error ? error.message : String(error));

function byKey(settings: readonly SettingDescribed[]): Record<string, SettingDescribed> {
  return Object.fromEntries(settings.map((s) => [s.key, s]));
}

/** Reads the user's settings from the server (`GET /api/settings`), in place of what the page held. */
export async function loadSettings(): Promise<void> {
  const answer = await call<{ readonly settings: readonly SettingDescribed[] }>('/api/settings');
  settingsStore.setState({ loaded: true, settings: byKey(answer.settings) });
}

async function send(capability: string, request: Readonly<Record<string, string>>): Promise<CommandAnswer> {
  return call<CommandAnswer>(`/api/commands/${capability}/1.0.0`, {
    method: 'POST', body: JSON.stringify({ inputs: { request }, idempotencyKey: crypto.randomUUID() }),
  });
}

// The writes to each setting, in the order they were made, whoever made them on this page: one waits for the one
// before it, so the server keeps the last one made. Each is numbered; only the last one made settles what applies.
const queues = new Map<string, Promise<unknown>>();
const issued = new Map<string, number>();
let writes = 0;

function enqueue<T>(key: string, write: () => Promise<T>): { readonly n: number; readonly done: Promise<T> } {
  const n = ++writes;
  issued.set(key, n);
  const done = (queues.get(key) ?? Promise.resolve()).then(write);
  // The next write waits for this one whether or not it was kept; whoever made this one hears how it ended.
  queues.set(key, done.then(() => undefined, () => undefined));
  return { n, done };
}

const isLast = (key: string, n: number): boolean => issued.get(key) === n;

/** A setting the page holds, with what applies now from a set's answer: what was unreadable or stored is gone. */
function taken(held: SettingDescribed, changed: SettingChanged): SettingDescribed {
  const { stored: _stored, unreadable: _unreadable, at: _at, ...rest } = held;
  return { ...rest, value: changed.value, set: true };
}

/** One setting as the server describes it now (`settings.read` of it). */
async function readOne(key: string): Promise<SettingDescribed> {
  const listed = outputOf<{ readonly settings: readonly SettingDescribed[] }>(await send('settings.read', { setting: key }), 'answer');
  const [one] = listed.settings;
  if (one === undefined || listed.settings.length !== 1) throw new Error(`settings.read answered ${listed.settings.length} settings for ${key}.`);
  return one;
}

/**
 * What applies once the last write made to a setting is answered: the answer's value, for a setting the page holds
 * whole; else the setting read again, whole (a family member set for the first time, a reset, a write not kept). The
 * page's own value then gives way.
 */
async function settle(key: string, n: number, changed: SettingChanged | undefined): Promise<void> {
  const held = settingsStore.getState().settings[key];
  if (changed !== undefined && changed.set && held !== undefined) {
    settingsStore.setState((s) => ({ settings: { ...s.settings, [key]: taken(held, changed) }, writing: without(s.writing, key) }));
    return;
  }
  if (changed === undefined || !changed.set) {
    // A reset family member is listed no more, and a write not kept leaves what was kept: the whole list says both.
    await loadSettings();
    if (isLast(key, n)) settingsStore.setState((s) => ({ writing: without(s.writing, key) }));
    return;
  }
  const one = await readOne(key);
  if (isLast(key, n)) settingsStore.setState((s) => ({ settings: { ...s.settings, [key]: one }, writing: without(s.writing, key) }));
}

function without<T>(record: Readonly<Record<string, T>>, key: string): Record<string, T> {
  const { [key]: _gone, ...rest } = record;
  return rest;
}

/** The key of a setting named as the line names it (`theme`), or given by its key. */
function keyOf(setting: string): string {
  const named = Object.values(settingsStore.getState().settings).find((s) => s.key === setting || s.name.toLowerCase() === setting.trim().toLowerCase());
  return named?.key ?? setting;
}

async function command(capability: string, setting: string, request: Readonly<Record<string, string>>): Promise<SettingChanged> {
  const key = keyOf(setting);
  const { n, done } = enqueue(key, async () => outputOf<SettingChanged>(await send(capability, request), 'answer'));
  const changed = await done;
  // A name the page holds no setting by is the server's to read: the whole list then says what it is now.
  if (changed.key !== key) await loadSettings();
  else if (isLast(key, n)) await settle(key, n, changed);
  return changed;
}

/** Sets a setting, by its name or key, to a value as a line types it (JSON for one its page writes): `settings.set`. */
export function setSetting(setting: string, value: string): Promise<SettingChanged> {
  return command('settings.set', setting, { setting, value });
}

/** Returns a setting to its registered value: `settings.reset`. */
export function resetSetting(setting: string): Promise<SettingChanged> {
  return command('settings.reset', setting, { setting });
}

/**
 * A set or a reset the AI made for the user (ADR-0007 §7): it is the last write to that setting, and what it answered
 * applies at once, as one the line made.
 */
export function heardChange(changed: SettingChanged): Promise<void> {
  const n = ++writes;
  issued.set(changed.key, n);
  return settle(changed.key, n, changed);
}

/**
 * Writes a value the page makes by itself (a theme toggled, a column dragged), as a line types it (JSON for one its
 * page writes): it applies at once, and the write follows the ones before it to the same setting. One the server does
 * not keep is said, and the page then shows what is kept.
 */
export function writeByPage(key: string, title: string, text: string): Promise<void> {
  settingsStore.setState((s) => ({ writing: { ...s.writing, [key]: text } }));
  const { n, done } = enqueue(key, async () => outputOf<SettingChanged>(await send('settings.set', { setting: key, value: text }), 'answer'));
  return done.then(
    (changed) => (isLast(key, n) ? settle(key, n, changed) : undefined),
    (error: unknown) => {
      say(`${title} could not be kept: ${messageOf(error)}`);
      return isLast(key, n) ? settle(key, n, undefined) : undefined;
    },
  ).catch((error: unknown) => say(`Your settings could not be read again: ${messageOf(error)}`));
}

/** {@link writeByPage} of a value the page holds as JSON (a grid's widths, the command ranking). */
export function setWritten(key: string, title: string, value: unknown): Promise<void> {
  return writeByPage(key, title, JSON.stringify(value));
}

/** For tests: the settings as a server would answer them, and nothing on its way. */
export function givenSettings(settings: readonly SettingDescribed[]): void {
  settingsStore.setState({ loaded: true, settings: byKey(settings), writing: {}, notice: undefined });
}
