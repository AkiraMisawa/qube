import type { PackageView } from './contracts';

// The packages of iterative addition as the trades' Package cells write them (ADR-0016 note 90), read for the page's
// *Make package…* dialog: the name it offers, and where a trade it lists is now. What a package may be, and which set
// add each makes of the cells, is the server's; nothing here decides either.

/** The first of `P1`, `P2`, … that no package written is, whatever the case of its letters, as the server names one. */
export function nextPackage(packages: readonly PackageView[]): string {
  const taken = new Set(packages.map((p) => p.name.toLowerCase()));
  // One more name than there are packages is always free.
  const free = Array.from({ length: packages.length + 1 }, (_, i) => `P${i + 1}`).find((name) => !taken.has(name.toLowerCase()));
  if (free === undefined) throw new Error('No name of P1 to P(n+1) is free among n packages.');
  return free;
}

/** The package a trade is in, by its id as the workspace holds it; none where it is in no package. */
export function packageOf(packages: readonly PackageView[], trade: string): string | undefined {
  return packages.find((p) => p.adds.includes(trade) || p.takesOut.includes(trade))?.name;
}
