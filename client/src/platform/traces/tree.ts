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
