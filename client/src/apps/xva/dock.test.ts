import { describe, expect, it } from 'vitest';
import type { RegisteredMeasure } from './contracts';
import { dockTabs, METRICS_TAB, unpricedReasons } from './dock';
import type { MetricLine, MetricsTable } from './metrics';

// The result dock's tabs (ADR-0010 §7, §10): Metrics, then the tabs of the measures the Run in view priced, in the
// register's order. They follow the Run, as its Metrics do, never the selection on screen. Every name is synthetic.

const measure = (id: string, title: string, tabs: RegisteredMeasure['tabs'] = []): RegisteredMeasure => ({
  id, title, module: id.split('.')[0]!, request: 'xva', metrics: [], headline: 'x', declaresMembership: false, traced: tabs.length > 0, quoted: false, tabs,
});
const register = [
  measure('cva', 'CVA'),
  measure('ccr.spotCapital', 'Spot CCR capital', [{ id: 'ccr', title: 'CCR', view: 'trace' }]),
  measure('simm.spotIm', 'Spot IM', [{ id: 'simm', title: 'SIMM', view: 'trace' }]),
];

describe('dockTabs', () => {
  it('ADR-0010 §7, ADR-0012 §7: Metrics, then each priced measure\'s own tabs in the register\'s order', () => {
    expect(dockTabs(register, ['simm.spotIm', 'cva', 'ccr.spotCapital'])).toEqual([
      METRICS_TAB,
      { id: 'ccr', title: 'CCR', view: 'trace', measure: 'ccr.spotCapital' },
      { id: 'simm', title: 'SIMM', view: 'trace', measure: 'simm.spotIm' },
    ]);
  });

  it('ADR-0010 §10: a measure the Run did not price has no tab, and a Run priced before the register has Metrics alone', () => {
    expect(dockTabs(register, ['cva']).map((t) => t.id)).toEqual(['metrics']);
    expect(dockTabs(register, undefined).map((t) => t.id)).toEqual(['metrics']);
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
  });
});
