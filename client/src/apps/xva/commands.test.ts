import { describe, expect, it } from 'vitest';
import { ambiguousComma, cdsCompletions, measureNamed, measuresTyped, varyHint, varyTyped } from './commands';
import type { CdsCatalogue, CdsCurve, RegisteredMeasure } from './contracts';

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

describe('A list of values separated by commas (ADR-0010 §9, spine 1)', () => {
  it('is refused where a comma stands between two digits, never split as a guess', () => {
    expect(ambiguousComma('10,000k, 5mm')).toBe('10,000k could be one value or two: a comma separates values here, so write an amount without separators, such as 10000k or 10mm.');
    expect(() => varyTyped('CSA-A threshold 5,10')).toThrow(/^5,10 could be one value or two/);
  });

  it('reads a comma after a unit, or before a space, as a separator', () => {
    expect(ambiguousComma('10mm,5mm, 0')).toBeUndefined();
    expect(varyTyped('CSA-A threshold 10mm,5mm, 0').values).toEqual(['10mm', '5mm', '0']);
  });
});

describe('`cds` completes from the catalogue (ADR-0010 §8\'s note of 2026-11-09)', () => {
  const name = (ticker: string, currency: string, seniority: string): CdsCurve => ({
    id: `${ticker}-${currency}-${seniority}`, type: 'singleName', ticker, currency, seniority, docClause: 'XR14', name: `${ticker} Holdings (synthetic)`, recovery: 0.4,
    spreads: {}, reference: `${ticker} | ${currency} | ${seniority} | XR14 | ${ticker} Holdings (synthetic)`,
  });
  const catalogue: CdsCatalogue = {
    singleNames: [name('ABC', 'USD', 'SNR'), name('ABC', 'JPY', 'SNR'), name('XYZ', 'USD', 'SUB')],
    sectors: ['financials', 'utilities'], regions: ['asia'], ratings: ['A', 'BBB'], tenors: ['1y', '5y'],
  };
  const values = (typed: string) => cdsCompletions(typed, catalogue).map((c) => c.value);

  it('offers none, proxy and the tickers first, then the other words of the names still matched', () => {
    expect(values('')).toEqual(['none', 'proxy', 'ABC', 'USD', 'SNR', 'XR14', 'JPY', 'XYZ', 'SUB']);
    expect(values('abc')).toEqual(['USD', 'SNR', 'XR14', 'JPY']);
    expect(values('abc usd')).toEqual(['SNR', 'XR14']);
    expect(cdsCompletions('', catalogue)[2]).toEqual({ value: 'ABC', detail: 'ABC | USD | SNR | XR14 | ABC Holdings (synthetic)' });
  });

  it('after proxy, offers the keys of the kinds it has none of yet; nothing before the catalogue is read', () => {
    expect(values('proxy')).toEqual(['financials', 'utilities', 'asia', 'A', 'BBB']);
    expect(values('proxy utilities a')).toEqual(['asia']);
    expect(cdsCompletions('', undefined)).toEqual([]);
  });
});

describe('the hint of `vary` (ADR-0006 note 4 of 2026-11-11)', () => {
  it('shows a row of the open workspace, never an id no workspace has', () => {
    expect(varyHint({ csa: 'CSA-A' })).toBe('a row and a column, then the values separated by commas: CSA-A threshold 10mm, 5mm, 0');
  });

  it('says the form alone while the page has no row to show', () => {
    expect(varyHint({ csa: undefined })).toBe('a row and a column, then the values separated by commas');
    expect(varyHint(undefined)).toBe('a row and a column, then the values separated by commas');
  });
});
