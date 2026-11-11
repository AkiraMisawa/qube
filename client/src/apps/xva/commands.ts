import type { RegisteredMeasure, ScenarioSetView } from './contracts';
import type { DockTab } from './dock';
import type { InputTab } from './XvaInputs';

// What an open XVA workspace page offers to the commands the client still writes, by panel (ADR-0010 §9). The page
// registers them while it is mounted, so a command and the page's own button run the same function. A command the
// server reads (ADR-0016 §5, notes 27 to 29) is not here: it goes through the page's door.

export interface XvaPage {
  price(): void;
  /** The scenario of that name with every shift written (`scenario s1 jpy_ois +10bp; usdjpy -5%`, ADR-0010 §4). */
  defineScenario(name: string, shifts: string): void;
  /** Whether Price prices a scenario, as its box says (`scenario include s1`, `scenario exclude s1`). */
  includeScenario(name: string, included: boolean): void;
  /** Removes a scenario's rows (`scenario remove s1`). */
  removeScenario(name: string): void;
  /** Copies a scenario's rows under a new name, to be edited (`scenario duplicate s1 s2`). */
  duplicateScenario(name: string, copy: string): void;
  /** Removes every scenario (`scenarios clear`). */
  clearScenarios(): void;
  /** A set of scenarios at once (`scenario ladder jpy_ois -50bp..+50bp step 10bp`, `scenario cross …`, ADR-0010 §4). */
  generateScenarios(generator: 'ladder' | 'cross', text: string): void;
  /** Saves every scenario as a set, for the user alone unless shared (`scenario save rates desk`, ADR-0010 §4, C32). */
  saveScenarios(set: string, share?: 'desk' | 'all'): void;
  /** Adds a saved set's scenarios, replacing those of the same name (`scenario load rates`, `scenario load rates@ben`). */
  loadScenarios(set: string): void;
  /** The saved sets the user can see, as last read, for the command line to offer. */
  scenarioSets(): readonly ScenarioSetView[];
  /** The cases Price prices and its cap of pairs, for a generator's preview to count against. */
  pricing(): { readonly cases: number; readonly cap: number } | undefined;
  /** The workspace's scenarios, for the command line to offer. */
  scenarios(): readonly { readonly name: string; readonly shifts: number; readonly included: boolean }[];
  /** Saves the Run the dock shows as a zip (`download run`, ADR-0010 §7). */
  downloadRun(): void;
  /** Puts the dock's Run's quote on the clipboard as HTML and text (`copy quote`, ADR-0010 §7). */
  copyQuote(): void;
  /** Asks again for every failed result on the new trades, such as a PV01 (`pv01 retry`, ADR-0010 §6). */
  retry(): void;
  /** Acquires the trades again for the value date (`refresh`, ADR-0010 §1). */
  refresh(): void;
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

/**
 * Why a list of values separated by commas cannot be read, if it cannot: a comma between two digits could separate two
 * values or the thousands of one (`10,000k`), so the list is refused rather than split as a guess. The server's
 * `xva.vary` refuses it in the same words.
 */
export function ambiguousComma(text: string): string | undefined {
  const found = /[^\s,]*\d(?:,\d[^\s,]*)+/.exec(text);
  return found === null ? undefined : `${found[0]} could be one value or two: a comma separates values here, so write an amount without separators, such as 10000k or 10mm.`;
}
