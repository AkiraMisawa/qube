import { describe, expect, it } from 'vitest';
import type { PkView } from './contracts';
import type { PositionLadder } from './contracts';
import { hedgeRows, ladderKinds, ladderLevels, ladderRows, ladderTenors, marksSaid, metricsOf, moveRows, riskInputs, tenorOf, xvaRows } from './rows';

// ADR-0019 §3, §7: the page joins what the server answered and computes nothing. Synthetic figures throughout.

const view = (over: Partial<PkView['position']> = {}, moves: readonly (readonly string[])[] = [['zero_rate', 'JPY-OIS', 'bp', 'empty']]): PkView => ({
  position: {
    workspaceId: 'ws-1', revision: 3, resultSet: 'set', engine: 'engine', engineCommit: 'abc', inputSet: 'inputs', valuationDate: '2026-11-13',
    counterparties: ['C1'], values: [], totals: [], hedges: [], risk: [], parallel: [], ladder: [], byCounterparty: [], pnl: [], pnlByKind: [], ...over,
  },
  hedges: { revision: 3, fields: ['product', 'currency', 'direction', 'notional', 'end', 'rate'], rows: [], more: 0 },
  moves: { revision: 3, fields: ['kind', 'input', 'typedIn', 'move'], rows: moves.map((cells) => ({ key: `${cells[0]}/${cells[1]}`, cells })), more: 0 },
});

describe('the Position Keeper page\'s rows', () => {
  it('a hedge row is its terms as typed with the figure and the marks the position gives it', () => {
    const hedge = { hedge: 'H1', value: 12.5, provider: 'pk.synthetic-hedge-risk', executionMode: 'Simulated', marks: ['WHAT-IF', 'SIMULATED'] as const };
    const shown = hedgeRows({ ...view({ hedges: [hedge] }), hedges: { revision: 3, fields: ['product', 'currency', 'direction', 'notional', 'end', 'rate'], rows: [{ key: 'H1', cells: ['irs', 'JPY', 'pay', '1bn', '5y', '1.2%'] }], more: 0 } });
    expect(shown).toEqual([{ ...hedge, terms: { product: 'irs', currency: 'JPY', direction: 'pay', notional: '1bn', end: '5y', rate: '1.2%' } }]);
    expect(marksSaid(shown[0]!.marks)).toBe('WHAT-IF · SIMULATED');
  });

  it('a hedge the position has no figure of is a defect said by name, never a row with an empty value', () => {
    const listed = { ...view(), hedges: { revision: 3, fields: ['product'], rows: [{ key: 'H2', cells: ['irs'] }], more: 0 } };
    expect(() => hedgeRows(listed)).toThrow('The position holds no figure of hedge H2');
  });

  it('a move row holds the move as typed and what it predicts; one with no move predicts nothing', () => {
    const pnl = { kind: 'zero_rate', input: 'JPY-OIS', move: 10, typedIn: 'bp', book: -5, net: -5, marks: ['FIRST ORDER'] as const };
    const rows = moveRows(view({ pnl: [pnl] }, [['zero_rate', 'JPY-OIS', 'bp', '10'], ['recovery', 'C1', 'empty', 'empty']]));
    expect(rows).toEqual([
      { key: 'zero_rate/JPY-OIS', kind: 'zero_rate', input: 'JPY-OIS', typedIn: 'bp', move: '10', pnl },
      { key: 'recovery/C1', kind: 'recovery', input: 'C1', typedIn: undefined, move: undefined, pnl: undefined },
    ]);
  });

  it('a move held with no prediction of it, or a prediction of no move, is a defect and not a row', () => {
    expect(() => moveRows(view({}, [['zero_rate', 'JPY-OIS', 'bp', '10']]))).toThrow('disagree on whether a move of zero_rate/JPY-OIS is held');
  });

  it('the risk grid narrows to each kind and input once, in the order the server gave them', () => {
    const line = (kind: string, input: string, key: string) => ({ kind, input, key, book: 1, net: 1, marks: [] });
    expect(riskInputs([line('zero_rate', 'JPY-OIS', '1d'), line('zero_rate', 'JPY-OIS', '7d'), line('fx_spot', 'USDJPY', '')])).toEqual(['zero_rate JPY-OIS', 'fx_spot USDJPY']);
  });

  it('a counterparty is one row, in the server\'s order, with each metric beside the total the server summed', () => {
    const values = [
      { counterparty: 'C1', legalEntity: 'LE-1', metric: 'CVA', value: -10, stdError: 1 }, { counterparty: 'C1', legalEntity: 'LE-1', metric: 'FVA', value: -2, stdError: 0.5 },
      { counterparty: 'C2', metric: 'CVA', value: -40, stdError: 3 },
    ];
    const byCounterparty = [{ counterparty: 'C2', total: -40 }, { counterparty: 'C1', legalEntity: 'LE-1', total: -12 }];
    expect(metricsOf({ values })).toEqual(['CVA', 'FVA']);
    expect(xvaRows({ values, byCounterparty })).toEqual([
      { counterparty: 'C2', total: -40, byMetric: { CVA: { value: -40, stdError: 3 } } },
      { counterparty: 'C1', legalEntity: 'LE-1', total: -12, byMetric: { CVA: { value: -10, stdError: 1 }, FVA: { value: -2, stdError: 0.5 } } },
    ]);
  });

  it('a value of a counterparty the server gave no total of is a defect, not a row left out', () => {
    expect(() => xvaRows({ values: [{ counterparty: 'C9', metric: 'CVA', value: -1, stdError: 0 }], byCounterparty: [] })).toThrow('gives a value of C9 and no total of it');
  });

  it('a risk line is named by its tenor where the server gave one, and by its key where it gave none', () => {
    expect([tenorOf({ tenor: '5Y', key: '1826d' }), tenorOf({ key: '3652d' }), tenorOf({})]).toEqual(['5Y', '3652d', undefined]);
  });

  it('a ladder is a row for each name and side, a cell a tenor in the server\'s order, and Net is said only where a hedge is in it', () => {
    const cell = (name: string, tenor: string, order: number, book: number, whatIf?: number): PositionLadder => ({
      kind: 'zero_rate', level: 'group', name, tenor, order, step: '1bp', book, net: book + (whatIf ?? 0),
      marks: whatIf === undefined ? [] : ['WHAT-IF'], ...(whatIf === undefined ? {} : { whatIf }),
    });
    const ladder = [cell('JPY', 'Total', 2, -30, 25), cell('JPY', '5Y', 1, -20, 25), cell('JPY', '1Y', 0, -10, 0), cell('USD', '1Y', 0, -1), cell('USD', '5Y', 1, -2), cell('USD', 'Total', 2, -3)];
    expect(ladderKinds(ladder)).toEqual(['zero_rate']);
    expect(ladderLevels(ladder, 'zero_rate')).toEqual(['group']);
    expect(ladderTenors(ladder, 'zero_rate')).toEqual(['1Y', '5Y', 'Total']);
    expect(ladderRows(ladder, 'zero_rate', 'group')).toEqual([
      { key: 'JPY/Book', name: 'JPY', side: 'Book', step: '1bp', cells: { Total: -30, '5Y': -20, '1Y': -10 }, marks: [] },
      { key: 'JPY/What-if', name: 'JPY', side: 'What-if', step: '1bp', cells: { Total: 25, '5Y': 25, '1Y': 0 }, marks: ['WHAT-IF'] },
      { key: 'JPY/Net', name: 'JPY', side: 'Net', step: '1bp', cells: { Total: -5, '5Y': 5, '1Y': -10 }, marks: ['WHAT-IF'] },
      { key: 'USD/Book', name: 'USD', side: 'Book', step: '1bp', cells: { '1Y': -1, '5Y': -2, Total: -3 }, marks: [] },
    ]);
  });
});
