import type { Side, TermPage, TermRead, TraceHeader } from './contracts';
import { diffText, valueText } from './formula';
import { SIDE_TITLE, unitText } from './words';

// A trace tab's cards (ADR-0012 §7: "the two directions' summaries"; ADR-0010 §7): one per root of the trace, above
// its drill-down. A card reads only its root's own page, so it says what the provider wrote and nothing it did not.

export interface RootCard {
  readonly id: string;
  readonly label: string;
  /** What the root's key names beyond its label, such as the IM CSA a direction is computed under. */
  readonly detail: string | undefined;
  /** The operand a max, min or bound took on each side, named by its term, such as `JFSA regime governs`. */
  readonly governing: string | undefined;
  readonly figures: readonly { readonly key: Side | 'diff'; readonly title: string; readonly text: string }[];
}

/** The term a side's governed mark names: a position among the root's children, or a role of its formula. */
function governedLabel(page: TermPage, governed: string | number): string {
  if (typeof governed === 'number') return page.children[governed]?.label ?? `operand ${governed + 1}`;
  const id = page.term.slots?.[governed];
  return page.children.find((c) => c.id === id)?.label ?? governed;
}

function governingOf(page: TermPage, header: TraceHeader): string | undefined {
  const marks = header.sides.flatMap((side) => {
    const governed = page.term[side]?.governed;
    return governed === undefined || governed === null ? [] : [[side, governedLabel(page, governed)] as const];
  });
  if (marks.length === 0) return undefined;
  if (marks.every(([, label]) => label === marks[0]![1])) return `${marks[0]![1]} governs`;
  return marks.map(([side, label]) => `${SIDE_TITLE[side]}: ${label} governs`).join(' · ');
}

const detailOf = (term: TermRead): string | undefined => {
  const extra = Object.values(term.key ?? {}).filter((v) => !term.label.includes(v));
  return extra.length === 0 ? undefined : extra.join(' · ');
};

/** A root's card: its label, what its key adds, what governed it, and its figure on each side priced and its Diff. */
export function rootCard(page: TermPage, header: TraceHeader): RootCard {
  const term = page.term;
  const unit = unitText(term.unit, header);
  const titled = (title: string) => (unit === '' ? title : `${title} (${unit})`);
  const figures = header.sides.flatMap((side) => {
    const value = term[side];
    return value === undefined || value === null ? [] : [{ key: side, title: titled(SIDE_TITLE[side]), text: valueText(value, term.unit) }];
  });
  const diff = header.sides.length === 2 && term.diff !== undefined && term.diff !== null
    ? [{ key: 'diff' as const, title: titled('Diff'), text: diffText(term.diff, term.unit) }] : [];
  return { id: term.id, label: term.label, detail: detailOf(term), governing: governingOf(page, header), figures: [...figures, ...diff] };
}

/** The root a term lies under, by its path (`collect/JFSA` is under `collect`), or none. */
export function rootOf(roots: readonly string[], path: string | undefined): string | undefined {
  if (path === undefined) return undefined;
  return roots.find((r) => path === r || path.startsWith(`${r}/`));
}
