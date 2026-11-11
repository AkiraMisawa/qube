import { readFlag } from '../../platform/flags';
import type { Resolution, XvaWorkspaceView } from './contracts';

// From what the user typed in a grid cell to the value the server is sent. Only the form is checked here;
// what a value means (an eligible CSA, an available curve) is the server's, and shows on the row.

export type Parsed = { readonly ok: true; readonly value: unknown } | { readonly ok: false; readonly message: string };

/**
 * A value sent as written (`12%`, `60bp`, `empty`), for the server to read and check as its cell reads it, with the
 * cell's title for what the page says if it is refused. It answers `undefined` once the server took it, and the server's
 * reason when it refused it.
 */
export type SetText = (entity: string, field: string, text: string, title: string) => Promise<string | undefined>;

/**
 * An edit of a field as a control makes it, with the title the page says if it is refused: what the screen calls what it
 * changes, its row and its column's header, or its label (ADR-0006's note 2 of 2026-11-11).
 */
export type PushEdit = (entity: string, field: string, value: unknown, title: string) => void;

const fail = (message: string): Parsed => ({ ok: false, message });

/**
 * A number as typed: digits with an optional sign and decimal point, and thousands commas only where every
 * group has three digits. Anything else is refused rather than read one way of several.
 */
export function parseNumber(text: string): number | undefined {
  const t = text.trim();
  if (!/^-?(\d{1,3}(,\d{3})+|\d+)(\.\d+)?$/.test(t)) return undefined;
  const value = Number(t.replaceAll(',', ''));
  return Number.isFinite(value) ? value : undefined;
}

/** A flag's cell: what was typed, read as a flag is read everywhere (ADR-0006's note 4 of 2026-11-11) or refused. */
export const parseFlag = (text: string): Parsed => {
  const b = readFlag(text);
  return b === undefined ? fail('Type on or off.') : { ok: true, value: b };
};

const imCsaOf = (text: string): Parsed => ({ ok: true, value: text.trim() === '' ? null : text.trim() });

/** The editable columns of the trades grid, and how each reads what was typed. */
export const tradeFields: Readonly<Record<string, (text: string) => Parsed>> = {
  notional: (text) => {
    const n = parseNumber(text);
    return n === undefined || n === 0 ? fail('Type a non-zero number, such as 1,000,000.') : { ok: true, value: n };
  },
  fixedRate: (text) => {
    if (text.trim() === '') return { ok: true, value: null };
    const n = parseNumber(text);
    return n === undefined ? fail('Type a rate as a decimal, such as 0.0125, or leave it empty.') : { ok: true, value: n };
  },
  schedule: (text) => {
    const t = text.trim().toUpperCase();
    return t === '3M' || t === '6M' ? { ok: true, value: t } : fail('Type 3M or 6M.');
  },
  csa: (text) => (text.trim() === '' ? fail('Type a CSA identifier.') : { ok: true, value: text.trim() }),
  // An IM CSA's identifier, or empty for none in that direction (ADR-0012 §2). Whether it is the counterparty's is the
  // server's to say, on the row's spot IM scopes.
  imR: imCsaOf,
  imP: imCsaOf,
  curve: (text) => (text.trim() === '' ? fail('Type a curve identifier.') : { ok: true, value: text.trim() }),
  before: parseFlag,
  after: parseFlag,
};

const amountOf = (text: string): Parsed => {
  const n = parseNumber(text);
  return n === undefined || n < 0 ? fail('Type an amount of 0 or more, such as 50,000,000.') : { ok: true, value: n };
};
const currencyOf = (text: string): Parsed => {
  const t = text.trim().toUpperCase();
  return /^[A-Z]{3}$/.test(t) ? { ok: true, value: t } : fail('Type a currency code, such as JPY.');
};
const identifierOf = (what: string) => (text: string): Parsed => (text.trim() === '' ? fail(`Type ${what}.`) : { ok: true, value: text.trim() });

/**
 * The editable CSA terms (ADR-0010 §8), and how each reads what was typed. The form only: which currencies and curves
 * exist, and what makes a CSA imperfect, are the server's.
 */
export const csaFields: Readonly<Record<string, (text: string) => Parsed>> = {
  currency: currencyOf,
  curve: identifierOf('a curve identifier'),
  fvaCurve: identifierOf('a curve identifier'),
  frequency: (text) => {
    const t = text.trim().toLowerCase();
    return t === 'daily' || t === 'weekly' || t === 'monthly' ? { ok: true, value: t } : fail('Type daily, weekly or monthly.');
  },
  exchange: parseFlag,
  oneWay: parseFlag,
  counterpartyThreshold: amountOf,
  counterpartyMta: amountOf,
  counterpartyCurrency: currencyOf,
  selfThreshold: amountOf,
  selfMta: amountOf,
  selfCurrency: currencyOf,
};

/** A recovery as typed: empty stays empty (the CDS recovery then applies), else a number from 0 up to 1. */
export function parseRecovery(text: string): Parsed {
  if (text.trim() === '') return { ok: true, value: null };
  const n = parseNumber(text);
  return n === undefined || n < 0 || n >= 1 ? fail('Type a recovery from 0 up to 1, or leave it empty.') : { ok: true, value: n };
}

/** The workspace's settings, as an edit names them: its display currency (ADR-0010 §1). */
export const SETTINGS_ENTITY = 'settings:workspace';

/** The workspace's selection of measures, as an edit names it (ADR-0010 §10). */
export const MEASURES_ENTITY = 'selection:measures';

/** The tables the trade grids and the CSA grid show, by the ids the server registers them with (ADR-0016 §1). */
export const TRADES_TABLE = 'xva.trades';
export const CSAS_TABLE = 'xva.csas';

/**
 * What the page says when the server refuses an edit (ADR-0006's note 2 of 2026-11-11): what the edit would have
 * changed, by the title the place that made it gave it, then the server's reason. An entity's key
 * (`selection:measures`) or a field's (`counterpartyThreshold`) is never shown.
 */
export function refusedEdit(edit: PendingEdit, reason: string): string {
  return edit.measures !== undefined ? `The measures were not changed: ${reason}` : `${edit.title} was not changed: ${reason}`;
}

/** B or A of every row of a trade grid, as its edit is titled: `Before on the existing trades`. */
export function sideTitle(side: 'before' | 'after', grid: 'new' | 'existing'): string {
  return `${side === 'before' ? 'Before' : 'After'} on the ${grid} trades`;
}

/** What `before` and `after` say once the edit is taken (ADR-0006's note 2 of 2026-11-11): what was done, and on how many trades. */
export function sideSaid(side: 'before' | 'after', value: boolean, grid: 'new' | 'existing', trades: number): string {
  return `${value ? 'Ticked' : 'Cleared'} ${side === 'before' ? 'Before' : 'After'} on ${trades} ${grid} trade${trades === 1 ? '' : 's'}.`;
}

/**
 * A change to send: a value set on a field, a held edit resolved, or the measures Price prices. They share one queue,
 * so each is sent against the revision the one before it made.
 */
export type PendingEdit =
  | {
    readonly entity: string; readonly field: string; readonly value: unknown; readonly resolution?: undefined; readonly measures?: undefined;
    /** What the screen calls what the edit changes, its row and its column's header (`DRAFT-1 After`, `CSA-A Cpty threshold`). */
    readonly title: string;
    /** What the page says once the edit is taken, where its own cell does not show it: B or A of a whole grid. */
    readonly says?: (next: XvaWorkspaceView) => string;
  }
  // A held edit, or a change removed (`reset`), of the Base or of the case named (ADR-0010 §3).
  | { readonly entity: string; readonly field: string; readonly resolution: Resolution; readonly case?: string | undefined; readonly measures?: undefined; readonly title: string }
  | { readonly entity: typeof MEASURES_ENTITY; readonly field: 'measures'; readonly measures: readonly string[]; readonly resolution?: undefined };

export { EditQueue } from '../../platform/workspace/editQueue';
