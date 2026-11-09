import type { CdsCatalogue, EffectiveCredit } from './contracts';
import type { Parsed } from './editing';
import { differs } from './original';

// The Credit tab's readings (ADR-0010 §8's note of 2026-11-09): spreads are fractions on the wire (0.0052) and bp on the
// screen (52); what a typed spread means is the server's to read, so the tab sends it as written, with the grid's unit
// where none was typed.

/** The CDS catalogue as the page has read it: not yet, the catalogue, or why it could not be read. */
export type CatalogueRead = CdsCatalogue | { readonly refused: string } | undefined;

/** The catalogue, once it has been read. */
export const catalogueOf = (read: CatalogueRead): CdsCatalogue | undefined => (read === undefined || 'refused' in read ? undefined : read);

/** Why the catalogue could not be read, where it could not. */
export const catalogueRefused = (read: CatalogueRead): string | undefined => (read !== undefined && 'refused' in read ? read.refused : undefined);

const bp = new Intl.NumberFormat('en-US', { maximumFractionDigits: 4 });

/** A spread in bp, the unit the curve's grid shows: 0.0052 is 52. */
export const inBp = (spread: number): number => spread * 10_000;

/** A figure already in bp as the curve's grid shows it: `52`, `52.5`. */
export const bpFigure = (inBp: number): string => bp.format(inBp);

/** A spread as the tab writes it: `52bp`. */
export const bpText = (spread: number): string => `${bpFigure(inBp(spread))}bp`;

/**
 * A spread as typed in the curve's grid, as the server reads a spread: a bare number is bp, the grid's unit (`60` is
 * `60bp`), and one typed with its unit (`60bp`, `0.6%`) is sent as written. An empty one is refused: a spread is never
 * empty, and the catalogue's is taken back by removing the change on the Changes tab.
 */
export function spreadTyped(text: string): Parsed {
  const t = text.trim();
  if (t === '') return { ok: false, message: 'A spread is never empty: remove its change on the Changes tab to take the catalogue\'s back.' };
  if (/^[+-]?\d+(?:\.\d+)?$/.test(t)) return { ok: true, value: `${t}bp` };
  if (/^[+-]?\d+(?:\.\d+)?\s*(?:bp|%)$/i.test(t)) return { ok: true, value: t };
  return { ok: false, message: `${t} is not a spread: type it in bp, such as 52, or with its unit, such as 0.52%.` };
}

/** How many of the Netting tab's values differ from Original: the CVA recovery. */
export const nettingChanges = (credit: EffectiveCredit): number => (differs(credit, 'cvaRecovery') ? 1 : 0);

/** How many of the Credit tab's values differ from Original: the CDS chosen and its keys, and the curve's spreads. */
export function creditChanges(credit: EffectiveCredit): number {
  const choice = credit.original == null ? 0 : Object.keys(credit.original).filter((f) => f !== 'cvaRecovery').length;
  return choice + (credit.cds?.original == null ? 0 : Object.keys(credit.cds.original).length);
}

/** What the CVA recovery is when it is empty, as the Netting tab's placeholder says it: the chosen curve's, or none. */
export const emptyRecovery = (credit: EffectiveCredit): string =>
  credit.cdsRecovery == null ? 'empty: no CDS is chosen' : `empty: CDS ${credit.cdsRecovery}`;
