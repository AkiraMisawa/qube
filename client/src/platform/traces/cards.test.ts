import { describe, expect, it } from 'vitest';
import type { SideValue, TermPage, TermRead, TraceHeader } from './contracts';
import { rootCard, rootOf } from './cards';

// A trace tab's cards (ADR-0010 §7, ADR-0012 §7): one per root of the trace, with what the root's key names beyond its
// label, the operand that governed it, and its figure on each side the case priced and its Diff. Synthetic and
// non-financial: a farm draws on the larger of two wells, in the north and in the south, each under its permit.

const litres = { kind: 'amount', currency: 'L' } as const;
const side = (value: number, governs?: boolean): SideValue =>
  ({ state: 'value', value, check: { status: 'matches' }, written: true, ...(governs === undefined ? {} : { governs }) });
const term = (id: string, label: string, extra: Partial<TermRead> = {}): TermRead =>
  ({ id, kind: 'k', label, symbol: 'W', childCount: 0, unit: litres, ...extra });
const header = (sides: TraceHeader['sides']): TraceHeader => ({
  runId: 'run-1', case: 'base', scenario: 'base', measure: 'farm.draw', method: { id: 'farm', version: '1' }, depth: 'well', sides, origins: [],
  roots: [{ id: 'north', label: 'Draw · north', symbol: 'W' }, { id: 'south', label: 'Draw · south', symbol: 'W' }],
});
const page = (root: TermRead, children: readonly TermRead[]): TermPage => ({ term: root, path: [], children, childTotal: children.length, offset: 0 });
const wells = (before: readonly [boolean, boolean] | undefined, after: readonly [boolean, boolean] | undefined) => ['A', 'B'].map((w, i) =>
  term(`north/${w}`, `Well ${w}`, { ...(before ? { before: side(10 * (i + 1), before[i]) } : {}), ...(after ? { after: side(10 * (i + 1), after[i]) } : {}) }));

describe('rootCard', () => {
  it('ADR-0012 §7: names what its key adds, the operand its children say governed, and Before, After and Diff in its unit', () => {
    const root = term('north', 'Draw · north', {
      key: { field: 'north', permit: 'P-1' }, childCount: 2,
      before: side(1000), after: side(1500.5), diff: { state: 'value', value: 500.5 },
    });

    expect(rootCard(page(root, wells([true, false], [true, false])), header(['before', 'after']))).toEqual({
      id: 'north', label: 'Draw · north', detail: 'P-1', governing: 'Well A governs',
      figures: [
        { key: 'before', title: 'Before (L)', text: '1,000.00' },
        { key: 'after', title: 'After (L)', text: '1,500.50' },
        { key: 'diff', title: 'Diff (L)', text: '500.50' },
      ],
    });
  });

  it('ADR-0011 §4: a case priced on one side has that side alone, and sides governed by different operands say so', () => {
    const one = term('north', 'Draw · north', { key: { field: 'north' }, after: side(1500) });
    expect(rootCard(page(one, wells(undefined, [false, true])), header(['after']))).toMatchObject({
      detail: undefined, governing: 'Well B governs', figures: [{ key: 'after', title: 'After (L)', text: '1,500.00' }],
    });

    const moved = term('north', 'Draw · north', { before: side(1000), after: side(1500), diff: { state: 'value', value: 500 } });
    expect(rootCard(page(moved, wells([true, false], [false, true])), header(['before', 'after'])).governing).toBe('Before: Well A governs · After: Well B governs');
  });

  it('ADR-0011 §2, §6: names no operand where no child says it governs, rather than one guessed from its place', () => {
    const root = term('north', 'Draw · north', { after: side(1500) });
    expect(rootCard(page(root, wells(undefined, undefined)), header(['after'])).governing).toBeUndefined();
  });

  it('ADR-0012 §1: a root with nothing in scope says absent, never 0, and names no governing operand', () => {
    const absent = term('south', 'Draw · south', { before: { state: 'absent', check: { status: 'notApplicable' }, written: true } });
    expect(rootCard(page(absent, []), header(['before']))).toMatchObject({ governing: undefined, figures: [{ key: 'before', text: 'absent' }] });
  });
});

describe('rootOf', () => {
  it('ADR-0012 §7: a term asked for chooses the card of the root it lies under, by its path, and none for a path under no root', () => {
    const roots = ['north', 'south'];
    expect(rootOf(roots, 'south')).toBe('south');
    expect(rootOf(roots, 'north/A/pump 1')).toBe('north');
    expect(rootOf(roots, 'northern')).toBeUndefined();
    expect(rootOf(roots, undefined)).toBeUndefined();
  });
});
