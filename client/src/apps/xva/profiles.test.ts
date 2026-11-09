import { describe, expect, it } from 'vitest';
import { chartSeries, profilesOf, profileTable } from './profiles';

// The Profile tab's reading of `portfolio.profiles` (the owner's Profile of 2026-11-08, ADR-0010 §8's note of 2026-11-09).
// Every figure is synthetic.

const point = (side: 'before' | 'after', series: string, years: number, value: number, over: Readonly<Record<string, unknown>> = {}) => ({
  case: 'base', caseTitle: 'Base', scenario: 'base', scenarioTitle: 'Base market', side, profile: 'exposure', profileTitle: 'Exposure',
  series, seriesTitle: series.toUpperCase(), years, date: `2026-1${years}-26`, value, currency: 'JPY', ...over,
});

describe('profilesOf', () => {
  it('the owner\'s Profile of 2026-11-08: the profiles the Run returned for the case and market in view, in its order', () => {
    const rows = [point('before', 'ee', 0, 10), point('after', 'ead', 0, 5, { profile: 'capital', profileTitle: 'Capital', currency: 'USD' }), point('after', 'ee', 0, 1, { case: 'c1' })];
    expect(profilesOf(rows, 'base', 'base')).toEqual([
      { id: 'exposure', title: 'Exposure', currency: 'JPY' },
      { id: 'capital', title: 'Capital', currency: 'USD' },
    ]);
    expect(profilesOf(rows, 'c1', 'base').map((p) => p.id)).toEqual(['exposure']);
    expect(profilesOf(rows, 'base', 's1')).toEqual([]);
  });
});

describe('profileTable', () => {
  const exposure = { id: 'exposure', title: 'Exposure', currency: 'JPY' };

  it('the owner\'s Profile of 2026-11-08: a row per date, each series Before, After and Diff', () => {
    const rows = [point('before', 'ee', 0, 100), point('after', 'ee', 0, 130), point('before', 'ene', 0, -20), point('after', 'ene', 0, -25),
      point('after', 'ee', 1, 60), point('before', 'ee', 1, 50)];
    const table = profileTable(rows, exposure, 'base', 'base');
    expect(table.series).toEqual([{ id: 'ee', title: 'EE' }, { id: 'ene', title: 'ENE' }]);
    expect(table.rows.map((r) => [r.years, r.values.ee])).toEqual([
      [0, { before: 100, after: 130, diff: 30 }],
      [1, { before: 50, after: 60, diff: 10 }],
    ]);
    expect(table.sides).toEqual({ before: true, after: true });
  });

  it('spine 1: past a side\'s last maturity it has no point, and no Diff: never a 0 made up; a point with no figure is refused', () => {
    const rows = [point('before', 'ee', 0, 100), point('after', 'ee', 0, 130), point('after', 'ee', 2, 0)];
    const late = profileTable(rows, exposure, 'base', 'base').rows[1];
    expect(late?.values.ee).toEqual({ before: undefined, after: 0, diff: undefined });
    expect(() => profileTable([point('after', 'ee', 0, Number.NaN)], exposure, 'base', 'base')).toThrow(/cannot be read/);
  });

  it('ADR-0010 §7: a side the Run did not price has no points, and the chart has no series of it', () => {
    const table = profileTable([point('after', 'ee', 0, 130), point('after', 'ee', 1, 60)], exposure, 'base', 'base');
    expect(table.sides).toEqual({ before: false, after: true });
    expect(chartSeries(table)).toEqual([{ id: 'ee.after', name: 'EE After', series: 'ee', side: 'after', points: [{ x: 0, y: 130 }, { x: 1, y: 60 }] }]);
  });
});
