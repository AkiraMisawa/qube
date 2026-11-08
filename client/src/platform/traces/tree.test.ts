import { describe, expect, it } from 'vitest';
import type { TermPage, TermRead } from './contracts';
import { childrenOf, opened, treeRows, type ChildrenRead } from './tree';

// ADR-0011 §6: the tree holds only the terms on the paths opened, each indented by its depth. Synthetic terms.

const term = (id: string, childCount = 0): TermRead => ({ id, kind: 'k', label: id, symbol: id, childCount, written: true } as unknown as TermRead);
const page = (parent: TermRead, children: readonly TermRead[], childTotal = children.length, offset = 0): TermPage =>
  ({ term: parent, path: [], children, childTotal, offset });

const capital = term('capital', 1);
const rwa = term('rwa', 2);
const rw = term('rw', 1);
const ead = term('ead', 3);

describe('the tree of terms (ADR-0011 §6)', () => {
  it('shows a closed root alone, and an open one with its children below it, a level deeper', () => {
    const read = new Map<string, ChildrenRead>([['capital', { kind: 'pages', pages: [page(capital, [rwa])] }], ['rwa', { kind: 'pages', pages: [page(rwa, [rw, ead])] }]]);

    expect(treeRows([capital], new Set(), (id) => read.get(id)).map((r) => r.key)).toEqual(['capital']);
    const rows = treeRows([capital], new Set(['capital', 'rwa']), (id) => read.get(id));
    expect(rows.map((r) => [r.key, r.depth, r.open, r.opens])).toEqual([
      ['capital', 0, true, true], ['rwa', 1, true, true], ['rw', 2, false, true], ['ead', 2, false, true],
    ]);
  });

  it('says children are being read, or why they could not be, never that there are none', () => {
    const loading = treeRows([capital], new Set(['capital']), () => ({ kind: 'loading' }));
    expect(loading.map((r) => r.kind)).toEqual(['term', 'loading']);

    const failed = treeRows([capital], new Set(['capital']), () => ({ kind: 'failed', reason: 'TraceExpired: the Run is past retention.' }));
    expect(failed[1]).toMatchObject({ kind: 'failed', depth: 1, note: 'TraceExpired: the Run is past retention.' });
  });

  it('offers to read more where more children exist than were read, and joins the pages in order', () => {
    const many = term('ead', 3);
    const pages = [page(many, [term('NS-1'), term('NS-2')], 3), page(many, [term('NS-3')], 3, 2)];

    expect(childrenOf(pages).children.map((c) => c.id)).toEqual(['NS-1', 'NS-2', 'NS-3']);
    const first = treeRows([many], new Set(['ead']), () => ({ kind: 'pages', pages: pages.slice(0, 1) }));
    expect(first.at(-1)).toMatchObject({ kind: 'more', note: '1 more of 3' });
  });

  it('never opens a term without children, though it is marked open', () => {
    const leaf = term('rw/class');

    expect(treeRows([leaf], new Set(['rw/class']), () => undefined)).toMatchObject([{ key: 'rw/class', open: false, opens: false }]);
  });

  it('refuses a trace that names a term below itself', () => {
    const loop = term('a', 1);

    expect(() => treeRows([loop], new Set(['a']), () => ({ kind: 'pages', pages: [page(loop, [loop])] }))).toThrow(/names a below itself/);
  });

  it('opens every term above one on a path, so it shows', () => {
    expect([...opened(new Set(['x']), [{ id: 'capital' }, { id: 'rwa' }, { id: 'ead' }])]).toEqual(['x', 'capital', 'rwa']);
  });
});
