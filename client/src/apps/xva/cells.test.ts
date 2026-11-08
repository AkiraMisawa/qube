import { describe, expect, it } from 'vitest';
import type { EffectiveTrade, RegisteredColumn, TradeCell } from './contracts';
import { cellMessage, cellRefusal, cellState, cellText, cellValue, moduleColumns, parseCellNumber } from './cells';

// ADR-0010 §6, §10: a module's trade columns show what the server answers for each cell, PV01 pending or failed as a
// word and never as 0, and are typed only where the cell says so.

const trade = (cells: Record<string, TradeCell>, original?: Record<string, unknown>): EffectiveTrade => ({
  tradeId: 'DRAFT-1', isNew: true, product: 'IRS', currency: 'JPY', notional: 5_000_000_000, maturity: '2031-10-26',
  csa: 'CSA-A', csaOwner: 'automatic', curve: 'jpy_ois', curveOwner: 'automatic', before: false, after: true,
  issues: [], held: [], overridden: [], cells, ...(original === undefined ? {} : { original }),
});

const pvAdj: RegisteredColumn = { field: 'xva.pvAdj', title: 'PV Adj', type: 'number', editable: true, module: 'xva' };

describe("a module's trade cells", () => {
  it('holds a pending or failed PV01 as its word, a figure as a number, and an empty cell as a Blank', () => {
    expect(cellValue({ state: 'pending', editable: false, note: 'PV01 is asked of the engine.' })).toBe('pending');
    expect(cellValue({ state: 'failed', editable: false, note: 'PV01 failed: no curve' })).toBe('failed');
    expect(cellValue({ state: 'value', value: -1_500, editable: false })).toBe(-1_500);
    expect(cellValue({ state: 'value', editable: true })).toBeUndefined();
    expect(cellValue(undefined)).toBeUndefined();
    expect([cellText(-3_000), cellText('pending')]).toEqual(['-3,000', 'pending']);
  });

  it('draws pending as missing and failed as an error, and marks a typed Margin against an empty Original', () => {
    const row = trade({
      'xva.pv01': { state: 'pending', editable: false },
      'xva.pvAdj': { state: 'failed', editable: false, note: 'From Margin: PV01 failed: no curve' },
      'xva.margin': { state: 'value', value: 2, editable: true },
    }, { 'xva.margin': null });

    expect([cellState(row, 'xva.pv01'), cellState(row, 'xva.pvAdj'), cellState(row, 'xva.margin')]).toEqual(['missing', 'error', 'modified']);
    expect(cellMessage(row, 'xva.margin')).toBe('Original empty · now 2');
    expect(cellMessage(row, 'xva.pvAdj')).toBe('From Margin: PV01 failed: no curve');
  });

  it('refuses typing a PV Adj that comes from Margin, saying so, and takes a number or empty', () => {
    const fromMargin = trade({ 'xva.pvAdj': { state: 'value', value: -3_000, editable: false, note: 'From Margin: 2 bp × PV01 -1,500' } });
    const typed = trade({ 'xva.pvAdj': { state: 'value', editable: true } });

    expect(cellRefusal(fromMargin, pvAdj)).toBe('PV Adj is read-only: From Margin: 2 bp × PV01 -1,500');
    expect(cellRefusal(typed, pvAdj)).toBeUndefined();
    expect(cellRefusal(trade({}), pvAdj)).toBe('DRAFT-1 has no PV Adj.');
    expect(parseCellNumber('-1,500')).toEqual({ ok: true, value: -1_500 });
    expect(parseCellNumber(' ')).toEqual({ ok: true, value: null });
    expect(parseCellNumber('two').ok).toBe(false);
  });

  it('makes one grid column per registered column, neither sorted nor filtered by the grid', () => {
    const columns = moduleColumns([{ field: 'xva.pv01', title: 'PV01', type: 'number', editable: false, module: 'xva' }, pvAdj]);

    expect(columns.map((c) => [c.name, c.header, c.editable, c.queryable])).toEqual([['xva.pv01', 'PV01', false, false], ['xva.pvAdj', 'PV Adj', true, false]]);
  });
});
