/**
 * A flag as typed, wherever it is typed (ADR-0006's note 4 of 2026-11-11): the words the server's reader of a flag takes
 * (`FlagWords.Read`), in any case, so a cell, a product's term and `set` read it alike. `TRUE` and `FALSE`, Excel's
 * words, are among them. A word that is none of them is no flag: `undefined`, never a guess.
 */
export function readFlag(text: string): boolean | undefined {
  switch (text.trim().toLowerCase()) {
    case 'on': case 'yes': case 'true': case 'y': return true;
    case 'off': case 'no': case 'false': case 'n': return false;
    default: return undefined;
  }
}
