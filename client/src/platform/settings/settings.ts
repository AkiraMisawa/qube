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
  /** The value that applies, as a line types it; JSON for one its page writes; `none` for no value. */
  readonly value: string;
  readonly registered?: string;
  /** The user set it; otherwise the registered value applies. */
  readonly set: boolean;
  /** What the user set, where it is no longer a value the setting takes, and why. */
  readonly stored?: string;
  readonly unreadable?: string;
  readonly at?: string;
}

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

export interface SettingsState {
  /** True once the server's answer is in. Until then a preference is the page's own first value (the theme's copy). */
  readonly loaded: boolean;
  readonly settings: Readonly<Record<string, SettingDescribed>>;
  /** Why the settings could not be read, where they could not. */
  readonly failed: string | undefined;
}

export const settingsStore = createStore<SettingsState>()(() => ({ loaded: false, settings: {}, failed: undefined }));

/** The setting by its key, once read; none before, or where it is not registered (a family member not set). */
export function useSetting(key: string): SettingDescribed | undefined {
  return useStore(settingsStore, (s) => s.settings[key]);
}

export function useSettings(): SettingsState {
  return useStore(settingsStore, (s) => s);
}

function byKey(settings: readonly SettingDescribed[]): Record<string, SettingDescribed> {
  return Object.fromEntries(settings.map((s) => [s.key, s]));
}

/** Reads the user's settings from the server (`GET /api/settings`), in place of what the page held. */
export async function loadSettings(): Promise<void> {
  try {
    const answer = await call<{ readonly settings: readonly SettingDescribed[] }>('/api/settings');
    settingsStore.setState({ loaded: true, settings: byKey(answer.settings), failed: undefined });
  } catch (error) {
    settingsStore.setState({ failed: error instanceof Error ? error.message : String(error) });
    throw error;
  }
}

async function command(capability: string, request: Readonly<Record<string, string>>): Promise<SettingChanged> {
  const answer = await call<CommandAnswer>(`/api/commands/${capability}/1.0.0`, {
    method: 'POST', body: JSON.stringify({ inputs: { request }, idempotencyKey: crypto.randomUUID() }),
  });
  const changed = outputOf<SettingChanged>(answer, 'answer');
  const held = settingsStore.getState().settings[changed.key];
  // A setting the page holds, set, takes what applies now from the answer. One it has not (a family member set for the
  // first time) or one reset (a family member then goes) is read back whole, with what it is.
  if (held !== undefined && changed.set)
    settingsStore.setState((s) => ({ settings: { ...s.settings, [changed.key]: { ...held, value: changed.value, set: true } } }));
  else await loadSettings();
  return changed;
}

/** Sets a setting, by its name or key, to a value as a line types it (JSON for one its page writes): `settings.set`. */
export function setSetting(setting: string, value: string): Promise<SettingChanged> {
  return command('settings.set', { setting, value });
}

/** Returns a setting to its registered value: `settings.reset`. */
export function resetSetting(setting: string): Promise<SettingChanged> {
  return command('settings.reset', { setting });
}

/** A setting's value as the page wrote it (JSON), where the user set it and it is still taken; none otherwise. */
export function written<T>(setting: SettingDescribed | undefined): T | undefined {
  if (setting === undefined || !setting.set || setting.unreadable !== undefined) return undefined;
  return JSON.parse(setting.value) as T;
}

// Writes the page makes by itself to one setting, in the order it made them, so that the last one made is the one kept.
const queued = new Map<string, Promise<unknown>>();

/**
 * Sets a setting the page writes (a grid's widths, the command ranking) to a value it holds as JSON: what applies is
 * the new value at once, and the write follows the ones before it to the same setting. One the server refuses is
 * said (`notKept`), and the page reads the settings again, so it shows what is kept.
 */
export function setWritten(key: string, title: string, value: unknown): Promise<void> {
  const text = JSON.stringify(value);
  settingsStore.setState((s) => {
    const held = s.settings[key];
    const now: SettingDescribed = held === undefined
      ? { key, name: key, title, words: [], kind: 'preference', takes: '', typed: false, value: text, set: true }
      : { ...held, value: text, set: true };
    return { settings: { ...s.settings, [key]: now } };
  });
  const write = (queued.get(key) ?? Promise.resolve()).then(() => setSetting(key, text)).then(
    () => undefined,
    async (error: unknown) => {
      notKept(title, error);
      // What is kept, read again; a reading that fails says so itself (`failed`).
      await loadSettings().catch(() => undefined);
    });
  queued.set(key, write);
  return write;
}

/**
 * Says that a setting the page wrote by itself (a theme toggled, a column dragged) was not kept: the shell shows it as a
 * refused notice, so the user knows it will not be there next time.
 */
export function notKept(what: string, error: unknown): void {
  settingsStore.setState({ failed: `${what} could not be kept: ${error instanceof Error ? error.message : String(error)}` });
}

/** For tests: the settings as a server would answer them. */
export function givenSettings(settings: readonly SettingDescribed[]): void {
  settingsStore.setState({ loaded: true, settings: byKey(settings), failed: undefined });
}

const dialog = createStore<{ readonly open: boolean }>()(() => ({ open: false }));

/** Shows the settings dialog (`settings` on the line). */
export function openSettings(): void {
  dialog.setState({ open: true });
}

export function closeSettings(): void {
  dialog.setState({ open: false });
}

export function useSettingsOpen(): boolean {
  return useStore(dialog, (s) => s.open);
}
