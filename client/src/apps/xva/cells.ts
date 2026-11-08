import type { CellState, GridColumn } from '@ex-grid/core';
import type { EffectiveTrade, RegisteredColumn, TradeCell } from './contracts';
import { parseNumber, type Parsed } from './editing';
import { differs, originalMessage } from './original';

// The columns modules add to the trade grids (ADR-0010 §10), such as XVA's PV01, Margin and PV Adj (§6). The server
// decides every cell: its value, whether it is pending or failed, what it says and whether it may be typed now. The page
// draws what it answers and sends what was typed.

const amount = new Intl.NumberFormat('en-US', { maximumFractionDigits: 2 });

/** The module's cell on a row, or undefined where the module adds none to it (an existing trade has no PV01). */
export const cellOf = (row: EffectiveTrade, field: string): TradeCell | undefined => row.cells?.[field];

/** What the grid holds for a cell: its figure or word, the word for why there is none, or a Blank where it is empty. */
export function cellValue(cell: TradeCell | undefined): number | string | undefined {
  if (cell === undefined) return undefined;
  if (cell.state !== 'value') return cell.state;
  return cell.value === null ? undefined : cell.value;
}

/** A figure as amounts are written, and a word as it is. */
export const cellText = (value: unknown): string => (typeof value === 'number' ? amount.format(value) : String(value));

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

/** Whether a cell has failed, so that asking again is its action (ADR-0010 §6). */
export const failed = (row: EffectiveTrade, field: string): boolean => cellOf(row, field)?.state === 'failed';

/**
 * The grid columns of the registered module columns, in order. The server neither sorts nor filters by them, so the grid
 * does not offer to. A number column is as wide as the notional column, so that an amount of the synthetic notionals'
 * size with its cents (a PV Adj of -47,500,000.00) never falls to `####`. A text column, such as SA-CCR's class of a
 * trade (`Interest rate · JPY · 1-5y · long`), takes the width of its words, within bounds.
 */
export function moduleColumns(columns: readonly RegisteredColumn[]): readonly GridColumn<EffectiveTrade>[] {
  return columns.map((c) => ({
    name: c.field,
    header: c.title,
    type: c.type,
    width: c.type === 'text' ? { auto: true, minPx: 120, maxPx: 280 } : 168,
    editable: c.editable,
    queryable: false,
    value: (row: EffectiveTrade) => cellValue(cellOf(row, c.field)),
    format: cellText,
  }));
}
