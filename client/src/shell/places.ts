import { CommandRefused } from '../platform/commands/commands';
import type { Notify } from '../platform/notices';
import { keepKeyboard } from './keyboard';

// The places of each open page (ADR-0016 §9, notes 20 and 30 of 2026-11-11): where the keyboard can be put by name. A
// page registers them while it is mounted, by its panel, and the shell builds `open <tab>`, `go <place>` and `focus
// grid` from the places of the page in the active panel. Nothing here knows which grids or tabs an application has.

/** A place of a page. */
export interface Place {
  /** The word `go` takes for it, or `open` where it is a tab: one word, lower case. */
  readonly word: string;
  /** What the screen calls it, as a sentence names it (`Existing trades`, `the CSA tab`): the notice says the keyboard is in it. */
  readonly title: string;
  /** A grid: `focus grid` goes to the first of a page's. */
  readonly grid?: boolean;
  /** A tab of the page (ADR-0016 note 30): `open <word>` shows it and puts the keyboard in it, and `go` offers the others. */
  readonly tab?: boolean;
  /**
   * Puts the keyboard there: at once, or once the page has drawn the part the place is in. A place a page offers has
   * something to take the keyboard, whatever else it shows. Where it cannot be gone to now, answers why, and has moved
   * nothing.
   */
  readonly go: () => string | undefined;
}

const pages = new Map<string, () => readonly Place[]>();

/** A page's places are read when a command asks for them, so a place that comes and goes with the page's state is there or not as the page then is. */
export function registerPlaces(panelId: string, places: (() => readonly Place[]) | undefined): void {
  if (places === undefined) pages.delete(panelId);
  else pages.set(panelId, places);
}

export function placesOf(panelId: string | undefined): readonly Place[] {
  return (panelId === undefined ? undefined : pages.get(panelId))?.() ?? [];
}

/** A page of one grid: its one place, `grid`. */
export function registerGridFocus(panelId: string, focus: (() => void) | undefined): void {
  registerPlaces(panelId, focus === undefined ? undefined : () => [{ word: 'grid', title: 'the grid', grid: true, go: () => { focus(); return undefined; } }]);
}

/**
 * Puts the keyboard in a place and says where it went (ADR-0016 §9, notes 20 and 22). The keyboard is the place's from
 * here on: the line gives none back, however long the page takes to draw the place. A place that cannot be gone to now
 * refuses with its reason, and nothing is said to have moved.
 */
export function goTo(place: Place, notify: Notify): void {
  const why = place.go();
  if (why !== undefined) throw new CommandRefused(why);
  keepKeyboard();
  notify('done', `The keyboard is in ${place.title}.`);
}
