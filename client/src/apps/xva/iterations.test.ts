import { describe, expect, it } from 'vitest';
import type { CaseView, Iteration } from './contracts';
import { tradeLeftOut } from './iterations';

// As the wire has them: a case that is no iteration has no `iteration` field.
const made = (id: string, iteration?: Iteration): CaseView =>
  (iteration === undefined ? { id, title: id, kind: 'vary', changes: 1 } : { id, title: id, kind: 'add', changes: 1, iteration });

const cases = [
  made('c1'),
  made('c2', { add: ['DRAFT-1'], without: [] }),
  made('c3', { add: ['DRAFT-2', 'DRAFT-3'], without: ['E00003', 'E00004'] }),
];

describe('add each\'s set of iterations, as the page says it again (ADR-0016 note 37)', () => {
  it('a trade left out of one list of one case leaves the others as they are; a case that is no iteration is no part of the set', () => {
    expect(tradeLeftOut(cases, 'c3', 'without', 'E00003')).toEqual([
      { add: ['DRAFT-1'], without: [] },
      { add: ['DRAFT-2', 'DRAFT-3'], without: ['E00004'] },
    ]);
    expect(tradeLeftOut(cases, 'c3', 'add', 'DRAFT-3')).toEqual([
      { add: ['DRAFT-1'], without: [] },
      { add: ['DRAFT-2'], without: ['E00003', 'E00004'] },
    ]);
  });

  it('what the page no longer shows is said, and no set is made: a case gone, a case that is no iteration, a trade not in the list', () => {
    expect(() => tradeLeftOut(cases, 'c9', 'add', 'DRAFT-1')).toThrow('That case is no longer in the workspace: look at the Changes tab again.');
    expect(() => tradeLeftOut(cases, 'c1', 'add', 'DRAFT-1')).toThrow('Case c1 is no case of add each, so it lists no trade.');
    expect(() => tradeLeftOut(cases, 'c2', 'without', 'E00003')).toThrow('Case c2 no longer takes E00003 out: look at the Changes tab again.');
    expect(() => tradeLeftOut(cases, 'c3', 'add', 'DRAFT-9')).toThrow('Case c3 no longer adds DRAFT-9: look at the Changes tab again.');
  });

  it('the last trade a case adds is not left out: the case is removed whole instead', () => {
    expect(() => tradeLeftOut(cases, 'c2', 'add', 'DRAFT-1')).toThrow('DRAFT-1 is the one trade c2 adds: remove the case instead.');
  });
});
