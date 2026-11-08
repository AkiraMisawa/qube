// What differs from Original (ADR-0010 §2): the server says which values differ and what Original held; the page marks
// those cells and describes them in words, since colour is never the only signal (docs/design-guidelines.md §7).

/** A row as the server sends it: the fields whose value differs from Original, with Original's value. */
export interface Marked { readonly original?: Readonly<Record<string, unknown>> | null }

export function differs(row: Marked, field: string): boolean {
  return row.original != null && Object.hasOwn(row.original, field);
}

const figure = new Intl.NumberFormat('en-US', { maximumFractionDigits: 6 });
const shown = (v: unknown) => (v === null || v === undefined ? 'empty' : typeof v === 'number' ? figure.format(v) : String(v));

/** The cell's description: `Original jpy_ois · now usd_sofr`; undefined where the value is Original's. */
export function originalMessage(row: Marked, field: string, now: unknown): string | undefined {
  return differs(row, field) ? `Original ${shown(row.original![field])} · now ${shown(now)}` : undefined;
}

/** How many values differ from Original across rows, for a tab's label (`CSA ●2`). */
export function changes(rows: readonly Marked[]): number {
  return rows.reduce((n, r) => n + (r.original == null ? 0 : Object.keys(r.original).length), 0);
}
