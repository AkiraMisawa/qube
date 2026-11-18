import type { TermPage, TermRead } from './contracts';

// The tree of terms as rows (ADR-0011 §6): the page holds which terms are open and the pages of children it has
// read, and pushes the rows to the grid, as ExPivot's Compact form does. A term's children are its rows only once
// they are read; until then its row says they are coming, never that it has none.

/** A row of the tree: a term at its depth, whether it can open, and whether it is open. */
export interface TreeRow {
  readonly key: string;
  readonly kind: 'term' | 'loading' | 'more' | 'failed';
  readonly depth: number;
  /** The term the row draws; for a loading, more or failed row, the term whose children it stands for. */
  readonly termId: string;
  readonly term: TermRead | undefined;
  readonly open: boolean;
  /** Whether the term has children to open. */
  readonly opens: boolean;
  /** What a loading, more or failed row says. */
  readonly note?: string;
}

/** The children read for an open term: the pages so far, or why they could not be read. */
export type ChildrenRead =
  | { readonly kind: 'pages'; readonly pages: readonly TermPage[] }
  | { readonly kind: 'loading' }
  | { readonly kind: 'failed'; readonly reason: string };

/** The children of a term over every page read, in the server's order, each once. */
export function childrenOf(pages: readonly TermPage[]): { readonly children: readonly TermRead[]; readonly total: number } {
  const seen = new Set<string>();
  const children: TermRead[] = [];
  for (const page of pages) for (const child of page.children) if (!seen.has(child.id)) { seen.add(child.id); children.push(child); }
  return { children, total: pages.at(-1)?.childTotal ?? 0 };
}

/**
 * Flattens the roots and every open term's children into rows, depth first in the order given. A term open whose
 * children are still being read shows a loading row; one with more children than read shows a row to read more.
 */
export function treeRows(roots: readonly TermRead[], open: ReadonlySet<string>, children: (id: string) => ChildrenRead | undefined): readonly TreeRow[] {
  const rows: TreeRow[] = [];
  const visit = (term: TermRead, depth: number, seen: ReadonlySet<string>) => {
    const opens = term.childCount > 0;
    const isOpen = opens && open.has(term.id);
    rows.push({ key: term.id, kind: 'term', depth, termId: term.id, term, open: isOpen, opens });
    if (!isOpen) return;
    // A trace is a tree; a term met again below itself is refused rather than drawn for ever.
    if (seen.has(term.id)) throw new Error(`The trace names ${term.id} below itself.`);
    const read = children(term.id);
    if (read === undefined || read.kind === 'loading') {
      rows.push({ key: `${term.id}\u0000loading`, kind: 'loading', depth: depth + 1, termId: term.id, term: undefined, open: false, opens: false, note: 'Reading its terms…' });
      return;
    }
    if (read.kind === 'failed') {
      rows.push({ key: `${term.id}\u0000failed`, kind: 'failed', depth: depth + 1, termId: term.id, term: undefined, open: false, opens: false, note: read.reason });
      return;
    }
    const next = new Set(seen).add(term.id);
    const { children: list, total } = childrenOf(read.pages);
    for (const child of list) visit(child, depth + 1, next);
    if (list.length < total) {
      rows.push({ key: `${term.id}\u0000more`, kind: 'more', depth: depth + 1, termId: term.id, term: undefined, open: false, opens: false,
        note: `${total - list.length} more of ${total}` });
    }
  };
  for (const root of roots) visit(root, 0, new Set());
  return rows;
}

/** The terms to open so that a term on this path shows: every term above it. */
export function opened(open: ReadonlySet<string>, path: readonly { readonly id: string }[]): ReadonlySet<string> {
  const next = new Set(open);
  for (const crumb of path.slice(0, -1)) next.add(crumb.id);
  return next;
}

/**
 * What the tree's two keys do at the row the Focus is on (ADR-0011 §6, its notes of 2026-11-08 and 2026-11-18). `in`
 * (Alt+→) opens a closed term and goes into an open one, to its first child, so that pressed again and again it walks
 * down; `out` (Alt+←) closes an open term and goes up from a closed one to its parent, and from a row that stands for a
 * term's children (being read, more to read, or failed) to that term. A term with nothing to open, a closed root, and a
 * position the rows do not have are answered with nothing, never with another row.
 */
export type TreeMove =
  | { readonly kind: 'open' | 'close' | 'into' | 'goTo'; readonly termId: string }
  | { readonly kind: 'none' };

const NONE: TreeMove = { kind: 'none' };

export function treeMove(rows: readonly TreeRow[], at: number, key: 'in' | 'out'): TreeMove {
  const row = rows[at];
  if (row === undefined) return NONE;
  if (row.kind !== 'term') return key === 'out' ? { kind: 'goTo', termId: row.termId } : NONE;
  if (key === 'in') {
    if (!row.opens) return NONE;
    return { kind: row.open ? 'into' : 'open', termId: row.termId };
  }
  if (row.open) return { kind: 'close', termId: row.termId };
  // Rows are depth first, so a term's parent is the nearest term above it a level up.
  for (let i = at - 1; i >= 0; i--) {
    const above = rows[i]!;
    if (above.kind === 'term' && above.depth === row.depth - 1) return { kind: 'goTo', termId: above.termId };
  }
  return NONE;
}

/**
 * Where an open term's children stand in the rows: still being read, or shown, with the first of them where it has one;
 * or `gone`, where the term is no longer an open row of the tree (closed, or under a term closed since). Going into a
 * term waits while they are read, so a key pressed before they arrive ends where one pressed after does (spine 6), and
 * goes nowhere where they could not be read, whose row says why, or where it has none.
 */
export type ChildrenShown =
  | { readonly kind: 'reading' }
  | { readonly kind: 'shown'; readonly first: string | undefined }
  | { readonly kind: 'gone' };

export function childrenShown(rows: readonly TreeRow[], termId: string): ChildrenShown {
  const at = rows.findIndex((r) => r.kind === 'term' && r.termId === termId);
  const row = rows[at];
  if (row === undefined || !row.open) return { kind: 'gone' };
  const next = rows[at + 1];
  if (next === undefined || next.depth !== row.depth + 1) return { kind: 'shown', first: undefined };
  if (next.kind === 'loading') return { kind: 'reading' };
  return { kind: 'shown', first: next.kind === 'term' ? next.termId : undefined };
}
