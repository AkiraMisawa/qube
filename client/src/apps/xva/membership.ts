import type { GridColumn } from '@ex-grid/core';
import type { Membership, RegisteredMeasure } from './contracts';

// A measure's membership (ADR-0010 §5, §10): whether it prices a trade, or counts a CSA, and why, as the server decided
// it. The page knows no measure's rule: it shows the registered measures that declare one, with the words their rule
// gave, on every grid whose rows carry them.

/** A row the server gave each declared measure's standing on. */
export interface Member { readonly membership?: Readonly<Record<string, Membership>> }

/** A row's membership of a measure: `In · CSA-B imperfect: one-way`, or `Out` with its reason. */
export function membershipOf(row: Member, measure: string): string | undefined {
  const m = row.membership?.[measure];
  return m === undefined ? undefined : `${m.in ? 'In' : 'Out'} · ${m.reason}`;
}

/**
 * A per-scope membership in words, scope by scope, for its cell's message (ADR-0012 §2): `IM-R JFSA: in. IMR-ABC-01
 * collects under JFSA. …`. A membership with no scopes has none: its reason is the whole of it.
 */
export function scopesOf(row: Member, measure: string): string | undefined {
  const scopes = row.membership?.[measure]?.scopes;
  return scopes == null || scopes.length === 0 ? undefined
    : scopes.map((s) => `${s.title}: ${s.in ? 'in' : 'out'}. ${s.reason}`).join(' ');
}

/** The membership column's field for a measure, and the measure a membership column's field names. */
export const membershipField = (measure: string) => `membership.${measure}`;
export const measureOfField = (field: string): string | undefined =>
  (field.startsWith('membership.') ? field.slice('membership.'.length) : undefined);

/** The read-only membership column of a measure that declares one, titled by the measure. */
export const membershipColumn = <Row extends Member>(measure: RegisteredMeasure): GridColumn<Row> => ({
  name: membershipField(measure.id), header: measure.title, type: 'text', width: 340,
  value: (row) => membershipOf(row, measure.id), format: (v) => String(v),
});

/** The selected measures that declare membership, in register order. */
export const declaring = (measures: readonly RegisteredMeasure[], selected: readonly string[]): readonly RegisteredMeasure[] =>
  measures.filter((m) => m.declaresMembership && selected.includes(m.id));
