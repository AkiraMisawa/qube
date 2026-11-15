import type { PackageView } from './contracts';

// The packages of iterative addition as the trades' Package cells write them (ADR-0016 note 90), read for the page's
// *Make package…* dialog: where a trade it lists is now. What a package may be, the name the next is given, and which
// set add each makes of the cells are the server's; nothing here decides any of them.

/** The package a trade is in, by its id as the workspace holds it; none where it is in no package. */
export function packageOf(packages: readonly PackageView[], trade: string): string | undefined {
  return packages.find((p) => p.adds.includes(trade) || p.takesOut.includes(trade))?.name;
}
