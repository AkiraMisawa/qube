import { describe, expect, it } from 'vitest';
import { measureNamed, measuresTyped } from './commands';
import type { RegisteredMeasure } from './contracts';

describe('`measures` as typed (ADR-0010 §9)', () => {
  it('names a registered measure whatever its case, once each, in the order typed', () => {
    expect(measuresTyped('KVA ccr.spotcapital kva', ['cva', 'kva', 'ccr.spotCapital'])).toEqual(['kva', 'ccr.spotCapital']);
  });

  it('sends a word that names no measure as typed, for the server to refuse by name', () => {
    expect(measuresTyped('cva nothing', ['cva'])).toEqual(['cva', 'nothing']);
  });
});

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
