import { useSyncExternalStore } from 'react';

// The windows of the browser the panels stand in (ADR-0018 §9): the main one, and each other window a panel was popped
// out into while it is open. The areas add a window as dockview opens it and take it away as it closes; what draws in
// a window (its portals, its notices, its keys) reads it here. And the **window in use**: the one the user last pressed
// in or focused, where the shell's dialogs and the command line open and the notices are said. It is read only while
// no dialog is open, so that a dialog never moves to another window while it is open.

/** Another window of the browser, and the node in its body that Mantine's portals draw into. */
export interface OtherWindow {
  readonly window: Window;
  readonly portal: HTMLElement;
}

let others: readonly OtherWindow[] = [];
let inUse: Window | undefined;
const listeners = new Set<() => void>();
const told = () => listeners.forEach((listener) => listener());

/** Hears the windows open and the window in use change; answers what stops hearing. */
export const hearWindowsChange = (listener: () => void): (() => void) => {
  listeners.add(listener);
  return () => { listeners.delete(listener); };
};

/** The other windows open now, in the order they were opened. */
export function otherWindows(): readonly OtherWindow[] {
  return others;
}

export function useOtherWindows(): readonly OtherWindow[] {
  return useSyncExternalStore(hearWindowsChange, otherWindows);
}

/** Every window the panels stand in: the main one, then the others in the order they were opened. */
export function allWindows(): readonly Window[] {
  return [window, ...others.map((o) => o.window)];
}

/** Adds a window as it opens; answers what takes it away as it closes. */
export function addWindow(other: OtherWindow): () => void {
  others = [...others, other];
  told();
  return () => {
    others = others.filter((o) => o !== other);
    if (inUse === other.window) inUse = undefined;
    told();
  };
}

/** The window in use: the main one until the user presses in or focuses another. */
export function windowInUse(): Window {
  return inUse ?? window;
}

export function useWindowInUse(): Window {
  return useSyncExternalStore(hearWindowsChange, windowInUse);
}

const keys = new WeakMap<Window, string>();
let windowsSeen = 0;

/**
 * A name for a window, the same while it is open: what is drawn in a window is keyed by it, so that a page moved into
 * another window, or back, is drawn anew there (ADR-0018 §9: ExGrid reads its window as a grid is created).
 */
export function windowKey(win: Window): string {
  if (win === window) return 'main';
  const known = keys.get(win);
  if (known !== undefined) return known;
  windowsSeen += 1;
  const key = `window-${windowsSeen}`;
  keys.set(win, key);
  return key;
}

/** The node an other window's portals draw into; undefined for the main window, whose are Mantine's own. */
export function portalOf(win: Window): HTMLElement | undefined {
  return others.find((o) => o.window === win)?.portal;
}

/** A dialog that holds the keyboard while it is open: the shell's, a page's, or the command line. */
export const MODAL_DIALOG = '[role="dialog"][aria-modal="true"]';

/** Whether a dialog, or the command line, is open in any window: the window in use does not change while one is. */
function aDialogIsOpen(): boolean {
  return allWindows().some((w) => w.document.querySelector(MODAL_DIALOG) !== null);
}

/**
 * Says that the user pressed in, focused or typed a key of the shell's in this window. It is told even where the window
 * was the one in use already, so that what follows the window in use (the active panel) is read again: a verb run there
 * may have moved the active panel out of it.
 */
export function usedWindow(win: Window): void {
  if (aDialogIsOpen()) return;
  inUse = win === window ? undefined : win;
  told();
}

/**
 * Hears the user use a window, for the window in use: a press in it, in the capture phase before anything under the
 * press opens a dialog, and the window taking the keyboard; in another window, a focus moved inside it too. Answers
 * what stops hearing.
 */
export function hearUse(win: Window): () => void {
  const used = () => usedWindow(win);
  // In the main window a focus moved inside it is the shell's own as often as the user's (the command line giving the
  // keyboard back as it closes, after `pop out panel` was run from it), and would take the window in use back from the
  // window just opened: there a press and the window's own focus say it.
  const focusIn = win !== window;
  win.document.addEventListener('pointerdown', used, true);
  if (focusIn) win.document.addEventListener('focusin', used, true);
  // The window itself taking the keyboard back (Alt+Tab, a press on its title bar) is a use of it too.
  win.addEventListener('focus', used);
  return () => {
    win.document.removeEventListener('pointerdown', used, true);
    if (focusIn) win.document.removeEventListener('focusin', used, true);
    win.removeEventListener('focus', used);
  };
}
