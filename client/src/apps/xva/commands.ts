import type { Completion } from '../../platform/commands/commands';
import type { CdsCatalogue, CdsCurve, RegisteredMeasure, ScenarioSetView } from './contracts';
import type { DockTab } from './dock';
import type { InputTab } from './XvaInputs';

// What an open XVA workspace page offers to commands, by panel: its Price and its measures (ADR-0010 §9). The page
// registers them while it is mounted, so a command and the page's own button run the same function.

export interface XvaPage {
  price(): void;
  /** The counterparty's CDS as written (`cds ABCH USD SNRFOR`, `cds proxy financials asia A`, `cds none`, ADR-0010 §8's note of
   * 2026-11-09); the server reads it against its catalogue and says what it chose. */
  chooseCds(text: string): void;
  /** The CDS catalogue as last read, for the command line to offer; none until it has been read. */
  cdsCatalogue(): CdsCatalogue | undefined;
  /** Sets the measures Price prices; the server refuses an unknown one, or none, by name. */
  setMeasures(measures: readonly string[]): void;
  /** B or A for every row of a grid (ADR-0010 §6); the grid the keyboard was last in when none is named. */
  setSide(side: 'before' | 'after', value: boolean, grid?: 'new' | 'existing'): void;
  /** A case per value of a cell (`vary CSA-A threshold 10mm, 5mm, 0`, ADR-0010 §3, §9). */
  vary(cell: string, values: readonly string[]): void;
  /** The edit a typed cell makes, on each row named, in the Base (`set CSA-A threshold 5mm`, ADR-0010 §9). */
  set(rows: readonly string[], column: string, text: string): void;
  /** A case per New row with A ticked, each holding that trade alone (`add trades`, docs/03 §5). */
  addTrades(): void;
  /** Removes a case with its changes, named by its title or id (`case remove`, ADR-0010 §3). */
  removeCase(name: string): void;
  /** Removes every case (`cases clear`). */
  clearCases(): void;
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
  /** Rows of the workspace as the page has read it, for the command line's examples (ADR-0006's note 4 of 2026-11-11). */
  examples(): XvaExamples;
  /** The registered measures, for the command line to offer. */
  measures(): readonly RegisteredMeasure[];
  /** The currencies the figures can be shown in, for the command line to offer. */
  currencies(): readonly string[];
}

/** A CSA and a trade of the open workspace, where the page has read one. */
export interface XvaExamples {
  readonly csa: string | undefined;
  readonly trade: string | undefined;
}

const SET_FORM = 'the rows separated by commas, a column and the value';
const VARY_FORM = 'a row and a column, then the values separated by commas';

/**
 * The hint of `set`: its form, and an example that can be typed as it is, made from the open workspace's own rows.
 * With none read yet it is the form alone, never an id no workspace has (ADR-0006's note 4 of 2026-11-11).
 */
export function setHint(examples: XvaExamples | undefined): string {
  const { csa, trade } = examples ?? { csa: undefined, trade: undefined };
  if (trade !== undefined && csa !== undefined) return `${SET_FORM}: ${trade} csa ${csa}`;
  if (csa !== undefined) return `${SET_FORM}: ${csa} threshold 5mm`;
  return trade === undefined ? SET_FORM : `${SET_FORM}: ${trade} a off`;
}

/** The hint of `vary`, as `setHint`: a CSA's threshold is the example, where the workspace has a CSA. */
export function varyHint(examples: XvaExamples | undefined): string {
  return examples?.csa === undefined ? VARY_FORM : `${VARY_FORM}: ${examples.csa} threshold 10mm, 5mm, 0`;
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
 * Why a list of values separated by commas cannot be read, if it cannot: a comma between two digits could separate two
 * values or the thousands of one (`10,000k`), so the list is refused rather than split as a guess. The server's
 * `xva.vary` refuses it in the same words.
 */
export function ambiguousComma(text: string): string | undefined {
  const found = /[^\s,]*\d(?:,\d[^\s,]*)+/.exec(text);
  return found === null ? undefined : `${found[0]} could be one value or two: a comma separates values here, so write an amount without separators, such as 10000k or 10mm.`;
}

/**
 * `vary CSA-A threshold 10mm, 5mm, 0` as typed (ADR-0010 §9's note of 2026-11-08): the cell, its row and its column, then
 * the values separated by commas. The first value is the last word before the first comma. Each value goes as written,
 * for the server to read as the cell reads it; a line with no cell or no value is refused here by name.
 */
export function varyTyped(text: string): { readonly cell: string; readonly values: readonly string[] } {
  const ambiguous = ambiguousComma(text);
  if (ambiguous !== undefined) throw new Error(ambiguous);
  const [first = '', ...rest] = text.split(',');
  const words = first.trim().split(/\s+/).filter((w) => w !== '');
  const value = words[words.length - 1];
  if (words.length < 3 || value === undefined) throw new Error('vary needs a cell, its row and its column, and the values, such as vary CSA-A threshold 10mm, 5mm, 0.');
  const values = [value, ...rest.map((v) => v.trim())];
  if (values.some((v) => v === '')) throw new Error('vary takes values separated by commas, none of them empty.');
  return { cell: words.slice(0, -1).join(' '), values };
}

/**
 * `set CSA-A threshold 5mm` and `set T-1001, T-1002 csa CSA-B` as typed (ADR-0010 §9): the rows, separated by commas,
 * then the column and the value, its last word. A value may hold a comma (`10,000k`): only the rows are a list. The
 * server names the cell and reads the value; nothing here decides what a word means.
 */
export function setTyped(text: string): { readonly rows: readonly string[]; readonly column: string; readonly text: string } {
  const words = text.trim().split(/\s+/).filter((w) => w !== '');
  let at = 0;
  while (at < words.length && words[at]?.endsWith(',') === true) at += 1;
  const rows = words.slice(0, at + 1).join('').split(',').map((r) => r.trim());
  const rest = words.slice(at + 1);
  const value = rest[rest.length - 1];
  if (rest.length < 2 || value === undefined || rows.some((r) => r === ''))
    throw new Error('set needs a row, a column and a value, such as set CSA-A threshold 5mm; rows are separated by commas.');
  return { rows, column: rest.slice(0, -1).join(' '), text: value };
}

/**
 * What `cds` offers for the word being typed (ADR-0010 §8's note of 2026-11-09), given every word typed so far: `none`,
 * `proxy` and the single names' tickers first; after `proxy`, the sectors, regions and ratings it has none of yet; after a
 * single name's word, the other words of the names it still matches. What the words choose is the server's to read; a
 * line that names nothing, or several, is refused there by name.
 */
export function cdsCompletions(typed: string, catalogue: CdsCatalogue | undefined): readonly Completion[] {
  if (catalogue === undefined) return [];
  const words = typed.trim().toLowerCase().split(/\s+/).filter((w) => w !== '');
  const is = (w: string, values: readonly string[]) => values.some((v) => v.toLowerCase() === w);
  if (words[0] === 'proxy') {
    const keys = [['sector', catalogue.sectors], ['region', catalogue.regions], ['rating', catalogue.ratings]] as const;
    return keys.filter(([, values]) => !words.some((w) => is(w, values)))
      .flatMap(([what, values]) => values.map((v) => ({ value: v, detail: `the proxy's ${what}` })));
  }
  const of = (c: CdsCurve) => [[c.ticker, c.reference], [c.currency, 'currency'], [c.seniority, 'seniority'], [c.docClause, 'doc clause']] as const;
  const last = words[words.length - 1];
  // The words before the one being typed are whole; the one being typed is whole once it is a word of a name.
  const done = last !== undefined && catalogue.singleNames.some((c) => is(last, of(c).map(([w]) => w))) ? words : words.slice(0, -1);
  const matching = catalogue.singleNames.filter((c) => done.every((w) => is(w, of(c).map(([x]) => x))));
  const offered = new Map<string, string>(done.length === 0 ? [['none', 'no CDS: CVA is refused'], ['proxy', 'a sector, a region and a rating']] : []);
  for (const c of matching) for (const [w, detail] of of(c)) if (!is(w.toLowerCase(), done) && !offered.has(w)) offered.set(w, detail);
  return [...offered].map(([value, detail]) => ({ value, detail }));
}
