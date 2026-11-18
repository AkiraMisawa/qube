import { describe, expect, it } from 'vitest';
import type { TermPage, TermRead } from './contracts';
import { childrenOf, childrenShown, opened, treeMove, treeRows, type ChildrenRead } from './tree';

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

// Akira, 2026-11-18: exploring the drill-down with the arrow keys, one press after another. Alt+→ opens a closed term
// and goes into an open one; Alt+← closes an open term and goes up from a closed one (ADR-0011 §6's note of that day).
describe('the keys of the tree (ADR-0011 §6)', () => {
  const read = new Map<string, ChildrenRead>([
    ['capital', { kind: 'pages', pages: [page(capital, [rwa])] }],
    ['rwa', { kind: 'pages', pages: [page(rwa, [rw, ead])] }],
  ]);
  // capital (open) › rwa (open) › rw (closed), ead (closed)
  const rows = treeRows([capital], new Set(['capital', 'rwa']), (id) => read.get(id));
  const at = (id: string) => rows.findIndex((r) => r.key === id);

  it('Alt+→ opens a closed term, goes into an open one, and does nothing on a term without children', () => {
    expect(treeMove(rows, at('ead'), 'in')).toEqual({ kind: 'open', termId: 'ead' });
    expect(treeMove(rows, at('rwa'), 'in')).toEqual({ kind: 'into', termId: 'rwa' });
    const leaf = treeRows([term('rw/class')], new Set(), () => undefined);
    expect(treeMove(leaf, 0, 'in')).toEqual({ kind: 'none' });
  });

  it('Alt+← closes an open term, goes up from a closed one to its parent, and does nothing on a closed root', () => {
    expect(treeMove(rows, at('rwa'), 'out')).toEqual({ kind: 'close', termId: 'rwa' });
    // ead's parent is rwa, not rw, the row just above it a level deeper.
    expect(treeMove(rows, at('ead'), 'out')).toEqual({ kind: 'goTo', termId: 'rwa' });
    expect(treeMove(treeRows([capital], new Set(), () => undefined), 0, 'out')).toEqual({ kind: 'none' });
  });

  it('Alt+← on a row that stands for the children of a term goes up to that term; Alt+→ there does nothing', () => {
    const loading = treeRows([capital], new Set(['capital']), () => ({ kind: 'loading' }));
    const failed = treeRows([capital], new Set(['capital']), () => ({ kind: 'failed', reason: 'TraceExpired: the Run is past retention.' }));
    const more = treeRows([ead], new Set(['ead']), () => ({ kind: 'pages', pages: [page(ead, [term('NS-1')], 3)] }));

    for (const shown of [loading, failed, more]) {
      const last = shown.length - 1;
      expect(shown[last]?.kind).not.toBe('term');
      expect(treeMove(shown, last, 'out')).toEqual({ kind: 'goTo', termId: shown[0]!.termId });
      expect(treeMove(shown, last, 'in')).toEqual({ kind: 'none' });
    }
  });

  it('answers nothing for a position outside the rows, rather than acting on another row', () => {
    expect(treeMove(rows, rows.length, 'in')).toEqual({ kind: 'none' });
    expect(treeMove(rows, -1, 'out')).toEqual({ kind: 'none' });
  });

  it('goes into the first child once the children are shown, waits while they are read, and stays where there is none', () => {
    expect(childrenShown(rows, 'rwa')).toEqual({ kind: 'shown', first: 'rw' });
    expect(childrenShown(treeRows([capital], new Set(['capital']), () => ({ kind: 'loading' })), 'capital')).toEqual({ kind: 'reading' });
    const failed = treeRows([capital], new Set(['capital']), () => ({ kind: 'failed', reason: 'TraceExpired: the Run is past retention.' }));
    expect(childrenShown(failed, 'capital')).toEqual({ kind: 'shown', first: undefined });
    const none = treeRows([capital], new Set(['capital']), () => ({ kind: 'pages', pages: [page(capital, [], 0)] }));
    expect(childrenShown(none, 'capital')).toEqual({ kind: 'shown', first: undefined });
  });

  it('says a term is gone where it was closed, or is no row of the tree, so that nothing waits on it', () => {
    expect(childrenShown(rows, 'ead')).toEqual({ kind: 'gone' });
    expect(childrenShown(rows, 'NS-404')).toEqual({ kind: 'gone' });
  });
});
