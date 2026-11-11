import type { CellState, GridColumn } from '@ex-grid/core';
import type { EffectiveTrade, RegisteredColumn, TradeCell } from './contracts';
import { amountTone, formatAmount } from '../../platform/grid/amounts';
import { parseNumber, TRADES_TABLE, type Parsed } from './editing';
import { differs, originalMessage } from './original';

// The columns modules add to the trade grids (ADR-0010 §10), such as XVA's PV01, Margin and PV Adj (§6). The server
// decides every cell: its value, whether it is pending or failed, what it says and whether it may be typed now. The page
// draws what it answers and sends what was typed.

const figure = new Intl.NumberFormat('en-US', { maximumFractionDigits: 6 });

/** The module's cell on a row, or undefined where the module adds none to it (an existing trade has no PV01). */
export const cellOf = (row: EffectiveTrade, field: string): TradeCell | undefined => row.cells?.[field];

/** What the grid holds for a cell: its figure or word, the word for why there is none, or a Blank where it is empty. */
export function cellValue(cell: TradeCell | undefined): number | string | undefined {
  if (cell === undefined) return undefined;
  if (cell.state !== 'value') return cell.state;
  return cell.value === null ? undefined : cell.value;
}

/** An amount as every amount is written (C48), and a word as it is. */
export const amountText = (value: unknown): string => (typeof value === 'number' ? formatAmount(value) : String(value));

/** A figure that is not money, such as a margin in bp, grouped and to its digits, and a word as it is. */
export const figureText = (value: unknown): string => (typeof value === 'number' ? figure.format(value) : String(value));

/** Pending is drawn as missing and failed as an error, never as a figure; a typed value that differs from Original is marked. */
export function cellState(row: EffectiveTrade, field: string): CellState {
  const cell = cellOf(row, field);
  if (cell?.state === 'pending') return 'missing';
  if (cell?.state === 'failed') return 'error';
  return differs(row, field) ? 'modified' : 'normal';
}

/** What a cell says: where its value came from or why there is none, else what Original held. */
export function cellMessage(row: EffectiveTrade, field: string): string | undefined {
  const cell = cellOf(row, field);
  if (cell === undefined) return undefined;
  if (cell.note !== undefined && cell.note !== null) return cell.note;
  return originalMessage(row, field, cell.value);
}

/** Why a cell cannot be typed now, as the server would refuse it, or undefined when it can. */
export function cellRefusal(row: EffectiveTrade, column: RegisteredColumn): string | undefined {
  const cell = cellOf(row, column.field);
  if (cell === undefined) return `${row.tradeId} has no ${column.title}.`;
  if (!cell.editable) return cell.note === undefined || cell.note === null ? `${column.title} is read-only.` : `${column.title} is read-only: ${cell.note}`;
  return undefined;
}

/** A number typed into a module's column, or empty for none. */
export function parseCellNumber(text: string): Parsed {
  if (text.trim() === '') return { ok: true, value: null };
  const n = parseNumber(text);
  return n === undefined ? { ok: false, message: 'Type a number, such as -1,500 or 2.5, or leave it empty.' } : { ok: true, value: n };
}

/** What a typed PV asks of `entity.set`: the trade by its id in the trades' table, and the value as typed. */
export type PvRequest = Readonly<Record<'table' | 'rows' | 'set', string>>;

/**
 * A PV typed into a trade's PV cell (ADR-0016 note 49), as the request of the command that writes it. The server reads
 * what was typed, a number alone or followed by its currency, as it reads the line `set <trade> pv <typed>`: the page
 * reads none of it. Nothing typed is refused here, since a PV is written and never emptied, and so is an `=`, which
 * would name another field of the trade.
 */
export function pvRequest(tradeId: string, typed: string): { readonly ok: true; readonly request: PvRequest } | { readonly ok: false; readonly message: string } {
  const text = typed.trim();
  if (text === '' || text.includes('=')) return { ok: false, message: 'Type a PV as a number, alone or followed by its currency: -350,000 or -350,000 USD.' };
  return { ok: true, request: { table: TRADES_TABLE, rows: tradeId, set: `pv ${text}` } };
}

/** Whether a cell has failed, so that asking again is its action (ADR-0010 §6). */
export const failed = (row: EffectiveTrade, field: string): boolean => cellOf(row, field)?.state === 'failed';

/**
 * The grid columns of the registered module columns, in order. The server neither sorts nor filters by them, so the grid
 * does not offer to. A figure column is as wide as the notional column, so that an amount of the synthetic notionals'
 * size (a PV Adj of -47,500,000) never falls to `####`; an amount is grouped in whole units and red when negative
 * (C48), any other figure, such as a margin in bp, keeps its digits. A text column, such as SA-CCR's class of a trade
 * (`Interest rate · JPY · 1-5y · long`), takes the width of its words, within bounds.
 */
export function moduleColumns(columns: readonly RegisteredColumn[]): readonly GridColumn<EffectiveTrade>[] {
  return columns.map((c) => ({
    name: c.field,
    header: c.title,
    type: c.type === 'amount' ? 'number' : c.type,
    width: c.type === 'text' ? { auto: true, minPx: 120, maxPx: 280 } : 168,
    editable: c.editable,
    queryable: false,
    value: (row: EffectiveTrade) => cellValue(cellOf(row, c.field)),
    format: c.type === 'amount' ? amountText : figureText,
    ...(c.type === 'amount' ? { tone: amountTone } : {}),
  }));
}
