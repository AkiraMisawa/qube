import { describe, expect, it } from 'vitest';
import { measureOfField, membershipField, membershipOf, scopesOf } from './membership';
import { tabShown } from './XvaInputs';
import { tradeFields } from './editing';

// A membership in words, as the server decided it (ADR-0010 §5, §10), and spot IM's per-scope standing (ADR-0012 §2,
// §3 with their notes of 2026-11-08). Every name is synthetic.

const spotIm = 'simm.spotIm';
const row = {
  membership: {
    fva: { in: true, reason: 'CSA-B imperfect: one-way.' },
    [spotIm]: {
      in: true, reason: 'R: JFSA · P: none',
      scopes: [
        { id: 'collect/JFSA', title: 'IM-R JFSA', in: true, reason: 'IMR-ABC-01 collects under JFSA. Started 2023-01-10, on or after ABC/LE-01 came into JFSA\'s scope on 2022-09-01.' },
        { id: 'post/JFSA', title: 'IM-P JFSA', in: false, reason: 'IM-P is empty: no IM CSA holds the trade in this direction.' },
      ],
    },
  },
};

describe('membership in words', () => {
  it('ADR-0012 §2: a measure priced per scope shows its compact form, and its cell says each scope with its reason', () => {
    expect(membershipOf(row, spotIm)).toBe('In · R: JFSA · P: none');
    expect(scopesOf(row, spotIm)).toBe(
      "IM-R JFSA: in. IMR-ABC-01 collects under JFSA. Started 2023-01-10, on or after ABC/LE-01 came into JFSA's scope on 2022-09-01. "
      + 'IM-P JFSA: out. IM-P is empty: no IM CSA holds the trade in this direction.');
  });

  it('ADR-0010 §5: a membership with no scopes says its reason alone, and its cell adds nothing', () => {
    expect(membershipOf(row, 'fva')).toBe('In · CSA-B imperfect: one-way.');
    expect(scopesOf(row, 'fva')).toBeUndefined();
    expect(scopesOf({}, spotIm)).toBeUndefined();
  });

  it('ADR-0010 §10: names a measure\'s membership column by the measure, and reads the measure back from it', () => {
    expect(measureOfField(membershipField(spotIm))).toBe(spotIm);
    expect(measureOfField('imR')).toBeUndefined();
  });
});

describe("spot IM's inputs (ADR-0012 §3)", () => {
  it('shows the IM tab only while a selected measure needs it; Netting and CSA always', () => {
    const selected = { inputs: [{ tab: 'trades', field: 'imR', measure: spotIm }, { tab: 'im', field: 'imCsas', measure: spotIm }] };
    expect(tabShown(selected, 'im')).toBe(true);
    expect(tabShown({ inputs: [] }, 'im')).toBe(false);
    expect(tabShown({ inputs: [] }, 'netting')).toBe(true);
    expect(tabShown({ inputs: [] }, 'csa')).toBe(true);
  });

  it('reads an IM CSA as typed, and empty as none in that direction, never a made-up ID', () => {
    expect(tradeFields.imR!(' IMR-ABC-01 ')).toEqual({ ok: true, value: 'IMR-ABC-01' });
    expect(tradeFields.imP!('')).toEqual({ ok: true, value: null });
    expect(tradeFields.imR!('  ')).toEqual({ ok: true, value: null });
  });
});
