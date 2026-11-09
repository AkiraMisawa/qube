import { describe, expect, it } from 'vitest';
import type { EffectiveCredit } from './contracts';
import { bpText, creditChanges, emptyRecovery, nettingChanges, spreadTyped } from './credit';

// The Credit tab's readings (ADR-0010 §8's note of 2026-11-09). Every name and figure is synthetic.

const credit = (over: Partial<EffectiveCredit> = {}): EffectiveCredit => ({ cvaRecoveryOwner: 'source', choice: { type: 'singleName', reference: 'ABC-SN' }, cdsRecovery: 0.4, ...over });

describe('spreadTyped', () => {
  it('ADR-0010 §8\'s note of 2026-11-09: a bare number is the grid\'s bp, a unit typed is sent as written for the server to read', () => {
    expect(spreadTyped(' 60 ')).toEqual({ ok: true, value: '60bp' });
    expect(spreadTyped('52.5')).toEqual({ ok: true, value: '52.5bp' });
    expect(spreadTyped('60bp')).toEqual({ ok: true, value: '60bp' });
    expect(spreadTyped('0.6%')).toEqual({ ok: true, value: '0.6%' });
  });

  it('spine 1: an empty spread or a word is refused by name, never read as 0', () => {
    expect(spreadTyped('')).toMatchObject({ ok: false });
    expect(spreadTyped('wide')).toEqual({ ok: false, message: 'wide is not a spread: type it in bp, such as 52, or with its unit, such as 0.52%.' });
  });
});

describe('the Credit tab\'s counts and words', () => {
  it('ADR-0010 §2: Netting counts the CVA recovery, Credit the CDS chosen and the curve\'s spreads', () => {
    const changed = credit({
      original: { cvaRecovery: 0.4, cdsType: 'singleName', cdsReference: 'ABC-SN' },
      cds: { curveId: 'XYZ-SN', type: 'singleName', ticker: 'XYZ', currency: 'USD', seniority: 'SNR', docClause: 'XR14', name: 'XYZ', reference: 'XYZ | USD | SNR | XR14 | XYZ', recovery: 0.4, spreads: { '5y': 0.006 }, overridden: ['5y'], original: { '5y': 0.0052 } },
    });
    expect([nettingChanges(changed), creditChanges(changed)]).toEqual([1, 3]);
    expect([nettingChanges(credit()), creditChanges(credit())]).toEqual([0, 0]);
  });

  it('ADR-0010 §8: an empty CVA recovery takes the chosen curve\'s, and says when there is none', () => {
    expect(emptyRecovery(credit())).toBe('empty: CDS 0.4');
    expect(emptyRecovery(credit({ cdsRecovery: null }))).toBe('empty: no CDS is chosen');
    expect(bpText(0.0052)).toBe('52bp');
  });
});
