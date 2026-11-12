import { describe, expect, it } from 'vitest';
import type { EffectiveTrade, RegisteredColumn, TradeCell } from './contracts';
import { amountText, cellMessage, cellRefusal, cellState, cellValue, figureText, moduleColumns, parseCellNumber, pvMessage, pvRequest } from './cells';

// ADR-0010 §6, §10: a module's trade columns show what the server answers for each cell, PV01 pending or failed as a
// word and never as 0, and are typed only where the cell says so.

const trade = (cells: Record<string, TradeCell>, original?: Record<string, unknown>): EffectiveTrade => ({
  tradeId: 'DRAFT-1', isNew: true, product: 'IRS', currency: 'JPY', notional: 5_000_000_000, maturity: '2031-10-26',
  csa: 'CSA-A', csaOwner: 'automatic', curve: 'jpy_ois', curveOwner: 'automatic', imROwner: 'automatic', imPOwner: 'automatic', before: false, after: true,
  issues: [], held: [], overridden: [], cells, ...(original === undefined ? {} : { original }),
});

const pvAdj: RegisteredColumn = { field: 'xva.pvAdj', title: 'PV Adj', type: 'amount', editable: true, module: 'xva' };

describe("a module's trade cells", () => {
  it('holds a pending or failed PV01 as its word, a figure as a number, and an empty cell as a Blank', () => {
    expect(cellValue({ state: 'pending', editable: false, note: 'PV01 is asked of the engine.' })).toBe('pending');
    expect(cellValue({ state: 'failed', editable: false, note: 'PV01 failed: no curve' })).toBe('failed');
    expect(cellValue({ state: 'value', value: -1_500, editable: false })).toBe(-1_500);
    expect(cellValue({ state: 'value', editable: true })).toBeUndefined();
    expect(cellValue(undefined)).toBeUndefined();
    expect([amountText(-3_000.4), amountText('pending')]).toEqual(['-3,000', 'pending']);
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
    const columns = moduleColumns([{ field: 'xva.pv01', title: 'PV01', type: 'amount', editable: false, module: 'xva' }, pvAdj]);

    expect(columns.map((c) => [c.name, c.header, c.editable, c.queryable])).toEqual([['xva.pv01', 'PV01', false, false], ['xva.pvAdj', 'PV Adj', true, false]]);
  });

  it('C48: shows an amount column as every amount, red when negative, and a margin in bp to its digits, never red', () => {
    const [pv01, margin] = moduleColumns([
      { field: 'xva.pv01', title: 'PV01', type: 'amount', editable: false, module: 'xva' },
      { field: 'xva.margin', title: 'Margin (bp)', type: 'number', editable: true, module: 'xva' },
    ]);

    expect([pv01?.type, pv01?.format?.(-1_234.56), pv01?.tone?.(-1_234.56), pv01?.tone?.(-0.4)]).toEqual(['number', '-1,235', 'negative', 'none']);
    expect([margin?.type, margin?.format?.(-0.125), margin?.tone]).toEqual(['number', '-0.125', undefined]);
    expect(figureText('pending')).toBe('pending');
  });

  it("ADR-0013 §3: draws SA-CCR's class of a trade as words in a text column, and a trade it refuses as an error saying why", () => {
    const [column] = moduleColumns([{ field: 'ccr.class', title: 'SA-CCR', type: 'text', editable: false, module: 'ccr' }]);
    const swap = trade({ 'ccr.class': { state: 'value', value: 'Interest rate · JPY · 1-5y · long', editable: false } });
    const refused = trade({ 'ccr.class': { state: 'failed', editable: false, note: 'SA-CCR cannot decompose a CDS.' } });

    expect([column?.type, column?.width]).toEqual(['text', { auto: true, minPx: 120, maxPx: 280 }]);
    expect(column?.value(swap)).toBe('Interest rate · JPY · 1-5y · long');
    expect(column?.format?.(column.value(swap))).toBe('Interest rate · JPY · 1-5y · long');
    expect([cellValue(refused['cells']?.['ccr.class']), cellState(refused, 'ccr.class'), cellMessage(refused, 'ccr.class')])
      .toEqual(['failed', 'error', 'SA-CCR cannot decompose a CDS.']);
  });
});

// ADR-0016 note 49: a PV typed in a trade grid is written by the command the line types, and read by the server alone.
describe("a trade's PV cell", () => {
  it('asks entity.set for the trade by its id, with what was typed as its value', () => {
    expect(pvRequest('ABC-LE-01-E00004', ' -350,000 USD ')).toEqual({ ok: true, request: { table: 'xva.trades', rows: 'ABC-LE-01-E00004', set: 'pv -350,000 USD' } });
    expect(pvRequest('DRAFT-1', '-350k')).toEqual({ ok: true, request: { table: 'xva.trades', rows: 'DRAFT-1', set: 'pv -350k' } });
  });

  it('says what Original held and that no measure reads it, where it differs from the source\'s', () => {
    expect(pvMessage({ ...trade({}), pv: 14_286_696 })).toBeUndefined();
    expect(pvMessage({ ...trade({}, { pv: 14_286_696 }), isNew: false, pv: 1_000_000 }))
      .toBe('Original 14,286,696 · now 1,000,000. No measure reads the PV legs yet: SA-CCR reads the official PV.');
    // A new trade has no official PV: SA-CCR takes it as traded at market (ADR-0010 §6).
    expect(pvMessage({ ...trade({}, { pv: null }), pv: 40_598_290.6 }))
      .toBe('Original empty · now 40,598,290.6. No measure reads the PV legs yet: SA-CCR takes a new trade at market.');
  });

  it('refuses nothing typed, and a value that would name another field', () => {
    expect(pvRequest('DRAFT-1', '  ').ok).toBe(false);
    expect(pvRequest('DRAFT-1', '5 after=off').ok).toBe(false);
  });
});
