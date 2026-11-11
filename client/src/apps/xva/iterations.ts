import type { CaseView, Iteration } from './contracts';

// `add each` takes its iterations as one list, which is the whole set unless it is told to keep the cases made
// (ADR-0016 note 37 of 2026-11-11): the cases made before are replaced by it. A page that leaves one trade out of one
// case therefore says the made iterations again with that one change. This reads them of the cases the page shows;
// what the page no longer shows is said, and no set is made of a guess. One more case needs none of this: the page
// asks for it beside the cases made.

type Made = Pick<CaseView, 'id' | 'title' | 'iteration'>;

/**
 * The set with `trade` left out of what the case `caseId` adds, or of what it takes out of After. The one trade a case
 * adds is not left out: a case that adds nothing is no case, and the case is removed whole instead.
 */
export function tradeLeftOut(cases: readonly Made[], caseId: string, list: keyof Iteration, trade: string): Iteration[] {
  const found = cases.find((c) => c.id === caseId);
  if (found === undefined) throw new Error('That case is no longer in the workspace: look at the Changes tab again.');
  const { title, iteration } = found;
  if (iteration === undefined) throw new Error(`Case ${title} is no case of add each, so it lists no trade.`);
  if (!iteration[list].includes(trade)) throw new Error(`Case ${title} no longer ${list === 'add' ? 'adds' : 'takes'} ${trade}${list === 'add' ? '' : ' out'}: look at the Changes tab again.`);
  if (list === 'add' && iteration.add.length === 1) throw new Error(`${trade} is the one trade ${title} adds: remove the case instead.`);
  return cases.flatMap((c) => (c.iteration === undefined ? [] : [c.id === caseId ? { ...c.iteration, [list]: c.iteration[list].filter((t) => t !== trade) } : c.iteration]));
}
