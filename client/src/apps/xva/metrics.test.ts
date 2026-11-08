import { describe, expect, it } from 'vitest';
import type { RegisteredMeasure } from './contracts';
import { metricsGrid, metricsTable, pricedMeasures, priceLabel, tradeCounts, type ModuleResult } from './metrics';

// The Metrics dock as a pure reading of the Run (ADR-0010 §7): a line per case and measure metric, Before, After and
// Diff, and a status in words where there is no figure. Every name and figure is synthetic.

const register: readonly RegisteredMeasure[] = [
  { id: 'cva', title: 'CVA', module: 'xva', request: 'xva', metrics: [{ id: 'cva', title: 'CVA' }, { id: 'cvaCharge', title: 'CVA Charge' }], headline: 'cva', declaresMembership: false, traced: false },
  { id: 'fva', title: 'FVA', module: 'xva', request: 'xva', metrics: [{ id: 'fva', title: 'FVA' }], headline: 'fva', declaresMembership: false, traced: false },
];

const row = (measure: string, metric: string, before: number | null, after: number | null, extra: Record<string, unknown> = {}) => ({
  case: 'base', caseTitle: 'Workspace', measure, metric, metricTitle: metric === 'cvaCharge' ? 'CVA Charge' : metric.toUpperCase(), currency: 'JPY',
  before, after, diff: before !== null && after !== null ? after - before : null, tradesBefore: 3, tradesAfter: 4,
  beforeStatus: before === null ? 'notPriced' : 'priced', afterStatus: after === null ? 'notPriced' : 'priced', reason: null, ...extra,
});

const answered = (rows: readonly Record<string, unknown>[]): ModuleResult => ({ kind: 'rows', rows });

describe('metricsTable', () => {
  it('ADR-0010 §7: gives Before, After and Diff for each metric of each priced measure, in the Run\'s order', () => {
    const table = metricsTable(register, ['cva', 'fva'], { xva: answered([row('cva', 'cva', -100, -150), row('cva', 'cvaCharge', -125, -187.5), row('fva', 'fva', -10, -12)]) });

    expect(table.lines.map((l) => [l.measureTitle, l.metricTitle, l.before.text, l.after.text, l.diff.text])).toEqual([
      ['CVA', 'CVA', '-100.00', '-150.00', '-50.00'],
      ['CVA', 'CVA Charge', '-125.00', '-187.50', '-62.50'],
      ['FVA', 'FVA', '-10.00', '-12.00', '-2.00'],
    ]);
    expect(table.sides).toEqual({ before: true, after: true });
    expect(table.note).toBeUndefined();
  });

  it('ADR-0010 §10: shows only the measures the Run priced, whatever else the module answered', () => {
    const table = metricsTable(register, ['cva'], { xva: answered([row('cva', 'cva', -1, -2), row('cva', 'cvaCharge', -1.25, -2.5)]) });

    expect(table.lines.map((l) => l.measure)).toEqual(['cva', 'cva']);
  });

  it("ADR-0013 §2: a measure another module's request prices reads that request's rows, After alone included", () => {
    const capital: RegisteredMeasure = { id: 'ccr.spotCapital', title: 'Spot CCR capital', module: 'ccr', request: 'xva',
      metrics: [{ id: 'capital', title: 'Capital' }], headline: 'capital', declaresMembership: true, traced: true };

    const table = metricsTable([...register, capital], ['ccr.spotCapital'], { xva: answered([row('ccr.spotCapital', 'capital', null, 1_234.5)]) });

    expect(table.lines.map((l) => [l.measureTitle, l.before.status, l.after.text])).toEqual([['Spot CCR capital', 'notPriced', '1,234.50']]);
    expect(table.sides).toEqual({ before: false, after: true });
  });

  it('EX06, ADR-0010 §10: a module whose request failed leaves its measures unavailable, never 0', () => {
    const table = metricsTable(register, ['cva', 'fva'], { xva: { kind: 'unavailable', reason: 'The engine stopped.' } });

    expect(table.lines.map((l) => [l.metricTitle, l.before.text, l.after.text, l.diff.text])).toEqual([
      ['CVA', 'unavailable', 'unavailable', 'unavailable'],
      ['CVA Charge', 'unavailable', 'unavailable', 'unavailable'],
      ['FVA', 'unavailable', 'unavailable', 'unavailable'],
    ]);
    expect(table.lines.every((l) => l.reason === 'The engine stopped.')).toBe(true);
  });

  it('ADR-0010 §10: a measure refused at Price says its reason, and the others stand', () => {
    const refused = { before: null, after: null, diff: null, beforeStatus: 'refused', afterStatus: 'refused', reason: 'No CDS for ABC/LE-01.', tradesBefore: null, tradesAfter: null };
    const table = metricsTable(register, ['cva', 'fva'], { xva: answered([row('cva', 'cva', null, null, refused), row('cva', 'cvaCharge', null, null, refused), row('fva', 'fva', -10, -12)]) });

    expect(table.lines.map((l) => [l.metricTitle, l.after.text, l.diff.text, l.reason])).toEqual([
      ['CVA', 'refused', '', 'No CDS for ABC/LE-01.'],
      ['CVA Charge', 'refused', '', 'No CDS for ABC/LE-01.'],
      ['FVA', '-12.00', '-2.00', undefined],
    ]);
  });

  it('ADR-0010 §7: with Before priced nowhere, Before and Diff are not drawn and the dock says so once', () => {
    const table = metricsTable(register, ['fva'], { xva: answered([row('fva', 'fva', null, -12)]) });

    expect(table.sides).toEqual({ before: false, after: true });
    expect(table.note).toBe('After only: Before was not priced');
    // Blank, never zero: no Diff exists where a side was not priced (docs/design-guidelines.md).
    expect(table.lines[0]!.diff).toEqual({ text: '', status: 'notPriced' });
    expect(table.lines[0]!.before.text).toBe('not priced');
  });

  it('ADR-0010 §10: a figure still being computed is pending, not unavailable', () => {
    const table = metricsTable(register, ['cva'], {});

    expect(table.lines.map((l) => l.after)).toEqual([{ text: '…', status: 'pending' }, { text: '…', status: 'pending' }]);
  });

  it('spine 1: a priced measure the register no longer knows is said, never dropped', () => {
    const table = metricsTable(register, ['kva'], { xva: answered([]) });

    expect(table.lines.map((l) => [l.measureTitle, l.after.text, l.reason])).toEqual([['kva', 'unavailable', 'No measure kva is registered now.']]);
  });

  it('EX06: a row a provider sent without a figure where it says priced is unavailable, never 0', () => {
    const table = metricsTable(register, ['fva'], { xva: answered([row('fva', 'fva', -10, -12, { after: null, diff: null })]) });

    expect(table.lines[0]!.after).toEqual({ text: 'unavailable', status: 'unavailable' });
    expect(table.lines[0]!.diff.text).toBe('unavailable');
  });
});

describe('metricsGrid', () => {
  it('ADR-0010 §7: a row per case and a column group per measure metric', () => {
    const grid = metricsGrid(metricsTable(register, ['cva', 'fva'], { xva: answered([row('cva', 'cva', -1, -2), row('cva', 'cvaCharge', -1.25, -2.5), row('fva', 'fva', -10, -12)]) }));

    expect(grid.columns.map((c) => c.metricTitle)).toEqual(['CVA', 'CVA Charge', 'FVA']);
    expect(grid.rows.map((r) => [r.title, Object.values(r.cells).map((l) => l.after.text)])).toEqual([['Workspace', ['-2.00', '-2.50', '-12.00']]]);
  });

  it('a module that has not answered stands in every case, and alone names no case', () => {
    const grid = metricsGrid(metricsTable(register, ['cva'], {}));

    expect(grid.rows.map((r) => [r.title, Object.values(r.cells).map((l) => l.after.status)])).toEqual([['All cases', ['pending', 'pending']]]);
  });
});

describe('tradeCounts', () => {
  it('ADR-0010 §5: says each measure\'s trades Before → After, FVA counting its members alone', () => {
    const table = metricsTable(register, ['cva', 'fva'], { xva: answered([
      row('cva', 'cva', -1, -2, { tradesBefore: 300, tradesAfter: 302 }), row('cva', 'cvaCharge', -1.25, -2.5, { tradesBefore: 300, tradesAfter: 302 }),
      row('fva', 'fva', -10, -12, { tradesBefore: 120, tradesAfter: 121 }),
    ]) });

    expect(tradeCounts(table)).toEqual(['CVA 300 → 302', 'FVA 120 → 121']);
  });
});

describe('pricedMeasures', () => {
  it('ADR-0010 §10: reads the measures from the Run\'s input', () => {
    expect(pricedMeasures({ workspaceId: 'w', revision: 3, measures: 'cva fva' })).toEqual(['cva', 'fva']);
  });

  it('a Run priced before the register says none, rather than a guess', () => {
    expect(pricedMeasures({ workspaceId: 'w', revision: 3 })).toBeUndefined();
  });
});

describe('priceLabel', () => {
  it('ADR-0010 §1: Price says what it will run', () => {
    expect(priceLabel(register, ['cva', 'fva'], 1)).toBe('Price CVA FVA · 1 case');
    expect(priceLabel(register, ['fva'], 3)).toBe('Price FVA · 3 cases');
  });

  it('ADR-0010 §3: says when a side has no trade and will not be priced', () => {
    expect(priceLabel(register, ['cva', 'fva'], 1, { before: false, after: true })).toBe('Price After only · CVA FVA · 1 case');
    expect(priceLabel(register, ['cva'], 1, { before: true, after: false })).toBe('Price Before only · CVA · 1 case');
  });
});
