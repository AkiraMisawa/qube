import type { EntityCheck, EntityRow, LeafSetKind, Side, TermRead, TraceCatalog, TraceUnit } from './contracts';
import { formatNumber } from './formula';
import { SIDE_TITLE } from './words';

// The entities below a term (ADR-0012 §6), in words: whether a term can be split by entity, how the rows are offered,
// and whether each side's entities add up to the term's allocation. Pure, and free of any domain: the leaf set's own
// column labels name the entity and the row.

/** The leaf set whose rows carry a contribution, if the catalogue registers one; none means no entity view at all. */
export function entitySetOf(catalog: TraceCatalog | undefined): LeafSetKind | undefined {
  return catalog?.leafSets.find((s) => s.contributionField !== undefined && s.contributionField !== null);
}

/**
 * Whether a term has a share to split among its entities: an allocation on some side it was priced on. A max over
 * operands, such as a direction over its regimes, has none, and summing below it would count what did not govern.
 */
export function splits(term: TermRead, sides: readonly Side[]): boolean {
  return sides.some((s) => term[s]?.allocation !== undefined && term[s]?.allocation !== null);
}

/** The label of a leaf set's column, else its name. */
export const columnLabel = (set: LeafSetKind, name: string | null | undefined): string | undefined =>
  name === undefined || name === null ? undefined : set.columns.find((c) => c.name === name)?.label ?? name;

/** The views of the rows below a term: by entity where it splits, by row where it has rows of its own. Entity first. */
export function belowViews(set: LeafSetKind | undefined, splitsBy: boolean, ownRows: LeafSetKind | undefined):
  readonly { readonly value: 'entities' | 'rows'; readonly label: string }[] {
  return [
    ...(set !== undefined && splitsBy ? [{ value: 'entities' as const, label: `By ${columnLabel(set, set.entityField)}` }] : []),
    ...(ownRows !== undefined ? [{ value: 'rows' as const, label: `By ${columnLabel(ownRows, ownRows.componentField) ?? 'row'}` }] : []),
  ];
}

/** Which sides hold an entity, where both were priced: a new trade is After only. */
export function entitySides(row: EntityRow, sides: readonly Side[]): string {
  if (sides.length === 1) return '';
  const on = sides.filter((s) => row[s] !== undefined && row[s] !== null);
  return on.length === 2 ? 'Before and After' : `${SIDE_TITLE[on[0]!]} only`;
}

/**
 * Each side's reconciliation of its entities against the term's allocation, said once where every side adds up. A side
 * that does not add up says both figures; the view shows it red.
 */
export function checkLine(checks: readonly EntityCheck[], unit: TraceUnit | null | undefined): { readonly text: string; readonly error: boolean } | undefined {
  if (checks.length === 0) return undefined;
  if (checks.every((c) => c.status === 'matches')) {
    const figures = checks.map((c) => `${checks.length === 2 ? `${SIDE_TITLE[c.side]} ` : ''}${formatNumber(c.sum, unit)}`).join(' · ');
    return { text: `They add up to its allocation: ${figures}`, error: false };
  }
  const parts = checks.filter((c) => c.status !== 'matches').map((c) => {
    const lead = checks.length === 2 ? `${SIDE_TITLE[c.side]} ` : '';
    if (c.status === 'inconsistent' && c.allocation !== undefined && c.allocation !== null)
      return `${lead}does not add up: ${formatNumber(c.sum, unit)} by its entities, ${formatNumber(c.allocation, unit)} allocated`;
    return `${lead}not checked${c.reason ? `: ${c.reason}` : ''}`;
  });
  return { text: parts.join(' · '), error: checks.some((c) => c.status === 'inconsistent') };
}
