import { formatAmount, isLoss } from '../../platform/grid/amounts';
import type { MetricDefinition, RegisteredMeasure } from './contracts';

// The Metrics dock as a reading of the Run (ADR-0010 §7, §10): a line per case and measure metric with Before, After
// and Diff. The figures come from the Run's retained results, never from the screen, and where there is no figure the
// cell says why in words: `not priced` (nothing failed, the side had no trade), `unavailable` (a request failed),
// `refused` (Price blocked the measure, with its reason). Never 0 for any of them. A metric its measure registers as
// shown by its Diff alone, as CVA's CVA Charge, is one column of the dock; the Run still holds both of its sides
// (§7's note of 2026-11-21).

export type CellStatus = 'priced' | 'notPriced' | 'unavailable' | 'refused' | 'pending';

export interface MetricCell {
  readonly text: string;
  readonly status: CellStatus;
  /** Said beside a side's figure, never a figure itself: spot IM's governing regime (ADR-0010 §7's note of 2026-11-08). */
  readonly note?: string | undefined;
  /** A figure shown as a loss, painted red beside its minus sign (C48). */
  readonly negative?: true;
}

export interface MetricLine {
  readonly key: string;
  /** The case the line is of; none where its module gave no rows, which then holds for every case. */
  readonly case: string | undefined;
  readonly caseTitle: string | undefined;
  /** The market the line is of (ADR-0010 §4): the base market or a scenario the Run priced; none where its module gave
   * no rows, which then holds for every market. A row written before scenarios is the base market's. */
  readonly scenario: string | undefined;
  readonly scenarioTitle: string | undefined;
  readonly measure: string;
  readonly measureTitle: string;
  readonly metric: string;
  readonly metricTitle: string;
  readonly currency: string | undefined;
  readonly before: MetricCell;
  readonly after: MetricCell;
  readonly diff: MetricCell;
  readonly tradesBefore: number | undefined;
  readonly tradesAfter: number | undefined;
  /** Why the line has no figure, where the module or Price said. */
  readonly reason: string | undefined;
  /** What the line's figures are computed on, said with them, such as `SIMM PARAMETERS DEMO`; never a figure. */
  readonly basis: string | undefined;
  /** The rate a figure computed in another currency is shown at, said once (ADR-0012 §7), such as spot IM's from USD. */
  readonly conversion: string | undefined;
  /** The trace term the line's cells open, where it is not the root: spot IM's IM-R opens at `collect`. */
  readonly term: string | undefined;
  /** Whether the dock shows the metric by its Diff alone, as its registration says (ADR-0010 §7's note of 2026-11-21). */
  readonly diffOnly: boolean;
  /** The heading of that one column, a line a tier, where the registration gives one: CVA Charge over `or Rebate`. */
  readonly heading: readonly string[] | undefined;
}

/** A column of a metric's group in the dock: one side, or the Diff. */
export type ResultSide = 'before' | 'after' | 'diff';

export const SIDE_TITLES: Readonly<Record<ResultSide, string>> = { before: 'Before', after: 'After', diff: 'Diff' };

export interface MetricsTable {
  readonly lines: readonly MetricLine[];
  /** A side no line has is not drawn, nor is the Diff (ADR-0010 §7). */
  readonly sides: { readonly before: boolean; readonly after: boolean };
  /** Said once when a side was priced nowhere, such as `After only: Before was not priced`. */
  readonly note: string | undefined;
}

/** What a module's request gave the Run: its rows, a failure, or nothing yet. */
export type ModuleResult =
  | { readonly kind: 'rows'; readonly rows: readonly Readonly<Record<string, unknown>>[] }
  | { readonly kind: 'unavailable'; readonly reason: string };

/** A priced figure: every metric is an amount (C48), grouped in threes, in whole units, and red when it is a loss. */
const figure = (value: number) => ({ text: formatAmount(value), ...(isLoss(value) ? { negative: true as const } : {}) });

const PENDING: MetricCell = { text: '…', status: 'pending' };
const UNAVAILABLE: MetricCell = { text: 'unavailable', status: 'unavailable' };
const NO_DIFF: MetricCell = { text: '', status: 'notPriced' };

/**
 * A side as the row holds it. A side that says priced and carries no figure is unavailable, never 0 (EX06). Its note is
 * said beside its figure alone: a side with no figure has nothing for it to qualify.
 */
export function side(status: unknown, value: unknown, note?: unknown): MetricCell {
  switch (status) {
    case 'priced': return typeof value === 'number' && Number.isFinite(value)
      ? { ...figure(value), status: 'priced', ...(text(note) === undefined ? {} : { note: text(note) }) }
      : UNAVAILABLE;
    case 'notPriced': return { text: 'not priced', status: 'notPriced' };
    case 'refused': return { text: 'refused', status: 'refused' };
    default: return UNAVAILABLE;
  }
}

/** The Diff exists only where both sides were priced; a one-sided or refused line has none, a failed one says so. */
export function diffOf(before: MetricCell, after: MetricCell, value: unknown): MetricCell {
  if (before.status === 'priced' && after.status === 'priced')
    return typeof value === 'number' && Number.isFinite(value) ? { ...figure(value), status: 'priced' } : UNAVAILABLE;
  if (before.status === 'pending' || after.status === 'pending') return PENDING;
  if (before.status === 'unavailable' || after.status === 'unavailable') return UNAVAILABLE;
  return NO_DIFF;
}

const text = (v: unknown) => (typeof v === 'string' && v !== '' ? v : undefined);

/** The trace term a metric's cells open, where its registration names one; none opens the trace at its root. */
const termOf = (definition: MetricDefinition | undefined): string | undefined => text(definition?.term);
/** Whether a metric's registration shows it by its Diff alone; a metric the register no longer knows shows every side. */
const diffOnlyOf = (definition: MetricDefinition | undefined): boolean => definition?.diffOnly === true;
/** A Diff-only metric's heading as registered; one that is not a line at least, each a text, heads nothing. */
const headingOf = (definition: MetricDefinition | undefined): readonly string[] | undefined => {
  const heading = definition?.heading;
  return definition?.diffOnly === true && Array.isArray(heading) && heading.length > 0 && heading.every((l) => text(l) !== undefined) ? heading : undefined;
};
const definitionOf = (measure: RegisteredMeasure | undefined, metric: string): MetricDefinition | undefined =>
  measure?.metrics.find((m) => m.id === metric);
const count = (v: unknown) => (typeof v === 'number' ? v : undefined);

/**
 * The dock's lines for the measures a Run priced, in the Run's order. `results` holds each request's answer by the id
 * of the module that makes it, and a measure reads its rows from its own request's (spot capital from XVA's); a request
 * not in it has not answered yet, so its measures' figures are pending.
 */
export function metricsTable(register: readonly RegisteredMeasure[], priced: readonly string[], results: Readonly<Record<string, ModuleResult>>): MetricsTable {
  const lines: MetricLine[] = [];
  const blank = (measure: string, measureTitle: string, metric: string, metricTitle: string, cell: MetricCell, reason: string | undefined): MetricLine => {
    const definition = definitionOf(register.find((m) => m.id === measure), metric);
    return {
    key: `${measure}.${metric}`, case: undefined, caseTitle: undefined, scenario: undefined, scenarioTitle: undefined, measure, measureTitle, metric, metricTitle, currency: undefined,
    before: cell, after: cell, diff: cell, tradesBefore: undefined, tradesAfter: undefined, reason, basis: undefined, conversion: undefined,
    term: termOf(definition), diffOnly: diffOnlyOf(definition), heading: headingOf(definition),
    };
  };

  for (const id of priced) {
    const measure = register.find((m) => m.id === id);
    if (measure === undefined) {
      lines.push(blank(id, id, id, id, UNAVAILABLE, `No measure ${id} is registered now.`));
      continue;
    }
    const result = results[measure.request];
    if (result === undefined || result.kind === 'unavailable') {
      const cell = result === undefined ? PENDING : UNAVAILABLE;
      for (const metric of measure.metrics) lines.push(blank(measure.id, measure.title, metric.id, metric.title, cell, result?.reason));
      continue;
    }
    for (const row of result.rows.filter((r) => r.measure === measure.id)) {
      const before = side(row.beforeStatus, row.before, row.beforeNote);
      const after = side(row.afterStatus, row.after, row.afterNote);
      const metric = String(row.metric);
      const scenario = text(row.scenario) ?? BASE_MARKET.key;
      lines.push({
        key: `${scenario}.${String(row.case)}.${measure.id}.${metric}`, case: String(row.case), caseTitle: String(row.caseTitle),
        scenario, scenarioTitle: text(row.scenarioTitle) ?? BASE_MARKET.title, measure: measure.id, measureTitle: measure.title,
        metric, metricTitle: String(row.metricTitle), currency: text(row.currency), before, after, diff: diffOf(before, after, row.diff),
        tradesBefore: count(row.tradesBefore), tradesAfter: count(row.tradesAfter), reason: text(row.reason), basis: text(row.basis), conversion: text(row.conversion),
        term: termOf(definitionOf(measure, metric)), diffOnly: diffOnlyOf(definitionOf(measure, metric)), heading: headingOf(definitionOf(measure, metric)),
      });
    }
  }

  return tableOf(lines);
}

/** The sides drawn and the note said for a set of lines: a side is drawn while any line has something to say on it;
 * only "not priced" everywhere hides it. */
function tableOf(lines: readonly MetricLine[]): MetricsTable {
  const drawn = (pick: (l: MetricLine) => MetricCell) => lines.length === 0 || lines.some((l) => pick(l).status !== 'notPriced');
  const sides = { before: drawn((l) => l.before), after: drawn((l) => l.after) };
  const note = !sides.before && sides.after ? 'After only: Before was not priced'
    : sides.before && !sides.after ? 'Before only: After was not priced'
      : undefined;
  return { lines, sides, note };
}

/** The base market, as the Run names it (ADR-0010 §4's note of 2026-11-09). */
export const BASE_MARKET = { key: 'base', title: 'Base market' } as const;

/**
 * The markets a Run priced, the base market first, then each scenario in the Run's order (ADR-0010 §4): what the dock's
 * scenario selector offers. A Run with no row yet, or one priced before scenarios, has the base market alone.
 */
export function marketsOf(table: MetricsTable): readonly { readonly key: string; readonly title: string }[] {
  const markets = new Map<string, string>([[BASE_MARKET.key, BASE_MARKET.title]]);
  for (const line of table.lines) if (line.scenario !== undefined && !markets.has(line.scenario)) markets.set(line.scenario, line.scenarioTitle ?? line.scenario);
  return [...markets].map(([key, title]) => ({ key, title }));
}

/**
 * The lines of one market (ADR-0010 §4, §7): the Metrics, the collapsed line and the reasons follow the scenario in view.
 * A line of no market, whose module has not answered or failed, stands in every one.
 */
export function inMarket(table: MetricsTable, scenario: string): MetricsTable {
  return tableOf(table.lines.filter((l) => l.scenario === undefined || l.scenario === scenario));
}

export interface MetricsGrid {
  /** A column group per measure metric, in the lines' order. */
  readonly columns: readonly MetricsColumn[];
  /** A row per case (ADR-0010 §7), each with its line per column; a line of no case stands in every row. */
  readonly rows: readonly { readonly key: string; readonly title: string; readonly cells: Readonly<Record<string, MetricLine>> }[];
}

/**
 * A measure metric's column group, with the sides it draws: those the table draws (§7), or for a metric shown by its Diff
 * alone the Diff, and where the table draws no Diff the one side it draws. A heading, where that metric registers one,
 * heads its one column in tiers, in place of its title over the side (§7's note of 2026-11-21).
 */
export interface MetricsColumn {
  readonly key: string;
  readonly measureTitle: string;
  readonly metricTitle: string;
  readonly sides: readonly ResultSide[];
  readonly heading: readonly string[] | undefined;
}

/** What names a column's cell, for its header and a screen reader: its heading, or its title and side. */
export const columnLabel = (column: MetricsColumn, side: ResultSide): string =>
  column.heading?.join(' ') ?? `${column.metricTitle} ${SIDE_TITLES[side]}`;

const columnKey = (line: MetricLine) => `${line.measure}.${line.metric}`;

/** The sides the table draws (ADR-0010 §7): each side some line has something to say on, and the Diff where both are. */
function drawnSides(table: MetricsTable): readonly ResultSide[] {
  return [
    ...(table.sides.before ? ['before' as const] : []),
    ...(table.sides.after ? ['after' as const] : []),
    ...(table.sides.before && table.sides.after ? ['diff' as const] : []),
  ];
}

/** The dock's grid: a row per case, a column group per measure metric. */
export function metricsGrid(table: MetricsTable): MetricsGrid {
  const drawn = drawnSides(table);
  const columns = new Map<string, MetricsColumn>();
  for (const line of table.lines)
    if (!columns.has(columnKey(line))) columns.set(columnKey(line), {
      key: columnKey(line), measureTitle: line.measureTitle, metricTitle: line.metricTitle,
      sides: line.diffOnly && drawn.includes('diff') ? ['diff'] : drawn, heading: line.heading,
    });
  const cases = new Map<string, string>();
  for (const line of table.lines) if (line.case !== undefined && !cases.has(line.case)) cases.set(line.case, line.caseTitle ?? line.case);
  // Until a module answers there is no case to name; its lines stand for all of them.
  if (cases.size === 0 && table.lines.length > 0) cases.set('', 'All cases');
  const rows = [...cases].map(([key, title]) => {
    const cells: Record<string, MetricLine> = {};
    for (const line of table.lines) if (line.case === undefined || line.case === key) cells[columnKey(line)] = line;
    return { key, title, cells };
  });
  return { columns: [...columns.values()], rows };
}

const NOT_PRICED: MetricCell = { text: 'not priced', status: 'notPriced' };

/**
 * What a case adds of a metric (ADR-0010 §7): its Diff; where the case is one-sided, its priced side, named; where
 * there is no figure, the word of a side that says why (`refused`, `unavailable`, `…`), else `not priced`. Never 0 and
 * never blank. The collapsed line and a metric shown by its Diff alone read it; Copy quote holds the same rule on the
 * server (XvaQuote), where it reads the Run alone.
 */
export function caseAdds(line: MetricLine): { readonly side: ResultSide | undefined; readonly cell: MetricCell } {
  if (line.diff.status === 'priced') return { side: 'diff', cell: line.diff };
  if (line.after.status === 'priced' && line.before.status === 'notPriced') return { side: 'after', cell: line.after };
  if (line.before.status === 'priced' && line.after.status === 'notPriced') return { side: 'before', cell: line.before };
  return { side: undefined, cell: [line.diff, line.after, line.before].find((c) => c.status !== 'priced' && c.status !== 'notPriced') ?? NOT_PRICED };
}

/**
 * What a column of a metric's group shows of a line: the side as the line holds it, or, in the one column of a metric
 * shown by its Diff alone, what the case adds, its side named beside the figure where that is not the Diff (ADR-0010
 * §7's note of 2026-11-21), as the collapsed line names it.
 */
export function shownCell(line: MetricLine, side: ResultSide): MetricCell {
  if (side !== 'diff' || !line.diffOnly) return line[side];
  const added = caseAdds(line);
  if (added.side === undefined || added.side === 'diff') return added.cell;
  const named = SIDE_TITLES[added.side];
  return { ...added.cell, note: added.cell.note === undefined ? named : `${named} ${added.cell.note}` };
}

/**
 * The collapsed dock (ADR-0010 §7): one line per case, each priced measure's headline metric as its Diff, or the priced
 * side, named, where the case is one-sided (`Base · CVA After -150.00 · FVA After -12.00`); a figure there is
 * not is its word, never 0.
 */
export function headlines(register: readonly RegisteredMeasure[], table: MetricsTable): readonly { readonly key: string; readonly text: string }[] {
  const grid = metricsGrid(table);
  return grid.rows.map((row) => {
    const parts = register.flatMap((measure) => {
      const line = row.cells[`${measure.id}.${measure.headline}`];
      if (line === undefined) return [];
      const added = caseAdds(line);
      return [added.side === undefined ? `${line.metricTitle} ${added.cell.text}` : `${line.metricTitle} ${SIDE_TITLES[added.side]} ${added.cell.text}`];
    });
    return { key: row.key, text: [row.title, ...parts].join(' · ') };
  });
}

/**
 * Each measure's trade counts, Before → After (ADR-0010 §5: `FVA 300 → 301`), from its first line that counted both.
 * A measure with a membership rule counts its members, so FVA's may differ from CVA's.
 */
export function tradeCounts(table: MetricsTable): readonly string[] {
  const counts = new Map<string, string>();
  for (const l of table.lines)
    if (!counts.has(l.measure) && l.tradesBefore !== undefined && l.tradesAfter !== undefined)
      counts.set(l.measure, `${l.measureTitle} ${l.tradesBefore} → ${l.tradesAfter}`);
  return [...counts.values()];
}

/**
 * Why lines have no figure, said once per measure and reason under the Metrics (ADR-0010 §7), with the word of the
 * worst side: `unavailable` (a request failed, said as a failure), else `refused`, else `not priced`, as when a side
 * had no trade in an IM scope. A side with no figure is never said to have failed when nothing did.
 */
export function reasons(table: MetricsTable): readonly { readonly key: string; readonly text: string; readonly failed: boolean }[] {
  const said = new Map<string, { readonly key: string; readonly text: string; readonly failed: boolean }>();
  for (const l of table.lines) {
    if (l.reason === undefined) continue;
    const key = `${l.measure}:${l.reason}`;
    if (said.has(key)) continue;
    const statuses = [l.before.status, l.after.status];
    const word = statuses.includes('unavailable') ? 'unavailable' : statuses.includes('refused') ? 'refused' : 'not priced';
    said.set(key, { key, text: `${l.measureTitle} ${word}: ${l.reason}`, failed: word === 'unavailable' });
  }
  return [...said.values()];
}

/** The measures a Run priced, from its input. A Run priced before the measures register says none. */
export function pricedMeasures(input: Readonly<Record<string, unknown>>): readonly string[] | undefined {
  const measures = input.measures;
  return typeof measures === 'string' ? measures.split(' ').filter((m) => m !== '') : undefined;
}

/**
 * What Price will run (ADR-0010 §1, §3, §4): `Price CVA FVA · 1 case`, `Price CVA FVA · 3 cases × 3 markets` with two
 * scenarios included beside the base market, and `Price After only · CVA FVA · 1 case` when no trade is in Before,
 * since that side will not be priced. With neither side holding a trade, Price is refused.
 */
export function priceLabel(register: readonly RegisteredMeasure[], selected: readonly string[], cases: number,
  sides: { readonly before: boolean; readonly after: boolean } = { before: true, after: true }, markets = 1): string {
  const titles = selected.map((id) => register.find((m) => m.id === id)?.title ?? id);
  const only = sides.before && !sides.after ? 'Before only · ' : !sides.before && sides.after ? 'After only · ' : '';
  const crossed = markets === 1 ? '' : ` × ${markets} markets`;
  return `Price ${only}${titles.join(' ')} · ${cases} ${cases === 1 ? 'case' : 'cases'}${crossed}`;
}
