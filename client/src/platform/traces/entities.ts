import type { EntityCheck, LeafSetKind, Side, TermRead, TraceCatalog, TraceUnit } from './contracts';
import { formatNumber } from './formula';
import { SIDE_TITLE, afterWord } from './words';

// The entities below a term (ADR-0012 §6), in words: whether a term can be split by entity, how the rows are offered,
// and whether each side's entities add up to the term's allocation. Pure, and free of any domain: the leaf set's own
// column labels name the entity and the row.

/**
 * The leaf set whose rows carry a contribution, where the catalogue registers exactly one: the entities and the rows
 * below a term are read in its columns. None, or several, means no entity view, rather than columns that fit some rows.
 */
export function entitySetOf(catalog: TraceCatalog | undefined): LeafSetKind | undefined {
  const sets = catalog?.leafSets.filter((s) => s.contributionField !== undefined && s.contributionField !== null) ?? [];
  return sets.length === 1 ? sets[0] : undefined;
}

/**
 * Whether a term has a share to split among its entities: an allocation on some side it was priced on. A max over
 * operands has none, and summing below it would count what did not govern.
 */
export function splits(term: TermRead, sides: readonly Side[]): boolean {
  return sides.some((s) => term[s]?.allocation !== undefined && term[s]?.allocation !== null);
}

/** The label of a leaf set's column, else the column's name. */
export const columnLabel = (set: LeafSetKind, name: string): string => set.columns.find((c) => c.name === name)?.label ?? name;

/** How the rows below a term are shown: by entity, or row by row, and from which term's rows. */
export interface BelowView {
  readonly value: 'entities' | 'rows';
  readonly label: string;
  /** The rows of every term below, rather than the term's own (ADR-0012 §6's note of 2026-11-08). */
  readonly below: boolean;
  /** The leaf set whose columns the rows are read in. */
  readonly set: LeafSetKind;
}

/**
 * The views of the rows below a term, entity first. Where it splits, its entities and every row at any depth below it,
 * in the entity set's columns; otherwise its own rows, where it has some.
 */
export function belowViews(set: LeafSetKind | undefined, splitsBy: boolean, ownRows: LeafSetKind | undefined): readonly BelowView[] {
  const byRow = (rows: LeafSetKind) => `By ${rows.componentField === undefined || rows.componentField === null ? 'row' : afterWord(columnLabel(rows, rows.componentField))}`;
  if (set !== undefined && splitsBy) {
    return [
      { value: 'entities', label: `By ${afterWord(columnLabel(set, set.entityField))}`, below: true, set },
      { value: 'rows', label: byRow(set), below: true, set },
    ];
  }
  return ownRows === undefined ? [] : [{ value: 'rows', label: byRow(ownRows), below: false, set: ownRows }];
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
    const both = checks.length === 2;
    if (c.status === 'inconsistent' && c.allocation !== undefined && c.allocation !== null)
      return `${both ? `${SIDE_TITLE[c.side]}'s do` : 'They do'} not add up: ${formatNumber(c.sum, unit)} by its entities, ${formatNumber(c.allocation, unit)} allocated`;
    return `${both ? `${SIDE_TITLE[c.side]} not` : 'Not'} checked${c.reason ? `: ${c.reason}` : ''}`;
  });
  return { text: parts.join(' · '), error: checks.some((c) => c.status === 'inconsistent') };
}
