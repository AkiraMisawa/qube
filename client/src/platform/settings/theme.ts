import type { MantineColorScheme, MantineColorSchemeManager } from '@mantine/core';
import { settingsStore, valueOf, writeByPage } from './settings';

// The theme is the user's `shell.theme` (ADR-0017's first registrations): dark, light, or auto, which follows each
// device's OS. The server holds it; this browser keeps a copy of the last one it read for the first frame only, so a
// light-theme user is not shown a dark page while the settings load (ADR-0017 §5). A preference changes no figure, so
// the copy can make nothing wrong; the server's answer replaces it.

export const THEME = 'shell.theme';
const COPY = 'qube.theme.v1';

function isScheme(value: unknown): value is MantineColorScheme {
  return value === 'dark' || value === 'light' || value === 'auto';
}

function copied(): MantineColorScheme | undefined {
  try {
    const value = localStorage.getItem(COPY);
    return isScheme(value) ? value : undefined;
  } catch {
    // Storage refused (a private window, blocked site data): the first frame is the registered theme until the
    // settings are read, as without a copy.
    return undefined;
  }
}

function copy(value: MantineColorScheme): void {
  try {
    localStorage.setItem(COPY, value);
  } catch {
    // Storage refused: the next start draws its first frame in the registered theme, then the user's.
  }
}

/** The theme that applies for the user: the page's own while its write is on its way, else the server's; none before the settings are read. */
function applying(state = settingsStore.getState()): MantineColorScheme | undefined {
  const value = valueOf(state, THEME);
  return isScheme(value) ? value : undefined;
}

/**
 * Mantine's colour scheme manager over the user's setting: it starts from the browser's copy, follows the server's
 * value as it is read or changed (by the line, the dialog or the AI), and writes a change the page makes (the header's
 * toggle) to the server, which the copy then follows.
 */
export function themeManager(): MantineColorSchemeManager {
  let unsubscribe: (() => void) | undefined;
  return {
    get: (defaultValue) => copied() ?? defaultValue,
    set: (value) => {
      copy(value);
      // What already applies is not written again: following the server calls this too. Before the settings are read
      // nothing is sent; the shell is drawn only once they are, or says that they could not be.
      if (!settingsStore.getState().loaded || applying() === value) return;
      void writeByPage(THEME, 'The theme', value);
    },
    subscribe: (onUpdate) => {
      unsubscribe = settingsStore.subscribe((state, before) => {
        const now = applying(state);
        if (now !== undefined && now !== applying(before)) onUpdate(now);
      });
    },
    unsubscribe: () => unsubscribe?.(),
    clear: () => {
      try {
        localStorage.removeItem(COPY);
      } catch {
        // Storage refused: there is no copy to clear.
      }
    },
  };
}
