import type { RegisteredMeasure } from './contracts';
import type { DockTab } from './dock';
import type { InputTab } from './XvaInputs';

// What an open XVA workspace page offers to the commands the client still writes, by panel (ADR-0010 §9). The page
// registers them while it is mounted, so a command and the page's own button run the same function. A command the
// server reads (ADR-0016 §5, notes 27 to 29) is not here: it goes through the page's door.

export interface XvaPage {
  /** Saves the Run the dock shows as a zip (`download run`, ADR-0010 §7). */
  downloadRun(): void;
  /** Puts the dock's Run's quote on the clipboard as HTML and text (`copy quote`, ADR-0010 §7). */
  copyQuote(): void;
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
