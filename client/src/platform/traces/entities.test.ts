import { describe, expect, it } from 'vitest';
import type { LeafSetKind, TermRead, TraceCatalog } from './contracts';
import { belowViews, checkLine, entitySetOf, splits } from './entities';

// ADR-0012 §6: the entities below a term, in words. Synthetic: an orchard's trees and their keepers.

const trees: LeafSetKind = {
  leafSet: 'orchard.trees', entityField: 'keeper', componentField: 'tree', contributionField: 'share',
  columns: [{ name: 'keeper', label: 'Keeper' }, { name: 'tree', label: 'Tree' }, { name: 'share', label: 'Share', unit: { kind: 'amount' } }],
};
const catalog = (sets: readonly LeafSetKind[]): TraceCatalog => ({ method: { id: 'orchard', version: '1' }, kinds: [], leafSets: sets });
const term = (extra: Partial<TermRead>): TermRead => ({ id: 't', kind: 'k', label: 'T', symbol: 'T', childCount: 0, ...extra });
const allocated = { state: 'value' as const, value: 3, check: { status: 'matches' as const }, written: true, allocation: { method: 'euler', value: 3 } };

describe('the entities below a term (ADR-0012 §6)', () => {
  it('are offered only where a leaf set carries a contribution and the term has an allocation to split', () => {
    expect(entitySetOf(catalog([trees]))).toBe(trees);
    expect(entitySetOf(catalog([{ ...trees, contributionField: null }]))).toBeUndefined();
    expect(splits(term({ after: allocated }), ['before', 'after'])).toBe(true);
    // A max over operands has no allocation: it is not split, rather than summed twice.
    expect(splits(term({ after: { ...allocated, allocation: null } }), ['after'])).toBe(false);
  });

  it('ADR-0012 §6\'s note of 2026-11-08: at a term that splits, by entity and by row at every depth; else its own rows, in sentence case', () => {
    expect(belowViews(trees, true, undefined)).toEqual([
      { value: 'entities', label: 'By keeper', below: true, set: trees },
      { value: 'rows', label: 'By tree', below: true, set: trees },
    ]);
    expect(belowViews(trees, false, trees)).toEqual([{ value: 'rows', label: 'By tree', below: false, set: trees }]);
    expect(belowViews(undefined, true, { ...trees, componentField: null }).map((v) => v.label)).toEqual(['By row']);
    expect(belowViews(trees, false, undefined)).toEqual([]);
    // An initialism keeps its capitals.
    const lines = { ...trees, columns: [...trees.columns.slice(0, 1), { name: 'tree', label: 'CRIF line' }, ...trees.columns.slice(2)] };
    expect(belowViews(lines, true, undefined).map((v) => v.label)).toEqual(['By keeper', 'By CRIF line']);
  });

  it('are read in one leaf set\'s columns, so a catalogue with two contribution sets has no entity view', () => {
    expect(entitySetOf(catalog([trees, { ...trees, leafSet: 'orchard.vines' }]))).toBeUndefined();
  });

  it('say once that they add up, and a side that does not with both figures', () => {
    const unit = { kind: 'amount' };
    expect(checkLine([{ side: 'before', status: 'matches', sum: 3.5, allocation: 3.5 }, { side: 'after', status: 'matches', sum: 7, allocation: 7 }], unit))
      .toEqual({ text: 'They add up to its allocation: Before 3.50 · After 7.00', error: false });
    expect(checkLine([{ side: 'after', status: 'inconsistent', sum: 7, allocation: 9 }], unit))
      .toEqual({ text: 'They do not add up: 7.00 by its entities, 9.00 allocated', error: true });
    expect(checkLine([{ side: 'before', status: 'inconsistent', sum: 1, allocation: 2 }, { side: 'after', status: 'matches', sum: 7, allocation: 7 }], unit)?.text)
      .toBe("Before's do not add up: 1.00 by its entities, 2.00 allocated");
    expect(checkLine([], unit)).toBeUndefined();
  });
});
