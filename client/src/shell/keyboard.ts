// Where the keyboard goes around the command line is owned here rather than left to Mantine's timers. Until a surface
// has mounted and taken the keyboard, keys go to nothing: opening the line and running a command first let go of the
// focused element, so a key typed at machine speed is dropped, never delivered to the grid behind the line or to a line
// that is closing. The keyboard goes back where it was when the line closes, unless what ran put it somewhere itself.
// The line is in the window in use (ADR-0018 §9), and so is the keyboard it remembers, lets go of and holds.

import { windowInUse } from './windows';
import { isHTMLElement } from '../platform/elements';

let before: HTMLElement | null = null;

/**
 * Remembers where the keyboard is as the line opens, or the place it is to go back to where the line is opened for one
 * (a tab's Rename opens it for its tab); an opening that already remembers a place keeps it.
 */
export function rememberKeyboard(place?: HTMLElement): void {
  if (before !== null) return;
  const doc = windowInUse().document;
  const at = place ?? doc.activeElement;
  if (isHTMLElement(at) && at !== doc.body) before = at;
}

export function letGo(): void {
  const at = windowInUse().document.activeElement;
  if (isHTMLElement(at)) at.blur();
}

/**
 * Gives the keyboard back as the line closes: to where it was when the line opened, or to where a chained command put
 * it since, where that is still on the page. The line holds it no longer.
 */
export function returnKeyboard(): void {
  releaseKeyboard();
  const element = before;
  before = null;
  if (element !== null && element.isConnected) element.focus({ preventScroll: true });
}

/**
 * What ran put the keyboard somewhere itself, or opened a surface that takes it (ADR-0016 §9, note 22 of 2026-11-11):
 * the line has none to give back from before, whenever the keyboard arrives there. Where the line holds the keyboard
 * and the place has taken it already, that place is kept: it is where the keyboard goes when the line closes.
 */
export function keepKeyboard(): void {
  if (held !== undefined) held.kept = true;
  if (held === undefined || !held.arrived) before = null;
}

/**
 * Whether the command the line holds the keyboard for said the keyboard is its place's, and the place has not taken it
 * yet (ADR-0016 §9, note 24): that command has not ended, and the line's screen is not yet the place's.
 */
export function keyboardAwaited(): boolean {
  return held !== undefined && held.kept && !held.arrived;
}

// While a chain of commands runs from the line (ADR-0016 §9, note 24 of 2026-11-11) the keyboard stays the line's. A
// command may still put it in a place, as `go`, `find` and `open <tab>` do, at once or once its page has drawn the
// place: the line then takes it back, and that place is where the keyboard goes when the line closes. A dialog that
// takes the keyboard keeps it, and the line is told it lost the keyboard. The listener is the line's own, attached from
// its first chained command until it closes, and acts only on a focus outside it.
export interface KeyboardHeld {
  /** A focus that landed outside the line was taken back. */
  taken(): void;
  /** A dialog took the keyboard: the line holds it no longer, and has no place to give it back to. */
  lost(): void;
}

/** The line holding the keyboard, and the document its focus is heard in: the line's own. */
let held: { readonly line: HTMLElement; readonly on: KeyboardHeld; readonly heardIn: Document; arrived: boolean; kept: boolean } | undefined;

const onFocusIn = (event: FocusEvent): void => {
  const to = event.target;
  if (held === undefined || !isHTMLElement(to) || to.closest('.qube-palette') !== null) return;
  if (to.closest('[role="dialog"]') !== null) {
    const { on } = held;
    releaseKeyboard();
    before = null;
    on.lost();
    return;
  }
  before = to;
  held.arrived = true;
  held.line.focus({ preventScroll: true });
  held.on.taken();
};

/**
 * The line keeps the keyboard from now on, as a chained command begins. Called again for the next chained command, it
 * counts that command's own: whether it said the keyboard is a place's, and whether the place has taken it.
 */
export function holdKeyboard(line: HTMLElement, on: KeyboardHeld): void {
  // Heard in the line's own document, which is the window in use's: the window in use does not change while the line
  // is open, but a line drawn anew in another document moves the listener with it.
  const heardIn = line.ownerDocument;
  if (held?.heardIn !== heardIn) {
    held?.heardIn.removeEventListener('focusin', onFocusIn);
    heardIn.addEventListener('focusin', onFocusIn);
  }
  held = { line, on, heardIn, arrived: false, kept: false };
}

/**
 * The line holds the keyboard no longer: it is closing, and what is focused from now on keeps the keyboard. Answers
 * whether it was holding it.
 */
export function releaseKeyboard(): boolean {
  if (held === undefined) return false;
  const { heardIn } = held;
  held = undefined;
  heardIn.removeEventListener('focusin', onFocusIn);
  return true;
}
