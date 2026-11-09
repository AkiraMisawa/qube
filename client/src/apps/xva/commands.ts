import type { RegisteredMeasure } from './contracts';
import type { DockTab } from './dock';
import type { InputTab } from './XvaInputs';

// What an open XVA workspace page offers to commands, by panel: its Price and its measures (ADR-0010 §9). The page
// registers them while it is mounted, so a command and the page's own button run the same function.

export interface XvaPage {
  price(): void;
  /** Sets the measures Price prices; the server refuses an unknown one, or none, by name. */
  setMeasures(measures: readonly string[]): void;
  /** B or A for every row of a grid (ADR-0010 §6); the grid the keyboard was last in when none is named. */
  setSide(side: 'before' | 'after', value: boolean, grid?: 'new' | 'existing'): void;
  /** A case per value of a cell (`vary CSA-A threshold 10mm, 5mm, 0`, ADR-0010 §3, §9). */
  vary(cell: string, values: readonly string[]): void;
  /** Removes a case with its changes, named by its title or id (`case remove`, ADR-0010 §3). */
  removeCase(name: string): void;
  /** Removes every case (`cases clear`). */
  clearCases(): void;
  /** Saves the Run the dock shows as a zip (`download run`, ADR-0010 §7). */
  downloadRun(): void;
  /** Puts the dock's Run's quote on the clipboard as HTML and text (`copy quote`, ADR-0010 §7). */
  copyQuote(): void;
  /** Asks again for every failed result on the new trades, such as a PV01 (`pv01 retry`, ADR-0010 §6). */
  retry(): void;
  /** Acquires the trades again for the value date (`refresh`, ADR-0010 §1). */
  refresh(): void;
  /** Sets the currency the figures are shown in (`currency USD`, ADR-0010 §1). */
  setCurrency(currency: string): void;
  /** Opens an input tab, showing the inputs if they were collapsed (`open netting`, ADR-0010 §9). */
  openTab(tab: InputTab): void;
  /** Opens the trace of a measure the dock's Run priced, for the case in view, at a term or its root (`explain`, ADR-0011 §7). */
  explain(measure: string, path?: string): void;
  /** The id of the trace the dock shows in a measure's own tab, which the trace commands act on; none on Metrics. */
  traceInView(): string | undefined;
  /** Shows a tab of the dock: Metrics or a priced measure's own, which widens the dock (`open result simm`, ADR-0010 §7). */
  openResult(tab: string): void;
  /** Widens the dock over the input tabs and the grids, or puts it back (`dock full`, `dock restore`; owner 2026-11-08). */
  setDockFull(full: boolean): void;
  /** The dock's tabs for the Run it shows, for the command line to offer. */
  resultTabs(): readonly DockTab[];
  /** The registered measures, for the command line to offer. */
  measures(): readonly RegisteredMeasure[];
  /** The currencies the figures can be shown in, for the command line to offer. */
  currencies(): readonly string[];
}

const pages = new Map<string, XvaPage>();

export function registerXvaPage(panelId: string, page: XvaPage | undefined): void {
  if (page === undefined) pages.delete(panelId);
  else pages.set(panelId, page);
}

export function xvaPageOf(panelId: string | undefined): XvaPage | undefined {
  return panelId === undefined ? undefined : pages.get(panelId);
}

/**
 * `measures cva fva`: the words typed, as the ids the server takes, in the order typed and once each. A word names a
 * registered measure whatever its case (`ccr.spotcapital` is `ccr.spotCapital`); one that names none is sent as typed,
 * for the server to refuse by name.
 */
export function measuresTyped(words: string, registered: readonly string[] = []): readonly string[] {
  const ids = words.trim().split(/\s+/).filter((w) => w !== '')
    .map((w) => registered.find((id) => id.toLowerCase() === w.toLowerCase()) ?? w.toLowerCase());
  return [...new Set(ids)];
}

/**
 * The measure a word names for `explain` (ADR-0012 §6's note of 2026-11-08): a registered measure's id whatever its
 * case, or its module's name where the module registers one measure that keeps a trace (`explain simm collect` is spot
 * IM's). None where it names neither, or names a module with several, for the page to refuse by name.
 */
export function measureNamed(word: string, measures: readonly RegisteredMeasure[]): RegisteredMeasure | undefined {
  const typed = word.toLowerCase();
  const exact = measures.find((m) => m.id.toLowerCase() === typed);
  if (exact !== undefined) return exact;
  const of = measures.filter((m) => m.traced && m.module.toLowerCase() === typed);
  return of.length === 1 ? of[0] : undefined;
}

/**
 * `vary CSA-A threshold 10mm, 5mm, 0` as typed (ADR-0010 §9's note of 2026-11-08): the cell, its row and its column, then
 * the values separated by commas. The first value is the last word before the first comma. Each value goes as written,
 * for the server to read as the cell reads it; a line with no cell or no value is refused here by name.
 */
export function varyTyped(text: string): { readonly cell: string; readonly values: readonly string[] } {
  const [first = '', ...rest] = text.split(',');
  const words = first.trim().split(/\s+/).filter((w) => w !== '');
  const value = words[words.length - 1];
  if (words.length < 3 || value === undefined) throw new Error('vary needs a cell, its row and its column, and the values, such as vary CSA-A threshold 10mm, 5mm, 0.');
  const values = [value, ...rest.map((v) => v.trim())];
  if (values.some((v) => v === '')) throw new Error('vary takes values separated by commas, none of them empty.');
  return { cell: words.slice(0, -1).join(' '), values };
}
