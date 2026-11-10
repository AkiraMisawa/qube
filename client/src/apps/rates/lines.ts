import type { CellState, GridColumn, GridHeaderGroup } from '@ex-grid/core';
import { actionColumn, gridHeaderRequiredPx } from '@ex-grid/core';
import { parseBoolean, parseNumber, type Parsed } from '../xva/editing';
import { amountTone, formatAmount } from '../../platform/grid/amounts';
import type { LineView, LiveFrame, PeriodView } from './contracts';

// The line grid's model (C40, C43): a line's flat fields as columns, the second level of each leg shown on
// request, and the latest figures beside them. Only the form of what was typed is checked here; whether the
// terms make a line is the server's, and shows on the row.

/** The figures a row shows, and what they were taken against. */
export interface LineFigures {
  readonly status: 'priced' | 'failed' | 'notPriced';
  readonly pv?: number;
  readonly pvCurrency?: string;
  /** The solved leg's quote: a par rate (fixed leg) or a fair spread (floating leg), in percent. */
  readonly par?: number;
  readonly parMeasure?: string;
  readonly message?: string;
  /** The input revision and the market the figures were calculated on. */
  readonly inputRevision: number;
  readonly snapshotId: string;
  readonly mode: string;
}

/** One row of the line grid: the line, and its figures if a frame has any. */
export interface LineRow {
  readonly line: string;
  readonly view: LineView;
  readonly figures: LineFigures | undefined;
  /** Whether the figures were calculated on an older revision than the row shows. */
  readonly figuresStale: boolean;
}

const fail = (message: string): Parsed => ({ ok: false, message });
const empty = (text: string) => text.trim() === '';

const TENOR = /^[1-9]\d{0,2}[dwmy]$/i;
const DATE = /^\d{4}-\d{2}-\d{2}$/;

function dateOrTenor(text: string, allowEmpty: boolean): Parsed {
  if (empty(text)) return allowEmpty ? { ok: true, value: null } : fail('Type a date (yyyy-mm-dd) or a tenor such as 5y.');
  const t = text.trim();
  if (DATE.test(t) || TENOR.test(t)) return { ok: true, value: t.toLowerCase() };
  return fail('Type a date (yyyy-mm-dd) or a tenor such as 5y.');
}

function choice(options: Readonly<Record<string, string>>, help: string) {
  return (text: string): Parsed => {
    if (empty(text)) return { ok: true, value: null };
    const value = options[text.trim().toLowerCase()];
    return value === undefined ? fail(help) : { ok: true, value };
  };
}

function number(text: string): Parsed {
  if (empty(text)) return { ok: true, value: null };
  const n = parseNumber(text);
  return n === undefined ? fail('Type a number, such as 1,000,000 or 0.25, or leave it empty.') : { ok: true, value: n };
}

/** A notional is shown and typed in millions (owner, 2026-11-04); the server keeps it in units of its currency. */
export const MILLION = 1e6;

/**
 * Reads a notional as typed: a bare number is millions (`1000` is a billion), and `k`, `m` or `bn` after it says its
 * own scale (`3bn`, `500mm`). A bare number of a million or more would be a trillion or more: almost surely a full
 * amount typed out of habit, so it is refused rather than taken as millions (spine 1).
 */
export function parseNotional(text: string): number | undefined | 'fullAmount' {
  // `mm`, never `m`: `m` is months wherever words are read by form (ADR-0008 §2), so it is never millions here either.
  const m = /^(.+?)\s*(k|mm|bn)?$/i.exec(text.trim());
  if (m === null) return undefined;
  const n = parseNumber(m[1]!);
  if (n === undefined || n <= 0) return undefined;
  if (m[2] === undefined) return n >= MILLION ? 'fullAmount' : n * MILLION;
  return n * { k: 1e3, mm: 1e6, bn: 1e9 }[m[2].toLowerCase() as 'k' | 'mm' | 'bn'];
}

const NOTIONAL_HELP = 'Type the notional in millions (1000 is a billion), or with its scale: 3bn, 500mm.';

function notional(text: string): Parsed {
  if (empty(text)) return { ok: true, value: null };
  const n = parseNotional(text);
  if (n === 'fullAmount') return fail(`That reads as ${millions.format(Number(text.replaceAll(',', '')))} million. ${NOTIONAL_HELP}`);
  return n === undefined ? fail(NOTIONAL_HELP) : { ok: true, value: n };
}

function whole(text: string): Parsed {
  if (empty(text)) return { ok: true, value: null };
  const n = parseNumber(text);
  return n === undefined || !Number.isInteger(n) || n < 0 ? fail('Type a whole number of days, or leave it empty.') : { ok: true, value: n };
}

function flag(text: string): Parsed {
  if (empty(text)) return { ok: true, value: null };
  const b = parseBoolean(text);
  return b === undefined ? fail('Type TRUE or FALSE.') : { ok: true, value: b };
}

function frequency(allowEmpty: boolean) {
  return (text: string): Parsed => {
    if (empty(text)) return allowEmpty ? { ok: true, value: null } : fail('Type 1m, 2m, 3m, 6m or 12m.');
    const t = text.trim().toLowerCase();
    return ['1m', '2m', '3m', '6m', '12m'].includes(t) ? { ok: true, value: t } : fail('Type 1m, 2m, 3m, 6m or 12m.');
  };
}

const code = (what: string) => (text: string): Parsed => {
  if (empty(text)) return { ok: true, value: null };
  const t = text.trim().toUpperCase();
  return /^[A-Z0-9_-]+$/.test(t) ? { ok: true, value: t } : fail(`Type ${what} in letters and digits.`);
};

/** The quotation a leg's Rate/Strike is given in (C40). The wire names are the server's enum. */
export const quoteKinds: Readonly<Record<string, string>> = {
  fixed: 'FixedRatePercent', fixedratepercent: 'FixedRatePercent',
  spread: 'FloatSpreadPercent', floatspreadpercent: 'FloatSpreadPercent',
  strike: 'StrikePercent', strikepercent: 'StrikePercent',
  fxpts: 'FxForwardPoints', fxforwardpoints: 'FxForwardPoints',
  atm: 'AtmOffsetBp', atmoffsetbp: 'AtmOffsetBp',
};

const quoteLabel: Readonly<Record<string, string>> = {
  FixedRatePercent: 'Fixed %', FloatSpreadPercent: 'Spread %', StrikePercent: 'Strike %', FxForwardPoints: 'FX pts', AtmOffsetBp: 'ATM ± bp',
};

/** The basic terms of a leg (C40), in display order. */
export const legBasic = ['currency', 'index', 'notional', 'quoteKind', 'quote', 'paymentFrequency', 'resetFrequency', 'floating'] as const;
/** The second level of a leg's terms (C43), collapsed until asked for. */
export const legDetail = ['dayCount', 'adjustment', 'dailyAverage', 'observationShift', 'lookback', 'paymentDelay', 'inArrears', 'gearing', 'currentReset'] as const;

const legParsers: Readonly<Record<string, (text: string) => Parsed>> = {
  currency: code('a currency'),
  index: code('an index'),
  notional,
  quoteKind: choice(quoteKinds, 'Type fixed, spread, strike, fxpts or atm, or leave it empty for par.'),
  quote: number,
  paymentFrequency: frequency(false),
  resetFrequency: frequency(true),
  floating: flag,
  dayCount: choice({ 'act/360': 'Act360', act360: 'Act360', 'act/365f': 'Act365Fixed', act365f: 'Act365Fixed', act365fixed: 'Act365Fixed' }, 'Type ACT/360 or ACT/365F, or leave it empty for the profile\'s.'),
  adjustment: choice({ mf: 'ModifiedFollowing', modifiedfollowing: 'ModifiedFollowing', f: 'Following', following: 'Following', p: 'Preceding', preceding: 'Preceding', u: 'Unadjusted', unadjusted: 'Unadjusted' }, 'Type MF, F, P or U, or leave it empty for the profile\'s.'),
  dailyAverage: flag,
  observationShift: whole,
  lookback: whole,
  paymentDelay: whole,
  inArrears: flag,
  gearing: number,
  currentReset: number,
};

/** How each editable line field reads what was typed into its cell. */
export function parserOf(field: string): ((text: string) => Parsed) | undefined {
  switch (field) {
    case 'start': return (t) => dateOrTenor(t, true);
    case 'end': return (t) => dateOrTenor(t, false);
    case 'direction': return choice({ p: 'pay', pay: 'pay', r: 'receive', receive: 'receive', rec: 'receive' }, 'Type P (pay) or R (receive).');
    case 'collateral': return (t) => (empty(t) ? { ok: true, value: null } : { ok: true, value: t.trim() });
    case 'mandatoryBreak': return (t) => (empty(t) ? { ok: true, value: null } : DATE.test(t.trim()) ? { ok: true, value: t.trim() } : fail('Type a date (yyyy-mm-dd), or leave it empty.'));
    case 'risk': return (t) => { const b = parseBoolean(t); return b === undefined ? fail('Type TRUE or FALSE.') : { ok: true, value: b }; };
    default: {
      const m = /^leg[12]\.(.+)$/.exec(field);
      return m === null ? undefined : legParsers[m[1] as string];
    }
  }
}

/** How each editable cashflow field reads what was typed. */
export function periodParserOf(field: string): ((text: string) => Parsed) | undefined {
  switch (field) {
    case 'accrualStart': case 'accrualEnd': case 'paymentDate':
      return (t) => (DATE.test(t.trim()) ? { ok: true, value: t.trim() } : fail('Type a date (yyyy-mm-dd).'));
    case 'notional': return (t) => (empty(t) ? fail(NOTIONAL_HELP) : notional(t));
    case 'rate': return number;
    default: return undefined;
  }
}

const millions = new Intl.NumberFormat('en-US', { maximumFractionDigits: 6 });
/** A notional in millions, as typed: 1,000 for a billion, 2.5 for two and a half million. */
const inMillions = (v: unknown) => millions.format((v as number) / MILLION);
const pct = new Intl.NumberFormat('en-US', { minimumFractionDigits: 5, maximumFractionDigits: 5 });
const plain = (v: unknown) => (typeof v === 'boolean' ? (v ? 'TRUE' : 'FALSE') : String(v));

/**
 * A column as wide as its values were laid out for (a billion notional), and never narrower than its header reads
 * whole, charged as ExGrid charges it (ADR-0016). A bare width cut headers to "P…". The line grid's headers carry no
 * column menu and no sort mark (ADR-0015 §4), so neither band is charged.
 */
const fitted = (header: string, values: number) =>
  Math.max(values, gridHeaderRequiredPx(header, { density: 'compact', menuButton: false, sortable: false }));

const field = (name: string, header: string, type: GridColumn<LineRow>['type'], width: number, format?: (v: unknown) => string): GridColumn<LineRow> => ({
  name, header, type, width: fitted(header, width), editable: true, queryable: false, value: (row) => row.view.fields[name] ?? undefined, format: format ?? plain,
});

const legHeaders: Readonly<Record<string, readonly [string, GridColumn<LineRow>['type'], number, ((v: unknown) => string)?]>> = {
  currency: ['Ccy', 'text', 56],
  index: ['Index', 'text', 72],
  notional: ['Notional (mm)', 'number', 90, inMillions],
  quoteKind: ['Quote type', 'text', 84, (v) => quoteLabel[v as string] ?? String(v)],
  quote: ['Rate/Strike', 'number', 92],
  paymentFrequency: ['Pay freq', 'text', 70],
  resetFrequency: ['Reset freq', 'text', 76],
  floating: ['Float', 'boolean', 56],
  dayCount: ['Day count', 'text', 90],
  adjustment: ['BDC', 'text', 120],
  dailyAverage: ['Daily avg', 'boolean', 72],
  observationShift: ['Obs shift', 'number', 70],
  lookback: ['Lookback', 'number', 72],
  paymentDelay: ['Pay delay', 'number', 72],
  inArrears: ['In arrears', 'boolean', 78],
  gearing: ['Gearing', 'number', 70],
  currentReset: ['Cur. reset', 'number', 78],
};

/** How far a leg is opened (ADR-0015 §2): one line, its basic terms (C40), or those and its conventions (C43). */
export type LegLevel = 'summary' | 'basic' | 'conventions';

/** What the user opened in the line grid (ADR-0015): the trade's second terms, and how far each leg is open. */
export interface LineLayout {
  readonly trade: boolean;
  readonly leg1: LegLevel;
  readonly leg2: LegLevel;
}

/** Everything folded: how a page first shows its lines, at every width (ADR-0015). */
export const FOLDED: LineLayout = { trade: false, leg1: 'summary', leg2: 'summary' };

/** The Header Group ids the grid names in `onHeaderGroupToggled` (ExGrid ADR-0400). */
export const GROUPS = {
  trade: 'trade',
  leg1: 'leg1', leg2: 'leg2',
  leg1Conventions: 'leg1.conventions', leg2Conventions: 'leg2.conventions',
} as const;

/** The layout after one of the grid's toggles. Closing a leg closes its conventions with it. */
export function toggled(layout: LineLayout, toggle: { readonly id: string; readonly expanded: boolean }): LineLayout {
  switch (toggle.id) {
    case GROUPS.trade: return { ...layout, trade: toggle.expanded };
    case GROUPS.leg1: return { ...layout, leg1: toggle.expanded ? 'basic' : 'summary' };
    case GROUPS.leg2: return { ...layout, leg2: toggle.expanded ? 'basic' : 'summary' };
    case GROUPS.leg1Conventions: return { ...layout, leg1: toggle.expanded ? 'conventions' : 'basic' };
    case GROUPS.leg2Conventions: return { ...layout, leg2: toggle.expanded ? 'conventions' : 'basic' };
    default: throw new Error(`The grid toggled a Header Group it was not given: ${toggle.id}.`);
  }
}

/** The layout a double click on a folded column opens: a leg's line opens the leg, its overrides its conventions. */
export function openedAt(layout: LineLayout, column: string): LineLayout | undefined {
  const m = /^leg([12])\.(summary|overrides)$/.exec(column);
  if (m === null) return undefined;
  return { ...layout, [`leg${m[1]}`]: m[2] === 'summary' ? 'basic' : 'conventions' };
}

/** The leg's terms the one-line summary reads, so a value set in one of them is not hidden by folding the leg. */
const SUMMARIZED: ReadonlySet<string> = new Set(['currency', 'notional', 'index', 'quoteKind', 'quote', 'paymentFrequency', 'floating']);

const scaled = (v: number) => (v >= 1e9 ? `${millions.format(v / 1e9)}bn` : `${millions.format(v / MILLION)}mm`);
const spread = new Intl.NumberFormat('en-US', { maximumFractionDigits: 5, signDisplay: 'always' });
const fixedRate = new Intl.NumberFormat('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 5 });

/**
 * A leg in one line, as a trader reads it (ADR-0015 §1): its currency and notional in the scale they are typed in, what
 * it pays, and how often. Leg 2 leaves out a currency and notional that are leg 1's, as on every IRS. A term the line
 * does not have reads `?`, never a made-up value: such a line also shows its issue.
 */
export function legSummary(fields: Readonly<Record<string, unknown>>, leg: 1 | 2): string {
  const of = (term: string) => fields[`leg${leg}.${term}`];
  const quote = of('quote');
  const kind = of('quoteKind');
  const floating = of('floating') === true;
  const solved = quote === null || quote === undefined;
  const pays = kind === 'AtmOffsetBp' && !solved ? `ATM${spread.format(quote as number)}bp`
    : kind === 'FxForwardPoints' && !solved ? `${String(quote)} pts`
    : floating ? `${String(of('index') ?? '?')} ${solved ? '+fair' : `${spread.format(quote as number)}%`}`
    : `Fixed ${solved ? 'par' : `${fixedRate.format(quote as number)}%`}`;
  const notional = of('notional');
  const sameAsLeg1 = leg === 2 && fields['leg1.currency'] === of('currency') && fields['leg1.notional'] === notional;
  const size = sameAsLeg1 ? '' : `${String(of('currency') ?? '?')} ${typeof notional === 'number' ? scaled(notional) : '?'} `;
  return `${size}${pays} ${String(of('paymentFrequency') ?? '?')}`;
}

/** A leg's second-level terms that override its convention profile (C43), in words, or `Profile` when none does. */
export function legOverrides(fields: Readonly<Record<string, unknown>>, leg: 1 | 2): string {
  const set = legDetail.flatMap((term) => {
    const value = fields[`leg${leg}.${term}`];
    if (value === null || value === undefined) return [];
    const [header, , , format] = legHeaders[term]!;
    return [`${header} ${format === undefined ? plain(value) : format(value)}`];
  });
  return set.length === 0 ? 'Profile' : set.join(', ');
}

const folded = (name: string, header: string, read: (row: LineRow) => string, maxPx: number): GridColumn<LineRow> => ({
  name, header, type: 'text', width: { auto: true, minPx: fitted(header, 0), maxPx }, queryable: false, value: read, format: plain,
});

function legColumns(leg: 1 | 2, level: LegLevel): GridColumn<LineRow>[] {
  if (level === 'summary') return [folded(`leg${leg}.summary`, 'Summary', (row) => legSummary(row.view.fields, leg), 280)];
  const terms = [...legBasic, ...(level === 'conventions' ? legDetail : [])].map((f) => {
    const [header, type, width, format] = legHeaders[f]!;
    return field(`leg${leg}.${f}`, header, type, width, format);
  });
  return level === 'conventions' ? terms : [...terms, folded(`leg${leg}.overrides`, 'Overrides', (row) => legOverrides(row.view.fields, leg), 240)];
}

const figure = (name: string, header: string, type: GridColumn<LineRow>['type'], width: number, read: (f: LineFigures) => unknown, format: (v: unknown) => string,
  tone?: GridColumn<LineRow>['tone']): GridColumn<LineRow> => ({
  name, header, type, width: fitted(header, width), queryable: false, value: (row) => (row.figures === undefined ? undefined : read(row.figures) ?? undefined), format,
  ...(tone === undefined ? {} : { tone }),
});

export const ACTIONS = 'Actions';

/** The trade's second terms, behind the Trade group's toggle (ADR-0015 §3). */
const TRADE_MORE = ['collateral', 'mandatoryBreak', 'risk'] as const;

/** The line grid's columns for a layout. Made once per layout (ExGrid ADR-0003). */
export function lineColumns(layout: LineLayout): readonly GridColumn<LineRow>[] {
  return [
    // Pinned: which line, and its cashflows, stay in view however far the terms scroll. Both sized to their content.
    // Removing a line is in its context menu (ADR-0015 §5).
    { name: 'line', header: 'Line', type: 'text', width: { auto: true, minPx: 40, maxPx: 72 }, queryable: false, value: (row) => row.line, format: plain },
    actionColumn<LineRow>(ACTIONS, [{ name: 'cashflows', label: 'Flows' }], { header: '', width: { auto: true, minPx: 40, maxPx: 120 } }),
    // The inputs, left to right as a trade is read: the trade, then each leg.
    field('product', 'Product', 'text', 64, (v) => String(v).toUpperCase()),
    field('start', 'Start', 'text', 96),
    field('end', 'End', 'text', 96),
    field('direction', 'P/R', 'text', 52, (v) => (v === 'pay' ? 'Pay' : v === 'receive' ? 'Rec' : String(v))),
    ...(layout.trade ? [field('collateral', 'Collateral', 'text', 90), field('mandatoryBreak', 'Break', 'text', 96), field('risk', 'Risk', 'boolean', 56)] : []),
    ...legColumns(1, layout.leg1),
    ...legColumns(2, layout.leg2),
    // The outputs after the inputs (owner, 2026-11-04): what was calculated, then whether and why a line was not.
    figure('pv', 'PV', 'number', 140, (f) => f.pv, formatAmount, amountTone),
    figure('pvCurrency', 'Ccy', 'text', 44, (f) => f.pvCurrency, plain),
    figure('par', 'Par %', 'number', 84, (f) => f.par, (v) => pct.format(v as number)),
    figure('status', 'Status', 'text', 82, (f) => f.status, plain),
    { name: 'message', header: 'Issues', type: 'text', width: 320, queryable: false, value: (row) => message(row), format: plain },
  ];
}

/** How many tiers of Header Groups stand over the columns: one, and two while a leg is open (ADR-0015 §2). */
export const headerTiers = (layout: LineLayout): 1 | 2 => (layout.leg1 !== 'summary' || layout.leg2 !== 'summary' ? 2 : 1);

/** How many columns from the left stay in place: the line and its Flows. */
export const PINNED_COLUMNS = 2;

/** How many values the user set each folded group hides, on any line (ExGrid ADR-0400's hidden mark). */
export interface HiddenEdits {
  readonly trade: number;
  /** Set in a leg's terms its one-line summary does not read. */
  readonly leg1: number;
  readonly leg2: number;
  /** Set in a leg's second level (C43). */
  readonly leg1Conventions: number;
  readonly leg2Conventions: number;
}

/**
 * How many values the user set hide in each folded group, on any line (owner, 2026-10-27): folding only hides them, and
 * they are still priced, so the group says so with ExGrid's hidden mark.
 */
export function hiddenEdits(lines: readonly LineView[]): HiddenEdits {
  const count = (fields: readonly string[]) => lines.reduce((n, l) => n + fields.filter((f) => l.userFields.includes(f)).length, 0);
  const unsummarized = (leg: 1 | 2) => [...legBasic, ...legDetail].filter((f) => !SUMMARIZED.has(f)).map((f) => `leg${leg}.${f}`);
  return {
    trade: count([...TRADE_MORE]),
    leg1: count(unsummarized(1)), leg2: count(unsummarized(2)),
    leg1Conventions: count(legDetail.map((f) => `leg1.${f}`)), leg2Conventions: count(legDetail.map((f) => `leg2.${f}`)),
  };
}

const NONE_HIDDEN: HiddenEdits = { trade: 0, leg1: 0, leg2: 0, leg1Conventions: 0, leg2Conventions: 0 };
const marked = (count: number) => (count > 0 ? { hiddenMark: { state: 'modified' as const, reason: `${count} field(s) you set in hidden columns` } } : {});

/**
 * The Header Groups over the columns (ExGrid ADR-0032): the trade, each leg, then the result. The trade and each leg draw
 * ExGrid's toggle (ADR-0400). An open leg has a second tier under it, its basic terms and its conventions, which have a
 * toggle of their own (ADR-0015 §2); while one is there, the groups with no level under them stand both tiers tall.
 */
export function lineHeaderGroups(layout: LineLayout, hidden: HiddenEdits = NONE_HIDDEN): readonly GridHeaderGroup[] {
  const full = headerTiers(layout) === 2 ? { tier: 2, tierSpan: 2 } : {};
  const leg = (n: 1 | 2): GridHeaderGroup[] => {
    const level = n === 1 ? layout.leg1 : layout.leg2;
    const id = n === 1 ? GROUPS.leg1 : GROUPS.leg2;
    const columns = legColumns(n, level).map((c) => c.name);
    if (level === 'summary') return [{ id, label: `Leg ${n}`, expanded: false, columns, ...full, ...marked(n === 1 ? hidden.leg1 : hidden.leg2) }];
    const open = level === 'conventions';
    return [
      { id, label: `Leg ${n}`, expanded: true, columns, tier: 2 },
      { label: 'Basic', columns: legBasic.map((f) => `leg${n}.${f}`), tier: 1 },
      {
        id: n === 1 ? GROUPS.leg1Conventions : GROUPS.leg2Conventions, label: 'Conventions', expanded: open, tier: 1,
        columns: open ? legDetail.map((f) => `leg${n}.${f}`) : [`leg${n}.overrides`],
        ...(open ? {} : marked(n === 1 ? hidden.leg1Conventions : hidden.leg2Conventions)),
      },
    ];
  };
  return [
    {
      id: GROUPS.trade, label: 'Trade', expanded: layout.trade, ...full,
      columns: ['product', 'start', 'end', 'direction', ...(layout.trade ? TRADE_MORE : [])], ...(layout.trade ? {} : marked(hidden.trade)),
    },
    ...leg(1),
    ...leg(2),
    { label: 'Result', columns: ['pv', 'pvCurrency', 'par'], ...full },
  ];
}

function message(row: LineRow): string | undefined {
  const parts = [...row.view.issues, ...row.view.held];
  if (row.figures?.message !== undefined) parts.push(row.figures.message);
  return parts.length === 0 ? undefined : parts.join(' · ');
}

const RESULT = new Set(['status', 'pv', 'pvCurrency', 'par']);

/** One line's result as the strip above the grid shows it, whatever the grid has scrolled to (owner, 2026-11-04). */
export interface LineResult {
  readonly line: string;
  /** `none` until a frame has figures for the line. */
  readonly status: LineFigures['status'] | 'none';
  readonly pv?: string;
  readonly par?: string;
  /** What the par figure is: the fixed leg's par rate or the floating leg's fair spread. */
  readonly parLabel?: string;
  /** Whether the figures were calculated on an older revision than the line shows: never shown as current. */
  readonly stale: boolean;
  readonly issues?: string;
}

export function lineResult(row: LineRow): LineResult {
  const f = row.figures;
  const issues = message(row);
  return {
    line: row.line,
    status: f === undefined ? 'none' : f.status,
    stale: row.figuresStale,
    ...(f?.pv === undefined ? {} : { pv: `${formatAmount(f.pv)}${f.pvCurrency === undefined ? '' : ` ${f.pvCurrency}`}` }),
    ...(f?.par === undefined ? {} : { par: `${pct.format(f.par)}%`, parLabel: f.parMeasure === 'fairSpread' ? 'Fair spread' : 'Par rate' }),
    ...(issues === undefined ? {} : { issues }),
  };
}

/** A cell's state: the user's edits, held edits, a line that cannot be priced, and figures of an older revision. */
export function lineCellState(row: LineRow, column: GridColumn<LineRow>): CellState {
  if (RESULT.has(column.name)) {
    if (row.figures === undefined) return 'normal';
    if (row.figuresStale) return 'stale';
    return row.figures.status === 'priced' ? 'normal' : 'error';
  }
  if (column.name === 'message') return message(row) === undefined ? 'normal' : 'error';
  // A folded column stands for the terms it reads, and takes their state (ADR-0015).
  const terms = foldedTerms(column.name) ?? [column.name];
  if (row.view.held.some((h) => terms.some((t) => h.startsWith(`${row.line} ${t}:`)))) return 'stale';
  if (terms.some((t) => row.view.userFields.includes(t))) return 'modified';
  return 'normal';
}

/** The terms a folded column reads: a leg's summarized terms, or its second level (ADR-0015 §1, §2). */
function foldedTerms(column: string): readonly string[] | undefined {
  const m = /^leg([12])\.(summary|overrides)$/.exec(column);
  if (m === null) return undefined;
  return (m[2] === 'summary' ? [...SUMMARIZED] : legDetail).map((t) => `leg${m[1]}.${t}`);
}

export function lineCellMessage(row: LineRow, column: GridColumn<LineRow>): string | undefined {
  if (RESULT.has(column.name) && row.figures !== undefined) {
    const f = row.figures;
    const when = `revision ${f.inputRevision}, market ${f.snapshotId}, ${f.mode}`;
    const what = column.name !== 'par' || f.par === undefined ? '' : f.parMeasure === 'fairSpread' ? 'Fair spread of the floating leg. ' : 'Par rate of the fixed leg. ';
    return row.figuresStale ? `${what}Calculated on an older revision (${when}); the line changed since.` : `${what}Calculated on ${when}.`;
  }
  if (column.name === 'message') return message(row);
  const folded = /^leg([12])\.(summary|overrides)$/.exec(column.name);
  if (folded !== null) {
    const set = foldedTerms(column.name)!.filter((t) => row.view.userFields.includes(t)).map((t) => legHeaders[t.slice(5)]![0]);
    const opens = folded[2] === 'summary' ? `Leg ${folded[1]} in one line. Double-click it, or press Alt+Shift+↓, to open its terms.`
      : `The conventions that override the leg's profile. Double-click it, or press Alt+Shift+↓, to open them.`;
    return set.length === 0 ? opens : `${opens} Set by you: ${set.join(', ')}.`;
  }
  if (column.name.startsWith('leg') && row.view.profiles.length > 0 && (column.name.endsWith('.index') || column.name.endsWith('.currency')))
    return `Conventions: ${row.view.profiles.join(', ')}`;
  if (row.view.userFields.includes(column.name)) return 'Set by you.';
  const source = row.view.defaulted[column.name];
  if (source !== undefined) return `Default, from ${defaultSource(source)}.`;
  return undefined;
}

/** Where a default came from, in words (ADR-0008 §4): the workspace's own defaults, a convention profile's template, or a decision. */
function defaultSource(source: string): string {
  if (source === 'workspace') return 'the workspace defaults';
  if (source.startsWith('profile:')) return `the convention profile ${source.slice('profile:'.length)}`;
  return `decision ${source}`;
}

/** The figures of each line in a live frame, keyed by line. */
export function figuresOf(frame: LiveFrame): ReadonlyMap<string, LineFigures> {
  const out = new Map<string, LineFigures>();
  const base = { inputRevision: frame.inputRevision, snapshotId: frame.market.snapshotId, mode: frame.calculationMode };
  for (const row of frame.rows) {
    if (row.failure !== undefined && row.failure !== null) { out.set(row.rowKey, { ...base, status: 'failed', message: row.failure }); continue; }
    const pv = row.measures.pv;
    const parName = row.measures.parRate !== undefined ? 'parRate' : row.measures.fairSpread !== undefined ? 'fairSpread' : undefined;
    out.set(row.rowKey, {
      ...base, status: 'priced',
      ...(pv === undefined ? {} : { pv: pv.value, pvCurrency: pv.unit }),
      ...(parName === undefined ? {} : { par: row.measures[parName]!.value, parMeasure: parName }),
    });
  }
  for (const left of frame.notPriced) out.set(left.line, { ...base, status: 'notPriced' });
  return out;
}

/**
 * The rows: one per line, with the newest frame's figures. A line whose figures were calculated on an older
 * revision keeps them, marked stale, until a frame of its revision arrives; never shown as current.
 */
export function lineRows(lines: readonly LineView[], revision: number, frame: LiveFrame | undefined, previous: ReadonlyMap<string, LineRow>): LineRow[] {
  const figures = frame === undefined ? undefined : figuresOf(frame);
  return lines.map((view) => {
    const f = figures?.get(view.line);
    const stale = frame !== undefined && frame.inputRevision < revision;
    const before = previous.get(view.line);
    // An unchanged row keeps its object, so the grid paints it again only when something it shows moved.
    if (before !== undefined && before.view === view && before.figuresStale === stale
      && (before.figures === f || (before.figures !== undefined && f !== undefined && sameFigures(before.figures, f)))) return before;
    return { line: view.line, view, figures: f, figuresStale: stale };
  });
}

function sameFigures(a: LineFigures, b: LineFigures): boolean {
  return a.status === b.status && a.pv === b.pv && a.par === b.par && a.message === b.message && a.inputRevision === b.inputRevision && a.snapshotId === b.snapshotId;
}

/** The cashflow grid: one row per period, the user's edits marked. */
export const periodColumns: readonly GridColumn<PeriodView>[] = [
  { name: 'leg', header: 'Leg', type: 'number', width: 44, value: (p) => p.leg, format: plain },
  { name: 'accrualStart', header: 'Accrual start', type: 'text', width: 104, editable: true, value: (p) => p.accrualStart, format: plain },
  { name: 'accrualEnd', header: 'Accrual end', type: 'text', width: 104, editable: true, value: (p) => p.accrualEnd, format: plain },
  { name: 'paymentDate', header: 'Payment', type: 'text', width: 104, editable: true, value: (p) => p.paymentDate, format: plain },
  { name: 'notional', header: 'Notional (mm)', type: 'number', width: 110, editable: true, value: (p) => p.notional, format: inMillions },
  { name: 'rate', header: 'Rate/Spread %', type: 'number', width: 110, editable: true, value: (p) => p.rate ?? undefined, format: plain },
  { name: 'yearFraction', header: 'Year fraction', type: 'number', width: 100, value: (p) => p.yearFraction, format: (v) => (v as number).toFixed(6) },
];

export function periodCellState(p: PeriodView, column: GridColumn<PeriodView>): CellState {
  return p.userFields.includes(column.name) ? 'modified' : 'normal';
}
