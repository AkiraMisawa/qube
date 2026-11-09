import { describe, expect, it } from 'vitest';
import { measureNamed, measuresTyped, setTyped, varyTyped } from './commands';
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

describe('`set` as typed (ADR-0010 §9)', () => {
  it('takes one row, a column of one word or more, and the value, its last word', () => {
    expect(setTyped('CSA-A threshold 5mm')).toEqual({ rows: ['CSA-A'], column: 'threshold', text: '5mm' });
    expect(setTyped(' CSA-A  self threshold 10,000k ')).toEqual({ rows: ['CSA-A'], column: 'self threshold', text: '10,000k' });
  });

  it('takes the rows as a list separated by commas, with or without a space', () => {
    expect(setTyped('T-1001, T-1002 csa CSA-B')).toEqual({ rows: ['T-1001', 'T-1002'], column: 'csa', text: 'CSA-B' });
    expect(setTyped('T-1001,T-1002, T-1003 A off')).toEqual({ rows: ['T-1001', 'T-1002', 'T-1003'], column: 'A', text: 'off' });
  });

  it('refuses a line with no column or value, or an empty row, rather than guess', () => {
    expect(() => setTyped('CSA-A 5mm')).toThrow(/set needs a row, a column and a value/);
    expect(() => setTyped('T-1001,, T-1002 csa CSA-B')).toThrow(/set needs a row/);
  });
});
