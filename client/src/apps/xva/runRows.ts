import type { RegisteredMeasure } from './contracts';

// A result tab's reading of the rows a Run retained (ADR-0010 §7): each row is the request's, read field by field. A field
// the request always writes is refused by name when it is missing, never filled in with a guess (spine 1).

export type RunRow = Readonly<Record<string, unknown>>;

/** A text field, or none where the row has no text there. */
export const textOf = (v: unknown): string | undefined => (typeof v === 'string' && v !== '' ? v : undefined);

/** A field every row of `what` carries, as text; a row without it cannot be read. */
export function requiredText(row: RunRow, field: string, what: string): string {
  const v = textOf(row[field]);
  if (v === undefined) throw new Error(`A row of the ${what} has no ${field}, so the Run's ${what} cannot be read.`);
  return v;
}

/** The rows of the case and the market in view. Every row of a result tab names its market (ADR-0010 §4). */
export const rowsInView = (rows: readonly RunRow[], caseId: string, scenario: string, what: string): readonly RunRow[] =>
  rows.filter((r) => r.case === caseId && requiredText(r, 'scenario', what) === scenario);

/** A measure of the register by its id; an id the register does not hold is a defect, said by name. */
export function registered(register: readonly RegisteredMeasure[], id: string): RegisteredMeasure {
  const measure = register.find((m) => m.id === id);
  if (measure === undefined) throw new Error(`${id} is no registered measure.`);
  return measure;
}
