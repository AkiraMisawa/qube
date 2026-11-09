import { describe, expect, it } from 'vitest';
import { addedText, valuesTyped, variedText } from './VaryDialog';

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

describe('Vary values… says what vary made in the command line\'s words (ADR-0010 §3)', () => {
  it('names the cases made and the values skipped', () => {
    expect(variedText({ made: ['CSA-A threshold 5mm', 'CSA-A threshold 10mm'], skipped: ['0 is the workspace\'s value'] }))
      .toBe('Made 2 case(s): CSA-A threshold 5mm, CSA-A threshold 10mm; skipped 0 is the workspace\'s value.');
  });

  it('says when no case was made', () => {
    expect(variedText({ made: [], skipped: ['0 is the workspace\'s value'] })).toBe('vary made no case; skipped 0 is the workspace\'s value.');
  });
});

describe('`add trades` says what it made and the set it replaced (docs/03 §5)', () => {
  it('names the cases made, and those of a set made before', () => {
    expect(addedText({ made: ['add DRAFT-1', 'add DRAFT-2'], replaced: [] })).toBe('Made 2 case(s): add DRAFT-1, add DRAFT-2.');
    expect(addedText({ made: ['add DRAFT-1'], replaced: ['add DRAFT-1', 'add DRAFT-2'] })).toBe('Made 1 case(s): add DRAFT-1; replaced add DRAFT-1, add DRAFT-2.');
  });
});
