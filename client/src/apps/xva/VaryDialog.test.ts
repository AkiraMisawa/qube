import { describe, expect, it } from 'vitest';
import { valuesTyped, variedText } from './VaryDialog';

describe('Vary values… reads its list as typed (ADR-0010 §3)', () => {
  it('takes every value between commas, trimmed, for the server to read as the cell does', () => {
    expect(valuesTyped(' 5mm, 10mm ,0')).toEqual({ ok: true, values: ['5mm', '10mm', '0'] });
  });

  it('refuses an empty list and an empty value rather than send a guess', () => {
    expect(valuesTyped('  ')).toEqual({ ok: false, message: 'Type one value or more, separated by commas.' });
    expect(valuesTyped('5mm,,10mm')).toEqual({ ok: false, message: 'A value between two commas is empty.' });
  });
});

describe('Vary values… says what vary made in the command line\'s words (ADR-0010 §3)', () => {
  it('names the cases made and the values skipped', () => {
    expect(variedText({ made: ['CSA-A threshold 5mm', 'CSA-A threshold 10mm'], skipped: ['0 is the workspace\'s value'] }))
      .toBe('Made 2 case(s): CSA-A threshold 5mm, CSA-A threshold 10mm; skipped 0 is the workspace\'s value.');
  });

  it('says when no case was made', () => {
    expect(variedText({ made: [], skipped: ['0 is the workspace\'s value'] })).toBe('vary made no case; skipped 0 is the workspace\'s value.');
  });
});
