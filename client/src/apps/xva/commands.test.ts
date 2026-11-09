import { describe, expect, it } from 'vitest';
import { measureNamed, measuresTyped, varyTyped } from './commands';
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

describe('`vary` as typed (ADR-0010 §9\'s note of 2026-11-08)', () => {
  it('takes the cell, then the values separated by commas, each as written', () => {
    expect(varyTyped('CSA-A threshold 10mm, 5mm, 0')).toEqual({ cell: 'CSA-A threshold', values: ['10mm', '5mm', '0'] });
    expect(varyTyped('  CSA-A self threshold 1mm ')).toEqual({ cell: 'CSA-A self threshold', values: ['1mm'] });
  });

  it('refuses a line with no cell or an empty value by name, never guessing one', () => {
    expect(() => varyTyped('CSA-A 10mm')).toThrow('vary needs a cell, its row and its column, and the values');
    expect(() => varyTyped('CSA-A threshold 10mm, , 0')).toThrow('none of them empty');
  });
});
