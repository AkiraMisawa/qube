import { describe, expect, it } from 'vitest';
import type { RegisteredMeasure } from './contracts';
import { riskSections } from './risks';

// The Risks tab's reading of `portfolio.risks` (the owner's Risks of 2026-11-08, ADR-0010 §8's note of 2026-11-09). Every
// figure and name is synthetic.

const measure = (id: string, title: string): RegisteredMeasure => ({
  id, title, module: 'xva', request: 'xva', metrics: [], headline: id, declaresMembership: false, traced: false, quoted: false, tabs: [],
});
const register = [measure('cva', 'CVA'), measure('fva', 'FVA')];
const row = (over: Readonly<Record<string, unknown>>) => ({
  case: 'base', caseTitle: 'Base', scenario: 'base', scenarioTitle: 'Base market', measure: 'cva', metric: 'cva', metricTitle: 'CVA', currency: 'JPY',
  riskClass: 'Credit', factor: 'cds 5y', bump: '+1bp', before: -120.5, after: -130.25, diff: -9.75, beforeStatus: 'priced', afterStatus: 'priced', ...over,
});

describe('riskSections', () => {
  it('the owner\'s Risks of 2026-11-08: a section per measure of the tab, each delta Before, After and Diff as the request wrote them', () => {
    const rows = [row({}), row({ riskClass: 'FX', factor: 'usdjpy', bump: '+1%', before: 3, after: 4, diff: 1 }), row({ measure: 'fva', metric: 'fva', metricTitle: 'FVA', riskClass: 'IR', factor: 'jpy_ois', before: 1, after: 2, diff: 1 })];
    const [cva, fva] = riskSections(rows, ['cva', 'fva'], register, 'base', 'base');
    expect(cva?.lines.map((l) => [l.riskClass, l.factor, l.bump, l.before.text, l.after.text, l.diff.text])).toEqual([
      ['Credit', 'cds 5y', '+1bp', '-120.50', '-130.25', '-9.75'],
      ['FX', 'usdjpy', '+1%', '3.00', '4.00', '1.00'],
    ]);
    expect([cva?.metricTitle, cva?.currency]).toEqual(['CVA', 'JPY']);
    expect(fva?.lines.map((l) => l.factor)).toEqual(['jpy_ois']);
  });

  it('the owner\'s Risks of 2026-11-08, spine 1: a measure with no deltas says why, never a table of zeros', () => {
    const why = 'Risks were not asked: turn Risks included on in Settings, or set xva risks on, and Price again.';
    const rows = [row({ riskClass: null, factor: null, bump: null, before: null, after: null, diff: null, beforeStatus: null, afterStatus: null, reason: why })];
    const [cva, fva] = riskSections(rows, ['cva', 'fva'], register, 'base', 'base');
    expect(cva).toMatchObject({ lines: [], reason: why });
    // No row at all for a measure is said too, by name.
    expect(fva).toMatchObject({ lines: [], reason: 'This Run returned no deltas of FVA for this case and market.' });
  });

  it('ADR-0010 §4, §7: the section follows the case and the market in view; a row that names no market is refused', () => {
    const rows = [row({}), row({ case: 'c1', factor: 'cds 1y' }), row({ scenario: 's1', factor: 'cds 3y' })];
    expect(riskSections(rows, ['cva'], register, 'base', 'base')[0]?.lines.map((l) => l.factor)).toEqual(['cds 5y']);
    expect(riskSections(rows, ['cva'], register, 'c1', 'base')[0]?.lines.map((l) => l.factor)).toEqual(['cds 1y']);
    expect(riskSections(rows, ['cva'], register, 'base', 's1')[0]?.lines.map((l) => l.factor)).toEqual(['cds 3y']);
    // Every row the request writes names its market: one that does not is never filed under the base market (spine 1).
    expect(() => riskSections([row({ scenario: undefined })], ['cva'], register, 'base', 'base')).toThrow('A row of the deltas has no scenario');
    expect(() => riskSections([row({})], ['kva'], register, 'base', 'base')).toThrow('kva is no registered measure.');
  });

  it('spine 1: a side not priced says so and has no Diff; a delta that names no bump is refused', () => {
    const [cva] = riskSections([row({ before: null, beforeStatus: 'notPriced', diff: null })], ['cva'], register, 'base', 'base');
    expect([cva?.lines[0]?.before.text, cva?.lines[0]?.diff.text]).toEqual(['not priced', '']);
    expect(() => riskSections([row({ bump: null })], ['cva'], register, 'base', 'base')).toThrow(/does not say its risk class or its bump/);
  });
});
