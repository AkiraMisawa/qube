import { describe, expect, it } from 'vitest';
import type { SideValue, TermPage, TermRead, TraceHeader } from './contracts';
import { rootCard, rootOf } from './cards';

// A trace tab's cards (ADR-0012 §7: the two directions' summaries): one per root of the trace, with what the root's
// key names beyond its label, the operand that governed it, and its figure on each side the case priced and its Diff.
// Synthetic terms, in the shapes the trace endpoint answers.

const usd = { kind: 'amount', currency: 'USD' } as const;
const side = (value: number, governed?: number): SideValue =>
  ({ state: 'value', value, check: { status: 'matches' }, written: true, ...(governed === undefined ? {} : { governed }) });
const term = (id: string, label: string, extra: Partial<TermRead> = {}): TermRead =>
  ({ id, kind: 'k', label, symbol: 'X', childCount: 0, unit: usd, ...extra });
const header = (sides: TraceHeader['sides']): TraceHeader => ({
  runId: 'run-1', case: 'base', scenario: 'base', measure: 'm', method: { id: 'm', version: '1' }, depth: 'line', sides, origins: [],
  roots: [{ id: 'collect', label: 'IM-R · collect', symbol: 'IM' }, { id: 'post', label: 'IM-P · post', symbol: 'IM' }],
});
const page = (root: TermRead, children: readonly TermRead[]): TermPage => ({ term: root, path: [], children, childTotal: children.length, offset: 0 });

const regimes = [term('collect/A', 'A regime'), term('collect/B', 'B regime')];

describe('rootCard', () => {
  it('ADR-0012 §7: names what its key adds, the operand that governed, and Before, After and Diff in its unit', () => {
    const root = term('collect', 'IM-R · collect', {
      key: { direction: 'collect', csa: 'IMR-1' }, childCount: 2,
      before: side(1000, 0), after: side(1500.5, 0), diff: { state: 'value', value: 500.5 },
    });

    expect(rootCard(page(root, regimes), header(['before', 'after']))).toEqual({
      id: 'collect', label: 'IM-R · collect', detail: 'IMR-1', governing: 'A regime governs',
      figures: [
        { key: 'before', title: 'Before (USD)', text: '1,000.00' },
        { key: 'after', title: 'After (USD)', text: '1,500.50' },
        { key: 'diff', title: 'Diff (USD)', text: '500.50' },
      ],
    });
  });

  it('ADR-0011 §4: a case priced on one side has that side alone, and sides governed by different operands say so', () => {
    const one = term('collect', 'IM-R · collect', { key: { direction: 'collect' }, after: side(1500, 1) });
    expect(rootCard(page(one, regimes), header(['after']))).toMatchObject({
      detail: undefined, governing: 'B regime governs', figures: [{ key: 'after', title: 'After (USD)', text: '1,500.00' }],
    });

    const moved = term('collect', 'IM-R · collect', { before: side(1000, 0), after: side(1500, 1), diff: { state: 'value', value: 500 } });
    expect(rootCard(page(moved, regimes), header(['before', 'after'])).governing).toBe('Before: A regime governs · After: B regime governs');
  });

  it('ADR-0012 §1: a root with nothing in scope says absent, never 0, and names no governing operand', () => {
    const absent = term('post', 'IM-P · post', { before: { state: 'absent', check: { status: 'notApplicable' }, written: true } });
    expect(rootCard(page(absent, []), header(['before']))).toMatchObject({ governing: undefined, figures: [{ key: 'before', text: 'absent' }] });
  });
});

describe('rootOf', () => {
  it('finds the root a term lies under, by its path, and none for a path under no root', () => {
    const roots = ['collect', 'post'];
    expect(rootOf(roots, 'post')).toBe('post');
    expect(rootOf(roots, 'collect/JFSA/RatesFX')).toBe('collect');
    expect(rootOf(roots, 'collector')).toBeUndefined();
    expect(rootOf(roots, undefined)).toBeUndefined();
  });
});
