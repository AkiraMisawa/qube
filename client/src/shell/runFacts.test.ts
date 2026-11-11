import { describe, expect, it } from 'vitest';
import { factsSaid } from './runFacts';

describe('what a Run panel says of its context and input', () => {
  it('says a value that holds values by each of theirs, and never as [object Object]', () => {
    expect(factsSaid({ valuationDate: '2026-10-27', defaults: { currency: 'JPY', notional: 500000000 }, revision: 6 }))
      .toEqual(['valuationDate 2026-10-27', 'defaults.currency JPY', 'defaults.notional 500000000', 'revision 6']);
  });

  it('says a list and a value that holds nothing as they are written', () => {
    expect(factsSaid({ measures: ['cva', 'fva'], defaults: {}, market: null })).toEqual(['measures ["cva","fva"]', 'defaults {}', 'market null']);
  });
});
