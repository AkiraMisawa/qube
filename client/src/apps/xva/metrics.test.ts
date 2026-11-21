import { describe, expect, it } from 'vitest';
import type { RegisteredMeasure } from './contracts';
import { columnLabel, diffOf, headlines, inMarket, marketsOf, metricsGrid, metricsTable, pricedMeasures, priceLabel, reasons, shownCell, side, tradeCounts, type ModuleResult } from './metrics';

// The Metrics dock as a pure reading of the Run (ADR-0010 §7): a line per case and measure metric, Before, After and
// Diff, and a status in words where there is no figure. Every name and figure is synthetic.

const register: readonly RegisteredMeasure[] = [
  { id: 'cva', title: 'CVA', module: 'xva', request: 'xva', metrics: [{ id: 'cva', title: 'CVA', diffOnly: false }, { id: 'cvaCharge', title: 'CVA Charge', diffOnly: true, heading: ['CVA Charge', 'or Rebate'] }], headline: 'cva', declaresMembership: false, traced: false, quoted: true, tabs: [] },
  { id: 'fva', title: 'FVA', module: 'xva', request: 'xva', metrics: [{ id: 'fva', title: 'FVA', diffOnly: false }], headline: 'fva', declaresMembership: false, traced: false, quoted: false, tabs: [] },
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
      ['CVA', 'CVA', '-100', '-150', '-50'],
      ['CVA', 'CVA Charge', '-125', '-188', '-63'],
      ['FVA', 'FVA', '-10', '-12', '-2'],
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
      metrics: [{ id: 'capital', title: 'Capital', diffOnly: false }], headline: 'capital', declaresMembership: true, traced: true, quoted: false, tabs: [] };

    const table = metricsTable([...register, capital], ['ccr.spotCapital'], { xva: answered([row('ccr.spotCapital', 'capital', null, 1_234.5)]) });

    expect(table.lines.map((l) => [l.measureTitle, l.before.status, l.after.text])).toEqual([['Spot CCR capital', 'notPriced', '1,235']]);
    expect(table.sides).toEqual({ before: false, after: true });
  });

  it("ADR-0012 §7's note: spot IM says each side's governing regime beside its figure, its basis with its figures, and the term each cell opens", () => {
    const spotIm: RegisteredMeasure = { id: 'simm.spotIm', title: 'Spot IM', module: 'simm', request: 'simm',
      metrics: [{ id: 'imR', title: 'IM-R', term: 'collect', diffOnly: false }, { id: 'imP', title: 'IM-P', term: 'post', diffOnly: false }], headline: 'imR', declaresMembership: true, traced: true, quoted: false, tabs: [] };
    const im = (metric: string, before: number | null, after: number | null, beforeNote: string | null, afterNote: string | null) =>
      row('simm.spotIm', metric, before, after, { metricTitle: metric === 'imR' ? 'IM-R' : 'IM-P', beforeNote, afterNote, basis: 'SIMM PARAMETERS DEMO' });

    const table = metricsTable([...register, spotIm], ['cva', 'simm.spotIm'], {
      xva: answered([row('cva', 'cva', -1, -2), row('cva', 'cvaCharge', -1.25, -2.5)]),
      simm: answered([im('imR', 1_000, 1_250.5, 'JFSA', 'CFTC'), im('imP', null, 800, null, 'JFSA')]),
    });

    const [imR, imP] = table.lines.filter((l) => l.measure === 'simm.spotIm');
    expect([imR!.before.note, imR!.after.note, imR!.diff.note, imR!.basis, imR!.term]).toEqual(['JFSA', 'CFTC', undefined, 'SIMM PARAMETERS DEMO', 'collect']);
    // A side with no figure has no regime to name; the basis is the measure's still.
    expect([imP!.before.text, imP!.before.note, imP!.after.note, imP!.term]).toEqual(['not priced', undefined, 'JFSA', 'post']);
    // A measure with nothing to say there says nothing, and its cells open the trace at its root.
    const cva = table.lines.find((l) => l.metric === 'cva')!;
    expect([cva.before.note, cva.basis, cva.term]).toEqual([undefined, undefined, undefined]);
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
      ['FVA', '-12', '-2', undefined],
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
    expect(grid.rows.map((r) => [r.title, Object.values(r.cells).map((l) => l.after.text)])).toEqual([['Workspace', ['-2', '-3', '-12']]]);
  });

  it("ADR-0010 §7's note of 2026-11-21: a metric shown by its Diff alone is one column, and the others keep Before, After and Diff", () => {
    const grid = metricsGrid(metricsTable(register, ['cva', 'fva'], { xva: answered([row('cva', 'cva', -100, -150), row('cva', 'cvaCharge', -125, -187.5), row('fva', 'fva', -10, -12)]) }));

    expect(grid.columns.map((c) => [c.metricTitle, c.sides])).toEqual([
      ['CVA', ['before', 'after', 'diff']], ['CVA Charge', ['diff']], ['FVA', ['before', 'after', 'diff']],
    ]);
    const charge = grid.rows[0]!.cells['cva.cvaCharge']!;
    expect(shownCell(charge, 'diff')).toEqual({ text: '-63', status: 'priced', negative: true });
    // The Run keeps both sides: only the dock leaves them out.
    expect([charge.before.text, charge.after.text]).toEqual(['-125', '-188']);
  });

  it("ADR-0010 §7's note of 2026-11-21: the one column is headed in tiers as registered, with no Diff under it, and the others by their title and side", () => {
    const grid = metricsGrid(metricsTable(register, ['cva', 'fva'], { xva: answered([row('cva', 'cva', -100, -150), row('cva', 'cvaCharge', -125, -187.5), row('fva', 'fva', -10, -12)]) }));
    const [cva, charge] = grid.columns;

    expect(charge!.heading).toEqual(['CVA Charge', 'or Rebate']);
    expect(columnLabel(charge!, 'diff')).toBe('CVA Charge or Rebate');
    expect([cva!.heading, columnLabel(cva!, 'after')]).toEqual([undefined, 'CVA After']);
  });

  it("ADR-0010 §7's note of 2026-11-21: a heading heads only a metric shown by its Diff alone, and one with no line, or a blank one, heads nothing", () => {
    const headed = (metric: { readonly diffOnly: boolean; readonly heading: readonly string[] }) => metricsGrid(metricsTable(
      [{ ...register[0]!, metrics: [{ id: 'cvaCharge', title: 'CVA Charge', ...metric }] }], ['cva'], { xva: answered([row('cva', 'cvaCharge', -125, -187.5)]) })).columns[0]!;

    expect(headed({ diffOnly: false, heading: ['CVA Charge', 'or Rebate'] }).heading).toBeUndefined();
    expect(headed({ diffOnly: true, heading: [] }).heading).toBeUndefined();
    expect(headed({ diffOnly: true, heading: ['CVA Charge', ''] }).heading).toBeUndefined();
    expect(columnLabel(headed({ diffOnly: true, heading: [] }), 'diff')).toBe('CVA Charge Diff');
  });

  it("ADR-0010 §7's note of 2026-11-21: with Before priced nowhere, a metric shown by its Diff alone shows its After, as every metric does", () => {
    const grid = metricsGrid(metricsTable(register, ['cva'], { xva: answered([row('cva', 'cva', null, -150), row('cva', 'cvaCharge', null, -187.5)]) }));

    expect(grid.columns.map((c) => [c.metricTitle, c.sides])).toEqual([['CVA', ['after']], ['CVA Charge', ['after']]]);
    expect(shownCell(grid.rows[0]!.cells['cva.cvaCharge']!, 'after').text).toBe('-188');
  });

  it("ADR-0010 §7's note of 2026-11-21: where a case has no Diff, its one cell says the side it has, named, or the word for why there is none, never blank or 0", () => {
    const vary = { case: 'c1', caseTitle: 'CSA-A threshold 5mm' };
    const grid = metricsGrid(metricsTable(register, ['cva'], { xva: answered([
      row('cva', 'cva', -100, -150), row('cva', 'cvaCharge', -125, -187.5),
      row('cva', 'cva', null, -150, vary), row('cva', 'cvaCharge', null, -187.5, vary),
    ]) }));
    const charge = (caseTitle: string) => grid.rows.find((r) => r.title === caseTitle)!.cells['cva.cvaCharge']!;

    expect(grid.columns.find((c) => c.key === 'cva.cvaCharge')!.sides).toEqual(['diff']);
    expect(shownCell(charge('Workspace'), 'diff')).toEqual({ text: '-63', status: 'priced', negative: true });
    // A one-sided case among two-sided ones gives the charge it adds, as the collapsed line does.
    expect(shownCell(charge('CSA-A threshold 5mm'), 'diff')).toEqual({ text: '-188', status: 'priced', negative: true, note: 'After' });

    const refused = { beforeStatus: 'refused', afterStatus: 'refused', reason: 'No CDS for ABC/LE-01.' };
    const said = metricsTable(register, ['cva'], { xva: answered([row('cva', 'cvaCharge', null, null, refused)]) }).lines[0]!;
    expect(shownCell(said, 'diff')).toEqual({ text: 'refused', status: 'refused' });
    const failed = metricsTable(register, ['cva'], { xva: { kind: 'unavailable', reason: 'The engine stopped.' } }).lines[1]!;
    expect(shownCell(failed, 'diff')).toEqual({ text: 'unavailable', status: 'unavailable' });
    expect(shownCell(metricsTable(register, ['cva'], {}).lines[1]!, 'diff')).toEqual({ text: '…', status: 'pending' });
    // Neither side priced, as where neither held a trade in the measure's scope: said, not left blank.
    const neither = metricsTable(register, ['cva'], { xva: answered([row('cva', 'cvaCharge', null, null, vary)]) }).lines[0]!;
    expect(shownCell(neither, 'diff')).toEqual({ text: 'not priced', status: 'notPriced' });
  });

  it("ADR-0010 §7: a metric shown by all its sides gives each side as the line holds it", () => {
    const line = metricsTable(register, ['cva'], { xva: answered([row('cva', 'cva', null, -150, { case: 'c1', caseTitle: 'c1' })]) }).lines[0]!;

    expect([shownCell(line, 'before'), shownCell(line, 'after'), shownCell(line, 'diff')]).toEqual([line.before, line.after, line.diff]);
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

describe('reasons', () => {
  it('ADR-0010 §7, ADR-0012 §4: says each reason once, as a failure only where a request failed', () => {
    const table = metricsTable(register, ['cva', 'fva'], { xva: answered([
      // A side with nothing in scope has no figure, and nothing failed.
      row('cva', 'cva', null, -2, { reason: 'No trade in Before is in an IM-P scope.' }),
      row('cva', 'cvaCharge', null, -2.5, { reason: 'No trade in Before is in an IM-P scope.' }),
      row('fva', 'fva', -10, null, { afterStatus: 'unavailable', reason: 'The engine did not converge.' }),
    ]) });

    expect(reasons(table)).toEqual([
      { key: 'cva:No trade in Before is in an IM-P scope.', text: 'CVA not priced: No trade in Before is in an IM-P scope.', failed: false },
      { key: 'fva:The engine did not converge.', text: 'FVA unavailable: The engine did not converge.', failed: true },
    ]);
  });

  it('ADR-0012 §3: a refused measure says refused, and not as a failure', () => {
    const table = metricsTable(register, ['cva'], { xva: answered([
      row('cva', 'cva', null, null, { beforeStatus: 'refused', afterStatus: 'refused', reason: "DRAFT-1's IM-R names IMP-ABC-01." }),
    ]) });

    expect(reasons(table).map((r) => [r.text, r.failed])).toEqual([["CVA refused: DRAFT-1's IM-R names IMP-ABC-01.", false]]);
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

  it('ADR-0010 §4: says the pairs it prices, cases crossed with the base market and each included scenario', () => {
    expect(priceLabel(register, ['cva', 'fva'], 3, undefined, 3)).toBe('Price CVA FVA · 3 cases × 3 markets');
    expect(priceLabel(register, ['cva'], 1, undefined, 2)).toBe('Price CVA · 1 case × 2 markets');
  });
});

describe('markets', () => {
  const under = (scenario: string, title: string, before: number, after: number) =>
    [row('cva', 'cva', before, after, { scenario, scenarioTitle: title }), row('fva', 'fva', before / 10, after / 10, { scenario, scenarioTitle: title })];
  const table = metricsTable(register, ['cva', 'fva'], { xva: answered([...under('base', 'Base market', -100, -150), ...under('s1', 'jpy_ois +50bp · usdjpy -10%', -100, -170)]) });

  it('ADR-0010 §4: the scenario selector offers the base market first, then each scenario the Run priced', () => {
    expect(marketsOf(table)).toEqual([{ key: 'base', title: 'Base market' }, { key: 's1', title: 'jpy_ois +50bp · usdjpy -10%' }]);
  });

  it('ADR-0010 §4, §7: the Metrics and the collapsed line follow the scenario in view', () => {
    expect(metricsGrid(inMarket(table, 's1')).rows.map((r) => r.cells['cva.cva']?.after.text)).toEqual(['-170']);
    expect(headlines(register, inMarket(table, 'base')).map((l) => l.text)).toEqual(['Workspace · CVA Diff -50 · FVA Diff -5']);
    expect(headlines(register, inMarket(table, 's1')).map((l) => l.text)).toEqual(['Workspace · CVA Diff -70 · FVA Diff -7']);
  });

  it('ADR-0010 §4: a Run priced before scenarios is the base market alone, and a module not answered stands in every market', () => {
    const before = metricsTable(register, ['cva'], { xva: answered([row('cva', 'cva', -1, -2), row('cva', 'cvaCharge', -1.25, -2.5)]) });
    const pending = metricsTable(register, ['cva'], {});

    expect(marketsOf(before)).toEqual([{ key: 'base', title: 'Base market' }]);
    expect(inMarket(before, 'base').lines).toHaveLength(2);
    expect(inMarket(pending, 's1').lines.map((l) => l.after.status)).toEqual(['pending', 'pending']);
  });
});

describe('headlines', () => {
  it('ADR-0010 §7: the collapsed dock gives each case each measure\'s headline Diff', () => {
    const table = metricsTable(register, ['cva', 'fva'], { xva: answered([row('cva', 'cva', -100, -150), row('cva', 'cvaCharge', -125, -187.5), row('fva', 'fva', -10, -12)]) });

    expect(headlines(register, table)).toEqual([{ key: 'base', text: 'Workspace · CVA Diff -50 · FVA Diff -2' }]);
  });

  it('ADR-0010 §7: a one-sided case shows its priced side, named, and a failed measure its word, never 0', () => {
    const oneSided = metricsTable(register, ['cva'], { xva: answered([row('cva', 'cva', null, -150), row('cva', 'cvaCharge', null, -187.5)]) });
    const failed = metricsTable(register, ['cva'], { xva: { kind: 'unavailable', reason: 'The engine is down.' } });

    expect(headlines(register, oneSided).map((l) => l.text)).toEqual(['Workspace · CVA After -150']);
    expect(headlines(register, failed).map((l) => l.text)).toEqual(['All cases · CVA unavailable']);
  });
});


describe('a metric as an amount (C48)', () => {
  it('shows every side and Diff in whole units, grouped, and marks a loss to be painted red, never one that rounds to 0', () => {
    expect(side('priced', -1_234_567.6)).toEqual({ text: '-1,234,568', status: 'priced', negative: true });
    expect(side('priced', 2_500.4)).toEqual({ text: '2,500', status: 'priced' });
    expect(side('priced', -0.4)).toEqual({ text: '0', status: 'priced' });
    expect(diffOf(side('priced', 10), side('priced', 4), -6)).toEqual({ text: '-6', status: 'priced', negative: true });
  });
});
