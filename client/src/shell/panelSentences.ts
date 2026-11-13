import type { SerializedDockview } from 'dockview-react';
import { panelLabel, type WorkspaceState } from '../platform/workspace/workspaceStore';
import { areasOf, type Returned } from './layout';

// How the shell's notices name panels, and where the panels of other windows went (ADR-0018 §9): a reload and the
// line's `arrange back` say it in the same words.

/** These panels' titles as a sentence says them: "A", "A and B", "A, B and C". */
export function titlesOf(state: Pick<WorkspaceState, 'panels'>, ids: readonly string[]): string {
  const titles = ids.map((id) => {
    const panel = state.panels.find((p) => p.id === id);
    if (panel === undefined) throw new Error(`No panel ${id} is open.`);
    return panelLabel(panel);
  });
  const last = titles.at(-1);
  return titles.length < 2 || last === undefined ? titles.join('') : `${titles.slice(0, -1).join(', ')} and ${last}`;
}

/** The words a sentence says panels by, each keyed by its plural: one panel is "it", more are "they". */
const ONE = { they: 'it', their: 'its', are: 'is', were: 'was' } as const;
const MANY = { they: 'they', their: 'their', are: 'are', were: 'were' } as const;
export const pronouns = (count: number) => (count === 1 ? ONE : MANY);

/**
 * Says where the panels of other windows went as an arrangement was drawn with them in the main window
 * (`withWindowsDocked`): where they were, in the area of another panel, or in an area of their own, as `drawn` has them,
 * and `why` they are not in their windows where there is a reason to give. Undefined where none of them is open.
 */
export function backInThisWindow(returned: readonly Returned[], state: Pick<WorkspaceState, 'panels'>, drawn: SerializedDockview,
  why?: string): string | undefined {
  const open = (ids: readonly string[]) => ids.filter((id) => state.panels.some((p) => p.id === id));
  const areas = areasOf(drawn);
  const parts = returned.filter((r) => open(r.panels).length > 0).map((r) => {
    const titles = titlesOf(state, open(r.panels));
    const pronoun = pronouns(open(r.panels).length);
    if (r.cameFrom) return `${titles} where ${pronoun.they} ${pronoun.were}`;
    const beside = areas.find((a) => a.id === r.into)?.panels.find((id) => !r.panels.includes(id));
    return beside === undefined ? `${titles} in an area of ${pronoun.their} own` : `${titles} in the area of ${titlesOf(state, [beside])}`;
  });
  if (parts.length === 0) return undefined;
  return `Back in this window${why === undefined ? '' : `, ${why}`}: ${parts.join('; ')}. pop out panel sends a panel out again.`;
}
