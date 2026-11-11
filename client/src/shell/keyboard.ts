// Where the keyboard goes around the command line is owned here rather than left to Mantine's timers. Until a surface
// has mounted and taken the keyboard, keys go to nothing: opening the line and running a command first let go of the
// focused element, so a key typed at machine speed is dropped, never delivered to the grid behind the line or to a line
// that is closing. The keyboard goes back where it was when the line closes, unless what ran put it somewhere itself.

let before: HTMLElement | null = null;

/** Remembers where the keyboard is as the line opens; an opening that already remembers a place keeps it. */
export function rememberKeyboard(): void {
  if (before === null && document.activeElement instanceof HTMLElement && document.activeElement !== document.body) before = document.activeElement;
}

export function letGo(): void {
  if (document.activeElement instanceof HTMLElement) document.activeElement.blur();
}

/** Gives the keyboard back to where it was when the line opened, where that is still on the page. */
export function returnKeyboard(): void {
  const element = before;
  before = null;
  if (element !== null && element.isConnected) element.focus({ preventScroll: true });
}

/**
 * What ran put the keyboard somewhere itself, or opened a surface that takes it (ADR-0016 §9, note 22 of 2026-11-11):
 * the line has none to give back, whenever the keyboard arrives there.
 */
export function keepKeyboard(): void {
  before = null;
}
