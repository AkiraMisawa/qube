import { describe, expect, it } from 'vitest';
import type { EntityRow, LeafSetKind, TermRead, TraceCatalog } from './contracts';
import { belowViews, checkLine, entitySetOf, entitySides, splits } from './entities';

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

  it('are offered by entity first, then by the term\'s own rows, named by the leaf set\'s own labels', () => {
    expect(belowViews(trees, true, trees)).toEqual([{ value: 'entities', label: 'By Keeper' }, { value: 'rows', label: 'By Tree' }]);
    expect(belowViews(trees, true, undefined)).toEqual([{ value: 'entities', label: 'By Keeper' }]);
    expect(belowViews(trees, false, trees)).toEqual([{ value: 'rows', label: 'By Tree' }]);
    expect(belowViews(undefined, true, { ...trees, componentField: null })).toEqual([{ value: 'rows', label: 'By row' }]);
  });

  it('say which sides hold an entity where both were priced', () => {
    const row = (extra: Partial<EntityRow>): EntityRow => ({ leafSet: 'orchard.trees', entity: 'ann', ...extra });
    expect(entitySides(row({ before: { contribution: 1, rows: 1 }, after: { contribution: 2, rows: 1 } }), ['before', 'after'])).toBe('Before and After');
    expect(entitySides(row({ after: { contribution: 2, rows: 1 } }), ['before', 'after'])).toBe('After only');
    expect(entitySides(row({ after: { contribution: 2, rows: 1 } }), ['after'])).toBe('');
  });

  it('say once that they add up, and a side that does not with both figures', () => {
    const unit = { kind: 'amount' };
    expect(checkLine([{ side: 'before', status: 'matches', sum: 3.5, allocation: 3.5 }, { side: 'after', status: 'matches', sum: 7, allocation: 7 }], unit))
      .toEqual({ text: 'They add up to its allocation: Before 3.50 · After 7.00', error: false });
    expect(checkLine([{ side: 'after', status: 'inconsistent', sum: 7, allocation: 9 }], unit))
      .toEqual({ text: 'does not add up: 7.00 by its entities, 9.00 allocated', error: true });
    expect(checkLine([], unit)).toBeUndefined();
  });
});
