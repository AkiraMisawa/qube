import { describe, expect, it } from 'vitest';
import { valuesTyped } from './VaryDialog';

describe('Vary values… reads its list as typed (ADR-0010 §3)', () => {
  it('takes every value between commas, trimmed, for the server to read as the cell does', () => {
    expect(valuesTyped(' 5mm, 10mm ,0')).toEqual({ ok: true, values: ['5mm', '10mm', '0'] });
  });

  it('refuses an empty list and an empty value rather than send a guess', () => {
    expect(valuesTyped('  ')).toEqual({ ok: false, message: 'Type one value or more, separated by commas.' });
    expect(valuesTyped('5mm,,10mm')).toEqual({ ok: false, message: 'A value between two commas is empty.' });
    expect(valuesTyped('1,000k, 5mm')).toEqual({ ok: false, message: '1,000k could be one value or two: a comma separates values here, so write an amount without separators, such as 10000k or 10mm.' });
  });
});
