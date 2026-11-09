import { describe, expect, it } from 'vitest';
import type { RegisteredMeasure } from './contracts';
import { conversionOf, dockTabs, METRICS_TAB, unpricedReasons } from './dock';
import type { MetricLine, MetricsTable } from './metrics';

// The result dock's tabs (ADR-0010 §7, §10): Metrics, then the tabs of the selected measures the Run in view priced, in
// the register's order. Every name is synthetic.

const measure = (id: string, title: string, tabs: RegisteredMeasure['tabs'] = []): RegisteredMeasure => ({
  id, title, module: id.split('.')[0]!, request: 'xva', metrics: [], headline: 'x', declaresMembership: false, traced: tabs.length > 0, quoted: false, tabs,
});
const register = [
  measure('cva', 'CVA'),
  measure('ccr.spotCapital', 'Spot CCR capital', [{ id: 'ccr', title: 'CCR', view: 'trace' }]),
  measure('simm.spotIm', 'Spot IM', [{ id: 'simm', title: 'SIMM', view: 'trace' }]),
];

describe('dockTabs', () => {
  const all = ['cva', 'ccr.spotCapital', 'simm.spotIm'];

  it('ADR-0010 §7, ADR-0012 §7: Metrics, then each priced measure\'s own tabs in the register\'s order', () => {
    expect(dockTabs(register, ['simm.spotIm', 'cva', 'ccr.spotCapital'], all)).toEqual([
      METRICS_TAB,
      { id: 'ccr', title: 'CCR', view: 'trace', measure: 'ccr.spotCapital', measures: ['ccr.spotCapital'] },
      { id: 'simm', title: 'SIMM', view: 'trace', measure: 'simm.spotIm', measures: ['simm.spotIm'] },
    ]);
  });

  it('ADR-0010 §10: a measure the Run did not price has no tab, and a Run priced before the register has Metrics alone', () => {
    expect(dockTabs(register, ['cva'], all).map((t) => t.id)).toEqual(['metrics']);
    expect(dockTabs(register, undefined, all).map((t) => t.id)).toEqual(['metrics']);
  });

  it('ADR-0010 §8\'s note of 2026-11-09: a tab several measures register is one tab, listing those priced and selected', () => {
    const risks = { id: 'risks', title: 'Risks', view: 'risks' } as const;
    const profile = { id: 'profile', title: 'Profile', view: 'profile' } as const;
    const xva = [measure('cva', 'CVA', [risks, profile]), measure('fva', 'FVA', [risks, profile]), measure('kva', 'KVA', [profile])];
    expect(dockTabs(xva, ['cva', 'fva', 'kva'], ['cva', 'fva', 'kva'])).toEqual([
      METRICS_TAB,
      { ...risks, measure: 'cva', measures: ['cva', 'fva'] },
      { ...profile, measure: 'cva', measures: ['cva', 'fva', 'kva'] },
    ]);
    expect(dockTabs(xva, ['cva', 'fva', 'kva'], ['kva']).map((t) => (t.view === 'metrics' ? t.id : `${t.id}:${t.measures.join('+')}`))).toEqual(['metrics', 'profile:kva']);
  });

  it('ADR-0010 §7 (CONFIRMED: the selected measures\' tabs): a measure left out after the Price loses its tab', () => {
    expect(dockTabs(register, ['cva', 'simm.spotIm', 'ccr.spotCapital'], ['cva', 'ccr.spotCapital']).map((t) => t.id)).toEqual(['metrics', 'ccr']);
  });
});

describe('unpricedReasons', () => {
  const line = (measure: string, status: MetricLine['before']['status'], reason?: string): MetricLine => ({
    key: `${measure}:${status}`, case: undefined, caseTitle: undefined, measure, measureTitle: measure === 'cva' ? 'CVA' : 'Spot IM', metric: 'm', metricTitle: 'M',
    currency: undefined, before: { text: status, status }, after: { text: status, status }, diff: { text: '', status: 'priced' }, reason,
  } as MetricLine);
  const table = (lines: readonly MetricLine[]): MetricsTable => ({ lines, sides: { before: true, after: true }, note: undefined });

  it('ADR-0012 §1, §7: a measure priced on no side says why in its tab, rather than read a trace that is not there', () => {
    const none = table([line('cva', 'priced'), line('simm.spotIm', 'notPriced', 'XYZ/LE-03 has no IM CSA.')]);
    expect(unpricedReasons(none, 'simm.spotIm')).toEqual([{ text: 'Spot IM not priced: XYZ/LE-03 has no IM CSA.', failed: false }]);
    // Priced, pending, or not in the Metrics: the tab draws its view.
    expect(unpricedReasons(table([line('simm.spotIm', 'priced')]), 'simm.spotIm')).toBeUndefined();
    expect(unpricedReasons(table([line('simm.spotIm', 'pending')]), 'simm.spotIm')).toBeUndefined();
    expect(unpricedReasons(table([]), 'simm.spotIm')).toBeUndefined();
    expect(unpricedReasons(undefined, 'simm.spotIm')).toBeUndefined();
  });

  it('ADR-0012 §7: the rate a measure\'s figures were converted at is said once, from its own lines', () => {
    const rate = 'Computed in USD; shown in JPY at 150 JPY per USD, the pinned FX of 2026-10-26.';
    const converted = table([{ ...line('simm.spotIm', 'priced'), conversion: rate }, { ...line('simm.spotIm', 'priced'), key: 'b', conversion: rate }, line('cva', 'priced')]);
    expect(conversionOf(converted, 'simm.spotIm')).toBe(rate);
    expect(conversionOf(converted, 'cva')).toBeUndefined();
  });
});
