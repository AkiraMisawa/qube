import type { RegisteredMeasure } from './contracts';

// The Metrics dock as a reading of the Run (ADR-0010 §7, §10): a line per case and measure metric with Before, After
// and Diff. The figures come from the Run's retained results, never from the screen, and where there is no figure the
// cell says why in words: `not priced` (nothing failed, the side had no trade), `unavailable` (a request failed),
// `refused` (Price blocked the measure, with its reason). Never 0 for any of them.

export type CellStatus = 'priced' | 'notPriced' | 'unavailable' | 'refused' | 'pending';

export interface MetricCell {
  readonly text: string;
  readonly status: CellStatus;
  /** Said beside a side's figure, never a figure itself: spot IM's governing regime (ADR-0010 §7's note of 2026-11-08). */
  readonly note?: string | undefined;
}

export interface MetricLine {
  readonly key: string;
  /** The case the line is of; none where its module gave no rows, which then holds for every case. */
  readonly case: string | undefined;
  readonly caseTitle: string | undefined;
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
}

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

const money = new Intl.NumberFormat('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });

const PENDING: MetricCell = { text: '…', status: 'pending' };
const UNAVAILABLE: MetricCell = { text: 'unavailable', status: 'unavailable' };
const NO_DIFF: MetricCell = { text: '', status: 'notPriced' };

/**
 * A side as the row holds it. A side that says priced and carries no figure is unavailable, never 0 (EX06). Its note is
 * said beside its figure alone: a side with no figure has nothing for it to qualify.
 */
function side(status: unknown, value: unknown, note?: unknown): MetricCell {
  switch (status) {
    case 'priced': return typeof value === 'number' && Number.isFinite(value)
      ? { text: money.format(value), status: 'priced', ...(text(note) === undefined ? {} : { note: text(note) }) }
      : UNAVAILABLE;
    case 'notPriced': return { text: 'not priced', status: 'notPriced' };
    case 'refused': return { text: 'refused', status: 'refused' };
    default: return UNAVAILABLE;
  }
}

/** The Diff exists only where both sides were priced; a one-sided or refused line has none, a failed one says so. */
function diffOf(before: MetricCell, after: MetricCell, value: unknown): MetricCell {
  if (before.status === 'priced' && after.status === 'priced')
    return typeof value === 'number' && Number.isFinite(value) ? { text: money.format(value), status: 'priced' } : UNAVAILABLE;
  if (before.status === 'pending' || after.status === 'pending') return PENDING;
  if (before.status === 'unavailable' || after.status === 'unavailable') return UNAVAILABLE;
  return NO_DIFF;
}

const text = (v: unknown) => (typeof v === 'string' && v !== '' ? v : undefined);

/** The trace term a metric's cells open, where its registration names one; none opens the trace at its root. */
const termOf = (measure: RegisteredMeasure | undefined, metric: string): string | undefined =>
  text(measure?.metrics.find((m) => m.id === metric)?.term);
const count = (v: unknown) => (typeof v === 'number' ? v : undefined);

/**
 * The dock's lines for the measures a Run priced, in the Run's order. `results` holds each request's answer by the id
 * of the module that makes it, and a measure reads its rows from its own request's (spot capital from XVA's); a request
 * not in it has not answered yet, so its measures' figures are pending.
 */
export function metricsTable(register: readonly RegisteredMeasure[], priced: readonly string[], results: Readonly<Record<string, ModuleResult>>): MetricsTable {
  const lines: MetricLine[] = [];
  const blank = (measure: string, measureTitle: string, metric: string, metricTitle: string, cell: MetricCell, reason: string | undefined): MetricLine => ({
    key: `${measure}.${metric}`, case: undefined, caseTitle: undefined, measure, measureTitle, metric, metricTitle, currency: undefined,
    before: cell, after: cell, diff: cell, tradesBefore: undefined, tradesAfter: undefined, reason, basis: undefined, conversion: undefined,
    term: termOf(register.find((m) => m.id === measure), metric),
  });

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
      lines.push({
        key: `${String(row.case)}.${measure.id}.${metric}`, case: String(row.case), caseTitle: String(row.caseTitle), measure: measure.id, measureTitle: measure.title,
        metric, metricTitle: String(row.metricTitle), currency: text(row.currency), before, after, diff: diffOf(before, after, row.diff),
        tradesBefore: count(row.tradesBefore), tradesAfter: count(row.tradesAfter), reason: text(row.reason), basis: text(row.basis), conversion: text(row.conversion),
        term: termOf(measure, metric),
      });
    }
  }

  // A side is drawn while any line has something to say on it; only "not priced" everywhere hides it.
  const drawn = (pick: (l: MetricLine) => MetricCell) => lines.length === 0 || lines.some((l) => pick(l).status !== 'notPriced');
  const sides = { before: drawn((l) => l.before), after: drawn((l) => l.after) };
  const note = !sides.before && sides.after ? 'After only: Before was not priced'
    : sides.before && !sides.after ? 'Before only: After was not priced'
      : undefined;
  return { lines, sides, note };
}

export interface MetricsGrid {
  /** A column group per measure metric, in the lines' order. */
  readonly columns: readonly { readonly key: string; readonly measureTitle: string; readonly metricTitle: string }[];
  /** A row per case (ADR-0010 §7), each with its line per column; a line of no case stands in every row. */
  readonly rows: readonly { readonly key: string; readonly title: string; readonly cells: Readonly<Record<string, MetricLine>> }[];
}

const columnKey = (line: MetricLine) => `${line.measure}.${line.metric}`;

/** The dock's grid: a row per case, a column group per measure metric. */
export function metricsGrid(table: MetricsTable): MetricsGrid {
  const columns = new Map<string, MetricsGrid['columns'][number]>();
  for (const line of table.lines)
    if (!columns.has(columnKey(line))) columns.set(columnKey(line), { key: columnKey(line), measureTitle: line.measureTitle, metricTitle: line.metricTitle });
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
      if (line.diff.status === 'priced') return [`${line.metricTitle} Diff ${line.diff.text}`];
      if (line.after.status === 'priced' && line.before.status === 'notPriced') return [`${line.metricTitle} After ${line.after.text}`];
      if (line.before.status === 'priced' && line.after.status === 'notPriced') return [`${line.metricTitle} Before ${line.before.text}`];
      const word = [line.diff, line.after, line.before].find((c) => c.status !== 'priced' && c.status !== 'notPriced') ?? line.diff;
      return [`${line.metricTitle} ${word.text === '' ? 'not priced' : word.text}`];
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
 * What Price will run (ADR-0010 §1, §3): `Price CVA FVA · 1 case`, and `Price After only · CVA FVA · 1 case` when no
 * trade is in Before, since that side will not be priced. With neither side holding a trade, Price is refused.
 */
export function priceLabel(register: readonly RegisteredMeasure[], selected: readonly string[], cases: number,
  sides: { readonly before: boolean; readonly after: boolean } = { before: true, after: true }): string {
  const titles = selected.map((id) => register.find((m) => m.id === id)?.title ?? id);
  const only = sides.before && !sides.after ? 'Before only · ' : !sides.before && sides.after ? 'After only · ' : '';
  return `Price ${only}${titles.join(' ')} · ${cases} ${cases === 1 ? 'case' : 'cases'}`;
}
