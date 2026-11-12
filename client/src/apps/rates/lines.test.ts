import type { LineView, LiveFrame } from './contracts';
import { gridHeaderRequiredPx } from '@ex-grid/core';
import {
  FOLDED, defaultsSaid, hiddenEdits, legOverrides, legSummary, lineCellState, lineColumns, lineFieldTitle, lineHeaderGroups, lineResult, lineRows, openedAt, parserOf, periodFieldTitle,
  toggled,
  type LineLayout,
} from './lines';

const line = (name: string): LineView => ({
  line: name, fields: {}, userFields: [], priceable: true, parLeg: 1, spot: null, profiles: [], issues: [], held: [], defaulted: {},
});

const frame = (inputRevision: number): LiveFrame => ({
  subscriptionId: 's', subscriptionRevision: 1, inputRevision,
  market: { sequence: 3, snapshotId: 'synthetic:t:1:3', asOf: '2026-10-27T00:00:03Z', origin: 'synthetic' },
  completedAt: '2026-10-27T00:00:03Z', provider: 'fake-rates/1', calculationMode: 'simulated',
  rows: [{ rowKey: 'L2', measures: { pv: { value: 12.5, unit: 'JPY' }, parRate: { value: 0.61, unit: '%/leg1' } } }],
  notPriced: [{ line: 'L3', reasons: ['The end is empty.'] }],
});

describe('what a line cell accepts (C40, C43)', () => {
  it('reads an end as a date or a tenor, and refuses anything else', () => {
    expect(parserOf('end')!('5Y')).toEqual({ ok: true, value: '5y' });
    expect(parserOf('end')!('2031-10-29')).toEqual({ ok: true, value: '2031-10-29' });
    expect(parserOf('end')!('five years').ok).toBe(false);
    expect(parserOf('end')!('').ok).toBe(false);
    expect(parserOf('start')!('')).toEqual({ ok: true, value: null });
  });

  it('keeps an empty quote empty, so the leg is solved at par, never zero', () => {
    expect(parserOf('leg1.quote')!('')).toEqual({ ok: true, value: null });
    expect(parserOf('leg1.quoteKind')!('fixed')).toEqual({ ok: true, value: 'FixedRatePercent' });
    expect(parserOf('leg1.quoteKind')!('bp').ok).toBe(false);
  });

  it('takes only the payment frequencies offered, and the second level by its short names', () => {
    expect(parserOf('leg2.paymentFrequency')!('3M')).toEqual({ ok: true, value: '3m' });
    expect(parserOf('leg2.paymentFrequency')!('4m').ok).toBe(false);
    expect(parserOf('leg2.dayCount')!('ACT/365F')).toEqual({ ok: true, value: 'Act365Fixed' });
    expect(parserOf('leg2.adjustment')!('mf')).toEqual({ ok: true, value: 'ModifiedFollowing' });
    expect(parserOf('leg2.lookback')!('2.5').ok).toBe(false);
    expect(parserOf('line')).toBeUndefined();
  });
});

describe('the line grid', () => {
  const layouts: LineLayout[] = [
    FOLDED,
    { trade: true, leg1: 'summary', leg2: 'summary' },
    { trade: false, leg1: 'basic', leg2: 'summary' },
    { trade: false, leg1: 'conventions', leg2: 'basic' },
    { trade: true, leg1: 'conventions', leg2: 'conventions' },
  ];

  it('groups exactly the columns it shows, at every level, each column in one group of a tier', () => {
    for (const layout of layouts) {
      const names = lineColumns(layout).map((c) => c.name);
      const groups = lineHeaderGroups(layout);
      for (const group of groups) for (const c of group.columns) expect(names).toContain(c);
      for (const tier of [1, 2]) {
        // A group stands from its tier down through `tierSpan` tiers (ExGrid ADR-0032).
        const covered = groups.filter((g) => (g.tier ?? 1) >= tier && (g.tier ?? 1) - (g.tierSpan ?? 1) < tier).flatMap((g) => g.columns);
        expect(new Set(covered).size).toBe(covered.length);
      }
    }
  });

  it('ADR-0015 §1: folds each leg into one read-only line until it is opened', () => {
    const folded = lineColumns(FOLDED).map((c) => c.name);
    expect(folded).toEqual(['line', 'Actions', 'product', 'start', 'end', 'direction', 'leg1.summary', 'leg2.summary', 'pv', 'pvCurrency', 'par', 'status', 'message']);
    expect(lineColumns(FOLDED).find((c) => c.name === 'leg1.summary')!.editable).toBeUndefined();
  });

  it('ADR-0015 §2: opens a leg to its basic terms and the overrides, then to its conventions', () => {
    const leg1 = (layout: LineLayout) => lineColumns(layout).map((c) => c.name).filter((n) => n.startsWith('leg1.'));
    expect(leg1({ ...FOLDED, leg1: 'basic' })).toEqual(['leg1.currency', 'leg1.index', 'leg1.notional', 'leg1.quoteKind', 'leg1.quote',
      'leg1.paymentFrequency', 'leg1.resetFrequency', 'leg1.floating', 'leg1.overrides']);
    expect(leg1({ ...FOLDED, leg1: 'conventions' })).toHaveLength(17);
    expect(leg1({ ...FOLDED, leg1: 'conventions' })).not.toContain('leg1.overrides');
  });

  it('ADR-0015 §2: adds a tier under an open leg, and the groups with no level under it stand both tiers', () => {
    expect(lineHeaderGroups(FOLDED).every((g) => g.tier === undefined && g.tierSpan === undefined)).toBe(true);

    const groups = lineHeaderGroups({ ...FOLDED, leg1: 'basic' });
    expect(groups.find((g) => g.id === 'leg1')).toMatchObject({ tier: 2, expanded: true });
    expect(groups.find((g) => g.label === 'Basic')).toMatchObject({ tier: 1 });
    expect(groups.find((g) => g.id === 'leg1.conventions')).toMatchObject({ tier: 1, expanded: false, columns: ['leg1.overrides'] });
    for (const id of ['trade', 'leg2']) expect(groups.find((g) => g.id === id)).toMatchObject({ tier: 2, tierSpan: 2 });
    expect(groups.find((g) => g.label === 'Result')).toMatchObject({ tier: 2, tierSpan: 2 });
  });

  it('ADR-0015 §2, §3: answers each toggle with the next level, and closing a leg closes its conventions', () => {
    let layout = toggled(FOLDED, { id: 'leg2', expanded: true });
    expect(layout.leg2).toBe('basic');
    layout = toggled(layout, { id: 'leg2.conventions', expanded: true });
    expect(layout.leg2).toBe('conventions');
    expect(toggled(layout, { id: 'leg2', expanded: false }).leg2).toBe('summary');
    expect(toggled(FOLDED, { id: 'trade', expanded: true }).trade).toBe(true);
    expect(() => toggled(FOLDED, { id: 'leg3', expanded: true })).toThrow(/not given/);
  });

  it('ADR-0015 §1: opens what a double-clicked folded column folds, and nothing for another column', () => {
    expect(openedAt(FOLDED, 'leg2.summary')).toEqual({ ...FOLDED, leg2: 'basic' });
    expect(openedAt({ ...FOLDED, leg1: 'basic' }, 'leg1.overrides')).toEqual({ ...FOLDED, leg1: 'conventions' });
    expect(openedAt(FOLDED, 'end')).toBeUndefined();
  });

  it('ADR-0015 §4: charges no column menu or sort mark, and sorts or filters no column', () => {
    for (const column of lineColumns(layouts.at(-1)!)) {
      if (column.name === 'Actions') continue;
      expect(column.queryable).toBe(false);
    }
    const end = lineColumns(FOLDED).find((c) => c.name === 'product')!;
    expect(end.width).toBe(Math.max(64, gridHeaderRequiredPx('Product', { density: 'compact', menuButton: false, sortable: false })));
  });

  it('marks a folded group whose hidden columns hold a value the user set, and only while it is folded (ExGrid ADR-0400)', () => {
    const edited = { ...line('L2'), userFields: ['leg2.lookback', 'leg2.notional', 'risk'] };
    const hides = hiddenEdits([line('L1'), edited]);
    // The notional is read by the summary, so folding the leg does not hide it.
    expect(hides).toEqual({ trade: 1, leg1: 0, leg2: 1, leg1Conventions: 0, leg2Conventions: 1 });

    const group = (layout: LineLayout, id: string) => lineHeaderGroups(layout, hides).find((g) => g.id === id)!;
    expect(group(FOLDED, 'leg2')).toMatchObject({ label: 'Leg 2', expanded: false, hiddenMark: { state: 'modified' } });
    expect(group(FOLDED, 'trade')).toMatchObject({ hiddenMark: { state: 'modified' } });
    expect(group({ ...FOLDED, leg2: 'basic' }, 'leg2').hiddenMark).toBeUndefined();
    expect(group({ ...FOLDED, leg2: 'basic' }, 'leg2.conventions')).toMatchObject({ hiddenMark: { state: 'modified' } });
    expect(group({ ...FOLDED, leg2: 'conventions' }, 'leg2.conventions').hiddenMark).toBeUndefined();
    expect(group(FOLDED, 'leg1').hiddenMark).toBeUndefined();
  });

  it('gives a folded column the state of the terms it reads', () => {
    const summary = lineColumns(FOLDED).find((c) => c.name === 'leg2.summary')!;
    const row = (userFields: string[]) => lineRows([{ ...line('L2'), userFields }], 1, undefined, new Map())[0]!;
    expect(lineCellState(row(['leg2.quote']), summary)).toBe('modified');
    expect(lineCellState(row(['leg1.quote', 'leg2.lookback']), summary)).toBe('normal');
  });

  it('puts the inputs first and the result after them (owner, 2026-11-04)', () => {
    const names = lineColumns({ trade: true, leg1: 'summary', leg2: 'summary' }).map((c) => c.name);
    expect(names.slice(0, 3)).toEqual(['line', 'Actions', 'product']);
    expect(names.indexOf('pv')).toBeGreaterThan(names.indexOf('risk'));
    expect(names.indexOf('pv')).toBeGreaterThan(names.indexOf('leg2.summary'));
    expect(names.at(-1)).toBe('message');
  });

  it('marks figures of an older revision stale, and lists a line left out as not priced', () => {
    const rows = lineRows([line('L2'), line('L3')], 7, frame(6), new Map());

    expect(rows[0]!.figures).toMatchObject({ status: 'priced', pv: 12.5, par: 0.61, parMeasure: 'parRate' });
    expect(rows[0]!.figuresStale).toBe(true);
    expect(rows[1]!.figures?.status).toBe('notPriced');
    expect(rows[1]!.figures?.pv).toBeUndefined();
  });

  it('keeps a row object whose figures did not move, so the grid does not paint it again', () => {
    const views = [line('L2')];
    const first = lineRows(views, 6, frame(6), new Map());
    const again = lineRows(views, 6, frame(6), new Map(first.map((r) => [r.line, r])));

    expect(again[0]).toBe(first[0]);
  });
});

describe('a notional in millions (owner, 2026-11-04)', () => {
  const read = parserOf('leg1.notional')!;

  it('reads a bare number as millions, and a scale as written', () => {
    expect(read('1000')).toEqual({ ok: true, value: 1e9 });
    expect(read('2.5')).toEqual({ ok: true, value: 2.5e6 });
    expect(read('3bn')).toEqual({ ok: true, value: 3e9 });
    expect(read('500mm')).toEqual({ ok: true, value: 5e8 });
    // `m` is months (ADR-0008 §2), so it is not a scale.
    expect(read('500m').ok).toBe(false);
    expect(read('')).toEqual({ ok: true, value: null });
  });

  it('refuses a full amount typed out of habit rather than make it a million times larger (spine 1)', () => {
    const typed = read('1,000,000,000');
    expect(typed.ok).toBe(false);
    expect(typed.ok ? '' : typed.message).toContain('millions');
  });

  it('shows a notional in millions under a header that says so', () => {
    const column = lineColumns({ ...FOLDED, leg1: 'basic' }).find((c) => c.name === 'leg1.notional')!;
    expect(column.header).toBe('Notional (mm)');
    expect(column.format!(1e9)).toBe('1,000');
    expect(column.format!(2.5e6)).toBe('2.5');
  });
});

describe("the strip with the focused line's result (owner, 2026-11-04)", () => {
  it('shows the figures with their unit and measure, marked stale while a newer revision waits', () => {
    const [priced, left] = lineRows([line('L2'), line('L3')], 7, frame(6), new Map());

    expect(lineResult(priced!)).toEqual({ line: 'L2', status: 'priced', pv: '13 JPY', par: '0.61000%', parLabel: 'Par rate', stale: true });
    expect(lineResult(left!)).toMatchObject({ line: 'L3', status: 'notPriced', stale: true });
    expect(lineResult(left!).pv).toBeUndefined();
  });

  it('says a line has no figures yet rather than show a zero', () => {
    const [row] = lineRows([{ ...line('L4'), issues: ['The end is empty.'] }], 7, undefined, new Map());
    expect(lineResult(row!)).toEqual({ line: 'L4', status: 'none', stale: false, issues: 'The end is empty.' });
  });
});

describe('a leg in one line (ADR-0015 §1)', () => {
  const irs = {
    'leg1.currency': 'USD', 'leg1.notional': 5e8, 'leg1.quoteKind': 'FixedRatePercent', 'leg1.quote': 4.1, 'leg1.paymentFrequency': '12m', 'leg1.floating': false,
    'leg2.currency': 'USD', 'leg2.notional': 5e8, 'leg2.index': 'SOFR', 'leg2.quoteKind': 'FloatSpreadPercent', 'leg2.quote': 0, 'leg2.paymentFrequency': '12m', 'leg2.floating': true,
  };

  it('reads the currency and notional in the scale they are typed in, what the leg pays, and how often', () => {
    expect(legSummary(irs, 1)).toBe('USD 500mm Fixed 4.10% 12m');
    expect(legSummary({ ...irs, 'leg1.notional': 3e9, 'leg1.quote': null }, 1)).toBe('USD 3bn Fixed par 12m');
    expect(legSummary({ ...irs, 'leg1.quote': 1.125 }, 1)).toBe('USD 500mm Fixed 1.125% 12m');
  });

  it("leaves out leg 2's currency and notional when they are leg 1's, as on every IRS", () => {
    expect(legSummary(irs, 2)).toBe('SOFR +0% 12m');
    expect(legSummary({ ...irs, 'leg2.currency': 'JPY', 'leg2.notional': 1.5e10, 'leg2.index': 'TONA', 'leg2.quote': -0.25, 'leg2.paymentFrequency': '3m' }, 2))
      .toBe('JPY 15bn TONA -0.25% 3m');
  });

  it('says a solved spread is solved, never zero, and reads the other quotations by their unit', () => {
    expect(legSummary({ ...irs, 'leg2.quote': null }, 2)).toBe('SOFR +fair 12m');
    expect(legSummary({ ...irs, 'leg1.quoteKind': 'AtmOffsetBp', 'leg1.quote': 5 }, 1)).toBe('USD 500mm ATM+5bp 12m');
    expect(legSummary({ ...irs, 'leg1.quoteKind': 'FxForwardPoints', 'leg1.quote': 12.5 }, 1)).toBe('USD 500mm 12.5 pts 12m');
  });

  it('marks a term the line does not have with ?, never a made-up value', () => {
    expect(legSummary({ 'leg1.floating': false }, 1)).toBe('? ? Fixed par ?');
  });

  it("names the conventions that override the leg's profile, or says the profile holds", () => {
    expect(legOverrides(irs, 2)).toBe('Profile');
    expect(legOverrides({ ...irs, 'leg2.dayCount': 'Act360', 'leg2.gearing': 0.5 }, 2)).toBe('Day count Act360, Gearing 0.5');
  });
});

describe('what a refused edit calls a field (ADR-0006 note 2 of 2026-11-11)', () => {
  it('is its column\'s header, after its leg\'s where it is a leg\'s, never its key', () => {
    expect(lineFieldTitle('risk')).toBe('Risk');
    expect(lineFieldTitle('direction')).toBe('P/R');
    expect(lineFieldTitle('leg1.quote')).toBe('Leg 1 Rate/Strike');
    expect(lineFieldTitle('leg2.paymentFrequency')).toBe('Leg 2 Pay freq');
    expect(periodFieldTitle('paymentDate')).toBe('Payment');
  });

  it('refuses to name a field no column shows', () => {
    expect(() => lineFieldTitle('leg3.quote')).toThrow('The line grid has no column leg3.quote to name an edit by.');
  });
});

describe("a workspace's defaults for a new line (ADR-0017 §3)", () => {
  it('says each default with where it came from, and nothing where the workspace holds none', () => {
    expect(defaultsSaid({ currency: 'JPY', notional: 500e6, direction: 'receive', fromSettings: ['currency', 'notional', 'direction'] }))
      .toBe('New lines default to JPY · 500mm · receive from your settings.');
    expect(defaultsSaid({ currency: 'JPY', notional: 1e9, direction: 'receive', fromSettings: ['currency', 'direction'] }))
      .toBe('New lines default to JPY · receive from your settings; 1bn set in this workspace.');
    expect(defaultsSaid({ currency: null, notional: 2.5e6, direction: null, fromSettings: [] })).toBe('New lines default to 2.5mm set in this workspace.');
    expect(defaultsSaid({ currency: null, notional: null, direction: null, fromSettings: [] })).toBeUndefined();
  });
});
