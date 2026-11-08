import type { Side, TermPage, TermRead, TraceHeader } from './contracts';
import { diffText, valueText } from './formula';
import { SIDE_TITLE, unitText } from './words';

// A trace tab's cards (ADR-0010 §7, ADR-0012 §7): one per root of the trace, above its drill-down. A card reads only
// its root's own page, so it says what the provider wrote and nothing it did not.

export interface RootCard {
  readonly id: string;
  readonly label: string;
  /** What the root's key names beyond its label, such as the agreement it is computed under. */
  readonly detail: string | undefined;
  /** The operand a max, min or bound took on each side, named by its term, such as `Well A governs`. */
  readonly governing: string | undefined;
  readonly figures: readonly { readonly key: Side | 'diff'; readonly title: string; readonly text: string }[];
}

/**
 * What governed the root on each side: the children the server marks as governing there (ADR-0011 §2, §6), said once
 * where the sides agree. None where no child on the page says so, rather than a child guessed from its position.
 */
function governingOf(page: TermPage, header: TraceHeader): string | undefined {
  const marks = header.sides.flatMap((side) => {
    const governing = page.children.filter((c) => c[side]?.governs === true).map((c) => c.label);
    return governing.length === 0 ? [] : [[side, governing.join(' and ')] as const];
  });
  const [first] = marks;
  if (first === undefined) return undefined;
  if (marks.length === header.sides.length && marks.every(([, label]) => label === first[1])) return `${first[1]} governs`;
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

/** The root a term lies under, by its path (`north/well-a` is under `north`), or none. */
export function rootOf(roots: readonly string[], path: string | undefined): string | undefined {
  if (path === undefined) return undefined;
  return roots.find((r) => path === r || path.startsWith(`${r}/`));
}
