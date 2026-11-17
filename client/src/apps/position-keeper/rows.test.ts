import { describe, expect, it } from 'vitest';
import type { PkView } from './contracts';
import { hedgeRows, marksSaid, metricsOf, moveRows, riskInputs, tenorOf, xvaRows } from './rows';

// ADR-0019 §3, §7: the page joins what the server answered and computes nothing. Synthetic figures throughout.

const view = (over: Partial<PkView['position']> = {}, moves: readonly (readonly string[])[] = [['zero_rate', 'JPY-OIS', 'bp', 'empty']]): PkView => ({
  position: {
    workspaceId: 'ws-1', revision: 3, resultSet: 'set', engine: 'engine', engineCommit: 'abc', inputSet: 'inputs', valuationDate: '2026-11-13',
    counterparties: ['C1'], values: [], totals: [], hedges: [], risk: [], parallel: [], byCounterparty: [], pnl: [], ...over,
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
});
