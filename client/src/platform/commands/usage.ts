import { isUnreadable, say, setWritten, settingsStore, writtenOf } from '../settings/settings';

// How often and how lately this user ran each command, so that what they use comes first and a few letters find it
// (ADR-0006 §6). The user's `commands.usage` setting since ADR-0017: it follows them to any browser. A browser that
// ranked commands before keeps its ranking: it is carried over once, the first time the settings are read here, and
// its own copy is then removed. A ranking the user has that is no longer one the setting takes is neither applied nor
// written over (ADR-0017 §2): the settings dialog says why, and the commands run meanwhile rank for this page alone.

export const USAGE = 'commands.usage';
const EARLIER = 'qube.commandUsage.v1';
const HALF_LIFE_DAYS = 14;

export interface Use { readonly count: number; readonly last: number }
export type Uses = Readonly<Record<string, Use>>;

let uses: Uses = {};
// Whether the ranking the settings hold was taken into this page's; until then a command run is ranked here alone.
let adopted = false;

/** A command's score: its runs, each worth less the older it is. */
export function usageScore(commandId: string, now = Date.now()): number {
  const use = uses[commandId];
  if (use === undefined) return 0;
  return use.count * 0.5 ** ((now - use.last) / (HALF_LIFE_DAYS * 86_400_000));
}

export function recordUse(commandId: string, now = Date.now()): void {
  const before = uses[commandId];
  uses = { ...uses, [commandId]: { count: (before?.count ?? 0) + 1, last: now } };
  // Kept for the user once their ranking is taken in; a use before then is in the ranking it is taken into.
  if (adopted && !isUnreadable(settingsStore.getState(), USAGE)) void setWritten(USAGE, 'The command ranking', uses);
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

function forgetEarlier(): void {
  try {
    localStorage.removeItem(EARLIER);
  } catch {
    // Storage refused: it held nothing to remove.
  }
}

/** The ranking this browser kept before ADR-0017, where it kept one; one it cannot read is said, and left out. */
function earlier(): Uses | undefined {
  let text: string | null;
  try {
    text = localStorage.getItem(EARLIER);
  } catch {
    // Storage refused: this browser keeps nothing to carry over.
    return undefined;
  }
  if (text === null) return undefined;
  try {
    return JSON.parse(text) as Uses;
  } catch (error) {
    say(`This browser's earlier command ranking could not be read, and was not carried over: ${error instanceof Error ? error.message : String(error)}`);
    forgetEarlier();
    return undefined;
  }
}

/**
 * Takes the user's ranking from their settings, as read, once a page: with this browser's earlier ranking added once,
 * and what was run before the settings were read, which is then kept.
 */
export async function adoptUsage(): Promise<void> {
  if (adopted) return;
  adopted = true;
  const state = settingsStore.getState();
  if (isUnreadable(state, USAGE)) return;
  // A user who ran no command anywhere has no ranking: none is registered.
  const kept = writtenOf<Uses>(state, USAGE) ?? {};
  const local = earlier();
  const before = uses;
  uses = merged(merged(kept, local ?? {}), before);
  if (local === undefined && Object.keys(before).length === 0) return;
  await setWritten(USAGE, 'The command ranking', uses);
  if (local !== undefined) forgetEarlier();
}

/** For tests. */
export function resetUsage(): void {
  uses = {};
  adopted = false;
}
