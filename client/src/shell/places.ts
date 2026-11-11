// The places of each open page (ADR-0016 §9, note 20 of 2026-11-11): where the keyboard can be put by name. A page
// registers them while it is mounted, by its panel, and the shell builds `go <place>` and `focus grid` from the places
// of the page in the active panel. Nothing here knows a grid, a tab or an application.

/** A place of a page. */
export interface Place {
  /** The word `go` takes for it: one word, lower case. */
  readonly word: string;
  /** What the screen calls it, as a sentence names it (`Existing trades`, `the CSA tab`): the notice says the keyboard is in it. */
  readonly title: string;
  /** A grid: `focus grid` goes to the first of a page's. */
  readonly grid?: boolean;
  /** Puts the keyboard there. Where it cannot now, answers why, and has moved nothing. */
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
