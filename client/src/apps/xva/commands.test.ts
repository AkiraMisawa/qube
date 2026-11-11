import { describe, expect, it } from 'vitest';
import { ambiguousComma, measureNamed } from './commands';
import type { RegisteredMeasure } from './contracts';

describe('`explain` names its measure (ADR-0011 §7, ADR-0012 §6\'s note of 2026-11-08)', () => {
  const measure = (id: string, module: string, traced: boolean): RegisteredMeasure => ({
    id, title: id, module, request: module, metrics: [], headline: 'x', declaresMembership: false, traced, quoted: false, tabs: [],
  });
  const register = [measure('cva', 'xva', false), measure('fva', 'xva', false), measure('simm.spotIm', 'simm', true), measure('ccr.spotCapital', 'ccr', true),
    measure('farm.draw', 'farm', true), measure('farm.flow', 'farm', true)];

  it('by its id whatever its case, or by its module where that module has one traced measure', () => {
    expect(measureNamed('SIMM.spotim', register)?.id).toBe('simm.spotIm');
    expect(measureNamed('simm', register)?.id).toBe('simm.spotIm');
    expect(measureNamed('CCR', register)?.id).toBe('ccr.spotCapital');
    expect(measureNamed('cva', register)?.id).toBe('cva');
  });

  it('names none for a module of several traced measures, or of none, so the page refuses it by name', () => {
    expect(measureNamed('farm', register)).toBeUndefined();
    expect(measureNamed('xva', register)).toBeUndefined();
    expect(measureNamed('nothing', register)).toBeUndefined();
  });
});

describe('A list of values separated by commas (ADR-0010 §9, spine 1)', () => {
  it('is refused where a comma stands between two digits, never split as a guess', () => {
    expect(ambiguousComma('10,000k, 5mm')).toBe('10,000k could be one value or two: a comma separates values here, so write an amount without separators, such as 10000k or 10mm.');
    expect(ambiguousComma('5,10')).toMatch(/^5,10 could be one value or two/);
  });

  it('reads a comma after a unit, or before a space, as a separator', () => {
    expect(ambiguousComma('10mm,5mm, 0')).toBeUndefined();
  });
});
