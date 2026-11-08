import type { RegisteredMeasure } from './contracts';

// What an open XVA workspace page offers to commands, by panel: its Price and its measures (ADR-0010 §9). The page
// registers them while it is mounted, so a command and the page's own button run the same function.

export interface XvaPage {
  price(): void;
  /** Sets the measures Price prices; the server refuses an unknown one, or none, by name. */
  setMeasures(measures: readonly string[]): void;
  /** B or A for every row of a grid (ADR-0010 §6); the grid the keyboard was last in when none is named. */
  setSide(side: 'before' | 'after', value: boolean, grid?: 'new' | 'existing'): void;
  /** Saves the Run the dock shows as a zip (`download run`, ADR-0010 §7). */
  downloadRun(): void;
  /** Puts the dock's Run's quote on the clipboard as HTML and text (`copy quote`, ADR-0010 §7). */
  copyQuote(): void;
  /** Asks again for every failed result on the new trades, such as a PV01 (`pv01 retry`, ADR-0010 §6). */
  retry(): void;
  /** The registered measures, for the command line to offer. */
  measures(): readonly RegisteredMeasure[];
}

const pages = new Map<string, XvaPage>();

export function registerXvaPage(panelId: string, page: XvaPage | undefined): void {
  if (page === undefined) pages.delete(panelId);
  else pages.set(panelId, page);
}

export function xvaPageOf(panelId: string | undefined): XvaPage | undefined {
  return panelId === undefined ? undefined : pages.get(panelId);
}

/** `measures cva fva`: the words typed, as the ids the server takes, in the order typed and once each. */
export function measuresTyped(words: string): readonly string[] {
  return [...new Set(words.trim().toLowerCase().split(/\s+/).filter((w) => w !== ''))];
}
