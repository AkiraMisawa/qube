import type { PackageView } from './contracts';

// The packages of iterative addition as the trades' Package cells write them (ADR-0016 notes 90 and 95), read for the
// page's *Make package…* dialog: which packages a trade it lists is in now. What a package may be, the name the next
// is given, and which set add each makes of the cells are the server's; nothing here decides any of them.

/** The packages a trade is in, by its id as the workspace holds it, in the order the server lists them; none where it is in no package. */
export function packagesOf(packages: readonly PackageView[], trade: string): readonly string[] {
  return packages.filter((p) => p.adds.includes(trade) || p.takesOut.includes(trade)).map((p) => p.name);
}
