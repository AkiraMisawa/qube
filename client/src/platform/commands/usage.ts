import { setSetting, setWritten, settingsStore, written } from '../settings/settings';

// How often and how lately this user ran each command, so that what they use comes first and a few letters find it
// (ADR-0006 §6). The user's `commands.usage` setting since ADR-0017: it follows them to any browser. A browser that
// ranked commands before keeps its ranking: it is carried over once, the first time the settings are read here, and
// its own copy is then removed.

export const USAGE = 'commands.usage';
const EARLIER = 'qube.commandUsage.v1';
const HALF_LIFE_DAYS = 14;

export interface Use { readonly count: number; readonly last: number }
export type Uses = Readonly<Record<string, Use>>;

let uses: Uses = {};

/** A command's score: its runs, each worth less the older it is. */
export function usageScore(commandId: string, now = Date.now()): number {
  const use = uses[commandId];
  if (use === undefined) return 0;
  return use.count * 0.5 ** ((now - use.last) / (HALF_LIFE_DAYS * 86_400_000));
}

export function recordUse(commandId: string, now = Date.now()): void {
  const before = uses[commandId];
  uses = { ...uses, [commandId]: { count: (before?.count ?? 0) + 1, last: now } };
  // Kept for the user once the settings are read; a use before then is in the ranking the page reads them into.
  if (settingsStore.getState().loaded) void setWritten(USAGE, 'The command ranking', uses);
}

/** Two rankings as one: each command's runs added, and its later last run. */
export function merged(a: Uses, b: Uses): Uses {
  const all: Record<string, Use> = { ...a };
  for (const [id, use] of Object.entries(b)) {
    const other = all[id];
    all[id] = other === undefined ? use : { count: other.count + use.count, last: Math.max(other.last, use.last) };
  }
  return all;
}

function earlier(): Uses | undefined {
  try {
    const text = localStorage.getItem(EARLIER);
    return text === null ? undefined : (JSON.parse(text) as Uses);
  } catch {
    // Storage refused, or a copy that cannot be read: there is nothing this browser can carry over.
    return undefined;
  }
}

/**
 * Takes the user's ranking from their settings, as read: with this browser's earlier ranking added once, and what
 * was run before the settings were read, which is then kept.
 */
export async function adoptUsage(): Promise<void> {
  const kept = written<Uses>(settingsStore.getState().settings[USAGE]) ?? {};
  const local = earlier();
  const before = uses;
  uses = merged(merged(kept, local ?? {}), before);
  if (local === undefined && Object.keys(before).length === 0) return;
  await setSetting(USAGE, JSON.stringify(uses));
  try {
    localStorage.removeItem(EARLIER);
  } catch {
    // Storage refused: it held nothing to remove.
  }
}

/** For tests. */
export function resetUsage(): void {
  uses = {};
}
